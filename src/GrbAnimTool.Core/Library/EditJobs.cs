using System.Globalization;
using System.Text;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Retiming;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Library;

/// <summary>The five edits the Edit screen offers.</summary>
public enum EditKind { Mirror, Scale, Speed, Trim, Swap }

/// <summary>One edit as asked: its kind, the factor (root-motion scale and speed), the cut (trim, source ticks) and the source clip (swap).</summary>
public sealed record EditRequest(EditKind Kind, double Factor = 1, int From = 0, int To = 0, ClipEntry? Source = null)
{
    /// <summary>The change the edit makes (<see cref="ClipChange.Of(EditRequest)"/>).</summary>
    public ClipChange Change => ClipChange.Of(this);

    /// <summary>What the edit does in the tool's one wording (<see cref="ClipChange.Words"/>): "Speed 1.25x", "Trim 0.9 s to 7.2 s".</summary>
    public string Describe() => Change.Words;
}

/// <summary>Whether an edit can be made on a clip: null <see cref="Unavailable"/> when it can, else why not in a modder's words
/// (<see cref="EditReasons.Plain(EditKind, EditRefusal?)"/>), with the technical <see cref="Detail"/> and the kind of refusal
/// (<see cref="Refusal"/>, null for a fault); and a warning it would carry (a blend that reads the clip's speed or heading, a motion
/// field's slot).</summary>
public sealed record EditOption(EditKind Kind, string? Unavailable, string? Warning = null, string? Detail = null, EditRefusal? Refusal = null)
{
    public bool Available => Unavailable is null;

    /// <summary>For a fault of the edit's own code on the clip (no kind of refusal): the error itself, for the error log.</summary>
    public Exception? Error { get; init; }

    /// <summary>The option for <paramref name="kind"/>: available (with its warning) when <paramref name="block"/> is null, else refused
    /// with the block's plain sentence and detail.</summary>
    public static EditOption Of(EditKind kind, EditBlock? block, string? warning) => block is null
        ? new EditOption(kind, null, warning)
        : new EditOption(kind, EditReasons.Plain(kind, block.Kind), null, block.Detail, block.Kind) { Error = block.Error };
}

/// <summary>An edit made: the clip, the request, the edited record, both motions at every tick, how the original's ticks map onto the
/// result's (trim and speed), and the checks on the result.</summary>
public sealed class EditOutcome
{
    public required ClipEntry Clip { get; init; }
    public required EditRequest Request { get; init; }
    public required EditedClip Result { get; init; }
    public required Motion Original { get; init; }
    public required Motion Edited { get; init; }
    /// <summary>The source time each of the result's ticks plays (trim and speed), else null: the result's tick is the original's.</summary>
    public TimeWarp? Warp { get; init; }
    public required CheckReport Report { get; init; }

    /// <summary>The original's tick that plays at the result's tick <paramref name="tick"/>, for the ghost following the edit's progress
    /// (<see cref="ReferenceClock.MappedTick"/>).</summary>
    public int OriginalTick(int tick) => ReferenceClock.MappedTick(Warp, tick, Original.LastTick);

    /// <summary>The original's mover for the ghost following the edit's progress at the result's tick <paramref name="tick"/>, in the
    /// result's frame: a trim from a later
    /// tick moves the result's mover so the cut starts where the game's clip started, and the ghost is moved the same way (by the result's
    /// first place against the original's at the cut's start), so the two stand together. Any other edit: the original's own mover.</summary>
    public Transform GhostMover(int tick)
    {
        var m = Original.Mover(OriginalTick(tick));
        if (Request.Kind != EditKind.Trim || Request.From <= 0) return m;
        var cut = Original.Mover(OriginalTick(0));
        var iq = System.Numerics.Quaternion.Inverse(cut.Rotation);
        var invCut = new Transform(System.Numerics.Vector3.Transform(-cut.Position, iq), iq);
        return Edited.Mover(0) * (invCut * m);
    }

    /// <summary>The report as text: the edit, every check, the verdict, the warnings and the edit's notes.</summary>
    public string ReportText()
    {
        var c = Clip;
        string head = string.Create(CultureInfo.InvariantCulture,
            $"Edit of {c.Index}_-_{c.Name} 0x{c.Id:X16} ({Request.Change.Detail}): {c.LastTick} ticks before, {Result.Record.LastTick} after; the record {Result.Bytes.Length} B");
        var sb = new StringBuilder(Report.ToText(head));
        sb.AppendLine("What the edit did and kept:");
        foreach (var n in Result.Notes) sb.AppendLine("   " + n);
        return sb.ToString();
    }
}

