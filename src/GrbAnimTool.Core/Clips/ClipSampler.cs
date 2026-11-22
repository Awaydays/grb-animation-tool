using System.Numerics;

namespace GrbAnimTool.Core.Clips;

/// <summary>One clip's rotation and position tracks sampled at any time: each track's keys found around the time and interpolated, slerp
/// for rotations and lerp for positions. Between keys this is the sampler's own blend, not necessarily the game's evaluator.</summary>
public sealed class ClipSampler
{
    /// <summary>A sampled track: its hash, its curve (references followed), its key times, and whether it is a rotation.</summary>
    public sealed record Channel(int TrackIndex, uint Track, Curve Curve, int[] Ticks, bool Rotation);

    readonly Channel[] _c;

    /// <summary>Per channel, the value of the last <see cref="At"/>: <see cref="Rotations"/> for a rotation channel, <see cref="Positions"/>
    /// for a position one.</summary>
    public readonly Quaternion[] Rotations;
    public readonly Vector3[] Positions;

    public IReadOnlyList<Channel> Channels => _c;

    /// <summary>The clip's rotation and position tracks whose first key decodes; with <paramref name="only"/>, only the tracks it holds.</summary>
    public ClipSampler(ClipRecord c, SharedCurveTable? table, IReadOnlySet<uint>? only = null)
    {
        var list = new List<Channel>();
        for (int i = 0; i < c.Curves.Count && i < c.Tracks.Count; i++)
        {
            if (only is not null && !only.Contains(c.Tracks[i])) continue;
            var u = SharedCurveTable.Resolve(c.Curves[i], table);
            if (u is null) continue;
            bool rot = CurveDecoder.KeyRot(u) is not null;
            bool pos = CurveDecoder.KeyPos(u) is not null;
            if (!rot && !pos) continue;
            list.Add(new Channel(i, c.Tracks[i], u, CurveDecoder.KeyTicks(u), rot));
        }
        _c = [.. list];
        Rotations = new Quaternion[_c.Length];
        Positions = new Vector3[_c.Length];
    }

    /// <summary>Samples every channel at <paramref name="seconds"/> (60 ticks a second): the last key at or before the tick and the next one,
    /// blended by the time between them. Before the first key or after the last, the nearest key holds.</summary>
    public void At(float seconds)
    {
        float tick = seconds * ClipRecord.TicksPerSecond;
        for (int i = 0; i < _c.Length; i++)
        {
            var (_, _, u, t, rot) = _c[i];
            int k = Array.BinarySearch(t, (int)MathF.Floor(tick));
            if (k < 0) k = ~k - 1;
            if (k < 0) k = 0;
            while (k + 1 < t.Length && t[k + 1] <= tick) k++;
            float f = k + 1 < t.Length && t[k + 1] > t[k] ? Math.Clamp((tick - t[k]) / (t[k + 1] - t[k]), 0, 1) : 0;
            if (rot)
            {
                var a = CurveDecoder.KeyRot(u, k) ?? CurveDecoder.KeyRot(u, 0)!.Value;
                if (f > 0 && CurveDecoder.KeyRot(u, k + 1) is { } b) a = Quaternion.Slerp(a, b, f);
                Rotations[i] = a;
            }
            else
            {
                var a = CurveDecoder.KeyPos(u, k) ?? CurveDecoder.KeyPos(u, 0)!.Value;
                if (f > 0 && CurveDecoder.KeyPos(u, k + 1) is { } b) a = Vector3.Lerp(a, b, f);
                Positions[i] = a;
            }
        }
    }

    /// <summary>The last <see cref="At"/>'s values by track hash: rotations into <paramref name="rot"/>, positions into <paramref name="pos"/>.</summary>
    public void CopyTo(IDictionary<uint, Quaternion> rot, IDictionary<uint, Vector3> pos)
    {
        for (int i = 0; i < _c.Length; i++)
            if (_c[i].Rotation) rot[_c[i].Track] = Rotations[i];
            else pos[_c[i].Track] = Positions[i];
    }
}
