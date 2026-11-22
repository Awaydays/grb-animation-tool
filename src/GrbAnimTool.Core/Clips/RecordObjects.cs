using System.Buffers.Binary;

namespace GrbAnimTool.Core.Clips;

/// <summary>Objects inside a record's body. Each starts with a handle (a u32 whose top byte is 0xF8), a u32 0 and a u32 class (the CRC-32
/// of the class's name). Objects are found by that pattern; their layouts are read only as far as the readers below need.</summary>
public static class RecordObjects
{
    /// <summary>The class of a skeleton's bone objects.</summary>
    public const uint BoneClass = 0x95741049;

    /// <summary>The class of a clip's track objects: one per track, the track's hash after the class.</summary>
    public const uint TrackClass = 0x653CAA76;

    /// <summary>The class of the object that heads a clip's track list.</summary>
    public const uint TrackListClass = 0x0181EFE8;

    /// <summary>Every object in <paramref name="b"/> by the handle pattern, in file order: its offset, its handle and its class.</summary>
    public static List<(int At, uint Handle, uint Class)> Objects(ReadOnlySpan<byte> b)
    {
        var list = new List<(int, uint, uint)>();
        for (int i = 0; i + 12 <= b.Length; i++)
        {
            if (b[i + 3] != 0xF8) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(b[(i + 4)..]) != 0) continue;
            uint h = BinaryPrimitives.ReadUInt32LittleEndian(b[i..]);
            list.Add((i, h, BinaryPrimitives.ReadUInt32LittleEndian(b[(i + 8)..])));
            i += 11;
        }
        return list;
    }

    /// <summary>A bone of a skeleton record: its place among the bones, its handle (low 24 bits), its name hash, its parent's handle (-1 for
    /// none) and the object's offset.</summary>
    public sealed record Bone(int Index, uint Handle, uint Hash, int ParentHandle, int At);

    /// <summary>A skeleton record's bones in file order, from the form: handle, 0, class 0x95741049, u32 hash, then 0x03 (no parent) or
    /// 0x02 + the parent's handle + u32 0.</summary>
    public static List<Bone> Bones(ReadOnlySpan<byte> b)
    {
        var list = new List<Bone>();
        foreach (var (at, h, c) in Objects(b))
        {
            if (c != BoneClass || at + 17 > b.Length) continue;
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(b[(at + 12)..]);
            int parent = -1;
            if (b[at + 16] == 2)
            {
                if (at + 21 > b.Length) throw new InvalidDataException($"bone {hash:X8}: parent handle runs past the record's end");
                parent = (int)(BinaryPrimitives.ReadUInt32LittleEndian(b[(at + 17)..]) & 0xFFFFFF);
            }
            list.Add(new Bone(list.Count, h & 0xFFFFFF, hash, parent, at));
        }
        return list;
    }
}
