using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using GrbAnimTool.Core.Export;

namespace GrbAnimTool.Core.Import;

/// <summary>How a glTF channel's keys are joined.</summary>
public enum GltfInterpolation { Linear, Step, CubicSpline }

/// <summary>One animation channel as a file holds it, on its own key times: the node and the property it drives, how its keys are joined,
/// the times in seconds and the values (4 a key for rotations, 3 for translations and scales; three times as many for a cubic spline: the
/// in-tangent, the value and the out-tangent of each key), in glTF's axes.</summary>
public sealed record GltfChannel(int Node, string Path, GltfInterpolation Interpolation, float[] Times, float[] Values)
{
    public int Width => Path == "rotation" ? 4 : 3;

    /// <summary>The channel's value at <paramref name="seconds"/>: before the first key the first, after the last the last; between keys
    /// joined by its interpolation (rotations by spherical interpolation, the shorter way, and normalised).</summary>
    public float[] At(double seconds)
    {
        int w = Width, k = Times.Length;
        bool cubic = Interpolation == GltfInterpolation.CubicSpline;
        // a key's time written as a float may stand a hair after the tick asked for: a microsecond's room finds it
        seconds += 1e-6;
        float[] Key(int i) { var v = new float[w]; Array.Copy(Values, (cubic ? 3 * i + 1 : i) * w, v, 0, w); return v; }
        if (k == 1 || seconds <= Times[0]) return Norm(Key(0));
        if (seconds >= Times[k - 1]) return Norm(Key(k - 1));
        int a = Array.BinarySearch(Times, (float)seconds);
        if (a >= 0) { while (a + 1 < k && Times[a + 1] <= seconds) a++; }
        else a = ~a - 1;
        a = Math.Clamp(a, 0, k - 2);
        double t0 = Times[a], t1 = Times[a + 1], dt = t1 - t0;
        float s = dt > 0 ? (float)Math.Clamp((seconds - t0) / dt, 0, 1) : 0;
        switch (Interpolation)
        {
            case GltfInterpolation.Step: return Norm(Key(a));
            case GltfInterpolation.Linear:
                {
                    var p = Key(a); var q = Key(a + 1);
                    if (w == 4)
                    {
                        var r = Quaternion.Slerp(new Quaternion(p[0], p[1], p[2], p[3]), new Quaternion(q[0], q[1], q[2], q[3]), s);
                        return Norm([r.X, r.Y, r.Z, r.W]);
                    }
                    return [p[0] + (q[0] - p[0]) * s, p[1] + (q[1] - p[1]) * s, p[2] + (q[2] - p[2]) * s];
                }
            default:
                {
                    // the format's cubic Hermite spline: the value and out-tangent of key a, the in-tangent and value of key a + 1
                    float s2 = s * s, s3 = s2 * s;
                    float h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2, h11 = s3 - s2;
                    var r = new float[w];
                    for (int j = 0; j < w; j++)
                    {
                        float v0 = Values[(3 * a + 1) * w + j], b0 = Values[(3 * a + 2) * w + j];
                        float a1 = Values[(3 * (a + 1)) * w + j], v1 = Values[(3 * (a + 1) + 1) * w + j];
                        r[j] = h00 * v0 + h10 * (float)dt * b0 + h01 * v1 + h11 * (float)dt * a1;
                    }
                    return Norm(r);
                }
        }

        float[] Norm(float[] v)
        {
            if (w != 4) return v;
            float l = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2] + v[3] * v[3]);
            return l > 0 && float.IsFinite(l) ? [v[0] / l, v[1] / l, v[2] / l, v[3] / l] : [0, 0, 0, 1];
        }
    }
}

/// <summary>A node: its name, its parent (-1 for a scene root) and its own translation, rotation and scale, in glTF's axes.</summary>
public sealed record GltfNode(string Name, int Parent, Vector3 Translation, Quaternion Rotation, Vector3 Scale);

/// <summary>An animation: its name and channels.</summary>
public sealed record GltfAnimationClip(string? Name, IReadOnlyList<GltfChannel> Channels);

