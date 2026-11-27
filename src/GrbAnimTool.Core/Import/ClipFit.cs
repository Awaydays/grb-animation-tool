using System.Globalization;
using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Import;

/// <summary>How a motion of another length is made the clip's length: <see cref="Cut"/> plays part of it from a start frame,
/// <see cref="Speed"/> resamples all of it (it plays faster or slower), <see cref="Hold"/> keeps its last pose to the end (or cuts its end).</summary>
public enum FitMode { Cut, Speed, Hold }

/// <summary>How a motion is put on a clip: the fit when the lengths differ (<see cref="Mode"/>, from <see cref="CutStart"/> for a cut),
/// the seamless loop (the last <see cref="LoopFrames"/> frames blended into the first frame's pose) and the root motion's scale (0: the
/// character stays in place).</summary>
public sealed record FitOptions
{
    public FitMode Mode { get; init; } = FitMode.Cut;
    /// <summary>The source frame a cut starts at.</summary>
    public int CutStart { get; init; }
    public bool Loop { get; init; }
    public int LoopFrames { get; init; } = ClipFit.DefaultLoopFrames;
    /// <summary>The mover's travel from its first place, scaled: 1 as it is, 0 in place. Its turn is kept.</summary>
    public double RootScale { get; init; } = 1;
    /// <summary>Line up the set: the pose (each bone's local transform) every clip of a set starts and ends at, so the game's switches
    /// between them don't jump. The first <see cref="LoopFrames"/> frames are blended from it, the last into it (half the clip at most);
    /// with <see cref="Loop"/> the loop closes on it. The mover is left alone. Null: no line-up.</summary>
    public Transform[]? SharedPose { get; init; }
    /// <summary>Keep planted feet in place during the line-up's and the loop's blends (<see cref="Feet.KeepPlanted"/>): the legs bent to the
    /// planted foot instead of the foot sliding, a long way stepped.</summary>
    public bool KeepFeet { get; init; } = true;

    /// <summary>The fit in the tool's wording, for a source of <paramref name="sourceLast"/> ticks on a clip of <paramref name="last"/>:
    /// "cut from 12.3 s", "speed 1.5x", "hold"; then the loop and the root motion when they are asked.</summary>
    public string Words(int sourceLast, int last)
    {
        var l = new List<string>();
        if (sourceLast != last)
            l.Add(Mode switch
            {
                FitMode.Cut => $"cut from {Seconds(CutStart)} s",
                FitMode.Speed => $"speed {(sourceLast / (double)last).ToString("0.###", CultureInfo.InvariantCulture)}x",
                _ => sourceLast < last ? "last pose held" : "end cut",
            });
        if (Loop) l.Add($"seamless loop {Seconds(LoopFrames)} s");
        if (SharedPose is not null) l.Add("lined up with the set");
        if (RootScale != 1) l.Add($"root motion {RootScale.ToString("0.###", CultureInfo.InvariantCulture)}x");
        return string.Join(", ", l);
    }

    static string Seconds(int ticks) => (ticks / (double)ClipRecord.TicksPerSecond).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>A motion fitted to a clip's length: the motion itself (every frame, the clip's length exactly), what was done in plain notes,
/// whether its mover travels or turns (<see cref="RootMoves"/>: the loop then leaves the mover alone), and the first frame of the loop's
/// blend, or null without one.</summary>
public sealed record FitResult(Motion Motion, IReadOnlyList<string> Notes, bool RootMoves, int? BlendFrom, bool PlaysAtOtherSpeed)
{
    /// <summary>The blends the fit made (the line-up's, the loop's), each from the last frame of the pose blended from to the first frame
    /// of the pose blended into: what the feet slide check measures.</summary>
    public IReadOnlyList<(int From, int To)> Blends { get; init; } = [];
}

/// <summary>Puts a motion of any length on a clip of a fixed length, always exactly that length (the clip's record keeps its structure).</summary>
public static class ClipFit
{
    /// <summary>The loop's blend by default: 30 frames, half a second.</summary>
    public const int DefaultLoopFrames = 30;
    /// <summary>Above these the mover moves (travels or turns) for real: the loop's blend leaves it alone.</summary>
    public const double RootMovesCm = 5, RootTurnsDeg = 5;
    /// <summary>The pose distance's weight of the hips' height against the bones' rotations: degrees per centimetre.</summary>
    public const double HeightWeight = 1;

