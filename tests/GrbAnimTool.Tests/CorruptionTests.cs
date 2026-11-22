using System.Buffers.Binary;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Diagnostics;
using GrbAnimTool.Core.Forge;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>Damaged copies of real records and payloads must fail cleanly: an <see cref="InvalidDataException"/> or a walk that doesn't
/// reach the end, never another exception, a hang, or a read outside the input.</summary>
[Collection("install")]
public sealed class CorruptionTests(InstallFixture f)
{
    /// <summary>Parses, and when the walk reaches the end, decodes every key and samples the clip, as the viewer would.</summary>
    static string Use(byte[] b, SharedCurveTable t)
    {
        try
        {
            var c = ClipRecord.Parse(b);
            if (!c.WalkedToEnd) return "not at end";
            foreach (var cv in c.Curves)
            {
                var u = SharedCurveTable.Resolve(cv, t);
                if (u is null) continue;
                CurveDecoder.KeyTicks(u);
                for (int k = 0; k < u.Keys; k++) { CurveDecoder.KeyRot(u, k); CurveDecoder.KeyPos(u, k); CurveDecoder.Plant(u, k); CurveDecoder.KeyTick(u, k); }
            }
            var s = new ClipSampler(c, t);
            for (int k = 0; k <= c.LastTick; k += 7) s.At(k / 60f);   // LastTick is bounded by the duration check
            s.At(-1); s.At(float.MaxValue); s.At(float.NaN);
            return "at end";
        }
        catch (InvalidDataException) { return "refused"; }
    }

    List<ClipEntry> Sample()
    {
        var rnd = new Random(7);
        var all = f.Install.Clips;
        return [f.Install.FindClip("mca_emote_Hello")!, .. Enumerable.Range(0, 60).Select(_ => all[rnd.Next(all.Count)])];
    }

