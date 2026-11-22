using GrbAnimTool.Core.IO;

namespace GrbAnimTool.Core.Graph;

/// <summary>The object classes the graph reader parses, and the forms it reads them by.</summary>
public static class GraphClasses
{
    /// <summary>A clip node: plays one clip. Its ID object (next) holds the clip's record ID after a byte 0x0A.</summary>
    public const uint ClipNode = 0xDEA49FF5;
    /// <summary>The ID object that stands after a node: 02 40, a 16-byte ID, the node's index, and for a clip node its ordinals and clip.</summary>
    public const uint IdObject = 0xBB7CBDA3;
    /// <summary>A motion field node: a clip list, a segment table over its rows, and the per-row tables the selection searches.</summary>
    public const uint MotionField = 0x54F4036B;
    /// <summary>One entry of a motion field's clip list: the clip's record ID after 04 70 00 0A.</summary>
    public const uint MotionFieldClip = 0x571CCF8A;
    /// <summary>One segment of a motion field's table: first row, end row, start time, covered length, the remainder, the clip's list index, a flag.</summary>
    public const uint MotionFieldSegment = 0x0FEA9A1E;
    /// <summary>One entry of GR_Graph's clip list, in the record's tail.</summary>
    public const uint GraphClipListEntry = 0x741729E5;
    /// <summary>A blend's child: the link from a blend to one child node, with the nominal value the blend's parameter picks it by. On a
    /// blend over the move speed the nominal is the child clip's root speed; on a heading blend its side follows the clip's heading. Not
    /// parsed: see <see cref="ClipGraphVerdict.UnderBlend"/>.</summary>
    public const uint BlendChild = 0xEB8598EE;

    /// <summary>The pieces of a test: a condition, the all-of and any-of groups, a markup test and its markup data, the expression wrapper
    /// and the loop condition.</summary>
    public static readonly IReadOnlySet<uint> TestClasses = new HashSet<uint> { 0x764F4223, 0xD3933A8A, 0x05A5172E, 0xD38E0067, 0x2AAA008F, 0x98396CCA, 0x9ECE6483 };

    public static bool IsIdOrTest(uint c) => c == IdObject || TestClasses.Contains(c);

    /// <summary>The node classes a parser here reads: the clip node and its ID object, and the motion field with its list entries and segments.</summary>
    public static bool IsParsed(uint c) => c is ClipNode or IdObject or MotionField or MotionFieldClip or MotionFieldSegment;
}

/// <summary>A clip node and the clip it names.</summary>
public readonly record struct ClipNode(int ObjectIndex, int At, int ClipIdAt, ulong ClipId);

/// <summary>One segment of a motion field's table. Rows are 0.04 s apart; <see cref="Start"/> + <see cref="Length"/> +
/// <see cref="Remainder"/> is the clip's duration for a clip covered whole, so the table holds the clip's length.</summary>
public readonly record struct MotionFieldSegment(int At, int FirstRow, int EndRow, float Start, float Length, float Remainder, int ClipIndex, byte Flag);

/// <summary>A motion field node, its clip list (by record ID) and its segment table (each naming a clip by its list index).</summary>
public sealed class MotionField
{
    public required int ObjectIndex { get; init; }
    public required int At { get; init; }
    public required IReadOnlyList<ulong> Clips { get; init; }
    /// <summary>Where each list entry's clip ID stands in the document.</summary>
    public required IReadOnlyList<int> ClipIdAts { get; init; }
    public required IReadOnlyList<MotionFieldSegment> Segments { get; init; }
    public int Rows => Segments.Count == 0 ? 0 : Segments.Max(s => s.EndRow);
}

/// <summary>Parsers for the node classes that name a clip. Each reads only the bytes of its own objects, checked against their ends.</summary>
public static class GraphNodes
{
    /// <summary>Every clip node of a document that has the clip node's form (see <see cref="TryClipNode"/>). <paramref name="unread"/>
    /// counts clip nodes without that form; a clip ID in them is then found by the ID scan as an unread place, which holds the length.</summary>
    public static List<ClipNode> ClipNodes(GraphDocument d, out int unread)
    {
        var list = new List<ClipNode>();
        unread = 0;
        var o = d.Objects;
        for (int i = 0; i < o.Count; i++)
        {
            if (o[i].Class != GraphClasses.ClipNode) continue;
            if (TryClipNode(d, i, out var n)) list.Add(n); else unread++;
        }
        return list;
    }

    /// <summary>The lengths of a clip node's ID object (from its class to its end) seen in the game's documents.</summary>
    public static readonly IReadOnlySet<int> ClipNodeIdObjectLengths = new HashSet<int> { 46, 60, 61 };

    /// <summary>Reads object <paramref name="i"/> as a clip node, only in the form the game's documents hold it: the node is plain (02 00)
    /// and its own bytes after the class are exactly 0E 7F FC, 02 or 03, then 00 01 00 (its field mask names no other field, so no duration);
    /// the next object is a plain ID object of a known length (<see cref="ClipNodeIdObjectLengths"/>) starting 02 40, with 0x0A at +34 and
    /// the clip's ID at +35. Any other form returns false: the node is unread.</summary>
    public static bool TryClipNode(GraphDocument d, int i, out ClipNode node)
    {
        node = default;
        var o = d.Objects;
        var b = d.Bytes;
        if (o[i].Class != GraphClasses.ClipNode || o[i].WithId || i + 1 >= o.Count || o[i + 1].Class != GraphClasses.IdObject || o[i + 1].WithId) return false;
        int n = o[i].At + 4;
        if (d.ObjectEnd(i) - n != 7 || b[n] != 0x0E || b[n + 1] != 0x7F || b[n + 2] != 0xFC || b[n + 3] is not (0x02 or 0x03)
            || b[n + 4] != 0x00 || b[n + 5] != 0x01 || b[n + 6] != 0x00) return false;
        int g = o[i + 1].At, end = d.ObjectEnd(i + 1);
        if (!ClipNodeIdObjectLengths.Contains(end - g) || b[g + 4] != 0x02 || b[g + 5] != 0x40 || b[g + 34] != 0x0A) return false;
        node = new ClipNode(i, o[i].At, g + 35, Bin.U64(d.Bytes, g + 35, "a clip node's clip"));
        return true;
    }

