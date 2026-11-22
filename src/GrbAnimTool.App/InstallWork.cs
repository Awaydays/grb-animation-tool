using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>The work of every clip's edit marks on an install (<see cref="InstallWork.Marks"/>): how far it is (0 to 1, read by the
/// screen's progress), and its end: the marks with how they were had (from the cache, or worked out), or null with why.</summary>
sealed class MarksJob : IProgress<double>
{
    double _progress;

    /// <summary>The share of the clips done, 0 to 1 (1 once the marks are had, from the cache or worked out).</summary>
    public double Progress => Volatile.Read(ref _progress);

    void IProgress<double>.Report(double value)
    {
        // reports come from the workers' threads in any order: the bar only goes forward
        double was;
        do { was = Volatile.Read(ref _progress); if (value <= was) return; }
        while (Interlocked.CompareExchange(ref _progress, value, was) != was);
    }

    public required Task<MarksResult> Done { get; init; }
}

/// <summary>The end of a <see cref="MarksJob"/>: the marks and how they were had (null: no cache was asked for), or null and why.</summary>
sealed record MarksResult(EditMarks? Marks, CacheOutcome? Outcome, TimeSpan Time, string? Error, string? SaveError);

/// <summary>What the screens read once per install, in the background, and share: the animation graph (null when it doesn't read), the
/// swap groups (null when they can't be built) and the skeleton's mirror; every clip's edit marks (from their cache when it holds this
/// install's); and the clip index the Library builds (null when it can't be built), for who plays each clip. None of the tasks faults.
/// An install opened (<see cref="Open"/>, called only with the install just opened) starts them again; a wait on the index of an install
/// no longer open ends with null, and a late call for such an install (any accessor but Open) gets nothing and switches nothing.</summary>
static class InstallWork
{
    static readonly object Lock = new();
    static GameInstall? _for;
    static Task<ClipGraph?>? _graph;
    static Task<SwapSources?>? _swaps;
    static Task<SkeletonMirror?>? _mirror;
    static MarksJob? _marks;
    static CancellationTokenSource? _marksRun;
    static TaskCompletionSource<ClipIndex?>? _index;

    /// <summary>Works out the marks for a job, in place of <see cref="EditMarks.Build"/> (for tests: a job that waits, or fails).</summary>
    internal static Func<GameInstall, IProgress<double>, CancellationToken, EditMarks>? BuildMarks;

    /// <summary>The install the screens now show (the Library opened it): its reads start, and the last install's stop. Only this switches
    /// the install: a call for any other (a late one, for an install already left) is answered with nothing and changes nothing. It is
    /// called with the install just opened only: by the Library before it hands the install to the screens, and by the screens as they
    /// take it (in the same turn of the window's thread, from the Library's InstallShown), so it never switches back to an older one.</summary>
    public static void Open(GameInstall g) { lock (Lock) Start(g); }

    /// <summary>Whether <paramref name="g"/> is the install open (<see cref="Open"/>).</summary>
    static bool IsOpen(GameInstall g) => ReferenceEquals(_for, g);

