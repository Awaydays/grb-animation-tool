using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>The feet: the pieces chosen where the feet match, the legs bent so a planted foot stays put through the tool's blends (or
/// steps where it must move), the feet slide check's limits, and the pieces sharing as little source as their lengths allow. On the
/// small synthetic body, standing on its feet (ankles 10 cm up, toes 2 cm).</summary>
public sealed class FeetTests
{
    static readonly Skeleton S = Humanoid.Skeleton();
    static readonly int Hips = Humanoid.Bone(S, "Hips"), Reference = Humanoid.Bone(S, "Reference"), Spine = Humanoid.Bone(S, "Spine");
    static readonly Feet.Leg Right = Feet.Legs(S).Single(l => l.Side == "Right");

    static Transform[] Rest() => S.RestLocals();

    static Vector3 Ankle(Transform[] l)
    {
        var m = new Transform[S.Bones.Count];
        S.ComputeModel(l, m);
        return m[Right.Ankle].Position;
    }

    static Quaternion FootRot(Transform[] l)
    {
        var m = new Transform[S.Bones.Count];
        S.ComputeModel(l, m);
        return m[Right.Ankle].Rotation;
    }

    /// <summary>Standing with the knees a little bent (the hips 5 cm down, the feet where they stand at rest), lowered by a further
    /// <paramref name="downCm"/>, the right foot put <paramref name="forwardCm"/> ahead, by the leg IK (so the pose is a real one: the
    /// bones keep their lengths; the rest pose's straight legs could reach no farther).</summary>
    static Transform[] Pose(float downCm, float forwardCm)
    {
        var l = Rest();
        var rest = Ankle(l); var rot = FootRot(l);
        l[Hips] = l[Hips] with { Position = l[Hips].Position - new Vector3(0, 0, (5 + downCm) / 100) };
        Assert.True(Feet.Reach(S, l, Right, rest + new Vector3(0, forwardCm / 100, 0), rot));
        var left = Feet.Legs(S).Single(g => g.Side == "Left");
        var lm = new Transform[S.Bones.Count]; S.ComputeModel(Rest(), lm);
        Assert.True(Feet.Reach(S, l, left, lm[left.Ankle].Position, lm[left.Ankle].Rotation));
        return l;
    }

    static Motion Still(int last, Transform[] pose) => Humanoid.Motion(S, last, (_, l) => Array.Copy(pose, l, pose.Length));

    static double Worst3d(Motion m, int from, int to, Vector3 at)
    {
        double w = 0;
        for (int t = from; t <= to; t++) w = Math.Max(w, (m.World(t, Right.Ankle).Position - at).Length() * 100);
        return w;
    }

    [Fact]
    public void TheLegIkReachesAndNeverStretches()
    {
        var l = Rest();
        var a = Ankle(l);
        var target = a + new Vector3(0.05f, 0.1f, 0.15f);
        Assert.True(Feet.Reach(S, l, Right, target, FootRot(Rest())));
        Assert.InRange((Ankle(l) - target).Length() * 100, 0, 0.01);
        // the bones keep their lengths
        var m = new Transform[S.Bones.Count]; S.ComputeModel(l, m);
        Assert.Equal(0.45, (m[Right.Knee].Position - m[Right.Hip].Position).Length(), 4);
        Assert.Equal(0.45, (m[Right.Ankle].Position - m[Right.Knee].Position).Length(), 4);
        // out of reach: refused as reached, stops short on the line from the hip, still no bone longer
        var far = Rest();
        Assert.False(Feet.Reach(S, far, Right, a - new Vector3(0, 0, 0.5f), FootRot(Rest())));
        S.ComputeModel(far, m);
        Assert.Equal(0.45, (m[Right.Ankle].Position - m[Right.Knee].Position).Length(), 4);
        Assert.InRange((m[Right.Ankle].Position - m[Right.Hip].Position).Length(), 0.899, 0.9);
    }

    /// <summary>A line-up from a deep crouch into standing, the feet where they are in both: the plain blend bends the legs half way and
    /// takes the feet more than 3 cm off their place (the control); kept planted, they stay within 1 cm.</summary>
    [Fact]
    public void APlantedFootStaysWithinACentimetreDuringABlend()
    {
        var crouch = Pose(35, 0);
        var stand = Still(80, Pose(0, 0));
        var at = stand.World(0, Right.Ankle).Position;
        FitResult Run(bool keep) => ClipFit.Apply(stand, 80, new FitOptions { Mode = FitMode.Cut, SharedPose = crouch, LoopFrames = 30, KeepFeet = keep });
        double kept = Worst3d(Run(true).Motion, 0, 30, at), plain = Worst3d(Run(false).Motion, 0, 30, at);
        Assert.InRange(kept, 0, 1);
        Assert.True(plain > 3, $"the control moves the foot {plain:F2} cm: the blend isn't shown to move it");
        Assert.Contains(Run(true).Notes, n => n.Contains("right foot kept planted"));
    }

