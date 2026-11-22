using System.Buffers.Binary;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Export;

/// <summary>An object before a clip's track list, as found by the handle pattern: its offset, class and the bytes to the next object.</summary>
public sealed record RecordObject(int At, uint Class, int Length);

/// <summary>An event inside a group: its main object (an <c>A85DEF18</c> sound event or an <c>0EFFFE90</c> step event; else the event's
/// last object), its class, and its type byte where that class has one (a step's at +43, a sound's at +13), else 0.</summary>
public sealed record ClipEvent(int At, uint Class, int Type);

/// <summary>A group of events: opened by an object of class <c>41682213</c> (whose u16 at +14 is the group's event count), its events each
/// opened by a 13-byte object, and its time list at the end of its last event object (a u32 byte length 2 + 2 x the count, a u16 count, the
/// times in 1/120 s). <see cref="TimesAt"/> is -1 when no list of the count was found there.</summary>
public sealed record EventGroup(int At, int DeclaredCount, IReadOnlyList<ClipEvent> Events, int TimesAt, IReadOnlyList<int> Times);

/// <summary>Reads, best effort, the objects that stand before a clip's track list and their event groups with their times. Objects are
/// found by their handle pattern (<see cref="RecordObjects"/>) and their extent is taken as the distance to the next one, so this reads
/// what is there without claiming every object's layout; nothing it finds enters the pose. Every read is checked against the record's
/// end.</summary>
public static class EventObjects
{
    public const uint GroupClass = 0x41682213;
    public const uint SoundEvent = 0xA85DEF18;
    public const uint StepEvent = 0x0EFFFE90;

    /// <summary>The objects before the track list's head object (class <see cref="RecordObjects.TrackListClass"/>), in file order.</summary>
    public static List<RecordObject> Before(ClipRecord c)
    {
        var b = c.Bytes;
        var all = RecordObjects.Objects(b);
        int list = all.FindLastIndex(o => o.Class == RecordObjects.TrackListClass);
        var r = new List<RecordObject>();
        for (int i = 0; i < list; i++)
            r.Add(new RecordObject(all[i].At, all[i].Class, all[i + 1].At - all[i].At));
        return r;
    }

    /// <summary>The event groups of the objects before the track list. Objects before the first group object form a group of their own
    /// with no declared count (-1).</summary>
    public static List<EventGroup> Groups(ClipRecord c)
    {
        var b = c.Bytes;
        var groups = new List<(int At, int Declared, List<RecordObject> Objs)>();
        foreach (var o in Before(c))
        {
            if (o.Class == GroupClass)
            {
                int declared = o.Length >= 16 && o.At + 16 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o.At + 14)) : -1;
                groups.Add((o.At, declared, []));
                continue;
            }
            if (groups.Count == 0) groups.Add((o.At, -1, []));
            groups[^1].Objs.Add(o);
        }
        var result = new List<EventGroup>();
        foreach (var (at, declared, objs) in groups)
        {
            // each event from its 13-byte opening object to the next one
            var evs = new List<List<RecordObject>>();
            foreach (var o in objs) { if (o.Length == 13) evs.Add([]); else if (evs.Count > 0) evs[^1].Add(o); }
            evs.RemoveAll(e => e.Count == 0);
            var events = evs.Select(e =>
            {
                var main = e.FirstOrDefault(o => o.Class is SoundEvent or StepEvent) ?? e[^1];
                int typeAt = main.Class == StepEvent ? 43 : main.Class == SoundEvent ? 13 : -1;
                int type = typeAt >= 0 && typeAt < main.Length && main.At + typeAt < b.Length ? b[main.At + typeAt] : 0;
                return new ClipEvent(main.At, main.Class, type);
            }).ToList();
            int k = declared >= 0 ? declared : events.Count;
            int found = -1;
            if (evs.Count > 0 && k > 0)
            {
                var last = evs[^1][^1];
                for (long q = last.At + 12; q + 6 <= last.At + last.Length && q + 6 <= b.Length; q++)
                {
                    uint len = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)q));
                    ushort cnt = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)q + 4));
                    if (cnt == k && len == 2 + 2 * cnt && q + 6 + 2L * cnt <= b.Length) { found = (int)q + 6; break; }
                }
            }
            var times = new List<int>();
            if (found >= 0) for (int j = 0; j < k; j++) times.Add(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(found + 2 * j)));
            result.Add(new EventGroup(at, declared, events, found, times));
        }
        return result;
    }
}
