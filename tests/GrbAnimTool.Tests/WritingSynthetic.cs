using System.Buffers.Binary;
using System.Text;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Tests;

/// <summary>Clip records built by hand for the write path's tests: a head with arena entries, an optional run of step objects with their
/// time list, the track list, and the curves; the length fields as the game's records hold them.</summary>
public static class WritingSynthetic
{
    /// <summary>A class-0 head entry: its kind and value (kind 0x15 the track count, 3 5 7 9 the arenas).</summary>
    public readonly record struct Entry(ushort Kind, uint Value);

    /// <summary>An event group of the game's form: a group object (class 0x41682213, its u16 at +14 the event count), then each event as a
    /// 13-byte opening object and its main object (a step event, class 0x0EFFFE90, 55 bytes with the step class's word at +24; or a sound
    /// event, class 0xA85DEF18, 43 bytes), the group's time list at the end of the last one, then one byte.</summary>
    public sealed record Group(int[] Times, bool[]? Sounds = null);

    public static byte[] Clip(string name, ulong id, float duration, Entry[] head, (uint Track, byte[] Curve)[] tracks, int[]? stepTimes = null,
        Group[]? groups = null, bool stepsWithoutGroup = false, byte[]? array = null)
    {
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        var nb = Encoding.ASCII.GetBytes(name);
        w.Write(ClipRecord.Type); w.Write(0u); w.Write(nb.Length); w.Write(nb);
        w.Write((byte)1); w.Write((byte)2); w.Write((ushort)0); w.Write((byte)head.Length); w.Write((byte)0); w.Write((ushort)0);
        foreach (var e in head) { w.Write(0u); w.Write((ushort)0); w.Write(e.Kind); w.Write(e.Value); }
        int idAt = (int)m.Position;
        w.Write(id); w.Write(ClipRecord.Type); w.Write((byte)0); w.Write(duration); w.Write(0x11223344u); w.Write(0x55667788u);
        uint handle = 0xF8000100;
        if (stepTimes is { Length: > 0 } && stepsWithoutGroup)
        {
            // step objects of their own, outside any event group: no list a reader of groups finds
            for (int i = 0; i < stepTimes.Length; i++)
            {
                w.Write(handle++); w.Write(0u); w.Write(StepTimeList.StepClass);
                w.Write(new byte[12]);
            }
            w.Write((uint)(2 + 2 * stepTimes.Length)); w.Write((ushort)stepTimes.Length);
            foreach (var t in stepTimes) w.Write((ushort)t);
        }
        else if (stepTimes is { Length: > 0 }) groups = [new Group(stepTimes), .. groups ?? []];
        foreach (var g in groups ?? [])
        {
            w.Write(handle++); w.Write(0u); w.Write(EventObjects.GroupClass); w.Write((ushort)0); w.Write((ushort)g.Times.Length); w.Write(new byte[3]);
            for (int i = 0; i < g.Times.Length; i++)
            {
                bool sound = g.Sounds is { } s && s[i];
                w.Write(handle++); w.Write(0u); w.Write(sound ? 0xB01B990Fu : 0x92332333u); w.Write((byte)0);
                w.Write(handle++); w.Write(0u);
                if (sound) { w.Write(EventObjects.SoundEvent); w.Write(new byte[31]); }
                else { w.Write(EventObjects.StepEvent); w.Write(new byte[12]); w.Write(StepTimeList.StepClass); w.Write(new byte[27]); }
            }
            w.Write((uint)(2 + 2 * g.Times.Length)); w.Write((ushort)g.Times.Length);
            foreach (var t in g.Times) w.Write((ushort)t);
            w.Write((byte)0);
        }
        w.Write(0xF8000001u); w.Write(0u); w.Write(RecordObjects.TrackListClass); w.Write(0x01020304u); w.Write(tracks.Length);
        uint h = 2;
        foreach (var (t, _) in tracks) { w.Write(0xF8000000u | h++); w.Write(0u); w.Write(RecordObjects.TrackClass); w.Write(t); }
        w.Write((ushort)0); w.Write((array ?? []).Length); w.Write(array ?? []);
        int body = tracks.Sum(t => t.Curve.Length);
        w.Write(0u); w.Write((uint)(4 + body)); w.Write((uint)tracks.Length);
        foreach (var (_, c) in tracks) w.Write(c);
        w.Flush();
        var b = m.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - idAt));
        return b;
    }

    /// <summary>One block of the byte array's form (<see cref="ByteArrayTimes"/>), led by the <c>02 00</c> that stands between blocks: the
    /// word, a head byte, FF FF FF, the payload's length as a LEB128 varint and the payload: the form byte (by default 2 for u8 values, 3 for
    /// u16 ones; a 0 byte after an odd form), the count and the values, then the per-value data of its form (forms 0 and 1: 1 byte a value;
    /// forms 2 and 3: a pad to 4 bytes and 4 bytes a value), or <paramref name="tail"/> in its place when given.</summary>
    public static byte[] Block(int width, int[] values, int? form = null, byte[]? tail = null)
    {
        int f = form ?? (width == 1 ? 2 : 3);
        var p = new List<byte> { (byte)f };
        if (width == 1) { p.Add((byte)values.Length); foreach (var v in values) p.Add((byte)v); }
        else { p.Add(0); p.Add((byte)values.Length); p.Add((byte)(values.Length >> 8)); foreach (var v in values) { p.Add((byte)v); p.Add((byte)(v >> 8)); } }
        if (tail is not null) p.AddRange(tail);
        else if (f is 0 or 1) for (int i = 0; i < values.Length; i++) p.Add((byte)(i + 1));
        else
        {
            while (p.Count % 4 != 0) p.Add(0);
            for (int i = 0; i < values.Length; i++) p.AddRange([0xDF, 0x00, 0x00, 0x01]);
        }
        var len = new List<byte>();
        for (uint l = (uint)p.Count; ; l >>= 7)
        {
            if (l < 0x80) { len.Add((byte)l); break; }
            len.Add((byte)(l & 0x7F | 0x80));
        }
        return [0x02, 0x00, 0x6C, 0x98, 0x41, 0x86, 0x03, 0x60, 0xF0, 0xFF, 0xFF, 0xFF, .. len, .. p];
    }

    /// <summary>The bytes a byte array's header (<see cref="Headed"/>) puts before its blocks.</summary>
    public const int HeaderLength = 16;

    /// <summary>A byte array of the game's form: a header of <see cref="HeaderLength"/> bytes giving the block count after the marker
    /// <c>30 05 A7 D0</c> (by default the block words <paramref name="blocks"/> holds), then <paramref name="blocks"/>.</summary>
    public static byte[] Headed(byte[] blocks, int? count = null)
    {
        int n = 0;
        for (int at = blocks.AsSpan().IndexOf(ByteArrayTimes.BlockWord); at >= 0;)
        {
            n++;
            int nx = blocks.AsSpan(at + 1).IndexOf(ByteArrayTimes.BlockWord);
            at = nx < 0 ? -1 : at + 1 + nx;
        }
        byte c = (byte)(count ?? n);
        byte[] head = [0x09, 0x01, 0xCE, 0xF4, 0xB1, 0xBA, 0xF0, 0x1C, 0x03, 0x30, 0x05, 0xA7, 0xD0, c, 0x02, 0x42];
        return [.. head, .. blocks];
    }

    /// <summary>The arena units a curve needs, by the rebuild's rule.</summary>
    public static uint Units(byte[] curve)
    {
        var c = CurveWalker.Walk(curve, 0, 1, out _, out _)[0];
        return (uint)ClipRebuild.UnitsOf(c);
    }
}
