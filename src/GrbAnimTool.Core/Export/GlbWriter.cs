using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Export;

/// <summary>A deliberate fault in a written file, for the controls that must fail: the axes not turned for glTF, or one thigh 2% long
/// (<c>LeftLeg</c>'s offset from its parent).</summary>
public enum ExportFault { None, AxesNotTurned, ThighLong }

/// <summary>Writes the skeleton, alone or with one clip, as a glTF 2.0 binary (.glb), by hand.
/// <para>The node tree: node 0 is the armature, named "Armature", with no transform of its own; under it the skeleton's roots, and every
/// bone as a node of its own in the skeleton's order (parents first), named by its name or, unnamed, by its hash as eight hex digits, at
/// its rest pose local to its parent. One skin lists every bone as a joint, with the armature as its skeleton root and inverse bind
/// matrices of the rest pose; there is no mesh.</para>
/// <para>A clip adds one animation: one time per tick, 0 to the last, at 60 a second, and for every bone the clip has a rotation or a
/// position track for, a LINEAR channel holding the track sampled at every tick. The mover (track 0) is a channel on the armature's node,
/// so the armature travels and turns as the clip does; Blender's importer keys it on the armature object. Values are the game's turned
/// for glTF's axes (<see cref="GltfAxes"/>). Successive rotations keep one sign, so the interpolation between them takes the short
/// way.</para></summary>
public static class GlbWriter
{
    public const string ArmatureNode = "Armature";

    const int Float = 5126;

    public static byte[] Skeleton(Skeleton s, ExportFault fault = ExportFault.None) => Build(s, null, "GRB skeleton", fault);

    public static byte[] Clip(Skeleton s, SampledClip clip, string name, ExportFault fault = ExportFault.None) => Build(s, clip, name, fault);

    sealed class Accessor(int view, int count, string type, float[]? min = null, float[]? max = null)
    {
        public int View => view; public int Count => count; public string Type => type; public float[]? Min => min; public float[]? Max => max;
    }

    sealed class Bin
    {
        readonly MemoryStream _m = new();
        public readonly List<(int Offset, int Length)> Views = [];
        public readonly List<Accessor> Accessors = [];

