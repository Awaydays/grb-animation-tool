using System.Globalization;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Export;

namespace GrbAnimTool.Core.Library;

/// <summary>One event of a clip at its time: the event group it stands in, its main object's class and type byte, and its time in seconds
/// (the group's time list holds 1/120 s).</summary>
public sealed record EventMark(double Seconds, int Group, int Index, uint Class, int Type)
{
    /// <summary>The event in words, for a marker's hover text: its kind, class, type and time.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{KindOf(Class)} event, class {Class:X8}{(Type != 0 ? $", type {Type}" : "")}, at {Seconds:F3} s (group {Group + 1}, event {Index + 1})");

    /// <summary>The kind of an event's main object by its class: a sound, a step, else the class alone.</summary>
    public static string KindOf(uint cls) => cls switch
    {
        EventObjects.SoundEvent => "sound",
        EventObjects.StepEvent => "step",
        _ => "other",
    };
}

/// <summary>A foot planted: the plant track's slot (0 to 7), the foot (0 left, 1 right) and the ticks it is planted from and to.</summary>
public sealed record PlantSpan(int Slot, int Foot, int From, int To)
{
    public string FootName => Foot == 0 ? "left" : "right";
}

/// <summary>What the viewer marks on a clip: its events on the timeline, its foot plants on the timeline and in the viewport, and the feet
/// that go below the floor. Read from the record as it is; nothing here changes it.</summary>
public static class ClipMarks
{
    /// <summary>A foot joint deeper than this below the floor (z = 0) is drawn red: the floor check's own warning depth.</summary>
    public const float BelowFloorCm = MotionChecks.FloorWarnCm;

    /// <summary>The joints of each foot, left then right.</summary>
    public static readonly string[][] FootBones = [["LeftFoot", "LeftToeBase"], ["RightFoot", "RightToeBase"]];

    /// <summary>Every event whose time the clip's event groups list, in time order. An event group whose time list wasn't found gives no
    /// marks; an event past the list's count gives none either.</summary>
    public static List<EventMark> Events(ClipRecord clip)
    {
        var l = new List<EventMark>();
        List<EventGroup> groups;
        try { groups = EventObjects.Groups(clip); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return l; }
        for (int g = 0; g < groups.Count; g++)
        {
            var grp = groups[g];
            for (int i = 0; i < grp.Times.Count; i++)
            {
                var ev = i < grp.Events.Count ? grp.Events[i] : null;
                l.Add(new EventMark(grp.Times[i] / 120.0, g, i, ev?.Class ?? 0, ev?.Type ?? 0));
            }
        }
        return l.OrderBy(e => e.Seconds).ThenBy(e => e.Group).ThenBy(e => e.Index).ToList();
    }

    /// <summary>Every foot plant the clip's plant tracks hold (slots 0xFFFFFFE4 to 0xFFFFFFEB; an even slot is the left foot), each from
    /// its key's tick for its length, clamped to the clip. A key of length 0 (a foot in the air at the start) isn't a plant.</summary>
    public static List<PlantSpan> Plants(ClipRecord clip, SharedCurveTable? table)
    {
        var l = new List<PlantSpan>();
        for (int i = 0; i < clip.Tracks.Count && i < clip.Curves.Count; i++)
        {
            uint h = clip.Tracks[i];
            if (!EditTracks.IsPlantSlot(h)) continue;
            var u = SharedCurveTable.Resolve(clip.Curves[i], table);
            if (u is null || u.Kind != TrackKind.Plant) continue;
            int slot = (int)(h - EditTracks.FirstPlantSlot);
            for (int k = 0; k < u.Keys; k++)
            {
                if (CurveDecoder.Plant(u, k) is not { } p || p.LengthTicks <= 0) continue;
                int from = Math.Clamp(p.Tick, 0, clip.LastTick), to = Math.Clamp(p.Tick + p.LengthTicks, 0, clip.LastTick);
                if (to > from) l.Add(new PlantSpan(slot, slot % 2, from, to));
            }
        }
        return l.OrderBy(p => p.From).ThenBy(p => p.Foot).ToList();
    }

    /// <summary>Whether <paramref name="foot"/> (0 left, 1 right) is planted at <paramref name="tick"/> by any of <paramref name="plants"/>.</summary>
    public static bool Planted(IReadOnlyList<PlantSpan> plants, int foot, int tick) =>
        plants.Any(p => p.Foot == foot && tick >= p.From && tick <= p.To);

    /// <summary>The plants as the checks take them: the foot, the first tick and the length.</summary>
    public static List<(int Slot, int Tick, int Length)> ForChecks(IEnumerable<PlantSpan> plants) =>
        plants.Select(p => (p.Foot, p.From, p.To - p.From)).ToList();

    /// <summary>Whether a joint at height <paramref name="z"/> (metres) is below the floor by more than <see cref="BelowFloorCm"/>.</summary>
    public static bool BelowFloor(float z) => z < -BelowFloorCm / 100f;

    /// <summary>The place of <paramref name="seconds"/> on a timeline <paramref name="width"/> wide that spans 0 to
    /// <paramref name="duration"/>, clamped to it; 0 for a clip of no length.</summary>
    public static double X(double seconds, double duration, double width) =>
        duration > 0 && width > 0 ? Math.Clamp(seconds / duration, 0, 1) * width : 0;
}
