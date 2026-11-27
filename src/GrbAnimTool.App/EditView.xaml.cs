using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using NVector3 = System.Numerics.Vector3;
using Transform = GrbAnimTool.Core.Skeletons.Transform;

namespace GrbAnimTool.App;

/// <summary>The Edit screen: a clip (the Library's, or one picked here), the five edits with the unavailable ones greyed and their reason
/// written under them, Apply in the background, the result playing with the original as a ghost on one timeline (trim handles over it),
/// the checks of the result with the edit's notes and warnings, and "Add to package".</summary>
public partial class EditView : UserControl
{
    /// <summary>One line of the report as the list shows it.</summary>
    public sealed record ReportRow(string Word, CheckLevel Level, string Check, string Text)
    {
        /// <summary>The check's own "?" text, for a check that has one (the feet slide), else null.</summary>
        public string? Help { get; init; }
        public Visibility HelpShown => Help is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A report's rows as the screens list them: game failures, game warnings, big differences, notes, then the rest, each in the
    /// checks' own order (the sort is stable); each with its kind's word and its colour (<see cref="CheckResult.Shown"/>).</summary>
    internal static List<ReportRow> ReportRows(CheckReport report) => report.Results.OrderByDescending(x => x.Rank).Select(x => new ReportRow(x.Said, x.Shown, x.Check,
        x.Message + (x.Tick is int t ? $" (tick {t}{(x.Bone is null ? "" : $", {x.Bone}")})" : x.Bone is null ? "" : $" ({x.Bone})"))
        { Help = x.Check == Feet.SlideCheck ? HelpTexts.FeetSlide : null }).ToList();

    GameInstall? _install;
    bool _automated;
    LibraryView? _library;

    ClipEntry? _clip;
    EditContext? _ctx;
    SkeletonMirror? _mirror;
    Motion? _original;
    List<EditOption> _options = [];
    IReadOnlyList<ClipEntry> _sources = [];
    EditOutcome? _outcome;
    TrimHandles _trim = TrimHandles.Whole(1);
    bool _busy;
    int _runId;

    /// <summary>The last Apply's refusal or failure, or null.</summary>
    public string? LastError { get; private set; }
    public EditOutcome? Outcome => _outcome;

    readonly DispatcherTimer _selectDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    /// <summary>Cancels the clip choice in flight (its trial edits), when another clip or install is chosen.</summary>
    CancellationTokenSource? _choosing;
    // the clip index once the Library has it (who plays the clip)
    ClipIndex? _index;

    SkeletonScene? _scene, _ghost;
    readonly ModelVisual3D _sceneHolder = new(), _ghostHolder = new();
    Transform[] _model = [];
    readonly PlaybackClock _clock = new();
    /// <summary>Where the original stands beside the result: in its own time, or at the result's progress (<see cref="ReferenceClock"/>).</summary>
    readonly ReferenceClock _reference = new();
    readonly OrbitCamera _camera;
    bool _syncingSlider, _rendering;
    TimeSpan _lastFrame;
    List<EventMark> _events = [];
    List<PlantSpan> _plants = [];
    System.Windows.Shapes.Line? _head;