    /// <summary>Records cut short. A cut before the curves fails the head or the wrapper's checks. A cut inside the curves has the
    /// wrapper's length field set to the cut, so the curve walker itself meets the cut: every such record must stop in the walker.</summary>
    [GameDataFact]
    public void TruncatedRecordsNeverWalkToTheEnd()
    {
        var t = f.Install.SharedTable;
        var rnd = new Random(11);
        int inWalker = 0;
        Within.Seconds(120, () =>
        {
            foreach (var e in Sample())
            {
                var b = e.Bytes.ToArray();
                int fc = e.Record!.FirstCurve;
                var head = new List<int> { 0, 1, 8, 12, 13, fc / 2, fc - 9, fc - 1 };
                for (int i = 0; i < 10; i++) head.Add(rnd.Next(fc));
                foreach (int n in head.Where(n => n >= 0 && n < fc))
                    Assert.NotEqual("at end", Use(b[..n], t));

                var inCurves = new List<int> { fc, fc + 1, fc + 5, fc + 9, (fc + b.Length) / 2, b.Length - 1, b.Length - 5 };
                foreach (var cv in e.Record.Curves.Take(20)) { inCurves.Add(cv.At + 1); inCurves.Add(cv.At + cv.DiskLength - 1); inCurves.Add(cv.At + cv.DiskLength); }
                for (int i = 0; i < 20; i++) inCurves.Add(fc + rnd.Next(b.Length - fc));
                foreach (int n in inCurves.Where(n => n >= fc && n < b.Length))
                {
                    var x = b[..n];
                    BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(fc - 8), (uint)(n - (fc - 4)));
                    var c = ClipRecord.Parse(x);
                    Assert.False(c.WalkedToEnd, $"{e.Name} cut at {n}");
                    Assert.DoesNotContain("length field", c.Stop);
                    Assert.True(c.Curves.Count < e.Record.Curves.Count, $"{e.Name} cut at {n}: {c.Stop}");
                    Assert.NotEqual("at end", Use(x, t));
                    inWalker++;
                }
            }
            return 0;
        });
        Assert.True(inWalker > 1000, $"only {inWalker} cuts reached the walker");
    }

    [GameDataFact]
    public void FlippedBytesFailCleanly()
    {
        var t = f.Install.SharedTable;
        var rnd = new Random(13);
        int structural = 0;
        Within.Seconds(300, () =>
        {
            foreach (var e in Sample())
            {
                var c = e.Record!;
                // the curve headers (tags, sizes, key counts) and the curve count: a change there must stop the walk
                var header = new List<int> { c.FirstCurve - 4 };
                foreach (var cv in c.Curves.Take(30)) { header.Add(cv.At); if (!cv.IsReference) { header.Add(cv.At + 1); header.Add(cv.At + 5); } }
                foreach (int at in header)
                {
                    var b = e.Bytes.ToArray();
                    b[at] ^= 0x5A;
                    var r = Use(b, t);
                    if (r != "at end") structural++;
                }
                // anywhere: a clean result of some kind, whatever byte changes
                for (int i = 0; i < 40; i++)
                {
                    var b = e.Bytes.ToArray();
                    b[rnd.Next(b.Length)] ^= (byte)(1 << rnd.Next(8));
                    Use(b, t);
                }
                // a run of 0xFF over a random stretch
                {
                    var b = e.Bytes.ToArray();
                    int at = rnd.Next(b.Length), n = Math.Min(b.Length - at, 64);
                    b.AsSpan(at, n).Fill(0xFF);
                    Use(b, t);
                }
            }
            return 0;
        });
        Assert.True(structural > 1000, $"only {structural} header changes were caught");
    }

    /// <summary>One full decode of entry 0x800 per damaged header byte: about 90 s, so in category Slow.</summary>
    [GameDataFact, Trait("Category", "Slow")]
    public void DamagedPayloadsAreRefused()
    {
        var g = f.Install;
        using var fs = ForgeReader.OpenRead(g.PatchForgePath);
        var lay = ForgeReader.Read(fs);
        // entry 0x800 is small enough to run many times
        var payload = ForgeReader.ReadPayload(fs, lay.Find(SharedCurveTable.EntryId)!);
        List<DatafileRecord> Decode(byte[] p)
        {
            var d = DatafileCodec.Decode(p, g.Oodle, out int table, out _);
            return DatafileCodec.Parse(d, table);
        }
        Assert.NotEmpty(Decode(payload));
        var blocks = CompressedFileData.ReadBlocks(payload, out _);
        Within.Seconds(300, () =>
        {
            // truncated anywhere
            foreach (int n in new[] { 0, 1, 7, 8, 14, 15, 18, 19, 23, payload.Length / 3, payload.Length / 2, payload.Length - 1 })
                Assert.Throws<InvalidDataException>(() => Decode(payload[..n]));
            // the magic, the algorithm, the TOC bit, the chunk count and every chunk's sizes of each block
            var head = new List<(int At, byte Xor)>();
            foreach (var bl in blocks)
            {
                int o = bl.Offset;
                head.AddRange([(o, 0x41), (o + 10, 0x41), (o + 14, 0x80), (o + 15, 0x41), (o + 18, 0x41)]);
                for (int i = 0; i < bl.Chunks.Count; i++) { head.Add((o + 19 + 8 * i, 0x41)); head.Add((o + 23 + 8 * i, 0x41)); }
            }
            var passed = new List<string>();
            foreach (var (at, xor) in head)
            {
                var p = (byte[])payload.Clone();
                p[at] ^= xor;
                try { Decode(p); passed.Add($"+{at} ^ 0x{xor:X2}"); }
                catch (InvalidDataException) { }
            }
            Assert.True(passed.Count == 0, "decoded although damaged: " + string.Join(", ", passed));
            // inside a compressed chunk: Oodle (fuzz-safe) refuses, or the data decodes to something the parse refuses or accepts
            var rnd = new Random(17);
            var ch = blocks[^1].Chunks.First(c => c.Compressed);
            for (int i = 0; i < 200; i++)
            {
                var p = (byte[])payload.Clone();
                p[ch.DataOffset + rnd.Next((int)ch.Stored)] ^= (byte)(1 << rnd.Next(8));
                try { Decode(p); } catch (InvalidDataException) { }
            }
            return 0;
        });
    }

    /// <summary>The body and add-on skeleton records cut short and with bytes changed: each parse is refused, or gives a skeleton held
    /// parents first with a finite rest pose.</summary>
    [GameDataFact]
    public void DamagedSkeletonsFailCleanly()
    {
        var g = f.Install;
        var rnd = new Random(23);
        int refused = 0, parsed = 0;
        Within.Seconds(120, () =>
        {
            foreach (var (rec, bones) in new[] { (g.BodySkeletonRecord, g.BodySkeleton.Bones.Count), (g.AddOnSkeletonRecord, g.AddOnSkeleton.Bones.Count) })
            {
                Assert.Equal(bones, Skeleton.Parse(rec).Bones.Count);
                var damaged = new List<byte[]>();
                foreach (int n in new[] { 0, 8, 12, 17, rec.Length / 4, rec.Length / 2, rec.Length - 1, rec.Length - 5 }) damaged.Add(rec[..n]);
                for (int i = 0; i < 30; i++) damaged.Add(rec[..rnd.Next(rec.Length)]);
                for (int i = 0; i < 300; i++)
                {
                    var b = (byte[])rec.Clone();
                    b[rnd.Next(b.Length)] ^= (byte)(1 << rnd.Next(8));
                    damaged.Add(b);
                }
                // bone objects' class, parent forms and vectors, overwritten with 0xFF (a NaN as a float)
                foreach (var bone in RecordObjects.Bones(rec).Take(20))
                    foreach (int d in new[] { 8, 16, 17, 26, 40 })
                    {
                        var b = (byte[])rec.Clone();
                        b.AsSpan(bone.At + d, Math.Min(4, b.Length - bone.At - d)).Fill(0xFF);
                        damaged.Add(b);
                    }
                foreach (var b in damaged)
                {
                    Skeleton s;
                    try { s = Skeleton.Parse(b); }
                    catch (InvalidDataException) { refused++; continue; }
                    parsed++;
                    foreach (var x in s.Bones)
                    {
                        Assert.True(x.ParentIndex < x.Index);
                        var l = x.LocalRest;
                        Assert.True(float.IsFinite(l.Position.X + l.Position.Y + l.Position.Z + l.Rotation.X + l.Rotation.Y + l.Rotation.Z + l.Rotation.W));
                    }
                }
            }
            return 0;
        });
        Assert.True(refused > 10 && parsed > 100, $"refused {refused}, parsed {parsed}");
    }

    /// <summary>The soak's decode pass over 200 clips, twice: the same counts and checksum both times.</summary>
    [GameDataFact]
    public void ADecodePassRepeatsItself()
    {
        var g = f.Install;
        var recs = g.Records.Where(r => r.Type == ClipRecord.Type).Take(200).ToList();
        var a = Within.Seconds(120, () => DecodePass.Run(g, recs));
        var b = Within.Seconds(120, () => DecodePass.Run(g, recs));
        Assert.Equal(200, a.Clips);
        Assert.True(a.Keys > 10_000 && a.Poses > 1_000, $"{a}");
        Assert.Equal(a, b);
        Assert.True(double.IsFinite(a.Checksum));
    }

    [GameDataFact]
    public void DamagedSharedTable()
    {
        var g = f.Install;
        var tb = g.SharedTable.Bytes;
        foreach (int n in new[] { 0, 9, 40, tb.Length / 2, tb.Length - 1 })
        {
            try { Assert.False(SharedCurveTable.Parse(tb[..n]).WalkedToEnd); }
            catch (InvalidDataException) { }
        }
        var rnd = new Random(19);
        for (int i = 0; i < 200; i++)
        {
            var b = (byte[])tb.Clone();
            b[rnd.Next(b.Length)] ^= 0xFF;
            try { SharedCurveTable.Parse(b); } catch (InvalidDataException) { }
        }
    }
}