/// <summary>A glTF binary file (.glb) read for what an import needs: the node tree with each node's own transform, and every animation
/// with each channel on its own key times, as Blender's exporter writes them (with "Optimize Animation Size", its default, channels of two
/// keys and STEP channels stand beside channels of every frame). Every index and every read of the binary buffer is checked against what
/// the file holds, and any malformed part comes out as an <see cref="InvalidDataException"/> saying what: a damaged file never hangs the
/// read or runs past its end.</summary>
public sealed class GlbScene
{
    public IReadOnlyList<GltfNode> Nodes { get; private init; } = [];
    public IReadOnlyList<GltfAnimationClip> Animations { get; private init; } = [];
    public string? Generator { get; private init; }

    /// <summary>The most keys one channel may hold: the longest clip of the game (36,001 ticks) keyed twice a tick. A cubic spline's values
    /// accessor holds three items a key (its tangents).</summary>
    public const int MaxKeys = 2 * 36_001;
    /// <summary>The most animation channels a file may hold, all animations together: the skeleton's bones and the armature, three
    /// properties each, many times over.</summary>
    public const int MaxChannels = 4_096;
    /// <summary>The most floats the read decodes, all accessors together (each accessor is decoded once, however many channels share it):
    /// at most this, and at most twice the floats the file's binary chunk holds (accessors may overlap, but not much).</summary>
    public const long MaxDecodedFloats = 40_000_000;
    /// <summary>The largest JSON chunk read: Blender's whole export of a long clip is under a megabyte, its JSON a fraction of that, so this
    /// is many times what an exporter writes, and it bounds the parse's own memory (a parsed document takes several times its text).</summary>
    public const int MaxJsonBytes = 16 * 1024 * 1024;
    /// <summary>The most items any one list of the JSON may hold (nodes, accessors, buffer views, animations, samplers, channels): checked
    /// before the list is taken.</summary>
    public const int MaxListItems = 100_000;

    public static GlbScene Read(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new InvalidDataException($"{path} doesn't exist");
        // a .glb of a skeleton and a long clip is a few MB; anything past this isn't a clip file
        if (fi.Length > 512L * 1024 * 1024) throw new InvalidDataException($"{path} is {fi.Length / 1048576} MB, too large for a clip");
        return Read(File.ReadAllBytes(path));
    }

