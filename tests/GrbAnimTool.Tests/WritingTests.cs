using System.Buffers.Binary;
using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Tests;

/// <summary>The write path on records built here: the curve writers read back through the decoders, the keying rule, curves of 256 keys or
/// more, the rebuild and its arena refusals, the duration writer, and the record-file writer. No game data.</summary>
public sealed class WritingTests : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "writing-" + Guid.NewGuid().ToString("N"));

    public WritingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static WritingSynthetic.Entry E(ushort kind, uint value) => new(kind, value);

    static Curve One(byte[] curve)
    {
        var l = CurveWalker.Walk(curve, 0, 1, out _, out string stop);
        Assert.Equal(CurveWalker.AtEnd, stop);
        return l[0];
    }

    static double Deg(Quaternion a, Quaternion b) => Angles.Deg(a, b);

    /// <summary>The 6-byte form stores three components in steps of 4.3e-5: a rotation comes back within about 0.012 deg.</summary>
    const double Quantum6Deg = 0.02;

    static Quaternion Rot(int i) => Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(i * 0.013f, MathF.Sin(i * 0.05f), i * -0.007f + 0.3f));

    [Fact]
    public void TheKeyingRuleStaysWithin255Keys()
    {
        Assert.Equal([0], KeyingRule.Ticks(0));
        Assert.Equal([0, 1], KeyingRule.Ticks(1));
        Assert.Equal([0, 2, 4], KeyingRule.Ticks(4));
        Assert.Equal([0, 2, 4, 5], KeyingRule.Ticks(5));
        foreach (int last in Enumerable.Range(0, 2000).Concat([5000, 9083, 15000, 36000]))
        {
            var t = KeyingRule.Ticks(last);
            Assert.True(t.Length <= 255, $"{last}: {t.Length} keys");
            Assert.Equal(0, t[0]);
            Assert.Equal(last, t[^1]);
            for (int i = 1; i < t.Length; i++) Assert.True(t[i] > t[i - 1]);
            int s = KeyingRule.Step(last);
            Assert.True(s >= 2 && s == Math.Max(2, (int)Math.Ceiling(last / 254.0)));
            for (int i = 1; i < t.Length - 1; i++) Assert.Equal(s, t[i] - t[i - 1]);
        }
        Assert.Equal(255, KeyingRule.Ticks(508).Length);
        Assert.Throws<ArgumentException>(() => KeyingRule.Ticks(-1));
    }

    [Fact]
    public void RotationsWrittenReadBack()
    {
        var ticks = new[] { 0, 2, 7, 30, 255 };
        var q = ticks.Select(Rot).ToArray();
        foreach (var (curve, tag, tol) in new[] { (CurveWriter.RotCurve(ticks, q), (byte)0x0C, Quantum6Deg), (CurveWriter.RotCurve12(ticks, q), (byte)0x18, 1e-4) })
        {
            var c = One(curve);
            Assert.Equal(tag, c.Tag);
            Assert.Equal(ticks, CurveDecoder.KeyTicks(c));
            for (int k = 0; k < ticks.Length; k++) Assert.True(Deg(q[k], CurveDecoder.KeyRot(c, k)!.Value) < tol, $"tag {tag:X2} key {k}");
            // written again from its decoded keys: the same layout, the values within the form's rounding
            var again = One(CurveReencoder.Reencode(c)!);
            Assert.Equal(curve.AsSpan(0, c.ValuesAt).ToArray(), again.Source.AsSpan(0, again.ValuesAt).ToArray());
            for (int k = 0; k < ticks.Length; k++) Assert.True(Deg(CurveDecoder.KeyRot(c, k)!.Value, CurveDecoder.KeyRot(again, k)!.Value) < tol);
        }
        // past tick 255 the two-byte time form; a negated rotation gives the same bytes
        var wide = One(CurveWriter.RotCurve([0, 100, 400], [q[0], q[1], q[2]]));
        Assert.Equal(0x0D, wide.Tag);
        Assert.Equal([0, 100, 400], CurveDecoder.KeyTicks(wide));
        Assert.Equal(CurveWriter.Encode6(q[3]), CurveWriter.Encode6(Quaternion.Negate(q[3])));
        Assert.Equal(CurveWriter.Encode12(q[3]), CurveWriter.Encode12(Quaternion.Negate(q[3])));
        // one key of the 12-byte form is the game's 21-byte curve: tag 0x18, size 16, one key
        var one12 = CurveWriter.RotCurve12([0], [q[1]]);
        Assert.Equal(21, one12.Length);
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(one12.AsSpan(1)));
    }

    [Fact]
    public void PositionsPlantsAndBytesWrittenReadBack()
    {
        var p = One(CurveWriter.PosCurve([0, 3, 9], [new(0.1f, -2, 3.5f), new(0, 0, 0), new(1e-3f, 5, -7)]));
        Assert.Equal(0x1B, p.Tag);
        Assert.Equal(new Vector3(1e-3f, 5, -7), CurveDecoder.KeyPos(p, 2));
        var plants = new[] { new FootPlant(0, 0, Vector3.Zero), new FootPlant(12, 30, new(-0.146f, 0.174f, 0.01f)), new FootPlant(300, 8, new(0.2f, 1.5f, 0)) };
        var f = One(CurveWriter.PlantCurve(plants));
        Assert.Equal(0x43, f.Tag);
        for (int k = 0; k < 3; k++)
        {
            var r = CurveDecoder.Plant(f, k)!.Value;
            Assert.Equal(plants[k].Tick, r.Tick);
            Assert.Equal(plants[k].LengthTicks, r.LengthTicks);
            Assert.True((plants[k].Point - r.Point).Length() < 1e-6f);
        }
        Assert.Equal(CurveWriter.PlantCurve(plants), CurveReencoder.Reencode(f));
        var b = One(CurveWriter.ByteCurve([0, 10, 12, 102, 104], [125, 125, 0, 0, 125]));
        Assert.Equal(0x27, b.Tag);
        Assert.Equal(2u + 4 + 5, b.Size);   // 2 B + 4 times, alignment 1, then 5 values
        // a byte track counts against the alignment-1 arena, but with two-byte times against the alignment-2 one
        Assert.Equal(1, ClipRebuild.ArenaOf(b.Tag, b.ValueBytes));
        Assert.Equal(2, ClipRebuild.ArenaOf(One(CurveWriter.ByteCurve([0, 300], [1, 2])).Tag, 1));
        Assert.Equal(8, ClipRebuild.ArenaOf(f.Tag, f.ValueBytes));
        Assert.Equal((byte)0, CurveReencoder.KeyByte(b, 2));
        Assert.Equal(new[] { 0, 10, 12, 102, 104 }, CurveDecoder.KeyTicks(b));
    }

    /// <summary>A toe on the ground for ticks 0 to 20, lifted and carried forward to tick 40, down again from 40 to 60: two stances, and the
    /// plant keys from them, which write and read back.</summary>
    [Fact]
    public void FootContactsBecomePlants()
    {
        var toe = new List<(int, Vector3)>();
        for (int t = 0; t <= 60; t += 2)
        {
            float y = t <= 20 ? 0 : t >= 40 ? 0.6f : (t - 20) * 0.03f;
            float z = t is > 20 and < 40 ? 0.1f : 0.02f;
            toe.Add((t, new Vector3(0.1f, y, z)));
        }
        // the speed is taken over the neighbouring samples, so the ticks next to the lift and the landing (20, 40) move too fast
        var st = FootContacts.Stances(toe, 0.02f, 0.3f);
        Assert.Equal([(0, 18), (42, 60)], st);
        var keys = FootContacts.PlantKeys(st, t => new Vector3(0.1f, t >= 40 ? 0.6f : 0, 0.02f));
        Assert.Equal([new FootPlant(0, 18, new(0.1f, 0, 0.02f)), new FootPlant(42, 18, new(0.1f, 0.6f, 0.02f))], keys);
        var c = One(CurveWriter.PlantCurve(keys));
        Assert.Equal(42, CurveDecoder.Plant(c, 1)!.Value.Tick);
        // a foot in the air at tick 0 gets a first key of zeros
        Assert.Equal(new FootPlant(0, 0, Vector3.Zero), FootContacts.PlantKeys([(40, 60)], _ => Vector3.One)[0]);
        Assert.Empty(FootContacts.Stances([], 0.02f, 0.3f));
    }

    [Fact]
    public void BadInputIsRefused()
    {
        var q = Quaternion.Identity;
        Assert.Throws<ArgumentException>(() => CurveWriter.RotCurve([], []));
        Assert.Throws<ArgumentException>(() => CurveWriter.RotCurve([0, 5, 4], [q, q, q]));
        // two keys at one tick (a step) are written, as the game's own curves hold them
        Assert.Equal([0, 5, 5], CurveDecoder.KeyTicks(One(CurveWriter.RotCurve([0, 5, 5], [q, q, q]))));
        Assert.Throws<ArgumentException>(() => CurveWriter.RotCurve([1], [q]));
        Assert.Throws<ArgumentException>(() => CurveWriter.RotCurve([0, 2], [q]));
        Assert.Throws<ArgumentException>(() => CurveWriter.RotCurve([0], [default]));
        Assert.Throws<ArgumentException>(() => CurveWriter.RotCurve([0, 300], [q, q], twoByteTimes: false));
        Assert.Throws<ArgumentException>(() => CurveWriter.PosCurve([0, 70000], [Vector3.Zero, Vector3.Zero]));
        Assert.Throws<ArgumentException>(() => CurveWriter.PlantCurve([new FootPlant(0, 40000, Vector3.Zero)]));
        Assert.Throws<ArgumentException>(() => CurveWriter.PlantCurve([new FootPlant(0, 1, new(float.NaN, 0, 0))]));
        Assert.Throws<ArgumentException>(() => CurveWriter.Curve(0x0C, 2, Enumerable.Range(0, 256).ToList(), Enumerable.Repeat(new byte[6], 256).ToList()));
        Assert.Throws<ArgumentException>(() => CurveWriter.Curve(0x0C, 2, [0, 1], [new byte[6], new byte[4]]));
        Assert.Throws<ArgumentException>(() => CurveWriter.Curve(0x0C, 2, [0], [new byte[4]]));
        Assert.Throws<ArgumentException>(() => CurveWriter.Curve(0x0D, 2, [0], [new byte[6]]));
    }

    /// <summary>Curves of 256 keys or more: the size in memory holds 2 B more, and they walk inside a record and read back key for key.</summary>
    [Fact]
    public void CurvesOf256KeysOrMoreReadBack()
    {
        foreach (int n in new[] { 255, 256, 300, 1150, 4001 })
        {
            var ticks = Enumerable.Range(0, n).Select(i => i * 2).ToList();
            var q = ticks.Select(Rot).ToList();
            var pos = ticks.Select(t => new Vector3(t * 0.01f, MathF.Sin(t), -t)).ToList();
            var rot = CurveWriter.RotCurve(ticks, q);
            var r12 = CurveWriter.RotCurve12(ticks, q);
            var ps = CurveWriter.PosCurve(ticks, pos);
            var rec = WritingSynthetic.Clip("long", 7, ticks[^1] / 60f,
                [E(0x15, 3), E(5, WritingSynthetic.Units(rot)), E(7, WritingSynthetic.Units(r12) + WritingSynthetic.Units(ps))],
                [(0, rot), (1, r12), (2, ps)]);
            var c = ClipRecord.Parse(rec);
            Assert.True(c.WalkedToEnd, c.Stop);
            int extra = n >= 256 ? 2 : 0;
            Assert.Equal((uint)((2 + extra + (n - 1) * 2 + 1) / 2 * 2 + n * 6), c.Curves[0].Size);
            Assert.Equal((uint)((2 + extra + (n - 1) * 2 + 3) / 4 * 4 + n * 12), c.Curves[2].Size);
            foreach (var cv in c.Curves) Assert.Equal(ticks, CurveDecoder.KeyTicks(cv));
            for (int k = 0; k < n; k++)
            {
                double d6 = Deg(q[k], CurveDecoder.KeyRot(c.Curves[0], k)!.Value), d12 = Deg(q[k], CurveDecoder.KeyRot(c.Curves[1], k)!.Value);
                Assert.True(d6 < Quantum6Deg, $"{n} keys, key {k}: 6-byte form {d6} deg");
                Assert.True(d12 < 1e-4, $"{n} keys, key {k}: 12-byte form {d12} deg");
                Assert.Equal(pos[k], CurveDecoder.KeyPos(c.Curves[2], k));
            }
            Assert.Equal(ps, CurveReencoder.Reencode(c.Curves[2]));
            foreach (var cv in c.Curves) Assert.Equal(cv.DiskLength, CurveReencoder.Reencode(cv)!.Length);
            Assert.Equal(rec, ClipRebuild.Rebuild(c, null).Bytes);
        }
    }

    static (byte[] Rec, byte[] Rot, byte[] Pos) TwoTrackClip(Func<byte[], byte[], WritingSynthetic.Entry[]> head, int[]? steps = null,
        WritingSynthetic.Group[]? groups = null, bool stepsWithoutGroup = false, float duration = 0.5f, byte[]? array = null, bool headed = true)
    {
        var rot = CurveWriter.RotCurve([0], [Rot(1)]);
        var pos = CurveWriter.PosCurve([0, 4], [Vector3.Zero, Vector3.One]);
        // the blocks given get the header of the game's form, with the count of the block words they hold, unless told otherwise
        if (headed && array is { Length: > 0 }) array = WritingSynthetic.Headed(array);
        return (WritingSynthetic.Clip("two", 0x1234, duration, head(rot, pos), [(0xAAAA0001, rot), (0xAAAA0001, pos)], steps, groups, stepsWithoutGroup, array), rot, pos);
    }

    static WritingSynthetic.Entry[] Head(byte[] r, byte[] p) => [E(0x15, 2), E(5, WritingSynthetic.Units(r)), E(7, WritingSynthetic.Units(p))];

    [Fact]
    public void RebuildKeepsTheRecordAndSizesTheArenas()
    {
        var (rec, rot, _) = TwoTrackClip((r, p) => [E(0x15, 2), E(5, WritingSynthetic.Units(r) + 3), E(7, WritingSynthetic.Units(p))]);
        var c = ClipRecord.Parse(rec);
        Assert.True(c.WalkedToEnd, c.Stop);
        var same = ClipRebuild.Rebuild(c, null);
        Assert.Equal(rec, same.Bytes);
        Assert.Equal(0, same.Rewritten);
        // a longer rotation: kind 5 grows by what the new curve needs, the 3 units beyond the curves stay
        var longer = CurveWriter.RotCurve([0, 5, 10, 15], [Rot(1), Rot(2), Rot(3), Rot(4)]);
        var b = ClipRebuild.Rebuild(c, null, new Dictionary<int, byte[]> { [0] = longer });
        Assert.True(b.Record.WalkedToEnd);
        Assert.Equal(1, b.Rewritten);
        Assert.Equal(WritingSynthetic.Units(longer) + 3, ClipRebuild.ArenaValue(b.Record, 5));
        Assert.Equal(ClipRebuild.ArenaValue(c, 7), ClipRebuild.ArenaValue(b.Record, 7));
        Assert.Equal(rec.Length - rot.Length + longer.Length, b.Bytes.Length);
        Assert.Equal((uint)(b.Bytes.Length - c.IdAt), BinaryPrimitives.ReadUInt32LittleEndian(b.Bytes.AsSpan(4)));
        Assert.Equal(rec.AsSpan(c.Curves[1].At, c.Curves[1].DiskLength).ToArray(), b.Bytes.AsSpan(b.Record.Curves[1].At, b.Record.Curves[1].DiskLength).ToArray());
        // and back again gives the original
        Assert.Equal(rec, ClipRebuild.Rebuild(b.Record, null, new Dictionary<int, byte[]> { [0] = rot }).Bytes);
    }

    /// <summary>The crafted cases: a head lacking the kind the new form needs; a kind that would drop to 0; a track changing its kind; a head
    /// holding less than its curves need; a new curve that isn't one whole curve.</summary>
    [Fact]
    public void RebuildRefusals()
    {
        // track 0 a 12-byte rotation (alignment 4), and the head with no kind 5: the 6-byte form would need it
        var r12 = CurveWriter.RotCurve12([0], [Rot(1)]);
        var pos = CurveWriter.PosCurve([0], [Vector3.One]);
        var rec = WritingSynthetic.Clip("arena", 0x99, 0.2f, [E(0x15, 2), E(7, WritingSynthetic.Units(r12) + WritingSynthetic.Units(pos))], [(1, r12), (1, pos)]);
        var c = ClipRecord.Parse(rec);
        Assert.Equal(rec, ClipRebuild.Rebuild(c, null).Bytes);
        var ex = Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(c, null, new Dictionary<int, byte[]> { [0] = CurveWriter.RotCurve([0], [Rot(1)]) }));
        Assert.Contains("no arena kind 5", ex.Message);
        // the same in the 12-byte form fits
        Assert.True(ClipRebuild.Rebuild(c, null, new Dictionary<int, byte[]> { [0] = CurveWriter.RotCurve12([0, 3], [Rot(1), Rot(2)]) }).Record.WalkedToEnd);

        // the position as a reference into the shared table: kind 7 would hold only the rotation's units; with the rotation a reference
        // too it would drop to 0
        var table = SharedCurveTable.Parse(Synthetic.SharedTable(2, [.. CurveWriter.PosCurve([0], [Vector3.Zero]), .. CurveWriter.RotCurve12([0], [Rot(5)])]));
        Assert.True(table.WalkedToEnd);
        byte[] Ref(uint i) { var b = new byte[5]; b[0] = Curve.ReferenceTag; BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), i); return b; }
        var oneRef = ClipRebuild.Rebuild(c, table, new Dictionary<int, byte[]> { [1] = Ref(1) });
        Assert.Equal(WritingSynthetic.Units(r12), ClipRebuild.ArenaValue(oneRef.Record, 7));
        ex = Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(c, table, new Dictionary<int, byte[]> { [0] = Ref(2), [1] = Ref(1) }));
        Assert.Contains("drop from", ex.Message);
        ex = Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(c, table, new Dictionary<int, byte[]> { [0] = Ref(3) }));
        Assert.Contains("doesn't hold", ex.Message);
        ex = Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(c, null, new Dictionary<int, byte[]> { [0] = Ref(2) }));
        Assert.Contains("no shared table", ex.Message);

        ex = Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(c, null, new Dictionary<int, byte[]> { [0] = pos }));
        Assert.Contains("change its kind", ex.Message);
        ex = Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(c, null, new Dictionary<int, byte[]> { [0] = r12[..^1] }));
        Assert.Contains("one whole curve", ex.Message);
        ex = Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(c, null, new Dictionary<int, byte[]> { [0] = [.. r12, .. r12] }));
        Assert.Contains("one whole curve", ex.Message);
        Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(c, null, new Dictionary<int, byte[]> { [2] = pos }));

        // a head holding less than its curves need
        var small = WritingSynthetic.Clip("small", 0x98, 0.2f, [E(0x15, 2), E(7, 3)], [(1, r12), (1, pos)]);
        ex = Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(small, null));
        Assert.Contains("damaged", ex.Message);
        // a length field that isn't the record's
        var badLen = (byte[])rec.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badLen.AsSpan(4), 5);
        Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(badLen, null));
        Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(rec[..40], null));
        Assert.Throws<RebuildRefusedException>(() => ClipRebuild.Rebuild(rec[..^1], null));
    }

    [Fact]
    public void TheDurationWriterScalesTheStepTimes()
    {
        var (rec, _, _) = TwoTrackClip((r, p) => [E(0x15, 2), E(5, WritingSynthetic.Units(r)), E(7, WritingSynthetic.Units(p))], [10, 40, 77]);
        var c = ClipRecord.Parse(rec);
        Assert.True(c.WalkedToEnd, c.Stop);
        var list = StepTimeList.Find(c)!;
        Assert.Equal([10, 40, 77], list.Times);
        var r = DurationWriter.WithDuration(c, 1.0f);
        var y = ClipRecord.Parse(r);
        Assert.Equal(1.0f, y.Duration);
        Assert.Equal(60, y.LastTick);
        Assert.Equal([20, 80, 154], StepTimeList.Find(y)!.Times);
        Assert.Equal(rec.Length, r.Length);
        Assert.Equal([10, 40, 77], StepTimeList.Find(ClipRecord.Parse(DurationWriter.WithDuration(c, 1.0f, scaleSteps: false)))!.Times);
        Assert.Throws<RebuildRefusedException>(() => DurationWriter.WithDuration(c, 0));
        Assert.Throws<RebuildRefusedException>(() => DurationWriter.WithDuration(c, float.NaN));
        Assert.Throws<RebuildRefusedException>(() => DurationWriter.WithDuration(c, 601));
        Assert.Throws<RebuildRefusedException>(() => DurationWriter.WithDuration(c, 500));   // 77 x 1000 is past a u16
        var none = ClipRecord.Parse(TwoTrackClip((r2, p2) => [E(0x15, 2), E(5, WritingSynthetic.Units(r2)), E(7, WritingSynthetic.Units(p2))]).Rec);
        Assert.Null(StepTimeList.Find(none));
        Assert.Equal(2.0f, ClipRecord.Parse(DurationWriter.WithDuration(none, 2.0f)).Duration);
        Assert.Empty(DurationWriter.Change(none, 2.0f).Lists);
    }

    /// <summary>Every event group's list scales, sounds and steps alike, in two groups; the times found by the group reader after the write
    /// are the scaled ones.</summary>
    [Fact]
    public void TheDurationWriterScalesEveryGroupsTimes()
    {
        var (rec, _, _) = TwoTrackClip(Head, [10, 40, 77], [new([5, 30, 60], [true, false, true])]);
        var c = ClipRecord.Parse(rec);
        Assert.True(c.WalkedToEnd, c.Stop);
        Assert.Equal(2, EventObjects.Groups(c).Count(g => g.TimesAt >= 0));
        // the step-only list is the first group's, the second group mixes sounds and a step: one list for steps alone wouldn't see it
        Assert.Null(StepTimeList.Find(c));
        var ch = DurationWriter.Change(c, 0.75f);
        Assert.Equal(4, ch.StepObjects);
        Assert.Equal(2, ch.Lists.Count);
        var y = ClipRecord.Parse(ch.Bytes);
        Assert.Equal(0.75f, y.Duration);
        var gs = EventObjects.Groups(y);
        Assert.Equal([15, 60, 116], gs[0].Times);   // 77 x 1.5 = 115.5, rounded to even
        Assert.Equal([8, 45, 90], gs[1].Times);     // 5 x 1.5 = 7.5, rounded to even
        Assert.Equal(rec.Length, ch.Bytes.Length);
    }

    /// <summary>A clip holding step objects whose list the group reader can't find, or a group whose list isn't there, is refused, never
    /// written with its times left as they were; so is a record whose own duration is damaged.</summary>
    [Fact]
    public void TheDurationWriterRefusesTimesItCantFind()
    {
        var loose = ClipRecord.Parse(TwoTrackClip(Head, [10, 40, 77], stepsWithoutGroup: true).Rec);
        Assert.True(loose.WalkedToEnd, loose.Stop);
        Assert.NotNull(StepTimeList.Find(loose));   // a list after the step objects, but in no event group
        var ex = Assert.Throws<RebuildRefusedException>(() => DurationWriter.WithDuration(loose, 1.0f));
        Assert.Contains("step objects", ex.Message);
        // without scaling asked, the duration alone is written
        Assert.Equal(1.0f, ClipRecord.Parse(DurationWriter.WithDuration(loose, 1.0f, scaleSteps: false)).Duration);

        // a group whose list's count isn't its events'
        var (rec, _, _) = TwoTrackClip(Head, [10, 40, 77]);
        var c = ClipRecord.Parse(rec);
        var g = EventObjects.Groups(c).Single();
        var broken = (byte[])rec.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(broken.AsSpan(g.TimesAt - 2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(g.TimesAt - 6), 6);
        var b = ClipRecord.Parse(broken);
        Assert.True(b.WalkedToEnd, b.Stop);
        ex = Assert.Throws<RebuildRefusedException>(() => DurationWriter.WithDuration(b, 1.0f));
        Assert.Contains("time list wasn't found", ex.Message);

        // a damaged own duration
        foreach (float bad in new[] { -1f, 0f, float.NaN, 700f })
        {
            var d = ClipRecord.Parse(TwoTrackClip(Head, [10], duration: bad).Rec);
            ex = Assert.Throws<RebuildRefusedException>(() => DurationWriter.WithDuration(d, 1.0f));
            Assert.Contains("its own duration", ex.Message);
        }
    }

    /// <summary>The times scale in float, rounded to even: 0.7 s made 0.75 s takes 21 to 22.5 in float (so 22), where double gives
    /// 22.5000004 (so 23); the float result is written.</summary>
    [Fact]
    public void TheDurationWriterRoundsInFloat()
    {
        int[] times = [21, 49, 77];
        Assert.Equal([23, 53, 83], times.Select(t => (int)Math.Round(t * (double)0.75f / 0.7f, MidpointRounding.ToEven)));
        var c = ClipRecord.Parse(TwoTrackClip(Head, times, duration: 0.7f).Rec);
        Assert.Equal([22, 52, 82], DurationWriter.Change(c, 0.75f).Lists.Single().After);
    }

    /// <summary>The byte array's lists are read in each form (u8 and u16, with 1 or 4 bytes a value, a payload length of one varint byte
    /// or two), and a clip holding a time above 0 in any of them is refused by the duration writer, with or without the event times scaled,
    /// and named by <see cref="DurationWriter.CanChange"/>; lists of zeros alone, or no array, change nothing. A block that doesn't fit its
    /// form exactly is unread, and refused too.</summary>
    [Fact]
    public void TheDurationWriterRefusesTimesInTheByteArray()
    {
        byte[] u8 = WritingSynthetic.Block(1, [12, 30, 58]), u16 = WritingSynthetic.Block(2, [76, 120, 720]);
        byte[] u8f0 = WritingSynthetic.Block(1, [5, 9], form: 0), u16f1 = WritingSynthetic.Block(2, [0, 300], form: 1);
        // a two-byte length: 22 u16 values give a payload of 136 B (varint 88 01), 57 give 348 B (varint DC 02)
        int[] t22 = Enumerable.Range(0, 22).Select(i => 32 * i).ToArray(), t57 = Enumerable.Range(0, 57).Select(i => 32 * i).ToArray();
        byte[] mid = WritingSynthetic.Block(2, t22), big = WritingSynthetic.Block(2, t57);
        Assert.Equal([0x88, 0x01], mid[12..14]);
        Assert.Equal([0xDC, 0x02], big[12..14]);
        // a form-0 block of 130 u8 times and 1 B a value: a payload of 262 B (varint 86 02), which a one-byte length would read as an
        // empty u8 list after 86 bytes
        int[] t130 = Enumerable.Range(0, 130).ToArray();
        byte[] f0big = WritingSynthetic.Block(1, t130, form: 0);
        Assert.Equal([0x86, 0x02], f0big[12..14]);
        byte[] zeros = WritingSynthetic.Block(1, [0]), zeros16 = WritingSynthetic.Block(2, [0, 0]);
        foreach (var (array, form) in new[] { (u8, 1), (u16, 2), (u8f0, 1), (u16f1, 2), (mid, 2), (big, 2), (f0big, 1), (zeros.Concat(u16).ToArray(), 2), (big.Concat(zeros).ToArray(), 2) })
        {
            var c = ClipRecord.Parse(TwoTrackClip(Head, [10, 40], array: array).Rec);
            Assert.True(c.WalkedToEnd, c.Stop);
            var lists = ByteArrayTimes.Lists(c, out int unread);
            Assert.Equal(0, unread);
            Assert.Contains(lists, l => l.Width == form && l.HoldsTime);
            Assert.True(ByteArrayTimes.HoldsTimes(c));
            var ex = Assert.Throws<RebuildRefusedException>(() => DurationWriter.Change(c, 1.0f));
            Assert.Contains(DurationWriter.UnreadTimesReason, ex.Message);
            Assert.Throws<RebuildRefusedException>(() => DurationWriter.WithDuration(c, 1.0f, scaleSteps: false));
            Assert.False(DurationWriter.CanChange(c, out string why));
            Assert.Equal(DurationWriter.UnreadTimesReason, why);
        }
        Assert.Equal([12, 30, 58], ByteArrayTimes.Lists(ClipRecord.Parse(TwoTrackClip(Head, array: u8).Rec), out _).Single().Values);
        Assert.Equal([0, 300], ByteArrayTimes.Lists(ClipRecord.Parse(TwoTrackClip(Head, array: u16f1).Rec), out _).Single().Values);
        foreach (var (array, values, nb, off) in new[] { (mid, t22, 2, 4), (big, t57, 2, 4), (f0big, t130, 2, 2) })
        {
            var c = ClipRecord.Parse(TwoTrackClip(Head, array: array).Rec);
            var l = ByteArrayTimes.Lists(c, out _).Single();
            Assert.Equal(values, l.Values);
            // the header, the leading 02 00, the word, the head byte and FF FF FF, the varint, then the form's own bytes
            Assert.Equal(ByteArrayTimes.ArrayOf(c)!.Value.At + WritingSynthetic.HeaderLength + 2 + 10 + nb + off, l.TimesAt);
            Assert.Equal(1, ByteArrayTimes.HeaderCount(c));
        }
        // blocks that don't fit their form exactly, or aren't followed by 02 00 and a word or the array's end: unread, so refused
        byte[] overlong = [0x02, 0x00, 0x6C, 0x98, 0x41, 0x86, 0x03, 0x60, 0xF0, 0xFF, 0xFF, 0xFF, 0x82, 0x00, 0x00, 0x00];
        byte[] sixBytes = [0x02, 0x00, 0x6C, 0x98, 0x41, 0x86, 0x03, 0x60, 0xF0, 0xFF, 0xFF, 0xFF, 0x82, 0x80, 0x80, 0x80, 0x80, 0x00, 0x00, 0x00];
        foreach (var array in new[] { WritingSynthetic.Block(2, [0, 0], tail: [0x40, 0x01]), WritingSynthetic.Block(1, [0], form: 0, tail: []),
            WritingSynthetic.Block(1, [0]).Concat(new byte[] { 0x01, 0x02 }).ToArray(), overlong, sixBytes, WritingSynthetic.Block(1, [0], form: 4) })
        {
            var c = ClipRecord.Parse(TwoTrackClip(Head, [10, 40], array: array).Rec);
            Assert.True(c.WalkedToEnd, c.Stop);
            ByteArrayTimes.Lists(c, out int un);
            Assert.Equal(1, un);
            Assert.True(ByteArrayTimes.HoldsTimes(c));
            Assert.False(DurationWriter.CanChange(c, out _));
        }
        // a block whose word isn't exact is found by no search: the header's count still says it is there, so the array is unread. One
        // byte of the word changed in a one-block array, in the second of two blocks and in the first of two; a count that differs from
        // sound blocks; a count byte this rule doesn't read
        var damagedOne = WritingSynthetic.Block(2, [76, 120, 720]);
        damagedOne[2 + 5] ^= 0x01;
        var damagedSecond = u8.Concat(u16).ToArray();
        damagedSecond[u8.Length + 2 + 3] ^= 0x10;
        var damagedFirst = u8.Concat(u16).ToArray();
        damagedFirst[2] ^= 0x01;
        // a count of 0 (no game clip holds one) over a block whose word was changed: found by no search and 0 = 0, so unread by the room
        // the bytes after the count leave for a block; a count of 0 with the header alone reads, holding nothing
        Assert.False(ByteArrayTimes.HoldsTimes(ClipRecord.Parse(TwoTrackClip(Head, [10, 40], array: WritingSynthetic.Headed([], 0), headed: false).Rec)));
        foreach (var (array, count) in new[] { (damagedOne, 1), (damagedSecond, 2), (damagedFirst, 2), (u8, 2), (u8.Concat(u16).ToArray(), 1), (u8, 0x81), (damagedOne, 0), (damagedFirst, 0) })
        {
            var c = ClipRecord.Parse(TwoTrackClip(Head, [10, 40], array: WritingSynthetic.Headed(array, count), headed: false).Rec);
            Assert.True(c.WalkedToEnd, c.Stop);
            ByteArrayTimes.Lists(c, out int un);
            Assert.True(un > 0, $"count {count}");
            Assert.True(ByteArrayTimes.HoldsTimes(c));
            Assert.False(DurationWriter.CanChange(c, out string why));
            Assert.Equal(DurationWriter.UnreadTimesReason, why);
        }
        // without the count's marker: the header-only form (66 bytes, no block) reads and holds nothing; blocks without it, or another
        // length, are unread
        byte[] headerOnly = new byte[ByteArrayTimes.HeaderOnlyLength];
        headerOnly[0] = 0x09;
        Assert.False(ByteArrayTimes.HoldsTimes(ClipRecord.Parse(TwoTrackClip(Head, [10, 40], array: headerOnly, headed: false).Rec)));
        Assert.Null(ByteArrayTimes.HeaderCount(ClipRecord.Parse(TwoTrackClip(Head, [10, 40], array: headerOnly, headed: false).Rec)));
        foreach (var array in new byte[][] { zeros, headerOnly[..^1], [.. headerOnly[..^3], 0xFF, 0xFF, 0xFF] })
        {
            var c = ClipRecord.Parse(TwoTrackClip(Head, [10, 40], array: array, headed: false).Rec);
            ByteArrayTimes.Lists(c, out int un);
            Assert.Equal(1, un);
            Assert.False(DurationWriter.CanChange(c, out _));
        }
        // the same zeros with a sound one-byte length: read, holding no time
        byte[] sound = [0x02, 0x00, 0x6C, 0x98, 0x41, 0x86, 0x03, 0x60, 0xF0, 0xFF, 0xFF, 0xFF, 0x02, 0x00, 0x00];
        Assert.False(ByteArrayTimes.HoldsTimes(ClipRecord.Parse(TwoTrackClip(Head, [10, 40], array: sound).Rec)));
        // zeros alone, or no array: the duration is written, and the array kept byte for byte
        foreach (var array in new[] { zeros.Concat(zeros16).ToArray(), Array.Empty<byte>() })
        {
            var (rec, _, _) = TwoTrackClip(Head, [10, 40], array: array);
            var c = ClipRecord.Parse(rec);
            Assert.False(ByteArrayTimes.HoldsTimes(c));
            Assert.True(DurationWriter.CanChange(c, out string why), why);
            var ch = DurationWriter.Change(c, 1.0f);
            var arr = ByteArrayTimes.ArrayOf(c)!.Value;
            Assert.Equal(rec.AsSpan(arr.At, arr.Length).ToArray(), ch.Bytes.AsSpan(arr.At, arr.Length).ToArray());
        }
        // a block whose payload runs past the array can't be read: refused too
        var cut = u16[..^6];
        var d = ClipRecord.Parse(TwoTrackClip(Head, array: cut).Rec);
        Assert.True(d.WalkedToEnd, d.Stop);
        ByteArrayTimes.Lists(d, out int bad);
        Assert.Equal(1, bad);
        Assert.False(DurationWriter.CanChange(d, out _));
    }

    [Fact]
    public void RecordFilesAreNamedCutAndKeptOutOfTheGameFolder()
    {
        var (rec, _, _) = TwoTrackClip((r, p) => [E(0x15, 2), E(5, WritingSynthetic.Units(r)), E(7, WritingSynthetic.Units(p))]);
        // the synthetic clip under a game clip's name, ID and index
        var c0 = ClipRecord.Parse(rec);
        var named = RecordFile.ToRecord(RecordFile.Content(rec), "mca_emote_Hello");
        Assert.Equal(c0.IdAt + 12, ClipRecord.Parse(named).IdAt);
        BinaryPrimitives.WriteUInt64LittleEndian(named.AsSpan(ClipRecord.Parse(named).IdAt), 0x0000016B6A04E0DBUL);
        var c = ClipRecord.Parse(named);
        Assert.Equal("mca_emote_Hello", c.Name);
        Assert.True(c.WalkedToEnd, c.Stop);

        Assert.Equal("102_-_mca_emote_Hello.Animation", RecordFile.ClipFileName(102, "mca_emote_Hello", c.Id));
        Assert.Equal("46_-_MIS_Y2E4_Katya_Maksimov.data", RecordFile.DatafileFolderName(46, "MIS_Y2E4_Katya_Maksimov"));
        var content = RecordFile.Content(named);
        Assert.Equal(named.AsSpan(12 + 15).ToArray(), content);
        Assert.Equal(1, content[0]);
        Assert.Equal(c.Id, RecordFile.IdOf(content));
        Assert.Equal(named, RecordFile.ToRecord(content, "mca_emote_Hello"));

        string game = Path.Combine(_dir, "game"), outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(game, "Extracted"));
        var item = new RecordFileItem(102, "mca_emote_Hello", c.Id, named);
        var pkg = RecordFileWriter.Write(outDir, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [item]);
        Assert.Equal("46_-_MIS_Y2E4_Katya_Maksimov.data", pkg.DatafileFolderName);
        var f = Assert.Single(pkg.Files);
        Assert.Equal("102_-_mca_emote_Hello.Animation", f.FileName);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(outDir, f.FileName)));
        // the record file alone: no install note
        Assert.Equal(new[] { f.FileName }, Directory.GetFiles(outDir).Select(Path.GetFileName));
        // a file there already is refused unless asked; then it is replaced
        Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(outDir, "x", [game], [item]));
        RecordFileWriter.Write(outDir, "x", [game], [item], overwrite: true);

        // never inside the game folder, at any depth, written any way
        foreach (var bad in new[] { game, game + Path.DirectorySeparatorChar, Path.Combine(game, "Extracted"), Path.Combine(game, "new", "deeper"), Path.Combine(game, "Extracted", "..", "x"), game.ToUpperInvariant() })
        {
            var ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(bad, "x", [game], [item]));
            Assert.Contains("never written into the game's folders", ex.Message);
        }
        Assert.False(Directory.Exists(Path.Combine(game, "new")));
        Assert.Empty(Directory.GetFiles(game, "*", SearchOption.AllDirectories));
        // a folder beside the game folder whose name starts the same is not inside it
        RecordFileWriter.Write(game + "-share", "x", [game], [item]);

        // names and records the rules refuse
        Assert.Throws<RecordFileException>(() => RecordFile.ClipFileName(1, "a.b", 1));
        Assert.Throws<RecordFileException>(() => RecordFile.ClipFileName(1, "a (3)", 1));
        Assert.Throws<RecordFileException>(() => RecordFile.ClipFileName(1, "", 1));
        Assert.Throws<RecordFileException>(() => RecordFile.ClipFileName(1, "a:b", 1));
        Assert.Throws<RecordFileException>(() => RecordFile.ClipFileName(1, "unnamed", 1));
        Assert.Throws<RecordFileException>(() => RecordFile.ClipFileName(1, "000000000000ABCD", 0xABCD));
        Assert.Throws<RecordFileException>(() => RecordFile.ClipFileName(-1, "a", 1));
        string other = Path.Combine(_dir, "other");
        Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(other, "x", [game], [item with { Id = 5 }]));
        Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(other, "x", [game], [item with { Name = "mca_emote_Clap" }]));
        Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(other, "x", [game], [item, item]));
        var broken = (byte[])named.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(4), 3);
        Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(other, "x", [game], [item with { Record = broken }]));
        Assert.False(Directory.Exists(other));
    }

    /// <summary>Two clips under game names, as the record-file writer takes them.</summary>
    static (RecordFileItem Hello, RecordFileItem Clap) TwoItems()
    {
        var (rec, _, _) = TwoTrackClip(Head);
        var content = RecordFile.Content(rec);
        BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(content[0] == 1 ? 8 + 12 * BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(4)) : 1), 0x0000016B6A04E0DBUL);
        ulong id = RecordFile.IdOf(content)!.Value;
        return (new RecordFileItem(102, "mca_emote_Hello", id, RecordFile.ToRecord(content, "mca_emote_Hello")),
                new RecordFileItem(103, "mca_emote_Clap", id, RecordFile.ToRecord(content, "mca_emote_Clap")));
    }

    static string[] Entries(string dir) => Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    /// <summary>A package is written whole or not at all: with the second target's name taken by a folder, or the second old file held
    /// open so it can't be replaced, the first file stays as it was (even with overwrite), no temporary file is left, and the error is a
    /// <see cref="RecordFileException"/> in words.</summary>
    [Fact]
    public void APackageIsWrittenWholeOrNotAtAll()
    {
        var (hello, clap) = TwoItems();
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        string h = "102_-_mca_emote_Hello.Animation", c = "103_-_mca_emote_Clap.Animation";

        // the second target's name taken by a folder
        string a = Path.Combine(_dir, "taken");
        Directory.CreateDirectory(Path.Combine(a, c));
        File.WriteAllText(Path.Combine(a, h), "old");
        var ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(a, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [hello, clap], overwrite: true));
        Assert.Contains("is a folder", ex.Message);
        Assert.Equal("old", File.ReadAllText(Path.Combine(a, h)));
        Assert.Equal(new[] { h, c }, Entries(a));

        // the second old file open, so it can't be moved aside: the first, already in place, is taken back and the old one restored
        string b = Path.Combine(_dir, "held");
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, h), "old hello");
        File.WriteAllText(Path.Combine(b, c), "old clap");
        using (new FileStream(Path.Combine(b, c), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(b, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [hello, clap], overwrite: true));
            Assert.Contains("Nothing was left there", ex.Message);
        }
        Assert.Equal("old hello", File.ReadAllText(Path.Combine(b, h)));
        Assert.Equal("old clap", File.ReadAllText(Path.Combine(b, c)));
        Assert.Equal(new[] { h, c }, Entries(b));

        // and once nothing stands in the way, both files
        var pkg = RecordFileWriter.Write(b, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [hello, clap], overwrite: true);
        Assert.Equal(2, pkg.Files.Count);
        Assert.Equal(new[] { c, h }.Order(StringComparer.Ordinal), Entries(b));
        Assert.Equal(RecordFile.Content(clap.Record), File.ReadAllBytes(Path.Combine(b, c)));

        // a read-only old file replaced: the new file in place, no old copy left beside it
        string ro = Path.Combine(_dir, "readonly");
        Directory.CreateDirectory(ro);
        File.WriteAllText(Path.Combine(ro, h), "old hello");
        File.SetAttributes(Path.Combine(ro, h), FileAttributes.ReadOnly);
        RecordFileWriter.Write(ro, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [hello], overwrite: true);
        Assert.Equal(new[] { h }, Entries(ro));
        Assert.Equal(RecordFile.Content(hello.Record), File.ReadAllBytes(Path.Combine(ro, h)));

        // a read-only old file moved aside, then the package fails on the second file: the old one is back, read-only, and nothing else is there
        string rb = Path.Combine(_dir, "readonly-held");
        Directory.CreateDirectory(rb);
        File.WriteAllText(Path.Combine(rb, h), "old hello");
        File.WriteAllText(Path.Combine(rb, c), "old clap");
        File.SetAttributes(Path.Combine(rb, h), FileAttributes.ReadOnly);
        using (new FileStream(Path.Combine(rb, c), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(rb, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [hello, clap], overwrite: true));
            Assert.Contains("Nothing was left there", ex.Message);
        }
        Assert.Equal(new[] { h, c }, Entries(rb));
        Assert.Equal("old hello", File.ReadAllText(Path.Combine(rb, h)));
        Assert.True(File.GetAttributes(Path.Combine(rb, h)).HasFlag(FileAttributes.ReadOnly));
        foreach (var x in new[] { ro, rb }) foreach (var fx in Directory.GetFiles(x)) File.SetAttributes(fx, FileAttributes.Normal);

        // a second package into the same folder: refused unless with overwrite; the earlier package's file stays beside it
        string two = Path.Combine(_dir, "two");
        RecordFileWriter.Write(two, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [hello]);
        RecordFileWriter.Write(two, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [clap]);
        Assert.Contains("exists already", Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(two, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [clap])).Message);
        var second = RecordFileWriter.Write(two, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [clap], overwrite: true);
        Assert.Single(second.Files);
        Assert.Equal(new[] { c, h }.Order(StringComparer.Ordinal), Entries(two));
    }

    /// <summary>The folders the animation datafile's record files go into: a folder of another index first, then both of the game's.</summary>
    [Fact]
    public void TheDatafileFoldersAreNamedByBothIndexes()
    {
        var other = RecordFile.DatafileFolders("51_-_MIS_Y2E4_Katya_Maksimov.data");
        Assert.Equal(["51_-_MIS_Y2E4_Katya_Maksimov.data", "46_-_MIS_Y2E4_Katya_Maksimov.data", "24_-_MIS_Y2E4_Katya_Maksimov.data"], other.Select(x => x.Folder));
        Assert.Equal(["46_-_MIS_Y2E4_Katya_Maksimov.data", "24_-_MIS_Y2E4_Katya_Maksimov.data"], RecordFile.DatafileFolders("46_-_MIS_Y2E4_Katya_Maksimov.data").Select(x => x.Folder));
        Assert.Equal("MIS_Y2E4_Katya_Maksimov", RecordFile.DatafileNameOf("24_-_MIS_Y2E4_Katya_Maksimov.data"));
        Assert.Null(RecordFile.DatafileNameOf("x"));
    }

    /// <summary>An extra file that can't be moved into place (its name held by an open file) rolls the whole package back: the record
    /// file as it was, the extra as it was, nothing new left.</summary>
    [Fact]
    public void AnExtraThatFailsRollsThePackageBack()
    {
        var (hello, _) = TwoItems();
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        string dir = Path.Combine(_dir, "extra-held");
        RecordFileWriter.Write(dir, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [hello], [("a.checks.txt", [1, 2])]);
        var before = Directory.GetFiles(dir).Order(StringComparer.Ordinal).Select(f => (Path.GetFileName(f), Convert.ToHexString(File.ReadAllBytes(f)))).ToList();
        using (new FileStream(Path.Combine(dir, "a.checks.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(dir, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game],
                [hello], [("a.checks.txt", [9])], overwrite: true));
            Assert.Contains("Nothing was left there", ex.Message);
            Assert.False(ex.FilesWritten);
        }
        Assert.Equal(before, Directory.GetFiles(dir).Order(StringComparer.Ordinal).Select(f => (Path.GetFileName(f), Convert.ToHexString(File.ReadAllBytes(f)))).ToList());
    }

    /// <summary>A path with a folder name ending in a space or a dot is refused before any folder is made.</summary>
    [Fact]
    public void AFolderNameEndingInASpaceOrADotIsRefused()
    {
        var (hello, _) = TwoItems();
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        var before = Entries(_dir);
        foreach (var bad in new[] { Path.Combine(_dir, "trail ", "o4"), Path.Combine(_dir, "trail.", "o4"), Path.Combine(_dir, "o5 "), Path.Combine(_dir, "o6.") })
        {
            var ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(bad, "x", [game], [hello]));
            Assert.Contains("ending in a space or a dot", ex.Message);
        }
        Assert.Equal(before, Entries(_dir));
        // "." and ".." are a path's own parts, not names
        RecordFileWriter.Write(Path.Combine(_dir, ".", "fine", "..", "fine2"), "x", [game], [hello]);
        Assert.True(File.Exists(Path.Combine(_dir, "fine2", "102_-_mca_emote_Hello.Animation")));
    }
}
