using System.Numerics;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Library;

/// <summary>The mover's path over a whole clip: its position (track 0's position curve) at every <c>step</c> ticks and at the last tick,
/// for drawing on the floor. Empty when the clip has no mover position or its duration is out of range.</summary>
public static class MoverPath
{
    /// <summary>The most points a path holds (the last tick's point aside): a long clip is sampled at a wider step, so the path costs the
    /// same to build and draw whatever the clip's length.</summary>
    public const int MaxPoints = 4096;

    public static Vector3[] Of(ClipRecord clip, SharedCurveTable? table, int step = 2)
    {
        if (step < 1) throw new ArgumentOutOfRangeException(nameof(step));
        if (!clip.DurationValid) return [];
        var s = new ClipSampler(clip, table, new HashSet<uint> { 0 });
        int ch = -1;
        for (int i = 0; i < s.Channels.Count; i++) if (!s.Channels[i].Rotation) { ch = i; break; }
        if (ch < 0) return [];
        int last = clip.LastTick;
        step = Math.Max(step, (last + MaxPoints - 1) / MaxPoints);
        var pts = new List<Vector3>(last / step + 2);
        for (int t = 0; ; t = Math.Min(t + step, last))
        {
            s.At(Math.Min(PlaybackClock.SecondsOf(t), clip.Duration));
            pts.Add(s.Positions[ch]);
            if (t >= last) break;
        }
        return [.. pts];
    }
}
