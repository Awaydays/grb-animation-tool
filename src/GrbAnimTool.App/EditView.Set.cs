using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using Path = System.IO.Path;

namespace GrbAnimTool.App;

/// <summary>The Edit screen's sets: the set the clip shown is in (read from the graph, or a probable set by name), Replace for the whole
/// set or the clips ticked (one source, or one per clip; each clip its own piece of a long source; lined up on one pose), the set's preview
/// (its clips one after another with the blend between) and its results kept for the package together.</summary>
public partial class EditView
{
    /// <summary>A row of the set's list: a clip, whether it is replaced, and its own source (with "a source per clip").</summary>
    public sealed class SetRow(ClipEntry clip) : INotifyPropertyChanged
    {
        public ClipEntry Clip { get; } = clip;
        bool _ticked = true;
        public bool Ticked { get => _ticked; set { _ticked = value; Changed(nameof(Ticked)); } }
        SetSource? _source;
        public SetSource? Source { get => _source; set { _source = value; Changed(nameof(SourceText)); } }
        public string SourceText => _source is null ? "" : $"  from {_source.Name}";
        /// <summary>Where the clip's piece starts in the source, as typed (empty: suggested when the set is made).</summary>
        string _start = "";
        public string Start { get => _start; set { _start = value; Changed(nameof(Start)); } }
        /// <summary>The start's box shown: with Cut, in set mode.</summary>
        Visibility _startShown = Visibility.Collapsed;
        public Visibility StartShown { get => _startShown; set { _startShown = value; Changed(nameof(StartShown)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>The clips selected together in the list (Ctrl or Shift), taken as a set's clips while two or more are selected; else null.</summary>
    ClipSet? _chosen;
    /// <summary>The most clips a selection takes as Replace's clips.</summary>
    internal const int MaxChosen = 100;
    /// <summary>More than <see cref="MaxChosen"/> rows are selected (none taken).</summary>
    bool _tooManyChosen;

    /// <summary>As a change of the rows selected in the list (for tests).</summary>
    internal void TakeSelection(IReadOnlyList<ClipEntry> picked) => OnSelectionPicked(picked);

    /// <summary>The rows selected in the list changed: two or more are Replace's clips, as a set's are.</summary>
    void OnSelectionPicked(IReadOnlyList<ClipEntry> picked)
    {
        var ids = picked.Select(c => c.Id).Distinct().ToList();
        // shortcut: a selection past MaxChosen (Ctrl+A) isn't taken, the screen's set list isn't virtualized; a paged list would lift it
        bool tooMany = ids.Count > MaxChosen;
        var now = ids.Count >= 2 && !tooMany ? ClipSet.ChosenOf(ids) : null;
        // too many: Replace isn't left ticked for the shown clip's set either, the screen saying only why the selection isn't taken
        if (tooMany != _tooManyChosen) { _tooManyChosen = tooMany; if (tooMany) WholeSetBox.IsChecked = false; ShowEditSet(); }
        if (now is null && _chosen is null) return;
        if (now is not null && _chosen is not null && now.Clips.SequenceEqual(_chosen.Clips)) return;
        _chosen = now;
        DropSetResults();
        // ticked for a selection, unticked when it ends: the one clip left doesn't stand for its game set
        WholeSetBox.IsChecked = now is not null;
        ShowEditSet();
        ShowReplace();
        UpdateButtons();
    }

    ClipSets? _editSets;
    /// <summary>How many times the set's options changed (<see cref="DropSetResults"/>).</summary>
    int _setOptionsChanged;
    /// <summary>The sets couldn't be worked out for the install open (the error log says why).</summary>
    bool _setsFailed;
    /// <summary>The set shown and its rows (for the clip shown).</summary>
    ClipSet? _set;
    List<SetRow> _setRows = [];
    /// <summary>The set's results, in the rows' order, once a set Replace is made; null otherwise.</summary>
    List<EditOutcome>? _setOutcomes;
    /// <summary>The set's preview playing in the viewport (its clips chained), or null.</summary>
    (Motion Chain, IReadOnlyList<int> Starts, IReadOnlyList<string> Names, double Blend, bool BlendRead)? _preview;

    internal ClipSet? SetShown => _set;
    internal IReadOnlyList<SetRow> SetRows => _setRows;
    internal IReadOnlyList<EditOutcome>? SetOutcomes => _setOutcomes;
    internal bool Previewing => _preview is not null;

    /// <summary>The install's sets, once worked out: the clip shown says its set.</summary>
    async Task TakeSetsAsync(GameInstall g)
    {
        var sets = await InstallWork.Sets(g);
        if (!ReferenceEquals(g, _install)) return;
        _editSets = sets;
        _setsFailed = sets is null;
        ClipPanel.SetSets(sets);
        ShowEditSet();
        UpdateButtons();
    }

    /// <summary>The set of the clip shown: its words, its rows (kept while the set stays the same), and the set's options.</summary>
    void ShowEditSet()
    {
        // two or more clips selected in the list are the clips Replace takes, as a set's
        var s = _chosen ?? (_clip is { } c ? _editSets?.Main(c.Id) : null);
        if (!ReferenceEquals(s, _set))
        {
            _set = s;
            _setRows = s is null || _install is not { } g ? [] : s.Clips.Select(id => g.FindClip(id)).OfType<ClipEntry>().Select(x => new SetRow(x)).ToList();
            SetClipList.ItemsSource = _setRows;
            _setOutcomes = null;
            // a blended set needs matching poses, not variety: the same piece for every clip, and by default a source per clip, so the
            // game's blend between them still does something
            DistinctPiecesBox.IsChecked = s?.Kind != ClipSetKind.Blended;
            if (s?.Kind == ClipSetKind.Blended) PerClipSourceChoice.IsChecked = true;
            else if (s is not null) OneSourceChoice.IsChecked = true;
        }
        EditSetText.Text = _chosen is { } ch ? $"{ch.Clips.Count} clips selected in the list: Replace puts the source on every one ticked below, as on a set's clips."
            : _tooManyChosen ? $"Over {MaxChosen} clips selected: Replace takes at most {MaxChosen} at once. Select fewer, or one clip and its set."
            : _editSets is null ? (_install is null ? "-" : _setsFailed ? "The sets couldn't be worked out (the error log says why)." : "Working out the sets…")
            : _clip is null ? "-"
            : s is null ? (_editSets.ReadByGraph(_clip.Id) ? "This clip is in no set." : "No set known for this clip: the game's data for it wasn't read.")
            : FormattableString.Invariant($"{char.ToUpperInvariant(s.Words[0])}{s.Words[1..]} of {s.Clips.Count} clips, in the graph's state \"{s.State}\"{(s.Machine is { } m ? $" of {m}" : "")}.");
        EditSetText.ToolTip = s is null ? null : $"{s.Words}: {s.Where}";
        EditProbableSetHelp.Visibility = s is { Kind: ClipSetKind.Probable } ? Visibility.Visible : Visibility.Collapsed;
        bool can = !_busy && s is not null;
        WholeSetBox.IsEnabled = can;
        if (s is null) WholeSetBox.IsChecked = false;
        bool set = WholeSetBox.IsChecked == true && s is not null;
        WholeSetBox.Content = _chosen is not null ? "Replace the clips selected: every clip ticked below" : "Replace the set: every clip ticked below";
        SetOptionsPanel.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
        DistinctPiecesBox.IsEnabled = s?.Kind != ClipSetKind.Blended;
        PreviewSetButton.IsEnabled = can;
        // in set mode each clip has its own start (beside it in the list): the single start box gives way
        bool cut = CutChoice.IsChecked == true;
        if (set) CutPanel.Visibility = Visibility.Collapsed;
        PieceHead.Visibility = set && cut ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in _setRows) r.StartShown = set && cut ? Visibility.Visible : Visibility.Collapsed;
        // one source for a blended set gives every clip the same pose: the game's blend by its value then does nothing
        bool warn = set && s is { Kind: ClipSetKind.Blended } && OneSourceChoice.IsChecked == true;
        BlendedOneSourceWarning.Text = warn ? HelpTexts.OneSourceOnBlended(s!.Input ?? "its value") : "";
        BlendedOneSourceWarning.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The starts a set Replace took are being put in their boxes: no change of the options.</summary>
    bool _fillingStarts;

    void OnPieceStartChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_built || _fillingStarts) return;
        DropSetResults(keepStarts: true);
        UpdateButtons();
    }

    void OnSuggestPieces(object sender, RoutedEventArgs e) => _ = SuggestPiecesAsync();

    /// <summary>Suggest for the set: every clip's start worked out anew (the whole distribution), in the background, into the boxes.
    /// Returns the refusal, or null.</summary>
    internal async Task<string?> SuggestPiecesAsync()
    {
        if (SetPlan(suggest: true) is not { } plan || plan.G is not { } g) return "Choose the set's source and how to fit it first.";
        var (_, rows, sources, opts) = plan;
        var set = _set;
        int options = _setOptionsChanged;
        try
        {
            var r = await Task.Run(() => ClipSetReplace.Suggest(g, InstallWork.Graph(g).Result, rows.Select(x => x.Clip).ToList(), sources, opts));
            // the set, its source or its options changed while it ran: these starts belong to what was
            if (!ReferenceEquals(set, _set) || options != _setOptionsChanged) return null;
            for (int i = 0; i < rows.Count; i++) rows[i].Start = r.CutStarts[i] is int st ? st.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            SetResultText.Text = string.Join("\n", r.Notes);
            StatusText.Text = "Suggested every clip's start: the pieces share as little of the source as their lengths allow, the feet lined up.";
            return null;
        }
        catch (ImportRefusedException ex) { StatusText.Text = $"No starts suggested: {ex.Message}"; return ex.Message; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { StatusText.Text = $"No starts suggested: {ex.Message}"; return ex.Message; }
        catch (Exception ex)
        {
            App.LogError(ex, "while suggesting the set's starts");
            StatusText.Text = $"No starts suggested ({ex.GetType().Name}): {ex.Message}";
            return ex.Message;
        }
    }

    /// <summary>The set's Replace as the controls stand: the install, the clips ticked, the sources and the options (the starts typed, empty
    /// ones suggested; none with <paramref name="suggest"/>), or null when something isn't chosen yet.</summary>
    (GameInstall? G, List<SetRow> Rows, List<SetSource> Sources, SetReplaceOptions Options)? SetPlan(bool suggest = false)
    {
        if (_install is not { } g || FitOptionsNow() is not { } fit || _set is not { } set) return null;
        var rows = _setRows.Where(r => r.Ticked).ToList();
        if (rows.Count == 0) return null;
        SetSource? one = PerClip ? null : FromClipChoice.IsChecked == true ? (_replaceSource is { } c ? SetSource.Of(c) : null) : _replaceGlb is { } gl ? SetSource.OfGlb(gl) : null;
        if (!PerClip && one is null) return null;
        if (PerClip && rows.Any(r => r.Source is null)) return null;
        var sources = PerClip ? rows.Select(r => r.Source!).ToList() : [one!];
        int? poseFrame = PoseFrameChoice.IsChecked == true && int.TryParse(PoseFrameBox.Text.Trim(), out int pf) ? pf : null;
        IReadOnlyList<int?>? starts = null;
        if (!suggest && fit.Mode == FitMode.Cut)
        {
            var l = new List<int?>();
            foreach (var r in rows)
            {
                // empty: suggested; a box holding anything but a frame number refuses the set
                string t = r.Start.Trim();
                if (t.Length == 0) { l.Add(null); continue; }
                if (!int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int st)) return null;
                l.Add(st);
            }
            starts = l;
        }
        var opts = new SetReplaceOptions
        {
            Fit = fit with { CutStart = 0 },
            DistinctPieces = DistinctPiecesBox.IsChecked == true && set.Kind != ClipSetKind.Blended,
            LineUp = LineUpBox.IsChecked == true,
            PoseSource = poseFrame is null ? null : sources[0],
            PoseFrame = poseFrame ?? 0,
            Starts = starts,
            SwitchTicks = (int)Math.Round(set.PreviewBlend * 60),
        };
        return (g, rows, sources, opts);
    }

    /// <summary>The set's results belong to the options they were made with: any change of the clips ticked, the sources, the fit or the
    /// line-up drops them (Apply makes them again), so Add to package never keeps clips or a fit the screen no longer shows.</summary>
    void DropSetResults() => DropSetResults(keepStarts: false);

    /// <summary>As <see cref="DropSetResults()"/>; the starts in the boxes go too (they belong to the source and options they were
    /// worked out for: empty, each is suggested anew), unless <paramref name="keepStarts"/> (a start itself was typed).</summary>
    void DropSetResults(bool keepStarts)
    {
        if (!keepStarts && !_fillingStarts)
        {
            _fillingStarts = true;
            foreach (var r in _setRows) r.Start = "";
            _fillingStarts = false;
        }
        // counted even while a set Replace runs: that run's results are dropped when it ends (ReplaceSetAsync)
        _setOptionsChanged++;
        if (_setOutcomes is null || _busy) return;
        _setOutcomes = null;
        _outcome = null;
        SetResultText.Text = "The set's options changed: Apply makes the set again with them.";
        ShowOutcome();
    }

    void OnSetChanged(object sender, RoutedEventArgs e)
    {
        if (!_built) return;
        DropSetResults();
        if (sender == WholeSetBox && WholeSetBox.IsChecked == true && ReplaceChoice.IsEnabled) ReplaceChoice.IsChecked = true;
        ShowEditSet();
        ShowReplace();
        UpdateButtons();
    }

    // with a source per clip, the source list is for the row selected
    void OnSetRowChosen(object sender, SelectionChangedEventArgs e) { if (_built) RefillReplaceListIfTargetsChanged(); }

    /// <summary>Whether Replace works on the set as the controls stand: the set ticked, with a clip ticked.</summary>
    bool SetChosen => SetTicked && _setRows.Any(r => r.Ticked);

    /// <summary>"Replace the set" ticked on a clip in a set: Replace is the set's (with no clip ticked it makes nothing, never the one clip alone).</summary>
    bool SetTicked => WholeSetBox.IsChecked == true && _set is not null;

    bool PerClip => PerClipSourceChoice.IsChecked == true;

    /// <summary>A source chosen while "a source per clip" is on: it goes to the set's row selected (and the row is ticked).</summary>
    void TakeSetSource(SetSource src)
    {
        if (WholeSetBox.IsChecked != true || !PerClip || SetClipList.SelectedItem is not SetRow row) return;
        row.Source = src;
        row.Ticked = true;
    }

    /// <summary>Whether the set Replace can be made: a source for all, or one for every clip ticked, and a fit that reads.</summary>
    bool SetReady => _install is not null && FitOptionsNow() is not null && (PerClip
        ? _setRows.Where(r => r.Ticked).All(r => r.Source is not null)
        : (FromClipChoice.IsChecked == true ? _replaceSource is not null : _replaceGlb is not null))
        && (PoseFrameChoice.IsChecked != true || int.TryParse(PoseFrameBox.Text.Trim(), out int f) && f >= 0);

    /// <summary>Replace on the set's clips ticked, in the background; the clip shown's result plays, every clip's checks are listed under
    /// the set, and Add to package keeps them all. Returns the refusal or failure, or null.</summary>
    internal async Task<string?> ReplaceSetAsync()
    {
        if (_install is not { } g || _clip is not { } shown || FitOptionsNow() is not { } fit || _set is not { } set) return "Choose a clip in a set, a source and how to fit it first.";
        var rows = _setRows.Where(r => r.Ticked).ToList();
        if (rows.Count == 0) return "Tick at least one clip of the set.";
        SetSource? one = PerClip ? null : FromClipChoice.IsChecked == true ? (_replaceSource is { } c ? SetSource.Of(c) : null) : _replaceGlb is { } gl ? SetSource.OfGlb(gl) : null;
        if (!PerClip && one is null) return "Choose the source clip or .glb first.";
        if (PerClip && rows.FirstOrDefault(r => r.Source is null) is { } missing) return $"Choose a source for {missing.Clip.Name}: select it in the set's list, then choose its source.";
        if (SetPlan() is not { } plan) return "A clip's start or a number box doesn't hold a number it takes: correct it, or empty a start to have it suggested.";
        var (_, _, sources, opts) = plan;
        int id = ++_runId;
        int options = _setOptionsChanged;
        _outcome = null;
        _setOutcomes = null;
        HideError();
        StatusText.Text = "";
        SetResultText.Text = "";
        SetBusy($"Replacing the set's {rows.Count} clips...");
        ShowOutcome();
        string? error = null;
        List<EditOutcome>? made = null;
        (List<EventMark>, List<PlantSpan>) marks = ([], []);
        string summary = "";
        IReadOnlyList<int?> took = [];
        try
        {
            (made, marks, summary, took) = await Task.Run(() =>
            {
                var graph = InstallWork.Graph(g).Result;
                var r = ClipSetReplace.Run(g, graph, rows.Select(x => x.Clip).ToList(), sources, opts);
                var outs = new List<EditOutcome>();
                for (int i = 0; i < r.Results.Count; i++)
                {
                    var res = r.Results[i];
                    var src = sources.Count == 1 ? sources[0] : sources[i];
                    string what = src.Clip is { } sc ? $"motion from {sc.Name}" : src.Name;
                    // the words only: a pose marks it lined up
                    var f = opts.Fit with { CutStart = r.CutStarts[i] ?? 0, SharedPose = opts.LineUp ? [] : null };
                    string words = f.Words(res.Unfitted?.LastTick ?? res.Slot.LastTick, res.Slot.LastTick) is { Length: > 0 } fw ? $"{what}, {fw}" : what;
                    outs.Add(EditOutcome.OfReplace(res, src.Clip, ClipChange.Replaced(words)));
                }
                var seams = ClipSetReplace.Seams(r.Results);
                var lines = new List<string>();
                for (int i = 0; i < outs.Count; i++)
                {
                    var feet = outs[i].Report.Results.FirstOrDefault(x => x.Check == Feet.SlideCheck);
                    lines.Add(FormattableString.Invariant($"{outs[i].Clip.Name}: {outs[i].Report.Headline} Into the next: {seams[i]:F1}.{(feet is null ? "" : $" Feet slide: {feet.Said}.")}"));
                }
                lines.AddRange(r.Notes);
                var main = outs.FirstOrDefault(o => o.Clip == shown) ?? outs[0];
                return (outs, Marks(main.Result.Record, g), string.Join("\n", lines), r.CutStarts);
            });
        }
        catch (ImportRefusedException ex) { error = "Not made. " + ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { error = $"The set's replace failed: {ex.Message}"; }
        catch (Exception ex)
        {
            App.LogError(ex, $"while replacing the set of {shown.Name}");
            error = $"The set's replace failed ({ex.GetType().Name}): {ex.Message}";
        }
        if (id != _runId) return error;
        SetBusy(null);
        // an option changed while it ran: what was made isn't what the screen shows now
        if (error is null && options != _setOptionsChanged)
        {
            made = null;
            error = "The set's options changed while it was being made: Apply makes it again with them.";
        }
        // each clip's start as the set took it, in its box (the results stay: these are the starts they were made with)
        if (made is not null)
        {
            _fillingStarts = true;
            for (int i = 0; i < rows.Count && i < took.Count; i++) rows[i].Start = took[i] is int st ? st.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            _fillingStarts = false;
        }
        _setOutcomes = made;
        _outcome = made?.FirstOrDefault(o => o.Clip == shown) ?? made?.FirstOrDefault();
        _resultMarks = marks;
        _loopFrom = null;
        LastError = error;
        TrackMapText.Text = "";
        SetResultText.Text = summary + (made is null ? "" : "\nThe pose distance into the next clip: how far one clip's end lies from the next one's start (near 0: no jump).");
        if (error is not null) { ShowError(error); StatusText.Text = ""; }
        else StatusText.Text = $"Made: the set's {made!.Count} clips. {_outcome!.Clip.Name} plays below; Preview the set plays them one after another. Add to package keeps them all.";
        ShowOutcome();
        if (_outcome is not null && !_automated) _clock.Play();
        UpdateTimeline();
        return error;
    }

    /// <summary>Why the set's results can't be kept, or null: one of them is no change, or failed a game check.</summary>
    string? SetNotKeepable(GameInstall g) => _setOutcomes?.Select(o => PackageEntries.Unchanged(g, o.Clip, o.Result.Bytes, PackageItemKind.Edit) is { } u ? $"{o.Clip.Name}: {u}"
        : PackageEntries.NotForPackage(o.Report.Verdict) is { } f ? $"{o.Clip.Name}: {f}" : null).OfType<string>().FirstOrDefault();

    /// <summary>Keeps every result of the set for the package (one package for the set). Returns the refusal, or null.</summary>
    string? KeepSet()
    {
        if (_setOutcomes is not { Count: > 0 } outs || _install is not { } g) return "Nothing applied to add.";
        if (SetNotKeepable(g) is { } why) return $"Not added (the set is kept whole or not at all): {why}.";
        foreach (var o in outs) PackageList.Current.Keep(PackageEntries.ForEdit(o));
        StatusText.Text = $"Added the set's {outs.Count} clips to the package. The package holds {PackageList.Current.Count}.";
        return null;
    }

    void OnPreviewSet(object sender, RoutedEventArgs e) => _ = PreviewSetAsync();

    /// <summary>Plays the set in the viewport: its clips one after another (the results once a set Replace is made, else the game's own
    /// clips), each blended in from the last one's end over the set's own blend (read from the graph) or 0.3 s. Pressed again, it stops.</summary>
    internal async Task<string?> PreviewSetAsync()
    {
        if (_preview is not null) { _preview = null; ShowOutcome(); return null; }
        if (_install is not { } g || _set is not { } set) return "This clip is in no set.";
        var outs = _setOutcomes;
        var rows = _setRows.Where(r => outs is null || outs.Any(o => o.Clip == r.Clip)).ToList();
        try
        {
            var chain = await Task.Run(() =>
            {
                var motions = outs is not null ? outs.Select(o => o.Edited).ToList()
                    : rows.Select(r => Motion.FromClip(g.Skeleton, r.Clip.Record ?? throw new InvalidDataException($"{r.Clip.Name} can't be read"), g.SharedTable)).ToList();
                return ClipSetReplace.Preview(motions, (int)Math.Round(set.PreviewBlend * 60));
            });
            if (_set != set) return null;
            _preview = (chain.Chain, chain.Starts, (outs?.Select(o => o.Clip.Name) ?? rows.Select(r => r.Clip.Name)).ToList(), set.PreviewBlend, set.BlendSeconds is not null);
            _clock.Reset(chain.Chain.LastTick / 60f, chain.Chain.LastTick);
            _ghostHolder.Children.Clear();
            StatusText.Text = FormattableString.Invariant($"Previewing the set: {_preview.Value.Names.Count} clips, a {set.PreviewBlend} s blend between ")
                + (set.BlendSeconds is null ? "(the usual: the graph's own wasn't read)." : "(the graph's own).") + " Press Preview the set again to stop.";
            if (!_automated) _clock.Play();
            ShowPose();
            UpdateTimeline();
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            StatusText.Text = $"No preview: {ex.Message}";
            return ex.Message;
        }
        catch (Exception ex)
        {
            App.LogError(ex, "while making the set's preview");
            StatusText.Text = $"No preview ({ex.GetType().Name}): {ex.Message}";
            return ex.Message;
        }
    }

    /// <summary>The preview's clip at <paramref name="tick"/> of the chain: its name and number.</summary>
    string PreviewLine(int tick)
    {
        var p = _preview!.Value;
        int k = 0;
        while (k + 1 < p.Starts.Count && p.Starts[k + 1] <= tick) k++;
        return FormattableString.Invariant($"Set preview: {p.Names[k]} ({k + 1} of {p.Names.Count}), {p.Blend} s blends {(p.BlendRead ? "(the graph's)" : "(the usual)")}");
    }
}
