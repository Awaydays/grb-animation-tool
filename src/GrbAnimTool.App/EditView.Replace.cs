using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Line = System.Windows.Shapes.Line;
using Shape = System.Windows.Shapes.Shape;
using Path = System.IO.Path;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>The Edit screen's Replace: another game clip's motion (modded or the game's own row, searched as the Library searches) or a
/// .glb's, put on the clip shown, fitted to its exact length (cut, speed or hold), with the seamless loop and the root motion's scale. The
/// clip keeps its own events and timing data; the result plays and is kept like any edit's.</summary>
public partial class EditView
{
    ClipEntry? _replaceSource;
    string? _replaceGlb;
    /// <summary>The .glb's length in ticks, from its keys, or null before one is had.</summary>
    int? _glbTicks;
    /// <summary>The source the radio buttons choose (the game clip, or the .glb), its length in ticks, and a key naming it.</summary>
    ClipEntry? ChosenClip => FromClipChoice.IsChecked == true ? _replaceSource : null;
    string? ChosenGlb => FromClipChoice.IsChecked == true ? null : _replaceGlb;
    int? _replaceTicks => ChosenClip is { } c ? c.Record?.LastTick : ChosenGlb is not null ? _glbTicks : null;
    string SourceKey() => ChosenClip?.Key.ToString() ?? ChosenGlb ?? "";
    /// <summary>The cut's suggestion, for the clip and source it was made for.</summary>
    (ulong Slot, string Source, int Start)? _suggested;
    /// <summary>The loop blend's first frame of the result shown, marked on the timeline.</summary>
    int? _loopFrom;

    bool ReplaceChosen => ReplaceChoice.IsChecked == true;

    /// <summary>Whether a replace can be made as the controls stand.</summary>
    bool ReplaceReady => _clip?.Record is not null && _replaceTicks is not null
        && (FromClipChoice.IsChecked == true ? _replaceSource is not null : _replaceGlb is not null)
        && FitOptionsNow() is not null;

    /// <summary>The fit as the controls give it, or null when a number box doesn't hold a number it takes.</summary>
    FitOptions? FitOptionsNow()
    {
        // in set mode each clip has its own start (the single box hidden): the single box isn't read
        int start = 0;
        if (!SetTicked && (!int.TryParse(CutStartBox.Text.Trim(), out start) || start < 0)) return null;
        if (!int.TryParse(LoopFramesBox.Text.Trim(), out int frames) || frames < 1) return null;
        if (!ClipQuery.TryParseSeconds(RootScaleBox.Text, out var root) || root is null) return null;
        var mode = StretchChoice.IsChecked == true ? FitMode.Speed : HoldChoice.IsChecked == true ? FitMode.Hold : FitMode.Cut;
        return new FitOptions { Mode = mode, CutStart = start, Loop = LoopFitBox.IsChecked == true, LoopFrames = frames, RootScale = root.Value };
    }

    /// <summary>Replace's controls as the clip and source stand: enabled on a clip that reads, the source's panel, the fit shown only when
    /// the lengths differ, the cut's start only for a cut.</summary>
    void ShowReplace()
    {
        RefillReplaceListIfTargetsChanged();
        bool can = !_busy && _clip?.Record is not null;
        ReplaceChoice.IsEnabled = can;
        if (!can && !_busy && ReplaceChoice.IsChecked == true) ReplaceChoice.IsChecked = false;
        bool fromClip = FromClipChoice.IsChecked == true;
        FromClipPanel.Visibility = fromClip ? Visibility.Visible : Visibility.Collapsed;
        FromGlbPanel.Visibility = fromClip ? Visibility.Collapsed : Visibility.Visible;
        int? slot = _clip?.Record?.LastTick;
        // for a set, with one source: the fit when any clip ticked differs from it; with a source per clip, always
        bool differs = SetChosen ? PerClip || _replaceTicks is int st && _setRows.Any(r => r.Ticked && r.Clip.LastTick != st)
            : slot is int s && _replaceTicks is int t && s != t;
        FitPanel.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        CutPanel.Visibility = CutChoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ReplaceSourceText.Text = _replaceTicks is not int ticks || slot is not int last ? ""
            : ticks == last ? $"The source lasts {ticks} ticks, as this clip does: no fit needed."
            : $"The source lasts {ticks} ticks ({ticks / 60.0:F2} s), this clip {last} ({last / 60.0:F2} s): choose how to fit it."
              + (ticks < last ? " A cut can't fill it: use Speed or Hold." : "");
        // the timing data's help beside a trim or speed refused for it
        bool timing = _options.Any(o => o.Kind is EditKind.Trim or EditKind.Speed && o.Refusal is Core.Edits.EditRefusal.TimesUnread);
        TimingHelp.Visibility = timing && !_busy ? Visibility.Visible : Visibility.Collapsed;
        // a new clip with a longer source chosen: its cut suggested (once per pair); a set's cuts are suggested per clip when it is made
        if (differs && ReplaceChosen && !SetChosen) SuggestSoon();
        ShowEditSet();
    }

    void OnReplaceChanged(object sender, RoutedEventArgs e)
    {
        if (!_built) return;
        DropSetResults();
        if (sender != FromClipChoice && sender != FromGlbChoice && ReplaceChoice.IsEnabled) ReplaceChoice.IsChecked = true;
        ShowReplace();
        UpdateButtons();
    }

    void OnReplaceNumbers(object sender, TextChangedEventArgs e)
    {
        if (!_built) return;
        // the cut's start is the single Replace's (its suggestion may land later); a set suggests its own per clip
        if (sender != CutStartBox) DropSetResults();
        UpdateButtons();
    }

    /// <summary>A row of Replace's source list: a game clip, and why the clips Replace is for can't take its motion (null: they can).</summary>
    public sealed record SourceRow(ClipEntry Clip, string? Refusal)
    {
        public string Name => Clip.Name;
        public float Duration => Clip.Duration;
        public bool Takes => Refusal is null;
        public string Note => Refusal is null ? Clip.StatusText : Refusal;
    }

    /// <summary>The clips Replace's source is for, and the list they were last worked out for.</summary>
    IReadOnlyList<ClipEntry> ReplaceTargets() =>
        SetChosen ? (PerClip && SetClipList.SelectedItem is SetRow { } row ? [row.Clip] : _setRows.Where(r => r.Ticked).Select(r => r.Clip).ToList())
        : _clip is { } c ? [c] : [];
    IReadOnlyList<ClipEntry>? _sourcesFor;

    void OnReplaceSearch(object sender, TextChangedEventArgs e) => FillReplaceList();

    void OnShowEveryClip(object sender, RoutedEventArgs e) => FillReplaceList();

    /// <summary>The source list again when the clips it is for changed (another clip shown, the set's ticks or row).</summary>
    void RefillReplaceListIfTargetsChanged()
    {
        if (_sourcesFor is null || !_sourcesFor.SequenceEqual(ReplaceTargets())) FillReplaceList();
    }

    bool _fillingSources;

    /// <summary>Replace's source list: the search's best matches first, as the Library's search ranks them (a few hundred are enough to
    /// pick from), only the clips every clip Replace is for can take (on its skeleton, or on one whose bones map onto it); with Show every
    /// clip the others too, greyed, each with why. The source chosen stays selected.</summary>
    void FillReplaceList()
    {
        if (_install is not { } g) { ReplaceList.ItemsSource = null; return; }
        var targets = ReplaceTargets();
        _sourcesFor = targets;
        // whether the targets take a source depends on its skeleton alone: worked out once a skeleton
        // shortcut: a skeleton's first look reads its record on the window's thread (once an install, a few ms); move off it if that shows
        var takes = new Dictionary<Core.Skeletons.ClipSkeleton, bool>();
        bool Takes(ClipEntry s) => takes.TryGetValue(s.OnSkeleton, out var t) ? t : takes[s.OnSkeleton] = targets.All(x => g.ReplaceProblem(x, s) is null);
        var found = new ClipQuery { Text = ReplaceSearch.Text }.Apply(g.AllClips, ClipSort.Relevance);
        var rows = (ShowEveryClipBox.IsChecked == true ? found : found.Where(Takes)).Take(400)
            .Select(s => new SourceRow(s, Takes(s) ? null : targets.Select(x => g.ReplaceProblem(x, s)).First(p => p is not null))).ToList();
        _fillingSources = true;
        ReplaceList.ItemsSource = rows;
        ReplaceList.SelectedItem = rows.FirstOrDefault(r => r.Takes && r.Clip == _replaceSource);
        _fillingSources = false;
    }

    /// <summary>The source list's rows (for tests).</summary>
    internal IReadOnlyList<SourceRow> SourceRows => ReplaceList.ItemsSource as IReadOnlyList<SourceRow> ?? [];

    /// <summary>Chooses the source list's row of the clip named <paramref name="name"/> as a click would (for tests).</summary>
    internal void ChooseSource(string name) => ReplaceList.SelectedItem = SourceRows.First(r => r.Name == name);

    void OnReplaceSourceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSources || ReplaceList.SelectedItem is not SourceRow { Takes: true, Clip: var src }) return;
        _replaceSource = src;
        DropSetResults();
        TakeSetSource(SetSource.Of(src));
        if (ReplaceChoice.IsEnabled) ReplaceChoice.IsChecked = true;
        SuggestSoon();
        ShowReplace();
        UpdateButtons();
    }

    void OnReplaceGlb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the .glb to put on this clip (made on the game's skeleton, at 60 frames a second)", Filter = "glTF binary (*.glb)|*.glb", CheckFileExists = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = TakeGlbAsync(dlg.FileName);
    }

    /// <summary>Takes a .glb as the source: its length read from its keys (in the background); a file that can't be read is said.</summary>
    internal async Task TakeGlbAsync(string path)
    {
        _replaceGlb = null;
        _glbTicks = null;
        ReplaceGlbText.Text = "reading...";
        try
        {
            var ticks = await Task.Run(() =>
            {
                var fi = new FileInfo(path);
                if (fi.Length > ClipImport.MaxFileBytes) throw new InvalidDataException($"{fi.Length / 1048576} MB, too large for a clip");
                return ClipImport.AnimationTicks(GlbScene.Read(File.ReadAllBytes(path)));
            });
            if (ticks is not double t) { ReplaceGlbText.Text = $"{Path.GetFileName(path)}: no animation in it"; return; }
            _replaceGlb = path;
            _glbTicks = (int)Math.Round(t);
            DropSetResults();
            TakeSetSource(SetSource.OfGlb(path));
            // the .glb chosen: the source is the file from now on
            FromGlbChoice.IsChecked = true;
            ReplaceGlbText.Text = Path.GetFileName(path);
            if (ReplaceChoice.IsEnabled) ReplaceChoice.IsChecked = true;
            SuggestSoon();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ReplaceGlbText.Text = $"{Path.GetFileName(path)} can't be read: {ex.Message}";
        }
        // started from a click and not awaited: nothing may go unseen
        catch (Exception ex)
        {
            App.LogError(ex, "while reading a .glb for Replace");
            ReplaceGlbText.Text = $"{Path.GetFileName(path)} can't be read ({ex.GetType().Name}): {ex.Message}";
        }
        ShowReplace();
        UpdateButtons();
    }

    void OnSuggestCut(object sender, RoutedEventArgs e) => SuggestSoon(force: true);

    /// <summary>Works out the cut's suggested start for the clip and source in the background and puts it in the box (once per pair,
    /// unless asked again).</summary>
    async void SuggestSoon(bool force = false)
    {
        if (_install is not { } g || _clip?.Record is not { } rec || _replaceTicks is not int ticks || ticks <= rec.LastTick) return;
        string key = SourceKey();
        if (!force && _suggested is { } s && s.Slot == _clip.Key && s.Source == key) return;
        var src = ChosenClip;
        var glb = ChosenGlb;
        var slot = _clip;
        int last = rec.LastTick;
        try
        {
            int? start = await Task.Run(() =>
            {
                if (g.ReplaceProblem(slot, src) is { } why) throw new ImportRefusedException(why, ImportRefusal.File);
                var sk = g.SkeletonOf(slot)!;
                Motion m = src is not null ? Motion.FromClip(sk, src.Record!, g.SharedTable)
                    : ClipImport.Run(sk, g.SharedTable, slot, File.ReadAllBytes(glb!), glb!, fit: new FitOptions { Mode = FitMode.Hold }).Unfitted!;
                return ClipFit.SuggestCut(m, last);
            });
            // the clip and the source still the ones it was worked out for
            if (start is int at && _clip == slot && SourceKey() == key)
            {
                _suggested = (slot.Key, key, at);
                CutStartBox.Text = at.ToString();
                StatusText.Text = $"Suggested cut start: frame {at} ({at / 60.0:F2} s), where the loop closes best.";
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ImportRefusedException or ArgumentException or UnauthorizedAccessException)
        {
            StatusText.Text = $"No cut suggested: {ex.Message}";
        }
        // an async void: nothing may escape it to the window's thread
        catch (Exception ex)
        {
            App.LogError(ex, "while suggesting a cut for Replace");
            StatusText.Text = $"No cut suggested ({ex.GetType().Name}): {ex.Message}";
        }
    }

    /// <summary>Makes the replace in the background and shows its result, as an edit's. Returns the refusal or failure, or null.</summary>
    internal async Task<string?> ReplaceAsync()
    {
        if (_install is not { } g || _clip is not { } c || FitOptionsNow() is not { } fit) return "Choose a clip, a source and how to fit it first.";
        var src = FromClipChoice.IsChecked == true ? _replaceSource : null;
        var glb = FromClipChoice.IsChecked == true ? null : _replaceGlb;
        if (src is null && glb is null) return "Choose the source clip or .glb first.";
        var verdict = _ctx?.Verdict;
        int id = ++_runId;
        _outcome = null;
        _setOutcomes = null;
        SetResultText.Text = "";
        HideError();
        StatusText.Text = "";
        string what = src is not null ? $"motion from {src.Name}" : Path.GetFileName(glb!);
        SetBusy($"Replacing: {c.Name} with {what}...");
        ShowOutcome();
        string? error = null;
        EditOutcome? o = null;
        (List<EventMark>, List<PlantSpan>) marks = ([], []);
        string map = "";
        try
        {
            (o, marks, map) = await Task.Run(() =>
            {
                // the clip's own skeleton, and a source on it (or on one whose bones map onto it)
                if (g.ReplaceProblem(c, src) is { } why) throw new ImportRefusedException(why, src is null ? ImportRefusal.Slot : ImportRefusal.File);
                var sk = g.SkeletonOf(c)!;
                var r = src is not null ? ClipReplace.FromClip(sk, g.SharedTable, c, src, fit, verdict, verdict is not null)
                    : ClipImport.Run(sk, g.SharedTable, c, File.ReadAllBytes(glb!), Path.GetFullPath(glb!), verdict, verdict is not null, fit);
                int srcTicks = r.Unfitted?.LastTick ?? c.LastTick;
                string words = fit.Words(srcTicks, c.LastTick) is { Length: > 0 } fw ? $"{what}, {fw}" : what;
                var made = EditOutcome.OfReplace(r, src, ClipChange.Replaced(words));
                return (made, Marks(made.Result.Record, g), TrackMapLine(ClipReplace.TrackMap(r, sk)));
            });
        }
        catch (ImportRefusedException ex) { error = "Not made. " + ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { error = $"The replace failed: {ex.Message}"; }
        catch (Exception ex)
        {
            App.LogError(ex, $"while replacing {c.Name} with {what}");
            error = $"The replace failed ({ex.GetType().Name}): {ex.Message}";
        }
        if (id != _runId) return error;
        SetBusy(null);
        _outcome = o;
        _resultMarks = marks;
        _loopFrom = o is not null && fit.Loop ? Math.Max(0, c.LastTick - Math.Clamp(fit.LoopFrames, 1, c.LastTick) + 1) : null;
        LastError = error;
        TrackMapText.Text = o is null ? "" : map;
        if (error is not null) { ShowError(error); StatusText.Text = ""; }
        else StatusText.Text = $"Made: {c.Name} ({o!.Change.Words}). It plays below, looping, with the original as a ghost. Check the tracks below before adding it.";
        ShowOutcome();
        if (o is not null && !_automated) _clock.Play();
        UpdateTimeline();
        return error;
    }

    /// <summary>The tracks the replace filled with the rest pose, dropped and kept, in a line each.</summary>
    internal static string TrackMapLine((IReadOnlyList<string> Filled, IReadOnlyList<string> Dropped, IReadOnlyList<string> Kept) m)
    {
        static string List(IReadOnlyList<string> l, int n) => string.Join(", ", l.Take(n)) + (l.Count > n ? ", ..." : "");
        return string.Join("\n",
            m.Filled.Count == 0 ? "Filled with the rest pose: none." : $"Filled with the rest pose (the source has no track for them): {List(m.Filled, 12)}.",
            m.Dropped.Count == 0 ? "Dropped: none." : $"Dropped (this clip has no track for them): {List(m.Dropped, 12)}.",
            m.Kept.Count == 0 ? "Kept from this clip: its events." : $"Kept from this clip: its events and {m.Kept.Count} track(s) that aren't bone rotations or positions ({List(m.Kept, 8)}).");
    }

    /// <summary>The loop's blend start marked on the timeline's strip, for a replace's result with the seamless loop.</summary>
    void DrawLoopMark()
    {
        if (_outcome?.ReportOverride is null || _loopFrom is not int from || _clock.Duration <= 0 || MarkStrip.ActualWidth <= 0) return;
        double x = MarkStrip.ActualWidth * (from / 60.0) / _clock.Duration;
        var line = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = MarkStrip.ActualHeight, StrokeThickness = 2, StrokeDashArray = [2, 2], ToolTip = $"The loop's blend starts here (frame {from}) and ends on the first frame's pose" };
        line.SetResourceReference(Shape.StrokeProperty, Theme.Key("Warning"));
        MarkStrip.Children.Add(line);
        MarkText.Text += $"  ·  loop blend from {from / 60.0:F2} s";
    }
}
