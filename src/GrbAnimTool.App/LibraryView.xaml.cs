using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Oodle;
using GrbAnimTool.Core.Skeletons;
using NVector3 = System.Numerics.Vector3;
using Transform = GrbAnimTool.Core.Skeletons.Transform;

namespace GrbAnimTool.App;

/// <summary>The Library screen: choose the game folder, browse every clip of the install, and play one on the bare skeleton. Once an
/// install is open its clip index is built, or read from its cache, in the background with its progress in the window; the list is in
/// use meanwhile, and its readable names, categories, category filters and the index's search fields come in when the index is ready.</summary>
public partial class LibraryView : UserControl
{
    const string Dash = "-";

    NVector3[]? _pathPoints;

    GameInstall? _install;
    bool _automated;
    bool _loading;

    // the clip index of the install open: null until it is ready; the cache's folder (null: an automated run keeps none)
    ClipIndex? _index;
    CancellationTokenSource? _indexRun;
    Task? _indexTask;
    string? _indexCache;

    // the clip on show
    ClipEntry? _clip;
    PoseSampler? _sampler;
    ClipPose? _pose;
    SkeletonScene? _scene;
    ModelVisual3D? _pathVisual;
    readonly PlaybackClock _clock = new();
    bool _syncingSlider;
    TimeSpan _lastFrame;
    NVector3 _followAnchor;
    readonly OrbitCamera _camera;
    readonly TranslateTransform3D _floorShift = new();

    public LibraryView()
    {
        InitializeComponent();
        ClipPanel.ReadableColumnWidth = ClipListPanel.LibraryReadableWidth;
        ClipPanel.Picked += c => { if (c is not null && c != _clip) ShowPicked(c); };
        // the list with a few rows, and the viewer with its timeline, in a window of any height
        PaneScroll.LeastHeight = () => Math.Max(ClipPanel.LeastHeight, LeastViewerHeight);
        PaneScroll.InView.Add((ViewerPane, () => FitScroll.ViewerLeast(ViewerPane, ViewFrame)));
        _camera = new OrbitCamera(ViewSurface);
        Viewport.Camera = _camera.Camera;
        var floor = SkeletonScene.FloorGrid();
        floor.Transform = _floorShift;
        Viewport.Children.Add(new ModelVisual3D { Content = floor });
        Viewport.Children.Add(new ModelVisual3D { Content = SkeletonScene.OriginAxes() });
        Loaded += (_, _) => SyncRendering();
        Unloaded += (_, _) => SyncRendering(stop: true);
        // Preview: the orbit camera marks the plain mouse-down handled, so a plain handler here would never run.
        ViewSurface.PreviewMouseDown += (_, _) => ViewSurface.Focus();
        Sketch.Camera = _camera.Camera;
        Sketch.Paint = (dc, project) =>
        {
            if (_scene is not null && _sampler is not null) _scene.Sketch(dc, project, NVector3.Zero);
        };
        ResetCamera();
        UpdateTimeline();
    }

    /// <summary>The least height of a screen's viewer: its switches, a 3D view of 200 and its timeline.</summary>
    internal const double LeastViewerHeight = 400;

    // the clip's marks: its events and its feet's plants, on the strip over the timeline and (the plants) in the viewport
    List<EventMark> _events = [];
    List<PlantSpan> _plants = [];
    System.Windows.Shapes.Line? _head;

    /// <summary>The clip on show, or null.</summary>
    public ClipEntry? CurrentClip => _clip;

    /// <summary>The folder the install's caches are kept in (the clip index's and the edit marks'), or null: this run keeps none.</summary>
    internal string? IndexCacheFolder
    {
        get => _indexCache;
        // for tests: a screen given an install without the Library's start
        set => _indexCache = value;
    }

    /// <summary>Raised when another clip is shown (null: none).</summary>
    public event Action<ClipEntry?>? ClipShown;

    void DrawMarks()
    {
        _head = TimelineMarks.Draw(MarkStrip, _clock.Duration, _events, _plants);
        TimelineMarks.MoveHead(_head, MarkStrip, _clock.Time, _clock.Duration);
        MarkText.Text = _clip is null || _sampler is null ? "" : TimelineMarks.Summary(_events, _plants);
    }

    void OnMarkStripSized(object sender, SizeChangedEventArgs e) => DrawMarks();

    // ---- start, the game folder and loading ----

    /// <summary>Starts the screen for a person: the command line's game folder, else the remembered one, else a prompt to choose.</summary>
    public async Task StartAsync(ViewerArgs args)
    {
        _automated = args.Automated;
        // a person's run keeps the index's cache in the user's own folder; an automated one only where its command line says
        _indexCache = args.IndexCache ?? (_automated ? null : ClipIndexCache.DefaultFolder);
        // a person's settings file that isn't settings any more (a hand edit gone wrong): kept aside, said, and the defaults used
        if (!_automated && ViewerSettings.SetAsideIfUnreadable(ViewerSettings.DefaultPath) is { } setAside) ShowError(setAside);
        // one that can't be read now (held by another program): said, rather than the remembered game folder just not opening
        else if (!_automated && args.Game is null && !ViewerSettings.TryLoad(ViewerSettings.DefaultPath, out _, out var unread))
            ShowError($"The settings couldn't be read ({unread}), so the last game folder isn't opened.");
        string? game = args.Game ?? (_automated ? null : ViewerSettings.Load(ViewerSettings.DefaultPath).GameFolder);
        if (game is null) return;
        if (!Directory.Exists(game)) { ShowError($"The last game folder, {game}, isn't there any more. Choose the game folder again."); return; }
        if (await LoadAsync(game, args.Forge)) await ShowAsync(args.Clip, args.Time, args.Mover, play: args.Time is null);
    }

