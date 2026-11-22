using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Edits;

/// <summary>The ground marker track <see cref="EditTracks.Marker"/>: at every tick its position is the mover's remaining way to its end
/// place, seen in the mover's own frame with the height left out, and its turn times the mover's is one fixed turn E (the clip's end turn in
/// some clips, none in others). A clip whose mover changes has it written again from the new mover.</summary>
public sealed class GroundMarker
{
    /// <summary>The marker's position and rotation track indices in the clip (-1 where it has none).</summary>
    public int PosTrack { get; private init; } = -1;
    public int RotTrack { get; private init; } = -1;
    /// <summary>The fixed turn E as the clip holds it: the mover's first turn times the marker's first turn.</summary>
    public Quaternion E { get; private init; } = Quaternion.Identity;
    /// <summary>Whether E is the mover's end turn (the mover turns, and E stands within 0.5 deg of its last turn).</summary>
    public bool EndTurn { get; private init; }
    /// <summary>Whether E is no turn at all (within 0.05 deg of the identity, the stored forms' rounding): the marker's turn then undoes the
    /// mover's at every tick.</summary>
    public bool NoTurn => CurveValues.Deg(E, Quaternion.Identity) <= 0.05;
    /// <summary>How far the clip's own marker stands from the relation, over every tick (cm and deg).</summary>
    public double FitCm { get; private init; }
    public double FitDeg { get; private init; }

    /// <summary>The fit the relation must reach on the clip's own marker before the marker is written again from a new mover (cm, deg).</summary>
    public const double FitLimitCm = 2, FitLimitDeg = 1;

    public bool Follows => FitCm <= FitLimitCm && FitDeg <= FitLimitDeg;

    /// <summary>The clip's marker read against its mover, or null when it holds no marker track.</summary>
    public static GroundMarker? Of(ClipRecord clip, SharedCurveTable table)
    {
        int pt = -1, rt = -1;
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            if (clip.Tracks[i] != EditTracks.Marker) continue;
            var u = EditRebuild.Resolved(clip, table, i);
            if (u.Kind == TrackKind.Position) pt = i; else if (u.Kind == TrackKind.Rotation) rt = i;
        }
        if (pt < 0 && rt < 0) return null;
        int last = clip.LastTick;
        var mover = Mover(clip, table, last);
        Curve? pc = pt >= 0 ? EditRebuild.Resolved(clip, table, pt) : null, rc = rt >= 0 ? EditRebuild.Resolved(clip, table, rt) : null;
        int[]? ptk = pc is null ? null : CurveDecoder.KeyTicks(pc), rtk = rc is null ? null : CurveDecoder.KeyTicks(rc);
        var e = Quaternion.Normalize(mover[0].Rotation * (rc is null ? Quaternion.Identity : CurveValues.Rot(rc, rtk!, 0)));
        bool endTurn = CurveValues.Deg(mover[0].Rotation, mover[last].Rotation) > 0.5 && CurveValues.Deg(e, mover[last].Rotation) <= 0.5;
        double cm = 0, deg = 0;
        for (int t = 0; t <= last; t++)
        {
            var (p, q) = At(mover, t, e);
            if (pc is not null) cm = Math.Max(cm, (CurveValues.Pos(pc, ptk!, t) - p).Length() * 100);
            if (rc is not null) deg = Math.Max(deg, CurveValues.Deg(CurveValues.Rot(rc, rtk!, t), q));
        }
        return new GroundMarker { PosTrack = pt, RotTrack = rt, E = e, EndTurn = endTurn, FitCm = cm, FitDeg = deg };
    }

    /// <summary>The mover at every tick 0 to <paramref name="last"/>.</summary>
    public static Transform[] Mover(ClipRecord clip, SharedCurveTable table, int last)
    {
        Curve? p = null, r = null;
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            if (clip.Tracks[i] != 0) continue;
            var u = EditRebuild.Resolved(clip, table, i);
            if (u.Kind == TrackKind.Position) p ??= u; else if (u.Kind == TrackKind.Rotation) r ??= u;
        }
        int[]? pt = p is null ? null : CurveDecoder.KeyTicks(p), rt = r is null ? null : CurveDecoder.KeyTicks(r);
        var m = new Transform[last + 1];
        for (int t = 0; t <= last; t++)
            m[t] = new Transform(p is null ? Vector3.Zero : CurveValues.Pos(p, pt!, t), r is null ? Quaternion.Identity : CurveValues.Rot(r, rt!, t));
        return m;
    }

    /// <summary>The marker at tick <paramref name="t"/> for a mover and a fixed turn.</summary>
    public static (Vector3 P, Quaternion Q) At(Transform[] mover, int t, Quaternion e)
    {
        var end = mover[^1].Position;
        var inv = Quaternion.Inverse(mover[t].Rotation);
        var d = Vector3.Transform(end - mover[t].Position, inv);
        return (new Vector3(d.X, d.Y, 0), Quaternion.Normalize(inv * e));
    }

    /// <summary>The marker's curves written again from <paramref name="mover"/> (every tick of the new clip) with the fixed turn
    /// <paramref name="e"/>, keyed by the keying rule (one key when every value is the same). A track whose curve already holds these keys keeps
    /// its bytes. Adds the new curves to <paramref name="curves"/>.</summary>
    public void Rewrite(ClipRecord clip, SharedCurveTable table, Transform[] mover, Quaternion e, Dictionary<int, byte[]> curves, List<string> notes)
    {
        int last = mover.Length - 1;
        var ticks = KeyingRule.Ticks(last);
        var ps = ticks.Select(t => At(mover, t, e).P).ToList();
        var qs = ticks.Select(t => At(mover, t, e).Q).ToList();
        if (PosTrack >= 0)
        {
            var u = EditRebuild.Resolved(clip, table, PosTrack);
            bool flat = ps.All(p => (p - ps[0]).Length() < 1e-6f);
            IReadOnlyList<int> tk = flat ? [0] : ticks; IReadOnlyList<Vector3> pv = flat ? [ps[0]] : ps;
            if (!CurveValues.SamePos(u, tk, pv)) curves[PosTrack] = CurveWriter.PosCurve(tk, pv);
        }
        if (RotTrack >= 0)
        {
            var u = EditRebuild.Resolved(clip, table, RotTrack);
            bool flat = qs.All(q => CurveValues.Deg(q, qs[0]) < 1e-4);
            IReadOnlyList<int> tk = flat ? [0] : ticks; IReadOnlyList<Quaternion> qv = flat ? [qs[0]] : qs;
            if (!CurveValues.SameRot(u, tk, qv)) curves[RotTrack] = CurveValues.WriteRot(u, tk, qv);
        }
        notes.Add($"the ground marker {EditTracks.Marker:X8} written again from the new mover (the game's own stood within {FitCm:F2} cm and {FitDeg:F2} deg of the mover's remaining way)");
    }

    /// <summary>The note for a clip whose marker track doesn't follow the relation: its meaning in this clip isn't known, so it is treated as
    /// a non-bone track of unknown meaning (<paramref name="how"/>).</summary>
    public string NotFollowing(string how) =>
        $"the track {EditTracks.Marker:X8} doesn't follow the mover's remaining way in this clip ({FitCm:F2} cm and {FitDeg:F2} deg off; the relation holds within {FitLimitCm} cm and {FitLimitDeg} deg where it is the ground marker), so its meaning here isn't known: {how}";
}