        public int Add(ReadOnlySpan<float> v, int count, string type, float[]? min = null, float[]? max = null)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), v[i]);
            Views.Add(((int)_m.Length, b.Length));
            _m.Write(b);     // float data only, so every view stays 4-aligned
            Accessors.Add(new Accessor(Views.Count - 1, count, type, min, max));
            return Accessors.Count - 1;
        }

        public byte[] ToArray() => _m.ToArray();
    }

    static Vector3 P(Vector3 p, ExportFault f) => f == ExportFault.AxesNotTurned ? p : GltfAxes.ToGltf(p);
    static Quaternion Q(Quaternion q, ExportFault f) => f == ExportFault.AxesNotTurned ? q : GltfAxes.ToGltf(q);

    static byte[] Build(Skeleton s, SampledClip? clip, string name, ExportFault fault)
    {
        var bones = s.Bones;
        int n = bones.Count;
        uint thigh = Crc32.Of("LeftLeg");
        // the rest pose as written, in glTF's axes
        var local = new Transform[n];
        for (int i = 0; i < n; i++)
        {
            var lr = bones[i].LocalRest;
            var p = lr.Position * (fault == ExportFault.ThighLong && bones[i].Hash == thigh ? 1.02f : 1f);
            local[i] = new Transform(P(p, fault), Q(Quaternion.Normalize(lr.Rotation), fault));
            // a rest rotation of zero length normalises to no number at all, which a glTF file can't hold
            if (!float.IsFinite(local[i].Rotation.LengthSquared()) || !float.IsFinite(local[i].Position.LengthSquared()))
                throw new ExportException($"the skeleton's bone {bones[i].Name} has a rest pose that isn't a rotation and a position, so it can't be written");
        }
        var world = new Transform[n];
        for (int i = 0; i < n; i++) world[i] = bones[i].ParentIndex >= 0 ? world[bones[i].ParentIndex] * local[i] : local[i];

        var bin = new Bin();
        var ibm = new float[16 * n];
        for (int i = 0; i < n; i++)
        {
            var inv = GltfAxes.Inverse(world[i]);
            var m = Matrix4x4.CreateFromQuaternion(inv.Rotation);
            m.Translation = inv.Position;
            // System.Numerics' row-vector matrix in field order is glTF's column-major column-vector matrix
            float[] f = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
            f.CopyTo(ibm, 16 * i);
        }
        int ibmAcc = bin.Add(ibm, n, "MAT4");

        var samplers = new List<(int Input, int Output)>();
        var channels = new List<(int Sampler, int Node, string Path)>();
        if (clip is not null)
        {
            int frames = clip.Frames;
            var times = new float[frames];
            for (int t = 0; t < frames; t++) times[t] = t / (float)ClipRecord.TicksPerSecond;
            int input = bin.Add(times, frames, "SCALAR", [times[0]], [times[^1]]);
            void Rot(Quaternion[] q, int node)
            {
                var v = new float[frames * 4];
                var prev = Quaternion.Identity;
                for (int t = 0; t < frames; t++)
                {
                    var g = Q(Quaternion.Normalize(q[t]), fault);
                    if (t > 0 && Quaternion.Dot(prev, g) < 0) g = Quaternion.Negate(g);
                    prev = g;
                    v[4 * t] = g.X; v[4 * t + 1] = g.Y; v[4 * t + 2] = g.Z; v[4 * t + 3] = g.W;
                }
                samplers.Add((input, bin.Add(v, frames, "VEC4")));
                channels.Add((samplers.Count - 1, node, "rotation"));
            }
            void Pos(Vector3[] p, int node)
            {
                var v = new float[frames * 3];
                for (int t = 0; t < frames; t++)
                {
                    var g = P(p[t], fault);
                    v[3 * t] = g.X; v[3 * t + 1] = g.Y; v[3 * t + 2] = g.Z;
                }
                samplers.Add((input, bin.Add(v, frames, "VEC3")));
                channels.Add((samplers.Count - 1, node, "translation"));
            }
            if (clip.MoverRotation is { } mr) Rot(mr, 0);
            if (clip.MoverPosition is { } mp) Pos(mp, 0);
            for (int i = 0; i < n; i++)
            {
                if (clip.Rotations[i] is { } r) Rot(r, i + 1);
                if (clip.Positions[i] is { } p) Pos(p, i + 1);
            }
            // a clip with no track on this skeleton (a face clip, say) still has its length: glTF wants a channel in an animation, so the
            // first root bone holds its rest rotation at every tick, which is the pose the clip gives it
            if (channels.Count == 0)
            {
                int root = bones.First(b => b.ParentIndex < 0).Index;
                Rot(Enumerable.Repeat(bones[root].LocalRest.Rotation, frames).ToArray(), root + 1);
            }
        }

        var data = bin.ToArray();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", $"{ToolInfo.Name} {ToolInfo.Version}");
            w.WriteEndObject();
            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes");
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteStartArray("nodes"); w.WriteNumberValue(0); w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            w.WriteStartObject();
            w.WriteString("name", ArmatureNode);
            Children(w, bones.Where(b => b.ParentIndex < 0));
            w.WriteEndObject();
            for (int i = 0; i < n; i++)
            {
                w.WriteStartObject();
                w.WriteString("name", bones[i].Name);
                Children(w, bones.Where(b => b.ParentIndex == i));
                var t = local[i];
                w.WriteStartArray("rotation");
                w.WriteNumberValue(t.Rotation.X); w.WriteNumberValue(t.Rotation.Y); w.WriteNumberValue(t.Rotation.Z); w.WriteNumberValue(t.Rotation.W);
                w.WriteEndArray();
                w.WriteStartArray("translation");
                w.WriteNumberValue(t.Position.X); w.WriteNumberValue(t.Position.Y); w.WriteNumberValue(t.Position.Z);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("skins");
            w.WriteStartObject();
            w.WriteString("name", ArmatureNode);
            w.WriteNumber("inverseBindMatrices", ibmAcc);
            w.WriteNumber("skeleton", 0);
            w.WriteStartArray("joints");
            for (int i = 0; i < n; i++) w.WriteNumberValue(i + 1);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            if (clip is not null)
            {
                w.WriteStartArray("animations");
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteStartArray("samplers");
                foreach (var (input, output) in samplers)
                {
                    w.WriteStartObject();
                    w.WriteNumber("input", input);
                    w.WriteString("interpolation", "LINEAR");
                    w.WriteNumber("output", output);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("channels");
                foreach (var (sampler, node, path) in channels)
                {
                    w.WriteStartObject();
                    w.WriteNumber("sampler", sampler);
                    w.WriteStartObject("target");
                    w.WriteNumber("node", node);
                    w.WriteString("path", path);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteStartArray("accessors");
            foreach (var a in bin.Accessors)
            {
                w.WriteStartObject();
                w.WriteNumber("bufferView", a.View);
                w.WriteNumber("componentType", Float);
                w.WriteNumber("count", a.Count);
                w.WriteString("type", a.Type);
                if (a.Min is { } mn) { w.WriteStartArray("min"); foreach (var v in mn) w.WriteNumberValue(v); w.WriteEndArray(); }
                if (a.Max is { } mx) { w.WriteStartArray("max"); foreach (var v in mx) w.WriteNumberValue(v); w.WriteEndArray(); }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("bufferViews");
            foreach (var (offset, length) in bin.Views)
            {
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", offset);
                w.WriteNumber("byteLength", length);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("buffers");
            w.WriteStartObject();
            w.WriteNumber("byteLength", data.Length);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Glb.Write(ms.ToArray(), data);
    }

    static void Children(Utf8JsonWriter w, IEnumerable<SkeletonBone> children)
    {
        var c = children.Select(b => b.Index + 1).ToList();
        if (c.Count == 0) return;
        w.WriteStartArray("children");
        foreach (var i in c) w.WriteNumberValue(i);
        w.WriteEndArray();
    }
}
