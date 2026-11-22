using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Checks;

/// <summary>A motion on a skeleton at every tick, 0 to its last: every bone's local transform (the rest pose's where nothing moves it) and
/// the mover's (track 0, the root motion), in the game's axes. What the checks read: a game clip decoded, an imported file, or an edit's
/// result. Model and world transforms are composed once, on first use.</summary>
public sealed class Motion
{
    readonly Transform[][] _locals;
    readonly Transform[] _mover;
    Transform[][]? _model;

    public Skeleton Skeleton { get; }
    public int LastTick { get; }
    public int Frames => LastTick + 1;
    /// <summary>By bone index: whether the motion keys the bone's rotation (a track or a channel), else it holds its rest.</summary>
    public bool[] RotationKeyed { get; }
    /// <summary>By bone index: whether the motion keys the bone's position.</summary>
    public bool[] PositionKeyed { get; }
    /// <summary>Whether the motion has a mover track (else the mover is the identity throughout).</summary>
    public bool HasMover { get; }

    /// <summary>A motion from per-tick locals (<c>[tick][bone]</c>) and movers (<c>[tick]</c>). Every array must hold one entry a tick and
    /// one transform a bone.</summary>
    public Motion(Skeleton skeleton, Transform[][] locals, Transform[] mover, bool[] rotationKeyed, bool[] positionKeyed, bool hasMover)
    {
        int n = skeleton.Bones.Count;
        if (locals.Length == 0 || locals.Length != mover.Length) throw new ArgumentException($"{locals.Length} ticks of locals and {mover.Length} of the mover");
        if (locals.Any(l => l is null || l.Length != n)) throw new ArgumentException($"a tick without {n} bone transforms");
        if (rotationKeyed.Length != n || positionKeyed.Length != n) throw new ArgumentException($"the keyed flags need {n} bones");
        Skeleton = skeleton;
        _locals = locals;
        _mover = mover;
        LastTick = locals.Length - 1;
        RotationKeyed = rotationKeyed;
        PositionKeyed = positionKeyed;
        HasMover = hasMover;
    }

    public Transform Local(int tick, int bone) => _locals[tick][bone];
    public Transform Mover(int tick) => _mover[tick];

    /// <summary>The bone's transform in model space (relative to the mover) at a tick.</summary>
    public Transform Model(int tick, int bone) => Models()[tick][bone];

    /// <summary>The bone's transform in the clip's world (the mover applied) at a tick.</summary>
    public Transform World(int tick, int bone) => _mover[tick] * Models()[tick][bone];

    Transform[][] Models()
    {
        if (_model is not null) return _model;
        var m = new Transform[_locals.Length][];
        for (int t = 0; t < m.Length; t++)
        {
            m[t] = new Transform[Skeleton.Bones.Count];
            Skeleton.ComputeModel(_locals[t], m[t]);
        }
        return _model = m;
    }

    /// <summary>A bone's index by name, or -1.</summary>
    public int Bone(string name) => Skeleton.IndexOf(Hashing.Crc32.Of(name));

