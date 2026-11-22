using System.Buffers.Binary;
using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.IO;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>Tests with no game data: hashes, transforms and forward kinematics, the decoders, the walker and the bounds checks.</summary>
public sealed class UnitTests
{
    /// <summary>The angle between two rotations from the vector part of their difference, in doubles: 2 acos of a float dot can't see below
    /// about 0.05 deg.</summary>
    static double Deg(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Conjugate(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
        double v = Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y + (double)d.Z * d.Z);
        return 2 * Math.Atan2(v, Math.Abs((double)d.W)) * 180 / Math.PI;
    }
    static Quaternion AboutZ(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg * MathF.PI / 180);

    [Theory]
    [InlineData("Reference", 0x2C52CBB0u)]
    [InlineData("Hips", 0xDED10611u)]
    [InlineData("LeftHand", 0xB675F36Cu)]
    [InlineData("RightToeBase", 0x42BE0FCBu)]
    [InlineData("Animation", 0x0FA3067Fu)]
    [InlineData("Skeleton", 0x24AECB7Cu)]
    public void Crc32OfNames(string name, uint hash)
    {
        Assert.Equal(hash, Crc32.Of(name));
        Assert.Equal(hash, Crc32.Of(System.Text.Encoding.ASCII.GetBytes(name)));
    }

    [Fact]
    public void TheBuiltInNamesCoverTheBodysBones()
    {
        var n = BoneNames.Default();
        foreach (var (h, name) in new[] { (0x2C52CBB0u, "Reference"), (0x530EC1CBu, "Spine"), (0x07C159A2u, "Head"), (0xF4A6EDBCu, "LeftHandPinky1"), (0x9B14362Cu, "RightFoot") })
            Assert.Equal(name, n.NameOf(h));
        Assert.Equal("(mover)", n.NameOf(0));
        Assert.Equal("C27A38FD", n.Display(0xC27A38FD));
    }

