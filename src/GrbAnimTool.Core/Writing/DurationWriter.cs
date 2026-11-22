using System.Buffers.Binary;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;

namespace GrbAnimTool.Core.Writing;

/// <summary>The step objects' time list: after a clip's last object of class 0x1C898E8A (one object a step) stands a u32 byte length
/// (2 + 2 x the count), a u16 count equal to the number of step objects, then that many u16 times in 1/120 s, rising. This finds a list
/// only where a clip's event group holds step events alone; <see cref="DurationWriter"/> reads every group's list through
/// <see cref="EventObjects.Groups"/> instead.</summary>
/// <param name="At">The offset of the list's u32 length in the record.</param>
/// <param name="Times">The times, in 1/120 s.</param>
public sealed record StepTimeList(int At, int[] Times)
{
    /// <summary>The class of a clip's step objects; its bytes stand in the record little-endian. In the game's clips the word stands 24
    /// bytes into each step event's object (class <see cref="EventObjects.StepEvent"/>).</summary>
    public const uint StepClass = 0x1C898E8A;

    /// <summary>The offset of the first time.</summary>
    public int TimesAt => At + 6;

    /// <summary>The step time list of a record, or null when it holds no step object or no list after the last one. Only the record's
    /// part before its curves is searched, so a curve value can't pass for a step object. Every read is checked against the record's end.</summary>
    public static StepTimeList? Find(ClipRecord clip) => Find(clip.Bytes, SearchEnd(clip));

    /// <summary>As <see cref="Find(ClipRecord)"/>, searching <paramref name="b"/> up to <paramref name="end"/>.</summary>
    public static StepTimeList? Find(byte[] b, int end)
    {
        end = Math.Clamp(end, 0, b.Length);
        var s = b.AsSpan(0, end);
        var at = StepWords(b, end);
        if (at.Count == 0) return null;
        int last = at[^1], n = at.Count;
        for (int q = last + 16; q + 6 <= end && q < last + 80; q++)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(s[q..]);
            ushort cnt = BinaryPrimitives.ReadUInt16LittleEndian(s[(q + 4)..]);
            if (len != 2 + 2 * cnt || cnt != n || q + 6 + 2L * cnt > end) continue;
            var v = new int[cnt];
            for (int i = 0; i < cnt; i++) v[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[(q + 6 + 2 * i)..]);
            return new StepTimeList(q, v);
        }
        return null;
    }

    /// <summary>Where the step class's word stands in <paramref name="b"/> before <paramref name="end"/>, in file order.</summary>
    public static List<int> StepWords(byte[] b, int end)
    {
        end = Math.Clamp(end, 0, b.Length);
        ReadOnlySpan<byte> word = [0x8A, 0x8E, 0x89, 0x1C];
        var s = b.AsSpan(0, end);
        var r = new List<int>();
        for (int at = s.IndexOf(word); at >= 0;)
        {
            r.Add(at);
            int nx = s[(at + 1)..].IndexOf(word);
            at = nx < 0 ? -1 : at + 1 + nx;
        }
        return r;
    }

    /// <summary>The end of the part of a record searched for step objects: up to the curves' wrapper when the curves read to the end.</summary>
    internal static int SearchEnd(ClipRecord clip) => clip.WalkedToEnd ? clip.FirstCurve - 12 : clip.Bytes.Length;
}

/// <summary>One event group's time list as the duration writer scaled it.</summary>
/// <param name="GroupAt">The offset of the group's first object.</param>
/// <param name="TimesAt">The offset of the list's first time.</param>
/// <param name="Before">The times before, in 1/120 s.</param>
/// <param name="After">The times written.</param>
public sealed record ScaledTimeList(int GroupAt, int TimesAt, IReadOnlyList<int> Before, IReadOnlyList<int> After);

/// <summary>What the duration writer made: the record, every time list it scaled, and how many step objects the record holds (each one's
/// time is in a scaled list).</summary>
public sealed record DurationChange(byte[] Bytes, float OldSeconds, float NewSeconds, IReadOnlyList<ScaledTimeList> Lists, int StepObjects);

/// <summary>Writes a clip's duration. The clip's own record is where its length stands (an f32 in seconds, 13 bytes after the ID); the
/// times of its events (steps, sounds and the rest, one time list per event group, read by <see cref="EventObjects.Groups"/>) scale with
/// it. A record holding an event or a step object whose list can't be found is refused. So is a record whose byte array holds a list of
/// times (<see cref="ByteArrayTimes"/>), or a block there that doesn't read by its rule: those times are read but not scaled yet, and a new
/// length would leave them out of step.
/// No time the tool can find is left unscaled without a word. The curves are not touched: keys past the new end, or a clip made longer
/// than its keys, are the caller's to rewrite (by <see cref="ClipRebuild"/>) before or after. A node in the graph may hold the length too
/// (a motion field's segment table); changing that is not done here.</summary>
public static class DurationWriter
{
    /// <summary>Why a clip whose byte array holds times is refused, in plain words.</summary>
    public const string UnreadTimesReason = "this clip's byte array holds a list of times the tool doesn't adjust yet; changing its length could leave them out of step";

    /// <summary>Whether <see cref="Change"/> (scaling the event times) takes <paramref name="clip"/> at all, whatever its new length; when
    /// not, <paramref name="reason"/> says why in plain words. A new length out of range, or a time it would scale past a u16, is still
    /// refused by <see cref="Change"/> itself.</summary>
    public static bool CanChange(ClipRecord clip, out string reason)
    {
        try
        {
            Change(clip, clip.DurationValid ? clip.Duration : 1, scaleTimes: true);
            reason = "";
            return true;
        }
        catch (RebuildRefusedException ex)
        {
            string p = clip.Name + ": ";
            reason = ex.Message.StartsWith(p, StringComparison.Ordinal) ? ex.Message[p.Length..] : ex.Message;
            return false;
        }
    }

