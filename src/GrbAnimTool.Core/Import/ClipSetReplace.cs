using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Import;

/// <summary>A source for one clip of a set: another game clip, or a .glb.</summary>
public sealed record SetSource(ClipEntry? Clip, string? Glb)
{
    public static SetSource Of(ClipEntry c) => new(c, null);
    public static SetSource OfGlb(string path) => new(null, Path.GetFullPath(path));
    public string Name => Clip?.Name ?? Path.GetFileName(Glb!);
    /// <summary>The key two clips share when they take the same source.</summary>
    public string Key => Clip is { } c ? $"clip {c.Key}" : $"glb {Glb}";
}

/// <summary>How a set is replaced: the fit for every clip (its mode, the loop, the root, keeping planted feet; a cut's start is
/// suggested, or given per clip in <see cref="Starts"/>), whether clips sharing one long source each take their own piece of it
/// (<see cref="DistinctPieces"/>; off for a blended set, whose clips need matching poses), and whether the set is lined up
/// (<see cref="LineUp"/>) on the pose closest to all the pieces or on a chosen one (<see cref="PoseSource"/> at <see cref="PoseFrame"/>).
/// <see cref="SwitchTicks"/> is the game's blend between the set's clips, which the feet slide check plays.</summary>
public sealed record SetReplaceOptions
{
    public FitOptions Fit { get; init; } = new();
    public bool DistinctPieces { get; init; } = true;
    public bool LineUp { get; init; } = true;
    public SetSource? PoseSource { get; init; }
    public int PoseFrame { get; init; }
    /// <summary>A cut's start per clip, in the slots' order; a null entry (or no list) is suggested.</summary>
    public IReadOnlyList<int?>? Starts { get; init; }
    public int SwitchTicks { get; init; } = DefaultSwitchTicks;
    /// <summary>The blend between a set's clips when the graph doesn't say: 0.3 s.</summary>
    public const int DefaultSwitchTicks = 18;
}

/// <summary>A set replaced: each clip's result, in the slots' order, the starts the cuts took, and what was done.</summary>
public sealed record SetReplaceResult(IReadOnlyList<ImportResult> Results, IReadOnlyList<int?> CutStarts, IReadOnlyList<string> Notes);

/// <summary>Replace for a set: every clip given (a whole set, or clips the player picked) takes the motion of its source (one source for
/// all, or one per clip), fitted to its own exact length with the same options, each clip's record kept as a single Replace keeps it
/// (<see cref="ClipReplace"/>). With one long source and Cut, each clip gets its own piece (<see cref="ClipFit.SuggestPieces"/>); with
/// line-up every clip starts and ends at one shared pose, so the game's switches between them don't jump.</summary>
public static class ClipSetReplace
{
    /// <summary>Every clip of a set on one skeleton, and every clip source on it too or on one that maps onto it
    /// (<see cref="GameInstall.ReplaceProblem"/>); else refused, the message naming both skeletons.</summary>
    public static SetReplaceResult Run(GameInstall g, ClipGraph? graph, IReadOnlyList<ClipEntry> slots, IReadOnlyList<SetSource> sources, SetReplaceOptions o) =>
        Run(g, graph, slots, sources, o, planOnly: false);

    /// <summary>The cut starts the set's Replace would take with these options and no start given (Suggest: the whole distribution
    /// anew), and its notes; nothing is made. A start is null for a clip that takes no cut (its source of its own length, or no Cut).</summary>
    public static SetReplaceResult Suggest(GameInstall g, ClipGraph? graph, IReadOnlyList<ClipEntry> slots, IReadOnlyList<SetSource> sources, SetReplaceOptions o) =>
        Run(g, graph, slots, sources, o with { Starts = null }, planOnly: true);

