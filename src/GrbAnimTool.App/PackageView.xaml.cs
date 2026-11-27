using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.App;

/// <summary>The Package screen: what the package holds (clip, index, edit or import, who plays it, the checks' verdict and warnings) with
/// Remove, its name, the save folder, the exact files a build writes, and Build, which writes them all or none and shows what was written.
/// Build waits for the clip index while it is read, so who plays each clip is on the screen before anything is written; it never refuses
/// to build because of who plays a clip. A successful Build empties the list (and so the list kept between runs), the screen showing what
/// it built until a clip is kept again; one that fails or is refused leaves the list as it was.</summary>
public partial class PackageView : UserControl
{
    /// <summary>One item as the list shows it, with who plays the clip (<see cref="ClipUsers.Text"/>), shown under its name as
    /// <see cref="PlayedBy"/>.</summary>
    public sealed record PackageRow(PackageItem Item, string Clip, int Index, string What, string Verdict, CheckLevel Level, string Warnings, string Users, int BigDifferences = 0)
    {
        public string PlayedBy => "Played by: " + Users;

        /// <summary>The look checks' mark: how many big differences from the game's own clip, or nothing.</summary>
        public string Look => BigDifferences == 0 ? "" : $"{BigDifferences} big difference{(BigDifferences == 1 ? "" : "s")}";
    }

    GameInstall? _install;
    ClipIndex? _index;
    /// <summary>Why the install's clip index couldn't be built, when it couldn't; null while it may still come.</summary>
    string? _indexError;
    /// <summary>How far the clip index's read is (0 to 1), shown while Build waits for it.</summary>
    double _indexProgress;
    /// <summary>The index and its error held back for a picture of the screen waiting for it (<see cref="ShowWaitingForShot"/>).</summary>
    (ClipIndex? Index, string? Error)? _heldForShot;
    bool _busy;

