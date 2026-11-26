using System.Collections.Concurrent;
using System.Globalization;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Library;

/// <summary>The factors of a slider (speed, root-motion scale) a clip's rebuild accepts, worked out by running the edit at each of the
/// slider's own steps (<see cref="EditJobs.Factors"/>): what the rebuild needs (the kinds of stored data in the clip's head, the keys
/// a curve may hold, the length) depends on the encoded curves at each factor, so no cheaper rule gives it exactly.</summary>
/// <param name="Kind">The edit: speed or root-motion scale.</param>
/// <param name="Grid">The factors tried, in order.</param>
/// <param name="Works">The factors the rebuild accepts, in order.</param>
/// <param name="Refused">Each factor refused, with why.</param>
public sealed record FactorChoices(EditKind Kind, IReadOnlyList<double> Grid, IReadOnlyList<double> Works, IReadOnlyList<(double Factor, EditBlock Block)> Refused)
{
    /// <summary>No factor tried (the plan refused the edit before any run).</summary>
    public static FactorChoices None(EditKind kind) => new(kind, [], [], []);

    /// <summary>Every factor of the grid was tried; false when the trying stopped at the first that works (an install's list needs no
    /// more): <see cref="Works"/> then holds that one only.</summary>
    public bool Complete { get; init; } = true;

    /// <summary>Every factor tried works.</summary>
    public bool All => Complete && Grid.Count > 0 && Works.Count == Grid.Count;

    /// <summary>The refusal most factors refused give (the earliest of them at a tie), or null when none was refused.</summary>
    public EditBlock? Main => Refused.Count == 0 ? null
        : Refused.GroupBy(r => r.Block.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.First().Factor).First().First().Block;

    /// <summary>The factors that work as runs of neighbouring grid steps: (from, to) each.</summary>
    public IReadOnlyList<(double From, double To)> Runs()
    {
        var runs = new List<(double, double)>();
        int i = 0;
        while (i < Grid.Count)
        {
            if (!Works.Contains(Grid[i])) { i++; continue; }
            int j = i;
            while (j + 1 < Grid.Count && Works.Contains(Grid[j + 1])) j++;
            runs.Add((Grid[i], Grid[j]));
            i = j + 1;
        }
        return runs;
    }

    static string X(double f) => f.ToString("0.0#", CultureInfo.InvariantCulture) + "x";

    /// <summary>What works, in plain words, when some factors don't: "This clip's speed can be set from 1.05x to 1.95x." with what goes
    /// wrong at the others; null when every factor works or none does.</summary>
    public string? Plain()
    {
        if (!Complete || All || Works.Count == 0) return null;
        var runs = Runs().Select(r => r.From == r.To ? $"to {X(r.From)}" : $"from {X(r.From)} to {X(r.To)}").ToList();
        string where = runs.Count == 1 ? runs[0] : string.Join(", ", runs.Take(runs.Count - 1)) + " or " + runs[^1];
        string what = Kind == EditKind.Speed ? "speed" : "root-motion scale";
        return $"This clip's {what} can be set {where}. At the other {(Kind == EditKind.Speed ? "speeds" : "scales")} {EditReasons.Why(Kind, Main!.Kind)}.";
    }

    /// <summary>The working factor nearest <paramref name="f"/> (the lower at a tie), or <paramref name="f"/> when none works.</summary>
    public double Nearest(double f) => Works.Count == 0 ? f : Works.OrderBy(w => Math.Abs(w - f)).ThenBy(w => w).First();
}