    /// <summary>A line-up from a pose with the right foot 12 cm ahead: blended, it slides there along the ground (the control, a big
    /// difference); kept planted, it steps (lifted and put down), and the feet slide check says OK.</summary>
    [Fact]
    public void AFootThatMustMoveFarStepsInsteadOfSliding()
    {
        var ahead = Pose(0, 12);
        var stand = Still(80, Pose(0, 0));
        FitResult Run(bool keep) => ClipFit.Apply(stand, 80, new FitOptions { Mode = FitMode.Cut, SharedPose = ahead, LoopFrames = 30, KeepFeet = keep });
        var kept = Run(true); var plain = Run(false);
        // lined up: blended from the pose at the start, into it at the end
        Assert.Equal([(0, 30), (50, 80)], kept.Blends);
        var c = Feet.Check(kept.Motion, kept.Blends)!;
        var p = Feet.Check(plain.Motion, plain.Blends)!;
        Assert.Equal(CheckLevel.Pass, c.Level);
        Assert.Equal(CheckLevel.Fail, p.Level);
        Assert.Contains(kept.Notes, n => n.Contains("right foot steps 12.0 cm"));
        // the step lifts the foot clear of the ground
        double top = Enumerable.Range(0, 31).Max(t => kept.Motion.World(t, Right.Ankle).Position.Z);
        Assert.True(top > 0.1f + 0.036f, $"the foot rises to {top * 100:F1} cm only");
    }

    /// <summary>A line-up from the same nearly straight-legged stance 35 cm aside, blended slowly enough that the feet count as planted:
    /// the first foot finds no time for its step within its leg's reach while the other is down, so it is left as the blend has it, said
    /// in the notes, never bent part way and snapped straight; the other steps alone. A metre aside, a step is longer than the tool makes,
    /// and both legs are left, the notes saying why. The control: the 12 cm step above is taken.</summary>
    [Fact]
    public void AFootTheLegCantReachIsLeftToTheBlend()
    {
        (FitResult Kept, FitResult Plain) Run(float aside, float downCm)
        {
            var pose = Pose(downCm, 0);
            pose[Reference] = pose[Reference] with { Position = pose[Reference].Position + new Vector3(aside, 0, 0) };
            var stand = Still(500, Pose(downCm, 0));
            FitResult Fit(bool keep) => ClipFit.Apply(stand, 500, new FitOptions { Mode = FitMode.Cut, SharedPose = pose, LoopFrames = 240, KeepFeet = keep });
            return (Fit(true), Fit(false));
        }
        void LeftAlone(FitResult kept, FitResult plain, string side)
        {
            var leg = Feet.Legs(S).Single(l => l.Side == side);
            for (int t = 0; t <= 500; t++)
                foreach (int b in new[] { leg.Hip, leg.Knee, leg.Ankle }) Assert.Equal(plain.Motion.Local(t, b).Rotation, kept.Motion.Local(t, b).Rotation);
        }
        // 35 cm aside: the first foot's step finds no time in reach with the other down; the other then steps alone
        var (kept, plain) = Run(0.35f, -4);
        Assert.Contains(kept.Notes, n => n.Contains("right foot left as the blend has it at frames 0 to 240: the leg can't keep it planted there"));
        LeftAlone(kept, plain, "Right");
        Assert.Contains(kept.Notes, n => n.Contains("left foot steps 35.0 cm"));
        // a metre aside: a stride, not a step; both legs as the blend has them
        var (far, farPlain) = Run(1f, 0);
        foreach (var side in new[] { "Right", "Left" })
        {
            Assert.Contains(far.Notes, n => n.Contains($"{side.ToLowerInvariant()} foot left as the blend has it at frames 0 to 240: a step of 100.0 cm is longer than the tool makes (40 cm)"));
            LeftAlone(far, farPlain, side);
        }
    }


    /// <summary>The feet slide check: a planted foot sliding 1.5 cm is OK, 3 cm a note, 6 cm a big difference; lifted between, its
    /// travel isn't a slide.</summary>
    [Fact]
    public void TheFeetSlideCheckSaysNoteAndBigDifferenceAtItsLimits()
    {
        CheckLevel Slid(float cm, bool lifted = false)
        {
            var stand = Pose(0, 0);
            var m = Humanoid.Motion(S, 40, (t, l) =>
            {
                Array.Copy(stand, l, stand.Length);
                float u = Math.Clamp((t - 10) / 20f, 0, 1);
                var rest = Ankle(Rest());
                var to = rest + new Vector3(0, cm / 100 * u, lifted ? 0.08f * MathF.Sin(MathF.PI * u) : 0);
                Assert.True(Feet.Reach(S, l, Right, to, FootRot(Rest())));
            });
            return Feet.Check(m, [(0, 40)])!.Level;
        }
        Assert.Equal(CheckLevel.Pass, Slid(1.5f));
        Assert.Equal(CheckLevel.Warn, Slid(3));
        Assert.Equal(CheckLevel.Fail, Slid(6));
        Assert.Equal(CheckLevel.Pass, Slid(6, lifted: true));
        // nothing to measure: no blend and no next clip
        Assert.Null(Feet.Check(Still(10, Rest()), []));
    }