/// <summary>The Edit screen's work, without a window: which edits a clip can take and why not, running one, and the checks on its result.</summary>
public static class EditJobs
{
    /// <summary>The ranges the Edit screen offers: root-motion scale and speed factors.</summary>
    public const double ScaleMin = 0.25, ScaleMax = 2.0, SpeedMin = 0.5, SpeedMax = 2.0;

    /// <summary>A foot planted in the game's clip may slide this far beyond it in a root-motion scale's result before it warns (cm).</summary>
    public const double SlideWarnCm = 3;

    public static string Name(EditKind k) => k switch
    {
        EditKind.Mirror => "Mirror",
        EditKind.Scale => "Root-motion scale",
        EditKind.Speed => "Speed",
        EditKind.Trim => "Trim",
        _ => "Swap",
    };

    /// <summary>The cut a trial trim makes: a tenth off each end (the edit census's own trial), or the whole clip when it is too short.</summary>
    public static (int From, int To) TrialCut(int last)
    {
        int cut = Math.Max(1, last / 10);
        // a clip too short for a tenth off each end loses its last tick: a trial that cuts something (the whole clip would cut nothing)
        return last > 2 * cut ? (cut, last - cut) : last >= 2 ? (0, last - 1) : (0, last);
    }

    /// <summary>Why <paramref name="kind"/> can't be made on <paramref name="clip"/> by what is known before running it (the clip's record,
    /// the animation graph's verdict, the hand tracks, the swap sources), as the technical detail (<see cref="PlanBlock"/>), or null.</summary>
    public static string? PlanReason(EditKind kind, ClipEntry clip, EditContext ctx, int swapSources) => PlanBlock(kind, clip, ctx, swapSources)?.Detail;

    /// <summary>Why <paramref name="kind"/> can't be made on <paramref name="clip"/> by what is known before running it, with the kind of
    /// refusal (as the edit itself would give it: <see cref="EditVerdicts"/>), or null.</summary>
    public static EditBlock? PlanBlock(EditKind kind, ClipEntry clip, EditContext ctx, int swapSources)
    {
        var c = clip.Record;
        if (c is null) return new(EditRefusal.Unreadable, $"the clip's record can't be read ({clip.ParseError ?? "no record"})");
        if (!c.WalkedToEnd) return new(EditRefusal.Unreadable, "the clip's curves don't read to the record's end");
        if (!c.DurationValid) return new(EditRefusal.Unreadable, "the clip's length isn't one a clip may have");
        var v = ctx.Verdict;
        switch (kind)
        {
            case EditKind.Mirror:
            case EditKind.Scale:
                if (v is null) return new(EditRefusal.GraphIncomplete, "the animation graph wasn't read, so whether a motion field reads this clip's motion isn't known");
                if (v.MotionRefusal is { } m)
                    return new(!v.MotionFieldKnown ? EditRefusal.GraphIncomplete : v.ListedWithoutNodeUse ? EditRefusal.ListedWithoutNodeUse : EditRefusal.MotionField, m);
                if (kind == EditKind.Mirror && EditTracks.HandTracksOf(c) is { Count: > 0 } hand)
                    return new(EditRefusal.HandTracks,
                        $"it holds hand tracks ({string.Join(", ", hand.Select(h => h.ToString("X8")))}), which a mirror in place would leave aiming the hands at the old side");
                return null;
            case EditKind.Speed:
            case EditKind.Trim:
                if (v is null) return new(EditRefusal.GraphIncomplete, "the animation graph wasn't read, so what holds this clip's length isn't known");
                if (v.LengthRefusal is { } l)
                    return new(!v.Scanned || !v.GraphComplete ? EditRefusal.GraphIncomplete : v.ListedWithoutNodeUse ? EditRefusal.ListedWithoutNodeUse : EditRefusal.LengthHeld, l);
                if (kind == EditKind.Trim && c.LastTick < 2) return new(EditRefusal.TooShort, $"the clip lasts {c.LastTick} tick(s): nothing to trim");
                return null;
            default:
                if (swapSources > 0) return null;
                string? key;
                try { key = SwapSources.Key(c, ctx.Table); }
                catch (Exception ex) when (ex is not OperationCanceledException) { key = null; }
                return key is null
                    ? new(EditRefusal.Unreadable, "the clip's own curves don't all resolve against the shared curve table (or don't pair with its tracks one to one), so no source can be matched to its track list")
                    : new(EditRefusal.NoSwapSource, "no other clip in these game files has the same length and the identical track list (every hash and kind), which a swap needs");
        }
    }

