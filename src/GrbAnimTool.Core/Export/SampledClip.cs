using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Export;

/// <summary>A clip's rotation and position tracks sampled at every tick, 0 to its last, on a skeleton, in the game's axes: per skeleton bone
/// its local rotation and position where the clip has a track for it, and the mover's (track 0). Tracks naming no skeleton bone aren't
/// sampled here (the sidecar carries them). Where a bone has two tracks of one kind, the later one counts, as in the viewer's pose.</summary>
public sealed class SampledClip
{
    public required int LastTick { get; init; }
    /// <summary>By bone index: the local rotation at each tick, or null where the clip has no rotation track for the bone.</summary>
    public required Quaternion[]?[] Rotations { get; init; }
    /// <summary>By bone index: the local position at each tick, or null where the clip has no position track for the bone.</summary>
    public required Vector3[]?[] Positions { get; init; }
    public Quaternion[]? MoverRotation { get; init; }
    public Vector3[]? MoverPosition { get; init; }
    /// <summary>The track indices the samples come from: the rotation and position track per bone (-1 for none), then the mover's.</summary>
    public required int[] RotationTrack { get; init; }
    public required int[] PositionTrack { get; init; }
    public int MoverRotationTrack { get; init; } = -1;
    public int MoverPositionTrack { get; init; } = -1;

    /// <summary>The tick count: one frame per tick, tick 0 included.</summary>
    public int Frames => LastTick + 1;

    /// <summary>Samples a clip that walked to its end and states a duration in range. Throws <see cref="InvalidDataException"/> for any
    /// other, or when a sampled value isn't finite.</summary>
    public static SampledClip Of(Skeleton s, ClipRecord c, SharedCurveTable? table)
    {
        if (!c.WalkedToEnd) throw new InvalidDataException($"clip {c.Name}'s curves don't read to the record's end: {c.Stop}");
        var sampler = new ClipSampler(c, table);
        var ch = sampler.Channels;
        int n = s.Bones.Count;
        var rotCh = Enumerable.Repeat(-1, n).ToArray();
        var posCh = Enumerable.Repeat(-1, n).ToArray();
        int moverRot = -1, moverPos = -1;
        for (int i = 0; i < ch.Count; i++)
        {
            uint h = ch[i].Track;
            if (h == 0) { if (ch[i].Rotation) moverRot = i; else moverPos = i; continue; }
            int b = s.IndexOf(h);
            if (b < 0) continue;
            if (ch[i].Rotation) rotCh[b] = i; else posCh[b] = i;
        }
        int frames = c.LastTick + 1;
        var rot = new Quaternion[]?[n];
        var pos = new Vector3[]?[n];
        for (int b = 0; b < n; b++)
        {
            if (rotCh[b] >= 0) rot[b] = new Quaternion[frames];
            if (posCh[b] >= 0) pos[b] = new Vector3[frames];
        }
        var mr = moverRot >= 0 ? new Quaternion[frames] : null;
        var mp = moverPos >= 0 ? new Vector3[frames] : null;
        for (int t = 0; t < frames; t++)
        {
            sampler.At(t / (float)ClipRecord.TicksPerSecond);
            for (int b = 0; b < n; b++)
            {
                if (rotCh[b] >= 0) rot[b]![t] = Finite(sampler.Rotations[rotCh[b]], s.Bones[b].Name, t);
                if (posCh[b] >= 0) pos[b]![t] = Finite(sampler.Positions[posCh[b]], s.Bones[b].Name, t);
            }
            if (mr is not null) mr[t] = Finite(sampler.Rotations[moverRot], "the mover", t);
            if (mp is not null) mp[t] = Finite(sampler.Positions[moverPos], "the mover", t);
        }
        return new SampledClip
        {
            LastTick = c.LastTick, Rotations = rot, Positions = pos, MoverRotation = mr, MoverPosition = mp,
            RotationTrack = rotCh.Select(i => i >= 0 ? ch[i].TrackIndex : -1).ToArray(),
            PositionTrack = posCh.Select(i => i >= 0 ? ch[i].TrackIndex : -1).ToArray(),
            MoverRotationTrack = moverRot >= 0 ? ch[moverRot].TrackIndex : -1,
            MoverPositionTrack = moverPos >= 0 ? ch[moverPos].TrackIndex : -1,
        };
    }

    static Quaternion Finite(Quaternion q, string what, int t) =>
        float.IsFinite(q.X + q.Y + q.Z + q.W) ? q : throw new InvalidDataException($"{what}'s rotation at tick {t} isn't a number");

    static Vector3 Finite(Vector3 p, string what, int t) =>
        float.IsFinite(p.X + p.Y + p.Z) ? p : throw new InvalidDataException($"{what}'s position at tick {t} isn't a number");

    /// <summary>The track indices this clip's samples carry: every track the .glb holds.</summary>
    public IEnumerable<int> CarriedTracks() =>
        RotationTrack.Concat(PositionTrack).Append(MoverRotationTrack).Append(MoverPositionTrack).Where(i => i >= 0);
}