    /// <summary>The feet count in the cut's choice: a source whose body stands 10 cm aside in one stretch (its joints turned the same,
    /// so the pose distance alone can't tell, the control) gives each piece a start and an end in one stance.</summary>
    [Fact]
    public void ThePiecesAreChosenWhereTheFeetMatch()
    {
        var src = Humanoid.Motion(S, 240, (t, l) =>
        {
            if (t is >= 100 and < 140) l[Reference] = l[Reference] with { Position = new Vector3(0.10f, 0, 0) };
            l[Spine] = l[Spine] with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.01f * MathF.Sin(t * 0.05f)) };
        });
        // the control: a cut from the other stance to this one is no different to the pose distance
        Assert.InRange(ClipFit.PoseDistance(src, 110, 50), 0, 1.5);
        Assert.True(Feet.Distance(src, 110, src, 50) > 15);
        var (st, overlap) = ClipFit.SuggestPieces(src, [60, 60]);
        Assert.False(overlap);
        foreach (var s in st) Assert.InRange(Feet.Distance(src, s, src, s + 60), 0, 0.5);
        Assert.Equal(0, Feet.Distance(src, ClipFit.SuggestCut(src, 60)!.Value, src, ClipFit.SuggestCut(src, 60)!.Value + 60), 3);
        // against a pose standing aside, both ends of every piece in that stance
        var (st2, _) = ClipFit.SuggestPieces(src, [15, 15], (src, 120));
        foreach (var s in st2) Assert.True(s >= 100 && s + 15 < 140, $"a piece at {s} to {s + 15} leaves the stance of the pose");
    }

    // ---- the pieces: as little shared source as the lengths allow, none inside another ----

    static Motion Wavy(int last) => Humanoid.Motion(S, last, (t, l) =>
        l[Spine] = l[Spine] with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)((40 * Math.Sin(t * 0.37) + 3 * Math.Cos(t * 1.3)) * Math.PI / 180)) });

    /// <summary>Pieces longer together than the source: they share exactly their surplus, none lies inside another, and among every
    /// placement that does so theirs costs least (a brute force over all of them).</summary>
    [Fact]
    public void PiecesLongerThanTheSourceShareOnlyTheirSurplus()
    {
        var src = Wavy(100);
        int[] lasts = [40, 30, 50];
        var (st, overlap) = ClipFit.SuggestPieces(src, lasts);
        Assert.True(overlap);
        Assert.Equal(20, ClipFit.SharedTicks(st, lasts));
        Assert.False(ClipFit.AnyInside(st, lasts));
        double Cost(int i, int s) => ClipFit.MatchDistance(src, s, src, s + lasts[i]);
        double best = double.MaxValue;
        for (int a = 0; a <= 60; a++)
            for (int b = 0; b <= 70; b++)
                for (int c = 0; c <= 50; c++)
                {
                    int[] p = [a, b, c];
                    if (ClipFit.SharedTicks(p, lasts) != 20 || ClipFit.AnyInside(p, lasts)) continue;
                    best = Math.Min(best, Cost(0, a) + Cost(1, b) + Cost(2, c));
                }
        Assert.Equal(best, Cost(0, st[0]) + Cost(1, st[1]) + Cost(2, st[2]), 3);
        // the control: each piece at its own best start, kept apart, shares more
        Assert.True(ClipFit.SharedTicks([1385, 1068, 222, 709], [136, 718, 754, 924]) == 968);
        // a piece as long as the source: the others can't avoid lying inside it, and are placed anyway
        var (st3, _) = ClipFit.SuggestPieces(src, [100, 20]);
        Assert.Equal(0, st3[0]);
    }

    /// <summary>Pieces that all fit: none overlapping, in whichever order costs least (a brute force over every order and placement).</summary>
    [Fact]
    public void PiecesThatFitTakeTheBestOrder()
    {
        var src = Wavy(60);
        int[] lasts = [10, 12, 9];
        var (st, overlap) = ClipFit.SuggestPieces(src, lasts);
        Assert.False(overlap);
        Assert.Equal(0, ClipFit.SharedTicks(st, lasts));
        double Cost(int i, int s) => ClipFit.MatchDistance(src, s, src, s + lasts[i]);
        double best = double.MaxValue;
        for (int a = 0; a <= 50; a++)
            for (int b = 0; b <= 48; b++)
                for (int c = 0; c <= 51; c++)
                    if (ClipFit.SharedTicks([a, b, c], lasts) == 0) best = Math.Min(best, Cost(0, a) + Cost(1, b) + Cost(2, c));
        Assert.Equal(best, Cost(0, st[0]) + Cost(1, st[1]) + Cost(2, st[2]), 3);
    }
}
