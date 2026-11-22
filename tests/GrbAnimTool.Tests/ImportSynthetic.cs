using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Tests;

/// <summary>A small human skeleton in the game's axes (x the character's left-right, y forward, z up; the head's own x up the head, as the
/// game's), standing straight, and motions and clip records on it, for the checks' and the import's tests without game data.</summary>
public static class Humanoid
{
    static (string Name, int Parent, Vector3 Pos, Quaternion Rot)[] Bones(string? leave = null, string? longer = null, float elbowRestDeg = 0)
    {
        var elbow = Quaternion.CreateFromAxisAngle(Vector3.UnitX, elbowRestDeg * MathF.PI / 180);
        var up = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        var l = new List<(string, int, Vector3, Quaternion)>
        {
            ("Reference", -1, Vector3.Zero, Quaternion.Identity),
            ("Hips", 0, new(0, 0, 1.0f), Quaternion.Identity),
            ("Spine", 1, new(0, 0, 0.1f), Quaternion.Identity),
            ("Spine1", 2, new(0, 0, 0.15f), Quaternion.Identity),
            ("Spine2", 3, new(0, 0, 0.15f), Quaternion.Identity),
            ("Neck", 4, new(0, 0, 0.15f), Quaternion.Identity),
            ("Head", 5, new(0, 0, 0.1f), up),
        };
        foreach (var (s, x) in new[] { ("Left", -1f), ("Right", 1f) })
        {
            int arm = l.Count;
            l.Add((s + "Arm", 4, new(0.2f * x, 0, 0.1f), Quaternion.Identity));
            l.Add((s + "ForeArm", arm, new(0, 0, -0.3f), elbow));
            l.Add((s + "Hand", arm + 1, new(0, 0, -0.25f), Quaternion.Identity));
            l.Add((s + "HandMiddle1", arm + 2, new(0, 0, -0.08f), Quaternion.Identity));
        }
        foreach (var (s, x) in new[] { ("Left", -1f), ("Right", 1f) })
        {
            int leg = l.Count;
            l.Add((s + "UpLeg", 1, new(0.1f * x, 0, 0), Quaternion.Identity));
            l.Add((s + "Leg", leg, new(0, 0, -0.45f), Quaternion.Identity));
            l.Add((s + "Foot", leg + 1, new(0, 0, -0.45f), Quaternion.Identity));
            l.Add((s + "ToeBase", leg + 2, new(0, 0.12f, -0.08f), Quaternion.Identity));
        }
        if (longer is not null) l = l.Select(b => b.Item1 == longer ? (b.Item1, b.Item2, b.Item3 * 1.02f, b.Item4) : b).ToList();
        if (leave is not null)
        {
            int i = l.FindIndex(b => b.Item1 == leave);
            if (l.Any(b => b.Item2 == i)) throw new ArgumentException("only a leaf bone can be left out");
            l = l.Select(b => (b.Item1, b.Item2 > i ? b.Item2 - 1 : b.Item2, b.Item3, b.Item4)).Where((_, k) => k != i).ToList();
        }
        return [.. l];
    }

    /// <summary>The skeleton; <paramref name="leave"/> leaves a leaf bone out, <paramref name="longer"/> makes one bone 2% longer,
    /// <paramref name="elbowRestDeg"/> bends both elbows forward in the rest pose.</summary>
    public static Skeleton Skeleton(string? leave = null, string? longer = null, float elbowRestDeg = 0) =>
        Core.Skeletons.Skeleton.Parse(Synthetic.Skeleton(Bones(leave, longer, elbowRestDeg).Select(b => (Crc32.Of(b.Name), b.Parent, b.Pos, b.Rot)).ToArray()), BoneNames.Default());

    public static int Bone(Skeleton s, string name) => s.IndexOf(Crc32.Of(name));