    /// <summary>Loads the install the command line names, for pictures, and for a run that takes pictures waits for the clip index (its
    /// columns, filters and clip info are in them). Returns an error message, or null when loaded.</summary>
    public async Task<string?> LoadForShotsAsync(ViewerArgs args)
    {
        _automated = true;
        _indexCache = args.IndexCache;
        // an off-screen picture can't see the 3D view: the flat sketch stands in for it
        Sketch.Visibility = Visibility.Visible;
        if (!await LoadAsync(args.Game!, args.Forge)) return ErrorText.Text;
        if (!args.TakesShots) return null;
        if (_indexTask is { } t) await t;
        return _index is null ? IndexText.Text : null;
    }

    /// <summary>Sets up one picture: the list's search text and category filters, its clip, time and mover setting, paused, with the
    /// camera reset. Returns an error message, or null.</summary>
    public async Task<string?> ShowForShotAsync(ShotSpec shot, ViewerArgs args)
    {
        HideError();
        if (ClipPanel.SetForShot(shot.Search, shot.Who, shot.What) is { } bad) return bad;
        string? clip = shot.Clip ?? args.Clip;
        // a picture of a search or a filter shows its own list: a clip it hides would clear them, and the picture show something else
        if ((shot.Search ?? shot.Who ?? shot.What) is not null && clip is not null && ClipQuery.Find(_install!.Clips, clip) is { } c && !ClipPanel.Rows.Contains(c))
            return $"{c.Name} isn't in the list the picture's search and filters give";
        string? err = await ShowAsync(clip, shot.Time ?? args.Time, shot.Mover ?? args.Mover, play: false);
        return err ?? (ErrorBox.Visibility == Visibility.Visible ? ErrorText.Text : null);
    }

    /// <summary>The 3D view's area (its surface, the viewport's size), for checking a picture.</summary>
    public FrameworkElement ViewArea => ViewSurface;

    /// <summary>For checking a picture: runs <paramref name="render"/> with the skeleton taken out of the viewport and puts it back, or
    /// returns null when no skeleton is shown.</summary>
    public T? WithoutSkeleton<T>(Func<T> render) where T : class
    {
        if (_scene is null) return null;
        int i = Viewport.Children.IndexOf(_scene.Visual);
        if (i < 0) return null;
        Viewport.Children.RemoveAt(i);
        try { return Sketch.Visibility == Visibility.Visible ? Sketch.WithoutContent(render) : render(); }
        finally { Viewport.Children.Insert(i, _scene.Visual); }
    }

    /// <summary>Selects a clip (by name or hex ID), the mover setting and the time, once the clip is read. Returns an error message, or null.</summary>
    async Task<string?> ShowAsync(string? clip, float? time, bool? mover, bool play)
    {
        if (mover is bool m) { MoverBox.IsChecked = m; DrawPath(); }
        if (clip is null) return null;
        var c = ClipQuery.Find(_install!.Clips, clip);
        if (c is null) { ShowError($"No clip named or with the ID '{clip}' in this game folder."); return ErrorText.Text; }
        if (!ClipPanel.Rows.Contains(c)) ClipPanel.ClearFilters();
        if (c != _clip) await ShowClip(c, play);
        if (c != _clip) return null;   // another clip was picked meanwhile
        ClipPanel.Show(c);
        _clock.Pause();
        _clock.Seek(time ?? 0);
        if (play) _clock.Play();
        ShowPose();
        UpdateTimeline();
        ResetCamera();
        return null;
    }

    /// <summary>A clip chosen on another screen (a line of the Log screen's): selected in the list as a click on its row would select it,
    /// the search and filters cleared when they hide it, so it shows, plays and exports as any clip picked here. Returns false, with the
    /// reason shown, when the install open doesn't hold it (one opened since).</summary>
    public bool ShowFromLog(ClipEntry c)
    {
        if (_install is null || !_install.Clips.Contains(c))
        {
            ShowError($"{c.Name} isn't a clip of the game folder open: the log's lines are being looked up in it; click the line again in a moment.");
            return false;
        }
        HideError();
        if (c == _clip)
        {
            // the clip shown already: its row brought back (the search and filters cleared when they hide it) and selected
            if (!ClipPanel.Rows.Contains(c)) ClipPanel.ClearFilters();
            ClipPanel.Show(c);
            return true;
        }
        return ClipPanel.Select(c) is not null;
    }

