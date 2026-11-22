using System.Buffers.Binary;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Graph;

namespace GrbAnimTool.Tests;

/// <summary>Small graph documents and records made in the tests, in the packed form the reader expects: the head with its class table,
/// pairs and tokens, a body, a literal-only LZO1X stream, and the record around it.</summary>
public static class GraphSynthetic
{
    public static byte[] Varint(int v)
    {
        var o = new List<byte>();
        uint u = (uint)v;
        do { byte b = (byte)(u & 0x7F); u >>= 7; o.Add(u != 0 ? (byte)(b | 0x80) : b); } while (u != 0);
        return [.. o];
    }

    static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); return b; }
    static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    static byte[] F32(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); return b; }
    static byte[] Cat(params byte[][] p) => [.. p.SelectMany(x => x)];

    /// <summary>A document: the class table (class, the number of objects its tokens name), the object tokens (n objects of a class index,
    /// the handle list's first), the body. The pairs are the sums of the tokens of each kind but 0x40.</summary>
    public static byte[] Document((uint Class, int Count)[] classes, (int N, int ClassIndex)[] objectTokens, byte[] body)
    {
        var head = new List<byte> { 9, 1, 0, 0, 0, 0 };
        head.AddRange(Varint(0));
        head.AddRange(Varint(classes.Length));
        foreach (var (c, n) in classes) { head.AddRange(U32(c)); head.AddRange(new byte[16]); head.AddRange(Varint(n)); }
        head.AddRange(Varint(0));   // no pairs: every token is of kind 0x40
        head.AddRange(Varint(0));
        var toks = objectTokens.SelectMany(t => Cat([0x40], Varint(t.N), Varint(t.ClassIndex))).ToArray();
        head.AddRange(Varint(objectTokens.Length));
        head.AddRange(Varint(toks.Length));
        head.AddRange(toks);
        head.AddRange(body);
        return [.. head];
    }

    /// <summary>A literal-only LZO1X stream of <paramref name="data"/>.</summary>
    public static byte[] Literal(byte[] data)
    {
        int n = data.Length;
        var o = new List<byte>();
        if (n <= 238) o.Add((byte)(17 + n));
        else
        {
            o.Add(0);
            int zeros = (n - 18 - 1) / 255;
            for (int k = 0; k < zeros; k++) o.Add(0);
            o.Add((byte)(n - 18 - 255 * zeros));
        }
        o.AddRange(data);
        o.AddRange([0x11, 0, 0]);
        return [.. o];
    }

    /// <summary>A record holding the document's stream: a few head bytes, the stream object and its length, the stream, then a zero u32 and
    /// the decoded length (a document's tail), or <paramref name="tail"/> instead (GR_Graph's clip list).</summary>
    public static DatafileRecord Record(ulong id, uint type, string name, byte[] document, byte[]? tail = null, byte[]? before = null)
    {
        var s = Literal(document);
        var rec = Cat(U32(type), before ?? new byte[12], U32(GraphDocument.StreamClass), U32((uint)s.Length), s, tail ?? Cat(U32(0), U32((uint)document.Length)));
        return new DatafileRecord { Id = id, Type = type, Name = name, Bytes = rec };
    }

    /// <summary>A document's tail as <see cref="Record"/> writes it, followed by <paramref name="more"/>.</summary>
    public static byte[] DocumentTail(byte[] document, byte[] more) => Cat(U32(0), U32((uint)document.Length), more);

    public const ulong ClipA = 0x0000010000000A01, ClipB = 0x0000010000000B02, ClipC = 0x0000010000000C03, ClipD = 0x0000010000000D04;

    public static byte[] Obj(uint c) => Cat([2, 0], U32(c));
    public static byte[] Id(ulong v) => U64(v);

    /// <summary>The bytes after a clip node's class in the game's form.</summary>
    public static readonly byte[] ClipNodeFields = [0x0E, 0x7F, 0xFC, 0x02, 0x00, 0x01, 0x00];

    /// <summary>A clip node and its 46-byte ID object naming <paramref name="clip"/> at +35; <paramref name="fields"/> replaces the node's
    /// bytes after its class, <paramref name="idExtra"/> lengthens the ID object.</summary>
    public static byte[] ClipNodeBytes(ulong clip, byte[]? fields = null, int idExtra = 0) => Cat(Obj(GraphClasses.ClipNode), fields ?? ClipNodeFields,
        Obj(GraphClasses.IdObject), [0x02, 0x40], Enumerable.Repeat((byte)0x11, 16).ToArray(), U16(1), new byte[8], [0, 0, 0x0A], U64(clip), [0, 0, 0], new byte[idExtra]);

    /// <summary>GR_Graph: an empty document and a clip list of <paramref name="clips"/> in its tail.</summary>
    public static DatafileRecord Graph(params ulong[] clips)
    {
        var doc = Document([(GraphDocument.HandleListClass, 1)], [(1, 0)], []);
        var tail = new List<byte>(U32((uint)clips.Length));
        for (int i = 0; i < clips.Length; i++) tail.AddRange(Cat(U32(0xF8000000u | (uint)(i + 1)), U32(0), U32(GraphClasses.GraphClipListEntry), U16(1), U64(clips[i])));
        return Record(ClipGraph.GraphRecordId, GraphDocument.GraphType, "GR_Graph", doc, [.. tail]);
    }

    /// <summary>A document of one clip node naming <paramref name="clip"/>, in the node form <see cref="ClipNodeBytes"/> makes.</summary>
    public static byte[] ClipDocument(ulong clip, byte[]? fields = null, int idExtra = 0) =>
        Document([(GraphDocument.HandleListClass, 1), (GraphClasses.ClipNode, 1), (GraphClasses.IdObject, 0)], [(1, 0), (1, 1)], ClipNodeBytes(clip, fields, idExtra));

    /// <summary>The class of the inline object <see cref="MotionFieldDocument"/> can place between the clip list and the segments.</summary>
    public const uint InlineClass = 0x2468ACE0;

    /// <summary>A document of one motion field over <paramref name="clips"/>, with lengths <paramref name="lengths"/>: one segment for each
    /// list index in <paramref name="segmented"/> (every index by default). <paramref name="between"/>, when given, stands after the clip
    /// list as an inline object of <see cref="InlineClass"/>.</summary>
    public static byte[] MotionFieldDocument(ulong[] clips, float[] lengths, int[]? segmented = null, byte[]? between = null)
    {
        segmented ??= [.. Enumerable.Range(0, clips.Length)];
        var body = new List<byte>(Cat(Obj(GraphClasses.MotionField), [0x0C, 0x7F, 0x00]));
        foreach (var c in clips) body.AddRange(Cat(Obj(GraphClasses.MotionFieldClip), [0x04, 0x70, 0x00, 0x0A], U64(c), [0, 0]));
        if (between is not null) body.AddRange(Cat(Obj(InlineClass), between));
        int row = 0;
        foreach (int i in segmented)
        {
            int rows = (int)MathF.Round(lengths[i] / 0.04f);
            body.AddRange(Cat(Obj(GraphClasses.MotionFieldSegment), [0x08, 0x7F], U32((uint)row), U32((uint)(row + rows)), F32(0), F32(lengths[i]), F32(0), U16(i), [1]));
            row += rows;
        }
        return Document([(GraphDocument.HandleListClass, 1), (GraphClasses.MotionField, 1), (GraphClasses.MotionFieldClip, clips.Length), (GraphClasses.MotionFieldSegment, segmented.Length), (InlineClass, 0)],
            [(1, 0), (1, 1), (clips.Length, 2), (segmented.Length, 3)], [.. body]);
    }

    /// <summary>A document whose only object, of a class no parser reads, holds <paramref name="clip"/>'s ID after 0x0A.</summary>
    public static byte[] UnreadDocument(ulong clip) =>
        Document([(GraphDocument.HandleListClass, 1), (0x12345678u, 1)], [(1, 0), (1, 1)], Cat(Obj(0x12345678u), [0x05, 0x78, 0, 0, 0x0A], U64(clip), [0, 0, 0, 0]));

    /// <summary>A document of a blend's child holding <paramref name="nominal"/> as an f32, then a clip node naming <paramref name="clip"/>.</summary>
    public static byte[] BlendDocument(ulong clip, float nominal) =>
        Document([(GraphDocument.HandleListClass, 1), (GraphClasses.BlendChild, 1), (GraphClasses.ClipNode, 1), (GraphClasses.IdObject, 0)], [(1, 0), (1, 1), (1, 2)],
            Cat(Obj(GraphClasses.BlendChild), [0x05, 0x10], F32(nominal), [0, 0], ClipNodeBytes(clip)));

    /// <summary>The class of the pick object <see cref="BlendGrandchildDocument"/> puts between a blend's child and a clip node.</summary>
    public const uint PickClass = 0x5314B576;

    /// <summary>A document of a blend's child, then <paramref name="between"/> pick objects, then a clip node naming <paramref name="clip"/>:
    /// the clip node's parent is the last pick, the blend's child stands <paramref name="between"/> levels above it.</summary>
    public static byte[] BlendGrandchildDocument(ulong clip, int between = 1)
    {
        var body = new List<byte>(Cat(Obj(GraphClasses.BlendChild), [0x05, 0x10], F32(1), [0, 0]));
        for (int i = 0; i < between; i++) body.AddRange(Cat(Obj(PickClass), [0x05, 0x10], F32(0), [0, 0]));
        body.AddRange(ClipNodeBytes(clip));
        return Document([(GraphDocument.HandleListClass, 1), (GraphClasses.BlendChild, 1), (PickClass, between), (GraphClasses.ClipNode, 1), (GraphClasses.IdObject, 0)],
            [(1, 0), (1, 1), (between, 2), (1, 3)], [.. body]);
    }

    /// <summary>A document written depth first: a pick; under it a blend's child holding a clip node naming <paramref name="sibling"/>;
    /// then a second pick (the first pick's child, after the blend's subtree) holding a clip node naming <paramref name="clip"/>.</summary>
    public static byte[] SiblingSubtreeDocument(ulong clip, ulong sibling)
    {
        var body = Cat(Obj(PickClass), [0x05, 0x10], F32(0), [0, 0],
            Obj(GraphClasses.BlendChild), [0x05, 0x10], F32(1), [0, 0],
            ClipNodeBytes(sibling),
            Obj(PickClass), [0x05, 0x10], F32(0), [0, 0],
            ClipNodeBytes(clip));
        return Document([(GraphDocument.HandleListClass, 1), (PickClass, 2), (GraphClasses.BlendChild, 1), (GraphClasses.ClipNode, 2), (GraphClasses.IdObject, 0)],
            [(1, 0), (1, 1), (1, 2), (1, 3), (1, 1), (1, 3)], body);
    }

    /// <summary>A document whose first object, an ID object standing inline, holds <paramref name="clip"/>'s ID: an unread place with no
    /// object before it to own it.</summary>
    public static byte[] LoneIdObjectDocument(ulong clip) =>
        Document([(GraphDocument.HandleListClass, 1), (GraphClasses.IdObject, 0)], [(1, 0)], Cat(Obj(GraphClasses.IdObject), [0x02, 0x40, 0x0A], U64(clip), [0, 0]));

    /// <summary>A clip ID that isn't one of the install's clips (a record of another entry).</summary>
    public const ulong OtherRecord = 0x0000030000000099;

    /// <summary>GR_Graph and three documents: clip A in a clip node, clip B in a motion field (beside a record that isn't a clip of the
    /// install), clip C in an unread object; D on GR_Graph's list only.</summary>
    public static List<DatafileRecord> Install() =>
    [
        Graph(ClipA, ClipB, ClipC, ClipD),
        Record(0x0000020000000001, GraphDocument.DocumentType, "Plain-StateMachine", ClipDocument(ClipA)),
        Record(0x0000020000000002, GraphDocument.DocumentType, "Moving-StateMachine", MotionFieldDocument([OtherRecord, ClipB], [0.8f, 1.2f])),
        Record(0x0000020000000003, GraphDocument.DocumentType, "Other-StateMachine", UnreadDocument(ClipC)),
    ];

    public static readonly ulong[] Clips = [ClipA, ClipB, ClipC, ClipD];
}
