using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>The fit of a motion of another length to a clip's: the cut's suggestion, the resample, the hold, the loop's blend and its
/// seam, the root's scale, the refusals, and the length always exact. On a small synthetic body: the spine turns by a known angle a tick.</summary>
public sealed class ClipFitTests
{
    static readonly Skeleton S = Humanoid.Skeleton();
    static readonly int Spine = Humanoid.Bone(S, "Spine");

    /// <summary>A motion of <paramref name="last"/> ticks: the spine turned about z by <paramref name="deg"/>(t) degrees, the mover at
    /// <paramref name="moverCm"/>(t) centimetres along x.</summary>
    static Motion Of(int last, Func<int, double> deg, Func<int, double>? moverCm = null)
    {
        var locals = new Transform[last + 1][];
        var mover = new Transform[last + 1];
        for (int t = 0; t <= last; t++)
        {
            var l = S.RestLocals();
            l[Spine] = l[Spine] with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(deg(t) * Math.PI / 180)) };
            locals[t] = l;
            mover[t] = new Transform(new Vector3((float)((moverCm?.Invoke(t) ?? 0) / 100), 0, 0), Quaternion.Identity);
        }
        var rk = new bool[S.Bones.Count];
        rk[Spine] = true;
        return new Motion(S, locals, mover, rk, new bool[S.Bones.Count], hasMover: true);
    }

    static double SpineDeg(Motion m, int t)
    {
        var q = m.Local(t, Spine).Rotation;
        return 2 * Math.Atan2(q.Z, q.W) * 180 / Math.PI;
    }

    [Fact]
    public void TheCutsSuggestionIsWhereTheLoopClosesBest()
    {
        // every frame's angle is its tick, but frame 50 stands at 10 degrees: only a cut from 10 to 50 starts and ends on one pose
        var m = Of(100, t => t == 50 ? 10 : t);
        Assert.Equal(10, ClipFit.SuggestCut(m, 40));
        Assert.Equal(0, ClipFit.PoseDistance(m, 10, 50), 6);
        Assert.True(ClipFit.PoseDistance(m, 11, 51) > 10);
        // the control: without the matching frame no start closes the loop, and the first of equals is taken
        Assert.Equal(0, ClipFit.SuggestCut(Of(100, t => t), 40));
        // a source shorter than the clip gives no cut
        Assert.Null(ClipFit.SuggestCut(Of(30, t => t), 40));
    }

    [Fact]
    public void TheHipsHeightCountsInThePoseDistance()
    {
        int hips = Humanoid.Bone(S, "Hips");
        var a = Of(60, _ => 0);
        var locals = Enumerable.Range(0, 61).Select(t => Enumerable.Range(0, S.Bones.Count).Select(b => a.Local(t, b)).ToArray()).ToArray();
        locals[30][hips] = locals[30][hips] with { Position = locals[30][hips].Position + new Vector3(0, 0, 0.05f) };
        var m = new Motion(S, locals, Enumerable.Range(0, 61).Select(a.Mover).ToArray(), a.RotationKeyed, a.PositionKeyed, true);
        Assert.Equal(5 * ClipFit.HeightWeight, ClipFit.PoseDistance(m, 0, 30), 3);
        Assert.Equal(0, ClipFit.PoseDistance(m, 0, 40), 6);
    }

    [Fact]
    public void ACutPlaysTheSourceFromItsStartAndKeepsTheMoverWhereTheSourceStarts()
    {
        var f = ClipFit.Apply(Of(100, t => t, t => t), 40, new FitOptions { Mode = FitMode.Cut, CutStart = 10 });
        Assert.Equal(40, f.Motion.LastTick);
        Assert.Equal(10, SpineDeg(f.Motion, 0), 3);
        Assert.Equal(50, SpineDeg(f.Motion, 40), 3);
        // the cut's mover starts at the source's first place and travels as the source did from the cut
        Assert.Equal(0, f.Motion.Mover(0).Position.X * 100, 3);
        Assert.Equal(40, f.Motion.Mover(40).Position.X * 100, 3);
        Assert.False(f.PlaysAtOtherSpeed);
    }

    [Fact]
    public void SpeedResamplesTheWholeSourceToTheClipsLength()
    {
        var f = ClipFit.Apply(Of(120, t => t), 60, new FitOptions { Mode = FitMode.Speed });
        Assert.Equal(60, f.Motion.LastTick);
        for (int t = 0; t <= 60; t += 7) Assert.Equal(2 * t, SpineDeg(f.Motion, t), 2);
        Assert.Equal(120, SpineDeg(f.Motion, 60), 2);
        Assert.True(f.PlaysAtOtherSpeed);
        // slower: 30 ticks over 60, a frame between two of the source's lies halfway
        var slow = ClipFit.Apply(Of(30, t => t), 60, new FitOptions { Mode = FitMode.Speed });
        Assert.Equal(0.5, SpineDeg(slow.Motion, 1), 2);
        Assert.Equal(30, SpineDeg(slow.Motion, 60), 2);
    }

    [Fact]
    public void HoldPadsWithTheLastPoseOrCutsTheEnd()
    {
        var pad = ClipFit.Apply(Of(30, t => t), 60, new FitOptions { Mode = FitMode.Hold });
        Assert.Equal(60, pad.Motion.LastTick);
        Assert.Equal(20, SpineDeg(pad.Motion, 20), 3);
        for (int t = 30; t <= 60; t++) Assert.Equal(30, SpineDeg(pad.Motion, t), 3);
        var cut = ClipFit.Apply(Of(90, t => t), 60, new FitOptions { Mode = FitMode.Hold });
        Assert.Equal(60, cut.Motion.LastTick);
        Assert.Equal(60, SpineDeg(cut.Motion, 60), 3);
    }

    [Fact]
    public void TheLoopsBlendEndsOnTheFirstFramesPoseWithNoJumpAtItsStart()
    {
        var src = Of(60, t => t);
        var f = ClipFit.Apply(src, 60, new FitOptions { Loop = true, LoopFrames = 30 });
        Assert.Equal(31, f.BlendFrom);
        // the seam: the last frame is the first's pose, so the loop restarts where it ends
        Assert.Equal(SpineDeg(f.Motion, 0), SpineDeg(f.Motion, 60), 3);
        // before the blend nothing changes; through it no step is larger than the source's own plus the blend's share of the way back
        for (int t = 0; t <= 30; t++) Assert.Equal(t, SpineDeg(f.Motion, t), 3);
        double worst = 0;
        for (int t = 30; t < 60; t++) worst = Math.Max(worst, Math.Abs(SpineDeg(f.Motion, t + 1) - SpineDeg(f.Motion, t)));
        Assert.True(worst <= 1 + 60.0 / 30 + 0.01, $"a step of {worst} deg in the blend");
        // the control: without the loop the seam jumps by the whole move
        Assert.Equal(60, Math.Abs(SpineDeg(ClipFit.Apply(src, 60, new FitOptions()).Motion, 60) - SpineDeg(src, 0)), 3);
    }

    [Fact]
    public void ARootThatMovesIsLeftOutOfTheBlendAndAScaleOfZeroStaysInPlace()
    {
        var walking = Of(60, _ => 0, t => 2 * t);
        var f = ClipFit.Apply(walking, 60, new FitOptions { Loop = true });
        Assert.True(f.RootMoves);
        Assert.Equal(120, f.Motion.Mover(60).Position.X * 100, 2);
        Assert.Contains(f.Notes, n => n.Contains("isn't blended", StringComparison.Ordinal));

        var still = ClipFit.Apply(walking, 60, new FitOptions { RootScale = 0, Loop = true });
        Assert.False(still.RootMoves);
        for (int t = 0; t <= 60; t++) Assert.Equal(0, still.Motion.Mover(t).Position.X, 6);
        var half = ClipFit.Apply(walking, 60, new FitOptions { RootScale = 0.5 });
        Assert.Equal(60, half.Motion.Mover(60).Position.X * 100, 2);
    }

    [Fact]
    public void ACutThatDoesntFitIsRefused()
    {
        Assert.Throws<ArgumentException>(() => ClipFit.Apply(Of(30, t => t), 60, new FitOptions { Mode = FitMode.Cut }));
        Assert.Throws<ArgumentException>(() => ClipFit.Apply(Of(100, t => t), 60, new FitOptions { Mode = FitMode.Cut, CutStart = 41 }));
        Assert.Throws<ArgumentException>(() => ClipFit.Apply(Of(100, t => t), 60, new FitOptions { Mode = FitMode.Cut, CutStart = -1 }));
        Assert.Throws<ArgumentException>(() => ClipFit.Apply(Of(100, t => t), 60, new FitOptions { Mode = FitMode.Speed, RootScale = -1 }));
        // the last start that fits does
        Assert.Equal(60, ClipFit.Apply(Of(100, t => t), 60, new FitOptions { Mode = FitMode.Cut, CutStart = 40 }).Motion.LastTick);
    }

    [Theory]
    [InlineData(10, 60)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(500, 37)]
    [InlineData(2, 718)]
    public void TheResultAlwaysLastsExactlyTheClipsLength(int sourceLast, int last)
    {
        foreach (var mode in Enum.GetValues<FitMode>())
        {
            if (mode == FitMode.Cut && sourceLast < last) continue;
            foreach (bool loop in new[] { false, true })
                Assert.Equal(last, ClipFit.Apply(Of(sourceLast, t => t % 90), last, new FitOptions { Mode = mode, Loop = loop }).Motion.LastTick);
        }
    }

    [Fact]
    public void TheFitsWordsSayWhatWasDone()
    {
        Assert.Equal("cut from 0.5 s, seamless loop 0.5 s", new FitOptions { CutStart = 30, Loop = true }.Words(2194, 718));
        Assert.Equal("speed 1.5x, root motion 0x", new FitOptions { Mode = FitMode.Speed, RootScale = 0 }.Words(90, 60));
        Assert.Equal("last pose held", new FitOptions { Mode = FitMode.Hold }.Words(30, 60));
        Assert.Equal("", new FitOptions().Words(60, 60));
    }
}