/// <summary>The cuts a clip's trim can make, tried on a grid of cut points (<see cref="EditJobs.TrimGrid"/>): what the rebuild needs
/// depends on the cut, as it does on a speed. A cut keeps the clip's start (from tick 0), its end (to its last tick), or neither.</summary>
/// <param name="LastTick">The clip's last tick.</param>
/// <param name="Grid">The cuts tried.</param>
/// <param name="Works">The cuts the rebuild accepts.</param>
/// <param name="Refused">Each cut refused, with why.</param>
public sealed record TrimChoices(int LastTick, IReadOnlyList<(int From, int To)> Grid, IReadOnlyList<(int From, int To)> Works,
    IReadOnlyList<((int From, int To) Cut, EditBlock Block)> Refused)
{
    /// <summary>No cut tried (the plan refused the trim, or the clip is too short).</summary>
    public static readonly TrimChoices None = new(0, [], [], []);

    /// <summary>Every cut of the grid was tried; false when the trying stopped at the first that works.</summary>
    public bool Complete { get; init; } = true;

    /// <summary>Every cut tried works.</summary>
    public bool All => Complete && Grid.Count > 0 && Works.Count == Grid.Count;

    /// <summary>The refusal most cuts refused give, or null when none was refused.</summary>
    public EditBlock? Main => Refused.Count == 0 ? null : Refused.GroupBy(r => r.Block.Kind).OrderByDescending(g => g.Count()).First().First().Block;

    /// <summary>A cut's kind: 0 keeps the start (cuts the end only), 1 keeps the end (cuts the start only), 2 cuts both ends.</summary>
    public int KindOf((int From, int To) c) => c.From == 0 ? 0 : c.To == LastTick ? 1 : 2;

    static readonly string[] KindNames = ["keep the clip's start", "keep the clip's end", "shorten both ends"];

    /// <summary>Whether, of the cuts tried, only those that keep the clip's start work (a sample: a later start the grid doesn't hold may
    /// still work, so the handles stay free and the exact cut is tried).</summary>
    public bool StartOnly => Complete && Works.Count > 0 && Works.All(c => c.From == 0);

    /// <summary>Whether, of the cuts tried, only those that keep the clip's end work.</summary>
    public bool EndOnly => Complete && Works.Count > 0 && Works.All(c => c.To == LastTick);

    /// <summary>What works, in plain words, when some trims don't: "Of the trims tried, trims that keep the clip's start can be made. The
    /// others can't: ...", with what goes wrong at the others; null when every cut tried works or none does.</summary>
    public string? Plain()
    {
        if (!Complete || All || Works.Count == 0) return null;
        var parts = new List<string>();
        for (int k = 0; k < 3; k++)
        {
            int tried = Grid.Count(c => KindOf(c) == k), works = Works.Count(c => KindOf(c) == k);
            if (tried == 0 || works == 0) continue;
            parts.Add((works == tried ? "trims that " : "some trims that ") + KindNames[k]);
        }
        string can = string.Join(parts.Count > 2 ? ", " : " and ", parts);
        return $"Of the trims tried, {can} can be made. The others can't: {EditReasons.Why(EditKind.Trim, Main!.Kind)}. "
            + "Each trim is tried when the handles stop.";
    }
}

/// <summary>Each edit's availability on a clip, as the Edit screen offers it (<see cref="EditJobs.Choices"/>), with the speeds, the
/// root-motion scales and the cuts that work.</summary>
public sealed record ClipEditChoices(IReadOnlyList<EditOption> Options, FactorChoices Speeds, FactorChoices Scales, TrimChoices Trims)
{
    public EditOption this[EditKind k] => Options.First(o => o.Kind == k);

    /// <summary>At least one edit can be made.</summary>
    public bool Any => Options.Any(o => o.Available);

    /// <summary>The edits this clip allows as the Edit screen's list marks them: a letter for each edit allowed (M mirror, R root-motion
    /// scale, S speed, T trim, W swap), a dot for each one not.</summary>
    public string Marks => EditMarks.Text(EditMarks.MaskOf(this));

    public static string Letter(EditKind k) => k switch
    {
        EditKind.Mirror => "M", EditKind.Scale => "R", EditKind.Speed => "S", EditKind.Trim => "T", _ => "W",
    };

    /// <summary>The marks' meaning, for their tooltip: the edits allowed by name, or that none is.</summary>
    public string MarksText => EditMarks.Meaning(EditMarks.MaskOf(this));
}

/// <summary>Every clip's edit choices on an install (<see cref="EditJobs.Choices"/>), worked out once in the background (the Edit
/// screen's list marks and filters by them), and the clips that allow no edit, by why.</summary>
public sealed class InstallEditChoices
{
    readonly Dictionary<ulong, ClipEditChoices> _of;

    InstallEditChoices(Dictionary<ulong, ClipEditChoices> of, Exception? transient = null) => (_of, Transient) = (of, transient);

    /// <summary>The first error that said nothing of the clips it met (<see cref="EditJobs.Transient"/>: memory or stack short): those
    /// clips aren't in the choices, and the work is to be done again; null when none came.</summary>
    public Exception? Transient { get; }

    /// <summary>The clips worked out: one of each ID (two records of one ID are one clip to the install, the first found:
    /// <see cref="GameInstall.FindClip(ulong)"/>).</summary>
    /// <para>Each row of a file of the unpacked folder that the forge doesn't hold is worked out too, by its own record (its
    /// <see cref="ClipEntry.Key"/>).</para>
    public static IReadOnlyList<ClipEntry> ClipsOf(GameInstall g) => [.. g.ForgeClips.DistinctBy(c => c.Id), .. g.Clips.Where(c => c.FromFile)];

