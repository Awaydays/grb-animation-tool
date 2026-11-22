using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Retiming;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;
using static GrbAnimTool.Tests.EditSynthetic;

namespace GrbAnimTool.Tests;

/// <summary>The edits on clips built by hand, with known answers, and every refusal path.</summary>
public sealed class EditUnitTests
{
    static readonly uint E4 = EditTracks.FirstPlantSlot, E5 = EditTracks.FirstPlantSlot + 1;

    static ClipRecord Parse(byte[] b) => ClipRecord.Parse(b);

    static EditRefusal Refused(Func<object> f) => Assert.Throws<EditRefusedException>(f).Kind;

    /// <summary>A plant key as written (its point held in mm) against the one expected.</summary>
    static void Plant(FootPlant want, FootPlant? got)
    {
        Assert.NotNull(got);
        Assert.Equal((want.Tick, want.LengthTicks), (got!.Value.Tick, got.Value.LengthTicks));
        Assert.True((want.Point - got.Value.Point).Length() < 0.0006f, $"point {got.Value.Point} against {want.Point}");
    }

    static void Plants(FootPlant[] want, Curve c)
    {
        Assert.Equal(want.Length, (int)c.Keys);
        for (int k = 0; k < want.Length; k++) Plant(want[k], CurveDecoder.Plant(c, k));
    }

    // ---------------------------------------------------------------------------------------------------------------- the time warp

    [Fact]
    public void TrimWarpShiftsAndItsInverseUndoesIt()
    {
        var w = TimeWarp.Trim(60, 10, 40);
        Assert.Equal(30, w.NewLast);
        Assert.Equal(10, w.SourceOf(0)); Assert.Equal(40, w.SourceOf(30));
        Assert.Equal(5, w.Inverse(15), 9); Assert.Equal(-5, w.Inverse(5), 9); Assert.Equal(35, w.Inverse(45), 9);
        Assert.Equal(5, w.FirstReaching(15)); Assert.Equal(0, w.FirstReaching(3)); Assert.Equal(-1, w.FirstReaching(3, clampBefore: false));
        Assert.False(w.IsIdentity);
        Assert.True(TimeWarp.Trim(60, 0, 60).IsIdentity);
        Assert.Throws<ArgumentException>(() => TimeWarp.Trim(60, 30, 30));
        Assert.Throws<ArgumentException>(() => TimeWarp.Trim(60, -1, 30));
        Assert.Throws<ArgumentException>(() => TimeWarp.Trim(60, 0, 61));
    }

    [Fact]
    public void SpeedWarpScalesAndEndsOnTheLastTick()
    {
        var w = TimeWarp.Speed(60, 2);
        Assert.Equal(30, w.NewLast);
        Assert.Equal(20, w.SourceOf(10), 9); Assert.Equal(60, w.SourceOf(30), 9);
        Assert.Equal(10, w.Inverse(20), 9); Assert.Equal(7.5, w.Inverse(15), 9);
        Assert.Equal(8, w.FirstReaching(15));
        var slow = TimeWarp.Speed(100, 0.8);
        Assert.Equal(125, slow.NewLast);
        Assert.Equal(100, slow.SourceOf(125), 9);
        Assert.True(TimeWarp.Speed(77, 1).IsIdentity);
        var list = TimeWarp.FromList([0, 0.5, 2, 2, 3], 3);
        Assert.Equal(2, list.FirstReaching(2)); Assert.Equal(1.5, list.Inverse(1.25), 9);
        Assert.Throws<ArgumentException>(() => TimeWarp.FromList([0, 2, 1], 2));
    }

    // ---------------------------------------------------------------------------------------------------------------- the curve re-timer

    static byte[][] Bytes(params int[] v) => v.Select(x => new[] { (byte)x }).ToArray();

    [Fact]
    public void SteppedCurveKeepsTheNearestEarlierKey()
    {
        var c = CurveWalker.Walk(Raw(13, [0, 10, 11, 20, 30], Bytes(1, 2, 3, 4, 5)), 0, 1, out _, out _)[0];
        // twice the speed: 10 -> 5, 11 -> 6 (5.5 reached first at tick 6), 20 -> 10, 30 -> 15
        var two = CurveWalker.Walk(CurveRetimer.Stepped(c, TimeWarp.Speed(30, 2), "x", 0)!, 0, 1, out _, out _)[0];
        Assert.Equal([0, 5, 6, 10, 15], CurveDecoder.KeyTicks(two));
        Assert.Equal([1, 2, 3, 4, 5], ScalarDecoder.Bytes(two)!.Select(b => (int)b));
        // three times: 10 -> 3.33 (tick 4), 11 -> 3.67 (tick 4 too: the later key's value), 20 -> 6.67 (tick 7), 30 -> 10
        var three = CurveWalker.Walk(CurveRetimer.Stepped(c, TimeWarp.Speed(30, 3), "x", 0)!, 0, 1, out _, out _)[0];
        Assert.Equal([0, 4, 7, 10], CurveDecoder.KeyTicks(three));
        Assert.Equal([1, 3, 4, 5], ScalarDecoder.Bytes(three)!.Select(b => (int)b));
        // a trim of 12 to 25: tick 0 takes the key at 11, 20 lands at 8, 30 is past the cut
        var trim = CurveWalker.Walk(CurveRetimer.Stepped(c, TimeWarp.Trim(30, 12, 25), "x", 0)!, 0, 1, out _, out _)[0];
        Assert.Equal([0, 8], CurveDecoder.KeyTicks(trim));
        Assert.Equal([3, 4], ScalarDecoder.Bytes(trim)!.Select(b => (int)b));
        Assert.Null(CurveRetimer.Stepped(c, TimeWarp.Speed(30, 1), "x", 0));
    }

    [Fact]
    public void RawFormsMoveByKeyTimeWithTheirBytes()
    {
        // form 14 (2-byte values, no reader) and form 18 (floats)
        var v14 = new[] { new byte[] { 1, 2 }, [3, 4], [5, 6] };
        var c = CurveWalker.Walk(Raw(14, [0, 20, 40], v14), 0, 1, out _, out _)[0];
        Assert.Equal(0x2A, c.Tag);
        var r = CurveWalker.Walk(CurveRetimer.Stepped(c, TimeWarp.Speed(40, 2), "x", 0)!, 0, 1, out _, out _)[0];
        Assert.Equal([0, 10, 20], CurveDecoder.KeyTicks(r));
        for (int k = 0; k < 3; k++) Assert.Equal(v14[k], ScalarDecoder.KeyRaw(r, k));
        var f = new[] { BitConverter.GetBytes(1.5f), BitConverter.GetBytes(-2f) };
        var cf = CurveWalker.Walk(Raw(18, [0, 300], f), 0, 1, out _, out _)[0];
        Assert.Equal(0x37, cf.Tag);                  // two-byte times past tick 255
        var rf = CurveWalker.Walk(CurveRetimer.Stepped(cf, TimeWarp.Speed(300, 2), "x", 0)!, 0, 1, out _, out _)[0];
        Assert.Equal(0x36, rf.Tag);                  // back to one-byte times
        Assert.Equal([1.5f, -2f], ScalarDecoder.Floats(rf)!);
    }

    [Fact]
    public void BitmapTimesAreConvertedToOneOrTwoByteForms()
    {
        // tag 0x29: a byte track with a bitmap of 2 bytes, keys at bits 0, 3 and 9, shift 1 (ticks 0, 6, 18)
        byte[] bm = [0x29, 0, 0, 0, 0, 3, 0, 0, 0, 2 | (1 << 6), 0b0000_1001, 0b0000_0010, 7, 8, 9];
        int head = 2 + 2;   // the bitmap's size in memory: 2 + its byte count
        BitConverter.GetBytes((uint)(head + 3)).CopyTo(bm, 1);
        var c = CurveWalker.Walk(bm, 0, 1, out _, out string stop)[0];
        Assert.Equal(CurveWalker.AtEnd, stop);
        Assert.Equal([0, 6, 18], CurveDecoder.KeyTicks(c));
        var r = CurveWalker.Walk(CurveRetimer.Stepped(c, TimeWarp.Speed(18, 2), "x", 0)!, 0, 1, out _, out _)[0];
        Assert.Equal(0x27, r.Tag);
        Assert.Equal([0, 3, 9], CurveDecoder.KeyTicks(r));
        Assert.Equal([7, 8, 9], ScalarDecoder.Bytes(r)!.Select(b => (int)b));
    }