    static SetReplaceResult Run(GameInstall g, ClipGraph? graph, IReadOnlyList<ClipEntry> slots, IReadOnlyList<SetSource> sources, SetReplaceOptions o, bool planOnly)
    {
        if (slots.Count == 0) throw new ImportRefusedException("no clip chosen for the set.", ImportRefusal.Slot);
        foreach (var s in slots)
        {
            if (g.ReplaceProblem(s, null) is { } why) throw new ImportRefusedException(why, ImportRefusal.Slot);
            if (s.OnSkeleton != slots[0].OnSkeleton)
                throw new ImportRefusedException($"{slots[0].Name} is on {g.SkeletonName(slots[0].OnSkeleton)} and {s.Name} on {g.SkeletonName(s.OnSkeleton)}: a set's clips are replaced on one skeleton.", ImportRefusal.Slot);
        }
        for (int i = 0; i < slots.Count; i++)
            if ((sources.Count == 1 ? sources[0] : i < sources.Count ? sources[i] : null)?.Clip is { } src && g.ReplaceProblem(slots[i], src) is { } why)
                throw new ImportRefusedException(why, ImportRefusal.File);
        var sk = g.SkeletonOf(slots[0])!;
        return Run(sk, g.SharedTable, graph, slots, sources, o, (slot, glb, f) => ClipImport.Run(g, slot, glb, graph, f), planOnly);
    }

