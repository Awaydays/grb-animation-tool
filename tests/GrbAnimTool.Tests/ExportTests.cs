using System.Numerics;
using System.Text.Json;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>The export on the game's own data: the byte and float readers' controls, the .glb's structure, the round trip through the
/// file against the decoder at every tick, the faulty files and spoiled expectations that must fail, and the sidecar.</summary>
[Collection("install")]
public sealed class ExportTests(InstallFixture f, ITestOutputHelper output)
{
    public const string Hello = "mca_emote_Hello";
    public static readonly string[] Three = [Hello, "mcm_nwp_fm_std_V1_cycle", "mil_gen_m_ale_ros_std_V0_N_death_regular_head_back_02"];

    /// <summary>The four helpers and the add-on bone each one's float names.</summary>
    static readonly (uint Helper, uint Target)[] Helpers = [(0x64F9A1CB, 0x447440F8), (0x6C8CB68C, 0x0F208B89), (0xB663DEC6, 0x8B9974FF), (0xF22AE62D, 0xFD962D0E)];

    ClipEntry Clip(string name) => f.Install.FindClip(name) ?? throw new InvalidOperationException($"{name} isn't in the install");

    Curve TrackCurve(ClipRecord c, uint hash, TrackKind kind) =>
        Enumerable.Range(0, c.Tracks.Count).Where(i => c.Tracks[i] == hash)
            .Select(i => SharedCurveTable.Resolve(c.Curves[i], f.Install.SharedTable)!).Single(u => u.Kind == kind);

    /// <summary>The hand-weight helpers in Greetings: the left's byte reads 125, then 0 from tick 12 to 102, then 125, as the left hand leaves
    /// the weapon and comes back; the right's stays 125. Each helper's float is the hash of its add-on target, a bone of the skeleton.</summary>
    [GameDataFact]
    public void HandWeightHelpersInGreetings()
    {
        var c = Clip(Hello).Record!;
        var left = TrackCurve(c, 0x6C8CB68C, TrackKind.Byte);
        Assert.Equal(13, left.Form);
        Assert.Equal(new[] { 0, 10, 12, 102, 104 }, CurveDecoder.KeyTicks(left));
        Assert.Equal(new byte[] { 125, 125, 0, 0, 125 }, ScalarDecoder.Bytes(left));
        var right = TrackCurve(c, 0x64F9A1CB, TrackKind.Byte);
        Assert.Equal(new byte[] { 125 }, ScalarDecoder.Bytes(right));
        foreach (var (helper, target) in Helpers)
        {
            var fl = TrackCurve(c, helper, TrackKind.Float);
            Assert.Equal(18, fl.Form);
            for (int k = 0; k < fl.Keys; k++) Assert.Equal(target, ScalarDecoder.KeyFloatBits(fl, k));
            Assert.True(f.Install.Skeleton.Contains(target), $"{target:X8} isn't a bone");
            Assert.True(f.Install.Skeleton.Bones[f.Install.Skeleton.IndexOf(target)].FromAddOn);
        }
    }

