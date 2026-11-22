using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Install;

/// <summary>A clip's pose at one time.
/// <para><see cref="Model"/> holds every skeleton bone's transform in the clip's model space, by bone index: the skeleton's roots stand at
/// their local transform, so the whole pose is relative to the mover. The mover (track 0, the root motion) is not a skeleton bone: its
/// position is the character's travel in the clip's world and its rotation the character's turn. A viewer places the body in the world with
/// <see cref="World"/> (mover times model), or leaves the mover out to play the clip in place. A clip without a mover track has the identity
/// there.</para>
/// <para>Axis convention: model space has x the character's left-right (its left at -x), y forward and z up, in metres. A glTF hand-over
/// turns vectors by (x, y, z) to (x, z, -y).</para></summary>
public sealed class ClipPose
{
    public ClipPose(int bones) => Model = new Transform[bones];

    public float Seconds { get; internal set; }
    public Transform Mover { get; internal set; } = Transform.Identity;
    /// <summary>Whether the clip has a mover track at all.</summary>
    public bool HasMover { get; internal set; }
    public Transform[] Model { get; }

    /// <summary>Bone <paramref name="index"/> in the clip's world: the mover applied to its model transform.</summary>
    public Transform World(int index) => Mover * Model[index];
}

/// <summary>Samples one clip on a skeleton: each skeleton bone's local rotation and position from the clip's track of that bone's hash
/// (the rest pose where the clip has none), forward kinematics to model space, and the mover (track 0) on its own. Tracks naming no
/// skeleton bone (helpers, the IK offsets, plants) don't enter the pose; <see cref="Sampler"/> still samples their rotations and positions.
/// Reuse one sampler per clip: sampling allocates nothing.</summary>
public sealed class PoseSampler
{
    readonly Skeleton _s;
    readonly ClipSampler _c;
    readonly int[] _bone;          // per channel: the bone index, -1 for the mover, -2 for a track naming no bone
    readonly Transform[] _locals;

    public PoseSampler(Skeleton skeleton, ClipRecord clip, SharedCurveTable? table)
    {
        _s = skeleton;
        Clip = clip;
        _c = new ClipSampler(clip, table);
        _bone = new int[_c.Channels.Count];
        for (int i = 0; i < _bone.Length; i++)
        {
            uint h = _c.Channels[i].Track;
            _bone[i] = h == 0 ? -1 : skeleton.IndexOf(h) is var b and >= 0 ? b : -2;
        }
        HasMover = _bone.Contains(-1);
        _locals = new Transform[skeleton.Bones.Count];
    }

    public Skeleton Skeleton => _s;
    public ClipRecord Clip { get; }
    public ClipSampler Sampler => _c;
    public bool HasMover { get; }

    public ClipPose NewPose() => new(_s.Bones.Count);

    /// <summary>The pose at <paramref name="seconds"/> into <paramref name="into"/>.</summary>
    public void Sample(float seconds, ClipPose into)
    {
        _c.At(seconds);
        for (int i = 0; i < _locals.Length; i++) _locals[i] = _s.Bones[i].LocalRest;
        var moverPos = Vector3.Zero;
        var moverRot = Quaternion.Identity;
        for (int i = 0; i < _bone.Length; i++)
        {
            int b = _bone[i];
            if (b == -2) continue;
            bool rot = _c.Channels[i].Rotation;
            if (b == -1)
            {
                if (rot) moverRot = _c.Rotations[i]; else moverPos = _c.Positions[i];
                continue;
            }
            _locals[b] = rot ? _locals[b] with { Rotation = _c.Rotations[i] } : _locals[b] with { Position = _c.Positions[i] };
        }
        _s.ComputeModel(_locals, into.Model);
        into.Mover = new Transform(moverPos, moverRot);
        into.HasMover = HasMover;
        into.Seconds = seconds;
    }

    public ClipPose Sample(float seconds)
    {
        var p = NewPose();
        Sample(seconds, p);
        return p;
    }
}