    internal static SetReplaceResult Run(Skeleton s, Clips.SharedCurveTable? table, ClipGraph? graph, IReadOnlyList<ClipEntry> slots, IReadOnlyList<SetSource> sources,
        SetReplaceOptions o, Func<ClipEntry, string, FitOptions, ImportResult> glbImport, bool planOnly = false)
    {
        if (slots.Count == 0) throw new ImportRefusedException("no clip chosen for the set.", ImportRefusal.Slot);
        if (sources.Count != 1 && sources.Count != slots.Count)
            throw new ImportRefusedException($"one source for the set, or one per clip: {sources.Count} sources for {slots.Count} clips.", ImportRefusal.File);
        if (slots.Select(c => c.Id).Distinct().Count() != slots.Count) throw new ImportRefusedException("a clip is chosen twice in the set.", ImportRefusal.Slot);
        SetSource SourceOf(int i) => sources.Count == 1 ? sources[0] : sources[i];
        ImportResult On(ClipEntry slot, SetSource src, FitOptions f) =>
            src.Clip is { } c ? ClipReplace.FromClip(s, table, slot, c, f, graph?.Verdict(slot.Id), graph is not null) : glbImport(slot, src.Glb!, f);
        ImportResult One(int i, FitOptions f) => On(slots[i], SourceOf(i), f);
        // each source's motion at its own length, read once (a hold with no loop leaves it as it is)
        var unfitted = new Dictionary<string, Motion>();
        Motion Unfitted(SetSource src)
        {
            if (unfitted.TryGetValue(src.Key, out var m)) return m;
            var raw = On(slots[0], src, new FitOptions { Mode = FitMode.Hold }).Unfitted
                ?? throw new ImportRefusedException($"{src.Name}'s motion couldn't be read.", ImportRefusal.File);
            return unfitted[src.Key] = raw;
        }
        var notes = new List<string>();
        var given = o.Starts;
        if (given is not null && given.Count != slots.Count)
            throw new ImportRefusedException($"{given.Count} starts given for {slots.Count} clips.", ImportRefusal.Length);
        var starts = new int?[slots.Count];
        bool suggested = false;
        // the cuts: a start given is taken as it is; the others are suggested, clips sharing a source each its own piece of it, against the
        // line-up's pose when there is one
        void Distribute((Motion Motion, int Frame)? against)
        {
            if (o.Fit.Mode != FitMode.Cut) return;
            foreach (var group in Enumerable.Range(0, slots.Count).GroupBy(i => SourceOf(i).Key))
            {
                var idx = group.ToList();
                var m = Unfitted(SourceOf(idx[0]));
                var need = idx.Where(i => slots[i].LastTick != m.LastTick).ToList();
                foreach (int i in need)
                    if (given?[i] is int g)
                    {
                        if (g < 0 || g > m.LastTick - slots[i].LastTick)
                            throw new ImportRefusedException($"{slots[i].Name}'s cut of {slots[i].LastTick} ticks starts at frame 0 to {Math.Max(0, m.LastTick - slots[i].LastTick)} of {SourceOf(i).Name}, not {g}.", ImportRefusal.Length);
                        starts[i] = g;
                    }
                var free = need.Where(i => given?[i] is null).ToList();
                if (free.Count == 0) continue;
                suggested = true;
                try
                {
                    if (o.DistinctPieces && free.Count > 1)
                    {
                        var (st, _) = ClipFit.SuggestPieces(m, free.Select(i => slots[i].LastTick).ToList(), against);
                        for (int k = 0; k < free.Count; k++) starts[free[k]] = st[k];
                    }
                    else
                        foreach (int i in free) starts[i] = ClipFit.SuggestPieces(m, [slots[i].LastTick], against).Starts[0];
                }
                catch (ArgumentException ex) { throw new ImportRefusedException(ex.Message, ImportRefusal.Length, ex); }
            }
        }
        (Motion Motion, int Frame)? poseAt = null;
        string poseWords = "";
        if (o.LineUp && o.PoseSource is { } ps)
        {
            var pm = Unfitted(ps);
            if (o.PoseFrame < 0 || o.PoseFrame > pm.LastTick) throw new ImportRefusedException($"the pose's frame {o.PoseFrame} is outside {ps.Name} (0 to {pm.LastTick}).", ImportRefusal.Length);
            poseAt = (pm, o.PoseFrame);
            poseWords = $"lined up on {ps.Name}'s frame {o.PoseFrame}";
            Distribute(poseAt);
        }
        else if (o.LineUp)
        {
            // the pose closest to all the pieces, and the pieces closest to it, in turn until neither changes
            Distribute(null);
            (string Key, int Frame)? had = null;
            for (int round = 0; round < 4; round++)
            {
                var (src, frame) = ClosestPose();
                if (had == (src.Key, frame)) break;
                had = (src.Key, frame);
                poseAt = (Unfitted(src), frame);
                poseWords = $"lined up on the pose closest to all the pieces: frame {frame} of {src.Name}";
                if (!suggested) break;
                Distribute(poseAt);
            }
        }
        else Distribute(null);
        if (poseWords.Length > 0) notes.Add(poseWords);

        // each clip's first and last frame in its source as the fit takes them
        (Motion M, int First, int Last) Ends(int i)
        {
            var m = Unfitted(SourceOf(i));
            int l = slots[i].LastTick;
            if (m.LastTick == l || o.Fit.Mode == FitMode.Speed) return (m, 0, m.LastTick);
            if (o.Fit.Mode == FitMode.Cut && starts[i] is int st) return (m, st, st + l);
            return (m, 0, Math.Min(l, m.LastTick));
        }
        // the frame of the sources used whose pose and feet lie closest to every piece's first and last frame, the first of equals
        (SetSource Src, int Frame) ClosestPose()
        {
            var ends = Enumerable.Range(0, slots.Count).Select(Ends).ToList();
            var srcs = Enumerable.Range(0, slots.Count).Select(SourceOf).DistinctBy(x => x.Key).ToList();
            int frames = srcs.Sum(x => Unfitted(x).Frames);
            int step = Math.Max(1, frames / 20000);
            (SetSource, int) best = (srcs[0], 0); double bd = double.MaxValue;
            foreach (var x in srcs)
            {
                var m = Unfitted(x);
                for (int f = 0; f <= m.LastTick; f += step)
                {
                    double d = 0;
                    foreach (var (em, a, z) in ends) d += ClipFit.MatchDistance(m, f, em, a) + ClipFit.MatchDistance(m, f, em, z);
                    if (d < bd - 1e-4) { bd = d; best = (x, f); }
                }
            }
            return best;
        }

        if (o.Fit.Mode == FitMode.Cut)
        {
            for (int i = 0; i < slots.Count; i++)
                if (starts[i] is int st) notes.Add($"{slots[i].Name}: source frames {st} to {st + slots[i].LastTick} of {SourceOf(i).Name}");
            foreach (var group in Enumerable.Range(0, slots.Count).Where(i => starts[i] is not null).GroupBy(i => SourceOf(i).Key))
            {
                var idx = group.ToList();
                if (idx.Count < 2) continue;
                var m = Unfitted(SourceOf(idx[0]));
                var lasts = idx.Select(i => slots[i].LastTick).ToList();
                int shared = ClipFit.SharedTicks(idx.Select(i => starts[i]!.Value).ToList(), lasts);
                int least = Math.Max(0, lasts.Sum() - m.LastTick);
                if (shared > 0 || least > 0)
                    notes.Add($"{SourceOf(idx[0]).Name} ({m.LastTick} ticks): its {idx.Count} pieces share {shared} ticks of it (the least their lengths allow: {least})");
            }
        }
        if (planOnly) return new SetReplaceResult([], starts, notes);
        Transform[]? pose = null;
        if (poseAt is { } pa)
        {
            pose = new Transform[s.Bones.Count];
            for (int b = 0; b < pose.Length; b++) pose[b] = pa.Motion.Local(pa.Frame, b);
        }
        var results = new List<ImportResult>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
            results.Add(One(i, o.Fit with { CutStart = starts[i] ?? 0, SharedPose = pose }));
        // the feet slide check with the game's switch into every other clip of the set (a random set may pick any of them; past 8 clips,
        // into the next one alone, the last back to the first)
        if (results.Count > 1)
            for (int i = 0; i < results.Count; i++)
            {
                var into = Enumerable.Range(1, results.Count - 1).Select(k => (i + k) % results.Count).Take(results.Count <= 8 ? results.Count - 1 : 1)
                    .Select(j => (results[j].Imported, slots[j].Name, o.SwitchTicks)).ToList();
                if (Feet.Check(results[i].Imported, results[i].Fit?.Blends ?? [], into) is { } c) results[i].Report.Set(c);
            }
        return new SetReplaceResult(results, starts, notes);
    }

