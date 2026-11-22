using System.Buffers.Binary;
using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Tests;

/// <summary>Skeletons, clips and verdicts built by hand for the edits' tests.</summary>
public static class EditSynthetic
{
    public static uint H(string name) => Crc32.Of(name);

    /// <summary>A small symmetric body: Reference at 1 m, the hips, a spine, two arms and hands, two legs with feet and toes; every rest
    /// rotation the identity.</summary>
    public static Skeleton Body() => Body(false);

    /// <summary><see cref="Body()"/> with its rest pose in a frame of its own, as the game's skeleton has it: every rest place turned -90 deg
    /// about z, so the rest faces +x with the hands on y (the left at +y). A clip turns it into the clip frame (facing +y, x side to side) by a
    /// +90 deg turn on Reference.</summary>
    public static Skeleton TurnedBody() => Body(true);

    static Skeleton Body(bool turned)
    {
        var b = new List<(uint, int, Vector3, Quaternion)>();
        var turn = RotZ(turned ? -90 : 0);
        int Add(string n, int parent, Vector3 p) { b.Add((H(n), parent, parent < 0 ? p : Vector3.Transform(p, turn), Quaternion.Identity)); return b.Count - 1; }
        int reference = Add("Reference", -1, new Vector3(0, 0, 1));
        int hips = Add("Hips", reference, Vector3.Zero);
        int spine = Add("Spine", hips, new Vector3(0, 0, 0.2f));
        foreach (var (side, x) in new[] { ("Left", -1f), ("Right", 1f) })
        {
            int arm = Add(side + "Arm", spine, new Vector3(0.2f * x, 0, 0.3f));
            Add(side + "Hand", arm, new Vector3(0.3f * x, 0, 0));
        }
        foreach (var (side, x) in new[] { ("Left", -1f), ("Right", 1f) })
        {
            int up = Add(side + "UpLeg", hips, new Vector3(0.1f * x, 0, 0));
            int leg = Add(side + "Leg", up, new Vector3(0, 0, -0.45f));
            int foot = Add(side + "Foot", leg, new Vector3(0, 0, -0.45f));
            Add(side + "ToeBase", foot, new Vector3(0, 0.1f, -0.05f));
        }
        return Skeleton.Parse(Synthetic.Skeleton([.. b]), BoneNames.Default());
    }

    /// <summary>A clip record with a head sized for its curves (arena kinds 3, 5, 7, 9 as the curves need, plus any of
    /// <paramref name="extraKinds"/> even when not needed, minus <paramref name="leaveOut"/>).</summary>
    public static byte[] Clip(string name, ulong id, int lastTick, (uint Track, byte[] Curve)[] tracks, int[]? stepTimes = null,
        int[]? leaveOut = null, int[]? extraKinds = null, float? duration = null)
    {
        var need = new long[9];
        foreach (var (_, c) in tracks)
        {
            var cv = CurveWalker.Walk(c, 0, 1, out _, out _)[0];
            if (cv.IsReference) continue;
            int a = ClipRebuild.ArenaOf(cv.Tag, cv.ValueBytes);
            need[a] += ClipRebuild.UnitsOf(cv);
        }
        var head = new List<WritingSynthetic.Entry> { new(0x15, (uint)tracks.Length) };
        foreach (var (kind, align) in new[] { (3, 1), (5, 2), (7, 4), (9, 8) })
        {
            if (leaveOut?.Contains(kind) == true) continue;
            if (need[align] > 0 || extraKinds?.Contains(kind) == true) head.Add(new((ushort)kind, (uint)Math.Max(need[align], 1)));
        }
        return WritingSynthetic.Clip(name, id, duration ?? lastTick / 60f, [.. head], tracks, stepTimes);
    }

    public static ClipGraphVerdict Free(ulong id) => new() { ClipId = id, Uses = [], GraphComplete = true, Scanned = true };

    public static ClipGraphVerdict MotionField(ulong id) => new()
    {
        ClipId = id, GraphComplete = true, Scanned = true,
        Uses = [new ClipGraphUse(id, GraphUseKind.MotionFieldSegment, 0, 1, "doc", 0, 0, HoldsDuration: true, MotionField: true)],
    };

    public static ClipGraphVerdict LengthHeld(ulong id) => new()
    {
        ClipId = id, GraphComplete = true, Scanned = true,
        Uses = [new ClipGraphUse(id, GraphUseKind.Unread, 0x1234, 1, "doc", 0, null, HoldsDuration: true, MotionField: false)],
    };

    public static ClipGraphVerdict Incomplete(ulong id) => new() { ClipId = id, Uses = [], GraphComplete = false, Scanned = true };

