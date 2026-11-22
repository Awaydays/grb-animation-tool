using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.IO;

namespace GrbAnimTool.Core.Skeletons;

/// <summary>A rigid transform: a rotation, then a translation (metres).
/// <para>Axis convention: the game's model space has x the character's left-right (its left at -x), y forward and z up, in metres. glTF is
/// y up: a hand-over to glTF turns every vector by (x, y, z) to (x, z, -y), a proper rotation, which Blender's importer turns back.</para></summary>
public readonly record struct Transform(Vector3 Position, Quaternion Rotation)
{
    public static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity);

    /// <summary>A child's local transform placed in its parent's frame: the parent's rotation applies to the child's position and rotation.</summary>
    public static Transform operator *(Transform parent, Transform local) =>
        new(parent.Position + Vector3.Transform(local.Position, parent.Rotation), Quaternion.Normalize(parent.Rotation * local.Rotation));

    /// <summary>A point in this frame, expressed in the frame's parent.</summary>
    public Vector3 Apply(Vector3 p) => Position + Vector3.Transform(p, Rotation);
}

/// <summary>A skeleton bone: its name hash (the CRC-32 of its name), name if known, parent (-1 for a root), rest pose as stored (local to
/// the parent, and in model space), and which record it came from.</summary>
public sealed record SkeletonBone(int Index, uint Hash, string Name, int ParentIndex, Transform LocalRest, Transform ModelRest, int FileIndex, bool FromAddOn)
{
    public bool IsRoot => ParentIndex < 0;
}

/// <summary>A skeleton record (type 0x24AECB7C, the CRC-32 of "Skeleton") as a tree with its rest pose, and forward kinematics over it.
/// Bones are held parents first, so one pass in order composes the whole tree.
/// <para>A bone object holds, after its hash, its parent (0x02 + handle + u32 0, or 0x03 for none) and its own reference (the same two forms),
/// then four vectors of four floats: model position, model rotation (x y z w), local position, local rotation. The locals compose to the
/// stored model pose.</para></summary>
public sealed class Skeleton
{
    public const uint Type = 0x24AECB7C;

    readonly Dictionary<uint, int> _byHash = [];

    public IReadOnlyList<SkeletonBone> Bones { get; }

    Skeleton(List<SkeletonBone> bones)
    {
        Bones = bones;
        foreach (var b in bones) _byHash.TryAdd(b.Hash, b.Index);
    }

    /// <summary>The bone's index, or -1.</summary>
    public int IndexOf(uint hash) => _byHash.GetValueOrDefault(hash, -1);

    public bool Contains(uint hash) => _byHash.ContainsKey(hash);

    static float[] Vectors(ReadOnlySpan<byte> b, RecordObjects.Bone x)
    {
        int q = x.At + 16;
        byte p = Bin.U8(b, q, "bone parent form");
        q += p == 2 ? 9 : 1;
        byte r = Bin.U8(b, q, "bone reference form");
        q += r == 2 ? 9 : r == 3 ? 1 : throw new InvalidDataException($"bone {x.Hash:X8}: reference byte {r:X2}");
        Bin.Need(b, q, 64, $"bone {x.Hash:X8}'s vectors");
        var f = new float[16];
        for (int i = 0; i < 16; i++)
        {
            f[i] = Bin.F32(b, q + 4 * i, "bone vector");
            // the fourth value of each position vector (3 and 11) is not used
            if (i is not (3 or 11) && !float.IsFinite(f[i])) throw new InvalidDataException($"bone {x.Hash:X8}: rest pose value {i} is {f[i]}");
        }
        return f;
    }