    /// <summary>The set's clips one after another as the game switches between them: each clip whole, the next one blended in from the last
    /// one's final pose over <paramref name="blendTicks"/> ticks (its own motion from its first frame), each clip's mover carried on from
    /// where the one before left it. Also the tick each clip starts at in the chain.</summary>
    public static (Motion Chain, IReadOnlyList<int> Starts) Preview(IReadOnlyList<Motion> clips, int blendTicks)
    {
        if (clips.Count == 0) throw new ArgumentException("no clip to preview");
        var sk = clips[0].Skeleton;
        int n = sk.Bones.Count;
        var locals = new List<Transform[]>();
        var mover = new List<Transform>();
        var starts = new List<int>();
        var rk = new bool[n]; var pk = new bool[n];
        bool hasMover = false;
        foreach (var m in clips)
        {
            if (m.Skeleton.Bones.Count != n) throw new ArgumentException("the clips aren't on one skeleton");
            for (int b = 0; b < n; b++) { rk[b] |= m.RotationKeyed[b]; pk[b] |= m.PositionKeyed[b]; }
            hasMover |= m.HasMover;
            Transform[]? last = locals.Count > 0 ? locals[^1] : null;
            // the mover goes on from the last one's end: this clip's own travel and turn, started there
            var basis = mover.Count > 0 ? mover[^1] * Inverse(m.Mover(0)) : Transform.Identity;
            starts.Add(locals.Count);
            for (int t = 0; t <= m.LastTick; t++)
            {
                var l = new Transform[n];
                float w = last is null || blendTicks <= 0 || t >= blendTicks ? 1 : t / (float)blendTicks;
                for (int b = 0; b < n; b++) l[b] = w >= 1 ? m.Local(t, b) : ClipFit.Blend(last![b], m.Local(t, b), w);
                locals.Add(l);
                mover.Add(basis * m.Mover(t));
            }
        }
        return (new Motion(sk, [.. locals], [.. mover], rk, pk, hasMover), starts);
    }

    static Transform Inverse(Transform t)
    {
        var iq = System.Numerics.Quaternion.Inverse(t.Rotation);
        return new Transform(System.Numerics.Vector3.Transform(-t.Position, iq), iq);
    }

    /// <summary>The pose distance from each clip's last frame to the next one's first, as the game's switches meet them (the clips in
    /// order, the last back to the first), on the records as written.</summary>
    public static IReadOnlyList<double> Seams(IReadOnlyList<ImportResult> results)
    {
        var l = new List<double>();
        for (int i = 0; i < results.Count; i++)
        {
            var a = results[i].Imported; var b = results[(i + 1) % results.Count].Imported;
            l.Add(ClipFit.PoseDistance(a, a.LastTick, b, 0));
        }
        return l;
    }
}
