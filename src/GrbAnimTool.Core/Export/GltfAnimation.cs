using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Export;

/// <summary>A glTF file's skeleton and its one animation, read back into the game's axes: what an export holds, and what the import of a
/// clip made in Blender takes.
/// <para>The rules: the armature is the parent node of the skin's root joint (or the skin's skeleton node when that isn't a joint), and has
/// no rest transform of its own; its channels are the mover. Every other node's rest is its translation and rotation. At most one
/// animation; every channel LINEAR (a STEP channel whose keys are all equal is the same curve and is taken), every channel on the same
/// key times, each a whole tick (time x 60). Values are turned back from glTF's axes (<see cref="GltfAxes"/>). Every index and every read
/// of the binary buffer is checked: a file that breaks a rule or doesn't hold together throws <see cref="InvalidDataException"/>.</para></summary>
public sealed class GltfAnimation
{
    /// <summary>The nodes by index: name, parent index (-1 for a scene root).</summary>
    public List<string> Names { get; } = [];
    public List<int> Parents { get; } = [];
    /// <summary>The armature node's index and name; -1 and "" when the joints stand at the scene's roots (no armature node, no mover).</summary>
    public int Armature { get; private set; } = -1;
    public string ArmatureName => Armature >= 0 ? Names[Armature] : "";
    /// <summary>The skin's joints (node indices), in its order.</summary>
    public List<int> Joints { get; } = [];
    /// <summary>Each node's rest, local to its parent, in the game's axes.</summary>
    public Dictionary<string, (Vector3 P, Quaternion Q)> Rest { get; } = [];
    /// <summary>The animation's key times in ticks, empty without an animation.</summary>
    public List<int> Ticks { get; private set; } = [];
    public string? AnimationName { get; private set; }
    public int Animations { get; private set; }
    public int Channels { get; private set; }
    /// <summary>Per node name, the rotation or position at each key time (the armature's are the mover's: <see cref="MoverRotation"/>).</summary>
    public Dictionary<string, Quaternion[]> Rotations { get; } = [];
    public Dictionary<string, Vector3[]> Positions { get; } = [];
    public Quaternion[]? MoverRotation { get; private set; }
    public Vector3[]? MoverPosition { get; private set; }
    /// <summary>The largest distance of any rest or animated scale from 1: the game's bones have none.</summary>
    public double MaxScaleOff { get; private set; }