    /// <summary>This motion mirrored through the body's middle plane the way a mirror edit mirrors a clip: each mirrored bone's local turn
    /// and place go to its counterpart (<see cref="Edits.SkeletonMirror.LocalRot"/>, <see cref="Edits.SkeletonMirror.LocalPos"/>), so a
    /// left forearm's roll becomes the right forearm's roll the other way; a pair neither of which keys a turn (or a place) keeps its rest;
    /// a bone with no counterpart keeps its own; the mover is mirrored in the frame of its first place. The keyed flags stay as they are (a
    /// mirror keeps the clip's track list). What the checks compare a mirror's result with: each bone against its counterpart in the game's
    /// clip, its twist and its sided angles turned the other way.</summary>
    public Motion Mirrored(Edits.SkeletonMirror mirror)
    {
        if (mirror.Skeleton != Skeleton) throw new ArgumentException("the mirror is built on another skeleton than the motion's");
        int n = Skeleton.Bones.Count;
        var pair = mirror.Pair;
        var frame = _mover[0];
        var locals = new Transform[_locals.Length][];
        var movers = new Transform[_locals.Length];
        for (int t = 0; t < locals.Length; t++)
        {
            var src = _locals[t];
            var l = (Transform[])src.Clone();
            for (int b = 0; b < n; b++)
            {
                int c = pair[b];
                if (c < 0) continue;
                var rot = RotationKeyed[b] || RotationKeyed[c] ? mirror.LocalRot(b, src[b].Rotation) : src[c].Rotation;
                var pos = PositionKeyed[b] || PositionKeyed[c] ? mirror.LocalPos(b, src[b].Position) : src[c].Position;
                l[c] = new Transform(pos, rot);
            }
            locals[t] = l;
            movers[t] = mirror.InFrame(frame, _mover[t]);
        }
        return new Motion(Skeleton, locals, movers, RotationKeyed, PositionKeyed, HasMover);
    }

    /// <summary>A clip decoded at every tick on <paramref name="skeleton"/>, as the viewer poses it: a track of a bone's hash sets that
    /// bone's local rotation or position (the later of two tracks of one kind wins), the rest pose holds elsewhere, track 0 is the mover.
    /// Throws <see cref="InvalidDataException"/> for a clip whose curves don't read to its end or a value that isn't a number.</summary>
    public static Motion FromClip(Skeleton skeleton, ClipRecord clip, SharedCurveTable? table)
    {
        if (!clip.WalkedToEnd) throw new InvalidDataException($"{clip.Name}'s curves don't read to the record's end: {clip.Stop}");
        var sampler = new ClipSampler(clip, table);
        var ch = sampler.Channels;
        int n = skeleton.Bones.Count;
        var bone = new int[ch.Count];
        var rk = new bool[n];
        var pk = new bool[n];
        bool mover = false;
        for (int i = 0; i < ch.Count; i++)
        {
            uint h = ch[i].Track;
            bone[i] = h == 0 ? -1 : skeleton.IndexOf(h) is var b and >= 0 ? b : -2;
            if (bone[i] == -1) mover = true;
            else if (bone[i] >= 0) { if (ch[i].Rotation) rk[bone[i]] = true; else pk[bone[i]] = true; }
        }
        int frames = clip.LastTick + 1;
        var locals = new Transform[frames][];
        var movers = new Transform[frames];
        for (int t = 0; t < frames; t++)
        {
            sampler.At(t / (float)ClipRecord.TicksPerSecond);
            var l = new Transform[n];
            for (int b = 0; b < n; b++) l[b] = skeleton.Bones[b].LocalRest;
            var mp = Vector3.Zero;
            var mq = Quaternion.Identity;
            for (int i = 0; i < ch.Count; i++)
            {
                int b = bone[i];
                if (b == -2) continue;
                bool rot = ch[i].Rotation;
                if (rot && !float.IsFinite(sampler.Rotations[i].X + sampler.Rotations[i].Y + sampler.Rotations[i].Z + sampler.Rotations[i].W))
                    throw new InvalidDataException($"{clip.Name}: track {ch[i].TrackIndex}'s rotation at tick {t} isn't a number");
                if (!rot && !float.IsFinite(sampler.Positions[i].X + sampler.Positions[i].Y + sampler.Positions[i].Z))
                    throw new InvalidDataException($"{clip.Name}: track {ch[i].TrackIndex}'s position at tick {t} isn't a number");
                if (b == -1) { if (rot) mq = sampler.Rotations[i]; else mp = sampler.Positions[i]; continue; }
                l[b] = rot ? l[b] with { Rotation = sampler.Rotations[i] } : l[b] with { Position = sampler.Positions[i] };
            }
            locals[t] = l;
            movers[t] = new Transform(mp, mq);
        }
        return new Motion(skeleton, locals, movers, rk, pk, mover);
    }
}
