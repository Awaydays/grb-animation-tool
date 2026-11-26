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
        if (RootScale != 1) l.Add($"root motion {RootScale.ToString("0.###", CultureInfo.InvariantCulture)}x");
        return string.Join(", ", l);
    }

    static string Seconds(int ticks) => (ticks / (double)ClipRecord.TicksPerSecond).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>A motion fitted to a clip's length: the motion itself (every frame, the clip's length exactly), what was done in plain notes,
/// whether its mover travels or turns (<see cref="RootMoves"/>: the loop then leaves the mover alone), and the first frame of the loop's
/// blend, or null without one.</summary>
public sealed record FitResult(Motion Motion, IReadOnlyList<string> Notes, bool RootMoves, int? BlendFrom, bool PlaysAtOtherSpeed);

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

        int? from = null;
        if (o.Loop)
        {
            int k = Math.Clamp(o.LoopFrames, 1, last);
            from = last - k + 1;
            for (int t = from.Value; t <= last; t++)
            {
                float w = (t - (last - k)) / (float)k;
                locals[t] = Blend(locals[t], locals[0], w);
                if (!moves) mover[t] = Blend(mover[t], mover[0], w);
            }
            notes.Add($"seamless loop: frames {from} to {last} blended into frame 0's pose" + (moves ? "; the root moves, so its motion isn't blended (the loop restarts where the clip's root starts)" : ""));
        }
        var rk = new bool[n];
        var pk = new bool[n];
        for (int b = 0; b < n; b++) { rk[b] = src.RotationKeyed[b]; pk[b] = src.PositionKeyed[b]; }
        return new FitResult(new Motion(src.Skeleton, locals, mover, rk, pk, src.HasMover), notes, moves, from, otherSpeed);
    }

    /// <summary>The cut's suggested start for a clip of <paramref name="last"/> ticks: the source frame s where frame s and frame s + last
    /// are closest (<see cref="PoseDistance"/>), so the loop closes best; the first of equals. Null when the source is shorter than the clip.</summary>
    public static int? SuggestCut(Motion src, int last)
    {
        int sl = src.LastTick;
        if (sl < last || last < 1) return null;
        var w = Weights(src);
        int hips = Hips(src.Skeleton);
        int best = 0; double bd = double.MaxValue;
        for (int s = 0; s <= sl - last; s++)
        {
            double d = PoseDistance(src, s, s + last, w, hips);
            // a hair's difference (the floats' rounding) is no better: the first of equals is kept
            if (d < bd - 1e-4) { bd = d; best = s; }
        }
        return best;
    }

    /// <summary>How far two frames' poses lie apart: the bones' local rotations, each weighted by the bones it carries (itself and those
    /// below it), in degrees on the weights' average, plus the hips' height in centimetres times <see cref="HeightWeight"/>.</summary>
    public static double PoseDistance(Motion m, int a, int b) => PoseDistance(m, a, b, Weights(m), Hips(m.Skeleton));

    static double PoseDistance(Motion m, int a, int b, double[] w, int hips)
    {
        double sum = 0, total = 0;
        for (int i = 0; i < w.Length; i++)
        {
            if (!m.RotationKeyed[i]) continue;
            sum += w[i] * AngleDeg(m.Local(a, i).Rotation, m.Local(b, i).Rotation);
            total += w[i];
        }
        double d = total > 0 ? sum / total : 0;
        if (hips >= 0) d += HeightWeight * Math.Abs(m.Model(a, hips).Position.Z - m.Model(b, hips).Position.Z) * 100;
        return d;
    }

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
