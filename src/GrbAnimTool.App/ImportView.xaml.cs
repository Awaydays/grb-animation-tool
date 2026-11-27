using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;
using NVector3 = System.Numerics.Vector3;
using Transform = GrbAnimTool.Core.Skeletons.Transform;

namespace GrbAnimTool.App;

/// <summary>The Import screen: pick a .glb made on the game's skeleton and the clip it replaces, run the import and its checks, see the report, and play
/// the game's clip and the import side by side on one timeline. The result can be kept for the package (<see cref="PackageList.Current"/>)
/// or its record file exported to a folder of the user's.</summary>
public partial class ImportView : UserControl
{
    /// <summary>One line of the report as the list shows it.</summary>
    public sealed record ReportRow(string Word, CheckLevel Level, string Check, string Text);

    GameInstall? _install;
    bool _automated;

    string? _glbPath;
    double? _glbTicks;
    ClipEntry? _slot;
    ImportResult? _result;
    bool _busy;
    int _runId;
    int _glbReadId;   // the file read's own id (see ChooseGlbAsync)
    bool _readingGlb; // a file chosen and still being read: the result shown is the file before's

    /// <summary>The last import's result, or null when it was refused or none ran.</summary>
    public ImportResult? Result => _result;
    /// <summary>The last import's refusal or failure, or null.</summary>
    public string? LastError { get; private set; }

    // the clip index once the Library has it (who plays the clip)
    ClipIndex? _index;

    // the game's clip solid (side by side) or as a ghost behind the import (overlaid), and the import
    SkeletonScene? _gameScene, _gameGhost, _importScene;
    readonly ModelVisual3D _gameHolder = new(), _importHolder = new();
    Transform[] _model = [];
    readonly PlaybackClock _clock = new();
    /// <summary>Where the game's clip stands beside the import: in its own time, or at the import's progress (<see cref="ReferenceClock"/>).</summary>
    readonly ReferenceClock _reference = new();
    readonly OrbitCamera _camera;
    bool _syncingSlider, _rendering;
    TimeSpan _lastFrame;

    public ImportView()
    {
        InitializeComponent();
        MakeFitDelay();
        SlotPanel.Picked += OnSlotSelected;
        // the list with a few rows and who plays its clip, the viewer, and the checks with every button under them, in a window of any height
        PaneScroll.LeastHeight = () => Math.Max(Math.Max(30 + SlotPanel.LeastHeight + UsersPanel.DesiredSize.Height, LibraryView.LeastViewerHeight),
            ChecksHead.DesiredSize.Height + 120 + ActionPanel.DesiredSize.Height + ActionPanel.Margin.Top);
        PaneScroll.InView.Add((ViewerPane, () => FitScroll.ViewerLeast(ViewerPane, ViewFrame)));
        SameLengthBox = new CheckBox
        {
            Content = "The file's length", IsChecked = true,
            ToolTip = "Only clips as long as the file's animation: an import must last exactly as long as the clip it replaces.",
        };
        SameLengthBox.Click += (_, _) => ApplyLength();
        SlotPanel.ExtraFilter = SameLengthBox;
        _camera = new OrbitCamera(ViewSurface);
        Viewport.Camera = _camera.Camera;
        Viewport.Children.Add(new ModelVisual3D { Content = SkeletonScene.FloorGrid() });
        Viewport.Children.Add(new ModelVisual3D { Content = SkeletonScene.OriginAxes() });
        // the import first, the game's clip after it: overlaid, the game's clip is a see-through ghost, which WPF must draw after the
        // solid import to blend over it (see SkeletonScene.GhostThickness)
        Viewport.Children.Add(_importHolder);
        Viewport.Children.Add(_gameHolder);
        ViewSurface.PreviewMouseDown += (_, _) => ViewSurface.Focus();
        Sketch.Camera = _camera.Camera;
        Sketch.Paint = (dc, project) =>
        {
            if (_result is null) return;
            (Layout == CompareLayout.Overlay ? _gameGhost : _gameScene)?.Sketch(dc, project, new NVector3((float)Compare.Offset(Layout, true), 0, 0));
            _importScene?.Sketch(dc, project, new NVector3((float)Compare.Offset(Layout, false), 0, 0));
        };
        _reference.FollowsEdit = PreviewChoices.OriginalFollowsEdit;
        FollowsEditBox.IsChecked = _reference.FollowsEdit;
        // the timing choice is the Edit screen's too: a change there shows here while this screen is on the window
        Unloaded += (_, _) => { PreviewChoices.Changed -= OnFollowsEditChanged; SyncRendering(stop: true); };
        Loaded += (_, _) => { PreviewChoices.Changed += OnFollowsEditChanged; OnFollowsEditChanged(); SyncRendering(); };
        PackageList.Current.Changed += (_, _) => Dispatcher.BeginInvoke(() => { ShowPackage(); UpdateButtons(); });
        ResetCamera();
        UpdateTimeline();
        UpdateButtons();
    }

    // ---- the install ----