    /// <summary>The warning on a swap when the animation graph wasn't read.</summary>
    public const string SwapGraphUnknownWarning = "whether a motion field uses this clip isn't known (the animation graph wasn't read)";

    /// <summary>The warning an edit would carry from the graph's verdict, or null. A swap with no verdict warns that the graph's answer
    /// isn't known, as an import does.</summary>
    public static string? WarningOf(EditKind kind, EditContext ctx) => ctx.Verdict switch
    {
        null => kind == EditKind.Swap ? SwapGraphUnknownWarning : null,
        var v when kind == EditKind.Swap => v.ReplaceWarning,
        var v when kind != EditKind.Trim => v.MotionWarning,
        _ => null,
    };

    /// <summary>Each edit's availability on <paramref name="clip"/>: <see cref="PlanReason"/>, then (with <paramref name="trial"/>) a run at
    /// the trial settings (<see cref="Starting"/>), whose refusal (any but a parameter out of range) is the reason. A swap's trial waits for its source.</summary>
    public static List<EditOption> Options(ClipEntry clip, EditContext ctx, SkeletonMirror? mirror, int swapSources, bool trial = true) =>
        Options(clip, ctx, mirror, swapSources, trial, CancellationToken.None);

    /// <summary>As <see cref="Options(ClipEntry, EditContext, SkeletonMirror?, int, bool)"/>, stopping with an
    /// <see cref="OperationCanceledException"/> between the edits once <paramref name="cancel"/> is set (another clip was chosen).</summary>
    public static List<EditOption> Options(ClipEntry clip, EditContext ctx, SkeletonMirror? mirror, int swapSources, bool trial, CancellationToken cancel)
    {
        var l = new List<EditOption>();
        foreach (var k in Enum.GetValues<EditKind>())
        {
            cancel.ThrowIfCancellationRequested();
            EditBlock? why;
            try
            {
                why = PlanBlock(k, clip, ctx, swapSources);
                if (why is null && trial && k != EditKind.Swap) why = TrialBlock(clip, Starting(k, clip), ctx, mirror);
            }
            // a fault of the code on this clip: this edit can't be told, the others still are
            catch (Exception ex) when (ex is not OperationCanceledException && !Transient(ex)) { why = Fault(ex); }
            l.Add(EditOption.Of(k, why, why is null ? WarningOf(k, ctx) : null));
        }
        return l;
    }

    /// <summary>The speed slider's step.</summary>
    public const double SpeedStep = 0.05;

    /// <summary>The speed slider's own steps, <see cref="SpeedMin"/> to <see cref="SpeedMax"/> by <see cref="SpeedStep"/>, without 1 (which
    /// changes nothing).</summary>
    public static readonly IReadOnlyList<double> SpeedGrid = Grid(SpeedMin, SpeedMax, SpeedStep);

    /// <summary>The factors <paramref name="from"/> to <paramref name="to"/> by <paramref name="step"/>, rounded to the step, without 1.</summary>
    public static IReadOnlyList<double> Grid(double from, double to, double step) =>
        Enumerable.Range(0, (int)Math.Round((to - from) / step) + 1).Select(i => Math.Round(from + i * step, 4)).Where(f => f != 1).ToList();

    /// <summary>The root-motion scale slider's step, and its own steps, <see cref="ScaleMin"/> to <see cref="ScaleMax"/>, without 1.</summary>
    public const double ScaleStep = 0.05;
    public static readonly IReadOnlyList<double> ScaleGrid = Grid(ScaleMin, ScaleMax, ScaleStep);

    /// <summary>The refusals that don't depend on the edit's setting (the factor or the cut): one of them at any setting is the answer for
    /// every setting, so no other is tried.</summary>
    public static bool AtEverySetting(EditRefusal? k) => k is EditRefusal.Unreadable or EditRefusal.GraphIncomplete or EditRefusal.LengthHeld
        or EditRefusal.MotionField or EditRefusal.HandTracks or EditRefusal.NoRootMotion or EditRefusal.TimesUnread or EditRefusal.ListedWithoutNodeUse
        or EditRefusal.NoMotion or EditRefusal.TooShort or EditRefusal.Unpaired;

