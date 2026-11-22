using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>The export's parts on records built here: the byte and float readers, the .glb container, the writer and reader, the
/// round-trip control and its faults, the sidecar. No game data.</summary>
public sealed class ExportUnitTests
{
    static Curve One(byte[] curve)
    {
        var cs = CurveWalker.Walk(curve, 0, 1, out _, out string stop);
        Assert.Equal(CurveWalker.AtEnd, stop);
        return cs[0];
    }

    [Fact]
    public void ByteKeysRead()
    {
        var c = One(Synthetic.Curve(0x27, [0, 10, 12, 102, 104], [[125], [125], [0], [0], [125]]));
        Assert.True(ScalarDecoder.IsByte(c));
        Assert.Equal(new byte[] { 125, 125, 0, 0, 125 }, ScalarDecoder.Bytes(c));
        Assert.Equal((byte)0, ScalarDecoder.KeyByte(c, 2));
        Assert.Null(ScalarDecoder.KeyByte(c, 5));
        Assert.Null(ScalarDecoder.KeyByte(c, -1));
        Assert.Null(ScalarDecoder.KeyFloat(c, 0));
        Assert.Equal(new[] { 0, 10, 12, 102, 104 }, CurveDecoder.KeyTicks(c));
    }

    [Fact]
    public void FloatKeysRead()
    {
        byte[] F(uint bits) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, bits); return b; }
        var c = One(Synthetic.Curve(0x36, [0, 122], [F(0x0F208B89), F(BitConverter.SingleToUInt32Bits(0.5f))]));
        Assert.True(ScalarDecoder.IsFloat(c));
        Assert.Equal(0x0F208B89u, ScalarDecoder.KeyFloatBits(c, 0));
        Assert.Equal(0.5f, ScalarDecoder.KeyFloat(c, 1));
        Assert.Equal(2, ScalarDecoder.Floats(c)!.Length);
        Assert.Null(ScalarDecoder.KeyFloat(c, 2));
        Assert.Null(ScalarDecoder.Bytes(c));
    }

    [Fact]
    public void RawKeysAndBounds()
    {
        var c = One(Synthetic.Curve(0x2A, [0, 5], [[1, 2], [3, 4]]));      // form 14: two bytes a key, no reader
        Assert.Null(ScalarDecoder.Bytes(c));
        Assert.Null(ScalarDecoder.Floats(c));
        Assert.Equal(new byte[] { 3, 4 }, ScalarDecoder.KeyRaw(c, 1));
        Assert.Null(ScalarDecoder.KeyRaw(c, 2));
        // a curve whose source is cut short of its values: every read is refused, none runs past the end
        var cut = c with { Source = c.Source[..^1] };
        Assert.Null(ScalarDecoder.KeyRaw(cut, 1));
        Assert.NotNull(ScalarDecoder.KeyRaw(cut, 0));
        var b = One(Synthetic.Curve(0x27, [0, 1, 2], [[7], [8], [9]]));
        Assert.Null(ScalarDecoder.Bytes(b with { Source = b.Source[..^1] }));
        Assert.Null(ScalarDecoder.KeyByte(b with { Source = b.Source[..^1] }, 2));
        // a reference has no values of its own
        var r = new Curve(0, Curve.ReferenceTag, 0, 0, 5, 5, 0, 0, [0xFE, 5, 0, 0, 0], 0, 0, 0);
        Assert.Null(ScalarDecoder.KeyByte(r, 0));
        Assert.Null(ScalarDecoder.KeyRaw(r, 0));
    }

    /// <summary>A small skeleton (a root, a spine, an arm and the thigh the fault lengthens) and a clip moving it with a mover.</summary>
    static (Skeleton S, ClipRecord C) Small()
    {
        uint root = Crc32.Of("Reference"), spine = Crc32.Of("Spine"), arm = Crc32.Of("LeftArm"), up = Crc32.Of("LeftUpLeg"), leg = Crc32.Of("LeftLeg");
        var s = Skeleton.Parse(Synthetic.Skeleton([
            (root, -1, new Vector3(0, 0, 0.96f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.5f)),
            (spine, 0, new Vector3(0.07f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f)),
            (arm, 1, new Vector3(0.3f, 0.15f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f)),
            (up, 0, new Vector3(0, 0.09f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 3.1f)),
            (leg, 3, new Vector3(0.44f, 0, 0), Quaternion.Identity),
        ]), BoneNames.Default());
        var c = ClipRecord.Parse(Synthetic.Clip("small", 0x77, 0.5f, [
            (0, Synthetic.PosCurve([0, 30], Vector3.Zero, new Vector3(0, 0.8f, 0))),
            (0, Synthetic.RotCurve([0, 30], Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f))),
            (spine, Synthetic.RotCurve([0, 12, 30], Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.3f))),
            (arm, Synthetic.RotCurve([0, 30], Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -2f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 2f))),
            (root, Synthetic.PosCurve([0, 15, 30], new Vector3(0, 0, 0.96f), new Vector3(0.01f, 0.02f, 0.9f), new Vector3(0, 0, 0.96f))),
            (0x6C8CB68C, Synthetic.Curve(0x27, [0, 10], [[125], [0]])),
        ]));
        Assert.True(c.WalkedToEnd, c.Stop);
        return (s, c);
    }

    [Fact]
    public void GlbContainerLayout()
    {
        var (s, _) = Small();
        var b = GlbWriter.Skeleton(s);
        Assert.Equal(Glb.Magic, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal((uint)b.Length, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        uint jl = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
        Assert.Equal(Glb.JsonChunk, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16)));
        Assert.Equal(0u, jl % 4);
        uint bl = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20 + (int)jl));
        Assert.Equal(Glb.BinChunk, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24 + (int)jl)));
        Assert.Equal(0u, bl % 4);
        Assert.Equal(b.Length, 28 + (int)jl + (int)bl);
        var (json, bin) = Glb.Read(b);
        Assert.Equal((int)jl, json.Length);                  // ASCII JSON, padded with spaces
        Assert.True(b.AsSpan(20, (int)jl).ToArray().All(x => x is >= 0x20 and < 0x7F));
        Assert.Equal(16 * 4 * s.Bones.Count, bin.Length);
        using var d = JsonDocument.Parse(json);
        Assert.Equal(s.Bones.Count + 1, d.RootElement.GetProperty("nodes").GetArrayLength());
    }

    [Fact]
    public void GlbReaderRefusesDamage()
    {
        var (s, c) = Small();
        var b = GlbWriter.Clip(s, SampledClip.Of(s, c, null), "small");
        Assert.Throws<InvalidDataException>(() => GltfAnimation.ReadGlb(b[..^4]));
        Assert.Throws<InvalidDataException>(() => GltfAnimation.ReadGlb(b[..10]));
        var bad = (byte[])b.Clone(); bad[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => GltfAnimation.ReadGlb(bad));
        // every single byte flipped in turn: the reader throws InvalidDataException or reads, never anything else
        var rnd = new Random(3);
        for (int i = 0; i < 400; i++)
        {
            var x = (byte[])b.Clone();
            int at = rnd.Next(x.Length);
            x[at] = (byte)rnd.Next(256);
            try { GltfAnimation.ReadGlb(x); }
            catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void SmallClipRoundTrips()
    {
        var (s, c) = Small();
        var sampled = SampledClip.Of(s, c, null);
        Assert.Equal(30, sampled.LastTick);
        Assert.NotNull(sampled.MoverPosition);
        Assert.NotNull(sampled.MoverRotation);
        var f = GltfAnimation.ReadGlb(GlbWriter.Clip(s, sampled, "small"));
        Assert.Equal(GlbWriter.ArmatureNode, f.ArmatureName);
        Assert.Equal(Enumerable.Range(0, 31), f.Ticks);
        Assert.Equal("small", f.AnimationName);
        var r = RoundTrip.Compare(s, f, c, null);
        Assert.True(r.Passed, r.ToString());
        Assert.True(r.WorstCm < 1e-3 && r.WorstDeg < 1e-3, r.ToString());
        // the mover travels 0.8 m: a file read without it would be far off
        Assert.Equal(0.8f, f.MoverPosition![30].Y, 4);
    }

    /// <summary>A clip with no track on the skeleton (as a face clip) still exports its length: one channel holds the root at rest.</summary>
    [Fact]
    public void ClipWithNoBoneTrack()
    {
        var (s, _) = Small();
        var c = ClipRecord.Parse(Synthetic.Clip("face", 0x78, 0.25f, [(0x12345678, Synthetic.RotCurve([0, 15], Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1)))]));
        var f = GltfAnimation.ReadGlb(GlbWriter.Clip(s, SampledClip.Of(s, c, null), "face"));
        Assert.Equal(1, f.Channels);
        Assert.Equal(Enumerable.Range(0, 16), f.Ticks);
        Assert.True(RoundTrip.Compare(s, f, c, null).Passed);
    }

    [Fact]
    public void FaultsFail()
    {
        var (s, c) = Small();
        var sampled = SampledClip.Of(s, c, null);
        var axes = RoundTrip.Compare(s, GltfAnimation.ReadGlb(GlbWriter.Clip(s, sampled, "small", ExportFault.AxesNotTurned)), c, null);
        Assert.False(axes.Passed, axes.ToString());
        var thigh = RoundTrip.Compare(s, GltfAnimation.ReadGlb(GlbWriter.Skeleton(s, ExportFault.ThighLong)));
        Assert.False(thigh.Passed, thigh.ToString());
        Assert.InRange(thigh.WorstCm, 0.87, 0.89);            // 2% of the 44 cm thigh, at the knee
        var ok = GltfAnimation.ReadGlb(GlbWriter.Skeleton(s));
        Assert.True(RoundTrip.Compare(s, ok).Passed);
        var arm = RoundTrip.Compare(s, ok, spoil: new Spoil(Crc32.Of("LeftArm"), 0.2f));
        Assert.False(arm.Passed, arm.ToString());
        Assert.InRange(arm.WorstDeg, 0.199, 0.201);
        var spine = RoundTrip.Compare(s, GltfAnimation.ReadGlb(GlbWriter.Clip(s, sampled, "small")), c, null, new Spoil(Crc32.Of("Spine"), 0.5f));
        Assert.False(spine.Passed, spine.ToString());
        Assert.InRange(spine.WorstDeg, 0.499, 0.501);
        // a file missing a bone, or with another parent for one, is refused before any number
        var other = Skeleton.Parse(Synthetic.Skeleton([(Crc32.Of("Reference"), -1, Vector3.Zero, Quaternion.Identity)]), BoneNames.Default());
        Assert.NotEmpty(RoundTrip.Compare(s, GltfAnimation.ReadGlb(GlbWriter.Skeleton(other))).Problems);
    }

    /// <summary>A .gltf whose buffer length is negative, or whose buffer file is missing or isn't a file, is refused as damaged.</summary>
    [Fact]
    public void GltfReaderRefusesABadBuffer()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "gltf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "b.bin"), [1, 2, 3, 4]);
            string Gltf(string buffer)
            {
                string p = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".gltf");
                File.WriteAllText(p, "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[" + buffer + "]}");
                return p;
            }
            Assert.Throws<InvalidDataException>(() => GltfAnimation.Read(Gltf("{\"byteLength\":-1,\"uri\":\"b.bin\"}")));
            Assert.Throws<InvalidDataException>(() => GltfAnimation.Read(Gltf("{\"byteLength\":4,\"uri\":\"missing.bin\"}")));
            Assert.Throws<InvalidDataException>(() => GltfAnimation.Read(Gltf("{\"byteLength\":4,\"uri\":\"\"}")));
            // the same file with a buffer that reads gets past its buffers
            Assert.Equal("no nodes", Assert.Throws<InvalidDataException>(() => GltfAnimation.Read(Gltf("{\"byteLength\":4,\"uri\":\"b.bin\"}"))).Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A skeleton whose rest rotation is all zeros has no direction to write: the writer refuses it as an export error.</summary>
    [Fact]
    public void AZeroRestRotationIsRefused()
    {
        var s = Skeleton.Parse(Synthetic.Skeleton([(Crc32.Of("Reference"), -1, Vector3.Zero, new Quaternion(0, 0, 0, 0))]), BoneNames.Default());
        Assert.Throws<ExportException>(() => GlbWriter.Skeleton(s));
    }

    [Fact]
    public void SidecarRoundTrips()
    {
        var (s, c) = Small();
        var sampled = SampledClip.Of(s, c, null);
        var side = ClipSidecar.Of(c, null, s, BoneNames.Default(), sampled, "small.glb", 7, ClipStatus.Added);
        string json = side.ToJson();
        var back = ClipSidecar.FromJson(json);
        Assert.Equal(json, back.ToJson());
        Assert.Equal("0x0000000000000077", back.Clip.Id);
        Assert.Equal(30, back.Clip.Ticks);
        Assert.Equal(6, back.Tracks.Count);
        Assert.Equal(new[] { "armature", "armature", "bone", "bone", "bone", "sidecar" }, back.Tracks.Select(t => t.CarriedBy));
        var helper = Assert.Single(back.OtherTracks);
        Assert.Equal("6C8CB68C", helper.Hash);
        Assert.Equal(new[] { 125, 0 }, helper.Bytes!);
        Assert.Equal(new[] { 0, 10 }, helper.Ticks);
        Assert.NotNull(helper.CurveHex);
        Assert.Throws<InvalidDataException>(() => ClipSidecar.FromJson(json.Replace("\"formatVersion\": 1", "\"formatVersion\": 99")));
        Assert.Throws<InvalidDataException>(() => ClipSidecar.FromJson("{\"format\": \"something else\"}"));
        Assert.Throws<InvalidDataException>(() => ClipSidecar.FromJson("{"));
    }

    /// <summary>The sidecar's format, field names included, against a fixed file: renaming a property or changing what a field holds
    /// fails here. On a difference the file written is left beside the test as <c>golden.actual.grbanim.json</c>.</summary>
    [Fact]
    public void SidecarMatchesTheGoldenFile()
    {
        var (s, _) = Small();
        byte[] F(float v) => BitConverter.GetBytes(v);
        // a track of each kind the sidecar holds values for: carried bones and mover, then a byte, a float, a raw form, and a rotation and
        // a position on a hash that names no skeleton bone
        var c = ClipRecord.Parse(Synthetic.Clip("golden", 0x79, 0.25f, [
            (0, Synthetic.PosCurve([0, 15], Vector3.Zero, new Vector3(0, 0.4f, 0))),
            (0, Synthetic.RotCurve([0, 15], Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f))),
            (Crc32.Of("Spine"), Synthetic.RotCurve([0, 15], Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.25f))),
            (0x6C8CB68C, Synthetic.Curve(0x27, [0, 10], [[125], [0]])),
            (0x0F208B89, Synthetic.Curve(0x36, [0, 15], [F(0.5f), F(-2f)])),
            (0x01020304, Synthetic.Curve(0x2A, [0, 5], [[1, 2], [3, 4]])),
            (0x0A0B0C0D, Synthetic.RotCurve([0, 15], Quaternion.Identity, Quaternion.Identity)),
            (0x0A0B0C0E, Synthetic.PosCurve([0], new Vector3(0.25f, 0.5f, 1))),
        ]));
        Assert.True(c.WalkedToEnd, c.Stop);
        var side = ClipSidecar.Of(c, null, s, BoneNames.Default(), SampledClip.Of(s, c, null), "golden.glb", 7, ClipStatus.Added)
            with { ToolVersion = "golden" };
        string actual = side.ToJson().ReplaceLineEndings("\n");
        string golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "golden.grbanim.json")).ReplaceLineEndings("\n").TrimEnd('\n');
        if (actual != golden) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "golden.actual.grbanim.json"), actual);
        Assert.Equal(golden, actual);
        // the fixed file reads, and writes back the same
        Assert.Equal(golden, ClipSidecar.FromJson(golden).ToJson().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ToolVersionIsThreeNumbers() => Assert.Matches(@"^\d+\.\d+\.\d+$", ToolInfo.Version);

    [Fact]
    public void ExportPlaces()
    {
        Assert.Equal("a_b.glb", ClipExport.FileNameFor("a|b"));
        Assert.Equal("clip.glb", ClipExport.FileNameFor(" "));
        string dir = Path.Combine(Path.GetTempPath(), "grbanim-guard");
        Assert.EndsWith(Path.Combine("grbanim-guard", "x.grbanim.json"), ClipExport.SidecarPathFor(Path.Combine(dir, "x.glb")));
        Assert.True(ClipExport.Refused(Path.Combine(dir, "Game", "x.glb"), [Path.Combine(dir, "game")]));
        Assert.True(ClipExport.Refused(Path.Combine(dir, "Game", "sub", "x.glb"), [Path.Combine(dir, "Game") + Path.DirectorySeparatorChar]));
        Assert.False(ClipExport.Refused(Path.Combine(dir, "Game2", "x.glb"), [Path.Combine(dir, "Game")]));
        Assert.False(ClipExport.Refused(Path.Combine(dir, "out", "x.glb"), [Path.Combine(dir, "Game"), ""]));
    }

    [Fact]
    public void ViewerExportOptions()
    {
        var a = GrbAnimTool.Core.Library.ViewerArgs.Parse(["--game", "g", "--export-clip", "mca_emote_Hello", "--export-skeleton", "--export-out", "out"]);
        Assert.True(a.Exports && a.Unattended && a.Automated && a.ExportSkeleton);
        Assert.False(a.TakesShots);
        Assert.Equal("mca_emote_Hello", a.ExportClip);
        Assert.Equal("out", a.ExportOut);
        Assert.False(GrbAnimTool.Core.Library.ViewerArgs.Parse(["--game", "g"]).Unattended);
        foreach (var bad in new[] { new[] { "--export-clip", "x", "--export-out", "o" }, ["--game", "g", "--export-clip", "x"], ["--game", "g", "--export-out", "o"], ["--game", "g", "--export-clip"] })
            Assert.Throws<ArgumentException>(() => GrbAnimTool.Core.Library.ViewerArgs.Parse(bad));
    }

    [Fact]
    public void AxesHandOver()
    {
        var p = new Vector3(1, 2, 3);
        Assert.Equal(new Vector3(1, 3, -2), GltfAxes.ToGltf(p));
        Assert.Equal(p, GltfAxes.FromGltf(GltfAxes.ToGltf(p)));
        // the hand-over is a rotation: it commutes with composing transforms
        var a = new Transform(new Vector3(0.1f, 0.2f, 0.3f), Quaternion.Normalize(new Quaternion(0.1f, 0.5f, -0.2f, 0.8f)));
        var b = new Transform(new Vector3(-0.4f, 0.0f, 0.7f), Quaternion.Normalize(new Quaternion(-0.3f, 0.1f, 0.6f, 0.7f)));
        var left = GltfAxes.ToGltf(a * b);
        var right = GltfAxes.ToGltf(a) * GltfAxes.ToGltf(b);
        Assert.True(Vector3.Distance(left.Position, right.Position) < 1e-6f);
        Assert.True(GltfAxes.AngleDeg(left.Rotation, right.Rotation) < 1e-4);
        Assert.Equal(0, GltfAxes.AngleDeg(a.Rotation, Quaternion.Negate(a.Rotation)), 6);
        Assert.Equal(90, GltfAxes.AngleDeg(Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)), 4);
    }
}