    [Fact]
    public void AnUndecodedCurveOf256KeysIsRefusedAndABytesCurveRekeyed()
    {
        var ticks = Enumerable.Range(0, 300).ToArray();
        var raw = CurveWalker.Walk(Raw(14, ticks, ticks.Select(t => new[] { (byte)t, (byte)(t >> 8) }).ToArray()), 0, 1, out _, out _)[0];
        Assert.Equal(EditRefusal.TooManyKeys, Refused(() => CurveRetimer.Stepped(raw, TimeWarp.Speed(299, 0.8), "x", 3)!));
        var bytes = CurveWalker.Walk(Raw(13, ticks, Bytes(ticks.Select(t => t % 7).ToArray())), 0, 1, out _, out _)[0];
        var notes = new List<string>();
        var r = CurveWalker.Walk(CurveRetimer.Stepped(bytes, TimeWarp.Speed(299, 0.8), "x", 3, notes)!, 0, 1, out _, out _)[0];
        Assert.True(r.Keys <= 255);
        Assert.Contains(notes, n => n.Contains("keying rule"));
    }

    [Fact]
    public void InterpolatedCurveKeepsItsOwnKeysAndAddsTheCuts()
    {
        var p = CurveWalker.Walk(CurveWriter.PosCurve([0, 30, 60], [Vector3.Zero, new(0, 3, 0), new(0, 6, 0)]), 0, 1, out _, out _)[0];
        var two = CurveWalker.Walk(CurveRetimer.Interpolated(p, TimeWarp.Speed(60, 2))!, 0, 1, out _, out _)[0];
        Assert.Equal([0, 15, 30], CurveDecoder.KeyTicks(two));
        Assert.Equal(new Vector3(0, 3, 0), CurveDecoder.KeyPos(two, 1));
        var trim = CurveWalker.Walk(CurveRetimer.Interpolated(p, TimeWarp.Trim(60, 10, 40))!, 0, 1, out _, out _)[0];
        Assert.Equal([0, 20, 30], CurveDecoder.KeyTicks(trim));
        Assert.Equal(1f, CurveDecoder.KeyPos(trim, 0)!.Value.Y, 5);
        Assert.Equal(4f, CurveDecoder.KeyPos(trim, 2)!.Value.Y, 5);
        Assert.Null(CurveRetimer.Interpolated(p, TimeWarp.Trim(60, 0, 60)));
        // a step (two keys at one tick) stays a step where its tick plays exactly
        var q = CurveWalker.Walk(CurveWriter.RotCurve([0, 20, 20, 40], [Quaternion.Identity, Quaternion.Identity, RotX(30), RotX(30)]), 0, 1, out _, out _)[0];
        var qs = CurveWalker.Walk(CurveRetimer.Interpolated(q, TimeWarp.Speed(40, 2))!, 0, 1, out _, out _)[0];
        Assert.Equal([0, 10, 10, 20], CurveDecoder.KeyTicks(qs));
    }

    [Fact]
    public void PlantsAreRetimedCutAndStartAtTickZero()
    {
        var keys = new List<FootPlant> { new(0, 0, Vector3.Zero), new(20, 10, new(0.1f, 0.2f, 0)), new(40, 20, new(0.1f, 0.5f, 0)) };
        var c = CurveWalker.Walk(CurveWriter.PlantCurve(keys), 0, 1, out _, out _)[0];
        var s = CurveWalker.Walk(CurveRetimer.Plants(c, TimeWarp.Speed(60, 2), "x", 0)!, 0, 1, out _, out _)[0];
        Plants([new FootPlant(0, 0, Vector3.Zero), new(10, 5, new(0.1f, 0.2f, 0)), new(20, 10, new(0.1f, 0.5f, 0))], s);
        // a trim from 25: the first plant (20 to 30) starts at 0 and lasts 5; the second at 15
        var t = CurveWalker.Walk(CurveRetimer.Plants(c, TimeWarp.Trim(60, 25, 50), "x", 0)!, 0, 1, out _, out _)[0];
        Plants([new FootPlant(0, 5, new(0.1f, 0.2f, 0)), new(15, 10, new(0.1f, 0.5f, 0))], t);
    }