    /// <summary>The factors of <paramref name="grid"/> (the slider's own steps by default) at which the speed change of
    /// <paramref name="clip"/> makes a record (<see cref="Factors"/>).</summary>
    public static FactorChoices Speeds(ClipEntry clip, EditContext ctx, CancellationToken cancel, IReadOnlyList<double>? grid = null, int? parallelism = null) =>
        Factors(EditKind.Speed, clip, ctx, cancel, grid ?? SpeedGrid, parallelism);

    /// <summary>The factors of <paramref name="grid"/> (the slider's own steps by default) at which the root-motion scale of
    /// <paramref name="clip"/> makes a record (<see cref="Factors"/>).</summary>
    public static FactorChoices Scales(ClipEntry clip, EditContext ctx, CancellationToken cancel, IReadOnlyList<double>? grid = null, int? parallelism = null) =>
        Factors(EditKind.Scale, clip, ctx, cancel, grid ?? ScaleGrid, parallelism);

    /// <summary>The factors of <paramref name="grid"/> at which <paramref name="kind"/> (speed or root-motion scale) makes a record of
    /// <paramref name="clip"/>, each run in turn (in parallel, stopping when <paramref name="cancel"/> is set): what the rebuild needs
    /// (the kinds of stored data in the clip's head, the keys a curve may hold) depends on the encoded curves at each factor, so no
    /// cheaper rule gives it. None tried when the plan refuses the edit; one tried when its refusal holds at every factor
    /// (<see cref="AtEverySetting"/>), for the same refusal at every one.</summary>
    public static FactorChoices Factors(EditKind kind, ClipEntry clip, EditContext ctx, CancellationToken cancel, IReadOnlyList<double> grid, int? parallelism = null,
        bool anyOnly = false)
    {
        cancel.ThrowIfCancellationRequested();
        if (PlanBlock(kind, clip, ctx, 0) is not null) return FactorChoices.None(kind);
        var order = grid.OrderBy(f => Math.Abs(f - (kind == EditKind.Speed ? 1.25 : 0.5))).ToList();
        if (RunBlock(clip, new EditRequest(kind, order[0]), ctx, null) is not { } first)
        {
            if (anyOnly) return new FactorChoices(kind, grid, [order[0]], []) { Complete = false };
        }
        else if (AtEverySetting(first.Kind)) return new FactorChoices(kind, grid, [], grid.Select(f => (f, first)).ToList());
        else if (anyOnly)
        {
            // whether any factor works, the nearest to the trial's first: enough for the install's list (the clip chosen is tried whole)
            var tried = new List<(double, EditBlock)> { (order[0], first) };
            foreach (var f in order.Skip(1))
            {
                cancel.ThrowIfCancellationRequested();
                if (RunBlock(clip, new EditRequest(kind, f), ctx, null) is { } b) tried.Add((f, b));
                else return new FactorChoices(kind, grid, [f], tried) { Complete = false };
            }
            return new FactorChoices(kind, grid, [], tried);
        }
        var blocks = new EditBlock?[grid.Count];
        Parallel.For(0, grid.Count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = parallelism ?? Environment.ProcessorCount },
            i => blocks[i] = RunBlock(clip, new EditRequest(kind, grid[i]), ctx, null));
        var works = grid.Where((_, i) => blocks[i] is null).ToList();
        var refused = grid.Select((f, i) => (f, blocks[i])).Where(x => x.Item2 is not null).Select(x => (x.f, x.Item2!)).ToList();
        return new FactorChoices(kind, grid, works, refused);
    }

    /// <summary>The cut points trims are tried at, as shares of the clip's length: each end kept or cut by a tenth, a quarter or a half.</summary>
    public static readonly IReadOnlyList<double> TrimPoints = [0, 0.1, 0.25, 0.5, 0.75, 0.9, 1];

    /// <summary>The cuts tried on a clip of <paramref name="lastTick"/> ticks: every pair of <see cref="TrimPoints"/>, from before to, that
    /// cuts something and keeps at least two ticks, each once.</summary>
    public static IReadOnlyList<(int From, int To)> TrimGrid(int lastTick)
    {
        var ticks = TrimPoints.Select(p => (int)Math.Round(p * lastTick)).Distinct().ToList();
        // a cut keeps at least two ticks, but in a clip of under four ticks one (so a two-tick clip still has cuts to try)
        int least = lastTick >= 4 ? 2 : 1;
        return [.. from a in ticks from b in ticks where b - a >= least && (a > 0 || b < lastTick) select (a, b)];
    }

    /// <summary>The cuts of <see cref="TrimGrid"/> at which the trim of <paramref name="clip"/> makes a record, each run in turn (in
    /// parallel); none tried when the plan refuses the trim, one when its refusal holds at every cut.</summary>
    public static TrimChoices Trims(ClipEntry clip, EditContext ctx, CancellationToken cancel, int? parallelism = null, bool anyOnly = false)
    {
        cancel.ThrowIfCancellationRequested();
        if (PlanBlock(EditKind.Trim, clip, ctx, 0) is not null) return TrimChoices.None;
        var grid = TrimGrid(clip.LastTick);
        if (grid.Count == 0) return TrimChoices.None;
        var trial = TrialCut(clip.LastTick);
        if (RunBlock(clip, new EditRequest(EditKind.Trim, 1, trial.From, trial.To), ctx, null) is not { } first)
        {
            if (anyOnly) return new TrimChoices(clip.LastTick, grid, [trial], []) { Complete = false };
        }
        else if (AtEverySetting(first.Kind)) return new TrimChoices(clip.LastTick, grid, [], grid.Select(c => (c, first)).ToList());
        else if (anyOnly)
        {
            var tried = new List<((int, int), EditBlock)>();
            foreach (var c in grid)
            {
                cancel.ThrowIfCancellationRequested();
                if (RunBlock(clip, new EditRequest(EditKind.Trim, 1, c.From, c.To), ctx, null) is { } b) tried.Add((c, b));
                else return new TrimChoices(clip.LastTick, grid, [c], tried) { Complete = false };
            }
            return new TrimChoices(clip.LastTick, grid, [], tried);
        }
        var blocks = new EditBlock?[grid.Count];
        Parallel.For(0, grid.Count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = parallelism ?? Environment.ProcessorCount },
            i => blocks[i] = RunBlock(clip, new EditRequest(EditKind.Trim, 1, grid[i].From, grid[i].To), ctx, null));
        var works = grid.Where((_, i) => blocks[i] is null).ToList();
        var refused = grid.Select((c, i) => (c, blocks[i])).Where(x => x.Item2 is not null).Select(x => (x.c, x.Item2!)).ToList();
        return new TrimChoices(clip.LastTick, grid, works, refused);
    }

    /// <summary>Each edit's availability on <paramref name="clip"/> as the Edit screen offers it: <see cref="Options(ClipEntry, EditContext, SkeletonMirror?, int, bool, CancellationToken)"/>,
    /// with speed, root-motion scale and trim available when they work at any factor of their sliders (<see cref="Speeds"/>,
    /// <see cref="Scales"/>) or any cut of the trim's grid (<see cref="Trims"/>), and refused, when they work at none, by the refusal most
    /// settings give. With <paramref name="full"/> false each stops at the first setting that works (enough for the install's list; the
    /// clip chosen on the screen is tried whole). Never throws but when cancelled: a fault of the edits' code on the clip is that edit's
    /// refusal, with no kind ("can't tell").</summary>
    public static ClipEditChoices Choices(ClipEntry clip, EditContext ctx, SkeletonMirror? mirror, int swapSources, CancellationToken cancel, int? parallelism = 1,
        bool full = true)
    {
        var options = Options(clip, ctx, mirror, swapSources, trial: true, cancel);
        var speeds = Guard(EditKind.Speed, () => Factors(EditKind.Speed, clip, ctx, cancel, SpeedGrid, parallelism, anyOnly: !full));
        var scales = Guard(EditKind.Scale, () => Factors(EditKind.Scale, clip, ctx, cancel, ScaleGrid, parallelism, anyOnly: !full));
        TrimChoices trims;
        try { trims = Trims(clip, ctx, cancel, parallelism, anyOnly: !full); }
        catch (Exception ex) when (ex is not OperationCanceledException and not AggregateException { InnerException: OperationCanceledException } && !Transient(ex))
        {
            var b = Fault(ex);
            trims = new TrimChoices(clip.LastTick, [(0, clip.LastTick)], [], [((0, clip.LastTick), b)]);
        }
        void Set(EditKind k, bool tried, bool any, EditBlock? main)
        {
            if (!tried) return;
            int at = options.FindIndex(o => o.Kind == k);
            options[at] = any ? EditOption.Of(k, null, WarningOf(k, ctx)) : EditOption.Of(k, main, null);
        }
        Set(EditKind.Speed, speeds.Grid.Count > 0, speeds.Works.Count > 0, speeds.Main);
        Set(EditKind.Scale, scales.Grid.Count > 0, scales.Works.Count > 0, scales.Main);
        Set(EditKind.Trim, trims.Grid.Count > 0, trims.Works.Count > 0, trims.Main);
        return new ClipEditChoices(options, speeds, scales, trims);

        // a fault of the code at a setting: the edit can't be told at any setting
        FactorChoices Guard(EditKind k, Func<FactorChoices> work)
        {
            try { return work(); }
            catch (Exception ex) when (ex is not OperationCanceledException and not AggregateException { InnerException: OperationCanceledException } && !Transient(ex))
            {
                return new FactorChoices(k, [1.5], [], [(1.5, Fault(ex))]);
            }
        }
    }

    /// <summary>A fault of the edits' code on a clip, as a refusal with no kind ("can't tell").</summary>
    public static EditBlock Fault(Exception ex) => new(null, $"the edit failed on this clip ({ex.GetType().Name}): {ex.Message}") { Error = ex };

    /// <summary>An error that says nothing of the clip: the machine ran short of memory or stack. Never a clip's fault: it is let through,
    /// and the work that met it is incomplete (worked out again on request). (The install closed under the work is a cancellation: the
    /// work is cancelled before the install is closed.)</summary>
    public static bool Transient(Exception ex) => (ex is AggregateException { InnerExceptions.Count: 1 } a ? a.InnerExceptions[0] : ex)
        is OutOfMemoryException or InsufficientExecutionStackException;

    /// <summary>The settings an edit's trial runs with (the edit census's own): a factor of 0.5 (scale), 1.25 (speed), the trial cut (trim).</summary>
    public static EditRequest Starting(EditKind k, ClipEntry clip) => k switch
    {
        EditKind.Scale => new EditRequest(k, 0.5),
        EditKind.Speed => new EditRequest(k, 1.25),
        EditKind.Trim => TrialCut(clip.LastTick) is var (f, t) ? new EditRequest(k, 1, f, t) : new EditRequest(k),
        _ => new EditRequest(k),
    };

    /// <summary>Why a run of <paramref name="r"/> is refused (the technical detail), or null when it makes a record.</summary>
    public static string? TrialReason(ClipEntry clip, EditRequest r, EditContext ctx, SkeletonMirror? mirror) => TrialBlock(clip, r, ctx, mirror)?.Detail;

    /// <summary>Why a run of <paramref name="r"/> is refused, with the kind of refusal (null for a fault), or null when it makes a record.
    /// A parameter out of range (<see cref="EditRefusal.BadParameter"/>) says nothing of the edit at another setting: null.</summary>
    public static EditBlock? TrialBlock(ClipEntry clip, EditRequest r, EditContext ctx, SkeletonMirror? mirror) =>
        RunBlock(clip, r, ctx, mirror) is { Kind: not EditRefusal.BadParameter } b ? b : null;

    /// <summary>Why a run of <paramref name="r"/> is refused, any refusal (a parameter out of range among them), or null when it makes a record.</summary>
    public static EditBlock? RunBlock(ClipEntry clip, EditRequest r, EditContext ctx, SkeletonMirror? mirror)
    {
        try { Run(clip, r, ctx, mirror, measure: false); return null; }
        catch (EditRefusedException ex) { return EditReasons.Of(clip.Name, ex); }
        catch (OperationCanceledException) { throw; }
        // anything else is a fault of the edit's code on this clip (but for a machine short of memory, say, let through): that edit can't be
        // told, never the whole screen's answer
        catch (Exception ex) when (!Transient(ex))
        {
            return Fault(ex);
        }
    }

    /// <summary>A refusal's message without the clip's name in front (the screen names the clip already).</summary>
    public static string Unnamed(string clip, string message) =>
        message.StartsWith(clip + ": ", StringComparison.Ordinal) ? message[(clip.Length + 2)..] : message;

    /// <summary>Runs the edit. Throws <see cref="EditRefusedException"/> when it is refused.</summary>
    public static EditedClip Run(ClipEntry clip, EditRequest r, EditContext ctx, SkeletonMirror? mirror, bool measure = true)
    {
        var c = clip.Record ?? throw new EditRefusedException(EditRefusal.Unreadable, $"{clip.Name}: the record can't be read");
        return r.Kind switch
        {
            EditKind.Mirror => MirrorEdit.Apply(c, ctx, mirror),
            EditKind.Scale => RootScaleEdit.Apply(c, ctx, (float)r.Factor, measure),
            EditKind.Speed => RetimeEdit.Speed(c, ctx, r.Factor),
            EditKind.Trim => RetimeEdit.Trim(c, ctx, r.From, r.To),
            _ => SwapEdit.Apply(c, (r.Source ?? throw new EditRefusedException(EditRefusal.BadParameter, $"{clip.Name}: no source clip was chosen")).Record
                ?? throw new EditRefusedException(EditRefusal.Unreadable, $"{r.Source.Name}: the record can't be read"), ctx),
        };
    }

    /// <summary>Runs the edit and checks its result against the clip. Throws <see cref="EditRefusedException"/> when it is refused.</summary>
    public static EditOutcome Apply(ClipEntry clip, EditRequest r, EditContext ctx, SkeletonMirror? mirror) =>
        Outcome(clip, r, Run(clip, r, ctx, mirror), ctx.Skeleton, ctx.Table, mirror);

    /// <summary>The checks on an edit's result: the body checks against the clip (feet in the floor, joint limits, speed, pops, limbs), the
    /// feet's slide for a root-motion scale, and each warning the graph's verdict gives the edit. A mirror's result is checked against the
    /// clip mirrored (see <see cref="Outcome(ClipEntry, EditRequest, EditedClip, Skeleton, Clips.SharedCurveTable?, SkeletonMirror?)"/>).</summary>
    public static EditOutcome Outcome(ClipEntry clip, EditRequest r, EditedClip e, Skeleton s, Clips.SharedCurveTable? table) =>
        Outcome(clip, r, e, s, table, null);

    /// <summary>As <see cref="Outcome(ClipEntry, EditRequest, EditedClip, Skeleton, Clips.SharedCurveTable?)"/>. A mirror's result is
    /// checked against the clip mirrored by <paramref name="mirror"/> (made on <paramref name="s"/> when null), so each bone is held to its
    /// counterpart's motion in the game's clip, its twist and sided angles the other way, not to its own; every other edit against the clip.</summary>
    public static EditOutcome Outcome(ClipEntry clip, EditRequest r, EditedClip e, Skeleton s, Clips.SharedCurveTable? table, SkeletonMirror? mirror)
    {
        var original = Motion.FromClip(s, clip.Record!, table);
        var edited = Motion.FromClip(s, e.Record, table);
        var reference = r.Kind == EditKind.Mirror ? original.Mirrored(mirror ?? new SkeletonMirror(s)) : original;
        var report = new CheckReport();
        report.AddRange(MotionChecks.Body(edited, reference));
        if (e is ScaledClip sc)
            report.Add(new CheckResult("feet during the plants", sc.ResidualSlideCm <= SlideWarnCm ? CheckLevel.Pass : CheckLevel.Warn, string.Create(CultureInfo.InvariantCulture,
                $"a planted foot slides at most {sc.ResidualSlideCm:F2} cm beyond the game's clip ({sc.UnlockedSlideCm:F2} cm without the foot lock; warn past {SlideWarnCm:F0} cm); the mover travels {sc.TravelCm.Before:F1} cm before, {sc.TravelCm.After:F1} after")));
        if (e.Warnings.Count == 0) report.Add(new CheckResult("animation graph", CheckLevel.Pass, "no warning: nothing the graph reads is known to depend on what this edit changes"));
        foreach (var w in e.Warnings) report.Add(new CheckResult("animation graph", CheckLevel.Warn, w));
        TimeWarp? warp = r.Kind switch
        {
            EditKind.Trim => TimeWarp.Trim(clip.LastTick, r.From, r.To),
            EditKind.Speed => TimeWarp.Speed(clip.LastTick, r.Factor),
            _ => null,
        };
        return new EditOutcome { Clip = clip, Request = r, Result = e, Original = original, Edited = edited, Warp = warp, Report = report };
    }
}