    public static GlbScene Read(byte[] glb)
    {
        if (glb.Length >= 1 && glb[0] == (byte)'{') throw new InvalidDataException("this is a .gltf text file; export a glTF binary (.glb)");
        // the JSON chunk's length (the first chunk, after the 12-byte header) is refused past the cap before its text is taken or parsed
        if (glb.Length >= 20 && glb.AsSpan(0, 4).SequenceEqual("glTF"u8) && BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12)) is var jsonLength && jsonLength > MaxJsonBytes)
            throw new InvalidDataException($"its JSON chunk is {jsonLength / 1048576.0:F1} MB, past the {MaxJsonBytes / 1048576} MB this tool reads (one clip on the game's skeleton needs well under 1 MB): not read");
        var (json, bin) = Glb.Read(glb);
        if (json.Length > MaxJsonBytes) throw new InvalidDataException($"its JSON is {json.Length / 1048576.0:F1} MB, past the {MaxJsonBytes / 1048576} MB a clip's file needs: not read");
        try { return Parse(json, bin); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException($"the glTF can't be read: {Shown(ex.Message)}", ex);
        }
    }

    /// <summary>Text from the file as it is kept and shown: every control character, line or paragraph separator (U+2028, U+2029) and
    /// bidirectional control (U+202A to U+202E, U+2066 to U+2069) replaced by '?', so a name can't break a message's line, reorder the
    /// rest of it, or drive a terminal. No bone's name holds one, so a name so changed matches the bones it matched before: none.</summary>
    static string Shown(string s) => s.Any(Unshown) ? string.Concat(s.Select(c => Unshown(c) ? '?' : c)) : s;

    static bool Unshown(char c) => char.IsControl(c) || (c >= 0x2028 && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069);

    const int Float = 5126;

    static int Int(JsonElement e, string name, string what)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i))
            throw new InvalidDataException($"{what}: no whole number '{name}'");
        return i;
    }

    static int OptInt(JsonElement e, string name, int def, string what) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : throw new InvalidDataException($"{what}: '{name}' isn't a whole number")) : def;

    static List<JsonElement> Array(JsonElement r, string name)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(name, out var a)) return [];
        if (a.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"'{name}' isn't a list");
        int n = a.GetArrayLength();
        if (n > MaxListItems) throw new InvalidDataException($"'{name}' lists {n:N0} items, past the {MaxListItems:N0} a clip's file could need");
        return a.EnumerateArray().ToList();
    }

    static float[] Numbers(JsonElement e, int n, string what)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != n) throw new InvalidDataException($"{what}: not {n} numbers");
        var f = new float[n];
        int i = 0;
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number) throw new InvalidDataException($"{what}: not a number");
            f[i] = x.GetSingle();
            if (!float.IsFinite(f[i++])) throw new InvalidDataException($"{what}: a value that isn't a finite number");
        }
        return f;
    }

    static GlbScene Parse(string json, ReadOnlyMemory<byte> glbBin)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.Object
            || !asset.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.String || ver.GetString() != "2.0")
            throw new InvalidDataException("not a glTF 2.0 file");
        string? gen = asset.TryGetProperty("generator", out var gn) && gn.ValueKind == JsonValueKind.String ? Shown(gn.GetString()!) : null;
        foreach (var x in Array(r, "extensionsRequired"))
            throw new InvalidDataException($"the file requires the glTF extension {Shown(x.ToString())}, which this tool doesn't read; export without it (without compression or quantization)");
        var buffers = Array(r, "buffers").Select((bf, i) =>
        {
            int len = Int(bf, "byteLength", $"buffer {i}");
            if (bf.TryGetProperty("uri", out _)) throw new InvalidDataException($"buffer {i} names an outside file: export a glTF binary (.glb) that holds its own data");
            if (i != 0) throw new InvalidDataException($"buffer {i} has no data (a .glb holds one buffer)");
            if (len < 0 || glbBin.Length < len) throw new InvalidDataException($"buffer {i} claims {len} B, the file's binary chunk holds {glbBin.Length} B");
            return glbBin[..len];
        }).ToList();
        var views = Array(r, "bufferViews");
        var accs = Array(r, "accessors");
        var decodedAccessors = new Dictionary<int, float[]>();
        long decoded = 0, budget = Math.Min(MaxDecodedFloats, 2L * (glbBin.Length / 4) + 65_536);

        float[] Floats(int a, int width, string what)
        {
            if (decodedAccessors.TryGetValue(a, out var known))
            {
                if (known.Length % width != 0 || Int(accs[a], "count", $"accessor {a}") * (long)width != known.Length)
                    throw new InvalidDataException($"{what}: accessor {a} doesn't hold {width} values an item");
                return known;
            }
            var f = Decode(a, width, what);
            decodedAccessors[a] = f;
            return f;
        }

        float[] Decode(int a, int width, string what)
        {
            if (a < 0 || a >= accs.Count) throw new InvalidDataException($"{what}: accessor {a} doesn't exist ({accs.Count} there)");
            var ac = accs[a];
            if (Int(ac, "componentType", $"accessor {a}") != Float) throw new InvalidDataException($"{what}: accessor {a} doesn't hold floats (export without quantization)");
            if (ac.TryGetProperty("sparse", out _)) throw new InvalidDataException($"{what}: accessor {a} is sparse");
            string type = ac.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() ?? "" : "";
            int w = type switch { "SCALAR" => 1, "VEC3" => 3, "VEC4" => 4, _ => throw new InvalidDataException($"{what}: accessor {a} has type '{Shown(type)}'") };
            if (w != width) throw new InvalidDataException($"{what}: accessor {a} holds {w} values an item, not {width}");
            int count = Int(ac, "count", $"accessor {a}");
            if (count < 1 || count > 3 * MaxKeys) throw new InvalidDataException($"{what}: accessor {a} holds {count} items");
            int vi = Int(ac, "bufferView", $"accessor {a}");
            if (vi < 0 || vi >= views.Count) throw new InvalidDataException($"{what}: buffer view {vi} doesn't exist");
            var v = views[vi];
            int bi = Int(v, "buffer", $"buffer view {vi}");
            if (bi < 0 || bi >= buffers.Count) throw new InvalidDataException($"{what}: buffer view {vi} names a buffer that doesn't exist");
            var buf = buffers[bi];
            long vOff = OptInt(v, "byteOffset", 0, $"buffer view {vi}"), vLen = Int(v, "byteLength", $"buffer view {vi}");
            long aOff = OptInt(ac, "byteOffset", 0, $"accessor {a}");
            long stride = OptInt(v, "byteStride", 4 * w, $"buffer view {vi}");
            if (stride < 4 * w || stride % 4 != 0 || vOff < 0 || vLen < 0 || aOff < 0 || vOff + vLen > buf.Length) throw new InvalidDataException($"{what}: accessor {a}'s view doesn't fit its buffer");
            if (aOff + (count - 1) * stride + 4L * w > vLen) throw new InvalidDataException($"{what}: accessor {a} runs past its view");
            decoded += (long)count * w;
            if (decoded > budget)
                throw new InvalidDataException($"its accessors decode to more than {budget:N0} floats (the file's binary chunk holds {glbBin.Length / 4:N0}): the accessors overlap far more than an exporter writes them");
            var span = buf.Span;
            var f = new float[(long)count * w];
            for (int i = 0; i < count; i++)
                for (int j = 0; j < w; j++)
                {
                    float x = BinaryPrimitives.ReadSingleLittleEndian(span[(int)(vOff + aOff + i * stride + 4 * j)..]);
                    if (!float.IsFinite(x)) throw new InvalidDataException($"{what}: a value that isn't a finite number");
                    f[i * w + j] = x;
                }
            return f;
        }

        var nodesJ = Array(r, "nodes");
        if (nodesJ.Count == 0) throw new InvalidDataException("the file has no nodes");
        if (nodesJ.Count > 100_000) throw new InvalidDataException($"{nodesJ.Count} nodes");
        var parents = Enumerable.Repeat(-1, nodesJ.Count).ToArray();
        var names = new string[nodesJ.Count];
        for (int i = 0; i < nodesJ.Count; i++)
        {
            if (nodesJ[i].ValueKind != JsonValueKind.Object) throw new InvalidDataException($"node {i} isn't an object");
            names[i] = nodesJ[i].TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? Shown(nm.GetString() ?? $"node{i}") : $"node{i}";
        }
        for (int i = 0; i < nodesJ.Count; i++)
            foreach (var c in Array(nodesJ[i], "children"))
            {
                if (c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out int ci) || ci < 0 || ci >= nodesJ.Count)
                    throw new InvalidDataException($"node {names[i]} names a child that doesn't exist");
                if (ci == i) throw new InvalidDataException($"node {names[i]} names itself as its child");
                if (parents[ci] >= 0) throw new InvalidDataException($"node {names[ci]} has two parents");
                parents[ci] = i;
            }
        // a loop of parents: each node's walk up stops at a node already known to reach a root (state 2), so the whole check is one pass
        var state = new byte[nodesJ.Count];   // 0 not seen, 1 on the current walk, 2 reaches a root
        var walk = new List<int>();
        for (int i = 0; i < nodesJ.Count; i++)
        {
            walk.Clear();
            int p = i;
            while (p >= 0 && state[p] == 0) { state[p] = 1; walk.Add(p); p = parents[p]; }
            if (p >= 0 && state[p] == 1) throw new InvalidDataException($"node {names[i]}'s parents form a loop");
            foreach (int w in walk) state[w] = 2;
        }
        var nodes = new List<GltfNode>(nodesJ.Count);
        for (int i = 0; i < nodesJ.Count; i++)
        {
            var n = nodesJ[i];
            if (n.TryGetProperty("matrix", out var mx))
            {
                // a matrix stands for a translation, rotation and scale; Blender writes none, so only the identity is taken
                var m = Numbers(mx, 16, $"{names[i]}'s matrix");
                for (int k = 0; k < 16; k++)
                    if (Math.Abs(m[k] - (k % 5 == 0 ? 1 : 0)) > 1e-6) throw new InvalidDataException($"node {names[i]} has a matrix: export translation, rotation and scale instead");
            }
            var t = n.TryGetProperty("translation", out var tt) ? Numbers(tt, 3, $"{names[i]}'s translation") : [0f, 0f, 0f];
            var q = n.TryGetProperty("rotation", out var qq) ? Numbers(qq, 4, $"{names[i]}'s rotation") : [0f, 0f, 0f, 1f];
            var s = n.TryGetProperty("scale", out var ss) ? Numbers(ss, 3, $"{names[i]}'s scale") : [1f, 1f, 1f];
            var rq = new Quaternion(q[0], q[1], q[2], q[3]);
            if (rq.LengthSquared() < 1e-12f) throw new InvalidDataException($"node {names[i]}'s rotation has no direction");
            nodes.Add(new GltfNode(names[i], parents[i], new Vector3(t[0], t[1], t[2]), Quaternion.Normalize(rq), new Vector3(s[0], s[1], s[2])));
        }

        var anims = new List<GltfAnimationClip>();
        int channelCount = 0;
        foreach (var (a, ai) in Array(r, "animations").Select((a, i) => (a, i)))
        {
            string? an = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var anm) && anm.ValueKind == JsonValueKind.String ? Shown(anm.GetString()!) : null;
            var samplers = Array(a, "samplers");
            var chans = new List<GltfChannel>();
            var seen = new HashSet<(int, string)>();
            foreach (var c in Array(a, "channels"))
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("target", out var tg) || tg.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"animation {ai}: a channel without a target");
                // a channel with no node (an extension's target) drives nothing an import reads
                if (!tg.TryGetProperty("node", out _)) continue;
                int node = Int(tg, "node", $"animation {ai}'s channel");
                if (node < 0 || node >= nodes.Count) throw new InvalidDataException($"animation {ai}: a channel targets node {node}, which doesn't exist");
                string path = tg.TryGetProperty("path", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() ?? "" : "";
                if (path == "weights") continue;   // a mesh's shape keys, not a bone's transform
                if (path is not ("rotation" or "translation" or "scale")) throw new InvalidDataException($"{names[node]}: a channel on '{Shown(path)}'");
                if (!seen.Add((node, path))) throw new InvalidDataException($"{names[node]} {path}: two channels in one animation");
                if (++channelCount > MaxChannels) throw new InvalidDataException($"more than {MaxChannels} animation channels, far more than a clip on the game's skeleton needs");
                int si = Int(c, "sampler", $"{names[node]} {path}");
                if (si < 0 || si >= samplers.Count) throw new InvalidDataException($"{names[node]} {path}: sampler {si} doesn't exist");
                var s = samplers[si];
                string ip = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("interpolation", out var ipe) && ipe.ValueKind == JsonValueKind.String ? ipe.GetString() ?? "" : "LINEAR";
                var interp = ip switch
                {
                    "LINEAR" => GltfInterpolation.Linear,
                    "STEP" => GltfInterpolation.Step,
                    "CUBICSPLINE" => GltfInterpolation.CubicSpline,
                    _ => throw new InvalidDataException($"{names[node]} {path}: interpolation '{Shown(ip)}'"),
                };
                int width = path == "rotation" ? 4 : 3;
                string what = $"{names[node]} {path}";
                var times = Floats(Int(s, "input", what), 1, what);
                if (times.Length > MaxKeys) throw new InvalidDataException($"{what}: {times.Length} keys");
                for (int i = 0; i < times.Length; i++)
                {
                    if (times[i] < 0) throw new InvalidDataException($"{what}: a key at {times[i]} s, before 0");
                    if (i > 0 && times[i] <= times[i - 1]) throw new InvalidDataException($"{what}: key times don't rise");
                }
                var v = Floats(Int(s, "output", what), width, what);
                int per = interp == GltfInterpolation.CubicSpline ? 3 : 1;
                if (v.Length != (long)times.Length * width * per) throw new InvalidDataException($"{what}: {v.Length / width} values for {times.Length} keys");
                chans.Add(new GltfChannel(node, path, interp, times, v));
            }
            anims.Add(new GltfAnimationClip(an, chans));
        }
        return new GlbScene { Nodes = nodes, Animations = anims, Generator = gen };
    }
}