    async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the Ghost Recon Breakpoint game folder (the one that holds GRB.exe)" };
        if (_install is not null) dlg.InitialDirectory = _install.GameFolder;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        await LoadAsync(dlg.FolderName, null);
    }

    const string OpenAdvice = "Check that this is the folder that holds GRB.exe; if it is, verify the game files in Steam or Ubisoft Connect, then try again.";

    /// <summary>Opens the install in the background (the window stays live), then shows it. On failure the message shows in the window
    /// and any install already open stays.</summary>
    async Task<bool> LoadAsync(string game, string? forge)
    {
        if (_loading) return false;
        _loading = true;
        UpdateExportButtons();
        ChooseButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;
        BusyText.Text = "Opening the game folder...";
        HideError();
        try
        {
            var g = await Task.Run(() => GameInstall.Open(game, forge));
            // the search's words are split in the background, not on the first key typed (a key typed sooner waits for it)
            _ = Task.Run(() => ClipSearch.For(g.Clips));
            var old = _install;
            _install = g;
            // the old install goes even when showing the new one fails: nothing of it is used after this point
            try { ShowInstall(); }
            finally
            {
                if (old is not null) { old.Dispose(); GC.Collect(); }
            }
            if (!_automated) SaveSettings(game);
            return true;
        }
        catch (OodleException ex)
        {
            ShowError(ex.Message);
        }
        catch (GameInstallException ex)
        {
            // the messages about a missing forge or a locked file end in their own advice, and a folder that isn't there needs none (verifying
            // the game files can't make it appear); the ones about damaged files don't
            ShowError(ex.Message.EndsWith("GRB.exe.") || ex.Message.EndsWith("try again.") || ex.Message.EndsWith("doesn't exist.") ? ex.Message : $"{ex.Message} {OpenAdvice}");
        }
        catch (Exception ex)
        {
            // a fault in the tool, not in the game files: the error log has it
            App.LogError(ex, $"while opening the game folder {game}");
            ShowError($"The game folder {game} couldn't be opened ({ex.GetType().Name}): {ex.Message} The detail is in the error log.");
        }
        finally
        {
            _loading = false;
            ChooseButton.IsEnabled = true;
            BusyPanel.Visibility = Visibility.Collapsed;
            UpdateExportButtons();
        }
        return false;
    }

    void SaveSettings(string game)
    {
        // guarded like every output: never in the game folder the settings name, nor the folder of the forge it read
        // a settings file that can't be read now isn't written over (the other settings in it would be lost)
        if (!ViewerSettings.TryLoad(ViewerSettings.DefaultPath, out var kept, out var why)) { ShowError($"The game folder couldn't be remembered: the settings file couldn't be read ({why})."); return; }
        try { (kept with { GameFolder = game }).Save(ViewerSettings.DefaultPath, _install is { } g ? ClipExport.ProtectedFolders(g) : [game]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { ShowError($"The game folder couldn't be remembered: {ex.Message}" + (why is null ? "" : $" {why}")); return; }
        if (why is not null) ShowError(why);
    }

    /// <summary>Remembers the theme chosen in the settings, guarded as the game folder's save is: never in the install's folders nor the
    /// game folder the settings name.</summary>
    public void SaveTheme(ThemeChoice choice)
    {
        if (!ViewerSettings.TryLoad(ViewerSettings.DefaultPath, out var kept, out var why)) { ShowError($"The theme couldn't be remembered: the settings file couldn't be read ({why})."); return; }
        var s = kept with { Theme = ThemeChoices.Name(choice) };
        var protect = new List<string>();
        if (_install is { } g) protect.AddRange(ClipExport.ProtectedFolders(g));
        if (s.GameFolder is { Length: > 0 } f) protect.Add(f);
        try { s.Save(ViewerSettings.DefaultPath, protect); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { ShowError($"The theme couldn't be remembered: {ex.Message}" + (why is null ? "" : $" {why}")); return; }
        if (why is not null) ShowError(why);
    }

    void ShowInstall()
    {
        var g = _install!;
        // the clip on show, and the clip index, belong to the old install
        _clip = null;
        _sampler = null;
        _pose = null;
        _clock.Reset(0, 0);
        _indexRun?.Cancel();
        _indexRun = null;
        _index = null;
        _indexError = null;
        FolderText.Text = $"Game folder: {g.GameFolder}    (forge: {g.PatchForgePath})";
        int v = g.Clips.Count(c => c.Status == ClipStatus.Vanilla), r = g.Clips.Count(c => c.Status == ClipStatus.Replaced),
            a = g.Clips.Count(c => c.Status == ClipStatus.Added), u = g.Clips.Count(c => c.Status == ClipStatus.Unknown);
        string s = $"{g.Clips.Count:N0} clips  ·  record folder {g.RecordFolderName}  ·  vanilla {v:N0}  ·  replaced {r:N0}  ·  added {a:N0}";
        if (u > 0) s += $"  ·  unknown {u:N0}";
        if (g.UnpackedFolderPath is not null) s += $"  ·  unpacked folder read: {g.Clips.Count(c => c.FromFile):N0} file(s) beside the forge's, {g.Clips.Count(c => c.Modded):N0} modded";
        if (g.UnpackedFolderError is { } ue) s += $"  ·  {ue}";
        if (g.Vanilla is null) s += "  (this copy of the tool has no table of the game's clips)";
        else if (g.VanillaClipsMissing > 0) s += $"  ·  game clips missing {g.VanillaClipsMissing:N0}";
        SummaryText.Text = s;
        SummaryText.ToolTip = s;

        // a new skeleton: new models; the old clip goes
        if (_scene is not null) Viewport.Children.Remove(_scene.Visual);
        _scene = new SkeletonScene(g.Skeleton);
        ShowClip(null, play: false);
        ClipPanel.SetClips(g.Clips);
        ResetCamera();
        ExportText.Text = "";
        UpdateExportButtons();
        // the install the screens show from now on: its background reads start, the last install's stop
        InstallWork.Open(g);
        InstallShown?.Invoke(g, _automated);
        StartIndex(g);
    }

    /// <summary>Raised once an install is open and shown: the other screens take it from here (with whether the run is automated).</summary>
    public event Action<GameInstall, bool>? InstallShown;

    /// <summary>Raised when the clip index of the install open is ready: the other screens' lists take its columns and search fields.</summary>
    public event Action<GameInstall, ClipIndex>? IndexReady;

    /// <summary>Raised when the clip index of the install open couldn't be built, with the reason: the other screens say who plays a
    /// clip isn't known.</summary>
    public event Action<GameInstall, string>? IndexFailed;

    /// <summary>Raised as the clip index of the install open is read, with its progress (0 to 1, as the bar shows it): the Package screen
    /// shows it while Build waits for the index.</summary>
    public event Action<GameInstall, double>? IndexProgress;

    /// <summary>Why the open install's clip index couldn't be built, when it couldn't; null otherwise.</summary>
    string? _indexError;

    // ---- the clip index ----

    /// <summary>Builds the install's clip index, or reads it from its cache, in the background, with its progress over the list. The
    /// list stays in use; when the index is ready its names and categories fill in, its filters open and the search takes its fields.</summary>
    void StartIndex(GameInstall g)
    {
        var run = _indexRun = new CancellationTokenSource();
        IndexBar.Value = 0;
        IndexBusyText.Text = "Clip index: opening...";
        IndexPanel.Visibility = Visibility.Visible;
        IndexText.Text = "Clip index: being read in the background. The list works meanwhile; its readable names and categories fill in when it is ready.";
        IndexText.ToolTip = null;
        // the graph's read reports its share from several threads: shown here on the window's thread, while this run is the current one
        var progress = new Progress<double>(p =>
        {
            if (!ReferenceEquals(run, _indexRun) || _index is not null) return;
            IndexReadProgress(g, p);
        });
        _indexTask = BuildIndexAsync(g, progress, run);
    }

    /// <summary>The clip index of <paramref name="g"/> is read this far (0 to 1): the Library's bar shows it, and the other screens
    /// (<see cref="IndexProgress"/>) beside what waits for the index.</summary>
    internal void IndexReadProgress(GameInstall g, double p)
    {
        ShowIndexProgress(p);
        IndexProgress?.Invoke(g, IndexBar.Value);
    }

    /// <summary>The index's bar and its line: the graph's read up to <see cref="ClipIndexCache.GraphReadShare"/>, then the rules
    /// applied and the search with the index's fields built, then the whole.</summary>
    void ShowIndexProgress(double p)
    {
        IndexBar.Value = Math.Max(IndexBar.Value, p);
        IndexBusyText.Text = "Clip index: " + IndexStage(IndexBar.Value);
    }

    /// <summary>Where the index's read stands, in words with its share: "reading the game's graph, 40%".</summary>
    public static string IndexStage(double p)
    {
        string stage = p < ClipIndexCache.GraphReadShare ? "reading the game's graph" : p < 1 ? "applying the rules" : "preparing the search";
        return FormattableString.Invariant($"{stage}, {p * 100:F0}%");
    }

    async Task BuildIndexAsync(GameInstall g, IProgress<double> progress, CancellationTokenSource run)
    {
        string? folder = _indexCache;
        try
        {
            var (index, outcome, time, saveError) = await Task.Run(() =>
            {
                var r = OpenIndex(g, folder, progress, run.Token);
                // the list's search with the index's fields is built here, so the first key typed after it doesn't wait for it
                ClipSearch.For(g.Clips, r.Index.SearchSource);
                return r;
            });
            if (!ReferenceEquals(run, _indexRun) || !ReferenceEquals(g, _install)) return;
            string status = ClipIndexView.Status(index, outcome, time, saveError);
            IndexBuilt(g, index, status, folder is null ? status : $"{status}\nThe cache: {new ClipIndexCache(folder).FileFor(g)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.LogError(ex, "while building the clip index");
            if (ReferenceEquals(run, _indexRun)) IndexBuildFailed(g, ex);
        }
        finally
        {
            if (ReferenceEquals(run, _indexRun)) IndexPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The clip index of <paramref name="g"/> is ready: a wait on the index ends with it, the Library's list takes its names,
    /// filters and search fields, and the other screens (<see cref="IndexReady"/>) say who plays each clip.</summary>
    internal void IndexBuilt(GameInstall g, ClipIndex index, string status, string statusTip)
    {
        // the bar ends whole before it goes
        ShowIndexProgress(1);
        _index = index;
        InstallWork.IndexDone(g, index);
        IndexText.Text = status;
        IndexText.ToolTip = statusTip;
        ClipPanel.SetIndex(index);
        ShowIndexInfo(_clip);
        IndexReady?.Invoke(g, index);
    }

    /// <summary>The clip index of <paramref name="g"/> couldn't be built: the Library says why, a wait on the index ends with none, and the
    /// other screens (<see cref="IndexFailed"/>) say who plays a clip isn't known, and why, instead of waiting for an index that won't come.</summary>
    internal void IndexBuildFailed(GameInstall g, Exception ex)
    {
        IndexText.Text = $"The clip index couldn't be built ({ex.GetType().Name}): {ex.Message} The list works without its readable names and categories.";
        _indexError = $"{ex.GetType().Name}: {ex.Message}";
        InstallWork.IndexDone(g, null);
        ShowIndexInfo(_clip);
        IndexFailed?.Invoke(g, _indexError);
    }

    /// <summary>The install's index through the cache in <paramref name="folder"/>; with none (an automated run that names none), read
    /// from the graph for this run alone and kept nowhere. The outcome is null then.</summary>
    static (ClipIndex Index, CacheOutcome? Outcome, TimeSpan Time, string? SaveError) OpenIndex(GameInstall g, string? folder, IProgress<double> progress,
        CancellationToken cancel)
    {
        if (folder is not null)
        {
            var r = new ClipIndexCache(folder).Open(g, progress, cancel);
            return (r.Index, r.Outcome, r.GraphTime, r.SaveError);
        }
        var sw = Stopwatch.StartNew();
        var facts = GraphFacts.Read(g.Records, g.ForgeClips.Select(c => c.Id),ClipIndexCache.GraphShare(progress), cancel);
        var time = sw.Elapsed;
        var index = ClipIndex.Build(g, facts, ClipIndexList.Embedded(), IndexFingerprint.Of(g));
        progress.Report(1.0);
        return (index, null, time, null);
    }

    /// <summary>The clip info's index rows: the readable name, who and what with each category's source (the reason on hover), and the
    /// graph's documents naming the clip.</summary>
    void ShowIndexInfo(ClipEntry? c)
    {
        InfoWho.Children.Clear();
        InfoWhat.Children.Clear();
        var e = c is null ? null : _index?.Find(c.Id);
        if (e is null)
        {
            string none = c is null ? Dash : _index is not null ? "(not in the clip index)"
                : _indexError is null ? "(the clip index isn't ready yet)" : "(the clip index couldn't be built)";
            InfoReadable.Text = InfoUsedBy.Text = InfoPlayedBy.Text = none;
            Line(InfoWho, none, null);
            Line(InfoWhat, none, null);
            return;
        }
        InfoReadable.Text = e.ReadableName ?? Dash;
        InfoPlayedBy.Text = ClipIndexView.PlayedBy(e);
        foreach (var l in ClipIndexView.CategoryLines(e, CategoryKind.Who)) Line(InfoWho, Wrappable(l.Text), l.Reason);
        foreach (var l in ClipIndexView.CategoryLines(e, CategoryKind.What)) Line(InfoWhat, l.Text, l.Reason);
        InfoUsedBy.Text = Wrappable(string.Join("\n", ClipIndexView.DocumentLines(e)));

        // the graph's document names hold hyphens and underscores ("SamFisher_Fullbody-StateMachine"): their hyphens non-breaking and a
        // break allowed after each underscore, so a long name wraps at a word of its own, never inside one or at "-StateMachine"
        static string Wrappable(string s) => s.Replace('-', '‑').Replace("_", "_​");
        static void Line(Panel p, string text, string? reason) => p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, ToolTip = reason });
    }

    // ---- the clip on show ----

    /// <summary>The clip a row picked shows (<see cref="ShowClip"/>), any error on the way said on the screen.</summary>
    async void ShowPicked(ClipEntry c)
    {
        try { await ShowClip(c, play: !_automated); }
        catch (Exception ex)
        {
            App.LogError(ex, $"while showing {c.Name} on the Library screen");
            ShowError($"{c.Name} couldn't be shown ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Whether the clip on show is read and plays (for tests and timings).</summary>
    internal bool ClipPlays => _clip is not null && _sampler is not null;

    /// <summary>The clip being read for showing: a read finished after another clip was picked is dropped.</summary>
    int _showRun;

    /// <summary>Shows a clip (or, with null, none): its name and plain facts at once, then, once read in the background (never on the
    /// window's thread), its sampler, its track counts, its mover's path and its marks, with the play head at 0. A clip the viewer can't
    /// play (see <see cref="ClipFacts.WhyNotShown"/>) shows its facts and the reason, and nothing in the viewport.</summary>
    Task ShowClip(ClipEntry? c, bool play)
    {
        int run = ++_showRun;
        HideError();
        _clip = c;
        _sampler = null;
        _pose = null;
        _clock.Reset(0, 0);
        SetPath(null);
        _events = [];
        _plants = [];
        InfoNote.Text = "";
        ShowIndexInfo(c);
        if (c is null || _install is not { } g)
        {
            foreach (var t in new[] { InfoName, InfoIndex, InfoDuration, InfoTicks, InfoTracks, InfoRot, InfoPos, InfoOther, InfoMover, InfoStatus }) t.Text = Dash;
            InfoId.Text = Dash;
            Shown();
            return Task.CompletedTask;
        }
        InfoName.Text = c.Name;
        InfoId.Text = $"0x{c.Id:X16}";
        InfoIndex.Text = c.Index.ToString();
        InfoDuration.Text = $"{c.Duration:F3} s";
        InfoTicks.Text = $"{c.LastTick} (frames 0 to {c.LastTick})";
        InfoTracks.Text = c.TrackCount.ToString();
        InfoStatus.Text = c.StatusText + (c.FilePath is { } file ? $"  ·  file {System.IO.Path.GetFileName(file)}" : "") + (c.Note is { } note ? $"  ·  {note}" : "");
        InfoStatus.ToolTip = c.FilePath is null && c.Note is null ? null : InfoStatus.Text;
        foreach (var t in new[] { InfoRot, InfoPos, InfoOther, InfoMover }) t.Text = "…";
        Shown();
        return Read();

        async Task Read()
        {
            (ClipFacts Facts, PoseSampler Sampler, NVector3[]? Path, List<EventMark> Events, List<PlantSpan> Plants)? r = null;
            Exception? failed = null;
            try
            {
                r = await Task.Run(() =>
                {
                    if (ClipFacts.WhyNotShown(c) is { } why) throw new InvalidDataException(why);
                    var record = c.Record!;
                    return (ClipFacts.Of(record, g.SharedTable), g.CreateSampler(c), MoverPath.Of(record, g.SharedTable), ClipMarks.Events(record),
                        ClipMarks.Plants(record, g.SharedTable));
                });
            }
            catch (Exception ex)
            {
                // a clip the viewer can't play says so (WhyNotShown's InvalidDataException); anything else is logged too
                App.LogError(ex, $"while reading {c.Name} for the Library screen");
                failed = ex;
            }
            // another clip (or install) was picked meanwhile: its own read shows it
            if (run != _showRun || !ReferenceEquals(g, _install)) return;
            if (r is { } x)
            {
                InfoRot.Text = x.Facts.Rotations.ToString();
                InfoPos.Text = x.Facts.Positions.ToString();
                InfoOther.Text = x.Facts.Others.ToString();
                InfoMover.Text = x.Facts.HasMover ? "yes" : "no";
                _sampler = x.Sampler;
                _pose = _sampler.NewPose();
                _scene?.ShowAddOnFor(c.Record!.Tracks);
                _clock.Reset(c.Duration, c.LastTick);
                SetPath(x.Path);
                _events = x.Events;
                _plants = x.Plants;
                if (play) _clock.Play();
            }
            else
            {
                foreach (var t in new[] { InfoRot, InfoPos, InfoOther, InfoMover }) t.Text = Dash;
                InfoNote.Text = $"This clip can't be shown: {failed!.Message}";
                ShowError($"{c.Name} can't be shown: {failed.Message}");
            }
            Shown();
        }

        void Shown()
        {
            ShowPose();
            UpdateTimeline();
            DrawMarks();
            UpdateExportButtons();
            ClipShown?.Invoke(_clip);
        }
    }

    void SetPath(NVector3[]? points)
    {
        _pathPoints = points;
        DrawPath();
    }

    /// <summary>The mover's path on the floor, shown while the mover is on.</summary>
    void DrawPath()
    {
        if (_pathVisual is not null) { Viewport.Children.Remove(_pathVisual); _pathVisual = null; }
        if (_pathPoints is null || MoverBox.IsChecked != true) return;
        var m = SkeletonScene.PathModel(_pathPoints);
        if (m is null) return;
        _pathVisual = new ModelVisual3D { Content = m };
        Viewport.Children.Add(_pathVisual);
    }

    /// <summary>Samples the clip at the play head and draws it. With no clip on show the skeleton is hidden: the skeleton record's rest
    /// pose has the hips at the floor's origin (the legs through the floor) and faces +x; the clips supply the hips' height and the facing,
    /// so the rest pose alone would only mislead. A sampling error stops playback and shows in the window; bones the pose can't place are
    /// left out with a warning over the viewport.</summary>
    void ShowPose()
    {
        if (_scene is null) return;
        bool mover = MoverBox.IsChecked == true;
        try
        {
            if (_sampler is not null && _pose is not null)
            {
                _sampler.Sample(_clock.Time, _pose);
                _scene.Update(_pose.Model, _pose.Mover, mover);
                int tick = _clock.Tick;
                _scene.MarkFeet(ClipMarks.Planted(_plants, 0, tick), ClipMarks.Planted(_plants, 1, tick));
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex, $"while sampling {_clip?.Name} at {_clock.Time:F3} s");
            _clock.Pause();
            _sampler = null;
            ShowError($"{_clip?.Name} couldn't be sampled at {_clock.Time:F3} s: {ex.Message}");
        }
        bool shown = _sampler is not null;
        if (shown != Viewport.Children.Contains(_scene.Visual))
        {
            if (shown) Viewport.Children.Add(_scene.Visual); else Viewport.Children.Remove(_scene.Visual);
        }
        HintText.Text = _install is null ? "Choose your game folder to begin."
            : _clip is null ? "Select a clip in the list." : "This clip can't be shown.";
        HintText.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        Sketch.InvalidateVisual();
        if (!shown) { OverlayText.Text = ""; return; }
        Follow();
        string text = $"{_clip!.Name}\n{_clock.Time:F3} s  tick {_clock.Tick} / {_clock.LastTick}  mover {(mover ? "on" : "off")}";
        int t0 = _clock.Tick;
        bool l = ClipMarks.Planted(_plants, 0, t0), r = ClipMarks.Planted(_plants, 1, t0);
        if (l || r) text += $"\nplanted: {(l && r ? "both feet" : l ? "left foot" : "right foot")}";
        if (_scene.FeetBelowFloor > 0) text += $"\nbelow the floor: {_scene.FeetBelowFloor} foot joint(s) more than {ClipMarks.BelowFloorCm:F0} cm down";
        if (_scene.NotDrawn > 0)
            text += $"\nWarning: {_scene.NotDrawn} bone(s) not drawn: the clip's keys give them no valid place at this time (damaged data).";
        OverlayText.Text = text;
    }

    bool _rendering;

    /// <summary>Runs the per-frame handler only while a clip plays, so a paused or empty viewer costs nothing per frame.</summary>
    void SyncRendering(bool stop = false)
    {
        bool want = !stop && IsLoaded && _clock.Playing && _sampler is not null;
        if (want == _rendering) return;
        if (want)
        {
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
        else CompositionTarget.Rendering -= OnRendering;
        _rendering = want;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 0 : Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;
        if (_clock.Advance(dt)) { ShowPose(); UpdateTimeline(); }
        else if (!_clock.Playing) UpdateTimeline();
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
        bool has = _sampler is not null;
        PlayButton.IsEnabled = StepBackButton.IsEnabled = StepForwardButton.IsEnabled = TimeSlider.IsEnabled = has;
        SyncRendering();
    }

    void OnScrub(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || _sampler is null) return;
        _clock.Pause();
        _clock.Seek((float)e.NewValue);
        ShowPose();
        UpdateTimeline();
    }

    void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();
    /// <summary>A click on the timeline's control row (its buttons, slider, check box and the gaps between them) hands the keyboard focus
    /// to the viewport, so Space and the arrows then drive playback. The speed box keeps its own while it is used.</summary>
    void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null;
             d = (d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
            if (d is ComboBox) return;
        ViewSurface.Focus();
    }

    void OnStepBack(object sender, RoutedEventArgs e) => Step(-1);
    void OnStepForward(object sender, RoutedEventArgs e) => Step(1);

    void TogglePlay()
    {
        if (_sampler is null) return;
        _clock.Toggle();
        UpdateTimeline();
    }

    void Step(int ticks)
    {
        if (_sampler is null) return;
        _clock.Step(ticks);
        ShowPose();
        UpdateTimeline();
    }

    void OnLoopToggled(object sender, RoutedEventArgs e) => _clock.Loop = LoopBox.IsChecked == true;

    void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedIndex >= 0) _clock.Speed = PlaybackClock.Speeds[SpeedBox.SelectedIndex];
    }

    void OnMoverToggled(object sender, RoutedEventArgs e)
    {
        DrawPath();
        ShowPose();
    }

    /// <summary>Space plays or pauses, Left and Right step a tick, while the focus is on the viewport, the timeline or nowhere in
    /// particular. A text box, a combo box and the clip list keep these keys for their own editing and navigation, and a focused button
    /// or check box keeps Space to press it.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Left or Key.Right)) return;
        for (var d = Keyboard.FocusedElement as DependencyObject; d is not null; d = Parent(d))
        {
            if (d is TextBoxBase or ComboBox or ComboBoxItem or ListView or ListViewItem) return;
            if (d is ButtonBase && e.Key == Key.Space) return;
        }
        if (e.Key == Key.Space && e.IsRepeat) { e.Handled = true; return; }   // a held Space toggles once, not on every repeat
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
        }
        e.Handled = true;

        // the visual parent, else the logical one (a combo box's open list sits in a popup outside the window's visual tree)
        static DependencyObject? Parent(DependencyObject d) =>
            (d is Visual or Visual3D ?VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);
    }

    // ---- the camera ----

    void OnResetCamera(object sender, RoutedEventArgs e) => ResetCamera();

    void OnFollowToggled(object sender, RoutedEventArgs e) => _followAnchor = Target();

    /// <summary>The point the camera looks at: over the character's hips, at a fixed height so the view doesn't bob.</summary>
    NVector3 Target()
    {
        var c = _scene?.Centre ?? new NVector3(0, 0, 0.95f);
        return new NVector3(c.X, c.Y, 0.9f);
    }

    /// <summary>A three-quarter view from the character's front right (the character faces +y), about four metres off.</summary>
    void ResetCamera()
    {
        var t = Target();
        _camera.Set(new Point3D(t.X, t.Y, t.Z), yaw: 60, pitch: 10, distance: 3.8);
        _followAnchor = t;
        MoveFloor(t);
    }

    /// <summary>With follow on, moves the camera by as much as the character moved since the last frame.</summary>
    void Follow()
    {
        if (FollowBox.IsChecked != true) return;
        var t = Target();
        var d = t - _followAnchor;
        if (d.LengthSquared() < 1e-10f) return;
        _camera.Move(new Vector3D(d.X, d.Y, d.Z));
        _followAnchor = t;
        MoveFloor(t);
    }

    /// <summary>Keeps the floor grid under the character, moved by whole metres so its lines stay put on the floor.</summary>
    void MoveFloor(NVector3 t)
    {
        double x = Math.Round(t.X), y = Math.Round(t.Y);
        if (x != _floorShift.OffsetX) _floorShift.OffsetX = x;
        if (y != _floorShift.OffsetY) _floorShift.OffsetY = y;
        Sketch.FloorCentre = new NVector3((float)x, (float)y, 0);
    }

    // ---- export ----

    bool _exporting;
    string? _exportFolder;

    /// <summary>The skeleton can be exported once an install is open; a clip while one the viewer plays is shown.</summary>
    void UpdateExportButtons()
    {
        SkeletonExportButton.IsEnabled = _install is not null && !_exporting && !_loading;
        ClipExportButton.IsEnabled = _install is not null && _clip is not null && _sampler is not null && !_exporting && !_loading;
    }

    string? SavePath(string title, string fileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = fileName,
            DefaultExt = ".glb",
            Filter = "glTF binary (*.glb)|*.glb",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = _exportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return null;
        _exportFolder = Path.GetDirectoryName(dlg.FileName);
        return dlg.FileName;
    }

    async void OnExportSkeleton(object sender, RoutedEventArgs e)
    {
        if (_install is null) return;
        if (SavePath("Save the skeleton as glTF binary", ClipExport.SkeletonFileName) is { } path) await ExportSkeletonAsync(path);
    }

    async void OnExportClip(object sender, RoutedEventArgs e)
    {
        if (_install is null || _clip is null || _sampler is null) return;
        if (SavePath($"Save {_clip.Name} as glTF binary", ClipExport.FileNameFor(_clip.Name)) is { } path) await ExportClipAsync(path);
    }

    /// <summary>Exports the skeleton to <paramref name="path"/> in the background and says how it went in the window. Returns an error
    /// message, or null.</summary>
    public Task<string?> ExportSkeletonAsync(string path)
    {
        var g = _install!;
        return RunExport(() => ClipExport.Skeleton(g, path), r =>
            $"Saved the skeleton ({g.Skeleton.Bones.Count} bones, rest pose) to {r.GlbPath}. Read back: {Numbers(r.Check)}.");
    }

    /// <summary>Exports the clip shown, with its sidecar, to <paramref name="path"/> in the background and says how it went in the window.
    /// Returns an error message, or null.</summary>
    public Task<string?> ExportClipAsync(string path)
    {
        var g = _install!;
        var c = _clip!;
        return RunExport(() => ClipExport.Clip(g, c, path), r =>
            $"Saved {c.Name} ({c.LastTick + 1} frames at 60 a second) to {r.GlbPath}, with {Path.GetFileName(r.SidecarPath)} beside it. "
            + $"Read back against the clip at every tick: {Numbers(r.Check)}. Frame n is the game's tick n at 60 frames a second.");
    }

    static string Numbers(RoundTripResult r) => FormattableString.Invariant($"worst {r.WorstCm:F4} cm, {r.WorstDeg:F4} deg");

    async Task<string?> RunExport(Func<ExportResult> export, Func<ExportResult, string> done)
    {
        if (_exporting) return "An export is already running.";
        _exporting = true;
        UpdateExportButtons();
        HideError();
        ExportText.Text = "Exporting...";
        string? error = null;
        try
        {
            var r = await Task.Run(export);
            if (r.Check.Passed) ExportText.Text = done(r);
            else
            {
                error = $"The file was written to {r.GlbPath}, but reading it back doesn't give the clip: {r.Check}";
                ExportText.Text = "";
            }
        }
        catch (ExportException ex) { error = ex.Message; ExportText.Text = ""; }
        catch (Exception ex)
        {
            // anything else (an odd path, a file too big for the format): the export ends with its message, never stuck at "Exporting...",
            // and one not foreseen is logged
            App.LogError(ex, "while exporting a .glb");
            error = $"The export failed: {ex.Message}";
            ExportText.Text = "";
        }
        finally
        {
            _exporting = false;
            UpdateExportButtons();
        }
        if (error is not null) ShowError(error);
        return error;
    }

    /// <summary>The command line's exports, for automated checks: the skeleton and the clip it names (shown first, as a person would),
    /// into its export folder, with no dialog. Returns an error message, or null; prints what was written.</summary>
    public async Task<string?> ExportForAutomationAsync(ViewerArgs args)
    {
        if (args.ExportSkeleton)
        {
            string p = Path.Combine(args.ExportOut!, ClipExport.SkeletonFileName);
            if (await ExportSkeletonAsync(p) is { } err) return err;
            Console.WriteLine($"wrote {p}");
        }
        if (args.ExportClip is { } clip)
        {
            if (await ShowAsync(clip, 0, null, play: false) is { } err) return err;
            if (_sampler is null) return $"{clip} can't be shown, so it can't be exported.";
            string p = Path.Combine(args.ExportOut!, ClipExport.FileNameFor(_clip!.Name));
            if (await ExportClipAsync(p) is { } err2) return err2;
            Console.WriteLine($"wrote {p} and {ClipExport.SidecarPathFor(p)}");
        }
        return null;
    }

    // ---- errors ----

    /// <summary>Shows a message in the window's error strip.</summary>
    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }

    void HideError() => ErrorBox.Visibility = Visibility.Collapsed;

    void OnDismissError(object sender, RoutedEventArgs e) => HideError();
}