    /// <summary>Parses a whole skeleton record. Bones whose parent handle isn't in the record are roots. A rest pose value that isn't finite
    /// throws <see cref="InvalidDataException"/>.</summary>
    public static Skeleton Parse(ReadOnlySpan<byte> record, BoneNames? names = null, bool addOn = false)
    {
        var bones = RecordObjects.Bones(record);
        if (bones.Count == 0) throw new InvalidDataException("no bones in the skeleton record");
        var byHandle = new Dictionary<uint, RecordObjects.Bone>();
        foreach (var b in bones) byHandle.TryAdd(b.Handle, b);
        var raw = new Dictionary<uint, (RecordObjects.Bone B, uint Parent, Transform Local, Transform Model)>();
        foreach (var b in bones)
        {
            var v = Vectors(record, b);
            uint parent = b.ParentHandle >= 0 && byHandle.TryGetValue((uint)b.ParentHandle, out var pb) && pb.Hash != b.Hash ? pb.Hash : 0;
            raw.TryAdd(b.Hash, (b, parent,
                new Transform(new Vector3(v[8], v[9], v[10]), new Quaternion(v[12], v[13], v[14], v[15])),
                new Transform(new Vector3(v[0], v[1], v[2]), new Quaternion(v[4], v[5], v[6], v[7]))));
        }
        // parents first, without recursion: from each bone, climb to the first ancestor not yet placed (or to where a parent loop closes),
        // then place the chain top down. A bone in a loop is placed as a root where the loop closes.
        var order = new List<SkeletonBone>();
        var index = new Dictionary<uint, int>();
        var chain = new List<uint>();
        var onChain = new HashSet<uint>();
        foreach (var b in bones)
        {
            chain.Clear(); onChain.Clear();
            for (uint h = b.Hash; !index.ContainsKey(h) && onChain.Add(h);)
            {
                chain.Add(h);
                uint p = raw[h].Parent;
                if (p == 0 || !raw.ContainsKey(p)) break;
                h = p;
            }
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                uint h = chain[i];
                var x = raw[h];
                int pi = x.Parent != 0 ? index.GetValueOrDefault(x.Parent, -1) : -1;
                index[h] = order.Count;
                order.Add(new SkeletonBone(order.Count, h, names?.NameOf(h) ?? BoneNames.Hex(h), pi, x.Local, x.Model, x.B.Index, addOn));
            }
        }
        return new Skeleton(order);
    }

    /// <summary>The body skeleton with an add-on's bones that the body lacks appended. An add-on bone whose parent is a body bone (by hash)
    /// hangs from that body bone; the body's own bones keep the body's rest pose.</summary>
    public static Skeleton Merge(Skeleton body, Skeleton addOn)
    {
        var list = new List<SkeletonBone>(body.Bones);
        var index = body._byHash.ToDictionary();
        foreach (var a in addOn.Bones)
        {
            if (index.ContainsKey(a.Hash)) continue;
            int pi = a.ParentIndex >= 0 ? index.GetValueOrDefault(addOn.Bones[a.ParentIndex].Hash, -1) : -1;
            index[a.Hash] = list.Count;
            list.Add(a with { Index = list.Count, ParentIndex = pi, FromAddOn = true });
        }
        return new Skeleton(list);
    }

    /// <summary>The rest pose's local transforms, by bone index.</summary>
    public Transform[] RestLocals() => Bones.Select(b => b.LocalRest).ToArray();

    /// <summary>Forward kinematics: <paramref name="locals"/> by bone index into <paramref name="model"/> by bone index. A root's model
    /// transform is its local one, so the result is relative to the clip's mover (track 0), which is not a skeleton bone.</summary>
    public void ComputeModel(ReadOnlySpan<Transform> locals, Span<Transform> model)
    {
        if (locals.Length < Bones.Count || model.Length < Bones.Count) throw new ArgumentException($"need {Bones.Count} transforms");
        for (int i = 0; i < Bones.Count; i++)
        {
            int p = Bones[i].ParentIndex;
            model[i] = p >= 0 ? model[p] * locals[i] : locals[i];
        }
    }

    /// <summary>Forward kinematics by track hash: the given local rotations and positions, the rest pose's where none is given. Returns model
    /// transforms by bone index.</summary>
    public Transform[] Fk(IReadOnlyDictionary<uint, Quaternion> rot, IReadOnlyDictionary<uint, Vector3> pos)
    {
        var locals = new Transform[Bones.Count];
        for (int i = 0; i < Bones.Count; i++)
        {
            var b = Bones[i];
            locals[i] = new Transform(pos.TryGetValue(b.Hash, out var p) ? p : b.LocalRest.Position, rot.TryGetValue(b.Hash, out var q) ? q : b.LocalRest.Rotation);
        }
        var model = new Transform[Bones.Count];
        ComputeModel(locals, model);
        return model;
    }
}