    /// <summary>The fitted motion of <paramref name="src"/> on a clip of <paramref name="last"/> ticks. A cut must lie inside the source
    /// (an <see cref="ArgumentException"/> says why not); the lengths being equal, there is no fit step.</summary>
    public static FitResult Apply(Motion src, int last, FitOptions o)
    {
        if (last < 1) throw new ArgumentException("the clip is shorter than one tick");
        int sl = src.LastTick, n = src.Skeleton.Bones.Count;
        var notes = new List<string>();
        var locals = new Transform[last + 1][];
        var mover = new Transform[last + 1];
        bool otherSpeed = false;
        if (sl == last)
            for (int t = 0; t <= last; t++) { locals[t] = Frame(src, t, n); mover[t] = src.Mover(t); }
        else switch (o.Mode)
        {
            case FitMode.Cut:
                if (sl < last) throw new ArgumentException($"the source lasts {sl} ticks, shorter than the clip's {last}: a cut can't fill it. Use Speed or Hold.");
                if (o.CutStart < 0 || o.CutStart > sl - last) throw new ArgumentException($"a cut of {last} ticks starts at frame 0 to {sl - last} of the source, not {o.CutStart}");
                for (int t = 0; t <= last; t++) { locals[t] = Frame(src, o.CutStart + t, n); mover[t] = src.Mover(o.CutStart + t); }
                // the cut's mover starts where the source's own starts: its place and turn at the cut taken back to the source's first
                var basis = src.Mover(0) * Inverse(mover[0]);
                for (int t = 0; t <= last; t++) mover[t] = basis * mover[t];
                notes.Add($"cut: source frames {o.CutStart} to {o.CutStart + last} of 0 to {sl}");
                break;
            case FitMode.Speed:
                for (int t = 0; t <= last; t++)
                {
                    double u = t * (double)sl / last;
                    int i = Math.Min((int)Math.Floor(u), sl);
                    int j = Math.Min(i + 1, sl);
                    float w = (float)(u - i);
                    locals[t] = Blend(Frame(src, i, n), Frame(src, j, n), w);
                    mover[t] = Blend(src.Mover(i), src.Mover(j), w);
                }
                otherSpeed = true;
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"speed: {sl} ticks resampled to {last}, so it plays {sl / (double)last:0.###} times as fast as the source"));
                break;
            default:
                for (int t = 0; t <= last; t++) { int f = Math.Min(t, sl); locals[t] = Frame(src, f, n); mover[t] = src.Mover(f); }
                notes.Add(sl < last ? $"hold: the source's last pose (frame {sl}) held from tick {sl} to {last}" : $"hold: the source's frames after {last} cut");
                break;
        }

        if (o.RootScale != 1)
        {
            if (!(o.RootScale >= 0 && double.IsFinite(o.RootScale))) throw new ArgumentException("the root motion's scale is 0 or more");
            var p0 = mover[0].Position;
            for (int t = 0; t <= last; t++) mover[t] = mover[t] with { Position = p0 + (mover[t].Position - p0) * (float)o.RootScale };
            notes.Add(o.RootScale == 0 ? "root motion 0: the character stays in place (its turn kept)" : string.Create(CultureInfo.InvariantCulture, $"root motion scaled by {o.RootScale:0.###} (its turn kept)"));
        }

        bool moves = false;
        for (int t = 0; t <= last && !moves; t++)
            moves = Vector3.Distance(mover[t].Position, mover[0].Position) * 100 > RootMovesCm || AngleDeg(mover[t].Rotation, mover[0].Rotation) > RootTurnsDeg;