    /// <summary>A motion of <paramref name="last"/> ticks: the rest pose, each tick's locals passed to <paramref name="edit"/> to change;
    /// every bone keyed.</summary>
    public static Motion Motion(Skeleton s, int last, Action<int, Transform[]>? edit = null, Func<int, Transform>? mover = null)
    {
        var locals = new Transform[last + 1][];
        var movers = new Transform[last + 1];
        for (int t = 0; t <= last; t++)
        {
            locals[t] = s.RestLocals();
            edit?.Invoke(t, locals[t]);
            movers[t] = mover?.Invoke(t) ?? Transform.Identity;
        }
        var all = Enumerable.Repeat(true, s.Bones.Count).ToArray();
        return new Motion(s, locals, movers, all, all, true);
    }

    /// <summary>Turns a bone's local rotation by <paramref name="deg"/> about its own <paramref name="axis"/>.</summary>
    public static void Turn(Skeleton s, Transform[] l, string bone, Vector3 axis, float deg)
    {
        int b = Bone(s, bone);
        l[b] = l[b] with { Rotation = Quaternion.Normalize(l[b].Rotation * Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180)) };
    }

    public const uint HandWeights = 0x6C8CB68C;
    public const uint OtherByteTrack = 0x08B5520E;

    /// <summary>A slot on the skeleton: <paramref name="last"/> ticks; the mover (a position and a rotation, one key each), Head swaying
    /// (a key every <paramref name="headStep"/> ticks), Spine and LeftArm held (one key each), the LeftLeg in 12-byte keys, then two byte
    /// tracks (the hand weights when <paramref name="armed"/>, else an unnamed one, and another). The head holds arena kinds 3, 5 and 7
    /// (without 5 when <paramref name="kind5"/> is false). <paramref name="moverPath"/> gives the mover's position keys instead of one at
    /// the origin.</summary>
    public static ClipEntry Slot(Skeleton s, int last, bool armed, int headStep = 1, string name = "test_slot", ulong id = 0x0000012345678901, bool kind5 = true,
        (int[] Ticks, Vector3[] Places)? moverPath = null)
    {
        var rest = s.RestLocals();
        Quaternion R(string b) => rest[Bone(s, b)].Rotation;
        var headTicks = Enumerable.Range(0, last / headStep + 1).Select(k => k * headStep).Append(last).Distinct().ToList();
        var headKeys = headTicks.Select(t => R("Head") * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f * MathF.Sin(t / 20f))).ToList();
        // without kind 5 the head has no room for 6-byte rotations: every rotation is then in the 12-byte form
        byte[] Rot(IReadOnlyList<int> ticks, IReadOnlyList<Quaternion> keys) => kind5 ? CurveWriter.RotCurve(ticks, keys) : CurveWriter.RotCurve12(ticks, keys);
        var tracks = new List<(uint, byte[])>
        {
            (0u, moverPath is { } mp ? CurveWriter.PosCurve(mp.Ticks, mp.Places) : CurveWriter.PosCurve([0], [Vector3.Zero])),
            (0u, Rot([0], [Quaternion.Identity])),
            (Crc32.Of("Head"), Rot(headTicks, headKeys)),
            (Crc32.Of("Spine"), Rot([0], [R("Spine")])),
            (Crc32.Of("LeftArm"), Rot([0], [R("LeftArm")])),
            (Crc32.Of("LeftLeg"), CurveWriter.RotCurve12([0, last], [R("LeftLeg"), R("LeftLeg")])),
            (armed ? HandWeights : 0x00ABCDEFu, CurveWriter.ByteCurve([0, last / 2, last], [125, 0, 125])),
            (OtherByteTrack, CurveWriter.ByteCurve([0], [7])),
        };
        WritingSynthetic.Entry[] head = kind5 ? [new(3, 1000), new(5, 200000), new(7, 200000)] : [new(3, 1000), new(7, 200000)];
        var rec = WritingSynthetic.Clip(name, id, last / 60f, head, [.. tracks]);
        var c = ClipRecord.Parse(rec);
        if (!c.WalkedToEnd) throw new InvalidOperationException(c.Stop);
        return new ClipEntry { Index = 7, Id = id, Name = name, Bytes = rec, Record = c, Status = ClipStatus.Vanilla };
    }
}