    public EditView()
    {
        InitializeComponent();
        ClipPanel.Picked += OnClipSelected;
        // Ctrl or Shift selects several clips: Replace takes them as its clips, as a set's
        ClipPanel.MultiSelect = true;
        ClipPanel.SelectionPicked += OnSelectionPicked;
        // the edits each clip allows, in a column of this screen's own after the four every list has, and the switch for the clips
        // that allow none (hidden until it is ticked)
        ClipPanel.AddColumn("Edits", 88, "The edits the clip allows: M mirror, R root-motion scale, S speed, T trim, W swap (a dot: not allowed). Filled in once they are worked out (the bar in the filters shows how far), at once when the cache holds them.",
            (c, data) => (data as EditMarks)?.Marks(c) ?? "",
            (c, data) => (data as EditMarks)?.MarksText(c) ?? "", font: "Consolas");
        ShowAllBox = new CheckBox
        {
            Content = "Show all clips", IsChecked = false, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = ShowAllTip,
        };
        ToolTipService.SetShowOnDisabled(ShowAllBox, true);
        ShowAllBox.Click += (_, _) => ApplyChoices();
        // how far the install's edit marks are worked out, beside the switch that waits for them
        MarksBar = new ProgressBar
        {
            Width = 80, Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        MarksText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed };
        MarksText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key("TextSecondary"));
        RetryButton = new Button
        {
            Content = "Retry", Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Works out again which edits each clip allows, after it failed.",
        };
        RetryButton.Click += OnRetryChoices;
        var own = new StackPanel { Orientation = Orientation.Horizontal };
        own.Children.Add(ShowAllBox);
        own.Children.Add(MarksBar);
        own.Children.Add(MarksText);
        own.Children.Add(RetryButton);
        ClipPanel.ExtraFilter = own;
        _marksTick.Tick += (_, _) => ShowMarksProgress();
        // the list with a few rows, the viewer with a usable 3D view, and the edits (a few lines of them at least: they scroll on their
        // own), the Apply buttons, the notices and a few lines of the checks, in a window of any height
        PaneScroll.LeastHeight = () => Math.Max(30 + ClipPanel.LeastHeight,
            Math.Min(ChoicesPanel.DesiredSize.Height, LeastChoicesHeight) + UnderChoices() + LeastChecksHeight);
        PaneScroll.InView.Add((ViewerPane, () => FitScroll.ViewerLeast(ViewerPane, ViewFrame)));
        // the edits take the pane's height but for what is under them and a few more lines of the checks; past that they scroll
        EditsPane.LayoutUpdated += (_, _) =>
        {
            double room = Math.Max(LeastChoicesHeight, EditsPane.ActualHeight - UnderChoices() - WishedChecksHeight);
            if (Math.Abs(ChoicesScroll.MaxHeight - room) > 0.5) ChoicesScroll.MaxHeight = room;
        };
        _selectDelay.Tick += (_, _) => OnSelectDelay();
        // the notice of a result not in the package follows the list, whichever screen changes it
        PackageList.Current.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateButtons);
        _trimDelay.Tick += (_, _) => { _trimDelay.Stop(); CheckTrim(); };
        _camera = new OrbitCamera(ViewSurface);
        Viewport.Camera = _camera.Camera;
        Viewport.Children.Add(new ModelVisual3D { Content = SkeletonScene.FloorGrid() });
        Viewport.Children.Add(new ModelVisual3D { Content = SkeletonScene.OriginAxes() });
        // the solid result first, the see-through ghost after it: WPF draws a viewport's models in order, so the ghost is blended over a
        // result already drawn, never the other way round (see SkeletonScene.GhostThickness for where the two stand in the same place)
        Viewport.Children.Add(_sceneHolder);
        Viewport.Children.Add(_ghostHolder);
        ViewSurface.PreviewMouseDown += (_, _) => ViewSurface.Focus();
        Sketch.Camera = _camera.Camera;
        Sketch.Paint = (dc, project) =>
        {
            if (_original is null) return;
            var layout = ShownLayout;
            if (_ghostHolder.Children.Count > 0) _ghost?.Sketch(dc, project, new NVector3((float)Compare.Offset(layout, true), 0, 0));
            _scene?.Sketch(dc, project, new NVector3((float)Compare.Offset(layout, false), 0, 0));
        };
        _reference.FollowsEdit = PreviewChoices.OriginalFollowsEdit;
        FollowsEditBox.IsChecked = _reference.FollowsEdit;
        ShowLayout();
        // the timing choice is the Import screen's too: a change there shows here while this screen is on the window
        Loaded += (_, _) => { PreviewChoices.Changed += OnFollowsEditChanged; OnFollowsEditChanged(); SyncRendering(); };
        Unloaded += (_, _) => { PreviewChoices.Changed -= OnFollowsEditChanged; SyncRendering(stop: true); };
        ResetCamera();
        UpdateTimeline();
        _built = true;
        ShowValues();
        UpdateButtons();
    }

    // ---- the install and the clip ----

    /// <summary>Takes the install the Library screen opened, and the Library itself (for its clip); with <paramref name="marks"/> false (a
    /// run that pictures no Edit screen) every clip's edit marks aren't worked out.</summary>
    public void SetInstall(GameInstall g, bool automated, LibraryView library, bool marks = true)
    {
        _automated = automated;
        // a selection made in the Library again after another is taken again
        if (!ReferenceEquals(_library, library)) library.ClipPanel.SelectionPicked += _ => _libraryTaken = null;
        _library = library;
        Sketch.Visibility = automated ? Visibility.Visible : Visibility.Collapsed;
        // a clip choice or an Apply in flight for the old install is dropped when it ends
        ++_runId;
        _choosing?.Cancel();
        _selectDelay.Stop();
        SetBusy(null);
        _install = g;
        _index = null;
        _indexError = null;
        _clip = null;
        _ctx = null;
        _original = null;
        _outcome = null;
        _setOutcomes = null;
        _preview = null;
        _editSets = null;
        _setsFailed = false;
        _set = null;
        _setRows = [];
        SetClipList.ItemsSource = null;
        _options = [];
        _scene = new SkeletonScene(g.Skeleton);
        _ghost = new SkeletonScene(g.Skeleton, ghost: true);
        _model = new Transform[g.Skeleton.Bones.Count];
        _sceneHolder.Children.Clear();
        _ghostHolder.Children.Clear();
        ClipText.Text = "No clip chosen: pick one in the list, or use the Library's.";
        ClipInfo.Text = "";
        ShowUsers();
        UseLibraryButton.IsEnabled = true;
        _choices = null;
        _choicesError = null;
        (_incomplete, _unknown) = (null, 0);
        MarksHad = null;
        RetryButton.Visibility = Visibility.Collapsed;
        ClipPanel.ColumnData = null;
        _chosen = null;
        _libraryTaken = null;
        ClipPanel.SetClips(g.AllClips);
        _replaceSource = null;
        _glbTicks = null;
        _replaceGlb = null;
        OnReplaceSearch(ReplaceSearch, null!);
        ShowOptions();
        ShowOutcome();
        InstallWork.Open(g);
        // the sets once the install's reads are open for it
        _ = TakeSetsAsync(g);
        _marksWorkedOut = marks;
        if (marks) TakeChoices(g, InstallWork.Marks(g, library.IndexCacheFolder));
        else ApplyChoices();
    }

    /// <summary>The checks list's least height: three lines of checks in sight (it scrolls for the rest).</summary>
    const double LeastChecksHeight = 48;

    /// <summary>The checks list's height the edits leave it before they scroll, and the edits' least height in sight (they scroll past it).</summary>
    const double WishedChecksHeight = 120, LeastChoicesHeight = 200;

    /// <summary>What stands under the edits in their pane: the Apply buttons, the notices, the checks' head and the notes' expander.</summary>
    double UnderChoices() => ButtonsPanel.DesiredSize.Height + ButtonsPanel.Margin.Top + NoticesPanel.DesiredSize.Height + 8
        + ChecksHead.DesiredSize.Height + NotesExpander.DesiredSize.Height + NotesExpander.Margin.Top;

    /// <summary>The list's switch for the clips that allow no edit, in its filter row, the bar and the line saying how far the install's
    /// edit marks are worked out, and the button that works them out again after they failed.</summary>
    internal readonly CheckBox ShowAllBox;
    internal readonly ProgressBar MarksBar;
    internal readonly TextBlock MarksText;
    internal readonly Button RetryButton;

    const string ShowAllTip = "Off: only the clips that allow at least one edit. On: every clip, those that allow none among them (each says why under its greyed edits).";

    /// <summary>Every clip's edit marks on the install, once had (null before).</summary>
    EditMarks? _choices;

    /// <summary>Why the marks had aren't whole (<see cref="EditMarks.Incomplete"/>), and how many clips they don't tell; null and 0 when
    /// they are.</summary>
    string? _incomplete;
    int _unknown;

    /// <summary>Why every clip's edit marks couldn't be worked out, when they couldn't (the list then shows every clip, and Retry tries
    /// again); null while they may come.</summary>
    string? _choicesError;

    /// <summary>The install's marks being had, while the screen waits for them (null once they came or failed).</summary>
    MarksJob? _marksJob;

    /// <summary>Shows the marks' progress while they are worked out (a poll: the workers report from their own threads).</summary>
    readonly DispatcherTimer _marksTick = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>Whether the screen waits for the install's edit marks: the list shows every clip meanwhile, its edits column empty.</summary>
    internal bool MarksPending => _marksJob is not null || _shotProgress is not null;

    /// <summary>Waits for every clip's edit marks on the install (had in the background, from the cache or worked out), the list and its
    /// switch saying it waits, with the progress; then marks and filters the list by them. When they couldn't be worked out, the list says so
    /// and offers Retry.</summary>
    async Task TakeChoicesAsync(GameInstall g, MarksJob job)
    {
        _marksWorkedOut = true;
        _marksJob = job;
        _choicesError = null;
        _retryError = null;
        RetryButton.Visibility = Visibility.Collapsed;
        ShowMarksProgress();
        // marks already had (a retry) stay in force meanwhile: the list as it is
        if (_choices is null) ApplyChoices();
        _marksTick.Start();
        var r = await job.Done;
        if (!ReferenceEquals(g, _install) || !ReferenceEquals(job, _marksJob)) return;
        _marksTick.Stop();
        _marksJob = null;
        if (r.Marks is not null) SetMarks(r.Marks, Had(r));
        else if (_choices is not null)
        {
            // a retry that failed keeps the marks had before, saying so, with Retry again
            _retryError = r.Error ?? "no result";
            RetryButton.Visibility = Visibility.Visible;
            ShowMarksProgress();
            ApplyChoices(keepPlace: true);
        }
        else SetChoicesFailed(r.Error ?? "no result");
    }

    /// <summary>Why working the marks out again (Retry) failed, the marks had before kept; null otherwise.</summary>
    string? _retryError;

    /// <summary>How the marks were had, in words, for the switch's tooltip.</summary>
    static string Had(MarksResult r)
    {
        string t = FormattableString.Invariant($"{r.Time.TotalSeconds:F1} s");
        string how = r.Outcome switch
        {
            CacheOutcome.Reused => $"read from the cache in {t}",
            CacheOutcome.Built => $"worked out in {t}",
            CacheOutcome.RebuiltChanged => $"worked out again in {t} (the game files or the tool changed since the cache was made)",
            CacheOutcome.RebuiltUnreadable => $"worked out again in {t} (the cache couldn't be read)",
            CacheOutcome.RebuiltOnRequest => $"worked out again on request in {t}",
            _ => $"worked out in {t} (this run keeps no cache)",
        };
        if (r.Outcome is null) how += r.SaveError is null ? "" : $"; {r.SaveError}";
        else if (r.Outcome != CacheOutcome.Reused) how += r.SaveError is null ? ", and kept in the cache for the next start" : $", but weren't kept in the cache: {r.SaveError}";
        return how;
    }

    /// <summary>For pictures and tests: waits for every clip's edit marks on the install, so the list shows them.</summary>
    public async Task WaitForChoicesAsync()
    {
        while (_install is { } g && _marksJob is { } job)
        {
            await job.Done;
            // the screen takes them on its own continuation: let it run
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (ReferenceEquals(job, _marksJob) && ReferenceEquals(g, _install)) break;
        }
    }

    /// <summary>The marks' progress a picture of the screen waiting stands at (<see cref="ShowWaitingForShot"/>), or null.</summary>
    double? _shotProgress;

    /// <summary>What a picture of the screen waiting held back: the marks and their failure.</summary>
    (EditMarks? Marks, string? Error)? _heldForShot;

    /// <summary>The share of the marks had, 0 to 1.</summary>
    internal double MarksProgress => _shotProgress ?? _marksJob?.Progress ?? 1;

    /// <summary>How the install's marks were had, in words ("from the cache in 0.1 s"), once they were; null before.</summary>
    internal string? MarksHad { get; private set; }

    /// <summary>The progress of the marks being had, in the bar and the line beside the switch; the switch waits (greyed) meanwhile.</summary>
    void ShowMarksProgress()
    {
        bool pending = MarksPending;
        MarksBar.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        double p = MarksProgress;
        MarksBar.Value = p;
        bool again = pending && _choices is not null;
        // the marks had, when something about them is to be said: a retry that failed, marks that aren't whole, clips the tool failed on
        string? warning = pending || _choices is null ? null
            : _retryError is { } failed ? $"working them out again failed ({failed}): the marks had before are kept"
            : _incomplete is { } why ? $"not every clip's edits are known: {why}"
            : _unknown > 0 ? $"the tool failed on {_unknown:N0} clip(s): which edits they allow isn't known (the detail is in the error log)"
            : null;
        MarksText.Text = pending ? FormattableString.Invariant($"working out the edits{(again ? " again" : "")}, {p * 100:F0}%") : warning ?? "";
        MarksText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(warning is not null ? "Warning" : "TextSecondary"));
        MarksText.Visibility = pending || warning is not null ? Visibility.Visible : Visibility.Collapsed;
        ShowAllBox.IsEnabled = _choices is not null;
        ShowAllBox.ToolTip = again
            ? "Working out again which edits each clip allows (the bar beside shows how far); the list stays as it is until then. " + ShowAllTip
            : pending
            ? "Waiting for which edits each clip allows, worked out in the background (the bar beside shows how far): every clip is listed until then. " + ShowAllTip
            : ShowAllTip + (MarksHad is { } had ? $" The edits each clip allows were {had}." : "");
        // the count's note follows the progress; the rows stay as they are (no new search, no scroll)
        if (pending && _choicesError is null) ClipPanel.SetConstraintNote(_choices is { } kept ? ChoicesNote(kept) : WaitingNote);
    }

    /// <summary>For tests: takes every clip's edit choices as their marks (<see cref="SetMarks"/>).</summary>
    internal void SetChoices(InstallEditChoices? choices) => SetMarks(choices is null ? null : EditMarks.From(choices));

    /// <summary>Takes every clip's edit marks: the list's edits column fills in, and, unless "Show all clips" is ticked, the clips that
    /// allow no edit leave the list. <paramref name="had"/>: how they were had, said in the switch's tooltip.</summary>
    internal void SetMarks(EditMarks? marks, string? had = null)
    {
        _marksJob = null;
        _marksTick.Stop();
        // a picture's hold ends: the marks that came are the screen's now
        (_heldForShot, _shotProgress) = (null, null);
        _choices = marks;
        _choicesError = null;
        _retryError = null;
        MarksHad = had;
        // marks that aren't whole (something they need didn't read: Retry works them out again from the start) or with clips the tool
        // failed on (the same every time for these files and this build): the clips not told are listed
        _incomplete = marks?.Incomplete;
        // the clips the tool failed on, by ID (two records of one ID are one clip; clips left out by an error of the moment aren't among them)
        _unknown = marks?.Unknown.Count ?? 0;
        RetryButton.Visibility = _incomplete is null ? Visibility.Collapsed : Visibility.Visible;
        ClipPanel.ColumnData = marks;
        ShowMarksProgress();
        // a row clicked just before (its short wait still running) is taken now, before the list is filtered: as the clip shown it stays in
        // the list, whatever it allows
        if (_selectDelay.IsEnabled && ClipPanel.SelectedClip is { } picked && picked != _clip)
        {
            _selectDelay.Stop();
            Choose(picked);
        }
        ApplyChoices(keepPlace: true);
    }

    /// <summary>Makes the edit in place of <see cref="EditJobs.Apply"/> (for tests: one that fails as the code or the data might).</summary>
    internal static Func<ClipEntry, EditRequest, EditContext, SkeletonMirror?, EditOutcome>? ApplyForTests;

    /// <summary>The short wait after a row is picked before its clip is taken (for tests: one long enough to hold a click pending).</summary>
    internal TimeSpan SelectDelay
    {
        get => _selectDelay.Interval;
        set => _selectDelay.Interval = value;
    }

    /// <summary>Every clip's edit marks couldn't be worked out: the list stops waiting, shows every clip, says why, and offers Retry.</summary>
    internal void SetChoicesFailed(string reason)
    {
        _marksJob = null;
        _marksTick.Stop();
        (_heldForShot, _shotProgress) = (null, null);
        _choices = null;
        _choicesError = reason;
        _retryError = null;
        (_incomplete, _unknown) = (null, 0);
        MarksHad = null;
        RetryButton.Visibility = Visibility.Visible;
        ClipPanel.ColumnData = null;
        ShowMarksProgress();
        ApplyChoices(keepPlace: true);
    }

    void OnRetryChoices(object sender, RoutedEventArgs e)
    {
        if (_install is not { } g) return;
        // from the start: the graph, the swap sources and the mirror read again, the cache passed over
        TakeChoices(g, InstallWork.Retry(g, _library?.IndexCacheFolder));
    }

    /// <summary>Takes the marks (<see cref="TakeChoicesAsync"/>), any error on the way ending as their failure on the screen, with Retry,
    /// at once (never left in a task nobody looks at, the screen waiting for ever).</summary>
    async void TakeChoices(GameInstall g, MarksJob job)
    {
        try { await TakeChoicesAsync(g, job); }
        catch (Exception ex)
        {
            App.LogError(ex, "while showing which edits each clip allows");
            if (!ReferenceEquals(g, _install)) return;
            try { SetChoicesFailed($"{ex.GetType().Name}: {ex.Message}"); }
            catch (Exception) { }
            ShowError($"Which edits each clip allows couldn't be shown ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>The list's constraint: the clips that allow an edit, unless "Show all clips" is ticked or the marks aren't known yet.</summary>
    /// <remarks><paramref name="keepPlace"/>: the marks came (or a picture's hold ended) on their own, the list staying where it was browsed;
    /// otherwise (a switch ticked, a clip chosen) it shows the clip shown.</remarks>
    void ApplyChoices(bool keepPlace = false)
    {
        var choices = _choices;
        if (choices is null) ClipPanel.SetConstraint(null, WaitingNote, keepPlace);
        else if (ShowAllBox.IsChecked == true) ClipPanel.SetConstraint(null, ChoicesNote(choices), keepPlace);
        else
            // the clip on the screen stays in the list (selected) even when it allows no edit, so the list never loses what is shown
            ClipPanel.SetConstraint(c => (choices.Any(c) ?? true) || c == _clip, ChoicesNote(choices), keepPlace);
    }

    /// <summary>The count's note with marks had: what they leave out, the clips they don't tell (listed, as ones that may allow an edit),
    /// and a retry in progress.</summary>
    string ChoicesNote(EditMarks choices)
    {
        // every clip listed without marks: those the tool failed on and those an error of the moment left out, by distinct ID
        int notTold = NotTold(choices);
        string unknown = notTold == 0 ? "" : $"; which edits {notTold:N0} clip(s) allow isn't known, so they are listed";
        string again = MarksPending ? FormattableString.Invariant($"; working them out again ({MarksProgress * 100:F0}%)") : "";
        return ShowAllBox.IsChecked == true ? $"{choices.NoEdit:N0} of them allow no edit{unknown}{again}"
            : $"{choices.NoEdit:N0} that allow no edit left out (Show all clips lists them){unknown}{again}";
    }

    /// <summary>How many of the install's clips (by distinct ID) the marks don't tell: the clips the tool failed on, and those an error of
    /// the moment left out. Counted once per marks had.</summary>
    int NotTold(EditMarks choices)
    {
        var clips = ClipPanel.Clips;
        if (!ReferenceEquals(choices, _notToldOf.Marks) || !ReferenceEquals(clips, _notToldOf.Clips))
            _notToldOf = (choices, clips, clips.Where(c => choices.MaskFor(c) is null).Select(c => c.Key).Distinct().Count());
        return _notToldOf.Count;
    }

    (EditMarks? Marks, IReadOnlyList<ClipEntry>? Clips, int Count) _notToldOf;

    /// <summary>The count's note with no marks yet: every clip is listed, and why (they are being worked out, with the progress, or failed,
    /// or a run that pictures no Edit screen doesn't work them out).</summary>
    string? WaitingNote => _choicesError is { } why ? $"which edits each clip allows couldn't be worked out ({why}), so every clip is listed"
        : _install is null ? null
        : !_marksWorkedOut && !MarksPending ? "which edits each clip allows isn't worked out in this run, so every clip is listed"
        : FormattableString.Invariant($"working out which edits each clip allows ({(MarksPending ? MarksProgress : 0) * 100:F0}%): every clip is listed until then");

    /// <summary>Whether the install's marks are worked out in this run (<see cref="SetInstall"/>'s marks).</summary>
    bool _marksWorkedOut = true;

    /// <summary>For a picture (<see cref="ViewerArgs.EditWaiting"/>): the screen as it stands while the edits each clip allows are worked
    /// out, at the shot's time as their progress (0 to 1), the marks held back until <see cref="EndWaitingForShot"/>; the clip shown at the
    /// run's time. Returns an error message, or null.</summary>
    public string? ShowWaitingForShot(ShotSpec shot, ViewerArgs args)
    {
        if (_install is null) return "no game folder is open";
        _heldForShot ??= (_choices, _choicesError);
        _shotProgress = Math.Clamp(shot.Time ?? 0, 0, 1);
        (_choices, _choicesError) = (null, null);
        ClipPanel.ColumnData = null;
        ShowMarksProgress();
        ApplyChoices(keepPlace: true);
        return ShowForShot(shot with { Time = null }, args);
    }

    /// <summary>Gives back the marks a picture held back (<see cref="ShowWaitingForShot"/>), if one did.</summary>
    public void EndWaitingForShot()
    {
        if (_heldForShot is not { } held) return;
        (_choices, _choicesError) = held;
        _heldForShot = null;
        _shotProgress = null;
        ClipPanel.ColumnData = _choices;
        ShowMarksProgress();
        ApplyChoices(keepPlace: true);
    }

    /// <summary>The edits the clip shown allows, in words (for a log).</summary>
    internal string OptionsSummary => _options.Count == 0 ? "no edits shown"
        : "allows " + (_options.Where(o => o.Available).Select(o => EditJobs.Name(o.Kind)).ToList() is { Count: > 0 } l ? string.Join(", ", l) : "none");

    /// <summary>Whether the clip named <paramref name="clip"/> (by name or hex ID) is the one shown.</summary>
    public bool ShowsClip(string clip) => _install is { } g && _clip is not null && ClipQuery.Find(g.Clips, clip) == _clip;

    /// <summary>When the screen is shown with no clip chosen, it takes the Library's.</summary>
    public void Shown()
    {
        if (TakeLibrarySelection()) return;
        if (_clip is null && _library?.CurrentClip is { } c) Choose(c);
    }

    void OnUseLibraryClip(object sender, RoutedEventArgs e)
    {
        if (TakeLibrarySelection(again: true)) return;
        if (_library?.CurrentClip is { } c) Choose(c);
        else ShowError("The Library screen shows no clip: select one there, or pick one in this screen's list.");
    }

    // the Library's selection of several last taken, so coming back to the screen doesn't undo what was chosen here since
    IReadOnlyList<ClipEntry>? _libraryTaken;

    /// <summary>Several clips selected in the Library: selected here too, as Ctrl-clicks in this screen's list would (Replace's clips),
    /// the Library's clip shown. Taken once per selection, unless <paramref name="again"/>. Returns whether it took one.</summary>
    bool TakeLibrarySelection(bool again = false)
    {
        if (_install is null || _library?.ClipPanel.SelectedClips is not { Count: >= 2 } many) return false;
        if (!again && _libraryTaken is { } was && was.SequenceEqual(many)) return false;
        _libraryTaken = many;
        var shown = _library.CurrentClip is { } cur && many.Contains(cur) ? cur : many[^1];
        LibrarySelectionTaken = TakeLibrarySelectionAsync(many, shown, ++_takeRun);
        return true;
    }

    // the takings of the Library's selection: one taken again (Use the Library's clip) ends the one before
    int _takeRun;

    /// <summary>The last taking of the Library's selection (for tests).</summary>
    internal Task LibrarySelectionTaken { get; private set; } = Task.CompletedTask;

    async Task TakeLibrarySelectionAsync(IReadOnlyList<ClipEntry> many, ClipEntry shown, int run)
    {
        try
        {
            // the Library's clip shown first (a clip shown stays in the list whatever its filters), then the rows selected around it, it
            // last, as the row picked; those hidden by this screen's search or filters brought back
            await ChooseClipAsync(shown);
            // another clip chosen here, or the Library's selection taken again, while the clip was read: that stands
            if (_clip != shown || run != _takeRun) return;
            // past the most a selection takes (Ctrl+A): nothing selected row by row, the screen says why at once
            if (many.Select(c => c.Id).Distinct().Count() > MaxChosen) { OnSelectionPicked(many); return; }
            // clips that allow no edit (Replace still takes them) shown, then the search and filters cleared, when they hide any
            if (_choices is { } marks && ShowAllBox.IsChecked != true && many.Any(c => marks.Any(c) == false))
            {
                ShowAllBox.IsChecked = true;
                ApplyChoices();
            }
            if (!many.All(ClipPanel.Rows.ToHashSet().Contains)) ClipPanel.ClearFilters();
            ClipPanel.SelectMany([.. many.Where(c => c != shown), shown]);
        }
        // started from a tab shown or a click and not awaited: nothing may go unseen
        catch (Exception ex)
        {
            App.LogError(ex, "while taking the Library's clips on the Edit screen");
            SetBusy(null);
            ShowError($"The Library's clips couldn't be taken ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Takes a clip from a click or a key: any error ends in the screen's error box, with the screen out of busy.</summary>
    async void Choose(ClipEntry c)
    {
        try { await ChooseClipAsync(c); }
        catch (Exception ex)
        {
            App.LogError(ex, $"while showing {c.Name} on the Edit screen");
            SetBusy(null);
            ShowError($"{c.Name} couldn't be shown ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Takes the clip index the Library built for the install: the list's readable names and categories fill in, and the
    /// search takes the index's fields.</summary>
    public void SetIndex(GameInstall g, ClipIndex index)
    {
        if (!ReferenceEquals(g, _install)) return;
        _index = index;
        ClipPanel.SetIndex(index);
        ShowUsers();
    }

    /// <summary>The clip index couldn't be built for the install: who plays the clip says so, with the reason, instead of waiting.</summary>
    public void SetIndexError(GameInstall g, string reason)
    {
        if (!ReferenceEquals(g, _install)) return;
        _indexError = reason;
        ShowUsers();
    }

    /// <summary>Why the install's clip index couldn't be built, when it couldn't; null while it may still come.</summary>
    string? _indexError;

    /// <summary>Who plays the clip being edited, and the notice when that is more than one kind of character, one only probably, none
    /// the tool can place, or not known (the index not ready, or not built): an edit added to the package changes the clip for all of
    /// them. Nothing is said before a clip is chosen.</summary>
    void ShowUsers()
    {
        var notice = _clip is null ? null : ClipUsers.Of(_index, _clip.Id, _indexError).Notice;
        UsersText.Text = _clip is null ? "" : ClipIndexView.PlayedByLine(_index, _clip.Id, _indexError);
        UsersNotice.Text = notice ?? "";
        UsersNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnClipSelected(ClipEntry? picked)
    {
        // a short wait, so moving through the list with the arrow keys starts one clip's trial edits, not one per row passed
        _selectDelay.Stop();
        if (picked is not null && picked != _clip) _selectDelay.Start();
        // Apply is greyed during the wait: it would run on the clip still shown, and its result be dropped when the choice lands
        UpdateButtons();
    }

    void OnSelectDelay()
    {
        _selectDelay.Stop();
        // the row the list stands on (the last one added to a selection of several), not merely the first selected
        if ((ClipPanel.Shown is { } s && ClipPanel.SelectedClips.Contains(s) ? s : ClipPanel.SelectedClip) is { } c && c != _clip) Choose(c);
        else UpdateButtons();
    }

    /// <summary>Selects a clip by name or hex ID and waits for its edits' availability. Returns an error message, or null.</summary>
    public async Task<string?> ChooseClipAsync(string clip)
    {
        if (_install is null) return "Open your game folder on the Library screen first.";
        var c = ClipQuery.Find(_install.AllClips, clip);
        if (c is null) return $"No clip named or with the ID '{clip}' in this game folder.";
        await ChooseClipAsync(c);
        return _original is null ? ErrorText.Text : null;
    }

    /// <summary>Takes a clip: its motion, the graph's verdict on it, its swap sources, and each edit's availability (a trial run in the
    /// background), then shows them.</summary>
    public async Task ChooseClipAsync(ClipEntry c)
    {
        if (_install is null) return;
        var g = _install;
        int id = ++_runId;
        // the previous clip's trial edits stop between edits; whatever they find is dropped
        _choosing?.Cancel();
        var cts = _choosing = new CancellationTokenSource();
        var cancel = cts.Token;
        var before = _clip;
        _clip = c;
        _ctx = null;
        _original = null;
        // the last replace's track map and loop mark are the last clip's
        TrackMapText.Text = "";
        _loopFrom = null;
        _outcome = null;
        _setOutcomes = null;
        SetResultText.Text = "";
        _options = [];
        _sources = [];
        LastError = null;
        HideError();
        StatusText.Text = "";
        // the clip shown stays in the filtered list (a clip taken from the Library that allows no edit, say), and only while it is shown:
        // the rows worked out again (where the list was browsed kept) when the new clip isn't in them or the one before was there only as
        // the clip shown; then the new one is selected
        if (_choices is { } marks && ShowAllBox.IsChecked != true
            && (!ClipPanel.Rows.Contains(c) || before is not null && before != c && marks.Any(before) == false && ClipPanel.Rows.Contains(before)))
            ApplyChoices(keepPlace: true);   // (the clip is scrolled to just below)
        ClipPanel.Show(c);
        ClipText.Text = c.Name;
        ClipInfo.Text = FormattableString.Invariant($"record {c.Index}, ID 0x{c.Id:X16}, {c.Duration:F3} s ({c.LastTick} ticks), {c.TrackCount} tracks, {c.StatusText}{(c.FilePath is { } f ? $", file {System.IO.Path.GetFileName(f)}" : "")}");
        ShowUsers();
        _trim = TrimHandles.Whole(c.LastTick);
        ++_trimCheckId;
        _trimDelay.Stop();
        _trimPending = false;
        _trimBlock = null;
        SetFactors(FactorChoices.None(EditKind.Speed));
        SetFactors(FactorChoices.None(EditKind.Scale));
        SetTrims(TrimChoices.None);
        SetBusy("Checking which edits this clip can take...");
        ShowOptions();
        ShowOutcome();
        (EditContext ctx, SkeletonMirror? mirror, IReadOnlyList<ClipEntry> sources, Motion? motion, string? why, ClipEditChoices choices, bool graphRead, bool swapsRead,
            (List<EventMark>, List<PlantSpan>) marks) r;
        try
        {
            r = await Task.Run(async () =>
            {
                var graph = await InstallWork.Graph(g);
                cancel.ThrowIfCancellationRequested();
                var swaps = await InstallWork.Swaps(g);
                cancel.ThrowIfCancellationRequested();
                // the clip on its own skeleton: the body's mirror only for a clip on the body; a clip whose skeleton isn't known isn't edited
                bool onBody = c.OnSkeleton.Kind == Core.Skeletons.ClipSkeletonKind.Body;
                var mirror = onBody ? await InstallWork.Mirror(g) : null;
                cancel.ThrowIfCancellationRequested();
                var sk = g.SkeletonOf(c);
                var ctx = new EditContext(sk ?? g.Skeleton, g.SharedTable, graph?.Verdict(c.Id));
                // the swap sources of the animation datafile's, and any other datafile's clip of the same length, tracks and kinds
                IReadOnlyList<ClipEntry> sources = sk is null ? [] : [.. (swaps?.For(c) ?? []), .. SwapSources.Among(c, g.OtherClips, g.SharedTable)];
                cancel.ThrowIfCancellationRequested();
                Motion? motion = null;
                string? why = sk is null ? g.ReplaceProblem(c, null) : null;
                if (sk is not null)
                    try { motion = Motion.FromClip(sk, c.Record ?? throw new InvalidDataException(c.ParseError ?? "the record can't be read"), g.SharedTable); }
                    catch (InvalidDataException ex) { why = ex.Message; }
                // the clip chosen is tried at every step of each slider and every cut of the trim's grid, on all the processor's cores
                var choices = EditJobs.Choices(c, ctx, mirror, sources.Count, cancel, parallelism: null, full: true);
                // the timeline's marks too: nothing of the clip is worked out on the window's thread
                var marks = Marks(c.Record, g);
                return (ctx, mirror, sources, motion, why, choices, graphRead: graph is not null, swapsRead: swaps is not null, marks);
            }, cancel);
        }
        catch (OperationCanceledException) { return; }   // another clip was chosen: its own run shows its result
        catch (Exception ex)
        {
            // anything unforeseen: logged, and the screen comes out of busy with the error in it, never stuck
            App.LogError(ex, $"while checking which edits {c.Name} takes");
            if (id != _runId) return;
            SetBusy(null);
            ShowError($"{c.Name} couldn't be checked for its edits ({ex.GetType().Name}): {ex.Message}");
            ShowOptions();
            ShowOutcome();
            UpdateTimeline();
            return;
        }
        if (id != _runId || cancel.IsCancellationRequested) return;   // another clip (or install) was chosen meanwhile
        SetBusy(null);
        _ctx = r.ctx;
        _mirror = r.mirror;
        _sources = r.sources;
        _original = r.motion;
        // a clip of another skeleton (an animal, a drone) is drawn on its own
        if (r.motion?.Skeleton is { } shownOn && !ReferenceEquals(_scene?.Skeleton, shownOn))
        {
            _scene = new SkeletonScene(shownOn);
            _ghost = new SkeletonScene(shownOn, ghost: true);
            _model = new Transform[shownOn.Bones.Count];
        }
        _options = [.. r.choices.Options];
        // an edit the tool failed on for this clip ("can't tell" under it): its error logged, the clip and the edit named
        foreach (var o in _options.Where(o => o.Error is not null)) App.LogCaught(o.Error!, $"while checking whether {c.Name} takes {EditJobs.Name(o.Kind)}");
        SetFactors(r.choices.Speeds);
        SetFactors(r.choices.Scales);
        SetTrims(r.choices.Trims);
        _clipMarks = r.marks;
        if (r.why is not null) ShowError($"{c.Name} can't be shown or edited: {r.why}");
        OptionsText.Text = (r.graphRead ? "Greyed edits can't be made on this clip; the reason is under each."
            : $"The animation graph couldn't be read{(InstallWork.WhyNoGraph(g) is { } why ? $" ({why})" : "")}, so the edits that depend on it are greyed.")
            + (r.swapsRead ? "" : " The swap sources couldn't be worked out for these game files, so swap has none.");
        _settingControls = true;
        SourceBox.ItemsSource = _sources;
        SourceBox.SelectedIndex = _sources.Count > 0 ? 0 : -1;
        _settingControls = false;
        ShowOptions();
        ShowOutcome();
        ResetCamera();
        if (!_automated && _original is not null) _clock.Play();
        UpdateTimeline();
    }

    // ---- the edits ----

    RadioButton Choice(EditKind k) => k switch
    {
        EditKind.Mirror => MirrorChoice, EditKind.Scale => ScaleChoice, EditKind.Speed => SpeedChoice, EditKind.Trim => TrimChoice, _ => SwapChoice,
    };

    TextBlock ReasonText(EditKind k) => k switch
    {
        EditKind.Mirror => MirrorReason, EditKind.Scale => ScaleReason, EditKind.Speed => SpeedReason, EditKind.Trim => TrimReason, _ => SwapReason,
    };

    TextBlock WarningText(EditKind k) => k switch
    {
        EditKind.Mirror => MirrorWarning, EditKind.Scale => ScaleWarning, EditKind.Speed => SpeedWarning, EditKind.Trim => TrimWarning, _ => SwapWarning,
    };

    EditKind? Chosen => Enum.GetValues<EditKind>().Cast<EditKind?>().FirstOrDefault(k => Choice(k!.Value).IsChecked == true);

    /// <summary>Each edit's choice enabled or greyed with its reason as text under it, and its warning; the chosen one's settings enabled.</summary>
    void ShowOptions()
    {
        bool known = _options.Count > 0 && !_busy;
        foreach (var k in Enum.GetValues<EditKind>())
        {
            var o = _options.FirstOrDefault(x => x.Kind == k);
            var choice = Choice(k);
            choice.IsEnabled = known && o is { Available: true };
            // while busy the choice stays as it is; a choice the clip can't take is cleared
            if (!_busy && !choice.IsEnabled && choice.IsChecked == true) choice.IsChecked = false;
            var reason = ReasonText(k);
            // in a modder's words, the technical detail on hover
            reason.Text = o?.Unavailable ?? "";
            reason.ToolTip = o?.Detail is { } detail ? "The detail: " + EditJobs.Unnamed(_clip?.Name ?? "", detail) : null;
            reason.Visibility = known && o is { Available: false } ? Visibility.Visible : Visibility.Collapsed;
            var warn = WarningText(k);
            warn.Text = o?.Warning is { } w ? "Warning: " + w : "";
            warn.Visibility = known && o?.Warning is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        var kind = Chosen;
        // an edit's own controls (its slider and value, the swap's source) are usable while the edit is available, a move of one choosing
        // the edit; while it isn't, they are greyed whole and the value shows none
        ScalePanel.IsEnabled = Can(EditKind.Scale);
        SpeedPanel.IsEnabled = Can(EditKind.Speed);
        SwapPanel.IsEnabled = Can(EditKind.Swap);
        ShowValues();
        // what works stays said while an Apply runs (the pane never jumps)
        SpeedRangeText.Visibility = Allows(EditKind.Speed) && SpeedRangeText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScaleRangeText.Visibility = Allows(EditKind.Scale) && ScaleRangeText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrimRangeText.Visibility = Allows(EditKind.Trim) && TrimRangeText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrimBorder.IsEnabled = !_busy;
        bool trim = kind == EditKind.Trim;
        TrimHint.Visibility = TrimBorder.Visibility = TrimLabel.Visibility = TrimValue.Visibility = trim ? Visibility.Visible : Visibility.Collapsed;
        if (trim) DrawTrim();
        ShowTrimCheck();
        ShowReplace();
        UpdateButtons();
    }

    /// <summary>For laying the screen out without an install: the edits' panel for these choices, as a clip chosen shows them.</summary>
    internal void ShowChoices(ClipEditChoices choices)
    {
        _options = [.. choices.Options];
        SetFactors(choices.Speeds);
        SetFactors(choices.Scales);
        SetTrims(choices.Trims);
        ShowOptions();
    }

    /// <summary>Whether <paramref name="k"/> can be chosen now: the clip's choices are known, the screen isn't busy, and the edit is available.</summary>
    bool Can(EditKind k) => !_busy && Allows(k);

    /// <summary>Whether the clip allows <paramref name="k"/>, busy or not (the value beside its slider stays while an Apply runs).</summary>
    bool Allows(EditKind k) => _options.Any(o => o.Kind == k && o.Available);

    /// <summary>A choice of edit: its controls, and where the result and the original will stand (the edit's own layout).</summary>
    void OnKindChosen(object sender, RoutedEventArgs e)
    {
        ShowOptions();
        if (_built) ShowLayout();
    }

    /// <summary>The screen sets the edits' controls itself (a new clip's speeds, its sources): no edit is chosen by it.</summary>
    bool _settingControls;

    /// <summary>Every control of the screen is made (the sliders report their first values while they are).</summary>
    readonly bool _built;

    void OnFactorChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;   // while the screen's controls are being made
        // a move of an available edit's slider chooses that edit
        if (!_settingControls && sender == ScaleSlider && Can(EditKind.Scale)) ScaleChoice.IsChecked = true;
        if (!_settingControls && sender == SpeedSlider && Can(EditKind.Speed)) SpeedChoice.IsChecked = true;
        ShowValues();
        UpdateButtons();
    }

    /// <summary>The sliders' values beside them, or a dash for an edit the clip can't take (its slider greyed: no value applies).</summary>
    void ShowValues()
    {
        ScaleValue.Text = Allows(EditKind.Scale) || _options.Count == 0 ? FormattableString.Invariant($"{ScaleSlider.Value:F2}x") : "–";
        SpeedValue.Text = Allows(EditKind.Speed) || _options.Count == 0 ? FormattableString.Invariant($"{SpeedSlider.Value:F2}x") : "–";
    }

    void OnSourceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingControls && SourceBox.SelectedItem is ClipEntry && Can(EditKind.Swap)) SwapChoice.IsChecked = true;
        UpdateButtons();
    }

    // ---- the speeds, the scales and the cuts that work ----

    /// <summary>The factors of the speed and the scale sliders that work on the clip (none tried before a clip, or where the plan
    /// refuses the edit).</summary>
    FactorChoices _speeds = FactorChoices.None(EditKind.Speed), _scales = FactorChoices.None(EditKind.Scale);

    /// <summary>The cuts of the trim's grid that work on the clip.</summary>
    TrimChoices _trims = TrimChoices.None;

    /// <summary>Takes the factors that work on the clip for a slider (speed or scale): it snaps to them and spans them only, its value
    /// the nearest to its usual one (1.25x, 1.5x), with what works said under it in plain words when not every factor does.</summary>
    void SetFactors(FactorChoices s)
    {
        bool speed = s.Kind == EditKind.Speed;
        var (slider, text, min, max, usual) = speed ? (SpeedSlider, SpeedRangeText, EditJobs.SpeedMin, EditJobs.SpeedMax, 1.25)
            : (ScaleSlider, ScaleRangeText, EditJobs.ScaleMin, EditJobs.ScaleMax, 1.5);
        if (speed) _speeds = s; else _scales = s;
        _settingControls = true;
        if (s.Works.Count > 0 && !s.All)
        {
            slider.Minimum = min;
            slider.Maximum = s.Works[^1];
            slider.Minimum = s.Works[0];
            slider.Ticks = new DoubleCollection(s.Works);
            slider.Value = s.Nearest(usual);
        }
        else
        {
            slider.ClearValue(Slider.TicksProperty);
            slider.Minimum = min;
            slider.Maximum = max;
            slider.Value = usual;
        }
        _settingControls = false;
        text.Text = s.Plain() ?? "";
        text.ToolTip = s.Main is { } m && s.Plain() is not null ? "The detail at the others: " + m.Detail : null;
    }

    /// <summary>Takes the cuts that work on the clip: what works said under the trim in plain words when not every cut tried does; where
    /// only the cuts keeping the clip's start work, the start handle stays at the start (and the same for the end).</summary>
    void SetTrims(TrimChoices t)
    {
        _trims = t;
        TrimRangeText.Text = t.Plain() ?? "";
        TrimRangeText.ToolTip = t.Main is { } m && t.Plain() is not null ? "The detail at the other trims: " + m.Detail : null;
        // the handles stay free: the cuts tried are a sample (a later start may work where none tried did), and the exact cut is tried
        // when the handles stop; their tooltips say which kind of cut worked among those tried
        FromThumb.ToolTip = t.StartOnly ? "The first tick kept: of the trims tried, only those keeping the clip's start work" : "The first tick kept";
        ToThumb.ToolTip = t.EndOnly ? "The last tick kept: of the trims tried, only those keeping the clip's end work" : "The last tick kept";
    }

    /// <summary>Why the factor on a slider can't be made, or null: only the factors the check found working are made.</summary>
    static EditBlock? FactorRefused(FactorChoices s, double f) => s.Grid.Count == 0 || s.All || s.Works.Contains(Math.Round(f, 4)) ? null
        : s.Refused.FirstOrDefault(r => Math.Abs(r.Factor - f) < 1e-6).Block ?? s.Main;

    EditBlock? SpeedRefused(double f) => FactorRefused(_speeds, f);

    /// <summary>The cut the trim's handles give, tried in the background once they stop (<see cref="CheckTrim"/>): pending while they
    /// move or the run goes, then its refusal (null: it can be made).</summary>
    readonly DispatcherTimer _trimDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    bool _trimPending;
    EditBlock? _trimBlock;
    int _trimCheckId;

    /// <summary>The handles moved: the cut is tried again once they stop.</summary>
    void TrimMoved()
    {
        _trimPending = true;
        _trimBlock = null;
        _trimDelay.Stop();
        _trimDelay.Start();
        ShowTrimCheck();
    }

    /// <summary>Tries the cut the handles give, in the background: a cut the rebuild refuses greys Apply, its reason under the trim.</summary>
    async void CheckTrim()
    {
        int id = ++_trimCheckId;
        if (_clip is not { } c || _ctx is not { } ctx || !_trim.Cuts) { _trimPending = false; _trimBlock = null; ShowTrimCheck(); UpdateButtons(); return; }
        var req = new EditRequest(EditKind.Trim, 1, _trim.From, _trim.To);
        EditBlock? b;
        try { b = await Task.Run(() => EditJobs.RunBlock(c, req, ctx, null)); }
        catch (Exception ex)
        {
            App.LogError(ex, $"while trying a cut of {c.Name}");
            b = new EditBlock(null, $"{ex.GetType().Name}: {ex.Message}");
        }
        if (id != _trimCheckId || c != _clip) return;
        _trimPending = false;
        _trimBlock = b;
        ShowTrimCheck();
        UpdateButtons();
    }

    void ShowTrimCheck()
    {
        bool shown = Chosen == EditKind.Trim && (_trimPending || _trimBlock is not null);
        TrimCheckText.Text = _trimPending ? "Trying this trim..." : _trimBlock is { } b ? EditReasons.Plain(EditKind.Trim, b) + " Move the handles to another trim." : "";
        TrimCheckText.ToolTip = !_trimPending && _trimBlock is { } d ? "The detail: " + d.Detail : null;
        TrimCheckText.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateButtons()
    {
        var kind = Chosen;
        // a trim with both handles at the clip's ends would change nothing; a cut being tried, or refused, isn't made; a speed only at a
        // factor that works
        ApplyButton.IsEnabled = !_busy && !_selectDelay.IsEnabled && _clip is not null && (ReplaceChosen ? (SetTicked ? SetChosen && SetReady : ReplaceReady)
            : _ctx is not null && kind is not null && (kind != EditKind.Swap || SourceBox.SelectedItem is ClipEntry)
            && (kind != EditKind.Trim || _trim.Cuts && !_trimPending && _trimBlock is null)
            && (kind != EditKind.Speed || SpeedRefused(SpeedSlider.Value) is null)
            && (kind != EditKind.Scale || FactorRefused(_scales, ScaleSlider.Value) is null));
        ApplyKeepButton.IsEnabled = ApplyButton.IsEnabled;
        // a result made but not in the package is said, until it is added or another is made
        NotKeptText.Visibility = !_busy && _outcome is { } made && NotKeepable is null && !InPackage(made) ? Visibility.Visible : Visibility.Collapsed;
        _keepTip ??= KeepButton.ToolTip;
        // a result that changes nothing is greyed with its reason: the game's own record is the game's file, never packaged
        string? same = _busy ? null : NotKeepable;
        KeepButton.IsEnabled = !_busy && _outcome is not null && same is null;
        KeepButton.ToolTip = same is null ? _keepTip : $"Not for the package: {same}.";
        // the edit's own reason already says it changed nothing; the game's own clip is said to be unchanged by the edit only when the
        // install held it so
        KeepReasonText.Text = same is null ? ""
            : $"Not for the package: {same}." + (same == PackageEntries.GamesOwnUnchanged && _outcome is { } o && o.Clip.Bytes.Span.SequenceEqual(o.Result.Bytes)
                ? " This edit changed nothing in the clip's record." : "");
        KeepReasonText.Visibility = same is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Why the result can't be added to the package, or null: no change (<see cref="Unchanged"/>), or a game check failed
    /// (<see cref="PackageEntries.NotForPackage"/>). A look check never stops it.</summary>
    string? NotKeepable => _setOutcomes is not null && _install is { } g ? SetNotKeepable(g)
        : Unchanged ?? (_outcome is { } o ? PackageEntries.NotForPackage(o.Report.Verdict) : null);

    /// <summary>The Add button's own tooltip, shown while it can be used.</summary>
    object? _keepTip;

    /// <summary>Why the result isn't added to the package, or null: it is the game's own clip unchanged, or the clip as the install
    /// already holds it (an edit that changes nothing), so it replaces nothing.</summary>
    string? Unchanged => _outcome is { } o && _install is { } g ? PackageEntries.Unchanged(g, o.Clip, o.Result.Bytes, PackageItemKind.Edit) : null;

    EditRequest? Request()
    {
        return Chosen switch
        {
            EditKind.Mirror => new EditRequest(EditKind.Mirror),
            EditKind.Scale => new EditRequest(EditKind.Scale, ClipChange.RoundFactor(ScaleSlider.Value)),
            EditKind.Speed => new EditRequest(EditKind.Speed, ClipChange.RoundFactor(SpeedSlider.Value)),
            EditKind.Trim => new EditRequest(EditKind.Trim, 1, _trim.From, _trim.To),
            EditKind.Swap when SourceBox.SelectedItem is ClipEntry src => new EditRequest(EditKind.Swap, Source: src),
            _ => null,
        };
    }

    async void OnApply(object sender, RoutedEventArgs e) => await ApplyAsync();

    async void OnApplyAndKeep(object sender, RoutedEventArgs e) => await ApplyAndKeepAsync();

    /// <summary>The one step: the edit made and, when it changes the clip, put in the package. Returns the refusal, the failure or why
    /// it wasn't added, or null.</summary>
    public async Task<string?> ApplyAndKeepAsync()
    {
        var clip = _clip;
        int run = _runId;
        if (await ApplyAsync() is { } error) return error;
        // another clip (or install) chosen while the edit ran: its result was dropped, and nothing is added for the clip now shown
        if (_runId != run + 1 || _clip != clip || _outcome is null) return null;
        if (Keep() is { } notKept)
        {
            StatusText.Text += " " + notKept;
            return notKept;
        }
        return null;
    }

    /// <summary>Whether the package list holds exactly this result for its clip.</summary>
    static bool InPackage(EditOutcome o) => PackageList.Current.Items.Any(i => i.Slot.Id == o.Clip.Id && i.Record.AsSpan().SequenceEqual(o.Result.Bytes));

    /// <summary>Runs the chosen edit in the background and shows its result. Returns the refusal or failure, or null.</summary>
    public async Task<string?> ApplyAsync()
    {
        if (ReplaceChosen) return SetTicked ? await ReplaceSetAsync() : await ReplaceAsync();
        _setOutcomes = null;
        SetResultText.Text = "";
        _loopFrom = null;
        TrackMapText.Text = "";
        if (_clip is null || _ctx is null || Request() is not { } req) return "Choose a clip and an edit first.";
        if (req.Kind == EditKind.Trim && !_trim.Cuts) return "The trim's handles are at the clip's ends: there is nothing to trim.";
        var c = _clip;
        var ctx = _ctx;
        var mirror = _mirror;
        int id = ++_runId;
        _outcome = null;
        HideError();
        StatusText.Text = "";
        SetBusy($"Applying: {c.Name} ({req.Describe()})...");
        ShowOutcome();
        string? error = null, detail = null;
        EditOutcome? o = null;
        // a clip's context comes with its install: one is open
        var g = _install!;
        (List<EventMark>, List<PlantSpan>) marks = ([], []);
        try
        {
            (o, marks) = await Task.Run(() =>
            {
                var made = (ApplyForTests ?? EditJobs.Apply)(c, req, ctx, mirror);
                return (made, Marks(made.Result.Record, g));
            });
        }
        // a refusal in a modder's words, its technical detail on hover (and after it for the command line)
        catch (EditRefusedException ex) { error = "Not made. " + EditReasons.Plain(req.Kind, ex.Kind); detail = EditJobs.Unnamed(c.Name, ex.Message); }
        // a record the edit can't read (its data, not the code): said; any other error is the code's, below, and logged
        catch (InvalidDataException ex) { error = $"The edit failed: {ex.Message}"; }
        catch (Exception ex)
        {
            // never left busy, never shown elsewhere; logged
            App.LogError(ex, $"while making {c.Name} ({req.Describe()})");
            error = $"The edit failed ({ex.GetType().Name}): {ex.Message}";
        }
        string? full = detail is null ? error : $"{error} (the detail: {detail})";
        if (id != _runId) return full;
        SetBusy(null);
        _outcome = o;
        _resultMarks = marks;
        LastError = full;
        if (error is not null) { ShowError(error, detail is null ? null : "The detail: " + detail); StatusText.Text = ""; }
        else StatusText.Text = $"Made: {c.Name} ({req.Describe()}). It plays below with the original as a ghost.";
        ShowOutcome();
        if (o is not null && !_automated) _clock.Play();
        UpdateTimeline();
        return error;
    }

    void OnKeep(object sender, RoutedEventArgs e) => Keep();

    /// <summary>Puts the result in the package list. Returns an error message, or null.</summary>
    public string? Keep()
    {
        if (_outcome is null) return "Nothing applied to add.";
        if (_setOutcomes is not null) return KeepSet();
        if (NotKeepable is { } same) return $"Not added: {_outcome.Clip.Name}: {same}.";
        bool replaced = PackageList.Current.Keep(PackageEntries.ForEdit(_outcome));
        StatusText.Text = $"Added {_outcome.Clip.Name} ({_outcome.Change.Words}) to the package{(replaced ? ", in place of the one added before for this clip" : "")}. "
            + $"The package holds {PackageList.Current.Count}.";
        return null;
    }

    /// <summary>The viewport, the timeline and the checks panel for the result (or, before one, the original alone).</summary>
    void ShowOutcome()
    {
        // a set's preview ends with any change to what is shown
        _preview = null;
        var o = _outcome;
        _sceneHolder.Children.Clear();
        _ghostHolder.Children.Clear();
        if (o is null)
        {
            ReportList.ItemsSource = null;
            NotesList.ItemsSource = null;
            VerdictText.Text = _clip is null ? "Nothing applied yet." : "Nothing applied yet: choose an edit and press Apply.";
            VerdictText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key("Text"));
        }
        else
        {
            // the failures first, then the warnings, then the passes, each in the checks' own order (the sort is stable), so what needs
            // a look is in sight without a scroll
            ReportList.ItemsSource = ReportRows(o.Report);
            NotesList.ItemsSource = o.Result.Notes;
            var v = o.Report.Verdict;
            VerdictText.Text = $"{o.Report.Headline} "
                + FormattableString.Invariant($"{o.Clip.LastTick} ticks before, {o.Result.Record.LastTick} after; {o.Result.Warnings.Count} warning(s) from the animation graph.");
            VerdictText.SetResourceReference(TextBlock.ForegroundProperty, Theme.TextKeyOf(v));
        }
        var shownRecord = o?.Result.Record ?? _clip?.Record;
        // the marks were read in the background with the clip, or with the edit's result
        if (o is not null) (_events, _plants) = _resultMarks;
        else if (_clip?.Record is not null && _original is not null) (_events, _plants) = _clipMarks;
        else { _events = []; _plants = []; }
        if (_original is not null && _scene is not null && _ghost is not null && shownRecord is not null)
        {
            _scene.ShowAddOnFor(shownRecord.Tracks);
            _ghost.ShowAddOnFor(_clip!.Record!.Tracks);
            _sceneHolder.Children.Add(_scene.Visual);
            if (o is not null && GhostBox.IsChecked == true) _ghostHolder.Children.Add(_ghost.Visual);
            var m = o?.Edited ?? _original;
            _clock.Reset(m.LastTick / 60f, m.LastTick);
            if (o is not null) _reference.Reset(o.Original.LastTick / 60f, o.Original.LastTick, o.Warp);
            else _reference.Reset(0, 0);
        }
        else
        {
            _clock.Reset(0, 0);
            _reference.Reset(0, 0);
        }
        ShowLayout();
        ShowPose();
        UpdateTimeline();
        DrawMarks();
        UpdateButtons();
    }

    /// <summary>The timeline's marks of the clip chosen and of the edit's result, read in the background with each.</summary>
    (List<EventMark>, List<PlantSpan>) _clipMarks = ([], []), _resultMarks = ([], []);

    /// <summary>A record's event and plant marks for the timeline; none when they can't be read (the timeline then shows none, and the
    /// screen goes on).</summary>
    static (List<EventMark>, List<PlantSpan>) Marks(GrbAnimTool.Core.Clips.ClipRecord? rec, GameInstall g)
    {
        if (rec is null) return ([], []);
        try { return (ClipMarks.Events(rec), ClipMarks.Plants(rec, g.SharedTable)); }
        catch (Exception ex)
        {
            App.LogError(ex, "while reading a clip's events and foot plants");
            return ([], []);
        }
    }

    void SetBusy(string? text)
    {
        _busy = text is not null;
        BusyPanel.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? "";
        ShowOptions();
    }

    // ---- the trim's handles ----

    void DrawTrim()
    {
        double w = TrimCanvas.ActualWidth;
        double x0 = TrimHandles.XOf(_trim.From, w, _trim.LastTick), x1 = TrimHandles.XOf(_trim.To, w, _trim.LastTick);
        Canvas.SetLeft(FromThumb, x0 - FromThumb.Width / 2);
        Canvas.SetLeft(ToThumb, x1 - ToThumb.Width / 2);
        Canvas.SetLeft(TrimKept, x0);
        TrimKept.Width = Math.Max(0, x1 - x0);
        TrimValue.Text = _trim.Describe();
    }

    void OnTrimSized(object sender, SizeChangedEventArgs e) => DrawTrim();

    void OnFromDragged(object sender, DragDeltaEventArgs e)
    {
        double x = Canvas.GetLeft(FromThumb) + FromThumb.Width / 2 + e.HorizontalChange;
        _trim = _trim.DragFrom(x, TrimCanvas.ActualWidth);
        DrawTrim();
        TrimMoved();
        UpdateButtons();
    }

    void OnToDragged(object sender, DragDeltaEventArgs e)
    {
        double x = Canvas.GetLeft(ToThumb) + ToThumb.Width / 2 + e.HorizontalChange;
        _trim = _trim.DragTo(x, TrimCanvas.ActualWidth);
        DrawTrim();
        TrimMoved();
        UpdateButtons();
    }

    // ---- the viewport and the timeline ----

    void DrawMarks()
    {
        _head = TimelineMarks.Draw(MarkStrip, _clock.Duration, _events, _plants);
        TimelineMarks.MoveHead(_head, MarkStrip, _clock.Time, _clock.Duration);
        MarkText.Text = _original is null ? "" : TimelineMarks.Summary(_events, _plants);
        DrawLoopMark();
    }

    void OnMarkStripSized(object sender, SizeChangedEventArgs e) => DrawMarks();

    void ShowPose()
    {
        Sketch.InvalidateVisual();
        bool shown = _original is not null && _sceneHolder.Children.Count > 0;
        HintText.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = _install is null ? "Open your game folder on the Library screen first."
            : _clip is null ? "Choose a clip to edit: the Library's, or one in the list." : _busy ? "Working..." : "This clip can't be shown.";
        if (!shown) { OverlayText.Text = ""; return; }
        var o = _outcome;
        int t = _clock.Tick;
        bool mover = MoverBox.IsChecked == true;
        var m = _preview?.Chain ?? o?.Edited ?? _original!;
        t = Math.Clamp(t, 0, m.LastTick);
        Draw(_scene!, m, t, mover);
        _scene!.MarkFeet(_preview is null && ClipMarks.Planted(_plants, 0, t), _preview is null && ClipMarks.Planted(_plants, 1, t));
        Follow();
        string text;
        if (_preview is not null)
            text = $"{PreviewLine(t)}\n{_clock.Time:F3} s  tick {t} / {m.LastTick}  mover {(mover ? "on" : "off")}";
        else if (o is not null)
        {
            int ot = _reference.TickAt(t);
            OriginalTickShown = ot;
            // following the edit's progress, the ghost stands in the result's frame (after a trim from a later tick, moved as the result's
            // mover was); in its own time, on its own mover
            if (_ghostHolder.Children.Count > 0)
            {
                if (_reference.FollowsEdit) Draw(_ghost!, o.Original, ot, o.GhostMover(t), mover);
                else Draw(_ghost!, o.Original, ot, mover);
            }
            _ghost!.MarkFeet(false, false, show: false);
            // the clip and its change in the short wording; then the result's time and tick, and the original's: at the edit's progress, or
            // in its own time
            // (the head made once for each result shown, not on every frame)
            if (!ReferenceEquals(_overlayHeadOf, o)) (_overlayHeadOf, _overlayHead) = (o, $"{o.Clip.Name} ({o.Change.Words})");
            text = _reference.FollowsEdit
                ? $"{_overlayHead}\n{_clock.Time:F3} s  tick {t} / {m.LastTick}  (the original's tick {ot}, following the edit's progress)  mover {(mover ? "on" : "off")}"
                : $"{_overlayHead}\n{_clock.Time:F3} s  tick {t} / {m.LastTick}  (the original in its own time: {_reference.Time:F3} s  tick {ot} / {_reference.LastTick})  mover {(mover ? "on" : "off")}";
        }
        else text = $"{_clip!.Name} (no edit applied)\n{_clock.Time:F3} s  tick {t} / {m.LastTick}  mover {(mover ? "on" : "off")}";
        if (_scene.FeetBelowFloor > 0) text += $"\nbelow the floor: {_scene.FeetBelowFloor} foot joint(s) more than {ClipMarks.BelowFloorCm:F0} cm down";
        OverlayText.Text = text;
    }

    /// <summary>The overlay's first line, the clip and its change in the tool's one wording, and the result it was made for.</summary>
    object? _overlayHeadOf;
    string _overlayHead = "";

    /// <summary>The original's tick last drawn beside the result (for checking its timing).</summary>
    internal int OriginalTickShown { get; private set; }

    /// <summary>The original's own time, and the result's clock (for checking their timing).</summary>
    internal ReferenceClock Reference => _reference;
    internal PlaybackClock Clock => _clock;

    /// <summary>The point the camera looks at (for checking Follow).</summary>
    internal Point3D CameraTarget => _camera.Target;

    /// <summary>The result's hips as last drawn, in the view's world (its side's offset included).</summary>
    internal NVector3? ResultCentre => _scene is { } s && _sceneHolder.Children.Count > 0 ? s.Centre + new NVector3((float)Compare.Offset(ShownLayout, false), 0, 0) : null;

    /// <summary>Where the camera last followed the result to (its hips on the floor), or null right after the camera was reset.</summary>
    NVector3? _followAnchor;

    /// <summary>With Follow on, moves the camera by as much as the result's hips moved on the floor since the last frame, so a travelling
    /// clip and its ghost stay in view.</summary>
    void Follow()
    {
        if (_scene is null) return;
        var at = _scene.Centre with { Z = 0 };
        if (FollowBox.IsChecked == true && _followAnchor is { } was && (at - was).LengthSquared() > 1e-10f)
            _camera.Move(new Vector3D(at.X - was.X, at.Y - was.Y, 0));
        _followAnchor = at;
    }

    void OnFollowToggled(object sender, RoutedEventArgs e) => _followAnchor = null;

    void Draw(SkeletonScene scene, Motion m, int t, bool mover) => Draw(scene, m, t, m.Mover(t), mover);

    void Draw(SkeletonScene scene, Motion m, int t, Transform moverAt, bool mover)
    {
        for (int b = 0; b < _model.Length; b++) _model[b] = m.Model(t, b);
        scene.Update(_model, moverAt, mover);
    }

    void UpdateTimeline()
    {
        _syncingSlider = true;
        TimeSlider.Maximum = Math.Max(_clock.Duration, 1e-3);
        TimeSlider.Value = _clock.Time;
        _syncingSlider = false;
        TimeText.Text = $"{_clock.Time:F3} s  tick {_clock.Tick} / {_clock.LastTick}";
        TimelineMarks.MoveHead(_head, MarkStrip, _clock.Time, _clock.Duration);
        PlayButton.Content = _clock.Playing ? "❚❚ Pause" : "▶ Play";
        PlayButton.IsEnabled = StepBackButton.IsEnabled = StepForwardButton.IsEnabled = TimeSlider.IsEnabled = _original is not null;
        SyncRendering();
    }

    void SyncRendering(bool stop = false)
    {
        bool want = !stop && IsLoaded && _clock.Playing && _original is not null;
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

    /// <summary>One frame: <paramref name="realSeconds"/> of real time gone by on the result's clock and the original's (which moves only
    /// while the result plays).</summary>
    internal void Frame(double realSeconds)
    {
        if (_reference.Advance(_clock, realSeconds)) { ShowPose(); UpdateTimeline(); }
        else if (!_clock.Playing) UpdateTimeline();
    }

    void OnScrub(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || _original is null) return;
        // the timeline is the result's; the original goes to the same time from its start (modulo its own length)
        _reference.Seek(_clock, (float)e.NewValue);
        ShowPose();
        UpdateTimeline();
    }

    void OnPlayPause(object sender, RoutedEventArgs e) { if (_original is null) return; _reference.Toggle(_clock); UpdateTimeline(); }
    void OnStepBack(object sender, RoutedEventArgs e) => Step(-1);
    void OnStepForward(object sender, RoutedEventArgs e) => Step(1);

    void Step(int ticks)
    {
        if (_original is null) return;
        _reference.Step(_clock, ticks);
        ShowPose();
        UpdateTimeline();
    }

    void OnLoopToggled(object sender, RoutedEventArgs e) => _clock.Loop = LoopBox.IsChecked == true;

    /// <summary>"Original follows the edit's progress" clicked: the choice for both screens, remembered.</summary>
    void OnFollowsEditToggled(object sender, RoutedEventArgs e)
    {
        if (PreviewChoices.Set(FollowsEditBox.IsChecked == true, remember: true, _install is { } g ? Core.Export.ClipExport.ProtectedFolders(g) : []) is { } error)
            ShowError(error);
        OnFollowsEditChanged();
    }

    /// <summary>The timing choice as it stands, shown: the box, the original's place and the legend.</summary>
    void OnFollowsEditChanged()
    {
        bool on = PreviewChoices.OriginalFollowsEdit;
        FollowsEditBox.IsChecked = on;
        if (_reference.FollowsEdit == on) return;
        _reference.FollowsEdit = on;
        ShowLayout();
        ShowPose();
    }

    void OnViewToggled(object sender, RoutedEventArgs e)
    {
        _ghostHolder.Children.Clear();
        if (_outcome is not null && _ghost is not null && GhostBox.IsChecked == true) _ghostHolder.Children.Add(_ghost.Visual);
        ShowLayout();
        ShowPose();
    }

    // ---- overlay or side by side ----

    /// <summary>The layout chosen in the box while the program runs (null until one is: each edit's own default then).</summary>
    CompareLayout? _layoutChosen;

    /// <summary>The layout the skeletons last stood in.</summary>
    CompareLayout _drawnLayout = CompareLayout.Overlay;

    /// <summary>The layout an edit starts in: side by side for speed, trim, swap and mirror, whose timing or whole motion differs from
    /// the original's; overlaid for root-motion scale, to see exactly how far the travel changed.</summary>
    public static CompareLayout DefaultLayout(EditKind? k) => k == EditKind.Scale ? CompareLayout.Overlay : CompareLayout.SideBySide;

    /// <summary>The layout of the result and the original: the one chosen, else the edit's default.</summary>
    internal CompareLayout Layout => _layoutChosen ?? DefaultLayout(_outcome?.Request.Kind ?? Chosen);

    /// <summary>The layout as drawn: side by side only while the ghost is shown (the result alone stands in the middle).</summary>
    CompareLayout ShownLayout => _ghostHolder.Children.Count > 0 ? Layout : CompareLayout.Overlay;

    void OnLayoutChosen(object sender, SelectionChangedEventArgs e)
    {
        if (!_built || _settingControls || LayoutBox.SelectedIndex < 0) return;
        _layoutChosen = (CompareLayout)LayoutBox.SelectedIndex;
        ShowLayout();
        ResetCamera();
        ShowPose();
    }

    /// <summary>The box shows the layout, the two skeletons stand where it puts them, and the legend says which is which.</summary>
    void ShowLayout()
    {
        _settingControls = true;
        LayoutBox.SelectedIndex = (int)Layout;
        _settingControls = false;
        var shown = ShownLayout;
        // a change of where the two stand brings the camera back to hold them
        if (shown != _drawnLayout) { _drawnLayout = shown; ResetCamera(); }
        _ghostHolder.Transform = new TranslateTransform3D(Compare.Offset(shown, true), 0, 0);
        _sceneHolder.Transform = new TranslateTransform3D(Compare.Offset(shown, false), 0, 0);
        string when = _reference.FollowsEdit ? "at the same point of its move" : "in its own time";
        LegendText.Text = _outcome is null ? "Blue: the clip · cyan: a planted foot · red: a foot below the floor"
            : shown == CompareLayout.SideBySide ? $"Right, blue: the result · left, pale: the original {when} · cyan: a planted foot · red: a foot below the floor"
            : $"Blue: the result · pale, behind: the original {when} · cyan: a planted foot · red: a foot below the floor";
    }

    void OnResetCamera(object sender, RoutedEventArgs e) => ResetCamera();

    /// <summary>A three-quarter view from the character's front right, over where the original's hips stand at its first tick (with the
    /// mover on), at the hips' height there (a clip that starts below the floor, as a swim, stays in view).</summary>
    void ResetCamera()
    {
        var at = new NVector3(0, 0, 0.9f);
        bool drawn = _original is not null && _scene is not null && _sceneHolder.Children.Count > 0;
        if (FollowBox.IsChecked == true && drawn)
        {
            // following: on the result where it stands now (between it and the ghost when both are shown), as the Library centres on
            // its clip's current place, so Follow goes on from there
            var layout = ShownLayout;
            var result = _scene!.Centre + new NVector3((float)Compare.Offset(layout, false), 0, 0);
            at = _ghostHolder.Children.Count > 0 && _ghost is not null
                ? (result + _ghost.Centre + new NVector3((float)Compare.Offset(layout, true), 0, 0)) / 2
                : result;
        }
        else if (_original is { } m && m.Bone("Hips") is int hips and >= 0)
        {
            var p = MoverBox.IsChecked == true ? m.World(0, hips).Position : m.Model(0, hips).Position;
            if (float.IsFinite(p.X) && float.IsFinite(p.Y)) at = new NVector3(p.X, p.Y, float.IsFinite(p.Z) ? p.Z : 0.9f);
        }
        if (!float.IsFinite(at.X + at.Y + at.Z)) at = new NVector3(0, 0, 0.9f);
        _camera.Set(new Point3D(at.X, at.Y, at.Z), yaw: 60, pitch: 10, distance: Compare.Distance(ShownLayout));
        _followAnchor = drawn && FollowBox.IsChecked == true ? _scene!.Centre with { Z = 0 } : null;
        Sketch.FloorCentre = new NVector3(MathF.Round(at.X), MathF.Round(at.Y), 0);
        Sketch.InvalidateVisual();
    }

    /// <summary>Space plays or pauses, Left and Right step a tick, unless a text box or a list has the keyboard.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Left or Key.Right)) return;
        for (var d = Keyboard.FocusedElement as DependencyObject; d is not null; d = (d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
        {
            if (d is TextBoxBase or ComboBox or ComboBoxItem or ListView or ListViewItem or ListBox or ListBoxItem or Slider or Thumb) return;
            if (d is ButtonBase && e.Key == Key.Space) return;
        }
        if (e.Key == Key.Space && e.IsRepeat) { e.Handled = true; return; }
        if (e.Key == Key.Space) OnPlayPause(this, e); else Step(e.Key == Key.Left ? -1 : 1);
        e.Handled = true;
    }

    // ---- automation and pictures ----

    /// <summary>The command line's edit, for automated checks: the clip chosen, the edit and its settings set as a person would, Apply,
    /// and with <c>--edit-keep</c> the result added to the package. Returns an error message (a refusal among them), or null; prints the
    /// availability and the report.</summary>
    public async Task<string?> EditForAutomationAsync(ViewerArgs a)
    {
        if (await ChooseClipAsync(a.EditClip!) is { } err) return err;
        foreach (var o in _options)
            Console.WriteLine($"   {EditJobs.Name(o.Kind)}: {(o.Available ? "available" : $"unavailable: {o.Unavailable} (the detail: {EditJobs.Unnamed(_clip!.Name, o.Detail ?? "")})")}{(o.Warning is null ? "" : " (warning: " + o.Warning + ")")}");
        var kind = a.EditKind!.Value;
        var opt = _options.First(o => o.Kind == kind);
        if (!opt.Available) return $"{EditJobs.Name(kind)} is unavailable on {_clip!.Name}. {opt.Unavailable} (the detail: {EditJobs.Unnamed(_clip.Name, opt.Detail ?? "")})";
        Choice(kind).IsChecked = true;
        switch (kind)
        {
            case EditKind.Scale:
                {
                    // a factor the scale check found refused is refused before Apply, as the slider never offers it
                    double f = Math.Round(a.Factor!.Value, 4);
                    if (_scales.Grid.Contains(f) && FactorRefused(_scales, f) is { } no)
                        return $"{ClipChange.Scale(f).Words} isn't offered on {_clip!.Name}. {ScaleRangeText.Text} (the detail: {no.Detail})";
                    ScaleSlider.Value = Math.Clamp(f, ScaleSlider.Minimum, ScaleSlider.Maximum);
                    break;
                }
            case EditKind.Speed:
                {
                    // a factor the speed check found refused is refused before Apply, as the slider never offers it
                    double f = Math.Round(a.Factor!.Value, 4);
                    if (_speeds.Grid.Contains(f) && SpeedRefused(f) is { } no)
                        return $"{ClipChange.Speed(f).Words} isn't offered on {_clip!.Name}. {SpeedRangeText.Text} (the detail: {no.Detail})";
                    SpeedSlider.Value = Math.Clamp(f, SpeedSlider.Minimum, SpeedSlider.Maximum);
                    break;
                }
            case EditKind.Trim: _trim = TrimHandles.At(_clip!.LastTick, a.From!.Value, a.To!.Value); DrawTrim(); break;
            case EditKind.Swap:
                {
                    var src = ClipQuery.Find(_sources, a.Source!);
                    if (src is null) return $"{a.Source} isn't among {_clip!.Name}'s swap sources (the clips of its length with the identical track list): {(_sources.Count == 0 ? "it has none" : string.Join(", ", _sources.Take(8).Select(s => s.Name)))}";
                    SourceBox.SelectedItem = src;
                    break;
                }
        }
        if (await ApplyAsync() is { } e2) return e2;
        var r = _outcome!;
        Console.WriteLine($"edited {r.Clip.Name} ({r.Change.Words}):{CheckResult.Word(r.Report.Verdict)}");
        foreach (var l in r.Report.Lines()) Console.WriteLine("   " + l);
        if (a.EditKeep)
        {
            // a result that changes nothing isn't added: the run fails with the reason, as the screen's greyed button says it
            if (Keep() is { } e3) return e3;
            Console.WriteLine($"kept {r.Clip.Name} for the package ({PackageList.Current.Count} kept)");
        }
        return null;
    }

    /// <summary>Sets up one picture: the time, the mover setting, paused, the camera reset.</summary>
    public string? ShowForShot(ShotSpec shot, ViewerArgs args)
    {
        // no clip chosen (a run that names none): the screen as it stands, its list
        if (_clip is null) { PaneScroll.ScrollToVerticalOffset(0); return null; }
        if (_original is null) return ErrorBox.Visibility == Visibility.Visible ? ErrorText.Text : "No clip on the Edit screen.";
        if ((shot.Mover ?? args.Mover) is bool m) MoverBox.IsChecked = m;
        if (shot.Follows is bool follows) { PreviewChoices.Set(follows, remember: false, []); OnFollowsEditChanged(); }
        // the picture's time on the result's timeline, the original at the same time from its start (as a scrub puts it)
        _reference.Seek(_clock, shot.Time ?? args.Time ?? 0);
        // the pose at the picture's time first, so the camera (with Follow on) centres on where the result stands then
        ShowPose();
        ResetCamera();
        ShowPose();
        UpdateTimeline();
        // in a window too short for the panes, the picture shows the 3D view (the panes scrolled to it)
        PaneScroll.ScrollToVerticalOffset(0);
        return null;
    }

    /// <summary>The 3D view's area, for checking a picture.</summary>
    public FrameworkElement ViewArea => ViewSurface;

    /// <summary>For checking a picture: runs <paramref name="render"/> with the skeletons taken out (and the sketch's), or returns null
    /// when none is shown.</summary>
    public T? WithoutSkeleton<T>(Func<T> render) where T : class
    {
        if (_sceneHolder.Children.Count == 0) return null;
        Viewport.Children.Remove(_sceneHolder);
        Viewport.Children.Remove(_ghostHolder);
        try { return Sketch.Visibility == Visibility.Visible ? Sketch.WithoutContent(render) : render(); }
        finally
        {
            Viewport.Children.Add(_sceneHolder);
            Viewport.Children.Add(_ghostHolder);
        }
    }

    // ---- errors ----

    /// <summary>Shows a message in the screen's error strip, with <paramref name="detail"/> (a refusal's technical detail) on hover.</summary>
    public void ShowError(string message, string? detail = null)
    {
        ErrorText.Text = message;
        ErrorText.ToolTip = detail;
        ErrorBox.Visibility = Visibility.Visible;
    }

    void HideError() => ErrorBox.Visibility = Visibility.Collapsed;

    void OnDismissError(object sender, RoutedEventArgs e) => HideError();
}