    public PackageView()
    {
        InitializeComponent();
        // the package's own columns after the four every list has: who plays each clip (a replaced clip changes for all of them; what
        // "probably" stands for on hover), what was changed in it (a trim's exact ticks on hover), its checks' verdict and its warnings
        // (whole on hover)
        // the checks' verdict first, a coloured badge in sight beside the four every list has, the warnings on hover
        // the look checks' mark on it, a *: big differences from the game's own clip, which never stop a build
        ItemsPanel.AddBadgeColumn("Checks", 58, "The game checks' verdict on the clip: PASS, WARN or FAIL, with * when the look checks found a big difference from the game's own clip; hover a badge for the warnings",
            (c, d) => RowOf(c, d) is { } r ? r.Verdict + (r.BigDifferences > 0 ? "*" : "") : "",
            (c, d) => RowOf(c, d) is { } r ? $"{r.Verdict}{(r.Look == "" ? "" : $" (look: {r.Look})")}. Warnings: {r.Warnings}" : "");
        // who plays each clip next (shown before the clip is replaced, as on every screen; a long one ends in an ellipsis, whole on hover),
        // then what was changed, as wide as its values up to a limit (FitChangeColumn: a swap from a clip name of up to about 25
        // characters reads whole; a longer value ends in an ellipsis, whole on hover), then the warnings, whole on hover. At the window's
        // usual size the four common columns, the checks, who plays each clip and the change are in sight; the warnings and the columns
        // every list has after them are a sideways scroll away
        ItemsPanel.AddColumn("Played by", PlayedByWidth, ClipUsers.PlayedByTip, (c, d) => RowOf(c, d)?.Users ?? "", (c, d) => RowOf(c, d) is { } r ? $"{r.Users}\n\n{ClipUsers.PlayedByTip}" : "");
        _changeColumn = ItemsPanel.AddColumn("Change", ChangeLeast, "What was changed in the clip: the edit made on it, or the .glb imported into it", (c, d) => RowOf(c, d)?.What ?? "",
            (c, d) => RowOf(c, d)?.Item.Change.Detail ?? "");
        ItemsPanel.AddColumn("Warnings", 320, "The checks' warnings and the edit's: hover a row for the whole text", (c, d) => RowOf(c, d)?.Warnings.Replace('\n', ' ') ?? "",
            (c, d) => RowOf(c, d)?.Warnings ?? "");
        // the package's clips are all changed ones: the Modded only filter has nothing to do here
        ItemsPanel.ModdedRow.Visibility = Visibility.Collapsed;
        ItemsPanel.Picked += _ => RemoveButton.IsEnabled = CanRemove;
        // the checks follow the selection, also when a search or a filter takes the selected row out (the list raises no Picked then)
        // and so does Remove: a row the search hides is no row to remove
        ItemsPanel.List.SelectionChanged += (_, _) => { ShowChecks(); RemoveButton.IsEnabled = CanRemove; };
        // the list's view getting wider or narrower (the window resized, the upright scroll bar shown or hidden): the Change column fitted again
        ItemsPanel.List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.ViewportWidthChange != 0 && e.OriginalSource == FindViewer(ItemsPanel.List)) RefitChangeColumn();
        }));
        // the list with a few rows and Remove, and the name, the folders, a few files and Build, in a window of any height
        PaneScroll.LeastHeight = () => Math.Max(ItemsTitle.DesiredSize.Height + 4 + ItemsPanel.LeastHeight + RemoveRow.DesiredSize.Height + RemoveRow.Margin.Top
            + (ChecksPanel.Visibility == Visibility.Visible ? ChecksPanel.DesiredSize.Height + ChecksPanel.Margin.Top : 0),
            SettingsGrid.DesiredSize.Height + FilesTitle.DesiredSize.Height + 12 + 120 + BuildRow.DesiredSize.Height + BuildRow.Margin.Top);
        _typed.Tick += (_, _) => _ = RefreshOffThreadAsync();
        FolderBox.Text = DefaultSaveFolder;
        PackageList.Current.Changed += (_, _) => Dispatcher.BeginInvoke(() => _ = RefreshOffThreadAsync());
        Refresh();
    }

    /// <summary>The selected row's checks, every one, as the Edit screen lists them (<see cref="PackageEntries.Checks"/>); hidden with no row
    /// selected.</summary>
    void ShowChecks()
    {
        var row = ItemsPanel.SelectedClip is { } c ? RowOf(c, ItemsPanel.ColumnData) : null;
        ChecksPanel.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        CheckList.ItemsSource = row is null ? null
            : PackageEntries.Checks(row.Item).Select(x => new EditView.ReportRow(x.Said, x.Shown, x.Check, x.Message)).ToList();
        ChecksTitle.Text = row is null ? "" : $"Checks of {row.Clip}: {row.Verdict}" + (row.Look == "" ? "" : $", look: {row.Look}");
    }

    /// <summary>The save folder with no setting and no build yet: a folder of the tool's own in the Documents folder.</summary>
    internal static string DefaultSaveFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GRB Animation Tool packages");

    /// <summary>Whether Remove can take the clip selected out: not busy, and the clip is in the list (not one of a build shown after it).</summary>
    bool CanRemove => !_busy && ItemsPanel.SelectedClip is { } c && PackageList.Current.Items.Any(i => i.Slot.Id == c.Id);

    /// <summary>What the last successful Build wrote, and its folder: shown in the list while the list is empty after it, until a clip is
    /// kept (a new list) or another install is opened. Held in memory only, never kept between runs.</summary>
    internal (IReadOnlyList<PackageItem> Items, string Folder)? LastBuilt { get; private set; }

    /// <summary>Whether the list shows what the last Build wrote rather than the list a Build would build (which is empty then).</summary>
    internal bool ShowsBuilt => LastBuilt is not null && PackageList.Current.Count == 0;

    /// <summary>The package's row for a kept clip, from the rows the list's own columns read.</summary>
    static PackageRow? RowOf(ClipEntry c, object? data) => (data as IReadOnlyDictionary<ulong, PackageRow>)?.GetValueOrDefault(c.Id);

    /// <summary>The rows as the list shows them now, in the order kept.</summary>
    internal IReadOnlyList<PackageRow> ShownRows { get; private set; } = [];

    /// <summary>Takes the install the Library screen opened; who plays each clip comes with its clip index (<see cref="SetIndex"/>).</summary>
    public void SetInstall(GameInstall g)
    {
        _install = g;
        _index = null;
        _indexError = null;
        _indexProgress = 0;
        _heldForShot = null;
        _builtFolder = null;
        LastBuilt = null;
        ResultText.Text = "";
        ItemsPanel.SetClips([]);
        Refresh();
    }

    /// <summary>Says what came back of the list kept between runs (<paramref name="file"/>: where it is kept, null when this run keeps
    /// none): how many clips; plainly, the clips kept for these game files that don't fit them now and the lists kept for other game
    /// files (all of them still kept); or that the kept file couldn't be read.</summary>
    public void ShowRestored(PackageRestore r, string? file)
    {
        _restored = (r, file);
        RestoreText.Text = r.Restored.Count == 0 ? ""
            : $"Back from the last run: {r.Restored.Count} kept clip{(r.Restored.Count == 1 ? "" : "s")}, kept between runs in {file}.";
        RestoreText.Visibility = RestoreText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var said = new List<string>();
        if (r.Problem is { } p) said.Add(p);
        if (r.NotFitting.Count > 0)
            said.Add($"Not shown, as they don't fit the game files open now ({r.NotFitting.Count}); they stay kept until you forget them:\n  " + string.Join("\n  ", r.NotFitting));
        if (r.Elsewhere.Count > 0)
            said.Add("Kept for other game files, and shown when those are open: " + string.Join("; ", r.Elsewhere) + ".");
        DroppedText.Text = string.Join("\n", said);
        DroppedText.Visibility = DroppedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ForgetButton.Visibility = r.NotFitting.Count > 0 && ForgetRequested is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The restore last shown, and where the list is kept.</summary>
    (PackageRestore Restore, string? File)? _restored;

    /// <summary>Forgets the kept clips that don't fit the game files open (the window's keeper does it); returns an error message, or null.</summary>
    public Func<string?>? ForgetRequested { get; set; }

    void OnForget(object sender, RoutedEventArgs e)
    {
        if (ForgetRequested?.Invoke() is { } error) { ShowError(error); return; }
        if (_restored is { } r) ShowRestored(r.Restore with { NotFitting = [] }, r.File);
    }

    /// <summary>Takes the clip index the Library built for the install: who plays each clip, in the list and the notice; the list's readable
    /// name, who and what columns, its search and its filters take it too.</summary>
    public void SetIndex(GameInstall g, ClipIndex index)
    {
        if (!ReferenceEquals(g, _install)) return;
        _index = index;
        ItemsPanel.SetIndex(index);
        _ = RefreshOffThreadAsync();
    }

    /// <summary>The clip index couldn't be built for the install: the list and the notice say who plays the clips isn't known, and why;
    /// Build goes on (it never waits for an index that won't come).</summary>
    public void SetIndexError(GameInstall g, string reason)
    {
        if (!ReferenceEquals(g, _install)) return;
        _indexError = reason;
        _ = RefreshOffThreadAsync();
    }

    /// <summary>How far the install's clip index is read (0 to 1): shown beside Build while it waits for the index.</summary>
    public void SetIndexProgress(GameInstall g, double progress)
    {
        if (!ReferenceEquals(g, _install) || _index is not null || _heldForShot is not null) return;
        _indexProgress = Math.Max(_indexProgress, progress);
        if (WaitPanel.Visibility == Visibility.Visible) ShowWaitProgress();
    }

    /// <summary>Whether Build waits for the clip index: an install is open, its index isn't here yet and hasn't failed.</summary>
    bool IndexPending => _install is not null && _index is null && _indexError is null;

    /// <summary>Called on the window's thread just before Build writes, with who plays each clip on the screen: the tests' view of the
    /// screen as it stands then.</summary>
    internal Action? WriteStarting;

    /// <summary>The list's rows: each clip kept with who plays it (<see cref="ClipUsers.Text"/>, by <paramref name="index"/>; why that
    /// isn't known without one).</summary>
    internal static List<PackageRow> Rows(IReadOnlyList<PackageItem> items, ClipIndex? index, string? indexError) => items.Select(i =>
    {
        var w = PackageEntries.Warnings(i);
        var v = PackageEntries.GameVerdict(i);
        return new PackageRow(i, i.Slot.Name, i.Slot.Index, PackageEntries.What(i), CheckResult.Word(v), v,
            w.Count == 0 ? "none" : string.Join("\n", w), ClipUsers.Of(index, i.Slot.Id, indexError).Text, PackageEntries.BigDifferences(i));
    }).ToList();

    /// <summary>Who plays each clip, as the screen shows it before Build: the list's rows, the notice above Build and, while Build waits
    /// for the clip index, the wait line with the index's progress. Returns whether Build waits.</summary>
    bool ShowUsers(IReadOnlyList<PackageItem> items, bool installOpen)
    {
        // an empty list right after a build shows what it built (no notice: nothing is left to build)
        var built = items.Count == 0 ? LastBuilt : null;
        var rows = Rows(built?.Items ?? items, _index, _indexError);
        // before Build: the clips several kinds of character play (or one only probably, or none the tool can place), which a package
        // changes for all of them; before the index is here, that who plays them isn't known yet (or, when it couldn't be built, why)
        var usersNotice = !installOpen || built is not null ? null : ClipIndexView.PackageNotice(_index, items.Select(i => (i.Slot.Name, i.Slot.Id)), _indexError);
        UsersNotice.Text = usersNotice ?? "";
        UsersNotice.Visibility = usersNotice is null ? Visibility.Collapsed : Visibility.Visible;
        // the list's clips are the slots kept (the selection, the search and the filters stay), its own columns read the rows
        ShownRows = rows;
        ItemsPanel.ColumnData = rows.ToDictionary(r => r.Item.Slot.Id);
        ItemsPanel.ReplaceClips([.. rows.Select(r => r.Item.Slot)]);
        ShowChecks();
        FitChangeColumn(rows);
        // Build never writes before who plays each clip is on the screen: while the index is read it waits, greyed, with the progress
        bool waiting = installOpen && _index is null && _indexError is null && items.Count > 0;
        WaitPanel.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        if (waiting) ShowWaitProgress();
        return waiting;
    }

    /// <summary>The column of what was changed in each clip, and its least and most width: at the most, the four every list has, the
    /// checks, who plays each clip and the change are in sight at the window's usual size, and a swap from a clip name of up to about 25
    /// characters reads whole.</summary>
    readonly GridViewColumn _changeColumn;
    internal const double ChangeLeast = 150, ChangeMost = 320;

    /// <summary>The width of who plays each clip: one kind of character whole ("Player", "Teammates"), several on hover.</summary>
    internal const double PlayedByWidth = 130;

    /// <summary>The width the screen last gave the Change column, and the values it was fitted to: a width the person set by dragging
    /// its header stays, and the column is fitted again only when its values change.</summary>
    double _changeFitted = double.NaN;
    string[] _changeFittedTo = [];

    /// <summary>The widest Change value's text, measured when the values change.</summary>
    double _changeWidest;

    /// <summary>The Change column as wide as its longest value in the list's own font (with the room a cell leaves around its text),
    /// between <see cref="ChangeLeast"/> and <see cref="ChangeMost"/>; a longer one ends in an ellipsis and reads whole on hover. Measured
    /// only when the values changed, and never over a width the person set.</summary>
    void FitChangeColumn(IReadOnlyList<PackageRow> rows)
    {
        var values = rows.Select(r => r.What).ToArray();
        if (values.SequenceEqual(_changeFittedTo)) return;
        _changeFittedTo = values;
        var list = ItemsPanel.List;
        var face = new Typeface(list.FontFamily, list.FontStyle, list.FontWeight, list.FontStretch);
        double dip = VisualTreeHelper.GetDpi(list).PixelsPerDip;
        _changeWidest = values.Select(v => new FormattedText(v, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, list.FontSize,
            Brushes.Black, dip).WidthIncludingTrailingWhitespace).DefaultIfEmpty(0).Max();
        RefitChangeColumn();
    }

    /// <summary>The Change column's width from its widest value, and never past the list's right edge as it is laid out now (a narrower
    /// window: the value then ends in an ellipsis inside the list, never cut mid-letter by its edge); again whenever the list's view
    /// gets wider or narrower. A width the person set stays.</summary>
    void RefitChangeColumn()
    {
        if (!double.IsNaN(_changeFitted) && Math.Abs(_changeColumn.Width - _changeFitted) > 0.5) return;
        double width = Math.Clamp(Math.Ceiling(_changeWidest) + ChangeCellRoom, ChangeLeast, ChangeMost);
        var columns = ((GridView)ItemsPanel.List.View).Columns;
        if (FindViewer(ItemsPanel.List) is { ViewportWidth: > 0 } sv)
        {
            // the room from the column's left (the columns before it, and the row's border and margin before the first) to the view's edge
            double left = columns.TakeWhile(c => c != _changeColumn).Sum(c => c.ActualWidth) + RowInset;
            width = Math.Min(width, Math.Max(40, Math.Floor(sv.ViewportWidth - left)));
        }
        if (Math.Abs(width - _changeColumn.Width) > 0.5 || double.IsNaN(_changeFitted)) _changeColumn.Width = width;
        _changeFitted = _changeColumn.Width;
    }

    /// <summary>Where a list's first cell starts in its view: the row's border and the room before the cell.</summary>
    const double RowInset = 8;

    static ScrollViewer? FindViewer(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (VisualTreeHelper.GetChild(d, i) is var c && (c as ScrollViewer ?? FindViewer(c)) is { } sv) return sv;
        return null;
    }

    /// <summary>The room a cell leaves beside its text: the 6 before it and after it, and the row's border.</summary>
    internal const double ChangeCellRoom = 14;

    /// <summary>The rows and the notice for <paramref name="items"/> with <paramref name="index"/> (or its error) as the screen takes them,
    /// as with an install open: the App's own rows and notice, for laying the screen out without an install. Returns whether Build waits.</summary>
    internal bool ShowUsersFor(IReadOnlyList<PackageItem> items, ClipIndex? index, string? indexError, double progress = 0)
    {
        (_index, _indexError, _indexProgress) = (index, indexError, progress);
        ItemsPanel.SetIndex(index);
        return ShowUsers(items, installOpen: true);
    }

    void ShowWaitProgress()
    {
        WaitBar.Value = _indexProgress;
        WaitText.Text = $"Waiting for the clip index: {LibraryView.IndexStage(_indexProgress)}. Build opens once it is ready and the notice above says who plays each clip.";
    }

    /// <summary>For a picture (<see cref="ViewerArgs.PackageWaiting"/>): the screen as it stands while Build waits for the clip index, at
    /// <paramref name="progress"/> (0 to 1), the index held back until <see cref="EndWaitingForShot"/>. Returns an error message, or null.</summary>
    public string? ShowWaitingForShot(double progress)
    {
        if (_install is null) return "no game folder is open";
        if (PackageList.Current.Count == 0) return "the package holds no clip, so Build doesn't wait for the clip index";
        _heldForShot ??= (_index, _indexError);
        (_index, _indexError, _indexProgress) = (null, null, Math.Clamp(progress, 0, 1));
        Refresh();
        return null;
    }

    /// <summary>Gives back the index a picture held back (<see cref="ShowWaitingForShot"/>), if one did.</summary>
    public void EndWaitingForShot()
    {
        if (_heldForShot is not { } held) return;
        (_index, _indexError) = held;
        _heldForShot = null;
        Refresh();
    }

    /// <summary>The list, the plan and the files as they stand, the save folder looked at on the calling thread: for a caller that reads
    /// the screen right after (an install opened, the screen's tab chosen, a picture, the end of a build, the command line's build, the
    /// tests).</summary>
    public void Refresh()
    {
        if (Start() is { } s) Show(s, Look(s));
    }

    /// <summary>As <see cref="Refresh"/>, the plan made and the save folder looked at off the window's thread, for the other refreshes the
    /// person or an event starts (the name or the folder typed, the replace box, the list changed, the clip index or its failure arriving,
    /// the forges' look ending, the backup set, Build after waiting for the index): a folder on a share that doesn't answer doesn't hold
    /// the window up then. What the look gives is dropped when the screen was refreshed again meanwhile; Build is greyed until then. A
    /// fault in it is logged and shown.</summary>
    internal async Task RefreshOffThreadAsync()
    {
        // the run Start makes this one
        int run = _refreshRun + 1;
        try
        {
            if (Start() is not { } s) return;
            BuildButton.IsEnabled = false;
            var looked = await Task.Run(() => Look(s));
            if (run == _refreshRun) Show(s, looked);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while refreshing the Package screen");
            if (run == _refreshRun) ShowError($"The package's plan couldn't be made ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Which refresh is the latest: a look at the save folder made for an earlier one is dropped.</summary>
    int _refreshRun;

    /// <summary>The name or the save folder typed: the screen is refreshed once the typing pauses, not at each key.</summary>
    readonly DispatcherTimer _typed = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>What a refresh takes from the screen for its look at the save folder.</summary>
    sealed record Started(GameInstall Install, IReadOnlyList<PackageItem> Items, string Name, string Folder, string? Backup, string? BuiltFolder, bool Waiting, bool Built);

    /// <summary>What the look found: the plan, whether its folder is the one just built, a note on a folder that is a link or can't be
    /// read, whether the folder holds anything, and then whether it is an earlier version's local undo folder, a build of this tool, and
    /// one of this name; and the notes on folders left beside it.</summary>
    sealed record Looked(PackagePlan Plan, bool JustBuilt, string? Note, bool Occupied, bool Undo, bool Build, bool SameName, IReadOnlyList<string> Beside);

    /// <summary>The list and the summary as they stand; null with no install open (nothing to plan).</summary>
    Started? Start()
    {
        _refreshRun++;
        _typed.Stop();
        var items = PackageList.Current.Items;
        // a clip kept after a build starts a new list: the build's view gives way to it
        if (items.Count > 0) LastBuilt = null;
        bool waiting = ShowUsers(items, _install is not null);
        var built = items.Count == 0 ? LastBuilt : null;
        ItemsTitle.Text = built is null ? "In the package" : "Built, no longer kept";
        if (built is { } b)
        {
            CountText.Text = $"{b.Items.Count} clip{(b.Items.Count == 1 ? "" : "s")} built; nothing left to build";
            RestoreText.Visibility = Visibility.Collapsed;
        }
        else
        {
            int edits = items.Count(i => PackageEntries.Kind(i) == PackageItemKind.Edit);
            CountText.Text = $"{items.Count} clip{(items.Count == 1 ? "" : "s")}: {items.Count - edits} import{(items.Count - edits == 1 ? "" : "s")}, {edits} edit{(edits == 1 ? "" : "s")}";
        }
        SummaryText.Text = _install is null ? "Open your game folder on the Library screen first."
            : built is not null ? $"Built into {built.Value.Folder}. Nothing left to build. "
                + (_builtNotForgotten is { } notSaved
                    ? $"But the list kept between runs couldn't be changed ({notSaved}), so the next run shows the clips built as kept again: remove them there, or build them again. "
                    : "A built list isn't kept, so the next run starts empty. ")
                + "Add an import or an edit to start a new package."
            : items.Count == 0 ? "The package is empty: add an import on the Import screen, or an edit on the Edit screen."
            : $"The package holds {items.Count} clip{(items.Count == 1 ? "" : "s")}, named from {_install.RecordFolderName}." + Failing(items);
        if (_install is null)
        {
            FileList.ItemsSource = null;
            PlanFolderText.Text = "";
            ProblemText.Text = "";
            BuildButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            return null;
        }
        return new Started(_install, items, NameBox.Text, FolderBox.Text, Backup, _builtFolder, waiting, built is not null);
    }

    /// <summary>The plan, and what the file system says of its folder. Touches nothing of the screen.</summary>
    static Looked Look(Started s)
    {
        // the guard keeps its count of the game's own clip files per folder while the folder's listing stays the same, so a save folder
        // of many record files is read once, not at every refresh
        var plan = PackageBuild.Plan(s.Install, s.Items, s.Name, s.Folder, s.Backup);
        string? note = null;
        bool justBuilt = plan.Problem is null && s.BuiltFolder is not null && string.Equals(s.BuiltFolder, plan.OutputFolder.Length > 0 ? Path.GetFullPath(plan.OutputFolder) : "", StringComparison.OrdinalIgnoreCase);
        bool occupied = false;
        if (plan.Problem is null && !justBuilt)
        {
            // a link is named whatever it leads to (an empty folder, or nothing): Build refuses it either way
            if (PackageBuild.IsLink(plan.OutputFolder, out string? unread))
                note = unread is null
                    ? "That folder is a link (a junction or a symbolic link) to another folder, so Build won't replace it: remove the link or give the package another name."
                    : $"That folder can't be read ({unread}): pick another save folder or give the package another name.";
            // a folder that can't be read is said, never thrown
            else
                try { occupied = Directory.Exists(plan.OutputFolder) && Directory.EnumerateFileSystemEntries(plan.OutputFolder).Any(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    note = $"That folder can't be read ({ex.Message}): pick another save folder or give the package another name.";
                }
        }
        // by the marker's kind, not the name alone: a package of this version named like one is a package
        bool undo = occupied && PackageBuild.IsEarlierBuild(plan.OutputFolder, plan.FolderName, restore: true);
        bool build = occupied && !undo && PackageBuild.IsEarlierBuild(plan.OutputFolder);
        bool sameName = build && PackageBuild.IsEarlierBuild(plan.OutputFolder, plan.PackageName.Length > 0 ? plan.PackageName : plan.FolderName, restore: false);
        // folders a build that didn't finish left beside the package, named before Build as after it
        return new Looked(plan, justBuilt, note, occupied, undo, build, sameName, plan.Problem is null ? PackageBuild.BesideNotes(plan) : []);
    }

    /// <summary>The plan, its files and what is said of its folder, onto the screen.</summary>
    void Show(Started s, Looked l)
    {
        var (items, plan, note) = (s.Items, l.Plan, l.Note);
        PlannedWith = (s.Backup, plan);
        // a modded slot's original is looked for in the vanilla backup and the base forge, read once per install and backup in the
        // background; Build waits for them, greyed
        // looked for again whenever a clip is pending that the last look of this install and backup didn't look for (one added since,
        // whose forge that look didn't need to read); never twice for the same clips, so a look that fails can't repeat for ever
        var pendingIds = plan.Originals.Where(o => o.Pending).Select(o => o.Slot.Id).ToHashSet();
        bool looking = pendingIds.Count > 0 && (_lookingIn == (s.Install, s.Backup)
            || _lookedIn != (s.Install, s.Backup) || !pendingIds.IsSubsetOf(_lookedFor));
        if (looking) LookInForges(s.Install, s.Backup, items);
        // an empty package writes nothing: no file is listed (its marker included) until a clip is kept
        FileList.ItemsSource = items.Count == 0 ? null : plan.Files;
        PlanFolderText.Text = plan.OutputFolder.Length == 0 || items.Count == 0 ? "" : $"into {plan.OutputFolder}{Path.DirectorySeparatorChar}";
        if (l.Undo)
            note = "That folder is a local undo folder an earlier version of this tool wrote beside a package: Build never replaces it. Give the package another name.";
        else if (l.Occupied)
            note = !l.Build
                ? "That folder exists and isn't a package this tool built (it has no marker file), so Build won't replace it: give the package another name or pick another save folder."
                : !l.SameName
                ? "That folder holds a package of another name built by this tool (or a local undo folder), so Build won't replace it: give the package another name."
                : ReplaceBox.IsChecked == true ? "That folder holds an earlier build: Build replaces it whole (only if it holds nothing but a package's files)."
                : "That folder holds an earlier build already: to build again, tick \"Replace an earlier build of this package\" or give the package another name.";
        else if (plan.Problem is null && l.JustBuilt)
            note = "Built. To build again into the same folder, tick \"Replace an earlier build of this package\".";
        var said = new List<string>();
        if (note is not null) said.Add(note);
        // where the original clips come from when not from the install, and what is still being looked for; a forge that couldn't be read
        // (the game holding it, say) isn't kept as a result, so Build reads it again
        var forgesUnread = new List<string>();
        if (!looking && plan.Originals.Any(o => o.Pending || o.Missing is not null))
        {
            if (s.Backup is { } backup && PackageBuild.ForgeReadFailure(s.Install, backup) is { } bf)
                forgesUnread.Add($"Your vanilla forge backup couldn't be read for the original clips ({bf}). Build reads it again.");
            if (PackageBuild.ForgeReadFailure(s.Install) is { } gf)
                forgesUnread.Add($"The game's base files couldn't be read for the original clips ({gf}). Build reads them again: close the game first if it is running.");
        }
        if (plan.Problem is null) said.AddRange(forgesUnread);
        string? originals = plan.Problem is null ? PackageBuild.OriginalsNote(plan.Originals) : null;
        said.AddRange(l.Beside);
        // right after a build the summary says nothing is left to build
        ProblemText.Text = s.Built ? "" : plan.Problem ?? string.Join(" ", said);
        // where the originals come from, and the clips packaged without one: a warning, never a refusal (Build stays open)
        OriginalsText.Text = originals ?? "";
        OriginalsText.Visibility = originals is null ? Visibility.Collapsed : Visibility.Visible;
        BuildButton.IsEnabled = !_busy && !s.Waiting && !looking && plan.Problem is null;
        RemoveButton.IsEnabled = CanRemove;
    }

    /// <summary>The install and the backup whose forges are being read, or were read, for the originals: read once per install and backup
    /// path (a new backup chosen reads again).</summary>
    (GameInstall?, string?)? _lookingIn, _lookedIn;

    /// <summary>The clips the last look of <see cref="_lookedIn"/> looked for.</summary>
    HashSet<ulong> _lookedFor = [];

    /// <summary>Reads the vanilla backup and the base forge for the originals of modded slots in the background (what each holds is kept
    /// for the install and path), then plans again, so a clip with no original found is named before Build. Read once per install and
    /// backup: should it end without a result, Build reads them itself.</summary>
    async void LookInForges(GameInstall g, string? backup, IReadOnlyList<PackageItem> items)
    {
        if (_lookingIn == (g, backup)) return;
        var ids = items.Select(i => i.Slot.Id).ToHashSet();
        if (_lookedIn == (g, backup) && ids.IsSubsetOf(_lookedFor)) return;
        _lookingIn = (g, backup);
        try { await Task.Run(() => PackageBuild.Originals(g, items, readForges: true, backup)); }
        // Build reads them again and says what failed (a forge that can't be read isn't thrown: what is, is unforeseen, and logged)
        catch (Exception ex) { App.LogError(ex, "while looking for the original clips in the forges"); }
        finally
        {
            // a new install or backup starts the set afresh; the same one adds the clips just looked for
            if (_lookedIn != (g, backup)) _lookedFor = [];
            _lookedFor.UnionWith(ids);
            (_lookingIn, _lookedIn) = (null, (g, backup));
        }
        if (ReferenceEquals(g, _install)) _ = RefreshOffThreadAsync();
    }

    // ---- the vanilla forge backup ----

    /// <summary>The settings file the backup's path is remembered in between runs (the tool's own, in the user's app-data folder, for a
    /// person's run; the one <c>--settings</c> names for an automated one), or null: not remembered.</summary>
    string? _settingsPath;

    /// <summary>The vanilla backup of the patch forge the originals are also taken from, or null.</summary>
    internal string? Backup { get; private set; }

    /// <summary>The backup the last plan was made with, and that plan (for the tests: Build takes the same backup).</summary>
    internal (string? Backup, PackagePlan Plan)? PlannedWith { get; private set; }

    /// <summary>Takes the settings file to remember the backup in, and the backup remembered there.</summary>
    public void UseSettings(string? path)
    {
        _settingsPath = path;
        var s = path is null ? new ViewerSettings() : ViewerSettings.Load(path);
        // the save folder Build starts in: the one chosen under Settings, else the one last built in, else the tool's own default
        PackageFolderSetting = s.PackageFolder is { Length: > 0 } pf ? pf : null;
        FolderBox.Text = s.StartPackageFolder(DefaultSaveFolder);
        _ = SetBackup(s.BackupForge, remember: false);
    }

    /// <summary>The backup box's own help, as the screen's markup gives it.</summary>
    string? _backupHelp;

    /// <summary>Sets the vanilla backup (null: none): shown in its box, remembered in the settings, and the plan made again with it off the
    /// window's thread (the task ends once it is on the screen).</summary>
    internal Task SetBackup(string? forge, bool remember = true)
    {
        Backup = string.IsNullOrWhiteSpace(forge) ? null : forge;
        BackupBox.Text = Backup ?? "";
        // the box's own help stays; a backup named shows its path above it, and Clear takes the path away again
        _backupHelp ??= BackupBox.ToolTip as string ?? "";
        BackupBox.ToolTip = Backup is null ? _backupHelp : $"{Backup}\n\n{_backupHelp}";
        // the install's own patch forge named as the backup is no vanilla backup: said, and nothing taken from it but the game's own records
        if (Backup is not null && _install is not null
            && string.Equals(Path.GetFullPath(Backup), Path.GetFullPath(_install.PatchForgePath), StringComparison.OrdinalIgnoreCase))
            BackupBox.ToolTip = $"{Backup}: that is your game folder's own forge, not a backup from before you modded it: it gives no original for a clip you modded.\n\n{_backupHelp}";
        ClearBackupButton.IsEnabled = Backup is not null;
        if (remember) Remember(s => s with { BackupForge = Backup }, "The vanilla forge backup");
        return RefreshOffThreadAsync();
    }

    /// <summary>Writes <paramref name="change"/> into the settings file (none for a run without one), the file's other settings kept;
    /// a failure is said on the screen, naming <paramref name="what"/>, and nothing else is stopped.</summary>
    void Remember(Func<ViewerSettings, ViewerSettings> change, string what)
    {
        if (_settingsPath is not { } p) return;
        var protect = new List<string>();
        if (_install is { } g) protect.AddRange(Core.Export.ClipExport.ProtectedFolders(g));
        // a settings file that can't be read now isn't written over (the other settings in it would be lost)
        if (!ViewerSettings.TryLoad(p, out var kept, out var why))
            ShowError($"{what} couldn't be remembered for the next run: the settings file couldn't be read ({why}), so it was left as it is.");
        else
            // guarded as every settings save is: never in the install's folders nor the game folder the settings name
            try { change(kept).Save(p, kept.GameFolder is { Length: > 0 } gf ? [.. protect, gf] : protect); }
            // any failure to remember it is said here, and the work it came from goes on
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                ShowError($"{what} couldn't be remembered for the next run: {ex.Message}");
            }
            // (a settings file that wasn't settings was kept aside before this save: said, after any error already shown)
            finally { if (why is not null) ShowError(ErrorBox.Visibility == Visibility.Visible ? $"{ErrorText.Text} {why}" : why); }
    }

    /// <summary>The save folder chosen under Settings > Package folder, or null for none (Build then starts in the folder it last wrote
    /// into).</summary>
    internal string? PackageFolderSetting { get; private set; }

    /// <summary>Sets the save folder Build starts in (Settings > Package folder; null: none): remembered in the settings, and put in the
    /// save folder box at once.</summary>
    public void SetPackageFolder(string? folder)
    {
        PackageFolderSetting = string.IsNullOrWhiteSpace(folder) ? null : folder;
        Remember(s => s with { PackageFolder = PackageFolderSetting }, "The package folder");
        if (PackageFolderSetting is { } f) FolderBox.Text = f;
    }

    void OnBrowseBackup(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose your vanilla backup of DataPC_patch_01.forge (the game's own, before any mod)",
            Filter = "Patch forge (DataPC_patch_01.forge)|DataPC_patch_01.forge|Forge files (*.forge)|*.forge",
            CheckFileExists = true,
        };
        if (Backup is { } b && Path.GetDirectoryName(b) is { } dir && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = SetBackup(dlg.FileName);
    }

    void OnClearBackup(object sender, RoutedEventArgs e) => _ = SetBackup(null);

    /// <summary>The clips whose checks fail, named after the summary (none: nothing), so a FAIL is seen before Build whatever the list
    /// shows.</summary>
    internal static string Failing(IReadOnlyList<PackageItem> items) => items.Where(i => PackageEntries.GameVerdict(i) == CheckLevel.Fail).Select(i => i.Slot.Name).ToList() is { Count: > 0 } f
        ? $" {f.Count} clip{(f.Count == 1 ? " fails" : "s fail")} {(f.Count == 1 ? "its" : "their")} game checks: {string.Join(", ", f)}."
        : "";

    /// <summary>The folder the last build wrote (full path), so the screen doesn't call it "an earlier build" right after.</summary>
    string? _builtFolder;

    /// <summary>The name, the save folder or the replace box changed by hand: the plan made again off the window's thread, a text box's
    /// once the typing pauses (Build greyed meanwhile).</summary>
    void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not TextBox) { _ = RefreshOffThreadAsync(); return; }
        // a look still running for the text before is dropped: its plan isn't the box's any more
        _refreshRun++;
        BuildButton.IsEnabled = false;
        _typed.Stop();
        _typed.Start();
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (ItemsPanel.SelectedClip is { } c) PackageList.Current.Remove(c.Id);
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder of your own for the package (never the game's folder)" };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        FolderBox.Text = dlg.FolderName;
    }

    async void OnBuild(object sender, RoutedEventArgs e) => await BuildAsync();

    /// <summary>Builds the package as the screen stands, in the background, and shows what was written: its record files, its original
    /// clips and its marker, in one step. Waits first for the clip index while it is being read, so who plays each clip is shown before
    /// anything is written. Returns an error message, or null.</summary>
    public async Task<string?> BuildAsync()
    {
        if (_install is null) return "Open your game folder on the Library screen first.";
        var g = _install;
        // nothing is written before who plays each clip is on the screen: while the clip index is read, Build waits for it (greyed on
        // the screen, with its progress), then the rows and the notice show the users, drawn before the build starts. When the index
        // couldn't be built, Build goes on, the notice saying so.
        if (IndexPending)
        {
            var index = await InstallWork.Index(g);
            if (!ReferenceEquals(g, _install)) return "Another game folder was opened while Build waited for the clip index.";
            if (index is not null) _index = index;
            // a failure reaches the screen (SetIndexError) before the wait ends; this stands in should it not
            else _indexError ??= "no clip index was made for this game folder";
            await RefreshOffThreadAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        var items = PackageList.Current.Items;
        string name = NameBox.Text;
        string? backup = Backup;
        var plan = PackageBuild.Plan(g, items, name, FolderBox.Text, backup);
        if (plan.Problem is { } p) { ShowError(p); return p; }
        bool replace = ReplaceBox.IsChecked == true;
        // the install stays whole until the build ends: another install opened meanwhile (which lets this one go) can't unload the
        // library the build reads the forges' originals with
        IDisposable hold;
        try { hold = g.Hold(); }
        catch (ObjectDisposedException)
        {
            const string left = "Another game folder was opened before the build could start: nothing was built.";
            ShowError(left);
            return left;
        }
        using var _ = hold;
        WriteStarting?.Invoke();
        // the originals from the install, then the vanilla backup, then the base forge; a clip with none is built without it
        var error = await RunBuild(g, items, () => Task.Run(() =>
            {
                BeforeBuildReadsForTests?.Invoke();
                return PackageBuild.Build(g, plan, items, replace, backup);
            }),
            // what the originals folder holds as built; the clips without an original are named by the originals' note under it
            b => b.Originals.All(o => o.Record is null)
                ? $"No \"{PackageBuild.OriginalsFolderName}\" folder: no clip's original was found."
                : $"\"{PackageBuild.OriginalsFolderName}\" holds the game's own record of {(b.Originals.Any(o => o.Record is null) ? "the clips it replaces but those named below" : "each clip it replaces")}: "
                    + "copied over the package's files, they uninstall it.");
        // the folder built in is where the next run starts, when no package folder is set under Settings
        if (error is null) Remember(s => s with { LastPackageFolder = Path.GetFullPath(plan.SaveFolder) }, "The folder built in");
        return error;
    }

    /// <summary>Runs a build of <paramref name="items"/>, kept on <paramref name="g"/>, with the screen busy, and shows its files or its
    /// error (any error: the screen never stays busy). Once built, those clips leave the list, and so the list kept between runs (a clip
    /// kept while it ran stays); the screen shows what was built until a clip is kept again. When another install was opened while it ran,
    /// the built clips leave the list kept for <paramref name="g"/>'s game files instead (<see cref="BuiltElsewhere"/>), and the screen,
    /// showing the other install's list, says so. A build that fails or is refused leaves the list as it was.</summary>
    async Task<string?> RunBuild(GameInstall g, IReadOnlyList<PackageItem> items, Func<Task<PackageBuilt>> build, Func<PackageBuilt, string> after)
    {
        _busy = true;
        HideError();
        ResultText.Text = "";
        BusyPanel.Visibility = Visibility.Visible;
        BuildButton.IsEnabled = RemoveButton.IsEnabled = false;
        string? error = null;
        PackageBuilt? done = null;
        try
        {
            done = await build();
            if (HoldBuildForTests is { } hold) await hold;
        }
        catch (RecordFileException ex) { error = ex.Message; }
        // a folder that can't be written: said; any other error is the code's, below, and logged
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = $"The package couldn't be built: {ex.Message}"; }
        catch (Exception ex)
        {
            App.LogError(ex, "while building the package");
            error = $"The package couldn't be built ({ex.GetType().Name}): {ex.Message}";
        }
        finally
        {
            _busy = false;
            BusyPanel.Visibility = Visibility.Collapsed;
        }
        if (done is { } built)
        {
            _builtFolder = Path.GetFullPath(built.Folder);
            // the package folder is whole (a build writes all or none): the list is done with, the list of the install it was built on.
            // Out of the build's own try: an error here (the code's, in what follows the list's change) is logged and said, and never
            // makes a package written read as not built
            string? elsewhere = null;
            try
            {
                if (ReferenceEquals(g, _install))
                {
                    LastBuilt = (items, built.Folder);
                    // the clips built leave the list; the list kept between runs follows, and when it couldn't, the summary says so
                    if (BuiltHere is { } here) _builtNotForgotten = here(items);
                    else
                    {
                        _builtNotForgotten = null;
                        PackageList.Current.RemoveItems([.. items]);
                    }
                }
                else elsewhere = BuiltElsewhere?.Invoke(g, items) ?? $"Another game folder was opened while it was built: the clips built stay kept for {g.GameFolder}.";
            }
            catch (Exception ex)
            {
                App.LogError(ex, "while taking the clips built out of the package list");
                elsewhere = $"Built, but the package list couldn't be updated ({ex.GetType().Name}): {ex.Message}";
            }
            // built, but a clip without its original is a warning: the theme's warning colour, not success's
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(built.Originals.Any(o => o.Missing is not null) ? "Warning" : "Success"));
            ResultText.Text = $"Built {built.Folder}:\n" + string.Join("\n", built.Files.Select(f => $"  {f.FileName}  ({f.Length:N0} B)"))
                + $"\n{after(built)}"
                + (PackageBuild.OriginalsNote(built.Originals) is { } from ? "\n" + from : "")
                + (built.EarlierLeftAt is { } left ? $"\nThe earlier build, moved aside, couldn't be deleted: it is in {left}. Delete it yourself." : "")
                + (elsewhere is null ? "" : "\n" + elsewhere);
        }
        if (error is not null) ShowError(error);
        Refresh();
        return error;
    }

    /// <summary>A build ended after another install was opened: the window's keeper takes the clips built out of the list kept for the
    /// built install's game files (and out of the list on the screen when it belongs to the same game files). Returns the sentence the
    /// screen says of it.</summary>
    public Func<GameInstall, IReadOnlyList<PackageItem>, string>? BuiltElsewhere { get; set; }

    /// <summary>A build ended on the install open: the window's keeper takes the clips built out of the list and saves it. Returns why
    /// the list kept between runs couldn't be changed, or null.</summary>
    public Func<IReadOnlyList<PackageItem>, string?>? BuiltHere { get; set; }

    /// <summary>Why the list kept between runs couldn't be changed after the last build, or null.</summary>
    string? _builtNotForgotten;

    /// <summary>For tests: run on the build's thread before it reads the forges (the install let go there, as opening another does).</summary>
    internal Action? BeforeBuildReadsForTests { get; set; }

    /// <summary>For tests: awaited after the build's files are written, before the list is done with (another install opened meanwhile).</summary>
    internal Task? HoldBuildForTests { get; set; }

    /// <summary>The command line's build: the save folder and the name set, then Build, as a person would. Returns an error message, or null;
    /// prints the files written.</summary>
    public async Task<string?> BuildForAutomationAsync(string folder, string? name)
    {
        FolderBox.Text = folder;
        if (name is not null) NameBox.Text = name;
        Refresh();
        if (await BuildAsync() is { } err) return err;
        Console.WriteLine(ResultText.Text);
        return null;
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }

    void HideError() => ErrorBox.Visibility = Visibility.Collapsed;

    void OnDismissError(object sender, RoutedEventArgs e) => HideError();
}