    /// <summary>Every byte (form 13) and float (form 18) curve of every clip reads all its keys, and the readers refuse every other form.
    /// The census: where the hand-weight helpers' bytes stand, and whether each helper's float names its own target in every clip.</summary>
    [GameDataFact]
    public void EveryByteAndFloatCurveReads()
    {
        var g = f.Install;
        long bytes = 0, floats = 0, raw = 0, helperFloats = 0, helperOther = 0;
        var helperBytes = new SortedDictionary<int, long>();
        var over = new List<string>();
        var targets = Helpers.ToDictionary(h => h.Helper, h => h.Target);
        foreach (var e in g.Clips)
        {
            var c = e.Record!;
            for (int i = 0; i < c.Curves.Count; i++)
            {
                var u = SharedCurveTable.Resolve(c.Curves[i], g.SharedTable)!;
                if (u.Form == 13)
                {
                    var v = ScalarDecoder.Bytes(u);
                    Assert.True(v is not null && v.Length == u.Keys, $"{e.Name} track {i}: byte keys not read");
                    bytes += v!.Length;
                    if (c.Tracks[i] is 0x64F9A1CB or 0x6C8CB68C)
                    {
                        foreach (var x in v) helperBytes[x] = helperBytes.GetValueOrDefault(x) + 1;
                        if (v.Any(x => x > 125) && over.Count < 12) over.Add($"{e.Name} {c.Tracks[i]:X8}");
                    }
                }
                else Assert.Null(ScalarDecoder.Bytes(u));
                if (u.Form == 18)
                {
                    var v = ScalarDecoder.Floats(u);
                    Assert.True(v is not null && v.Length == u.Keys, $"{e.Name} track {i}: float keys not read");
                    floats += v!.Length;
                    if (targets.TryGetValue(c.Tracks[i], out uint t))
                        for (int k = 0; k < u.Keys; k++) { if (ScalarDecoder.KeyFloatBits(u, k) == t) helperFloats++; else helperOther++; }
                }
                else Assert.Null(ScalarDecoder.Floats(u));
                if (u.Form is 14 or 15)
                    for (int k = 0; k < u.Keys; k++) { Assert.NotNull(ScalarDecoder.KeyRaw(u, k)); raw++; }
            }
        }
        output.WriteLine($"byte keys {bytes}, float keys {floats}, raw keys of forms 14 and 15 {raw}");
        output.WriteLine($"hand-weight helpers' bytes: {string.Join(", ", helperBytes.Select(x => $"{x.Key} x{x.Value}"))}");
        output.WriteLine($"helper floats naming their own target {helperFloats}, naming another {helperOther}");
        output.WriteLine($"clips with a hand-weight byte over 125 (first 12): {string.Join(", ", over)}");
        Assert.True(bytes > 40_000 && floats > 15_000 && raw > 0);
        // the hand weights read as a weight on the weapon: 125 (on) or 0 (off) above all, values between where a hand blends; a few keys
        // stand over 125 (their meaning isn't read), so the control is on the shape of the census, not on every key
        long total = helperBytes.Values.Sum(), inRange = helperBytes.Where(x => x.Key <= 125).Sum(x => x.Value);
        Assert.True(inRange >= 0.99 * total, $"{inRange} of {total} hand-weight keys within 0 to 125");
        Assert.Equal(new[] { 125, 0 }, helperBytes.OrderByDescending(x => x.Value).Take(2).Select(x => x.Key));
        // each helper's float names its own add-on target, in every clip
        Assert.Equal(0, helperOther);
    }