    /// <summary>Every motion field node of a document with its clip list and segment table: the first run of clip list entries after the
    /// node, and the first run of segments after that list, both before the next motion field node. Throws
    /// <see cref="InvalidDataException"/> when a node lacks either, an entry or segment doesn't have its form, or a segment's clip index
    /// lies outside the list.</summary>
    public static List<MotionField> MotionFields(GraphDocument d)
    {
        var o = d.Objects;
        var fields = new List<MotionField>();
        var nodes = Enumerable.Range(0, o.Count).Where(i => o[i].Class == GraphClasses.MotionField).ToList();
        for (int k = 0; k < nodes.Count; k++)
        {
            int i = nodes[k], stop = k + 1 < nodes.Count ? nodes[k + 1] : o.Count;
            int a = i + 1;
            while (a < stop && o[a].Class != GraphClasses.MotionFieldClip) a++;
            var clips = new List<ulong>(); var ats = new List<int>();
            for (; a < stop && o[a].Class == GraphClasses.MotionFieldClip; a++)
            {
                int at = o[a].At;
                if (d.ObjectEnd(a) - at != 18 || d.Bytes[at + 4] != 0x04 || d.Bytes[at + 5] != 0x70 || d.Bytes[at + 6] != 0x00 || d.Bytes[at + 7] != 0x0A)
                    throw new InvalidDataException($"motion field at +{o[i].At:X}: the clip list entry at +{at:X} doesn't have its form");
                clips.Add(Bin.U64(d.Bytes, at + 8, "a motion field's clip"));
                ats.Add(at + 8);
            }
            if (clips.Count == 0) throw new InvalidDataException($"motion field at +{o[i].At:X}: no clip list before the next motion field");
            while (a < stop && o[a].Class != GraphClasses.MotionFieldSegment) a++;
            var segs = new List<MotionFieldSegment>();
            for (; a < stop && o[a].Class == GraphClasses.MotionFieldSegment; a++)
            {
                int at = o[a].At;
                var b = d.Bytes;
                // a segment's 29 B within its own object (a table's last segment runs on over the rows block)
                if (d.ObjectEnd(a) - at < 29) throw new InvalidDataException($"motion field at +{o[i].At:X}: the segment at +{at:X} is shorter than 29 B");
                if (b[at + 4] != 0x08 || b[at + 5] != 0x7F) throw new InvalidDataException($"motion field at +{o[i].At:X}: the segment at +{at:X} doesn't have its form");
                var s = new MotionFieldSegment(at, Bin.I32(b, at + 6, "a segment"), Bin.I32(b, at + 10, "a segment"), Bin.F32(b, at + 14, "a segment"),
                    Bin.F32(b, at + 18, "a segment"), Bin.F32(b, at + 22, "a segment"), b[at + 26] | b[at + 27] << 8, b[at + 28]);
                if (s.ClipIndex >= clips.Count) throw new InvalidDataException($"motion field at +{o[i].At:X}: segment at +{at:X} names clip {s.ClipIndex} of a list of {clips.Count}");
                if (s.FirstRow < 0 || s.EndRow < s.FirstRow || !float.IsFinite(s.Start) || !float.IsFinite(s.Length) || !float.IsFinite(s.Remainder) || s.Start < 0 || s.Length < 0)
                    throw new InvalidDataException($"motion field at +{o[i].At:X}: segment at +{at:X} holds values out of range");
                segs.Add(s);
            }
            if (segs.Count == 0) throw new InvalidDataException($"motion field at +{o[i].At:X}: no segment table after its clip list");
            fields.Add(new MotionField { ObjectIndex = i, At = o[i].At, Clips = clips, ClipIdAts = ats, Segments = segs });
        }
        return fields;
    }

    /// <summary>GR_Graph's clip list, in its record's tail: a u32 count, then each entry its number (n+1 with 0xF8000000 set), a zero u32,
    /// the class 0x741729E5, a u16 1 and the clip's ID (22 B). Returns each ID and where it stands in the tail.</summary>
    public static List<(ulong Id, int At)> GraphClipList(ReadOnlySpan<byte> tail)
    {
        int n = Bin.I32(tail, 0, "the clip list's count");
        if (n < 0 || n > (tail.Length - 4) / 22) throw new InvalidDataException($"a clip list of {n} entries can't fit {tail.Length} B");
        var l = new List<(ulong, int)>(n);
        for (int i = 0; i < n; i++)
        {
            int at = 4 + 22 * i;
            if (Bin.U32(tail, at, "a clip list entry") != (0xF8000000u | (uint)(i + 1)) || Bin.U32(tail, at + 4, "a clip list entry") != 0
                || Bin.U32(tail, at + 8, "a clip list entry") != GraphClasses.GraphClipListEntry || (tail[at + 12] | tail[at + 13] << 8) != 1)
                throw new InvalidDataException($"clip list entry {i} at tail +{at:X} doesn't have its form");
            l.Add((Bin.U64(tail, at + 14, "a clip list entry"), at + 14));
        }
        return l;
    }
}
