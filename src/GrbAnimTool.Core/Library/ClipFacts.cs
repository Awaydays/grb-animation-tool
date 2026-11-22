using System.Globalization;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Library;

/// <summary>What a clip's tracks hold, for an info panel: how many tracks are rotations, positions or anything else (floats, bytes, plants,
/// kinds not read, and tracks whose curve can't be found), and whether it has a mover (track 0 with a rotation or position curve).</summary>
public sealed record ClipFacts(int Rotations, int Positions, int Others, bool HasMover)
{
    public static ClipFacts Of(ClipRecord clip, SharedCurveTable? table)
    {
        int rot = 0, pos = 0, other = 0;
        bool mover = false;
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            var kind = i < clip.Curves.Count ? SharedCurveTable.Resolve(clip.Curves[i], table)?.Kind ?? TrackKind.Unknown : TrackKind.Unknown;
            switch (kind)
            {
                case TrackKind.Rotation: rot++; break;
                case TrackKind.Position: pos++; break;
                default: other++; break;
            }
            if (clip.Tracks[i] == 0 && kind is TrackKind.Rotation or TrackKind.Position) mover = true;
        }
        return new ClipFacts(rot, pos, other, mover);
    }

    /// <summary>Why a viewer can't play <paramref name="clip"/>, in plain words, or null when it can: its record can't be read, its
    /// duration is out of range, its curves don't read to the record's end, or it is shorter than one tick.</summary>
    public static string? WhyNotShown(ClipEntry clip)
    {
        var r = clip.Record;
        if (r is null) return $"its record can't be read ({clip.ParseError ?? "no reason given"})";
        if (!r.DurationValid) return $"its duration ({r.Duration.ToString(CultureInfo.InvariantCulture)} s) isn't above 0 and at most {ClipRecord.MaxDuration} s";
        if (!r.WalkedToEnd) return $"its curves can't be read to the record's end ({r.Stop})";
        if (r.LastTick == 0) return "it is shorter than one tick (1/60 s)";
        return null;
    }
}