    [Fact]
    public void Sha256OfBytes() =>
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Sha256Hex.Of("abc"u8));

    [Fact]
    public void TransformsCompose()
    {
        var parent = new Transform(new Vector3(0, 0, 1), AboutZ(90));
        var child = new Transform(new Vector3(1, 0, 0), AboutZ(90));
        var m = parent * child;
        Assert.True(Vector3.Distance(m.Position, new Vector3(0, 1, 1)) < 1e-6f);
        Assert.True(Deg(m.Rotation, AboutZ(180)) < 1e-3f);
        Assert.True(Vector3.Distance(parent.Apply(Vector3.UnitX), m.Position) < 1e-6f);
        var same = Transform.Identity * child;
        Assert.True(Vector3.Distance(same.Position, child.Position) < 1e-7f && Deg(same.Rotation, child.Rotation) < 1e-4);
    }

    [Fact]
    public void ForwardKinematicsOnAChain()
    {
        var rec = Synthetic.Skeleton([
            (0xA, -1, new Vector3(0, 0, 1), AboutZ(90)),
            (0xB, 0, new Vector3(1, 0, 0), Quaternion.Identity),
            (0xC, 1, new Vector3(1, 0, 0), Quaternion.Identity),
            (0xD, -1, new Vector3(5, 0, 0), Quaternion.Identity),
        ]);
        var s = Skeleton.Parse(rec);
        Assert.Equal(4, s.Bones.Count);
        Assert.Equal(-1, s.Bones[s.IndexOf(0xA)].ParentIndex);
        Assert.Equal(s.IndexOf(0xB), s.Bones[s.IndexOf(0xC)].ParentIndex);
        var model = s.Fk(new Dictionary<uint, Quaternion>(), new Dictionary<uint, Vector3>());
        Assert.True(Vector3.Distance(model[s.IndexOf(0xC)].Position, new Vector3(0, 2, 1)) < 1e-6f);
        Assert.True(Vector3.Distance(model[s.IndexOf(0xD)].Position, new Vector3(5, 0, 0)) < 1e-6f);
        // bend the middle bone 90 degrees about z: the tip turns from +y to -x
        model = s.Fk(new Dictionary<uint, Quaternion> { [0xB] = AboutZ(90) }, new Dictionary<uint, Vector3>());
        Assert.True(Vector3.Distance(model[s.IndexOf(0xC)].Position, new Vector3(-1, 1, 1)) < 1e-6f);

        // an add-on whose bone hangs from the body's 0xB by hash; the body's own bones keep the body's rest
        var add = Skeleton.Parse(Synthetic.Skeleton([(0xB, -1, new Vector3(9, 9, 9), Quaternion.Identity), (0xE, 0, new Vector3(0, 0, 1), Quaternion.Identity)]), addOn: true);
        var m = Skeleton.Merge(s, add);
        Assert.Equal(5, m.Bones.Count);
        Assert.Equal(m.IndexOf(0xB), m.Bones[m.IndexOf(0xE)].ParentIndex);
        Assert.True(m.Bones[m.IndexOf(0xE)].FromAddOn);
        var mm = m.Fk(new Dictionary<uint, Quaternion>(), new Dictionary<uint, Vector3>());
        Assert.True(Vector3.Distance(mm[m.IndexOf(0xE)].Position, new Vector3(0, 1, 2)) < 1e-6f);
    }

    [Fact]
    public void SixByteRotationsDecode()
    {
        var rnd = new Random(3);
        for (int i = 0; i < 2000; i++)
        {
            var q = Quaternion.Normalize(new Quaternion((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1));
            var c = Synthetic.RotCurve([0], q);
            var cv = CurveWalker.Walk(c, 0, 1, out _, out string stop).Single();
            Assert.Equal(CurveWalker.AtEnd, stop);
            Assert.True(CurveDecoder.TryKeyRot(cv, 0, out var d, out float stored));
            Assert.True(stored <= 0.75f + 1e-4f);
            Assert.True(Deg(q, d) < 0.01f, $"{q} -> {d}");
        }
    }

    [Fact]
    public void OneValueAboutZ()
    {
        // tag 0x15: a u16, 15 bits of z (or w, when the top bit is set); the other component rebuilt positive
        var c = new byte[] { 0x15, 4, 0, 0, 0, 1, 0, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(9), (ushort)MathF.Round((0.5f + 0.70710677f) / 4.3161006e-05f));
        var cv = CurveWalker.Walk(c, 0, 1, out _, out string stop).Single();
        Assert.Equal(CurveWalker.AtEnd, stop);
        var q = CurveDecoder.KeyRot(cv)!.Value;
        Assert.Equal(0, q.X); Assert.Equal(0, q.Y);
        Assert.Equal(0.5f, q.Z, 3);
        Assert.Equal(MathF.Sqrt(0.75f), q.W, 3);
    }

    [Fact]
    public void ASyntheticClipParsesAndSamples()
    {
        var b = Synthetic.Clip("test_clip", 0x1234, 10 / 60f, [
            (0, Synthetic.PosCurve([0, 10], Vector3.Zero, new Vector3(1, 0, 0))),
            (Crc32.Of("Reference"), Synthetic.RotCurve([0, 10], Quaternion.Identity, AboutZ(90))),
            (Crc32.Of("Reference"), Synthetic.PosCurve([0], new Vector3(0, 0, 0.96f))),
        ]);
        var c = ClipRecord.Parse(b);
        Assert.Equal(CurveWalker.AtEnd, c.Stop);
        Assert.Equal("test_clip", c.Name);
        Assert.Equal(0x1234ul, c.Id);
        Assert.Equal(10, c.LastTick);
        Assert.Equal([0u, Crc32.Of("Reference"), Crc32.Of("Reference")], c.Tracks);
        Assert.Equal(10, CurveDecoder.KeyTick(c.Curves[1], 1));

        var s = new ClipSampler(c, null);
        s.At(5 / 60f);
        Assert.True(Deg(s.Rotations[1], AboutZ(45)) < 0.05f);
        Assert.True(Vector3.Distance(s.Positions[0], new Vector3(0.5f, 0, 0)) < 1e-5f);
        s.At(100);   // past the end: the last key holds
        Assert.True(Deg(s.Rotations[1], AboutZ(90)) < 0.05f);

        var skel = Skeleton.Parse(Synthetic.Skeleton([(Crc32.Of("Reference"), -1, Vector3.Zero, Quaternion.Identity), (Crc32.Of("Hips"), 0, new Vector3(1, 0, 0), Quaternion.Identity)]));
        var ps = new PoseSampler(skel, c, new GrbAnimTool.Core.Clips.SharedCurveTable());
        Assert.True(ps.HasMover);
        var pose = ps.Sample(10 / 60f);
        int hips = skel.IndexOf(Crc32.Of("Hips"));
        Assert.True(Vector3.Distance(pose.Model[hips].Position, new Vector3(0, 1, 0.96f)) < 1e-4f);    // Reference turned 90 degrees about z
        Assert.True(Vector3.Distance(pose.World(hips).Position, new Vector3(1, 1, 0.96f)) < 1e-4f);    // and the mover 1 m along x
        Assert.True(Vector3.Distance(pose.Mover.Position, Vector3.UnitX) < 1e-5f);
    }

    [Fact]
    public void TruncatedSyntheticClipsStopCleanly()
    {
        var b = Synthetic.Clip("t", 1, 1, [
            (Crc32.Of("Hips"), Synthetic.RotCurve([0, 5, 9], Quaternion.Identity, AboutZ(10), AboutZ(20))),
            (Crc32.Of("Spine"), Synthetic.PosCurve([0, 3], Vector3.One, Vector3.UnitX)),
        ]);
        var whole = ClipRecord.Parse(b);
        Assert.True(whole.WalkedToEnd);
        int fc = whole.FirstCurve, walkerStops = 0;
        for (int n = 0; n < b.Length; n++)
        {
            var x = b[..n];
            // inside the curves, the wrapper's length field is set to the cut, so the cut meets the curve walker and not the length check
            if (n >= fc) BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(fc - 8), (uint)(n - (fc - 4)));
            try
            {
                var c = ClipRecord.Parse(x);
                Assert.False(c.WalkedToEnd);
                if (n >= fc) { Assert.DoesNotContain("length field", c.Stop); walkerStops++; }
            }
            catch (InvalidDataException) { Assert.True(n < fc, $"cut at {n} of {b.Length} threw"); }
        }
        Assert.Equal(b.Length - fc, walkerStops);
        for (int i = 0; i < b.Length; i++)
            for (int bit = 0; bit < 8; bit++)
            {
                var x = (byte[])b.Clone();
                x[i] ^= (byte)(1 << bit);
                try { var c = ClipRecord.Parse(x); new ClipSampler(c, null).At(0.1f); }
                catch (InvalidDataException) { }
            }
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.MaxValue)]
    [InlineData(-1f)]
    [InlineData(0f)]
    [InlineData(600.1f)]
    [InlineData(1e9f)]
    public void ADurationOutOfRangeIsRefused(float seconds)
    {
        var c = ClipRecord.Parse(Synthetic.Clip("d", 1, seconds, [(Crc32.Of("Hips"), Synthetic.RotCurve([0], Quaternion.Identity))]));
        Assert.False(c.WalkedToEnd);
        Assert.False(c.DurationValid);
        Assert.Contains("duration", c.Stop);
        Assert.Equal(0, c.LastTick);
    }

    [Fact]
    public void TheLongestDurationAllowedWalks()
    {
        var c = ClipRecord.Parse(Synthetic.Clip("d", 1, ClipRecord.MaxDuration, [(Crc32.Of("Hips"), Synthetic.RotCurve([0], Quaternion.Identity))]));
        Assert.True(c.WalkedToEnd);
        Assert.Equal(36_000, c.LastTick);
    }

    [Fact]
    public void CurveAndTrackCountsMustMatch()
    {
        var one = Synthetic.RotCurve([0], Quaternion.Identity);
        var c = ClipRecord.Parse(Synthetic.Clip("n", 1, 1, [(Crc32.Of("Hips"), one)], extraCurves: [one]));
        Assert.False(c.WalkedToEnd);
        Assert.Equal("2 curves for 1 tracks", c.Stop);
    }

    [Fact]
    public void BitmapKeyTimesAgreeWhenBitZeroIsClear()
    {
        // tag 0x0E: 6-byte rotations with a bitmap of times; one bitmap byte, bits 1, 2 and 5 set, so the first key is at tick 1, not 0
        var w = new List<byte> { 0x0E };
        w.AddRange(BitConverter.GetBytes(3u + 3 * 6)); w.AddRange(BitConverter.GetBytes(3u));
        w.Add(0x01); w.Add(0b0010_0110);
        for (int i = 0; i < 3; i++) w.AddRange(Synthetic.Encode6(AboutZ(10 * i)));
        var cv = CurveWalker.Walk([.. w], 0, 1, out _, out string stop).Single();
        Assert.Equal(CurveWalker.AtEnd, stop);
        var all = CurveDecoder.KeyTicks(cv);
        Assert.Equal([1, 2, 5], all);
        for (int k = 0; k < 3; k++) Assert.Equal(all[k], CurveDecoder.KeyTick(cv, k));
        // and in the usual case, bit 0 set: the first key at tick 0
        w[10] = 0b0010_0101;
        cv = CurveWalker.Walk([.. w], 0, 1, out _, out _).Single();
        Assert.Equal([0, 2, 5], CurveDecoder.KeyTicks(cv));
        Assert.Equal(0, CurveDecoder.KeyTick(cv, 0));
    }

    [Fact]
    public void ALongParentChainParsesWithoutRecursion()
    {
        // 100,000 bones, each the child of the next one in the record: one chain from the last bone (the root) down to the first
        const int n = 100_000;
        var bones = Enumerable.Range(0, n).Select(i => ((uint)i + 1, i + 1 < n ? i + 1 : -1, new Vector3(0, 0, 0.01f), Quaternion.Identity)).ToArray();
        var s = Within.Seconds(60, () => Skeleton.Parse(Synthetic.Skeleton(bones)));
        Assert.Equal(n, s.Bones.Count);
        Assert.Equal(-1, s.Bones[0].ParentIndex);
        Assert.Equal((uint)n, s.Bones[0].Hash);
        for (int i = 1; i < n; i++) Assert.Equal(i - 1, s.Bones[i].ParentIndex);
        // a parent loop: each bone is placed once, parents first, the loop broken where it closes
        var loop = Skeleton.Parse(Synthetic.Skeleton([(1, 1, Vector3.Zero, Quaternion.Identity), (2, 0, Vector3.Zero, Quaternion.Identity), (3, 1, Vector3.Zero, Quaternion.Identity)]));
        Assert.Equal(3, loop.Bones.Count);
        Assert.All(loop.Bones, b => Assert.True(b.ParentIndex < b.Index));
        Assert.Single(loop.Bones, b => b.IsRoot);
    }

    [Fact]
    public void TheWalkerNeverReadsPastItsInput()
    {
        // a bitmap-form tag as the last byte, a reference cut short, a size that doesn't fit, a huge key count
        Assert.NotEqual(CurveWalker.AtEnd, Stop([0x0E]));
        Assert.NotEqual(CurveWalker.AtEnd, Stop([0x0E, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.NotEqual(CurveWalker.AtEnd, Stop([0xFE, 1, 0]));
        Assert.NotEqual(CurveWalker.AtEnd, Stop([0x0C, 99, 0, 0, 0, 1, 0, 0, 0, 1, 2, 3, 4, 5, 6]));
        Assert.NotEqual(CurveWalker.AtEnd, Stop([0x0C, 8, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3, 4, 5, 6]));
        Assert.NotEqual(CurveWalker.AtEnd, Stop([0x99, 8, 0, 0, 0, 1, 0, 0, 0]));
        Assert.Equal(CurveWalker.AtEnd, Stop([0x0C, 8, 0, 0, 0, 1, 0, 0, 0, 1, 2, 3, 4, 5, 6]));

        static string Stop(byte[] b) { CurveWalker.Walk(b, 0, 1, out _, out string s); return s; }
    }

    [Fact]
    public void DecodersRefuseKeysOutsideTheCurve()
    {
        var b = Synthetic.RotCurve([0, 3], Quaternion.Identity, AboutZ(5));
        var cv = CurveWalker.Walk(b, 0, 1, out _, out _).Single();
        Assert.NotNull(CurveDecoder.KeyRot(cv, 1));
        Assert.Null(CurveDecoder.KeyRot(cv, 2));
        Assert.Null(CurveDecoder.KeyRot(cv, -1));
        Assert.Null(CurveDecoder.KeyPos(cv, 0));
        Assert.Null(CurveDecoder.KeyRot(cv with { ValuesAt = b.Length - 3 }, 0));
        Assert.Equal(0, CurveDecoder.KeyTick(cv, 5));
    }

    [Fact]
    public void BoundsChecks()
    {
        byte[] b = [1, 2, 3, 4];
        Assert.Equal(0x04030201u, Bin.U32(b, 0, "x"));
        Assert.Throws<InvalidDataException>(() => Bin.U32(b, 1, "x"));
        Assert.Throws<InvalidDataException>(() => Bin.U8(b, -1, "x"));
        Assert.Throws<InvalidDataException>(() => Bin.U64(b, 0, "x"));
        Assert.Throws<InvalidDataException>(() => Bin.Need(b, 2, long.MaxValue, "x"));
        Assert.Throws<InvalidDataException>(() => ClipRecord.Parse([]));
        Assert.Throws<InvalidDataException>(() => ClipRecord.Parse([0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F]));
    }

    [Fact]
    public void AStoredDatafileDecodesWithoutOodle()
    {
        var r1 = Synthetic.Record(0x11111111, "first", 0xAA, [1, 2, 3]);
        var r2 = Synthetic.Record(0x22222222, "second", 0xBB, new byte[70_000]);   // more than one 0x8000 chunk
        var p = Synthetic.StoredDatafile((0xAA, r1), (0xBB, r2));
        var d = DatafileCodec.Decode(p, null, out int table, out int trailer);
        Assert.Equal(p.Length, trailer);
        var recs = DatafileCodec.Parse(d, table);
        Assert.Equal(2, recs.Count);
        Assert.Equal("second", recs[1].Name);
        Assert.Equal(1, recs[1].Index);
        Assert.True(recs[1].Bytes.Span.SequenceEqual(r2));
        Assert.False(recs[0].Opaque);
        // the table's ID no longer matching the body's is refused, and so is a payload cut short
        var bad = Synthetic.StoredDatafile((0xAC, r1));
        Assert.Throws<InvalidDataException>(() => { var x = DatafileCodec.Decode(bad, null, out int t, out _); DatafileCodec.Parse(x, t); });
        Assert.Throws<InvalidDataException>(() => DatafileCodec.Decode(p[..(p.Length - 1)], null, out _, out _));
        Assert.Throws<InvalidDataException>(() => DatafileCodec.Decode(p[..10], null, out _, out _));
    }

    [Fact]
    public void ClipHashTablesRoundTrip()
    {
        var ms = new MemoryStream();
        var h = System.Security.Cryptography.SHA256.HashData("clip"u8);
        VanillaClipHashes.Write(ms, [(5, h), (3, new byte[32])]);
        ms.Position = 0;
        var t = VanillaClipHashes.Read(ms);
        Assert.Equal(2, t.Count);
        Assert.Equal(ClipStatus.Vanilla, t.StatusOf(5, "clip"u8));
        Assert.Equal(ClipStatus.Replaced, t.StatusOf(5, "clip!"u8));
        Assert.Equal(ClipStatus.Added, t.StatusOf(9, "clip"u8));
    }

    [Fact]
    public void TheEmbeddedTableIsThere()
    {
        var t = VanillaClipHashes.Embedded();
        Assert.NotNull(t);
        Assert.Equal(9_938, t!.Count);
    }
}