    /// <summary>Reads a .glb, or a .gltf whose buffer is a file beside it.</summary>
    public static GltfAnimation Read(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(b) == Glb.Magic) return ReadGlb(b);
        return Checked(() => Parse(System.Text.Encoding.UTF8.GetString(b), uri =>
        {
            string name = Uri.UnescapeDataString(uri);
            if (name.Contains(':') || name.Contains('/') || name.Contains('\\') || name.Contains("..")) throw new InvalidDataException($"the buffer '{uri}' isn't a file beside the .gltf");
            try { return File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException($"the buffer '{uri}' can't be read: {ex.Message}", ex); }
        }));
    }

    public static GltfAnimation ReadGlb(byte[] glb)
    {
        var (json, bin) = Glb.Read(glb);
        return Checked(() => Parse(json, _ => throw new InvalidDataException("a .glb whose buffer names an outside file"), bin));
    }

    /// <summary>Any malformed JSON or a member of the wrong type comes out as <see cref="InvalidDataException"/>.</summary>
    static GltfAnimation Checked(Func<GltfAnimation> f)
    {
        try { return f(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"the glTF can't be read: {ex.Message}", ex);
        }
    }

    static int Int(JsonElement e, string name, string what)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i))
            throw new InvalidDataException($"{what}: no whole number '{name}'");
        return i;
    }

    static JsonElement Item(List<JsonElement> list, int i, string what) =>
        i >= 0 && i < list.Count ? list[i] : throw new InvalidDataException($"{what} {i} doesn't exist ({list.Count} there)");

    static List<JsonElement> Array(JsonElement r, string name) =>
        r.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];

    static float[] Numbers(JsonElement e, int n, string what)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != n) throw new InvalidDataException($"{what}: not {n} numbers");
        var f = new float[n];
        int i = 0;
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number) throw new InvalidDataException($"{what}: not a number");
            f[i++] = x.GetSingle();
        }
        return f;
    }

    static GltfAnimation Parse(string json, Func<string, byte[]> external, ReadOnlyMemory<byte> glbBin = default)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("asset", out var asset) || asset.GetProperty("version").GetString() != "2.0") throw new InvalidDataException("not glTF 2.0");
        var buffers = Array(r, "buffers").Select((bf, i) =>
        {
            int len = Int(bf, "byteLength", $"buffer {i}");
            if (len < 0) throw new InvalidDataException($"buffer {i}: a length below 0");
            ReadOnlyMemory<byte> data = bf.TryGetProperty("uri", out var u) ? external(u.GetString() ?? "") : i == 0 ? glbBin : throw new InvalidDataException($"buffer {i} has no data");
            if (data.Length < len) throw new InvalidDataException($"buffer {i} holds {data.Length} B, less than its {len}");
            return data[..len];
        }).ToList();
        var views = Array(r, "bufferViews");
        var accs = Array(r, "accessors");

        float[] Floats(int a, int width)
        {
            var ac = Item(accs, a, "accessor");
            if (Int(ac, "componentType", $"accessor {a}") != Float) throw new InvalidDataException($"accessor {a} doesn't hold floats");
            if (ac.TryGetProperty("sparse", out _)) throw new InvalidDataException($"accessor {a} is sparse");
            int w = ac.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, var t => throw new InvalidDataException($"accessor {a}: type {t}") };
            if (w != width) throw new InvalidDataException($"accessor {a} holds {w} values an item, not {width}");
            int count = Int(ac, "count", $"accessor {a}");
            int vi = Int(ac, "bufferView", $"accessor {a}");
            var v = Item(views, vi, "buffer view");
            int bi = Int(v, "buffer", $"buffer view {vi}");
            if (bi < 0 || bi >= buffers.Count) throw new InvalidDataException($"buffer view {vi} names a buffer that doesn't exist");
            var buf = buffers[bi];
            long vOff = v.TryGetProperty("byteOffset", out var o1) ? o1.GetInt64() : 0, vLen = Int(v, "byteLength", $"buffer view {vi}");
            long aOff = ac.TryGetProperty("byteOffset", out var o2) ? o2.GetInt64() : 0;
            int stride = v.TryGetProperty("byteStride", out var st) ? st.GetInt32() : 4 * w;
            if (count < 0 || stride < 4 * w || vOff < 0 || aOff < 0 || vOff + vLen > buf.Length) throw new InvalidDataException($"accessor {a}'s view doesn't fit its buffer");
            if (count > 0 && aOff + (long)(count - 1) * stride + 4L * w > vLen) throw new InvalidDataException($"accessor {a} runs past its view");
            var span = buf.Span;
            var f = new float[(long)count * w];
            for (int i = 0; i < count; i++)
                for (int j = 0; j < w; j++)
                    f[i * w + j] = BinaryPrimitives.ReadSingleLittleEndian(span[(int)(vOff + aOff + (long)i * stride + 4 * j)..]);
            return f;
        }

        var g = new GltfAnimation();
        var nodes = Array(r, "nodes");
        if (nodes.Count == 0) throw new InvalidDataException("no nodes");
        for (int i = 0; i < nodes.Count; i++)
        {
            g.Names.Add(nodes[i].TryGetProperty("name", out var nm) ? nm.GetString() ?? $"node{i}" : $"node{i}");
            g.Parents.Add(-1);
        }
        for (int i = 0; i < nodes.Count; i++)
            foreach (var c in Array(nodes[i], "children"))
            {
                int ci = c.GetInt32();
                if (ci < 0 || ci >= nodes.Count || ci == i) throw new InvalidDataException($"node {i} names a child {ci} that doesn't exist");
                if (g.Parents[ci] >= 0) throw new InvalidDataException($"node {ci} has two parents");
                g.Parents[ci] = i;
            }
        // no loops: from every node the parents reach a root within the node count
        for (int i = 0; i < nodes.Count; i++)
        {
            int p = i, steps = 0;
            while (p >= 0 && steps++ <= nodes.Count) p = g.Parents[p];
            if (p >= 0) throw new InvalidDataException($"node {i}'s parents form a loop");
        }
        if (g.Names.Distinct(StringComparer.Ordinal).Count() != g.Names.Count) throw new InvalidDataException("two nodes share a name");

        var skins = Array(r, "skins");
        if (skins.Count != 1) throw new InvalidDataException($"{skins.Count} skins, not one");
        foreach (var j in Array(skins[0], "joints"))
        {
            int ji = j.GetInt32();
            if (ji < 0 || ji >= nodes.Count) throw new InvalidDataException($"joint {ji} doesn't exist");
            g.Joints.Add(ji);
        }
        if (g.Joints.Count == 0) throw new InvalidDataException("the skin has no joints");
        var jointSet = g.Joints.ToHashSet();
        var rootJoints = g.Joints.Where(j => g.Parents[j] < 0 || !jointSet.Contains(g.Parents[j])).ToList();
        var armParents = rootJoints.Select(j => g.Parents[j]).Distinct().ToList();
        if (armParents.Count != 1) throw new InvalidDataException("the skin's root joints don't share one parent");
        g.Armature = armParents[0];
        // the skin's skeleton node, when given, is a joint or stands above the joints (the format's rule)
        if (skins[0].TryGetProperty("skeleton", out var sk) && sk.GetInt32() is var ski && !jointSet.Contains(ski))
        {
            bool above = false;
            for (int p = g.Armature; p >= 0 && !above; p = g.Parents[p]) above = p == ski;
            if (!above) throw new InvalidDataException($"the skin's skeleton node {ski} stands neither among nor above the joints");
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n.TryGetProperty("matrix", out _)) throw new InvalidDataException($"node {g.Names[i]} has a matrix: only translation and rotation are read");
            var t = n.TryGetProperty("translation", out var tt) ? Numbers(tt, 3, $"{g.Names[i]}'s translation") : [0f, 0f, 0f];
            var q = n.TryGetProperty("rotation", out var qq) ? Numbers(qq, 4, $"{g.Names[i]}'s rotation") : [0f, 0f, 0f, 1f];
            if (n.TryGetProperty("scale", out var ss)) foreach (var x in Numbers(ss, 3, $"{g.Names[i]}'s scale")) g.MaxScaleOff = Math.Max(g.MaxScaleOff, Math.Abs(x - 1.0));
            if (i == g.Armature)
            {
                if (Math.Abs(t[0]) + Math.Abs(t[1]) + Math.Abs(t[2]) > 1e-6 || Math.Abs(Math.Abs(q[3]) - 1) > 1e-6)
                    throw new InvalidDataException("the armature's node has a transform of its own");
                continue;
            }
            g.Rest[g.Names[i]] = (GltfAxes.FromGltf(new Vector3(t[0], t[1], t[2])), GltfAxes.FromGltf(new Quaternion(q[0], q[1], q[2], q[3])));
        }

        var anims = Array(r, "animations");
        g.Animations = anims.Count;
        if (anims.Count > 1) throw new InvalidDataException($"{anims.Count} animations, not one");
        if (anims.Count == 0) return g;
        var a = anims[0];
        g.AnimationName = a.TryGetProperty("name", out var an) ? an.GetString() : null;
        var samplers = Array(a, "samplers");
        var seen = new HashSet<(int, string)>();
        foreach (var c in Array(a, "channels"))
        {
            g.Channels++;
            var tg = c.GetProperty("target");
            int node = Int(tg, "node", "a channel's target");
            if (node < 0 || node >= nodes.Count) throw new InvalidDataException($"a channel targets node {node}, which doesn't exist");
            string nm = g.Names[node];
            string path = tg.GetProperty("path").GetString() ?? "";
            if (!seen.Add((node, path))) throw new InvalidDataException($"{nm} {path}: two channels");
            var s = Item(samplers, Int(c, "sampler", $"{nm} {path}"), "sampler");
            int width = path switch { "rotation" => 4, "translation" or "scale" => 3, _ => throw new InvalidDataException($"{nm}: a channel on {path}") };
            var times = Floats(Int(s, "input", $"{nm} {path}"), 1);
            var v = Floats(Int(s, "output", $"{nm} {path}"), width);
            if (v.Length != times.Length * width) throw new InvalidDataException($"{nm} {path}: {v.Length / width} values for {times.Length} times");
            string ip = s.TryGetProperty("interpolation", out var ipe) ? ipe.GetString() ?? "" : "LINEAR";
            bool constant = Enumerable.Range(width, Math.Max(0, v.Length - width)).All(i => Math.Abs(v[i] - v[i % width]) < 1e-6f);
            if (ip != "LINEAR" && !(ip == "STEP" && constant)) throw new InvalidDataException($"{nm} {path}: {ip} interpolation, not LINEAR");
            var ticks = new List<int>(times.Length);
            foreach (var tm in times)
            {
                double tk = tm * (double)ClipRecord.TicksPerSecond;
                if (!double.IsFinite(tk) || Math.Abs(tk - Math.Round(tk)) > 1e-3 || tk < 0 || tk > ClipRecord.MaxDuration * ClipRecord.TicksPerSecond)
                    throw new InvalidDataException($"{nm} {path}: a key at {tm} s, not on a tick of 1/60 s");
                ticks.Add((int)Math.Round(tk));
            }
            for (int i = 1; i < ticks.Count; i++) if (ticks[i] <= ticks[i - 1]) throw new InvalidDataException($"{nm} {path}: key times don't rise");
            if (g.Ticks.Count == 0) g.Ticks = ticks;
            else if (!g.Ticks.SequenceEqual(ticks)) throw new InvalidDataException($"{nm} {path}: other key times than the first channel's");
            int k = ticks.Count;
            if (path == "rotation")
            {
                var q = new Quaternion[k];
                for (int i = 0; i < k; i++) q[i] = GltfAxes.FromGltf(new Quaternion(v[4 * i], v[4 * i + 1], v[4 * i + 2], v[4 * i + 3]));
                if (node == g.Armature) g.MoverRotation = q; else g.Rotations[nm] = q;
            }
            else if (path == "translation")
            {
                var p = new Vector3[k];
                for (int i = 0; i < k; i++) p[i] = GltfAxes.FromGltf(new Vector3(v[3 * i], v[3 * i + 1], v[3 * i + 2]));
                if (node == g.Armature) g.MoverPosition = p; else g.Positions[nm] = p;
            }
            else for (int i = 0; i < v.Length; i++) g.MaxScaleOff = Math.Max(g.MaxScaleOff, Math.Abs(v[i] - 1.0));
        }
        return g;
    }

    const int Float = 5126;
}
