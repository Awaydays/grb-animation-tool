using System.Buffers.Binary;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Retiming;

/// <summary>The time lists before a clip's tracks, re-timed in place: every event group's list (sound events, step events, the timed objects
/// of class 0x83EC6C6D and any other kind in a group) and the step objects' list where it isn't a group's. Each time (a u16 in 1/120 s) is
/// taken through the warp's inverse and rounded; a time the warp cuts off is clamped to the new clip's start or end, as an event object can't
/// be dropped. The objects themselves stay byte for byte.</summary>
public static class EventRetimer
{
    /// <summary>One time list: where its first time stands, its count, what it is.</summary>
    public sealed record TimeList(int TimesAt, int Count, string What);

    /// <summary>Every time list of a record, refusing a group of events whose list wasn't found (its times couldn't be moved).</summary>
    public static List<TimeList> Lists(ClipRecord clip)
    {
        var o = new List<TimeList>();
        var seen = new HashSet<int>();
        foreach (var g in EventObjects.Groups(clip))
        {
            if (g.Events.Count == 0) continue;
            if (g.TimesAt < 0)
                throw new EditRefusedException(EditRefusal.CantMove, $"{clip.Name}: the event group at +{g.At:X} ({g.Events.Count} events) has no time list where its last event ends, so its times can't be moved");
            if (!seen.Add(g.TimesAt)) continue;
            var kinds = g.Events.GroupBy(e => e.Class).Select(x => $"{x.Count()} x {x.Key:X8}");
            o.Add(new TimeList(g.TimesAt, g.Times.Count, $"the event group at +{g.At:X} ({string.Join(", ", kinds)})"));
        }
        if (StepTimeList.Find(clip) is { } st && seen.Add(st.TimesAt)) o.Add(new TimeList(st.TimesAt, st.Times.Length, "the step objects' list"));
        return o;
    }

    /// <summary>Re-times every list of <paramref name="clip"/> into <paramref name="record"/> (the rebuilt record, whose bytes before the
    /// curves stand where the clip's did). Returns how many times moved and how many were clamped.</summary>
    public static (int Lists, int Times, int Moved, int Clamped) Apply(ClipRecord clip, byte[] record, TimeWarp w, List<string> notes)
    {
        var lists = Lists(clip);
        int times = 0, moved = 0, clamped = 0;
        int max120 = 2 * w.NewLast;
        if (max120 > ushort.MaxValue)
            throw new EditRefusedException(EditRefusal.CantMove, $"{clip.Name}: the new clip's {w.NewLast} ticks are {max120} in the event lists' 1/120 s, past a u16");
        foreach (var l in lists)
        {
            for (int i = 0; i < l.Count; i++)
            {
                int at = l.TimesAt + 2 * i;
                if (at + 2 > clip.FirstCurve || at + 2 > record.Length) throw new EditRefusedException(EditRefusal.CantMove, $"{clip.Name}: {l.What}'s times run into the curves");
                int v = BinaryPrimitives.ReadUInt16LittleEndian(clip.Bytes.AsSpan(at));
                double x = w.Inverse(v / 2.0) * 2;
                bool cut = x < -1e-6 || x > max120 + 1e-6;
                long nv = (long)Math.Round(Math.Clamp(x, 0, max120), MidpointRounding.ToEven);
                times++;
                if (cut) clamped++;
                if (nv != v) moved++;
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(at), (ushort)nv);
            }
        }
        if (lists.Count > 0)
            notes.Add($"event times re-timed: {times} times in {lists.Count} lists ({string.Join("; ", lists.Select(x => x.What))}); {moved} moved{(clamped > 0 ? $", {clamped} cut off by the trim and clamped to its {(clamped == 1 ? "edge" : "edges")}" : "")}");
        return (lists.Count, times, moved, clamped);
    }
}