    /// <summary>Choices already worked out, by clip ID.</summary>
    public static InstallEditChoices From(IReadOnlyDictionary<ulong, ClipEditChoices> of) => new(new Dictionary<ulong, ClipEditChoices>(of));

    public int Count => _of.Count;

    public ClipEditChoices? For(ClipEntry c) => _of.GetValueOrDefault(c.Key);

    public IEnumerable<ClipEditChoices> All => _of.Values;

    /// <summary>Every clip's choices, by clip ID.</summary>
    public IReadOnlyDictionary<ulong, ClipEditChoices> Clips => _of;

    /// <summary>How many clips allow no edit.</summary>
    public int NoEdit => _of.Values.Count(c => !c.Any);

    /// <summary>Works out every clip's choices, in parallel, with the install's graph (null: not read), swap groups and mirror; with
    /// <paramref name="full"/> false (the screen's list) each slider and the trim stop at the first setting that works, the clip chosen on
    /// the screen being tried whole. A clip whose work fails in a way no edit foresees gets that failure as every edit's refusal ("can't
    /// tell"), never the whole install's answer. <paramref name="progress"/> hears the share of the clips done (0 to 1, in steps of about a
    /// hundredth, from the workers' threads). The work runs on the thread pool; the program's own background work uses
    /// <see cref="OfInBackground"/>.</summary>
    public static InstallEditChoices Of(GameInstall g, ClipGraph? graph, SwapSources? swaps, SkeletonMirror? mirror, CancellationToken cancel, int? parallelism = null,
        bool full = false, IProgress<double>? progress = null, IReadOnlyList<ClipEntry>? only = null)
    {
        var of = new ConcurrentDictionary<ulong, ClipEditChoices>();
        var transient = new System.Runtime.CompilerServices.StrongBox<Exception?>();
        var clips = only ?? ClipsOf(g);
        var one = Worker(g, clips.Count, graph, swaps, mirror, cancel, full, progress, of, transient);
        Parallel.ForEach(clips, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, parallelism ?? Environment.ProcessorCount - 1) }, one);
        return new InstallEditChoices(new Dictionary<ulong, ClipEditChoices>(of), transient.Value);
    }

    /// <summary>As <see cref="Of"/> with its background workers: threads of their own below the normal priority (all but one of the
    /// processor's cores, or <paramref name="parallelism"/>), each taking the next clip until none is left or the work is cancelled, the
    /// last to end completing the task; no thread is held waiting for them. The task ends cancelled (an
    /// <see cref="OperationCanceledException"/>) when the work is cancelled, and faults when a worker's work fails or a worker can't be
    /// started (no memory or threads left): then once the workers already started have ended, never left waiting.</summary>
    public static Task<InstallEditChoices> OfInBackground(GameInstall g, ClipGraph? graph, SwapSources? swaps, SkeletonMirror? mirror, CancellationToken cancel,
        int? parallelism = null, bool full = false, IProgress<double>? progress = null)
    {
        var of = new ConcurrentDictionary<ulong, ClipEditChoices>();
        var transient = new System.Runtime.CompilerServices.StrongBox<Exception?>();
        var clips = ClipsOf(g);
        var one = Worker(g, clips.Count, graph, swaps, mirror, cancel, full, progress, of, transient);
        int total = clips.Count, workers = Math.Max(1, parallelism ?? Environment.ProcessorCount - 1), next = -1, running = workers;
        Exception? failed = null;
        var ended = new TaskCompletionSource<InstallEditChoices>(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        try
        {
            for (; started < workers; started++) Start(started);
        }
        catch (Exception ex)
        {
            // a worker that couldn't be started (no memory or threads left): the work fails with it, once the workers started have ended
            Interlocked.CompareExchange(ref failed, ex, null);
            if (Interlocked.Add(ref running, started - workers) == 0)
                ended.TrySetException(new InvalidOperationException($"working out the edits failed: {ex.Message}", ex));
        }
        return ended.Task;

        void Start(int i)
        {
            var t = (NewThreadForTests.Value ?? (body => new Thread(body)))(() =>
            {
                // nothing a worker does escapes its thread, its end included: a fault is kept for the task, a cancellation ends the work
                try
                {
                    try
                    {
                        int at;
                        // the work stops early once it has failed (a worker that couldn't start, or another's fault): it can't end well
                        while (!cancel.IsCancellationRequested && Volatile.Read(ref failed) is null && (at = Interlocked.Increment(ref next)) < total) one(clips[at]);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Interlocked.CompareExchange(ref failed, ex, null); }
                    if (Interlocked.Decrement(ref running) > 0) return;
                    if (cancel.IsCancellationRequested) ended.TrySetCanceled(cancel);
                    else if (failed is not null) ended.TrySetException(new InvalidOperationException($"working out the edits failed: {failed.Message}", failed));
                    else ended.TrySetResult(new InstallEditChoices(new Dictionary<ulong, ClipEditChoices>(of), transient.Value));
                }
                catch (Exception ex) { ended.TrySetException(ex); }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = $"Edit marks {i + 1}";
            t.Start();
        }
    }

    /// <summary>For tests: makes the workers' threads in place of <c>new Thread</c> (one that throws stands for a thread that can't be
    /// had), for the calls made in the test's own flow only; null makes them as the program does.</summary>
    internal static readonly AsyncLocal<Func<ThreadStart, Thread>?> NewThreadForTests = new();

    /// <summary>The work on one clip: its choices into <paramref name="of"/>, a failure of the edits' code as every edit's refusal ("can't
    /// tell", <see cref="Failed"/>), an <see cref="OperationCanceledException"/> let through only when <paramref name="cancel"/> is set (one
    /// from anywhere else is the clip's failure, never a clip silently left out), an error that says nothing of the clip
    /// (<see cref="EditJobs.Transient"/>) kept in <paramref name="transient"/>, the clip left out; then the progress.</summary>
    static Action<ClipEntry> Worker(GameInstall g, int total, ClipGraph? graph, SwapSources? swaps, SkeletonMirror? mirror, CancellationToken cancel, bool full,
        IProgress<double>? progress, ConcurrentDictionary<ulong, ClipEditChoices> of, System.Runtime.CompilerServices.StrongBox<Exception?> transient)
    {
        int done = 0, step = Math.Max(1, total / 100);
        return c =>
        {
            try
            {
                var ctx = new EditContext(g.Skeleton, g.SharedTable, graph?.Verdict(c.Id));
                of[c.Key] = ChoicesForTests?.Invoke(g, c) ?? EditJobs.Choices(c, ctx, mirror, swaps?.For(c).Count ?? 0, cancel, full: full);
            }
            catch (Exception ex) when (EditJobs.Transient(ex)) { Interlocked.CompareExchange(ref transient.Value, ex, null); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested) { of[c.Key] = Failed(ex); }
            int n = Interlocked.Increment(ref done);
            if (progress is not null && (n % step == 0 || n == total)) progress.Report((double)n / total);
        };
    }

    /// <summary>For tests: a clip's choices in place of the edits' own (or an error they throw), for the install it is made for; null
    /// falls through to the edits.</summary>
    internal static Func<GameInstall, ClipEntry, ClipEditChoices?>? ChoicesForTests;

    /// <summary>A clip whose choices couldn't be worked out: every edit refused with the failure, with no kind ("can't tell").</summary>
    public static ClipEditChoices Failed(Exception ex)
    {
        var b = new EditBlock(null, $"the tool failed on this clip ({ex.GetType().Name}): {ex.Message}") { Error = ex };
        return new ClipEditChoices(Enum.GetValues<EditKind>().Select(k => EditOption.Of(k, b, null)).ToList(),
            FactorChoices.None(EditKind.Speed), FactorChoices.None(EditKind.Scale), TrimChoices.None);
    }

    /// <summary>How many clips couldn't be told for some edit (a fault of the edits' code on the clip).</summary>
    public int Faults => _of.Values.Count(IsFault);

    /// <summary>Whether some edit of the clip couldn't be told (a fault of the edits' code on it, no refusal of a kind).</summary>
    public static bool IsFault(ClipEditChoices c) => c.Options.Any(o => !o.Available && o.Refusal is null);

    /// <summary>The clips that allow no edit, grouped by each edit's kind of refusal in order (mirror, scale, speed, trim, swap), the
    /// largest group first: "Mirror HandTracks, Scale NoRootMotion, Speed TimesUnread, Trim TimesUnread, Swap NoSwapSource: 1,234".</summary>
    public IEnumerable<(string Reasons, int Clips)> NoEditByReason() => _of.Values.Where(c => !c.Any)
        .GroupBy(c => string.Join(", ", c.Options.Select(o => $"{EditJobs.Name(o.Kind)} {o.Refusal?.ToString() ?? "fault"}")))
        .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal);
}
