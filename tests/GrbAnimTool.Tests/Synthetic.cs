using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Tests;

/// <summary>Records built by hand in the game's layouts, for tests that need no game data.</summary>
public static class Synthetic
{
    sealed class W
    {
        readonly MemoryStream _m = new();
        public int Pos => (int)_m.Position;
        public W U8(byte v) { _m.WriteByte(v); return this; }
        public W U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _m.Write(b); return this; }
        public W U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _m.Write(b); return this; }
        public W I32(int v) => U32((uint)v);
        public W U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _m.Write(b); return this; }
        public W F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); _m.Write(b); return this; }
        public W Bytes(ReadOnlySpan<byte> b) { _m.Write(b); return this; }
        public W Pad(long to) { while (_m.Position < to) _m.WriteByte(0); return this; }
        public byte[] ToArray() => _m.ToArray();
    }

    /// <summary>A rotation in the 6-byte form: the largest component dropped and made positive, the other three in 15 bits each, the
    /// dropped one's place in the top bits of the first two.</summary>
    public static byte[] Encode6(Quaternion q0)
    {
        var q = Quaternion.Normalize(q0);
        float[] c = [q.X, q.Y, q.Z, q.W];
        int sel = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[sel])) sel = i;
        float s = c[sel] < 0 ? -1 : 1;
        var f = new List<float>(3);
        for (int i = 0; i < 4; i++) if (i != sel) f.Add(c[i] * s);
        var u = new ushort[3];
        for (int i = 0; i < 3; i++) u[i] = (ushort)Math.Clamp((int)MathF.Round((f[i] + 0.70710677f) / 4.3161006e-05f), 0, 0x7FFF);
        u[0] |= (ushort)((sel & 1) << 15);
        u[1] |= (ushort)(((sel >> 1) & 1) << 15);
        var b = new byte[6];
        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), u[i]);
        return b;
    }

    static int Align(int head, int a) => (head + a - 1) / a * a;

    /// <summary>An inline curve with one-byte key times (time form 0): tag, size in memory, key count, times after the first, values.</summary>
    public static byte[] Curve(byte tag, int[] ticks, byte[][] values)
    {
        int k = ticks.Length, e = values[0].Length;
        int head = 2 + (k >= 256 ? 2 : 0) + (k - 1);
        var w = new W().U8(tag).U32((uint)(Align(head, e >= 4 ? 4 : 2) + k * e)).U32((uint)k);
        for (int i = 1; i < k; i++) w.U8((byte)ticks[i]);
        foreach (var v in values) w.Bytes(v);
        return w.ToArray();
    }

    public static byte[] RotCurve(int[] ticks, params Quaternion[] q) => Curve(0x0C, ticks, q.Select(Encode6).ToArray());

    public static byte[] PosCurve(int[] ticks, params Vector3[] p) => Curve(0x1B, ticks, p.Select(v =>
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(b, v.X); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), v.Y); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), v.Z);
        return b;
    }).ToArray());

    /// <summary>A whole clip record: the name, a head with no entries, the ID, duration and words, the track list, an empty byte array, then
    /// the curves' wrapper and the curves (curve i animates track i). <paramref name="extraCurves"/> follow with no track of their own.</summary>
    public static byte[] Clip(string name, ulong id, float duration, (uint Track, byte[] Curve)[] tracks, byte[][]? extraCurves = null)
    {
        var curves = tracks.Select(t => t.Curve).Concat(extraCurves ?? []).ToArray();
        var nb = Encoding.ASCII.GetBytes(name);
        var w = new W().U32(ClipRecord.Type).U32(0).I32(nb.Length).Bytes(nb);
        w.U8(1).U8(2).U16(0).U8(0).U8(0).U16(0);                          // the head: no entries
        w.U64(id).U32(ClipRecord.Type).U8(0).F32(duration).U32(0x11223344).U32(0x55667788);
        w.U32(0xF8000001).U32(0).U32(RecordObjects.TrackListClass).U32(0x01020304).I32(tracks.Length);
        uint h = 2;
        foreach (var (t, _) in tracks) w.U32(0xF8000000 | h++).U32(0).U32(RecordObjects.TrackClass).U32(t);
        w.U16(0).I32(0);                                                    // two bytes of 0 and an empty byte array
        int body = curves.Sum(x => x.Length);
        w.U32(0).U32((uint)(4 + body)).U32((uint)curves.Length);
        foreach (var c in curves) w.Bytes(c);
        var b = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - 13 - nb.Length));
        return b;
    }

    /// <summary>A skeleton record: one bone object per entry (hash, parent index or -1, local position and rotation; the model pose is left
    /// at zero, as only the locals are posed).</summary>
    public static byte[] Skeleton((uint Hash, int Parent, Vector3 Pos, Quaternion Rot)[] bones)
    {
        var w = new W().U32(0x24AECB7C).U32(0).I32(4).Bytes("Skel"u8).U8(0).U64(1).U32(0x24AECB7C);
        for (int i = 0; i < bones.Length; i++)
        {
            var (hash, parent, p, q) = bones[i];
            w.U32(0xF8000010 + (uint)i).U32(0).U32(RecordObjects.BoneClass).U32(hash);
            if (parent < 0) w.U8(3); else w.U8(2).U32(0xF8000010 + (uint)parent).U32(0);
            w.U8(3);
            for (int k = 0; k < 8; k++) w.F32(0);
            w.F32(p.X).F32(p.Y).F32(p.Z).F32(0).F32(q.X).F32(q.Y).F32(q.Z).F32(q.W);
        }
        return w.ToArray();
    }

    /// <summary>A datafile payload with STORED chunks: block 1 the table, block 2 the records.</summary>
    public static byte[] StoredDatafile(params (ulong Id, byte[] Record)[] recs)
    {
        var table = new W().U16((ushort)recs.Length);
        foreach (var (id, r) in recs) table.U64(id).U32((uint)r.Length).U16(0);
        var body = recs.SelectMany(r => r.Record).ToArray();
        return [.. StoredBlock(table.ToArray()), .. StoredBlock(body)];
    }

    public static byte[] StoredBlock(byte[] data)
    {
        const int Chunk = 0x8000;
        int n = (data.Length + Chunk - 1) / Chunk;
        var w = new W().U64(0x1004FA9957FBAA33UL).U16(3).U8(3).U32(0x80008000).I32(n);
        for (int i = 0; i < n; i++) { uint len = (uint)Math.Min(Chunk, data.Length - i * Chunk); w.U32(len).U32(len); }
        for (int i = 0; i < n; i++) w.U32(0).Bytes(data.AsSpan(i * Chunk, Math.Min(Chunk, data.Length - i * Chunk)));
        return w.ToArray();
    }

    /// <summary>A shared curve table record (opaque, as the game's is): a head laid out as a clip's, the ID, then the length of the rest,
    /// the declared curve count and the curves.</summary>
    public static byte[] SharedTable(uint declared, byte[] curves)
    {
        var w = new W().U32(0x5EED0001).U32(0).I32(1).Bytes("T"u8);
        w.U8(1).U8(2).U16(0).U8(0).U8(0).U16(0);                                            // the head: no entries
        w.U64(SharedCurveTable.RecordId).U32(0).U8(0);
        return w.U32((uint)(4 + curves.Length)).U32(declared).Bytes(curves).ToArray();
    }

    /// <summary>A forge container: the header, one data header, one directory, the index and name tables, then each entry's payload.</summary>
    public static byte[] Forge(params (ulong Id, string Name, byte[] Payload)[] entries)
    {
        int n = entries.Length;
        const long DataHeader = 0x40, Directory = 0x70, Index = 0xA0;
        long names = Index + 20L * n, data = names + 192L * n;
        var w = new W().Bytes("scimitar\0"u8).U32(27).U64(DataHeader);
        w.Pad(DataHeader).U32((uint)n).Pad(DataHeader + 0x20).U32(1).U64(Directory);
        w.Pad(Directory).U32((uint)n).U32(0).U64(Index).U64(ulong.MaxValue).U32(0).U32((uint)n - 1).U64((ulong)names).U64(0);
        long at = data;
        foreach (var (id, _, p) in entries) { w.U64((ulong)at).U64(id).U32((uint)p.Length); at += p.Length; }
        foreach (var (_, name, _) in entries)
        {
            var r = new byte[192];
            Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(name.Length, 127)).CopyTo(r.AsSpan(0x2C));
            w.Bytes(r);
        }
        foreach (var (_, _, p) in entries) w.Bytes(p);
        return w.ToArray();
    }

    /// <summary>A datafile record whose byte after the name is 0: type, length, name length, name, 0, then the body (ID, type, rest).</summary>
    public static byte[] Record(uint type, string name, ulong id, byte[] rest)
    {
        var nb = Encoding.ASCII.GetBytes(name);
        return new W().U32(type).U32((uint)(12 + rest.Length)).I32(nb.Length).Bytes(nb).U8(0).U64(id).U32(type).Bytes(rest).ToArray();
    }
}