    /// <summary>A copy of <paramref name="clip"/>'s record with the duration <paramref name="seconds"/> and, when
    /// <paramref name="scaleSteps"/>, every event time scaled by the new length over the old (see <see cref="Change"/>).</summary>
    public static byte[] WithDuration(ClipRecord clip, float seconds, bool scaleSteps = true) => Change(clip, seconds, scaleSteps).Bytes;

    /// <summary>The record of <paramref name="clip"/> with the duration <paramref name="seconds"/>; with <paramref name="scaleTimes"/>, each
    /// event group's time list scaled as <c>(int)MathF.Round(t * seconds / old)</c>: in float, rounded to even, so a time near a half step
    /// rounds the way a float computation of it does. Refuses (<see cref="RebuildRefusedException"/>): a new or old duration out of range; a record whose curves don't
    /// read to its end; a record whose byte array holds a list of times (<see cref="ByteArrayTimes.HoldsTimes"/>, with or without
    /// <paramref name="scaleTimes"/>); with <paramref name="scaleTimes"/>, a group of events whose time list isn't found or doesn't lie in its last event's
    /// object, a step object outside every listed group, and a scaled time past a u16 or out of the old times' order.</summary>
    public static DurationChange Change(ClipRecord clip, float seconds, bool scaleTimes = true)
    {
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > ClipRecord.MaxDuration)
            throw new RebuildRefusedException($"{clip.Name}: a duration of {seconds} s is outside (0, {ClipRecord.MaxDuration}] s");
        if (!clip.DurationValid)
            throw new RebuildRefusedException($"{clip.Name}: its own duration, {clip.Duration} s, is outside (0, {ClipRecord.MaxDuration}] s: the record is damaged");
        if (!clip.WalkedToEnd) throw new RebuildRefusedException($"{clip.Name}: its curves don't read to the record's end ({clip.Stop})");
        if (ByteArrayTimes.HoldsTimes(clip)) throw new RebuildRefusedException($"{clip.Name}: {UnreadTimesReason}");
        float old = clip.Duration;
        var r = (byte[])clip.Bytes.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(clip.IdAt + 13), seconds);
        var steps = StepTimeList.StepWords(clip.Bytes, StepTimeList.SearchEnd(clip));
        var lists = new List<ScaledTimeList>();
        if (scaleTimes)
        {
            var objects = EventObjects.Before(clip);
            var all = RecordObjects.Objects(clip.Bytes);
            var groups = EventObjects.Groups(clip);
            foreach (var g in groups)
            {
                if (g.Events.Count == 0) continue;
                if (g.TimesAt < 0)
                    throw new RebuildRefusedException($"{clip.Name}: the event group at +0x{g.At:X} holds {g.Events.Count} events and its time list wasn't found, so their times can't scale with the length");
                // the list stands at the end of the group's last event object: it may not run into the next object
                int next = all.FindIndex(o => o.At > g.TimesAt);
                long end = g.TimesAt + 2L * g.Times.Count;
                if (next < 0 || end > all[next].At || end > clip.FirstCurve)
                    throw new RebuildRefusedException($"{clip.Name}: the time list of the event group at +0x{g.At:X} runs past its object");
                var after = new int[g.Times.Count];
                for (int i = 0; i < after.Length; i++)
                {
                    int t = g.Times[i];
                    int v = (int)MathF.Round(t * seconds / old);
                    if (v > ushort.MaxValue) throw new RebuildRefusedException($"{clip.Name}: event time {t} scaled to {v} is past a u16");
                    if (i > 0 && t >= g.Times[i - 1] && v < after[i - 1])
                        throw new RebuildRefusedException($"{clip.Name}: the scaled event times don't keep their order ({g.Times[i - 1]} {t} -> {after[i - 1]} {v})");
                    after[i] = v;
                    BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(g.TimesAt + 2 * i), (ushort)v);
                }
                lists.Add(new ScaledTimeList(g.At, g.TimesAt, g.Times, after));
            }
            // every step object must lie in an event of a group whose list was scaled
            foreach (int s in steps)
            {
                var o = objects.LastOrDefault(x => x.At <= s);
                bool inside = o is not null && s + 4 <= o.At + o.Length;
                int gi = inside ? groups.FindLastIndex(x => x.At <= o!.At) : -1;
                if (gi < 0 || groups[gi].TimesAt < 0 || groups[gi].Events.Count == 0)
                    throw new RebuildRefusedException($"{clip.Name}: it holds {steps.Count} step objects, and the time list of the one at +0x{s:X} wasn't found, so its time can't scale with the length");
            }
        }
        var y = ClipRecord.Parse(r);
        if (!y.WalkedToEnd || y.Duration != seconds || y.Id != clip.Id || !y.Tracks.SequenceEqual(clip.Tracks))
            throw new RebuildRefusedException($"{clip.Name}: the record with its new duration doesn't read back ({y.Stop})");
        var back = EventObjects.Groups(y).Where(g => g.TimesAt >= 0).ToDictionary(g => g.TimesAt, g => g.Times);
        foreach (var l in lists)
            if (!back.TryGetValue(l.TimesAt, out var t) || !t.SequenceEqual(l.After))
                throw new RebuildRefusedException($"{clip.Name}: the scaled times of the event group at +0x{l.GroupAt:X} don't read back");
        return new DurationChange(r, old, seconds, lists, steps.Count);
    }
}