    /// <summary>Takes the install the Library screen opened: the clip list fills, and the animation graph is read in the background for
    /// the motion-field check.</summary>
    public void SetInstall(GameInstall g, bool automated)
    {
        _automated = automated;
        // an off-screen picture can't see the 3D view: the flat sketch stands in for it
        Sketch.Visibility = automated ? Visibility.Visible : Visibility.Collapsed;
        _install = g;
        _index = null;
        _indexError = null;
        _indexProgress = 0;
        // its graph and the rest start being read (each import takes the graph as it stands then)
        InstallWork.Open(g);
        // a run on the install before is dropped when it ends: its result belongs to the old install
        ++_runId;
        _busy = false;
        BusyPanel.Visibility = Visibility.Collapsed;
        _result = null;
        _slot = null;
        PackageList.Current.Clear();
        _gameScene = new SkeletonScene(g.Skeleton);
        _gameGhost = new SkeletonScene(g.Skeleton, ghost: true);
        _importScene = new SkeletonScene(g.Skeleton, second: true);
        _model = new Transform[g.Skeleton.Bones.Count];
        _gameHolder.Children.Clear();
        _importHolder.Children.Clear();
        GlbText.Text = _glbPath ?? "No file chosen.";
        ChooseGlbButton.IsEnabled = true;
        SlotPanel.SetClips(g.AllClips);
        ApplyLength();
        ShowResult();
        // the list's Set filter opens once the install's sets are worked out
        _ = TakeSetsAsync(g);
    }

    async Task TakeSetsAsync(GameInstall g)
    {
        var sets = await InstallWork.Sets(g);
        if (ReferenceEquals(g, _install)) SlotPanel.SetSets(sets);
    }

    // ---- the file ----