        // line up the set: the start blended from the set's pose, then (below) the end into it, with the loop or without
        Transform[]? endPose = null;
        int lineUp = 0;
        // each blend from its last frame of the pose blended from to its first frame of the pose blended into
        var blends = new List<(int From, int To)>();
        if (o.SharedPose is { } pose)
        {
            if (pose.Length != n) throw new ArgumentException($"the set's pose holds {pose.Length} bones, the skeleton {n}");
            lineUp = Math.Clamp(o.LoopFrames, 1, Math.Max(1, last / 2));
            for (int t = 0; t < lineUp; t++) locals[t] = Blend(pose, locals[t], t / (float)lineUp);
            endPose = pose;
            blends.Add((0, lineUp));
            notes.Add($"lined up with the set: frames 0 to {lineUp - 1} blended from the set's pose, frames {last - lineUp + 1} to {last} into it");
        }
        int? from = null;
        if (o.Loop)
        {
            int k = endPose is null ? Math.Clamp(o.LoopFrames, 1, last) : lineUp;
            from = last - k + 1;
            for (int t = from.Value; t <= last; t++)
            {
                float w = (t - (last - k)) / (float)k;
                locals[t] = Blend(locals[t], endPose ?? locals[0], w);
                if (!moves) mover[t] = Blend(mover[t], mover[0], w);
            }
            notes.Add($"seamless loop: frames {from} to {last} blended into frame 0's pose" + (moves ? "; the root moves, so its motion isn't blended (the loop restarts where the clip's root starts)" : ""));
            blends.Add((from.Value - 1, last));
        }
        else if (endPose is not null)
        {
            for (int t = last - lineUp + 1; t <= last; t++) locals[t] = Blend(locals[t], endPose, (t - (last - lineUp)) / (float)lineUp);
            blends.Add((last - lineUp, last));
        }
        var rk = new bool[n];
        var pk = new bool[n];
        for (int b = 0; b < n; b++) { rk[b] = src.RotationKeyed[b]; pk[b] = src.PositionKeyed[b]; }
        // a blend shorter than the quickest step leaves the feet as it has them: holding them in so few frames would snap the knees
        if (o.KeepFeet && blends.Any(b => b.To - b.From < Feet.MinStepTicks))
            notes.Add($"the feet left as the blends have them: a blend of under {Feet.MinStepTicks} frames is too short to keep them planted");
        if (o.KeepFeet)
            foreach (var (a, z) in blends.Where(b => b.To - b.From >= Feet.MinStepTicks))
                foreach (var k in Feet.KeepPlanted(src.Skeleton, locals, mover, rk, a, z, blends))
                    notes.Add(k.Short && k.StepCm > Feet.MaxStepCm ? string.Create(CultureInfo.InvariantCulture, $"the {Side(k.Foot)} foot left as the blend has it at frames {k.From} to {k.To}: a step of {k.StepCm:0.0} cm is longer than the tool makes ({Feet.MaxStepCm:0} cm)")
                        : k.Short ? $"the {Side(k.Foot)} foot left as the blend has it at frames {k.From} to {k.To}: the leg can't keep it planted there (out of its reach, a turn too sharp or a blend too short to step, or only by stepping while the other foot is in the air)"
                        : k.StepCm > 0 ? string.Create(CultureInfo.InvariantCulture, $"the {Side(k.Foot)} foot steps {k.StepCm:0.0} cm in the blend at frames {k.From} to {k.To} instead of sliding")
                        : $"the {Side(k.Foot)} foot kept planted in the blend at frames {k.From} to {k.To} (the leg bent to it)");
        return new FitResult(new Motion(src.Skeleton, locals, mover, rk, pk, src.HasMover), notes, moves, from, otherSpeed) { Blends = blends };
    }

    static string Side(string foot) => foot.StartsWith("Left", StringComparison.Ordinal) ? "left" : "right";

    /// <summary>How well two frames match for a cut or a switch: their pose distance (<see cref="PoseDistance(Motion, int, Motion, int)"/>)
    /// plus where the feet stand and which one carries the weight (<see cref="Feet.Distance"/>, centimetres counted as degrees).</summary>
    public static double MatchDistance(Motion a, int ta, Motion b, int tb) => PoseDistance(a, ta, b, tb) + Feet.Distance(a, ta, b, tb);

    /// <summary>The cut's suggested start for a clip of <paramref name="last"/> ticks: the source frame s where frame s and frame s + last
    /// match best (<see cref="MatchDistance"/>: the pose and where the feet stand), so the loop closes best; the first of equals. Null when
    /// the source is shorter than the clip.</summary>
    public static int? SuggestCut(Motion src, int last)
    {
        int sl = src.LastTick;
        if (sl < last || last < 1) return null;
        var w = Weights(src);
        int hips = Hips(src.Skeleton);
        int best = 0; double bd = double.MaxValue;
        for (int s = 0; s <= sl - last; s++)
        {
            double d = PoseDistance(src, s, s + last, w, hips) + Feet.Distance(src, s, src, s + last);
            // a hair's difference (the floats' rounding) is no better: the first of equals is kept
            if (d < bd - 1e-4) { bd = d; best = s; }
        }
        return best;
    }

    /// <summary>How far two frames' poses lie apart: the bones' local rotations, each weighted by the bones it carries (itself and those
    /// below it), in degrees on the weights' average, plus the hips' height in centimetres times <see cref="HeightWeight"/>.</summary>
    public static double PoseDistance(Motion m, int a, int b) => PoseDistance(m, a, b, Weights(m), Hips(m.Skeleton));

    static double PoseDistance(Motion m, int a, int b, double[] w, int hips) => PoseDistance(m, a, m, b, w, hips);

    /// <summary>As <see cref="PoseDistance(Motion, int, int)"/>, between frame <paramref name="ta"/> of <paramref name="a"/> and frame
    /// <paramref name="tb"/> of <paramref name="b"/> (on one skeleton): how far one clip's end lies from the next one's start, say.</summary>
    public static double PoseDistance(Motion a, int ta, Motion b, int tb)
    {
        if (a.Skeleton.Bones.Count != b.Skeleton.Bones.Count) throw new ArgumentException("the two motions aren't on one skeleton");
        return PoseDistance(a, ta, b, tb, Weights(a), Hips(a.Skeleton));
    }

    static double PoseDistance(Motion m, int a, Motion n, int b, double[] w, int hips)
    {
        double sum = 0, total = 0;
        for (int i = 0; i < w.Length; i++)
        {
            if (!m.RotationKeyed[i] && !n.RotationKeyed[i]) continue;
            sum += w[i] * AngleDeg(m.Local(a, i).Rotation, n.Local(b, i).Rotation);
            total += w[i];
        }
        double d = total > 0 ? sum / total : 0;
        if (hips >= 0) d += HeightWeight * Math.Abs(m.Model(a, hips).Position.Z - n.Model(b, hips).Position.Z) * 100;
        return d;
    }

    /// <summary>The cut starts for pieces of <paramref name="lasts"/> ticks each from one source, one piece per clip of a set, chosen in two
    /// steps. First the overlap: as little shared source as the lengths allow. When the source holds them all, no two pieces overlap (each
    /// starts at or after the one before ends, in any order); when it doesn't, the pieces cover the whole source in a chain (the first from
    /// frame 0, the last to the source's end, each starting after and ending after the one before), so they share exactly their total length
    /// less the source's, and no piece lies inside another. Only where no such chain exists (a piece as long as the source, say) do they
    /// overlap more, each start kept apart from the others' as far as it can be. Then, among those, the starts whose ends match best
    /// (<see cref="MatchDistance"/>: the pose and where the feet stand and carry the weight): each piece's first frame with its last (its loop),
    /// or, with <paramref name="pose"/> (the set lined up on it), both against that pose. <c>Overlap</c> is true when the pieces share source.
    /// Refused (an <see cref="ArgumentException"/>) for a piece longer than the source.</summary>
    public static (int[] Starts, bool Overlap) SuggestPieces(Motion src, IReadOnlyList<int> lasts, (Motion Motion, int Frame)? pose = null)
    {
        int sl = src.LastTick, n = lasts.Count;
        if (n == 0) return ([], false);
        foreach (int l in lasts)
            if (l < 1 || l > sl) throw new ArgumentException($"a piece of {l} ticks can't be cut from a source of {sl}: use Speed or Hold");
        var cost = PieceCosts(src, lasts, pose);
        int total = lasts.Sum();
        if (total <= sl) return (Apart(sl, lasts, cost), false);
        if (Chain(sl, lasts, cost) is { } chained) return (chained, true);
        // no chain covers the source without a piece inside another: each piece at its own best start, kept apart where it can be
        var starts = new int[n];
        var orders = Enumerable.Range(0, n).Select(i => Enumerable.Range(0, sl - lasts[i] + 1).OrderBy(s => cost[i][s]).ThenBy(s => s).ToList()).ToList();
        var byLength = Enumerable.Range(0, n).OrderByDescending(i => lasts[i]).ThenBy(i => i).ToList();
        for (int sep = Math.Max(1, (sl - lasts.Max()) / n); sep >= 1; sep = sep > 8 ? sep * 3 / 4 : sep - 1)
        {
            var placed = new List<int>();
            bool all = true;
            foreach (int i in byLength)
            {
                int pick = orders[i].FirstOrDefault(s => placed.All(o => Math.Abs(o - s) >= sep), -1);
                if (pick < 0) { all = false; break; }
                starts[i] = pick;
                placed.Add(pick);
            }
            if (all) return (starts, true);
        }
        for (int i = 0; i < n; i++) starts[i] = orders[i][0];
        return (starts, true);
    }

    /// <summary>The frames of source shared by pieces of <paramref name="lasts"/> at <paramref name="starts"/>: their total length less the
    /// length of the source they cover together (frame spans, so two pieces meeting at one frame share none).</summary>
    public static int SharedTicks(IReadOnlyList<int> starts, IReadOnlyList<int> lasts)
    {
        var spans = starts.Select((s, i) => (s, e: s + lasts[i])).OrderBy(x => x.s).ToList();
        int covered = 0, reach = int.MinValue;
        foreach (var (s, e) in spans)
        {
            if (e <= reach) continue;
            covered += e - Math.Max(s, reach);
            reach = e;
        }
        return lasts.Sum() - covered;
    }

    /// <summary>Whether a piece lies wholly inside another (the two the same piece included).</summary>
    public static bool AnyInside(IReadOnlyList<int> starts, IReadOnlyList<int> lasts)
    {
        for (int i = 0; i < starts.Count; i++)
            for (int j = 0; j < starts.Count; j++)
                if (i != j && starts[j] <= starts[i] && starts[i] + lasts[i] <= starts[j] + lasts[j]) return true;
        return false;
    }

    /// <summary>Each piece's cost at each start: <see cref="MatchDistance"/> between its first and last frame, or of both against the pose.</summary>
    static double[][] PieceCosts(Motion src, IReadOnlyList<int> lasts, (Motion Motion, int Frame)? pose)
    {
        int sl = src.LastTick;
        var w = Weights(src);
        int hips = Hips(src.Skeleton);
        double[]? toPose = null;
        if (pose is { } p)
        {
            if (p.Motion.Skeleton.Bones.Count != src.Skeleton.Bones.Count) throw new ArgumentException("the pose isn't on the source's skeleton");
            toPose = new double[sl + 1];
            for (int f = 0; f <= sl; f++) toPose[f] = PoseDistance(src, f, p.Motion, p.Frame, w, hips) + Feet.Distance(src, f, p.Motion, p.Frame);
        }
        var c = new double[lasts.Count][];
        for (int i = 0; i < lasts.Count; i++)
        {
            c[i] = new double[sl - lasts[i] + 1];
            for (int s = 0; s <= sl - lasts[i]; s++)
                c[i][s] = toPose is not null ? toPose[s] + toPose[s + lasts[i]] : PoseDistance(src, s, s + lasts[i], w, hips) + Feet.Distance(src, s, src, s + lasts[i]);
        }
        return c;
    }

    /// <summary>Pieces that all fit: none overlapping, in whichever order along the source costs least (every order for up to
    /// <see cref="OrderAll"/> pieces while the search's arrays stay small, else in the given order). The first of equals.</summary>
    static int[] Apart(int sl, IReadOnlyList<int> lasts, double[][] cost)
    {
        int n = lasts.Count;
        // every order holds 2^n arrays of the source's length: past a few million cells (8 pieces of a long .glb), the given order
        if (n > OrderAll || (1L << n) * (sl + 1L) > 4_000_000)
        {
            // in the given order: best[i][s] the least sum for pieces 0..i, piece i at s
            var best = new double[n][]; var from = new int[n][];
            for (int i = 0; i < n; i++)
            {
                best[i] = Enumerable.Repeat(double.MaxValue, sl + 1).ToArray();
                from[i] = Enumerable.Repeat(-1, sl + 1).ToArray();
                int after = lasts.Skip(i + 1).Sum();
                double runMin = double.MaxValue; int runArg = -1, fed = -1;
                for (int s = 0; s + lasts[i] + after <= sl; s++)
                {
                    double before = 0; int prev = -1;
                    if (i > 0)
                    {
                        for (int q = fed + 1; q <= s - lasts[i - 1]; q++) { if (best[i - 1][q] < runMin - 1e-4) { runMin = best[i - 1][q]; runArg = q; } fed = q; }
                        if (runArg < 0) continue;
                        before = runMin; prev = runArg;
                    }
                    best[i][s] = before + cost[i][s];
                    from[i][s] = prev;
                }
            }
            int at = -1; double bd = double.MaxValue;
            for (int s = 0; s <= sl; s++) if (best[n - 1][s] < bd - 1e-4) { bd = best[n - 1][s]; at = s; }
            var st = new int[n];
            for (int i = n - 1; i >= 0; i--) { st[i] = at; at = from[i][at]; }
            return st;
        }
        // f[mask][e]: the least sum with the pieces of mask placed, the last ending at frame e; g its least over ends up to e
        int full = (1 << n) - 1;
        var f = new double[full + 1][];
        var pick = new (int Piece, int Start)[full + 1][];
        var g = new double[full + 1][]; var gArg = new int[full + 1][];
        for (int m = 0; m <= full; m++)
        {
            f[m] = Enumerable.Repeat(double.MaxValue, sl + 1).ToArray();
            pick[m] = new (int, int)[sl + 1];
        }
        f[0][0] = 0;
        for (int m = 0; m <= full; m++)
        {
            g[m] = new double[sl + 1]; gArg[m] = new int[sl + 1];
            double run = double.MaxValue; int arg = -1;
            for (int e = 0; e <= sl; e++)
            {
                if (m == 0) { run = 0; arg = 0; }
                else if (f[m][e] < run - 1e-4) { run = f[m][e]; arg = e; }
                g[m][e] = run; gArg[m][e] = arg;
            }
            if (m == full) break;
            for (int j = 0; j < n; j++)
            {
                if ((m & (1 << j)) != 0) continue;
                int mj = m | (1 << j);
                for (int s = 0; s + lasts[j] <= sl; s++)
                {
                    if (g[m][s] == double.MaxValue) continue;
                    double v = g[m][s] + cost[j][s];
                    int e = s + lasts[j];
                    if (v < f[mj][e] - 1e-4) { f[mj][e] = v; pick[mj][e] = (j, s); }
                }
            }
        }
        var starts = new int[n];
        int mask = full, end = gArg[full][sl];
        while (mask != 0)
        {
            var (j, s) = pick[mask][end];
            starts[j] = s;
            mask &= ~(1 << j);
            end = gArg[mask][s];
        }
        return starts;
    }

    /// <summary>Up to this many pieces, every order along the source is tried.</summary>
    const int OrderAll = 8;

    /// <summary>Pieces longer together than the source by B ticks: a chain from frame 0 to the source's end, each piece overlapping the one
    /// before by o (0 to less than either's length), the overlaps adding up to B, so the pieces share no more than they must and none lies
    /// inside another; whichever order and overlaps cost least (every order while the search stays small, else the given order). Null
    /// when no chain fits.</summary>
    static int[]? Chain(int sl, IReadOnlyList<int> lasts, double[][] cost)
    {
        int n = lasts.Count, budget = lasts.Sum() - sl;
        long work = (1L << n) * n * (budget + 1L) * n * (budget + 1L);
        bool every = n <= OrderAll && work <= 400_000_000;
        int[] order = Enumerable.Range(0, n).ToArray();
        if (!every)
        {
            // the given order: best[i][u] the least sum for pieces 0..i with u of the overlap used
            var best = new double[n][]; var back = new int[n][];
            for (int i = 0; i < n; i++) { best[i] = Enumerable.Repeat(double.MaxValue, budget + 1).ToArray(); back[i] = Enumerable.Repeat(-1, budget + 1).ToArray(); }
            best[0][0] = cost[0][0];
            int placedLen = lasts[0];
            for (int i = 1; i < n; i++)
            {
                int maxO = Math.Min(lasts[i - 1], lasts[i]) - 1;
                for (int u = 0; u <= budget; u++)
                {
                    if (best[i - 1][u] == double.MaxValue) continue;
                    int end = placedLen - u;
                    for (int o = 0; o <= Math.Min(maxO, budget - u); o++)
                    {
                        int s = end - o;
                        if (s < 0 || s + lasts[i] > sl) continue;
                        double v = best[i - 1][u] + cost[i][s];
                        if (v < best[i][u + o] - 1e-4) { best[i][u + o] = v; back[i][u + o] = o; }
                    }
                }
                placedLen += lasts[i];
            }
            if (best[n - 1][budget] == double.MaxValue) return null;
            var st = new int[n];
            int uu = budget, len = lasts.Sum();
            for (int i = n - 1; i >= 0; i--)
            {
                int endI = len - uu;
                st[i] = endI - lasts[i];
                len -= lasts[i];
                if (i > 0) uu -= back[i][uu];
            }
            return st;
        }
        // every order: state (mask, last piece, overlap used); the chain's end is the mask's length less the overlap used
        int full = (1 << n) - 1;
        var bestM = new Dictionary<(int Mask, int Last, int Used), (double Cost, int PrevLast, int O)>();
        for (int j = 0; j < n; j++) bestM[(1 << j, j, 0)] = (cost[j][0], -1, 0);
        var lenOf = new int[full + 1];
        for (int m = 1; m <= full; m++) for (int j = 0; j < n; j++) if ((m & (1 << j)) != 0) lenOf[m] += lasts[j];
        foreach (int m in Enumerable.Range(1, full).OrderBy(BitCount))
        {
            if (m == full) break;
            for (int k = 0; k < n; k++)
            {
                if ((m & (1 << k)) == 0) continue;
                for (int u = 0; u <= budget; u++)
                {
                    if (!bestM.TryGetValue((m, k, u), out var cur)) continue;
                    int end = lenOf[m] - u;
                    for (int j = 0; j < n; j++)
                    {
                        if ((m & (1 << j)) != 0) continue;
                        int maxO = Math.Min(Math.Min(lasts[k], lasts[j]) - 1, budget - u);
                        for (int o = 0; o <= maxO; o++)
                        {
                            int s = end - o;
                            if (s < 0 || s + lasts[j] > sl) continue;
                            // the chain must still reach the source's end with what is left
                            var key = (m | (1 << j), j, u + o);
                            double v = cur.Cost + cost[j][s];
                            if (!bestM.TryGetValue(key, out var had) || v < had.Cost - 1e-4) bestM[key] = (v, k, o);
                        }
                    }
                }
            }
        }
        int lastPiece = -1; double bc = double.MaxValue;
        for (int j = 0; j < n; j++)
            if (bestM.TryGetValue((full, j, budget), out var e) && e.Cost < bc - 1e-4) { bc = e.Cost; lastPiece = j; }
        if (lastPiece < 0) return null;
        var starts = new int[n];
        int mask = full, lp = lastPiece, used = budget;
        while (lp >= 0)
        {
            var e = bestM[(mask, lp, used)];
            starts[lp] = lenOf[mask] - used - lasts[lp];
            mask &= ~(1 << lp);
            used -= e.O;
            lp = e.PrevLast;
        }
        return starts;
    }

    static int BitCount(int m) => System.Numerics.BitOperations.PopCount((uint)m);

    /// <summary>Each bone's weight: 1 plus the number of bones below it.</summary>
    static double[] Weights(Motion m)
    {
        var bones = m.Skeleton.Bones;
        var w = new double[bones.Count];
        for (int i = 0; i < bones.Count; i++)
            for (int p = i, guard = 0; p >= 0 && guard <= bones.Count; p = bones[p].ParentIndex, guard++) w[p] += 1;
        return w;
    }

    /// <summary>The hips' bone, or -1 when the skeleton has none.</summary>
    static int Hips(Skeleton s) => s.IndexOf(Hashing.Crc32.Of("Hips"));

    static Transform[] Frame(Motion m, int t, int n)
    {
        var l = new Transform[n];
        for (int b = 0; b < n; b++) l[b] = m.Local(t, b);
        return l;
    }

    static Transform[] Blend(Transform[] a, Transform[] b, float w)
    {
        var l = new Transform[a.Length];
        for (int i = 0; i < a.Length; i++) l[i] = Blend(a[i], b[i], w);
        return l;
    }

    /// <summary>Rotations by slerp (the shorter way), positions by lerp; <paramref name="w"/> 0 gives a, 1 gives b.</summary>
    internal static Transform Blend(Transform a, Transform b, float w) =>
        new(Vector3.Lerp(a.Position, b.Position, w), Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, w)));

    static Transform Inverse(Transform t)
    {
        var iq = Quaternion.Inverse(t.Rotation);
        return new Transform(Vector3.Transform(-t.Position, iq), iq);
    }

    internal static double AngleDeg(Quaternion a, Quaternion b)
    {
        double d = Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)));
        return 2 * Math.Acos(Math.Min(1, d)) * 180 / Math.PI;
    }
}