    [Fact]
    public void RawWriterLayoutsFitTheWalkersSizeRule()
    {
        foreach (int form in new[] { 13, 14, 15, 16, 18, 19 })
        {
            int e = CurveTags.ValueBytes((byte)(form * 3));
            foreach (int n in new[] { 1, 2, 7, 255, 256, 300 })
            {
                var ticks = Enumerable.Range(0, n).Select(i => i * (n > 200 ? 1 : 2)).ToArray();
                var b = Raw(form, ticks, ticks.Select(_ => new byte[e]).ToArray());
                var c = CurveWalker.Walk(b, 0, 1, out _, out string stop);
                Assert.Equal(CurveWalker.AtEnd, stop);
                Assert.Equal(n, (int)c[0].Keys);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- trim and speed

    const ulong Id = 0x0000012300000001;

    static byte[] RetimeClip(SharedCurveTable? table = null, byte[]? byteTrack = null, int[]? leaveOut = null) => Clip("retime", Id, 60,
    [
        (0u, CurveWriter.PosCurve([0, 60], [Vector3.Zero, new(0, 1.2f, 0)])),
        (H("Spine"), CurveWriter.RotCurve([0, 30, 60], [Quaternion.Identity, RotX(30), RotX(60)])),
        (0x6C8CB68C, byteTrack ?? Raw(13, [0, 10, 20, 30], Bytes(1, 2, 3, 4))),
        (0x11111111, Raw(14, [0, 30], [[1, 2], [3, 4]])),
        (0x6C8CB68C, Raw(18, [0, 45], [BitConverter.GetBytes(1f), BitConverter.GetBytes(2f)])),
        (E4, CurveWriter.PlantCurve([new(0, 0, Vector3.Zero), new(20, 10, new(0.1f, 0.3f, 0))])),
        (E5, CurveWriter.PlantCurve([new(0, 60, new(-0.1f, 0.3f, 0))])),
    ], stepTimes: [40, 80], leaveOut: leaveOut);

    [Fact]
    public void SpeedTwoHalvesEveryTime()
    {
        var c = Parse(RetimeClip());
        var r = RetimeEdit.Speed(c, Ctx(Body(), Free(Id)), 2);
        var n = r.Record;
        Assert.Equal(30, n.LastTick);
        Assert.Equal(0.5f, n.Duration);
        var t = Table();
        var bytes = Track(n, t, 0x6C8CB68C, TrackKind.Byte);
        Assert.Equal([0, 5, 10, 15], CurveDecoder.KeyTicks(bytes));
        Assert.Equal([0, 15, 30], CurveDecoder.KeyTicks(Track(n, t, H("Spine"), TrackKind.Rotation)));
        Assert.Equal([0, 15], CurveDecoder.KeyTicks(Track(n, t, 0x11111111, TrackKind.Byte)));
        var pl = Track(n, t, E4, TrackKind.Plant);
        Plant(new FootPlant(10, 5, new(0.1f, 0.3f, 0)), CurveDecoder.Plant(pl, 1));
        Assert.Equal(30, CurveDecoder.Plant(Track(n, t, E5, TrackKind.Plant), 0)!.Value.LengthTicks);
        Assert.Equal([20, 40], StepTimeList.Find(n)!.Times);
        Assert.True(EditRebuild.Check(r.Bytes, t).Why is null);
    }

    [Fact]
    public void TrimKeepsTimesAndCutsTheRest()
    {
        var c = Parse(RetimeClip());
        var r = RetimeEdit.Trim(c, Ctx(Body(), Free(Id)), 10, 40);
        var n = r.Record;
        var t = Table();
        Assert.Equal(30, n.LastTick);
        var bytes = Track(n, t, 0x6C8CB68C, TrackKind.Byte);
        Assert.Equal([0, 10, 20], CurveDecoder.KeyTicks(bytes));
        Assert.Equal([2, 3, 4], ScalarDecoder.Bytes(bytes)!.Select(b => (int)b));
        var mover = Track(n, t, 0, TrackKind.Position);
        Assert.Equal(0f, CurveDecoder.KeyPos(mover, 0)!.Value.Y, 5);                      // starts where the game's clip started
        Assert.Equal(0.6f, CurveValues.Pos(mover, CurveDecoder.KeyTicks(mover), 30).Y, 5);
        Assert.Equal([20, 60], StepTimeList.Find(n)!.Times);                               // 40 and 80 (1/120 s) less the cut's 20
        // a plant's point stands in the mover's frame at its start: one the cut keeps whole keeps its point, the rebase notwithstanding
        Plant(new FootPlant(10, 10, new(0.1f, 0.3f, 0)), CurveDecoder.Plant(Track(n, t, E4, TrackKind.Plant), 1));
        // one the cut starts (0 to 60, cut at 10) is read in the mover at the cut, which has gone 20 cm on: 20 cm further back
        Plant(new FootPlant(0, 30, new(-0.1f, 0.1f, 0)), CurveDecoder.Plant(Track(n, t, E5, TrackKind.Plant), 0));
        Assert.Contains(r.Notes, x => x.Contains("starts where the game's did"));
    }

    [Fact]
    public void SpeedOneAndTrimOfNothingGiveTheRecordBack()
    {
        var b = RetimeClip();
        var c = Parse(b);
        Assert.Equal(b, RetimeEdit.Speed(c, Ctx(Body(), Free(Id)), 1).Bytes);
        Assert.Equal(b, RetimeEdit.Trim(c, Ctx(Body(), Free(Id)), 0, 60).Bytes);
    }

    [Fact]
    public void RetimeRefusals()
    {
        var c = Parse(RetimeClip());
        var s = Body();
        Assert.Equal(EditRefusal.LengthHeld, Refused(() => RetimeEdit.Speed(c, Ctx(s, LengthHeld(Id)), 1.5)));
        Assert.Equal(EditRefusal.LengthHeld, Refused(() => RetimeEdit.Trim(c, Ctx(s, MotionField(Id)), 0, 30)));
        Assert.Equal(EditRefusal.GraphIncomplete, Refused(() => RetimeEdit.Speed(c, Ctx(s, Incomplete(Id)), 1.5)));
        Assert.Equal(EditRefusal.GraphIncomplete, Refused(() => RetimeEdit.Speed(c, new EditContext(s, Table(), null), 1.5)));
        Assert.Equal(EditRefusal.BadParameter, Refused(() => RetimeEdit.Speed(c, Ctx(s, Free(Id)), 9)));
        Assert.Equal(EditRefusal.BadParameter, Refused(() => RetimeEdit.Speed(c, Ctx(s, Free(Id)), double.NaN)));
        Assert.Equal(EditRefusal.BadParameter, Refused(() => RetimeEdit.Trim(c, Ctx(s, Free(Id)), 40, 20)));
        // a byte track held by the shared table becomes an inline curve: refused where the head has no kind 3
        var table = Table(Raw(13, [0, 10, 20, 30], Bytes(1, 2, 3, 4)));
        var noKind3 = Parse(RetimeClip(table, Ref(1), leaveOut: [3]));
        Assert.Null(ClipRebuild.ArenaValue(noKind3, 3));
        var e = Assert.Throws<EditRefusedException>(() => RetimeEdit.Speed(noKind3, Ctx(s, Free(Id), table), 2));
        Assert.Equal(EditRefusal.ArenaKindMissing, e.Kind);
        Assert.Contains("arena kind 3", e.Message);
        // an undecoded curve of 300 keys slowed down keeps 300 keys: refused
        var ticks = Enumerable.Range(0, 300).ToArray();
        var longClip = Parse(Clip("long", Id, 299, [(0x22222222u, Raw(15, ticks, Bytes(ticks.Select(t => t % 5).ToArray())))]));
        Assert.Equal(EditRefusal.TooManyKeys, Refused(() => RetimeEdit.Speed(longClip, Ctx(s, Free(Id)), 0.8)));
    }

    // ---------------------------------------------------------------------------------------------------------------- mirror

    static byte[] MirrorClip(params (uint, byte[])[] extra) => Clip("mirror", Id, 60,
    [
        (0u, CurveWriter.PosCurve([0, 60], [Vector3.Zero, new(1, 2, 0)])),
        (0u, CurveWriter.RotCurve([0, 60], [Quaternion.Identity, RotZ(30)])),
        (H("Reference"), CurveWriter.PosCurve([0, 30], [new(0, 0, 1), new(0.1f, 0, 1)])),
        (H("LeftArm"), CurveWriter.RotCurve([0, 30, 60], [Quaternion.Identity, RotX(20), RotZ(40)])),
        (H("RightArm"), CurveWriter.RotCurve([0], [Quaternion.Identity])),
        (H("Spine"), CurveWriter.RotCurve([0, 60], [Quaternion.Identity, RotZ(10)])),
        (E4, CurveWriter.PlantCurve([new(0, 60, new(-0.1f, 0.2f, 0))])),
        (E5, CurveWriter.PlantCurve([new(0, 0, Vector3.Zero), new(20, 40, new(0.12f, 0.5f, 0))])),
        .. extra,
    ]);

    [Fact]
    public void MirrorSwapsSidesAndReflectsTheMover()
    {
        var c = Parse(MirrorClip());
        var t = Table();
        var r = MirrorEdit.Apply(c, Ctx(Body(), Free(Id)));
        var n = r.Record;
        var mp = Track(n, t, 0, TrackKind.Position);
        Assert.Equal(new Vector3(-1, 2, 0), CurveDecoder.KeyPos(mp, 1));
        var mr = Track(n, t, 0, TrackKind.Rotation);
        Assert.True(CurveValues.Deg(CurveDecoder.KeyRot(mr, 1)!.Value, RotZ(-30)) < 0.01, $"mover turn {CurveDecoder.KeyRot(mr, 1)} tag 0x{mr.Tag:X2} keys {mr.Keys}");
        Assert.Equal(-0.1f, CurveDecoder.KeyPos(Track(n, t, H("Reference"), TrackKind.Position), 1)!.Value.X, 5);
        var right = Track(n, t, H("RightArm"), TrackKind.Rotation);
        Assert.Equal([0, 30, 60], CurveDecoder.KeyTicks(right));
        Assert.True(CurveValues.Deg(CurveDecoder.KeyRot(right, 1)!.Value, RotX(20)) < 0.01);
        Assert.True(CurveValues.Deg(CurveDecoder.KeyRot(right, 2)!.Value, RotZ(-40)) < 0.01);
        Assert.Equal(1u, Track(n, t, H("LeftArm"), TrackKind.Rotation).Keys);
        Assert.True(CurveValues.Deg(CurveDecoder.KeyRot(Track(n, t, H("Spine"), TrackKind.Rotation), 1)!.Value, RotZ(-10)) < 0.01);
        // slots swapped, lateral sign flipped
        Plant(new FootPlant(0, 60, new(0.1f, 0.2f, 0)), CurveDecoder.Plant(Track(n, t, E5, TrackKind.Plant), 0));
        Plant(new FootPlant(20, 40, new(-0.12f, 0.5f, 0)), CurveDecoder.Plant(Track(n, t, E4, TrackKind.Plant), 1));
        Assert.Contains(r.Notes, x => x.Contains("tracks whose meaning isn't known") || x.Contains("foot plants mirrored"));
    }

    [Fact]
    public void MirrorTwiceIsTheClip()
    {
        var b = MirrorClip();
        var t = Table();
        var s = Body();
        var once = MirrorEdit.Apply(Parse(b), Ctx(s, Free(Id)));
        var twice = MirrorEdit.Apply(once.Record, Ctx(s, Free(Id)));
        var a = Parse(b); var z = twice.Record;
        for (int i = 0; i < a.Tracks.Count; i++)
        {
            var u = SharedCurveTable.Resolve(a.Curves[i], t)!; var v = SharedCurveTable.Resolve(z.Curves[i], t)!;
            Assert.Equal(CurveDecoder.KeyTicks(u), CurveDecoder.KeyTicks(v));
            for (int k = 0; k < u.Keys; k++)
            {
                if (u.Kind == TrackKind.Rotation) Assert.True(CurveValues.Deg(CurveDecoder.KeyRot(u, k)!.Value, CurveDecoder.KeyRot(v, k)!.Value) < 0.02, $"track {i} key {k}: {CurveDecoder.KeyRot(u, k)} against {CurveDecoder.KeyRot(v, k)}");
                if (u.Kind == TrackKind.Position) Assert.True((CurveDecoder.KeyPos(u, k)!.Value - CurveDecoder.KeyPos(v, k)!.Value).Length() < 1e-6);
                if (u.Kind == TrackKind.Plant) Assert.Equal(CurveDecoder.Plant(u, k), CurveDecoder.Plant(v, k));
            }
        }
    }

    /// <summary>Two keys at one tick (a step) keep their own values through the mirror: the mover's place and a bone's turn.</summary>
    [Fact]
    public void MirrorKeepsBothKeysOfAStep()
    {
        var t = Table();
        var c = Parse(Clip("step", Id, 60,
        [
            (0u, CurveWriter.PosCurve([0, 30, 30, 60], [Vector3.Zero, new(0, 1, 0), new(1, 1, 0), new(1, 2, 0)])),
            (H("LeftArm"), CurveWriter.RotCurve([0, 20, 20, 40], [Quaternion.Identity, Quaternion.Identity, RotX(30), RotX(30)])),
            (H("RightArm"), CurveWriter.RotCurve([0], [Quaternion.Identity])),
        ]));
        var n = MirrorEdit.Apply(c, Ctx(Body(), Free(Id))).Record;
        var mp = Track(n, t, 0, TrackKind.Position);
        Assert.Equal([0, 30, 30, 60], CurveDecoder.KeyTicks(mp));
        Assert.Equal(new Vector3(0, 1, 0), CurveDecoder.KeyPos(mp, 1));
        Assert.Equal(new Vector3(-1, 1, 0), CurveDecoder.KeyPos(mp, 2));
        var right = Track(n, t, H("RightArm"), TrackKind.Rotation);
        Assert.Equal([0, 20, 20, 40], CurveDecoder.KeyTicks(right));
        Assert.True(CurveValues.Deg(CurveDecoder.KeyRot(right, 1)!.Value, Quaternion.Identity) < 0.01, $"key 1 {CurveDecoder.KeyRot(right, 1)}");
        Assert.True(CurveValues.Deg(CurveDecoder.KeyRot(right, 2)!.Value, RotX(30)) < 0.01, $"key 2 {CurveDecoder.KeyRot(right, 2)}");
    }

    [Fact]
    public void MirrorRefusals()
    {
        var s = Body();
        var c = Parse(MirrorClip());
        Assert.Equal(EditRefusal.MotionField, Refused(() => MirrorEdit.Apply(c, Ctx(s, MotionField(Id)))));
        Assert.Equal(EditRefusal.GraphIncomplete, Refused(() => MirrorEdit.Apply(c, Ctx(s, Incomplete(Id)))));
        Assert.Equal(EditRefusal.GraphIncomplete, Refused(() => MirrorEdit.Apply(c, new EditContext(s, Table(), null))));
        foreach (uint hand in EditTracks.Hand)
        {
            var armed = Parse(MirrorClip((hand, CurveWriter.PosCurve([0], [Vector3.Zero]))));
            Assert.Equal(EditRefusal.HandTracks, Refused(() => MirrorEdit.Apply(armed, Ctx(s, Free(Id)))));
        }
        // a moving left hand whose counterpart has no track
        var lone = Parse(MirrorClip((H("LeftHand"), CurveWriter.RotCurve([0, 30], [Quaternion.Identity, RotX(45)]))));
        Assert.Equal(EditRefusal.Unpaired, Refused(() => MirrorEdit.Apply(lone, Ctx(s, Free(Id)))));
        // one foot's plant slot alone
        var onePlant = Parse(Clip("one", Id, 60, [(0u, CurveWriter.PosCurve([0], [Vector3.Zero])), (E4, CurveWriter.PlantCurve([new(0, 60, new(0.1f, 0, 0))]))]));
        Assert.Equal(EditRefusal.Unpaired, Refused(() => MirrorEdit.Apply(onePlant, Ctx(s, Free(Id)))));
        // plants held by the shared table become inline: refused where the head has no kind 9
        var table = Table(CurveWriter.PlantCurve([new(0, 60, new(0.1f, 0.2f, 0))]), CurveWriter.PlantCurve([new(0, 60, new(-0.1f, 0.2f, 0))]));
        var shared = Parse(Clip("shared", Id, 60, [(0u, CurveWriter.PosCurve([0], [Vector3.Zero])), (E4, Ref(1)), (E5, Ref(1))]));
        Assert.Equal(EditRefusal.ArenaKindMissing, Refused(() => MirrorEdit.Apply(shared, Ctx(s, Free(Id), table))));
        // a clip of byte channels only (a face's, say): no bone or mover track, nothing to mirror, said so
        var face = Assert.Throws<EditRefusedException>(() => MirrorEdit.Apply(Parse(Clip("face", Id, 60, [(0x22222222u, Raw(13, [0, 30], Bytes(1, 2))), (E4, CurveWriter.PlantCurve([new(0, 60, Vector3.Zero)])), (E5, CurveWriter.PlantCurve([new(0, 60, Vector3.Zero)]))])), Ctx(s, Free(Id))));
        Assert.Equal(EditRefusal.NoMotion, face.Kind);
        Assert.Contains("no bone or mover track", face.Message);
    }

    /// <summary>The mirror writes no curve of 256 keys or more: a clip where a curve it rewrites would hold that many is refused, with the
    /// plain reason; a long curve the mirror leaves as it is (a turn about the side axis mirrors to itself) doesn't refuse the clip.</summary>
    [Fact]
    public void MirrorRefusesALongCurveItWouldRewrite()
    {
        var ticks = Enumerable.Range(0, 300).ToArray();
        byte[] Long(Func<int, Quaternion> q) => Clip("long", Id, 299, [(0u, CurveWriter.PosCurve([0], [Vector3.Zero])), (H("Spine"), CurveWriter.RotCurve(ticks, ticks.Select(q).ToList()))]);
        var e = Assert.Throws<EditRefusedException>(() => MirrorEdit.Apply(Parse(Long(x => RotZ(x * 0.1f))), Ctx(Body(), Free(Id))));
        Assert.Equal(EditRefusal.LongCurve, e.Kind);
        Assert.Contains("this clip holds curves of 256 keys or more; the tool doesn't write such curves until one is seen playing", e.Message);
        var kept = MirrorEdit.Apply(Parse(Long(x => RotX(x * 0.1f))), Ctx(Body(), Free(Id)));
        Assert.Equal(300u, Track(kept.Record, Table(), H("Spine"), TrackKind.Rotation).Keys);
        Assert.Equal(0, kept.Rebuilt.Rewritten);
    }

    /// <summary>A plant's point stands in the mover's frame at its start: on a clip whose mover starts off the origin and turned, the mirror
    /// negates the point's x in that frame, and the mirrored point stands in the world where the counterpart's point stood, reflected through
    /// the mover's first place.</summary>
    [Fact]
    public void MirrorPlantsStandInTheMoversFrame()
    {
        var s = Body();
        var t = Table();
        var c = Parse(Clip("offset", Id, 60,
        [
            (0u, CurveWriter.PosCurve([0, 60], [new(0.5f, 0.3f, 0), new(1, 2, 0)])),
            (0u, CurveWriter.RotCurve([0, 60], [RotZ(40), RotZ(70)])),
            (H("LeftArm"), CurveWriter.RotCurve([0, 30], [Quaternion.Identity, RotX(20)])),
            (H("RightArm"), CurveWriter.RotCurve([0], [Quaternion.Identity])),
            (E4, CurveWriter.PlantCurve([new(0, 60, new(-0.1f, 0.2f, 0))])),
            (E5, CurveWriter.PlantCurve([new(0, 0, Vector3.Zero), new(20, 40, new(0.12f, 0.5f, 0))])),
        ]));
        var m = new SkeletonMirror(s);
        var r = MirrorEdit.Apply(c, Ctx(s, Free(Id)), m);
        Plant(new FootPlant(0, 60, new(0.1f, 0.2f, 0)), CurveDecoder.Plant(Track(r.Record, t, E5, TrackKind.Plant), 0));
        Plant(new FootPlant(20, 40, new(-0.12f, 0.5f, 0)), CurveDecoder.Plant(Track(r.Record, t, E4, TrackKind.Plant), 1));
        var before = ClipMotion.Sample(c, s, t).Mover; var after = ClipMotion.Sample(r.Record, s, t).Mover;
        var game = before[20].Apply(new Vector3(0.12f, 0.5f, 0));
        var mirrored = after[20].Apply(CurveDecoder.Plant(Track(r.Record, t, E4, TrackKind.Plant), 1)!.Value.Point);
        Assert.True((mirrored - m.PointInFrame(before[0], game)).Length() < 0.002f, $"{mirrored} against {m.PointInFrame(before[0], game)}");
    }

    /// <summary>A clip on the turned body: Reference turns the rest into the clip frame (+90 deg about z) and sways; the mover walks forward
    /// and to the right while turning; the left arm and leg move, the right ones stand at their rest.</summary>
    static byte[] TurnedClip(bool referenceTurn = true) => Clip("turned", Id, 60,
    [
        (0u, CurveWriter.PosCurve([0, 30, 60], [Vector3.Zero, new(0.2f, 0.6f, 0), new(0.5f, 1.2f, 0)])),
        (0u, CurveWriter.RotCurve([0, 60], [Quaternion.Identity, RotZ(-25)])),
        .. referenceTurn ? new[] { (H("Reference"), CurveWriter.RotCurve([0, 30, 60], [RotZ(90), RotZ(90) * RotX(8), RotZ(95)])) } : [],
        (H("Reference"), CurveWriter.PosCurve([0, 30], [new(0, 0, 1), new(0.05f, 0.02f, 0.98f)])),
        (H("LeftArm"), CurveWriter.RotCurve([0, 30, 60], [Quaternion.Identity, RotX(30), RotZ(40) * RotX(10)])),
        (H("RightArm"), CurveWriter.RotCurve([0], [Quaternion.Identity])),
        (H("LeftUpLeg"), CurveWriter.RotCurve([0, 40], [RotX(-10), RotX(25)])),
        (H("RightUpLeg"), CurveWriter.RotCurve([0], [Quaternion.Identity])),
        (H("Spine"), CurveWriter.RotCurve([0, 60], [Quaternion.Identity, RotZ(15) * RotX(5)])),
        (E4, CurveWriter.PlantCurve([new(0, 60, new(-0.1f, 0.2f, 0))])),
        (E5, CurveWriter.PlantCurve([new(0, 0, Vector3.Zero), new(20, 40, new(0.12f, 0.5f, 0))])),
    ]);

    [Fact]
    public void MirrorOnARestTurnedAgainstTheClipFrameIsAReflectionInTheWorld()
    {
        var s = TurnedBody();
        var t = Table();
        var m = new SkeletonMirror(s);
        Assert.Equal(SkeletonMirror.ClipLateralAxis, m.Axis);
        Assert.Equal((1, 2, 0), (m.RestLateralAxis, m.RestUpAxis, m.RestForwardAxis));
        Assert.True(CurveValues.Deg(m.RestTurn, RotZ(90)) < 1e-3, $"rest turn {m.RestTurn}");
        Assert.Equal(s.IndexOf(H("RightHand")), m.Pair[s.IndexOf(H("LeftHand"))]);
        var c = Parse(TurnedClip());
        var r = MirrorEdit.Apply(c, Ctx(s, Free(Id)), m);
        var (cm, deg, mcm, mdeg) = MirrorWorldError(c, r.Record, s, t, m.Pair, RotZ(90));
        Assert.True(cm < 0.05 && deg < 0.05 && mcm < 0.01 && mdeg < 0.02, $"{cm} cm, {deg} deg; mover {mcm} cm, {mdeg} deg");
        // the mover's travel: sideways negated, forward kept; its turn reversed; the plants by x sign and slot swap
        Assert.Equal(new Vector3(-0.5f, 1.2f, 0), CurveDecoder.KeyPos(Track(r.Record, t, 0, TrackKind.Position), 2));
        Assert.True(CurveValues.Deg(CurveDecoder.KeyRot(Track(r.Record, t, 0, TrackKind.Rotation), 1)!.Value, RotZ(25)) < 0.02);
        Plant(new FootPlant(0, 60, new(0.1f, 0.2f, 0)), CurveDecoder.Plant(Track(r.Record, t, E5, TrackKind.Plant), 0));
        // twice is the clip
        var twice = MirrorEdit.Apply(r.Record, Ctx(s, Free(Id)), m);
        var o = ClipMotion.Sample(c, s, t); var z = ClipMotion.Sample(twice.Record, s, t);
        for (int tick = 0; tick <= 60; tick++)
        {
            var a = o.World(tick); var b = z.World(tick);
            for (int i = 0; i < a.Length; i++) Assert.True((a[i].Position - b[i].Position).Length() < 2e-4f, $"tick {tick} bone {i}");
        }
        // the same body with its rest in the clip frame gives the same mirror: the turn is found, not assumed
        var plain = new SkeletonMirror(Body());
        Assert.True(CurveValues.Deg(plain.RestTurn, Quaternion.Identity) < 1e-3);
        var pc = Parse(MirrorClip());
        var pr = MirrorEdit.Apply(pc, Ctx(Body(), Free(Id)), plain);
        var pe = MirrorWorldError(pc, pr.Record, Body(), t, plain.Pair, Quaternion.Identity);
        Assert.True(pe.Cm < 0.05 && pe.Deg < 0.05 && pe.MoverCm < 0.01 && pe.MoverDeg < 0.02, $"{pe}");
    }

    [Fact]
    public void MirrorRefusesARootWhoseRestIsntInTheClipFrame()
    {
        // with no turn on Reference, its rest stands in the skeleton's frame: the mirror would need a rotation track the clip lacks
        var e = Assert.Throws<EditRefusedException>(() => MirrorEdit.Apply(Parse(TurnedClip(referenceTurn: false)), Ctx(TurnedBody(), Free(Id))));
        Assert.Equal(EditRefusal.Unpaired, e.Kind);
        Assert.Contains("Reference", e.Message);
    }

    [Fact]
    public void MirrorOnAnUndecodedLoneTrackIsARefusal()
    {
        // a rotation of form 2 (3-byte values, not decoded) on a bone whose counterpart has no track, and a position of a form with no reader
        var rot = CurveWriter.CurveN(0x06, 1, [0, 30], [new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 }]);
        var b = Clip("probe", Id, 60, [(0u, CurveWriter.PosCurve([0, 60], [Vector3.Zero, new(1, 2, 0)])), (H("LeftHand"), rot)]);
        var c = Parse(b);
        Assert.True(c.WalkedToEnd, c.Stop);
        var ex = Record.Exception(() => MirrorEdit.Apply(c, Ctx(Body(), Free(Id))));
        Assert.IsType<EditRefusedException>(ex);
        Assert.Equal(EditRefusal.Unreadable, ((EditRefusedException)ex).Kind);
    }

    [Fact]
    public void MirrorWarnsOfTracksShapedLikeAnIkOffset()
    {
        uint odd = 0x4DE55211;
        var c = Parse(MirrorClip((odd, Raw(13, [0, 30], Bytes(1, 0))), (odd, CurveWriter.PosCurve([0], [new(0.1f, 0, 0)])), (odd, CurveWriter.RotCurve([0], [Quaternion.Identity]))));
        var r = MirrorEdit.Apply(c, Ctx(Body(), Free(Id)));
        Assert.Contains(r.Warnings, w => w.Contains("IK offset") && w.Contains("4DE55211"));
        Assert.DoesNotContain(MirrorEdit.Apply(Parse(MirrorClip()), Ctx(Body(), Free(Id))).Warnings, w => w.Contains("IK offset"));
    }

    /// <summary>The edits gate on the verdict's own answers: a clip only GR_Graph's list names, and a clip the read didn't look for, are
    /// refused by mirror, scale, trim and speed with the verdict's reason; a blend's child carries the verdict's warning once.</summary>
    [Fact]
    public void TheEditsUseTheVerdictsOwnAnswers()
    {
        var s = Body();
        var mirror = Parse(MirrorClip());
        var walk = Parse(WalkClip(0.15f));
        foreach (var (v, want) in new[] { (ListedOnly(Id), EditRefusal.ListedWithoutNodeUse), (Unscanned(Id), EditRefusal.GraphIncomplete) })
        {
            Assert.False(v.CanChangeMotion);
            Assert.False(v.CanChangeLength);
            foreach (var run in new Func<object>[]
            {
                () => MirrorEdit.Apply(mirror, Ctx(s, v)), () => RootScaleEdit.Apply(walk, Ctx(s, v), 0.5f),
                () => RetimeEdit.Trim(mirror, Ctx(s, v), 5, 50), () => RetimeEdit.Speed(mirror, Ctx(s, v), 1.5),
            })
            {
                var e = Assert.Throws<EditRefusedException>(run);
                Assert.Equal(want, e.Kind);
                Assert.Contains(e.Kind == EditRefusal.ListedWithoutNodeUse ? Core.Graph.ClipGraphVerdict.ListedWithoutNodeUseReason : "wasn't part of the graph read", e.Message);
            }
        }
        // a blend's child: the verdict's warning, once, on mirror, scale and speed; none on a trim (it keeps the pace)
        var blend = new Core.Graph.ClipGraphVerdict
        {
            ClipId = Id, GraphComplete = true, Scanned = true,
            Uses = [new Core.Graph.ClipGraphUse(Id, Core.Graph.GraphUseKind.ClipNode, 0xDEA49FF5, 1, "doc", 0, null, false, false) { ParentClass = Core.Graph.GraphClasses.BlendChild }],
        };
        Assert.Single(MirrorEdit.Apply(mirror, Ctx(s, blend)).Warnings, w => w.StartsWith(blend.MotionWarning!, StringComparison.Ordinal));
        Assert.Single(RetimeEdit.Speed(mirror, Ctx(s, blend), 1.5).Warnings, w => w.StartsWith(blend.MotionWarning!, StringComparison.Ordinal));
        Assert.Single(RootScaleEdit.Apply(walk, Ctx(s, blend), 0.5f).Warnings, w => w.StartsWith(blend.MotionWarning!, StringComparison.Ordinal));
        Assert.Empty(RetimeEdit.Trim(mirror, Ctx(s, blend), 5, 50).Warnings);
        // a swap into a blend's child: the replace warning, which carries the blend's case, once and alone
        Assert.Equal([Core.Graph.ClipGraphVerdict.ReplaceBlendWarning], SwapEdit.Apply(mirror, mirror, Ctx(s, blend)).Warnings);
        // an equal swap takes the verdict's replace warning
        Assert.Contains(SwapEdit.Apply(mirror, mirror, Ctx(s, ListedOnly(Id))).Warnings, w => w == Core.Graph.ClipGraphVerdict.ListedWithoutNodeUseReason);
    }

    [Fact]
    public void SwapNeedsTheSameDurationExactly()
    {
        var s = Body();
        var a = Parse(MirrorClip());
        // 60.24 ticks rounds to the same 60, but the duration isn't the same
        var b = Parse(Clip("near", Id + 1, 60, [.. Enumerable.Range(0, a.Tracks.Count).Select(i => (a.Tracks[i], a.Bytes.AsSpan(a.Curves[i].At, a.Curves[i].DiskLength).ToArray()))], duration: 1.004f));
        Assert.Equal(a.LastTick, b.LastTick);
        Assert.Equal(EditRefusal.LengthsDiffer, Refused(() => SwapEdit.Apply(a, b, Ctx(s, Free(Id)))));
    }

    [Fact]
    public void TrimKeepsAMarkersNoTurn()
    {
        // a mover that walks and turns, and a ground marker whose fixed turn E is no turn: its turn undoes the mover's at every tick
        var mt = new[] { 0, 60 };
        var mp = new[] { Vector3.Zero, new Vector3(0, 1, 0) };
        var mq = new[] { Quaternion.Identity, RotZ(30) };
        var mover = Enumerable.Range(0, 61).Select(x => new Transform(Vector3.Lerp(mp[0], mp[1], x / 60f), Quaternion.Slerp(mq[0], mq[1], x / 60f))).ToArray();
        var ticks = Enumerable.Range(0, 61).ToArray();
        var c = Parse(Clip("marker", Id, 60,
        [
            (0u, CurveWriter.PosCurve(mt, mp)), (0u, CurveWriter.RotCurve(mt, mq)),
            (EditTracks.Marker, CurveWriter.PosCurve(ticks, ticks.Select(x => GroundMarker.At(mover, x, Quaternion.Identity).P).ToList())),
            (EditTracks.Marker, CurveWriter.RotCurve(ticks, ticks.Select(x => GroundMarker.At(mover, x, Quaternion.Identity).Q).ToList())),
        ]));
        var gm = GroundMarker.Of(c, Table())!;
        Assert.True(gm.Follows && gm.NoTurn && !gm.EndTurn, $"{gm.FitCm} cm {gm.FitDeg} deg");
        var r = RetimeEdit.Trim(c, Ctx(Body(), Free(Id)), 20, 60);
        var after = GroundMarker.Of(r.Record, Table())!;
        Assert.True(after.Follows, $"{after.FitCm} cm {after.FitDeg} deg");
        // turned with the mover's rebase, E would be the cut's 10 deg
        Assert.True(CurveValues.Deg(after.E, Quaternion.Identity) < 0.05, $"E {after.E}");
    }

    // ---------------------------------------------------------------------------------------------------------------- root-motion scale

    /// <summary>A clip whose mover creeps forward with the knees bent (thigh -20 deg, shin +40, foot -20, the hips 4 cm lower), so the toes
    /// stand at the floor and an ankle held back stays in the legs' reach.</summary>
    static byte[] WalkClip(float travel) => Clip("walk", Id, 60,
    [
        (0u, CurveWriter.PosCurve([0, 60], [Vector3.Zero, new(0, travel, 0)])),
        (0u, CurveWriter.RotCurve([0], [RotZ(5)])),
        (H("Reference"), CurveWriter.PosCurve([0], [new(0, 0, 0.9257f)])),
        (H("LeftUpLeg"), CurveWriter.RotCurve([0], [RotX(-20)])), (H("LeftLeg"), CurveWriter.RotCurve([0], [RotX(40)])),
        (H("LeftFoot"), CurveWriter.RotCurve([0], [RotX(-20)])), (H("RightUpLeg"), CurveWriter.RotCurve([0], [RotX(-20)])),
        (H("RightLeg"), CurveWriter.RotCurve([0], [RotX(40)])), (H("RightFoot"), CurveWriter.RotCurve([0], [RotX(-20)])),
        (E4, CurveWriter.PlantCurve([new(0, 0, Vector3.Zero), new(30, 30, new(-0.1f, 0.2f, 0))])),
        (E5, CurveWriter.PlantCurve([new(0, 60, new(0.1f, 0.1f, 0))])),
    ]);

    [Fact]
    public void ScaleMovesTheTravelAndHoldsTheFeet()
    {
        var s = Body();
        var t = Table();
        // 15 cm in a second: the toes move under 0.3 m/s, so both feet count as planted the whole clip
        var c = Parse(WalkClip(0.15f));
        var r = RootScaleEdit.Apply(c, Ctx(s, Free(Id)), 2f);
        var n = r.Record;
        Assert.Equal(new Vector3(0, 0.3f, 0), CurveDecoder.KeyPos(Track(n, t, 0, TrackKind.Position), 1));
        // the turn keeps its bytes (the 6-byte form's own rounding of 5 deg)
        Assert.Equal(c.Bytes.AsSpan(c.Curves[1].At, c.Curves[1].DiskLength).ToArray(), n.Bytes.AsSpan(n.Curves[1].At, n.Curves[1].DiskLength).ToArray());
        Assert.Equal(2, r.Plants.Count);
        Assert.Equal(15, r.UnlockedSlideCm, 1);
        Assert.True(r.ResidualSlideCm < 0.5, $"residual {r.ResidualSlideCm} cm");
        Assert.True(Track(n, t, H("LeftUpLeg"), TrackKind.Rotation).Keys > 1);
        // a plant's point stands in the mover's frame at its start tick: the left foot is held where it stood, the mover goes on 7.5 cm
        // further by tick 30, so the point is read 7.5 cm further back in the mover's turned frame, and stands in the world where it stood
        var p = CurveDecoder.Plant(Track(n, t, E4, TrackKind.Plant), 1)!.Value;
        Assert.Equal((30, 30), (p.Tick, p.LengthTicks));
        var want = new Vector3(-0.1f, 0.2f, 0) + Vector3.Transform(new Vector3(0, -0.075f, 0), Quaternion.Inverse(RotZ(5)));
        Assert.True((p.Point - want).Length() < 0.004f, $"point {p.Point} against {want}");
        var gameWorld = ClipMotion.Sample(c, s, t).Mover[30].Apply(new Vector3(-0.1f, 0.2f, 0));
        Assert.True((ClipMotion.Sample(n, s, t).Mover[30].Apply(p.Point) - gameWorld).Length() < 0.004f, "the point's place in the world moved");
        // the right foot's plant starts at tick 0, where the mover hasn't moved: kept
        Plant(new FootPlant(0, 60, new(0.1f, 0.1f, 0)), CurveDecoder.Plant(Track(n, t, E5, TrackKind.Plant), 0));
        Assert.Equal((15, 30), (Math.Round(r.TravelCm.Before, 3), Math.Round(r.TravelCm.After, 3)));
    }

    /// <summary>A factor read as a float and widened to a double (0.8 is 0.800000011920929 so) reads as it was typed in the edit's words
    /// and in its account, in the invariant culture.</summary>
    [Fact]
    public void AFactorReadsAsTyped()
    {
        var s = Body();
        var c = Parse(WalkClip(0.15f));
        var scaled = EditPackage.Run(c, Ctx(s, Free(Id)), "scale", out var d, factor: 1.1f);
        Assert.Equal("Root-motion scale 1.1x", d);
        Assert.Contains(scaled.Notes, n => n.Contains("cm scaled by 1.1 to ", StringComparison.Ordinal));
        EditPackage.Run(Parse(RetimeClip()), Ctx(s, Free(Id)), "speed", out d, factor: 1.1f);
        Assert.Equal("Speed 1.1x", d);
        Assert.Equal("Speed 0.8x", GrbAnimTool.Core.Library.ClipChange.Speed(0.8f).Words);
        Assert.Equal("0.8", EditPackage.FactorText(0.8f));
        Assert.Equal("1.25", EditPackage.FactorText(1.25));
        Assert.Equal("0.333", EditPackage.FactorText(1 / 3.0));
        Assert.Contains("a travel factor of 0.1 is outside 0.25 to 4", Assert.Throws<EditRefusedException>(() => RootScaleEdit.Apply(c, Ctx(s, Free(Id)), 0.1f)).Message);
    }

    /// <summary>A factor given with more than three decimals is taken to three, a half away from zero, as the screen takes it: the edit
    /// built is the one of the rounded factor, and its words name that factor.</summary>
    [Fact]
    public void AFactorOfMoreThanThreeDecimalsBuildsTheEditOfTheRoundedFactor()
    {
        var s = Body();
        var clip = Parse(RetimeClip());
        var given = EditPackage.Run(clip, Ctx(s, Free(Id)), "speed", out var d, factor: 1.2345);
        var rounded = EditPackage.Run(clip, Ctx(s, Free(Id)), "speed", out var dr, factor: 1.235);
        Assert.Equal("Speed 1.235x", d);
        Assert.Equal(dr, d);
        Assert.Equal(rounded.Bytes, given.Bytes);
        // the rounded factor is the one used: 0.2496, below the scale's least (refused as it is), is 0.25 (built)
        var walk = Parse(WalkClip(0.15f));
        Assert.Throws<EditRefusedException>(() => RootScaleEdit.Apply(walk, Ctx(s, Free(Id)), 0.2496f));
        Assert.Equal(EditPackage.Run(walk, Ctx(s, Free(Id)), "scale", out _, factor: 0.25).Bytes, EditPackage.Run(walk, Ctx(s, Free(Id)), "scale", out d, factor: 0.2496).Bytes);
        Assert.Equal("Root-motion scale 0.25x", d);
        var scaled = EditPackage.Run(Parse(WalkClip(0.15f)), Ctx(s, Free(Id)), "scale", out d, factor: 1.0625);
        Assert.Equal("Root-motion scale 1.063x", d);
        Assert.Equal(EditPackage.Run(Parse(WalkClip(0.15f)), Ctx(s, Free(Id)), "scale", out _, factor: 1.063).Bytes, scaled.Bytes);
        Assert.Equal(1.063, GrbAnimTool.Core.Library.ClipChange.RoundFactor(1.0625));
        Assert.Equal(-1.063, GrbAnimTool.Core.Library.ClipChange.RoundFactor(-1.0625));
    }

    /// <summary>Two keys of the mover at one tick (a step) keep their own places through the scale.</summary>
    [Fact]
    public void ScaleKeepsBothKeysOfAStep()
    {
        var c = Parse(Clip("step", Id, 60,
        [
            (0u, CurveWriter.PosCurve([0, 30, 30, 60], [Vector3.Zero, new(0, 0.1f, 0), new(0, 0.2f, 0), new(0, 0.3f, 0)])),
            (H("Reference"), CurveWriter.PosCurve([0], [new(0, 0, 0.9257f)])),
            (H("LeftUpLeg"), CurveWriter.RotCurve([0], [RotX(-20)])), (H("LeftLeg"), CurveWriter.RotCurve([0], [RotX(40)])),
            (H("LeftFoot"), CurveWriter.RotCurve([0], [RotX(-20)])), (H("RightUpLeg"), CurveWriter.RotCurve([0], [RotX(-20)])),
            (H("RightLeg"), CurveWriter.RotCurve([0], [RotX(40)])), (H("RightFoot"), CurveWriter.RotCurve([0], [RotX(-20)])),
        ]));
        var mover = Track(RootScaleEdit.Apply(c, Ctx(Body(), Free(Id)), 2f).Record, Table(), 0, TrackKind.Position);
        Assert.Equal([0, 30, 30, 60], CurveDecoder.KeyTicks(mover));
        Assert.True((CurveDecoder.KeyPos(mover, 1)!.Value - new Vector3(0, 0.2f, 0)).Length() < 1e-5f, $"key 1 {CurveDecoder.KeyPos(mover, 1)}");
        Assert.True((CurveDecoder.KeyPos(mover, 2)!.Value - new Vector3(0, 0.4f, 0)).Length() < 1e-5f, $"key 2 {CurveDecoder.KeyPos(mover, 2)}");
    }

    /// <summary>Curves the edits key again by the keying rule are named in the notes: how many, and the ticks between keys.</summary>
    [Fact]
    public void RekeyedCurvesAreNamedInTheNotes()
    {
        var s = Body();
        var ticks = Enumerable.Range(0, 300).ToArray();
        var longClip = Parse(Clip("long", Id, 299, [(0u, CurveWriter.PosCurve([0], [Vector3.Zero])), (H("Spine"), CurveWriter.RotCurve(ticks, ticks.Select(x => RotZ(x * 0.1f)).ToList()))]));
        var slow = RetimeEdit.Speed(longClip, Ctx(s, Free(Id)), 0.8);
        Assert.True(Track(slow.Record, Table(), H("Spine"), TrackKind.Rotation).Keys <= 255);
        Assert.Contains(slow.Notes, x => x.Contains("1 rotation and position curve") && x.Contains("more than 255 keys") && x.Contains("every 2 ticks"));
        // a clip whose curves stay within 255 keys says nothing of it
        Assert.DoesNotContain(RetimeEdit.Speed(Parse(RetimeClip()), Ctx(s, Free(Id)), 2).Notes, x => x.Contains("more than 255 keys"));
        // the scale keys the legs its foot lock turned by the keying rule
        var scaled = RootScaleEdit.Apply(Parse(WalkClip(0.15f)), Ctx(s, Free(Id)), 2f);
        Assert.Contains(scaled.Notes, x => x.Contains("6 leg curves") && x.Contains("every 2 ticks") && x.Contains("keying rule"));
    }

    /// <summary>The slide measure skips a span whose side has no foot bone in the skeleton.</summary>
    [Fact]
    public void ResidualSkipsASideWithNoFootBone()
    {
        var s = Skeleton.Parse(Synthetic.Skeleton([(H("Reference"), -1, new Vector3(0, 0, 1), Quaternion.Identity), (H("LeftToeBase"), 0, new Vector3(0, 0.1f, -1), Quaternion.Identity)]), BoneNames.Default());
        var m = ClipMotion.Sample(Parse(Clip("toe", Id, 10, [(0u, CurveWriter.PosCurve([0], [Vector3.Zero]))])), s, Table());
        Assert.Equal(0.0, RootScaleEdit.Residual(m, m, [("Left", 0, 10)]));
    }

    [Fact]
    public void ScaleOneIsTheClip()
    {
        var b = WalkClip(0.15f);
        Assert.Equal(b, RootScaleEdit.Apply(Parse(b), Ctx(Body(), Free(Id)), 1f).Bytes);
    }

    [Fact]
    public void ScaleRefusals()
    {
        var s = Body();
        var c = Parse(WalkClip(0.15f));
        Assert.Equal(EditRefusal.MotionField, Refused(() => RootScaleEdit.Apply(c, Ctx(s, MotionField(Id)), 0.5f)));
        Assert.Equal(EditRefusal.GraphIncomplete, Refused(() => RootScaleEdit.Apply(c, Ctx(s, Incomplete(Id)), 0.5f)));
        Assert.Equal(EditRefusal.BadParameter, Refused(() => RootScaleEdit.Apply(c, Ctx(s, Free(Id)), 0.1f)));
        Assert.Equal(EditRefusal.BadParameter, Refused(() => RootScaleEdit.Apply(c, Ctx(s, Free(Id)), float.PositiveInfinity)));
        Assert.Equal(EditRefusal.NoRootMotion, Refused(() => RootScaleEdit.Apply(Parse(WalkClip(0.001f)), Ctx(s, Free(Id)), 0.5f)));
        var noMover = Parse(Clip("still", Id, 60, [(H("Spine"), CurveWriter.RotCurve([0], [Quaternion.Identity]))]));
        Assert.Equal(EditRefusal.NoRootMotion, Refused(() => RootScaleEdit.Apply(noMover, Ctx(s, Free(Id)), 0.5f)));
        // the lock needs the legs' rotation tracks
        var noLegs = Parse(Clip("nolegs", Id, 60, [(0u, CurveWriter.PosCurve([0, 60], [Vector3.Zero, new(0, 0.15f, 0)]))]));
        Assert.Equal(EditRefusal.CantMove, Refused(() => RootScaleEdit.Apply(noLegs, Ctx(s, Free(Id)), 2f)));
    }

    // ---------------------------------------------------------------------------------------------------------------- swap

    [Fact]
    public void SwapCopiesEveryCurveAndKeepsTheTarget()
    {
        var t = Table();
        var target = Parse(MirrorClip());
        var source = Parse(Clip("source", Id + 1, 60,
        [
            (0u, CurveWriter.PosCurve([0, 60], [Vector3.Zero, new(3, 0, 0)])),
            (0u, CurveWriter.RotCurve([0], [Quaternion.Identity])),
            (H("Reference"), CurveWriter.PosCurve([0], [new(0, 0, 0.9f)])),
            (H("LeftArm"), CurveWriter.RotCurve([0], [RotX(5)])),
            (H("RightArm"), CurveWriter.RotCurve([0, 10, 20, 60], [Quaternion.Identity, RotX(1), RotX(2), RotX(3)])),
            (H("Spine"), CurveWriter.RotCurve([0], [Quaternion.Identity])),
            (E4, CurveWriter.PlantCurve([new(0, 60, new(-0.2f, 0, 0))])),
            (E5, CurveWriter.PlantCurve([new(0, 60, new(0.2f, 0, 0))])),
        ]));
        var r = SwapEdit.Apply(target, source, Ctx(Body(), Free(Id)));
        Assert.Equal(target.Name, r.Record.Name);
        Assert.Equal(target.Id, r.Record.Id);
        for (int i = 0; i < source.Curves.Count; i++)
            Assert.Equal(source.Bytes.AsSpan(source.Curves[i].At, source.Curves[i].DiskLength).ToArray(), r.Bytes.AsSpan(r.Record.Curves[i].At, r.Record.Curves[i].DiskLength).ToArray());
        Assert.Equal(target.Bytes, SwapEdit.Apply(target, target, Ctx(Body(), Free(Id))).Bytes);
        // a motion field's slot takes an equal swap, with a warning
        Assert.Contains(SwapEdit.Apply(target, source, Ctx(Body(), MotionField(Id))).Warnings, x => x.Contains("motion field"));
        Assert.Empty(SwapEdit.Apply(target, source, Ctx(Body(), Free(Id))).Warnings);
        // a blend's child: mirror, scale and speed carry the blend warning, a trim doesn't
        var blend = new Core.Graph.ClipGraphVerdict
        {
            ClipId = Id, GraphComplete = true, Scanned = true,
            Uses = [new Core.Graph.ClipGraphUse(Id, Core.Graph.GraphUseKind.ClipNode, 0xDEA49FF5, 1, "doc", 0, null, false, false) { ParentClass = EditVerdicts.BlendClass }],
        };
        Assert.Contains(MirrorEdit.Apply(target, Ctx(Body(), blend)).Warnings, x => x.Contains("blend"));
        Assert.Contains(RetimeEdit.Speed(target, Ctx(Body(), blend), 1.5).Warnings, x => x.Contains("blend"));
        Assert.Empty(RetimeEdit.Trim(target, Ctx(Body(), blend), 5, 50).Warnings);
        Assert.Contains(RootScaleEdit.Apply(Parse(WalkClip(0.15f)), Ctx(Body(), blend), 0.5f).Warnings, x => x.Contains("blend"));
        Assert.Equal(EditRefusal.GraphIncomplete, Refused(() => MirrorEdit.Apply(target, Ctx(Body(), new Core.Graph.ClipGraphVerdict { ClipId = Id, GraphComplete = true, Uses = [], Scanned = false }))));
        _ = t;
    }

    [Fact]
    public void SwapRefusals()
    {
        var s = Body();
        var target = Parse(MirrorClip());
        var shorter = Parse(Clip("short", Id + 1, 59, [(0u, CurveWriter.PosCurve([0], [Vector3.Zero]))]));
        Assert.Equal(EditRefusal.LengthsDiffer, Refused(() => SwapEdit.Apply(target, shorter, Ctx(s, Free(Id)))));
        var other = Parse(Clip("other", Id + 1, 60, [(0u, CurveWriter.PosCurve([0], [Vector3.Zero]))]));
        var e = Assert.Throws<EditRefusedException>(() => SwapEdit.Apply(target, other, Ctx(s, Free(Id))));
        Assert.Equal(EditRefusal.TrackListsDiffer, e.Kind);
        Assert.Contains("tracks", e.Message);
        // one kind differing (a rotation where the target has a position)
        var kinds = Parse(MirrorClip());
        var swapped = Parse(Clip("kinds", Id + 1, 60,
        [
            (0u, CurveWriter.RotCurve([0], [Quaternion.Identity])), (0u, CurveWriter.PosCurve([0], [Vector3.Zero])),
            (H("Reference"), CurveWriter.PosCurve([0], [new(0, 0, 1)])), (H("LeftArm"), CurveWriter.RotCurve([0], [Quaternion.Identity])),
            (H("RightArm"), CurveWriter.RotCurve([0], [Quaternion.Identity])), (H("Spine"), CurveWriter.RotCurve([0], [Quaternion.Identity])),
            (E4, CurveWriter.PlantCurve([new(0, 60, Vector3.Zero)])), (E5, CurveWriter.PlantCurve([new(0, 60, Vector3.Zero)])),
        ]));
        var k = Assert.Throws<EditRefusedException>(() => SwapEdit.Apply(kinds, swapped, Ctx(s, Free(Id))));
        Assert.Equal(EditRefusal.TrackListsDiffer, k.Kind);
        Assert.Contains("track 0", k.Message);
        // a copy that needs an arena kind the target's head lacks: the source's moving arm needs kind 5 (6-byte rotations, alignment 2)
        var noKind5 = Parse(Clip("nok5", Id, 60, [(H("LeftArm"), Ref(1))]));
        var table = Table(CurveWriter.RotCurve([0], [Quaternion.Identity]));
        var mover = Parse(Clip("src", Id + 1, 60, [(H("LeftArm"), CurveWriter.RotCurve([0, 60], [Quaternion.Identity, RotX(9)]))]));
        Assert.Equal(EditRefusal.ArenaKindMissing, Refused(() => SwapEdit.Apply(noKind5, mover, Ctx(s, Free(Id), table))));
        // and a copy that would leave a kind at 0: the target's own inline arm against the source's shared one
        var inline = Parse(Clip("inl", Id, 60, [(H("LeftArm"), CurveWriter.RotCurve([0, 60], [Quaternion.Identity, RotX(9)]))]));
        var sharedArm = Parse(Clip("shr", Id + 1, 60, [(H("LeftArm"), Ref(1))]));
        Assert.Equal(EditRefusal.ArenaKindToZero, Refused(() => SwapEdit.Apply(inline, sharedArm, Ctx(s, Free(Id), table))));
    }

    [Fact]
    public void UnreadableRecordsAreRefused()
    {
        var b = MirrorClip();
        var cut = Parse(b[..(b.Length - 3)]);
        var s = Body();
        Assert.Equal(EditRefusal.Unreadable, Refused(() => MirrorEdit.Apply(cut, Ctx(s, Free(Id)))));
        Assert.Equal(EditRefusal.Unreadable, Refused(() => RootScaleEdit.Apply(cut, Ctx(s, Free(Id)), 0.5f)));
        Assert.Equal(EditRefusal.Unreadable, Refused(() => RetimeEdit.Speed(cut, Ctx(s, Free(Id)), 1.5)));
        Assert.Equal(EditRefusal.Unreadable, Refused(() => SwapEdit.Apply(cut, cut, Ctx(s, Free(Id)))));
        Assert.Equal(EditRefusal.Unreadable, Refused(() => EditRebuild.Parse(b[..20])));
    }
}
