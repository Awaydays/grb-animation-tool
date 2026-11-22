using System.Buffers.Binary;
using System.Text;
using GrbAnimTool.Core.IO;

namespace GrbAnimTool.Core.Forge;

/// <summary>One file-data table (FDT) header, also called a directory: a slot range of the forge's entries and where its index and name
/// tables stand.</summary>
public sealed class FdtHeader
{
    public long At;
    public uint Count, Unk04;
    public long IndexOff, Next;
    public uint IdxStart, IdxEnd;
    public long NameOff, RawOff;
    public long Slots => (long)IdxEnd - IdxStart + 1;
}

/// <summary>One live entry of a forge: the index-table fields and the raw 192-byte name-table record.</summary>
public sealed class ForgeEntry
{
    /// <summary>The entry's index in the forge (its slot). A datafile's record files are installed under a folder name that starts with it.</summary>
    public int Index;
    public long Offset;
    public ulong Id;
    public uint Size;
    public byte[] NameRecord = new byte[192];

    /// <summary>Name bytes up to the first NUL (at most 128), decoded as ASCII.</summary>
    public string Name
    {
        get
        {
            int z = 0;
            while (z < 128 && NameRecord[0x2C + z] != 0) z++;
            return Encoding.ASCII.GetString(NameRecord, 0x2C, z);
        }
    }
}

/// <summary>What the reader learns about a forge container: its header fields, its directories and its entries.</summary>
public sealed class ForgeLayout
{
    public long Length;
    public uint Version;
    public long DataHeaderOffset;
    public uint DhCount, DhDirectories;
    public long DhFirstFdt;
    public List<FdtHeader> Fdts = [];
    public List<ForgeEntry> Entries = [];

    /// <summary>The first entry with this ID, or null.</summary>
    public ForgeEntry? Find(ulong id) => Entries.FirstOrDefault(e => e.Id == id);
}

/// <summary>Reads a forge container's directories and entries, read-only. Every offset and count read from the file is checked against
/// the file's length before it is used.</summary>
public static class ForgeReader
{
    /// <summary>Opens a forge for reading, sharing it for reading only (the game may hold it open the same way).</summary>
    public static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

    public static ForgeLayout Read(string path)
    {
        using var fs = OpenRead(path);
        return Read(fs);
    }

    public static ForgeLayout Read(Stream s)
    {
        var f = new ForgeLayout { Length = s.Length };
        if (f.Length < 0x40) throw new InvalidDataException("file too short for a forge header");
        var h = Bin.ReadAt(s, 0, 0x40);
        if (Encoding.ASCII.GetString(h, 0, 9) != "scimitar\0") throw new InvalidDataException("not a forge (no scimitar magic)");
        f.Version = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(9));
        f.DataHeaderOffset = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(0x0D));
        // compared without adding to a possibly hostile 64-bit value, so nothing wraps around
        if (f.DataHeaderOffset < 0x1E || f.DataHeaderOffset > f.Length - 0x2C) throw new InvalidDataException("forge data header offset out of range");

        var d = Bin.ReadAt(s, f.DataHeaderOffset, 0x2C);
        f.DhCount = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x00));
        f.DhDirectories = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x20));
        f.DhFirstFdt = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(0x24));

        long at = f.DhFirstFdt;
        var seen = new HashSet<long>();
        while (at != -1)
        {
            if (at < 0 || at > f.Length - 0x30 || !seen.Add(at)) throw new InvalidDataException($"forge directory chain pointer invalid or looping: 0x{at:X}");
            var b = Bin.ReadAt(s, at, 0x30);
            var t = new FdtHeader
            {
                At = at,
                Count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x00)),
                Unk04 = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x04)),
                IndexOff = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x08)),
                Next = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x10)),
                IdxStart = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x18)),
                IdxEnd = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x1C)),
                NameOff = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x20)),
                RawOff = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x28)),
            };
            // an entry's index is an int, so the last slot is one too
            if (t.Slots < 0 || t.Slots > 1_000_000 || t.Count > t.Slots || t.IdxEnd > int.MaxValue) throw new InvalidDataException($"forge directory at 0x{at:X}: implausible slot range");
            // the directories together: many headers may name the same tables, so each one's own range doesn't bound the whole
            if (f.Fdts.Count >= 1_000_000 || f.Entries.Count + (long)t.Count > 1_000_000)
                throw new InvalidDataException($"forge directory at 0x{at:X}: more than 1,000,000 directories or entries in all");
            f.Fdts.Add(t);
            if (t.Count > 0)
            {
                if (t.IndexOff < 0 || t.IndexOff > f.Length - 20L * t.Count || t.NameOff < 0 || t.NameOff > f.Length - 192L * t.Count)
                    throw new InvalidDataException($"forge directory at 0x{at:X}: index or name table outside the file");
                var ib = Bin.ReadAt(s, t.IndexOff, checked((int)(t.Count * 20)));
                var nb = Bin.ReadAt(s, t.NameOff, checked((int)(t.Count * 192)));
                for (int i = 0; i < t.Count; i++)
                {
                    var e = new ForgeEntry
                    {
                        Index = (int)(t.IdxStart + i),
                        Offset = BinaryPrimitives.ReadInt64LittleEndian(ib.AsSpan(i * 20)),
                        Id = BinaryPrimitives.ReadUInt64LittleEndian(ib.AsSpan(i * 20 + 8)),
                        Size = BinaryPrimitives.ReadUInt32LittleEndian(ib.AsSpan(i * 20 + 16)),
                    };
                    Array.Copy(nb, i * 192, e.NameRecord, 0, 192);
                    f.Entries.Add(e);
                }
            }
            at = t.Next;
        }
        foreach (var e in f.Entries)
            if (e.Offset < 0 || e.Offset > f.Length - e.Size) throw new InvalidDataException($"forge entry {e.Index}: payload outside the file");
        return f;
    }

    /// <summary>The raw payload bytes of one entry (still compressed). An entry of 2 GiB or more is refused with
    /// <see cref="InvalidDataException"/>: no datafile the tool reads comes near that.</summary>
    public static byte[] ReadPayload(Stream s, ForgeEntry e)
    {
        if (e.Size > int.MaxValue) throw new InvalidDataException($"forge entry {e.Index} (0x{e.Id:X16}): a payload of {e.Size} B is 2 GiB or more");
        return Bin.ReadAt(s, e.Offset, (int)e.Size);
    }
}