    void OnChooseGlb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the .glb to import (made on the game's skeleton, at 60 frames a second)",
            Filter = "glTF binary (*.glb)|*.glb",
            CheckFileExists = true,
        };
        if (_glbPath is not null) dlg.InitialDirectory = Path.GetDirectoryName(_glbPath);
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Guarded(() => ChooseGlbAsync(dlg.FileName), "The file couldn't be taken");
    }

    /// <summary>Runs work started by a click, awaited: any error it lets through ends in the screen's error box, with the screen out of
    /// its busy state, never unseen and never ending the program.</summary>
    async void Guarded(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception ex)
        {
            App.LogError(ex, $"on the Import screen: {what}");
            ++_runId;   // a run still going is dropped
            _busy = false;
            BusyPanel.Visibility = Visibility.Collapsed;
            LastError = $"{what} ({ex.GetType().Name}): {ex.Message}";
            ShowError(LastError);
            UpdateButtons();
        }
    }

    /// <summary>Takes a file: reads its length, offers the slots of that length (and selects the slot it is named after, as the tool's own
    /// exports are), and imports when a slot is chosen.</summary>
    public async Task ChooseGlbAsync(string path, bool pickNamed = true)
    {
        HideError();
        // the path is taken into a local, and a read finished after another file was chosen is dropped by its id: a large first file
        // read slowly never gives its length to the second
        string glb = Path.GetFullPath(path);
        int id = ++_glbReadId;
        _glbPath = glb;
        GlbText.Text = glb;
        _glbTicks = null;
        GlbInfo.Text = "";
        // the result on the screen is the last file's: Keep and Export wait until this file is read and imported
        _readingGlb = true;
        UpdateButtons();
        try
        {
            var scene = await Task.Run(() => GlbScene.Read(glb));
            if (id != _glbReadId) return;
            _glbTicks = ClipImport.AnimationTicks(scene);
            int bones = _install is null ? 0 : scene.Nodes.Count(n => _install.Skeleton.Bones.Any(b => b.Name == n.Name));
            GlbInfo.Text = _glbTicks is double t
                ? FormattableString.Invariant($"{scene.Nodes.Count} nodes ({bones} of the game skeleton's bones), {scene.Animations.Count} animation(s), {t / 60:F3} s = {t:F1} ticks at 60 a second")
                : $"{scene.Nodes.Count} nodes, no animation";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (id != _glbReadId) return;
            GlbInfo.Text = $"This file can't be read: {ex.Message}";
        }
        catch (Exception ex)
        {
            App.LogError(ex, $"while reading {Path.GetFileName(glb)}");
            if (id != _glbReadId) return;
            // anything else (out of memory on a huge file, say) is shown too, never left to end the program
            GlbInfo.Text = $"This file can't be read ({ex.GetType().Name}): {ex.Message}";
        }
        _readingGlb = false;
        UpdateButtons();
        ApplyLength();
        if (!pickNamed) return;
        // the slot the file is named after (as the tool's own exports are), selected as a click would select it: that runs the import
        if (_install is not null && _slot is null && ClipQuery.Find(_install.AllClips, Path.GetFileNameWithoutExtension(glb)) is { } named && SlotPanel.Rows.Contains(named))
            SlotPanel.Select(named.Name);
        else await RunAsync();
    }

    // ---- the slot list ----

    /// <summary>Takes the clip index the Library built for the install: the list's readable names and categories fill in, and the
    /// search takes the index's fields.</summary>
    public void SetIndex(GameInstall g, ClipIndex index)
    {
        if (!ReferenceEquals(g, _install)) return;
        // a picture's hold ends: the index that came is the screen's now
        _heldForShot = null;
        _index = index;
        SlotPanel.SetIndex(index);
        ShowUsers();
        UpdateButtons();
    }

    /// <summary>How far the install's clip index is read (0 to 1): shown under Export while it waits for the index.</summary>
    public void SetIndexProgress(GameInstall g, double progress)
    {
        if (!ReferenceEquals(g, _install) || _index is not null || _heldForShot is not null) return;
        _indexProgress = Math.Max(_indexProgress, progress);
        UpdateButtons();
    }

    /// <summary>How far the clip index's read is (0 to 1), shown while the export waits for it.</summary>
    double _indexProgress;

    /// <summary>Whether the export waits for the clip index: an install is open, its index isn't here yet and hasn't failed.</summary>
    bool IndexPending => _install is not null && _index is null && _indexError is null;

    /// <summary>Called on the window's thread just before the export writes, with who plays the clip on the screen: the tests' view of
    /// the screen as it stands then.</summary>
    internal Action? WriteStarting;

    /// <summary>Waits for the install's clip index when this screen hasn't had it yet nor heard that it couldn't be built (an export
    /// before the index is ready), so the screen says who plays the clip before the write.</summary>
    public async Task WaitForIndexAsync()
    {
        if (_index is not null || _indexError is not null || _install is not { } g) return;
        if (await InstallWork.Index(g) is { } index) SetIndex(g, index);
    }

    /// <summary>The clip index couldn't be built for the install: who plays the clip says so, with the reason, instead of waiting.</summary>
    public void SetIndexError(GameInstall g, string reason)
    {
        if (!ReferenceEquals(g, _install)) return;
        _heldForShot = null;
        _indexError = reason;
        ShowUsers();
        UpdateButtons();
    }

    /// <summary>Why the install's clip index couldn't be built, when it couldn't; null while it may still come.</summary>
    string? _indexError;

    /// <summary>Who plays the clip the file replaces, and the notice when that is more than one kind of character, one only probably,
    /// none the tool can place, or not known (the index not ready, or not built): the import kept or exported changes the clip for all
    /// of them. Nothing is said before a clip is chosen.</summary>
    void ShowUsers()
    {
        var notice = _slot is null ? null : ClipUsers.Of(_index, _slot.Id, _indexError).Notice;
        UsersText.Text = _slot is null ? "" : ClipIndexView.PlayedByLine(_index, _slot.Id, _indexError);
        UsersNotice.Text = notice ?? "";
        UsersNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The list's fixed constraint: with "The file's length" on and a file read, only the clips of the file's length (an import
    /// must be exactly its slot's length).</summary>
    void ApplyLength()
    {
        if (SameLengthBox.IsChecked == true && _glbTicks is double t)
        {
            int ticks = (int)Math.Round(t);
            SlotPanel.SetConstraint(c => c.LastTick == ticks, $"only the file's length, {ticks} ticks");
        }
        else SlotPanel.SetConstraint(null);
    }

    /// <summary>The list's switch for the file's length, in its filter row.</summary>
    internal readonly CheckBox SameLengthBox;

    void OnSlotSelected(ClipEntry? c)
    {
        if (c is not null && c != _slot)
        {
            _slot = c;
            Guarded(RunAsync, "The import failed");
        }
    }

    /// <summary>Selects a slot by name or hex ID, clearing the filters and the file's length if they hide it, and imports. Returns an
    /// error message, or null.</summary>
    public async Task<string?> ChooseSlotAsync(string clip)
    {
        if (_install is null) return "Open your game folder on the Library screen first.";
        var c = ClipQuery.Find(_install.AllClips, clip);
        if (c is null) return $"No clip named or with the ID '{clip}' in this game folder.";
        if (!SlotPanel.Rows.Contains(c))
        {
            SameLengthBox.IsChecked = false;
            ApplyLength();
            SlotPanel.ClearFilters();
        }
        _slot = c;
        SlotPanel.Show(c);
        await RunAsync();
        return null;
    }

    // ---- the import ----

    /// <summary>Imports the chosen file into the chosen slot in the background and shows the result. Returns an error message (the
    /// refusal), or null.</summary>
    public Task<string?> RunAsync() => _lastRun = RunNowAsync();

    async Task<string?> RunNowAsync()
    {
        if (_install is null || _glbPath is null || _slot is null) { ShowResult(); return "Choose a .glb and the clip it replaces."; }
        var g = _install;
        var slot = _slot;
        string path = _glbPath;
        int id = ++_runId;
        _busy = true;
        _result = null;
        HideError();
        StatusText.Text = "";
        BusyPanel.Visibility = Visibility.Visible;
        BusyText.Text = $"Importing into {slot.Name}...";
        ShowUsers();
        UpdateButtons();
        string? error = null;
        ImportResult? r = null;
        try
        {
            // the install's graph as it is read now (read again after a retry on the Edit screen; nothing for an install no longer open)
            var graph = await InstallWork.Graph(g);
            var fit = FitNow(slot);
            bool autoStart = CutStartBox.Text.Trim().Length == 0;
            r = await Task.Run(() =>
            {
                // a cut with no start given: the start where the loop closes best, found on the file at its own length
                if (fit is { Mode: FitMode.Cut } && autoStart && _glbTicks is double ft && ft > slot.LastTick)
                {
                    var whole = ClipImport.Run(g, slot, path, graph, fit with { Mode = FitMode.Hold, Loop = false });
                    if (whole.Unfitted is { } u && ClipFit.SuggestCut(u, slot.LastTick) is int at) fit = fit with { CutStart = at };
                }
                return ClipImport.Run(g, slot, path, graph, fit);
            });
        }
        catch (ImportRefusedException ex) { error = $"Not imported: {ex.Message}"; }
        // a file that can't be read, or data that isn't what it should be: said; any other error is the code's, below, and logged
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"The import failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            // any other failure of the work (out of memory, a fault in the import) is shown and logged, and the screen leaves its busy state
            App.LogError(ex, $"while importing {Path.GetFileName(path)} into {slot.Name}");
            error = $"The import failed ({ex.GetType().Name}): {ex.Message}";
        }
        if (id != _runId) return error;   // a newer run started meanwhile
        _busy = false;
        BusyPanel.Visibility = Visibility.Collapsed;
        _result = r;
        LastError = error;
        if (error is not null) ShowError(error);
        ShowResult();
        if (r is not null && !_automated) _clock.Play();
        UpdateTimeline();
        return error;
    }

    void ShowResult()
    {
        var r = _result;
        _gameHolder.Children.Clear();
        _importHolder.Children.Clear();
        if (r is null)
        {
            ReportList.ItemsSource = null;
            VerdictText.Text = _slot is null || _glbPath is null ? "Nothing imported yet." : "Not imported.";
            VerdictText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key("Text"));
            _clock.Reset(0, 0);
            _reference.Reset(0, 0);
        }
        else
        {
            // the failures first, then the warnings, then the passes, each in the checks' own order (the sort is stable), so what needs
            // a look is in sight without a scroll
            ReportList.ItemsSource = EditView.ReportRows(r.Report);
            var v = r.Report.Verdict;
            VerdictText.Text = $"{r.Report.Headline} {r.Written} tracks written, {r.Kept} kept as the game's."
                + (v == CheckLevel.Fail ? " A failed game check means the game may not play it correctly, so it can't be added to the package: look at the lines in red." : "");
            VerdictText.SetResourceReference(TextBlock.ForegroundProperty, Theme.TextKeyOf(v));
            int head = r.Game.Bone("Head");
            var face = head >= 0 ? SkeletonScene.FaceAxis(r.Game.Model(0, head).Rotation) : (NVector3?)null;
            // a clip of another skeleton than the body's: its scenes drawn on it
            if (!ReferenceEquals(_gameScene?.Skeleton, r.Game.Skeleton))
            {
                var sk = r.Game.Skeleton;
                _gameScene = new SkeletonScene(sk);
                _gameGhost = new SkeletonScene(sk, ghost: true);
                _importScene = new SkeletonScene(sk, second: true);
                _model = new Transform[sk.Bones.Count];
            }
            _gameScene!.NoseAxis = _gameGhost!.NoseAxis = _importScene!.NoseAxis = face;
            _gameScene.ShowAddOnFor(r.Slot.Record!.Tracks);
            _gameGhost.ShowAddOnFor(r.Slot.Record!.Tracks);
            _importScene.ShowAddOnFor(r.Slot.Record!.Tracks);
            _importHolder.Children.Add(_importScene.Visual);
            _clock.Reset(r.Slot.Duration, r.Slot.LastTick);
            // the game's clip is the import's length: in its own time it keeps the import's tick, as following it does
            _reference.Reset(r.Slot.Duration, r.Slot.LastTick);
        }
        ShowLayout();
        ShowPose();
        UpdateTimeline();
        UpdateButtons();
        ShowPackage();
        ShowUsers();
    }

    /// <summary>The buttons' own tooltips, shown while they can be used.</summary>
    object? _keepTip, _exportTip;

    /// <summary>Why the result isn't kept or exported, or null: it is the game's own clip unchanged, or what the install already holds
    /// for the clip (an import of an unedited export, say, or of the builder's own clip on an install holding it), so it replaces nothing.</summary>
    string? Unchanged => _result is { } r && _install is { } g ? PackageEntries.Unchanged(g, r.Slot, r.Record) : null;

    /// <summary>Why the result can't be added to the package, or null: no change (<see cref="Unchanged"/>), or a game check failed
    /// (<see cref="PackageEntries.NotForPackage"/>). A look check never stops it.</summary>
    string? NotKeepable => Unchanged ?? (_result is { } r ? PackageEntries.NotForPackage(r.Report.Verdict) : null);

    /// <summary>A fit option changed: the import made again with it, once typing in the start box pauses, so the result shown (and kept)
    /// is always the one the options give.</summary>
    void OnFitChanged(object sender, RoutedEventArgs e)
    {
        if (_fitDelay is null || FitRow.Visibility != Visibility.Visible) return;
        _fitDelay.Stop();
        _fitDelay.Start();
    }

    void OnFitTextChanged(object sender, TextChangedEventArgs e) => OnFitChanged(sender, e);

    DispatcherTimer? _fitDelay;

    /// <summary>Starts the fit options' re-run timer (made once the screen's controls are).</summary>
    void MakeFitDelay()
    {
        _fitDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _fitDelay.Tick += (_, _) =>
        {
            _fitDelay.Stop();
            if (_install is not null && _glbPath is not null && _slot is not null) Guarded(async () => await RunAsync(), "The import couldn't be made again");
        };
    }

    /// <summary>The fit for a file of another length than <paramref name="slot"/> (as the Fit row stands), or null when the lengths are
    /// equal or not known: the import's exact-length path, unchanged.</summary>
    FitOptions? FitNow(ClipEntry slot)
    {
        if (_glbTicks is not double t || (int)Math.Round(t) == slot.LastTick) return null;
        int.TryParse(CutStartBox.Text.Trim(), out int start);
        var mode = StretchChoice.IsChecked == true ? FitMode.Speed : HoldChoice.IsChecked == true ? FitMode.Hold : FitMode.Cut;
        return new FitOptions { Mode = mode, CutStart = Math.Max(0, start), Loop = LoopFitBox.IsChecked == true };
    }

    void UpdateButtons()
    {
        FitRow.Visibility = _slot is { } fs && _glbTicks is double ft && (int)Math.Round(ft) != fs.LastTick ? Visibility.Visible : Visibility.Collapsed;
        _keepTip ??= KeepButton.ToolTip;
        _exportTip ??= ExportButton.ToolTip;
        // an import that changes nothing is greyed with its reason: the game's own record is the game's file, never packaged
        string? same = _busy ? null : Unchanged;
        // a failed game check keeps it out of the package; it can still be exported to try
        string? failed = _busy || same is not null || _result is null ? null : PackageEntries.NotForPackage(_result.Report.Verdict);
        bool usable = _result is not null && !_busy && !_readingGlb && same is null;
        KeepButton.IsEnabled = usable && failed is null;
        // nothing is written before who plays the clip is on the screen: while the clip index is read, Export waits for it (greyed,
        // with the index's progress under it), as the Package screen's Build does; keeping writes nothing, and the package waits itself
        bool waiting = usable && IndexPending;
        ExportButton.IsEnabled = usable && !waiting;
        KeepButton.ToolTip = (same ?? failed) is { } why ? $"Not for the package: {why}." : _keepTip;
        ExportButton.ToolTip = same is null ? _exportTip : $"Not exported: {same}.";
        ExportWaitText.Text = waiting
            ? $"Waiting for the clip index: {LibraryView.IndexStage(_indexProgress)}. Export opens once it is ready and Played by says who plays the clip."
            : "";
        ExportWaitText.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        // both buttons are greyed, so the reason names both; only the game's own clip unchanged is to be changed and imported again: what
        // the install holds may be the builder's own installed clip, whose reason says the way out (an import on game files that don't hold it)
        KeepReasonText.Text = failed is not null ? $"Not added: {failed}." : same is null ? ""
            : $"Not added or exported: {same}." + (same == PackageEntries.GamesOwnUnchanged ? " Change the clip and import it again." : "");
        KeepReasonText.Visibility = same is null && failed is null ? Visibility.Collapsed : Visibility.Visible;
        // an import made but not in the package is said, until it is kept or another is made
        NotKeptText.Visibility = KeepButton.IsEnabled && _result is { } r
            && !PackageList.Current.Items.Any(i => i.Slot.Id == r.Slot.Id && i.Record.AsSpan().SequenceEqual(r.Record)) ? Visibility.Visible : Visibility.Collapsed;
        ChooseGlbButton.IsEnabled = _install is not null;
        // the list says how far the clip index is read while its readable names and categories wait for it
        SlotPanel.SetIndexNote(IndexPending ? "the clip index: " + LibraryView.IndexStage(_indexProgress) + ", its columns fill in when ready" : null);
    }

    void ShowPackage()
    {
        var items = PackageList.Current.Items;
        PackageText.Text = items.Count == 0 ? "In the package: none yet."
            : $"In the package ({items.Count}): " + string.Join(", ", items.Select(i => $"{i.Slot.Name} ({CheckResult.Word(PackageEntries.GameVerdict(i))})"));
    }

    void OnKeep(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        // the re-run made first may be refused (its reason is in the error box already); any other reason is said there too
        if (await KeepAsync() is { } why && ErrorBox.Visibility != Visibility.Visible) ShowError(why);
    }, "The clip couldn't be added");

    /// <summary>A fit option changed and its re-run hasn't started yet: started now and awaited, so what is kept or exported is the
    /// result the options give, not the one before.</summary>
    async Task FlushFitAsync()
    {
        if (_fitDelay is { IsEnabled: true })
        {
            _fitDelay.Stop();
            if (_install is not null && _glbPath is not null && _slot is not null) { await RunAsync(); return; }
        }
        // a re-run already going (it started while a folder was being chosen, say): its result, not none
        if (_busy && _lastRun is { } run) await run;
    }

    /// <summary>The newest import run (<see cref="RunAsync"/>).</summary>
    Task<string?>? _lastRun;

    /// <summary>Keeps the result for the package, the fit options' pending re-run made first. Returns an error message, or null.</summary>
    public async Task<string?> KeepAsync()
    {
        await FlushFitAsync();
        if (_result is null) return "Nothing imported to add.";
        if (NotKeepable is { } why) return $"Not added: {_result.Slot.Name}: {why}.";
        bool replaced = PackageList.Current.Keep(_result);
        StatusText.Text = $"Added {_result.Slot.Name} to the package{(replaced ? ", in place of the one added before for this clip" : "")}.";
        return null;
    }

    async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder of your own for the record file (never the game's folder)" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        // never a silent overwrite: "Replace files already there" decides; without it, files of these names there are refused
        await ExportShownAsync(dlg.FolderName, ReplaceBox.IsChecked == true);
    }

    /// <summary>The export as the button runs it (<see cref="ExportAsync"/>): a refusal is shown on the screen, the early ones too
    /// (nothing imported, or the import changed while the export waited for the clip index). Returns the error message, or null.</summary>
    internal async Task<string?> ExportShownAsync(string folder, bool overwrite)
    {
        var m = await ExportAsync(folder, overwrite);
        if (m is not null && ErrorBox.Visibility != Visibility.Visible) ShowError(m);
        return m;
    }

    /// <summary>Writes the record file and its report into <paramref name="folder"/>; files of the same names already
    /// there are refused (nothing is written) unless <paramref name="overwrite"/>. While the clip index is being read it waits for it
    /// first, saying so (the screen's button is greyed meanwhile), and lets the screen draw who plays the clip before anything is
    /// written, so the users notice shows them first. Under automation (<paramref name="commandLine"/>) a
    /// refusal of files already there says to add <c>--overwrite</c>, the command line's own way, instead of the screen's box. Returns an
    /// error message, or null.</summary>
    public async Task<string?> ExportAsync(string folder, bool overwrite, bool commandLine = false)
    {
        await FlushFitAsync();
        if (_result is null || _install is null) return "Nothing imported to export.";
        if (_index is null && _indexError is null)
        {
            GameInstall g = _install;
            var result = _result;
            StatusText.Text = "Waiting for the clip index before writing, so the screen says who plays the clip...";
            ExportButton.IsEnabled = false;
            await WaitForIndexAsync();
            UpdateButtons();
            StatusText.Text = "";
            // a failure reaches the screen (SetIndexError) before the wait ends; this stands in should it not (for another install
            // open by now, SetIndexError leaves the screen alone)
            if (_index is null && _indexError is null) SetIndexError(g, "no clip index was made for this game folder");
            // who plays the clip, now on the screen, is drawn before the write starts
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (!ReferenceEquals(g, _install) || !ReferenceEquals(result, _result))
                return "The import changed while the export waited for the clip index: export again.";
        }
        WriteStarting?.Invoke();
        return Write(folder, overwrite, commandLine);
    }

    /// <summary>The export's writing, once the screen says who plays the clip (<see cref="ExportAsync"/>).</summary>
    string? Write(string folder, bool overwrite, bool commandLine)
    {
        if (_result is null || _install is null) return "Nothing imported to export.";
        if (Unchanged is { } same)
        {
            // the record file of an unchanged import is the game's own file (or the install's own): it replaces nothing
            string m = $"Not exported: {_result.Slot.Name}: {same}.";
            ShowError(m);
            return m;
        }
        HideError();
        try
        {
            var files = ImportOutput.Write(_install, folder, [_result], overwrite);
            var f = files.Package.Files[0];
            StatusText.Text = $"Wrote {f.Path} ({f.Length:N0} B), with {Path.GetFileName(files.Reports[0])} beside it. "
                + $"Copy it into the {files.Package.DatafileFolderName} folder of the unpacked animation datafile (named 24_-_... on the 2023 game files).";
            return null;
        }
        catch (RecordFileException ex) when (!overwrite && ex.Message.Contains("exists already", StringComparison.Ordinal))
        {
            string m = commandLine ? $"{ex.Message} Add --overwrite to replace them, or give another folder."
                : $"{ex.Message} Tick \"Replace files already there\" to replace them, or pick another folder.";
            ShowError(m);
            return m;
        }
        catch (Exception ex)
        {
            // any failure is shown, never left to end the program; one not foreseen is logged too
            App.LogError(ex, "while exporting the record file");
            string m = ex is RecordFileException or IOException or UnauthorizedAccessException ? ex.Message : $"The export failed ({ex.GetType().Name}): {ex.Message}";
            ShowError(m);
            return m;
        }
    }

    // ---- the viewport and the timeline ----

    void ShowPose()
    {
        Sketch.InvalidateVisual();
        bool shown = _result is not null;
        HintText.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = _install is null ? "Open your game folder on the Library screen first."
            : _glbPath is null ? "Choose a .glb and the clip it replaces." : _slot is null ? "Choose the clip it replaces in the list." : "Not imported: see the message above.";
        if (!shown) { OverlayText.Text = ""; return; }
        var r = _result!;
        int t = Math.Clamp(_clock.Tick, 0, r.Game.LastTick);
        int gt = Math.Clamp(_reference.TickAt(t), 0, r.Game.LastTick);
        GameTickShown = gt;
        bool mover = MoverBox.IsChecked == true;
        Draw(Layout == CompareLayout.Overlay ? _gameGhost! : _gameScene!, r.Game, gt, mover);
        Draw(_importScene!, r.Imported, t, mover);
        // the clip and its change in the tool's one wording, as the Edit screen's view says an edit (made once for each import shown, not
        // on every frame)
        // (the file the import was made from, not the one chosen since, still being read)
        if (!ReferenceEquals(_headOf, r)) (_headOf, _head) = (r, $"{r.Slot.Name} ({ClipChange.Imported(r.Source).Words})");
        OverlayText.Text = $"{_head}\n{_clock.Time:F3} s  tick {t} / {r.Slot.LastTick}  mover {(mover ? "on" : "off")}\n"
            + (_reference.FollowsEdit ? $"the game's clip at tick {gt}, following the import's progress\n" : $"the game's clip in its own time: {_reference.Time:F3} s  tick {gt} / {_reference.LastTick}\n")
            + (Layout == CompareLayout.Overlay ? "green: the import   pale, behind: the game's clip" : "blue, left: the game's clip   green, right: the import");
    }

    /// <summary>The overlay's first line, and the import it was made for.</summary>
    object? _headOf;
    string _head = "";

    /// <summary>The game's clip's tick last drawn beside the import, the game's clip's own time and the import's clock (for checking their timing).</summary>
    internal int GameTickShown { get; private set; }
    internal ReferenceClock Reference => _reference;
    internal PlaybackClock Clock => _clock;

    void Draw(SkeletonScene scene, Motion m, int t, bool mover)
    {
        for (int b = 0; b < _model.Length; b++) _model[b] = m.Model(t, b);
        scene.Update(_model, m.Mover(t), mover);
    }

    void UpdateTimeline()
    {
        _syncingSlider = true;
        TimeSlider.Maximum = Math.Max(_clock.Duration, 1e-3);
        TimeSlider.Value = _clock.Time;
        _syncingSlider = false;
        TimeText.Text = $"{_clock.Time:F3} s  tick {_clock.Tick} / {_clock.LastTick}";
        PlayButton.Content = _clock.Playing ? "❚❚ Pause" : "▶ Play";
        PlayButton.IsEnabled = StepBackButton.IsEnabled = StepForwardButton.IsEnabled = TimeSlider.IsEnabled = _result is not null;
        SyncRendering();
    }

    void SyncRendering(bool stop = false)
    {
        bool want = !stop && IsLoaded && _clock.Playing && _result is not null;
        if (want == _rendering) return;
        if (want) { _lastFrame = TimeSpan.Zero; CompositionTarget.Rendering += OnRendering; }
        else CompositionTarget.Rendering -= OnRendering;
        _rendering = want;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 0 : Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;
        Frame(dt);
    }

    /// <summary>One frame: <paramref name="realSeconds"/> of real time gone by on the import's clock and the game's clip's (which moves
    /// only while the import plays).</summary>
    internal void Frame(double realSeconds)
    {
        if (_reference.Advance(_clock, realSeconds)) { ShowPose(); UpdateTimeline(); }
        else if (!_clock.Playing) UpdateTimeline();
    }

    void OnScrub(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || _result is null) return;
        _reference.Seek(_clock, (float)e.NewValue);
        ShowPose();
        UpdateTimeline();
    }

    void OnPlayPause(object sender, RoutedEventArgs e) { if (_result is null) return; _reference.Toggle(_clock); UpdateTimeline(); }
    void OnStepBack(object sender, RoutedEventArgs e) => Step(-1);
    void OnStepForward(object sender, RoutedEventArgs e) => Step(1);

    void Step(int ticks)
    {
        if (_result is null) return;
        _reference.Step(_clock, ticks);
        ShowPose();
        UpdateTimeline();
    }

    void OnLoopToggled(object sender, RoutedEventArgs e) => _clock.Loop = LoopBox.IsChecked == true;
    void OnMoverToggled(object sender, RoutedEventArgs e) => ShowPose();

    /// <summary>"Game's clip follows the import's progress" clicked: the choice for both screens, remembered.</summary>
    void OnFollowsEditToggled(object sender, RoutedEventArgs e)
    {
        if (PreviewChoices.Set(FollowsEditBox.IsChecked == true, remember: true, _install is { } g ? Core.Export.ClipExport.ProtectedFolders(g) : []) is { } error)
            ShowError(error);
        OnFollowsEditChanged();
    }

    /// <summary>The timing choice as it stands, shown: the box and the game's clip's place.</summary>
    void OnFollowsEditChanged()
    {
        bool on = PreviewChoices.OriginalFollowsEdit;
        FollowsEditBox.IsChecked = on;
        if (_reference.FollowsEdit == on) return;
        _reference.FollowsEdit = on;
        ShowPose();
    }
    void OnResetCamera(object sender, RoutedEventArgs e) => ResetCamera();

    /// <summary>A view from the front right of both skeletons, far enough to hold both.</summary>
    void ResetCamera()
    {
        _camera.Set(new Point3D(0, 0, 0.9), yaw: 70, pitch: 10, distance: Compare.Distance(Layout));
        Sketch.InvalidateVisual();
    }

    // ---- overlay or side by side ----

    /// <summary>How the game's clip and the import stand: overlaid at first (to see exactly which bones the import changed), or as
    /// chosen in the box, which holds while the program runs.</summary>
    internal CompareLayout Layout { get; private set; } = CompareLayout.Overlay;

    bool _settingLayout;

    void OnLayoutChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_settingLayout || LayoutBox.SelectedIndex < 0 || LegendText is null) return;   // LegendText: null while the screen is being made
        Layout = (CompareLayout)LayoutBox.SelectedIndex;
        ShowLayout();
        ResetCamera();
        ShowPose();
    }

    /// <summary>The box shows the layout; overlaid, the game's clip is a ghost behind the import, side by side both stand solid and apart;
    /// the legend says which is which.</summary>
    void ShowLayout()
    {
        _settingLayout = true;
        LayoutBox.SelectedIndex = (int)Layout;
        _settingLayout = false;
        _gameHolder.Children.Clear();
        if (_result is not null && (Layout == CompareLayout.Overlay ? _gameGhost : _gameScene) is { } game) _gameHolder.Children.Add(game.Visual);
        _gameHolder.Transform = new TranslateTransform3D(Compare.Offset(Layout, true), 0, 0);
        _importHolder.Transform = new TranslateTransform3D(Compare.Offset(Layout, false), 0, 0);
        LegendText.Text = Layout == CompareLayout.Overlay
            ? "Green: the import, as its record plays · pale, behind: the game's clip · red: where each head looks"
            : "Left, blue: the game's clip · right, green: the import, as its record plays · red: where each head looks";
    }

    /// <summary>Space plays or pauses, Left and Right step a tick, unless a text box or the list has the keyboard.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Left or Key.Right)) return;
        for (var d = Keyboard.FocusedElement as DependencyObject; d is not null; d = (d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
        {
            if (d is TextBoxBase or ComboBox or ComboBoxItem or ListView or ListViewItem or ListBox or ListBoxItem) return;
            if (d is ButtonBase && e.Key == Key.Space) return;
        }
        if (e.Key == Key.Space && e.IsRepeat) { e.Handled = true; return; }
        if (e.Key == Key.Space) OnPlayPause(this, e); else Step(e.Key == Key.Left ? -1 : 1);
        e.Handled = true;
    }

    /// <summary>Sets up one picture: the time (the shot's own, else the run's, else 0) and the mover setting, paused, the camera reset.</summary>
    public string? ShowForShot(ShotSpec shot, ViewerArgs args)
    {
        // no file chosen (a run that imports nothing): the screen as it stands, its list
        if (_result is null) return _glbPath is null ? null : ErrorBox.Visibility == Visibility.Visible ? ErrorText.Text : "Nothing imported.";
        if ((shot.Mover ?? args.Mover) is bool m) MoverBox.IsChecked = m;
        if (shot.Follows is bool follows) { PreviewChoices.Set(follows, remember: false, []); OnFollowsEditChanged(); }
        _reference.Seek(_clock, shot.Time ?? args.Time ?? 0);
        ResetCamera();
        ShowPose();
        UpdateTimeline();
        return null;
    }

    /// <summary>What a picture of the screen waiting held back: the clip index and its failure.</summary>
    (ClipIndex? Index, string? Error)? _heldForShot;

    /// <summary>For a picture (<see cref="ViewerArgs.ImportWaiting"/>): the screen as it stands while the clip index is read, at the
    /// shot's time as its progress (0 to 1), the index held back until <see cref="EndWaitingForShot"/>; an import made shown at the run's
    /// time. Returns an error message, or null.</summary>
    public string? ShowWaitingForShot(ShotSpec shot, ViewerArgs args)
    {
        if (_install is null) return "no game folder is open";
        _heldForShot ??= (_index, _indexError);
        (_index, _indexError, _indexProgress) = (null, null, Math.Clamp(shot.Time ?? 0, 0, 1));
        SlotPanel.SetIndex(null);
        ShowUsers();
        UpdateButtons();
        return _result is null ? null : ShowForShot(shot with { Time = null }, args);
    }

    /// <summary>Gives back the index a picture held back (<see cref="ShowWaitingForShot"/>), if one did.</summary>
    public void EndWaitingForShot()
    {
        if (_heldForShot is not { } held) return;
        (_index, _indexError) = held;
        _heldForShot = null;
        SlotPanel.SetIndex(_index);
        ShowUsers();
        UpdateButtons();
    }

    /// <summary>The 3D view's area, for checking a picture.</summary>
    public FrameworkElement ViewArea => ViewSurface;

    /// <summary>For checking a picture: runs <paramref name="render"/> with both skeletons taken out of the view (and the sketch), or
    /// returns null when none is shown.</summary>
    public T? WithoutSkeleton<T>(Func<T> render) where T : class
    {
        if (_result is null) return null;
        Viewport.Children.Remove(_gameHolder);
        Viewport.Children.Remove(_importHolder);
        try { return Sketch.Visibility == Visibility.Visible ? Sketch.WithoutContent(render) : render(); }
        finally
        {
            Viewport.Children.Add(_importHolder);
            Viewport.Children.Add(_gameHolder);
        }
    }

    // ---- errors ----

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }

    void HideError() => ErrorBox.Visibility = Visibility.Collapsed;

    void OnDismissError(object sender, RoutedEventArgs e) => HideError();
}