    [GameDataFact]
    public void SkeletonGlbStructure()
    {
        var s = f.Install.Skeleton;
        Assert.Equal(75, s.Bones.Count);
        var b = GlbWriter.Skeleton(s);
        var (json, bin) = Glb.Read(b);
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        var nodes = r.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(76, nodes.Count);
        Assert.Equal(GlbWriter.ArmatureNode, nodes[0].GetProperty("name").GetString());
        Assert.False(nodes[0].TryGetProperty("translation", out _));
        Assert.False(nodes[0].TryGetProperty("rotation", out _));
        var names = nodes.Select(n => n.GetProperty("name").GetString()).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        // the node tree is the skeleton's tree, under the armature
        int[] Kids(int node) => nodes[node].TryGetProperty("children", out var ch) ? ch.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];
        Assert.Equal(s.Bones.Where(x => x.IsRoot).Select(x => x.Index + 1), Kids(0));
        for (int i = 0; i < s.Bones.Count; i++)
        {
            Assert.Equal(s.Bones[i].Name, names[i + 1]);
            Assert.Equal(s.Bones.Where(x => x.ParentIndex == i).Select(x => x.Index + 1), Kids(i + 1));
        }
        Assert.Equal(54, s.Bones.Count(x => !x.Name.All(Uri.IsHexDigit) || x.Name.Length != 8));   // named bones; the rest stand as their hash
        var skin = r.GetProperty("skins")[0];
        Assert.Equal(0, skin.GetProperty("skeleton").GetInt32());
        Assert.Equal(Enumerable.Range(1, 75), skin.GetProperty("joints").EnumerateArray().Select(x => x.GetInt32()));
        var acc = r.GetProperty("accessors")[skin.GetProperty("inverseBindMatrices").GetInt32()];
        Assert.Equal(75, acc.GetProperty("count").GetInt32());
        Assert.Equal("MAT4", acc.GetProperty("type").GetString());
        Assert.Equal(5126, acc.GetProperty("componentType").GetInt32());
        Assert.Equal(bin.Length, r.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32());
        Assert.False(r.TryGetProperty("animations", out _));
        // each inverse bind matrix undoes its bone's rest place: applied to the bone's own origin in glTF's axes, it gives the origin
        var model = new Transform[75];
        s.ComputeModel(s.RestLocals(), model);
        var m = bin.ToArray();
        for (int i = 0; i < 75; i++)
        {
            var o = GltfAxes.ToGltf(model[i].Position);
            float F(int k) => BitConverter.ToSingle(m, 64 * i + 4 * k);
            var x = new Vector3(F(0) * o.X + F(4) * o.Y + F(8) * o.Z + F(12), F(1) * o.X + F(5) * o.Y + F(9) * o.Z + F(13), F(2) * o.X + F(6) * o.Y + F(10) * o.Z + F(14));
            Assert.True(x.Length() < 1e-5f, $"{s.Bones[i].Name}: {x}");
        }
        var back = RoundTrip.Compare(s, GltfAnimation.ReadGlb(b));
        Assert.True(back.Passed, back.ToString());
        output.WriteLine($"skeleton: {b.Length} B; read back {back}");
    }

    [GameDataFact]
    public void ClipGlbStructure()
    {
        var g = f.Install;
        var c = Clip(Hello).Record!;
        var sampled = SampledClip.Of(g.Skeleton, c, g.SharedTable);
        var b = GlbWriter.Clip(g.Skeleton, sampled, c.Name);
        var (json, _) = Glb.Read(b);
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        var anim = Assert.Single(r.GetProperty("animations").EnumerateArray());
        Assert.Equal(Hello, anim.GetProperty("name").GetString());
        var accs = r.GetProperty("accessors").EnumerateArray().ToList();
        var samplers = anim.GetProperty("samplers").EnumerateArray().ToList();
        var channels = anim.GetProperty("channels").EnumerateArray().ToList();
        Assert.Equal(samplers.Count, channels.Count);
        int expected = sampled.Rotations.Count(x => x is not null) + sampled.Positions.Count(x => x is not null)
            + (sampled.MoverRotation is null ? 0 : 1) + (sampled.MoverPosition is null ? 0 : 1);
        Assert.Equal(expected, channels.Count);
        foreach (var sm in samplers)
        {
            Assert.Equal("LINEAR", sm.GetProperty("interpolation").GetString());
            var input = accs[sm.GetProperty("input").GetInt32()];
            Assert.Equal(123, input.GetProperty("count").GetInt32());
            Assert.Equal(0f, input.GetProperty("min")[0].GetSingle());
            Assert.Equal(122 / 60f, input.GetProperty("max")[0].GetSingle());
            Assert.Equal(123, accs[sm.GetProperty("output").GetInt32()].GetProperty("count").GetInt32());
        }
        // the mover is on the armature's node; every other channel on a bone with a track
        var onArmature = channels.Where(x => x.GetProperty("target").GetProperty("node").GetInt32() == 0).Select(x => x.GetProperty("target").GetProperty("path").GetString()).ToList();
        Assert.Equal(new[] { "rotation", "translation" }, onArmature.Order());
        output.WriteLine($"{Hello}: {b.Length} B, {channels.Count} channels ({onArmature.Count} on the armature)");
    }

    RoundTripResult RoundTripOf(ClipEntry e, ExportFault fault = ExportFault.None, Spoil? spoil = null)
    {
        var g = f.Install;
        var c = e.Record!;
        var file = GltfAnimation.ReadGlb(GlbWriter.Clip(g.Skeleton, SampledClip.Of(g.Skeleton, c, g.SharedTable), c.Name, fault));
        return RoundTrip.Compare(g.Skeleton, file, c, g.SharedTable, spoil);
    }

    [GameDataFact]
    public void ThreeClipsRoundTrip()
    {
        foreach (var name in Three)
        {
            var r = RoundTripOf(Clip(name));
            output.WriteLine($"{name}: {r}");
            Assert.True(r.Passed, $"{name}: {r}");
        }
    }

    /// <summary>A sample of 200 clips spread over the datafile, every one that the viewer plays: each exported, read back and compared at
    /// every tick.</summary>
    [GameDataFact]
    public void SampleOf200RoundTrips()
    {
        var all = f.Install.Clips.Where(e => e.Record is { WalkedToEnd: true }).ToList();
        int step = all.Count / 200;
        var sample = Enumerable.Range(0, 200).Select(i => all[i * step]).ToList();
        double cm = 0, deg = 0;
        long ticks = 0;
        var failed = new List<string>();
        foreach (var e in sample)
        {
            var r = RoundTripOf(e);
            ticks += r.Ticks;
            if (!r.Passed) failed.Add($"{e.Name}: {r}");
            else { cm = Math.Max(cm, r.WorstCm); deg = Math.Max(deg, r.WorstDeg); }
        }
        output.WriteLine($"200 clips, {ticks} ticks: worst {cm:F5} cm, {deg:F5} deg; failed {failed.Count}");
        Assert.Empty(failed);
    }

    sealed class SlowGameDataFactAttribute : FactAttribute
    {
        public SlowGameDataFactAttribute() { if (!GameData.Available) Skip = GameData.SkipText; }
    }

    /// <summary>Every clip the viewer plays, exported, read back and compared at every tick. Slow: run it with --filter Category=Slow.</summary>
    [SlowGameDataFact, Trait("Category", "Slow")]
    public void EveryClipRoundTrips()
    {
        double cm = 0, deg = 0;
        long ticks = 0;
        int n = 0;
        string at = "";
        var failed = new List<string>();
        foreach (var e in f.Install.Clips.Where(e => e.Record is { WalkedToEnd: true }))
        {
            var r = RoundTripOf(e);
            n++;
            ticks += r.Ticks;
            if (!r.Passed) { if (failed.Count < 20) failed.Add($"{e.Name}: {r}"); continue; }
            if (r.WorstCm > cm) { cm = r.WorstCm; at = $"{e.Name} {r.WorstCmAt}"; }
            deg = Math.Max(deg, r.WorstDeg);
        }
        output.WriteLine($"{n} clips, {ticks} ticks: worst {cm:F5} cm ({at}), {deg:F5} deg; failed {failed.Count}");
        Assert.Empty(failed);
        Assert.Equal(f.Install.Clips.Count, n);
    }

    /// <summary>The controls that must fail: a skeleton file with the axes not turned, one with
    /// a thigh 2% long, an expected bind pose with LeftArm 0.2 deg out, and Greetings with Spine 0.5 deg out.</summary>
    [GameDataFact]
    public void CorruptedControlsFail()
    {
        var s = f.Install.Skeleton;
        var axes = RoundTrip.Compare(s, GltfAnimation.ReadGlb(GlbWriter.Skeleton(s, ExportFault.AxesNotTurned)));
        var thigh = RoundTrip.Compare(s, GltfAnimation.ReadGlb(GlbWriter.Skeleton(s, ExportFault.ThighLong)));
        var good = GltfAnimation.ReadGlb(GlbWriter.Skeleton(s));
        var arm = RoundTrip.Compare(s, good, spoil: new Spoil(Crc32.Of("LeftArm"), 0.2f));
        var spine = RoundTripOf(Clip(Hello), spoil: new Spoil(Crc32.Of("Spine"), 0.5f));
        var clipAxes = RoundTripOf(Clip(Hello), ExportFault.AxesNotTurned);
        foreach (var (what, r) in new[] { ("axes not turned", axes), ("a thigh 2% long", thigh), ("LeftArm 0.2 deg out", arm), ("Spine 0.5 deg out", spine), ("Greetings, axes not turned", clipAxes) })
        {
            output.WriteLine($"{what}: {r}");
            Assert.False(r.Passed, what);
        }
        Assert.InRange(thigh.WorstCm, 0.87, 0.88);    // 0.8738 cm at the knee
        Assert.InRange(axes.WorstCm, 137.5, 138.5);   // 138.0 cm, 179.2 deg
        Assert.InRange(axes.WorstDeg, 179, 179.5);
        Assert.InRange(arm.WorstDeg, 0.1999, 0.2001);
        Assert.InRange(spine.WorstDeg, 0.499, 0.502);
    }

    [GameDataFact]
    public void GreetingsSidecar()
    {
        var g = f.Install;
        var e = Clip(Hello);
        var c = e.Record!;
        var sampled = SampledClip.Of(g.Skeleton, c, g.SharedTable);
        var side = ClipSidecar.Of(c, g.SharedTable, g.Skeleton, g.Names, sampled, "mca_emote_Hello.glb", e.Index, e.Status);
        string json = side.ToJson();
        var back = ClipSidecar.FromJson(json);
        Assert.Equal(json, back.ToJson());
        Assert.Equal("0x000001A625068E05", back.Clip.Id);
        Assert.Equal(102, back.Clip.Index);
        Assert.Equal(122, back.Clip.Ticks);
        Assert.Equal(84, back.Tracks.Count);
        Assert.Equal("cf49a0204dd2a2bd84b19dd4fc94c5009b3d9dd36b982aada32c7395e9231d96", back.Clip.RecordSha256);
        // every track the .glb doesn't carry is in the sidecar, with its stored bytes
        var others = back.Tracks.Where(t => t.CarriedBy == "sidecar").Select(t => t.Index).ToList();
        Assert.Equal(others, back.OtherTracks.Select(t => t.Index));
        Assert.All(back.OtherTracks, t => Assert.NotNull(t.CurveHex));
        var left = back.OtherTracks.Single(t => t.Hash == "6C8CB68C" && t.Kind == "Byte");
        Assert.Equal(new[] { 125, 125, 0, 0, 125 }, left.Bytes!);
        Assert.Equal("0F208B89", back.OtherTracks.Single(t => t.Hash == "6C8CB68C" && t.Kind == "Float").FloatBits![0]);
        Assert.Equal(2, back.OtherTracks.Count(t => t.Plants is { Count: > 0 }));
        Assert.Contains(back.Events.Groups, gr => gr.Times120 is [30]);
        var carried = back.Tracks.GroupBy(t => t.CarriedBy).ToDictionary(x => x.Key, x => x.Count());
        output.WriteLine($"tracks carried: {string.Join(", ", carried.Select(x => $"{x.Key} {x.Value}"))}; sidecar {json.Length} B");
    }

    [GameDataFact]
    public void ExportWritesBothFiles()
    {
        var g = f.Install;
        string dir = Path.Combine(AppContext.BaseDirectory, "export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = ClipExport.Clip(g, Clip(Hello), Path.Combine(dir, ClipExport.FileNameFor(Hello)));
            Assert.True(r.Check.Passed, r.Check.ToString());
            Assert.True(File.Exists(r.GlbPath));
            Assert.Equal(Path.Combine(dir, "mca_emote_Hello.grbanim.json"), r.SidecarPath);
            var side = ClipSidecar.FromJson(File.ReadAllText(r.SidecarPath!));
            Assert.Equal("mca_emote_Hello.glb", side.Glb);
            var sk = ClipExport.Skeleton(g, Path.Combine(dir, ClipExport.SkeletonFileName));
            Assert.True(sk.Check.Passed, sk.Check.ToString());
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