    static void Start(GameInstall g)
    {
        if (_for == g) return;
        _for = g;
        StartReads(g);
        // the old install's marks stop; the new install's start when a screen first asks for them
        _marksRun?.Cancel();
        _marksRun = null;
        _marks = null;
        _index?.TrySetResult(null);
        _index = new TaskCompletionSource<ClipIndex?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Starts the install's reads of the graph, the swap groups and the mirror. One that fails is null, and why is kept
    /// (<see cref="WhyNot"/>) and logged: an error the tool didn't foresee as such, one it foresees (a record it can't read) as a line, as
    /// it decides whether the marks are kept.</summary>
    static void StartReads(GameInstall g)
    {
        _whyNot = (null, null, null);
        // the reads of this start only: an earlier one (before a Retry) still running keeps its reason to itself
        int reads = ++_reads;
        var readGraph = ReadGraph ?? ClipGraph.Read;
        _graph = Read(() => readGraph(g), "while reading the game's animation graph", e => _whyNot = _whyNot with { Graph = e });
        _swaps = Read(() => SwapSources.Build(g.Clips, g.SharedTable), "while working out the swap sources", e => _whyNot = _whyNot with { Swaps = e });
        _mirror = Read(() => new SkeletonMirror(g.Skeleton), "while making the skeleton's mirror", e => _whyNot = _whyNot with { Mirror = e });

        Task<T?> Read<T>(Func<T> read, string where, Action<string> keep) where T : class => Task.Run(() =>
        {
            try { return read(); }
            catch (Exception ex)
            {
                var inner = ErrorReport.Inner(ex);
                lock (Lock) if (IsOpen(g) && reads == _reads) keep($"{inner.GetType().Name}: {inner.Message}");
                if (App.Foreseen(ex)) App.LogNote(where, $"{inner.GetType().Name}: {inner.Message}");
                else App.LogError(ex, where);
                return null;
            }
        });
    }

    /// <summary>Why the open install's graph, swap groups and mirror couldn't be had, each null when it was (or is still being read).</summary>
    static (string? Graph, string? Swaps, string? Mirror) _whyNot;

    /// <summary>How many times the reads were started (<see cref="StartReads"/>): a read's reason is kept only for its own start's.</summary>
    static int _reads;

    /// <summary>Why the open install's animation graph couldn't be read, or null (it read, isn't read yet, or <paramref name="g"/> isn't open).</summary>
    public static string? WhyNoGraph(GameInstall g) { lock (Lock) return IsOpen(g) ? _whyNot.Graph : null; }

    /// <summary>Reads the graph in place of <see cref="ClipGraph.Read"/> (for tests: one that fails).</summary>
    internal static Func<GameInstall, ClipGraph>? ReadGraph;

    /// <summary>For tests (one process, many installs of the same files): the clips the tool failed on are said again on the next read.</summary>
    internal static void ForgetToldForTests() { lock (ToldUnknown) ToldUnknown.Clear(); }

    /// <summary>The patch forges whose clips the tool failed on were logged for, this run: once each.</summary>
    static readonly HashSet<string> ToldUnknown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the clips the tool failed on are still to be said for <paramref name="g"/>'s patch forge this run (then they are, from
    /// now on).</summary>
    static bool Told(GameInstall g) { lock (ToldUnknown) return ToldUnknown.Add(g.PatchForgePath); }

    /// <summary>Works the open install's marks out again, from the start: the graph, the swap groups and the mirror read again (one of
    /// them may have failed for a reason that has passed), the cache passed over, the result kept in it when whole. The job running stops.
    /// For an install that isn't the one open, a job already ended.</summary>
    public static MarksJob Retry(GameInstall g, string? cacheFolder)
    {
        lock (Lock)
        {
            if (!IsOpen(g)) return Left();
            _marksRun?.Cancel();
            _marksRun = null;
            _marks = null;
            StartReads(g);
            return Marks(g, cacheFolder, fresh: true);
        }
    }

    /// <summary>The open install's animation graph (null: it doesn't read, or <paramref name="g"/> isn't the install open).</summary>
    public static Task<ClipGraph?> Graph(GameInstall g) { lock (Lock) return IsOpen(g) ? _graph! : Task.FromResult<ClipGraph?>(null); }
    public static Task<SwapSources?> Swaps(GameInstall g) { lock (Lock) return IsOpen(g) ? _swaps! : Task.FromResult<SwapSources?>(null); }
    public static Task<SkeletonMirror?> Mirror(GameInstall g) { lock (Lock) return IsOpen(g) ? _mirror! : Task.FromResult<SkeletonMirror?>(null); }

    /// <summary>Every clip's edit marks on the install, had once in the background: from the cache in <paramref name="cacheFolder"/> when
    /// it holds this install's (null: no cache, worked out for this run alone), else worked out after the graph, the swap groups and the
    /// mirror, on all but one of the processor's cores below the normal priority, and kept there. A failure (or another install opened
    /// meanwhile) ends with null and why, and isn't kept: the next call starts again. For an install that isn't the one open, a job
    /// already ended so. With <paramref name="fresh"/> (<see cref="Retry"/>) the cache isn't read, only written.</summary>
    public static MarksJob Marks(GameInstall g, string? cacheFolder, bool fresh = false)
    {
        lock (Lock)
        {
            if (!IsOpen(g)) return Left();
            if (_marks is not null) return _marks;
            var run = _marksRun = new CancellationTokenSource();
            var graph = _graph!; var swaps = _swaps!; var mirror = _mirror!;
            var build = BuildMarks;
            MarksJob? job = null;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            job = new MarksJob
            {
                Done = Task.Run(async () =>
                {
                    await started.Task;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var progress = (IProgress<double>)job!;
                        // the cache's read waits for nothing
                        var cache = cacheFolder is null ? null : new EditMarksCache(cacheFolder);
                        var outcome = fresh ? CacheOutcome.RebuiltOnRequest : CacheOutcome.Built;
                        if (!fresh && cache?.Read(g, out outcome) is { } kept)
                        {
                            progress.Report(1);
                            // the clips the tool failed on when the marks were worked out: said once a run, as a build says them (the
                            // errors themselves were logged then)
                            if (kept.Marks.Unknown.Count > 0 && Told(g))
                            {
                                var names = g.Clips.Where(c => kept.Marks.Unknown.Contains(c.Id)).Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
                                App.LogNote("while reading which edits each clip allows from the cache",
                                    $"the tool failed on {names.Count:N0} clip(s) when they were worked out, so which edits they allow isn't known: "
                                    + string.Join(", ", names.Take(20)) + (names.Count > 20 ? ", ..." : ""), counts: true);
                            }
                            return new MarksResult(kept.Marks, kept.Outcome, sw.Elapsed, null, null);
                        }
                        var fp = cache is null ? null : EditMarksFingerprint.Of(g);
                        // the graph, the swap groups and the mirror (none of them faults) are awaited, and the workers too: no thread waits
                        await Task.WhenAll(graph, swaps, mirror);
                        (string?, string?, string?) why;
                        lock (Lock) why = _whyNot;
                        var marks = build is not null ? await Task.Run(() => build(g, progress, run.Token))
                            : await EditMarks.BuildAsync(g, graph.Result, swaps.Result, mirror.Result, progress, run.Token, why);
                        if (marks.Unknown.Count > 0) Told(g);
                        progress.Report(1);
                        // the clips the tool failed on: the first few logged whole (the clip and where it failed), then how many in all
                        foreach (var (clip, error) in marks.Faults) App.LogCaught(error, $"while working out which edits {clip} allows");
                        // an error of the moment that left clips out: logged once, whole, and counted (the marks aren't whole, below)
                        if (marks.Transient is { } t) App.LogCaught(t, "while working out which edits each clip allows (some clips left out)");
                        if (marks.Unknown.Count > marks.Faults.Count)
                            App.LogNote("while working out which edits each clip allows", $"the tool failed on {marks.Unknown.Count:N0} clip(s) in all; the first {marks.Faults.Count} are logged above");
                        // marks that aren't whole aren't kept for this run either: Retry works them out again
                        if (marks.Incomplete is not null) lock (Lock) if (ReferenceEquals(_marks, job)) _marks = null;
                        if (cache is null) return new MarksResult(marks, null, sw.Elapsed, null, marks.Incomplete is { } notWhole ? $"they may not hold next time: {notWhole}" : null);
                        var r = cache.Save(g, fp!, marks, outcome, sw.Elapsed);
                        return new MarksResult(r.Marks, r.Outcome, r.Time, null, r.SaveError);
                    }
                    catch (Exception ex)
                    {
                        // not kept: a retry works them out again; an error not foreseen (a cancellation is) is logged
                        App.LogError(ex, "while working out which edits each clip allows");
                        lock (Lock) if (ReferenceEquals(_marks, job)) _marks = null;
                        return new MarksResult(null, null, sw.Elapsed, ex is OperationCanceledException or AggregateException { InnerException: OperationCanceledException }
                            ? LeftError : $"{ex.GetType().Name}: {ex.Message}", null);
                    }
                }),
            };
            _marks = job;
            started.SetResult();
            return job;
        }
    }

    /// <summary>Why the marks of an install left end with none.</summary>
    const string LeftError = "another game folder was opened";

    /// <summary>A job for an install no longer open: already ended, with no marks.</summary>
    static MarksJob Left() => new() { Done = Task.FromResult(new MarksResult(null, null, TimeSpan.Zero, LeftError, null)) };

    /// <summary>The install's clip index once the Library has it (<see cref="IndexDone"/>), or null when it couldn't be built or
    /// <paramref name="g"/> isn't the install open.</summary>
    public static Task<ClipIndex?> Index(GameInstall g) { lock (Lock) return IsOpen(g) ? _index!.Task : Task.FromResult<ClipIndex?>(null); }

    /// <summary>The Library's clip index of <paramref name="g"/> is ready (null: it couldn't be built); nothing for an install left.</summary>
    public static void IndexDone(GameInstall g, ClipIndex? index) { lock (Lock) if (IsOpen(g)) _index!.TrySetResult(index); }
}
