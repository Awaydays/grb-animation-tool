using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Edits;

/// <summary>A clip sampled at every tick on a skeleton: per tick the mover (track 0) and every bone's local transform (the rest pose's where
/// the clip has no track). The edits that move bones by their place in the world (the foot lock) work on this, and write back only the
/// bones they changed.</summary>
public sealed class ClipMotion
{
    public Skeleton Skeleton { get; }
    public int Last { get; }
    /// <summary>The mover at each tick (identity where the clip has none).</summary>
    public Transform[] Mover { get; }
    /// <summary>Each tick's local transforms, by bone index.</summary>
    public Transform[][] Locals { get; }
    /// <summary>The bones the clip has a rotation track for, and a position track for.</summary>
    public IReadOnlySet<int> RotTracked { get; }
    public IReadOnlySet<int> PosTracked { get; }

    ClipMotion(Skeleton s, int last, Transform[] mover, Transform[][] locals, HashSet<int> rot, HashSet<int> pos)
    {
        Skeleton = s; Last = last; Mover = mover; Locals = locals; RotTracked = rot; PosTracked = pos;
    }

    /// <summary>Samples <paramref name="clip"/> at ticks 0 to its last tick.</summary>
    public static ClipMotion Sample(ClipRecord clip, Skeleton s, SharedCurveTable table)
    {
        var cs = new ClipSampler(clip, table);
        int last = clip.LastTick, n = s.Bones.Count;
        var bone = new int[cs.Channels.Count];
        var rot = new HashSet<int>(); var pos = new HashSet<int>();
        for (int i = 0; i < bone.Length; i++)
        {
            uint h = cs.Channels[i].Track;
            bone[i] = h == 0 ? -1 : s.IndexOf(h) is var b and >= 0 ? b : -2;
            if (bone[i] >= 0) (cs.Channels[i].Rotation ? rot : pos).Add(bone[i]);
        }
        var rest = s.RestLocals();
        var mover = new Transform[last + 1];
        var locals = new Transform[last + 1][];
        for (int t = 0; t <= last; t++)
        {
            cs.At(t / (float)ClipRecord.TicksPerSecond);
            var l = (Transform[])rest.Clone();
            var mp = Vector3.Zero; var mq = Quaternion.Identity;
            for (int i = 0; i < bone.Length; i++)
            {
                int b = bone[i];
                if (b == -2) continue;
                bool r = cs.Channels[i].Rotation;
                if (b == -1) { if (r) mq = cs.Rotations[i]; else mp = cs.Positions[i]; continue; }
                l[b] = r ? l[b] with { Rotation = Quaternion.Normalize(cs.Rotations[i]) } : l[b] with { Position = cs.Positions[i] };
            }
            mover[t] = new Transform(mp, Quaternion.Normalize(mq));
            locals[t] = l;
        }
        return new ClipMotion(s, last, mover, locals, rot, pos);
    }

    /// <summary>A copy whose ticks can be changed without touching this one.</summary>
    public ClipMotion Copy() => new(Skeleton, Last, (Transform[])Mover.Clone(), Locals.Select(l => (Transform[])l.Clone()).ToArray(),
        RotTracked.ToHashSet(), PosTracked.ToHashSet());

    /// <summary>Every bone's transform in the clip's world at tick <paramref name="t"/>: the mover times the model pose.</summary>
    public Transform[] World(int t)
    {
        var m = new Transform[Skeleton.Bones.Count];
        Skeleton.ComputeModel(Locals[t], m);
        for (int i = 0; i < m.Length; i++) m[i] = Mover[t] * m[i];
        return m;
    }

    /// <summary>Sets bone <paramref name="b"/>'s turn in the world at tick <paramref name="t"/> by its local rotation, its parent's world turn
    /// as it stands in <paramref name="world"/> (the mover's for a root bone).</summary>
    public void SetWorldRotation(int t, int b, Quaternion worldRot, Transform[] world)
    {
        int p = Skeleton.Bones[b].ParentIndex;
        var pq = p >= 0 ? world[p].Rotation : Mover[t].Rotation;
        Locals[t][b] = Locals[t][b] with { Rotation = Quaternion.Normalize(Quaternion.Inverse(pq) * worldRot) };
    }
}