    /// <summary>A clip GR_Graph's clip list names and no graph node does: the graph read whole and the clip looked for.</summary>
    public static ClipGraphVerdict ListedOnly(ulong id) => new()
    {
        ClipId = id, GraphComplete = true, Scanned = true,
        Uses = [new ClipGraphUse(id, GraphUseKind.GraphClipList, 0, 1, "GR_Graph", 0, null, HoldsDuration: false, MotionField: false)],
    };

    /// <summary>A clip the graph read didn't look for (the graph read whole).</summary>
    public static ClipGraphVerdict Unscanned(ulong id) => new() { ClipId = id, Uses = [], GraphComplete = true, Scanned = false };

    /// <summary>An empty shared table, or one holding <paramref name="curves"/>.</summary>
    public static SharedCurveTable Table(params byte[][] curves) =>
        SharedCurveTable.Parse(Synthetic.SharedTable((uint)curves.Length, curves.SelectMany(c => c).ToArray()));

    public static EditContext Ctx(Skeleton s, ClipGraphVerdict v, SharedCurveTable? t = null) => new(s, t ?? Table(), v);

    public static byte[] Raw(int form, int[] ticks, byte[][] values) => Core.Retiming.CurveRetimer.WriteRaw(form, ticks, values);

    public static byte[] Ref(uint index)
    {
        var b = new byte[5];
        b[0] = Curve.ReferenceTag;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), index);
        return b;
    }

    public static Quaternion RotX(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg * MathF.PI / 180);
    public static Quaternion RotZ(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg * MathF.PI / 180);

    /// <summary>A mirror checked in the world, apart from the mirror's own rule: at every tick each paired bone's world place in
    /// <paramref name="mirrored"/> against its counterpart's place in <paramref name="original"/> reflected through the plane x = 0 of the
    /// mover's first place (cm); each bone's turn away from its rest (the rest pose turned by <paramref name="restTurn"/> into the clip frame,
    /// set at the mover's first place) against the counterpart's, reflected the same way (deg); and the mover against its own reflection: its
    /// place (cm) and turn (deg) seen from its first place, x negated, y kept, the turn reversed. Gives the worst of each.</summary>
    public static (double Cm, double Deg, double MoverCm, double MoverDeg) MirrorWorldError(ClipRecord original, ClipRecord mirrored, Skeleton s,
        SharedCurveTable t, IReadOnlyList<int> pair, Quaternion restTurn)
    {
        var o = ClipMotion.Sample(original, s, t);
        var m = ClipMotion.Sample(mirrored, s, t);
        var f = o.Mover[0];
        var fi = Quaternion.Inverse(f.Rotation);
        var model = new Transform[s.Bones.Count];
        s.ComputeModel(s.RestLocals(), model);
        var restW = model.Select(x => Quaternion.Normalize(f.Rotation * restTurn * x.Rotation)).ToArray();
        static Quaternion Rx(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);
        Vector3 Local(Vector3 p) => Vector3.Transform(p - f.Position, fi);
        Vector3 Refl(Vector3 p) { var l = Local(p); l.X = -l.X; return f.Position + Vector3.Transform(l, f.Rotation); }
        Quaternion ReflRot(Quaternion q) => Quaternion.Normalize(f.Rotation * Rx(fi * q * f.Rotation) * fi);
        double cm = 0, deg = 0, mcm = 0, mdeg = 0;
        for (int tick = 0; tick <= o.Last; tick++)
        {
            var ow = o.World(tick); var mw = m.World(tick);
            for (int b = 0; b < s.Bones.Count; b++)
            {
                int c = pair[b];
                if (c < 0) continue;
                cm = Math.Max(cm, (mw[c].Position - Refl(ow[b].Position)).Length() * 100);
                var pose = Quaternion.Normalize(ow[b].Rotation * Quaternion.Inverse(restW[b]));
                var poseM = Quaternion.Normalize(mw[c].Rotation * Quaternion.Inverse(restW[c]));
                deg = Math.Max(deg, CurveValues.Deg(poseM, ReflRot(pose)));
            }
            var d = Local(o.Mover[tick].Position); var dm = Local(m.Mover[tick].Position);
            mcm = Math.Max(mcm, (dm - new Vector3(-d.X, d.Y, d.Z)).Length() * 100);
            mdeg = Math.Max(mdeg, CurveValues.Deg(fi * m.Mover[tick].Rotation, Rx(fi * o.Mover[tick].Rotation)));
        }
        return (cm, deg, mcm, mdeg);
    }

    public static Curve Track(ClipRecord c, SharedCurveTable t, uint hash, TrackKind kind)
    {
        for (int i = 0; i < c.Tracks.Count; i++)
            if (c.Tracks[i] == hash && SharedCurveTable.Resolve(c.Curves[i], t)!.Kind == kind) return SharedCurveTable.Resolve(c.Curves[i], t)!;
        throw new InvalidOperationException($"no track {hash:X8} {kind}");
    }
}
