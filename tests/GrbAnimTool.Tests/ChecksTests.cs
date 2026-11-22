using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>The checks library on synthetic motions: each check passes on a plain motion, and warns and fails where it must.</summary>
public sealed class ChecksTests
{
    static readonly Skeleton S = Humanoid.Skeleton();
    static Motion Still(int last = 30) => Humanoid.Motion(S, last);

    [Fact]
    public void A_standing_skeleton_passes_every_body_check()
    {
        var m = Still();
        foreach (var r in MotionChecks.Body(m)) Assert.True(r.Level == CheckLevel.Pass, r.Line);
        foreach (var r in MotionChecks.Body(m, m)) Assert.True(r.Level == CheckLevel.Pass, r.Line);
        var ja = JointAngles.Measure(m)!;
        Assert.InRange(ja["Left knee"].Lo, -0.01f, 0.01f);
        Assert.InRange(ja["neck and head"].Hi, -0.01f, 0.01f);
        Assert.InRange(ja["Right ankle"].Hi, -0.01f, 0.01f);
    }

    [Fact]
    public void Feet_in_the_floor_warn_then_fail()
    {
        Motion Down(float cm) => Humanoid.Motion(S, 10, mover: t => new Transform(new Vector3(0, 0, t == 5 ? -cm / 100 : 0), Quaternion.Identity));
        Assert.Equal(CheckLevel.Pass, MotionChecks.FeetInFloor(Down(1)).Level);      // the toes at 2 cm go to 1 cm
        var w = MotionChecks.FeetInFloor(Down(5));                                    // -3 cm
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Equal(5, w.Tick);
        Assert.Contains("ToeBase", w.Bone);
        Assert.Equal(CheckLevel.Fail, MotionChecks.FeetInFloor(Down(10)).Level);     // -8 cm
        // as deep as the game's own clip goes: not held against it
        Assert.Equal(CheckLevel.Pass, MotionChecks.FeetInFloor(Down(10), Down(10)).Level);
    }

    /// <summary>A body raised off the floor where the game's clip stands: a few centimetres pass, more warn, a float fails; a game clip
    /// that is itself off the floor isn't held against it.</summary>
    [Fact]
    public void A_floating_body_warns_then_fails()
    {
        Motion Up(float cm, Func<int, bool>? at = null) => Humanoid.Motion(S, 20, mover: t => new Transform(new Vector3(0, 0, at is null || at(t) ? cm / 100 : 0), Quaternion.Identity));
        var game = Still(20);
        Assert.Equal(CheckLevel.Pass, MotionChecks.FeetOffTheFloor(Up(3), game).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.FeetOffTheFloor(Up(8), game).Level);
        var f = MotionChecks.FeetOffTheFloor(Up(100), game);
        Assert.Equal(CheckLevel.Fail, f.Level);
        Assert.Contains("floats", f.Message);
        // a hop of 25 cm for a few ticks where the game's feet stay down: the clip's lowest is on the floor, the ticks warn
        var hop = MotionChecks.FeetOffTheFloor(Up(25, t => t is >= 8 and <= 12), game);
        Assert.Equal(CheckLevel.Warn, hop.Level);
        Assert.InRange(hop.Tick!.Value, 8, 12);
        // the body raised 1 m and eased in and out, the feet down only at the clip's ends: the ticks fail; 40 cm fails, 20 warns
        Motion Eased(float cm) => Humanoid.Motion(S, 20, mover: t => new Transform(new Vector3(0, 0, cm / 100 * MathF.Sin(MathF.PI * t / 20)), Quaternion.Identity));
        var eased = MotionChecks.FeetOffTheFloor(Eased(100), game);
        Assert.Equal(CheckLevel.Fail, eased.Level);
        Assert.Equal(10, eased.Tick);
        Assert.Contains("floats", eased.Message);
        Assert.Equal(CheckLevel.Fail, MotionChecks.FeetOffTheFloor(Eased(40), game).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.FeetOffTheFloor(Eased(20), game).Level);
        Assert.Contains(MotionChecks.Body(Eased(100), game), r => r.Check == "feet off the floor" && r.Level == CheckLevel.Fail);
        // a jump clip whose own feet leave the floor for those ticks: the same jump, or a higher one there, isn't held against it
        var jump = Up(60, t => t is >= 6 and <= 14);
        Assert.Equal(CheckLevel.Pass, MotionChecks.FeetOffTheFloor(jump, jump).Level);
        Assert.Equal(CheckLevel.Pass, MotionChecks.FeetOffTheFloor(Up(100, t => t is >= 6 and <= 14), jump).Level);
        // but a float where the jump clip's feet are down still fails
        Assert.Equal(CheckLevel.Fail, MotionChecks.FeetOffTheFloor(Up(60, t => t is >= 2 and <= 4), jump).Level);
        // the game's own clip 1 m up (a climb): not compared
        Assert.Equal(CheckLevel.Pass, MotionChecks.FeetOffTheFloor(Up(100), Up(100)).Level);
        Assert.Equal(CheckLevel.Pass, MotionChecks.FeetOffTheFloor(Up(100), Up(90)).Level);
        Assert.Contains(MotionChecks.Body(Up(100), game), r => r.Check == "feet off the floor" && r.Level == CheckLevel.Fail);
    }

    /// <summary>A twist about a bone's own axis, which no joint angle sees: the allowed range is the wider of the game clip's own and the
    /// human range, so a head turned to look or a forearm rolled within the human range passes where the game's clip holds them still; the
    /// head turned round 180 degrees fails; a forearm rolled past the human range warns, then fails; the game's own twist is allowed.</summary>
    [Fact]
    public void A_twist_about_a_bones_own_axis_warns_then_fails()
    {
        // the head's own x runs up the head: a turn about it is the head's yaw
        Motion Yaw(float deg) => Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "Head", Vector3.UnitX, deg));
        var game = Still(20);
        Assert.Equal(CheckLevel.Pass, MotionChecks.Twist(Yaw(10), game).Level);
        Assert.Equal(CheckLevel.Pass, MotionChecks.Twist(Yaw(60), game).Level);      // within the head's 80
        Assert.Equal(CheckLevel.Warn, MotionChecks.Twist(Yaw(100), game).Level);     // 20 past
        var owl = MotionChecks.Twist(Yaw(180), game);
        Assert.Equal(CheckLevel.Fail, owl.Level);
        Assert.Equal("Head", owl.Bone);
        // the joint angles don't see it: the reason for this check
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Yaw(180), game).Level);
        Assert.Contains(MotionChecks.Body(Yaw(180), game), r => r.Check == "twists" && r.Level == CheckLevel.Fail);
        // the game's own clip turning the head as far allows it
        Assert.Equal(CheckLevel.Pass, MotionChecks.Twist(Yaw(180), Yaw(170)).Level);
        // without a game clip, the human range (the head 80 degrees)
        Assert.Equal(CheckLevel.Pass, MotionChecks.Twist(Yaw(70)).Level);
        Assert.Equal(CheckLevel.Fail, MotionChecks.Twist(Yaw(180)).Level);
        // a forearm rolled about its own length (towards the hand: down, -z): 70 degrees is within the human 90, 120 is 30 past (warn),
        // 150 is 60 past (fail); a swing of it isn't a twist
        Motion Roll(float deg) => Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "LeftForeArm", Vector3.UnitZ, t >= 10 ? deg : 0));
        Assert.Equal(CheckLevel.Pass, MotionChecks.Twist(Roll(70), game).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.Twist(Roll(120), game).Level);
        var roll = MotionChecks.Twist(Roll(150), game);
        Assert.Equal(CheckLevel.Fail, roll.Level);
        Assert.Equal("LeftForeArm", roll.Bone);
        Assert.Equal(CheckLevel.Pass, MotionChecks.Twist(Roll(150), Roll(150)).Level);
        Assert.Equal(CheckLevel.Pass, MotionChecks.Twist(Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "LeftArm", Vector3.UnitY, 50)), game).Level);
        Assert.Equal(180, Math.Abs(MotionChecks.TwistDeg(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI), Vector3.UnitX)), 3);
    }

    /// <summary>A turn split over several bones, each within its own range, is held to its chain's: the head turned right round as Neck 90
    /// and Head 90 fails (the neck and head, 90 either way), a torso turned 180 over the three spine bones fails (the spine, 60), about 350
    /// degrees from the hips to the head fails (120); natural turns pass, and so does the game clip's own chain turn.</summary>
    [Fact]
    public void A_turn_split_along_the_neck_or_the_spine_is_held_to_its_chain()
    {
        Motion Twisted(params (string Bone, float Deg)[] parts) => Humanoid.Motion(S, 20, (t, l) =>
        {
            float w = MathF.Sin(MathF.PI * t / 20);
            foreach (var (b, d) in parts) Humanoid.Turn(S, l, b, MotionChecks.TwistAxis(S, Humanoid.Bone(S, b)), w * d);
        });
        var game = Still(20);
        CheckResult Of(params (string, float)[] parts) => MotionChecks.Twist(Twisted(parts), game);
        // each bone within its own range (the neck 60, the head 80, a spine bone 45): only the chain sees it
        var owl = Of(("Neck", 55), ("Head", 75));
        Assert.Equal(CheckLevel.Warn, owl.Level);                                     // 130, 40 past the neck and head's 90
        var round = Of(("Neck", 90), ("Head", 90));
        Assert.Equal(CheckLevel.Fail, round.Level);
        Assert.Contains("the neck and head (Neck + Head)", round.Message);
        Assert.Equal(10, round.Tick);
        Assert.Equal(CheckLevel.Fail, Of(("Neck", 75), ("Head", 105)).Level);
        var torso = Of(("Spine", 60), ("Spine1", 60), ("Spine2", 60));
        Assert.Equal(CheckLevel.Fail, torso.Level);
        Assert.Contains("the spine (Spine + Spine1 + Spine2)", torso.Message);
        Assert.Equal(CheckLevel.Fail, Of(("Spine", 40), ("Spine1", 40), ("Spine2", 40)).Level);   // 120, 60 past
        var whole = Of(("Spine", 60), ("Spine1", 60), ("Spine2", 60), ("Neck", 75), ("Head", 95));
        Assert.Equal(CheckLevel.Fail, whole.Level);
        // the hips to the head: each bone within its range and each shorter chain at most 15 past its own (a pass), the whole past its own
        var hipsToHead = Of(("Spine", 25), ("Spine1", 25), ("Spine2", 25), ("Neck", 45), ("Head", 60));
        Assert.Equal(CheckLevel.Fail, hipsToHead.Level);                             // 180 against 120: 60 past
        Assert.Contains("the hips to the head", hipsToHead.Message);
        var lookBack = Of(("Spine", 20), ("Spine1", 20), ("Spine2", 20), ("Neck", 40), ("Head", 50));
        Assert.Equal(CheckLevel.Warn, lookBack.Level);                               // 150: 30 past
        Assert.Contains("the hips to the head", lookBack.Message);
        // natural turns: a look to the side, a torso turned 45, the two together 110
        Assert.Equal(CheckLevel.Pass, Of(("Neck", 30), ("Head", 50)).Level);
        Assert.Equal(CheckLevel.Pass, Of(("Spine", 15), ("Spine1", 15), ("Spine2", 15)).Level);
        Assert.Equal(CheckLevel.Pass, Of(("Spine", 15), ("Spine1", 15), ("Spine2", 15), ("Neck", 25), ("Head", 40)).Level);
        // the game's own clip turning the head as far round on the neck allows it; the other way round is still held to the human range
        Assert.Equal(CheckLevel.Pass, MotionChecks.Twist(Twisted(("Neck", 90), ("Head", 90)), Twisted(("Neck", 85), ("Head", 90))).Level);
        Assert.Equal(CheckLevel.Fail, MotionChecks.Twist(Twisted(("Neck", -90), ("Head", -90)), Twisted(("Neck", 85), ("Head", 90))).Level);
        // without a game clip, the human chain ranges alone
        Assert.Equal(CheckLevel.Fail, MotionChecks.Twist(Twisted(("Neck", 90), ("Head", 90))).Level);
        Assert.Contains(MotionChecks.Body(Twisted(("Neck", 90), ("Head", 90)), game), r => r.Check == "twists" && r.Level == CheckLevel.Fail);
    }

    /// <summary>The twist is split about the bone's own long axis from the game clip's pose at each tick: a pure bend added to a forearm
    /// that the game's clip holds swung out of its rest's plane adds no twist (split from the rest pose, the swing's path reads as one), and
    /// a roll added to the same pose still does.</summary>
    [Fact]
    public void A_pure_bend_added_to_the_games_pose_is_no_twist()
    {
        var s = Humanoid.Skeleton(elbowRestDeg: 25);
        int fa = Humanoid.Bone(s, "LeftForeArm"), arm = Humanoid.Bone(s, "LeftArm"), hand = Humanoid.Bone(s, "LeftHand");
        var axis = MotionChecks.TwistAxis(s, fa);
        // the game's clip: the forearm swung 50 degrees out to the side of the rest's bend
        var game = Humanoid.Motion(s, 20, (t, l) => Humanoid.Turn(s, l, "LeftForeArm", Vector3.UnitY, 50));
        // the edit: the elbow folded 110 degrees further about its hinge in the game's pose (perpendicular to the forearm's length)
        var ua = Vector3.Normalize(game.Model(0, fa).Position - game.Model(0, arm).Position);
        var fd = Vector3.Normalize(game.Model(0, hand).Position - game.Model(0, fa).Position);
        var hinge = Vector3.Normalize(Vector3.Transform(Vector3.Normalize(Vector3.Cross(ua, fd)), Quaternion.Inverse(game.Model(0, fa).Rotation)));
        Assert.Equal(0, Vector3.Dot(hinge, axis), 3);
        Motion Bent(float deg, float roll = 0) => Humanoid.Motion(s, 20, (t, l) =>
        {
            Humanoid.Turn(s, l, "LeftForeArm", Vector3.UnitY, 50);
            float w = MathF.Sin(MathF.PI * t / 20);
            Humanoid.Turn(s, l, "LeftForeArm", hinge, w * deg);
            Humanoid.Turn(s, l, "LeftForeArm", axis, w * roll);
        });
        var bent = Bent(110);
        // split from the rest, the bend reads as a large twist
        double fromRest = MotionChecks.TwistDeg(Quaternion.Inverse(s.Bones[fa].LocalRest.Rotation) * bent.Local(10, fa).Rotation, axis);
        Assert.True(Math.Abs(fromRest) > 30, $"the rest split reads {fromRest:F1} deg");
        var tw = MotionChecks.Twist(bent, game);
        Assert.Equal(CheckLevel.Pass, tw.Level);
        Assert.StartsWith("every twist and chain within", tw.Message);
        // a roll of 150 added to the same pose fails
        var rolled = MotionChecks.Twist(Bent(110, 150), game);
        Assert.Equal(CheckLevel.Fail, rolled.Level);
        Assert.Equal("LeftForeArm", rolled.Bone);
    }

    /// <summary>The elbow is signed against its own hinge: bent backwards through straight, past the game clip's own backward bend by more
    /// than 10 degrees warns, more than 25 fails; a fold forward is held to the human range as before.</summary>
    [Fact]
    public void An_elbow_bent_backwards_warns_then_fails()
    {
        // the game's clip holds the elbow bent forward 60 degrees; the edit opens it about the same hinge, eased in and out
        Motion Elbow(float back) => Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "LeftForeArm", Vector3.UnitX, 60 - back * MathF.Sin(MathF.PI * t / 20)));
        var game = Elbow(0);
        var ja = JointAngles.Measure(Elbow(90), game)!;
        Assert.Equal(-30, ja["Left elbow"].Lo, 1);
        Assert.Equal(60, ja["Left elbow"].Hi, 1);
        Assert.Equal(0, ja["Right elbow"].Lo, 1);                                     // the right arm hangs straight
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Elbow(60), game).Level);     // straight
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Elbow(68), game).Level);     // 8 backwards
        var w = MotionChecks.JointLimits(Elbow(75), game);                                   // 15 backwards
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Equal("Left elbow", w.Bone);
        Assert.Equal(10, w.Tick);
        var f = MotionChecks.JointLimits(Elbow(90), game);                                   // 30 backwards
        Assert.Equal(CheckLevel.Fail, f.Level);
        Assert.Contains("backwards", f.Message);
        Assert.Equal(CheckLevel.Fail, MotionChecks.JointLimits(Elbow(120), game).Level);
        // the game's own clip bending it 20 backwards: 5 past that passes, 30 past fails
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Elbow(85), Elbow(80)).Level);
        Assert.Equal(CheckLevel.Fail, MotionChecks.JointLimits(Elbow(110), Elbow(80)).Level);
        // folded forward past the human 150 fails as before
        Assert.Equal(CheckLevel.Fail, MotionChecks.JointLimits(Elbow(-110), game).Level);
    }

    /// <summary>The elbow's sign is read from the upper arm's hinge alone: an elbow opened backwards through straight with the forearm
    /// rolled about its own length as well (palm turned down, a real motion that turns the forearm's own copy of the hinge away) still
    /// reads backwards and fails.</summary>
    [Fact]
    public void An_elbow_bent_backwards_with_the_forearm_rolled_still_fails()
    {
        var along = MotionChecks.TwistAxis(S, Humanoid.Bone(S, "LeftForeArm"));
        Motion Elbow(float back, float roll) => Humanoid.Motion(S, 20, (t, l) =>
        {
            float w = MathF.Sin(MathF.PI * t / 20);
            Humanoid.Turn(S, l, "LeftForeArm", Vector3.UnitX, 60 - back * w);
            Humanoid.Turn(S, l, "LeftForeArm", along, roll * w);
        });
        var game = Elbow(0, 0);
        foreach (var roll in new[] { 0f, 60, 100, 120 })
        {
            var ja = JointAngles.Measure(Elbow(120, roll), game)!;
            Assert.True(ja["Left elbow"].Lo < -55, $"roll {roll}: the elbow reads {ja["Left elbow"].Lo:F0}");
            var f = MotionChecks.JointLimits(Elbow(120, roll), game);
            Assert.Equal(CheckLevel.Fail, f.Level);
            Assert.Contains("backwards", f.Message);
        }
        // the roll alone, or with the elbow folded further, isn't a backward bend
        Assert.True(JointAngles.Measure(Elbow(-30, 100), game)!["Left elbow"].Lo > 0);
    }

    /// <summary>A knee or an elbow bent sideways, out of its hinge's plane: a knee past the game clip's own by more than 15 degrees warns,
    /// by more than 30 fails; an elbow 30 and 45; an ankle rolled 15 and 30. A bend about the hinge itself stays in its plane, and the game
    /// clip's own sideways bend is allowed.</summary>
    [Fact]
    public void A_knee_or_an_elbow_bent_sideways_warns_then_fails()
    {
        var game = Still(20);
        float W(int t) => MathF.Sin(MathF.PI * t / 20);
        // the shin turned about the body's forward axis (y) at the knee: out of the knee's plane by that much
        Motion Knee(float deg, float fold = 0) => Humanoid.Motion(S, 20, (t, l) =>
        {
            Humanoid.Turn(S, l, "LeftLeg", Vector3.UnitX, -fold * W(t));
            Humanoid.Turn(S, l, "LeftLeg", Vector3.UnitY, deg * W(t));
        });
        Assert.Equal(-40, JointAngles.Measure(Knee(-40), game)!["Left knee sideways"].Lo, 1);
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Knee(10), game).Level);
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Knee(0, fold: 90), game).Level);
        var w = MotionChecks.JointLimits(Knee(25), game);
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Equal("Left knee sideways", w.Bone);
        Assert.Equal(10, w.Tick);
        var f = MotionChecks.JointLimits(Knee(-45, fold: 60), game);
        Assert.Equal(CheckLevel.Fail, f.Level);
        Assert.Contains("hinge's plane", f.Message);
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Knee(45), Knee(40)).Level);
        // the elbow: the game's clip bends it 60 forward about x; the forearm then turned about its own y, out of that plane
        Motion Elbow(float side) => Humanoid.Motion(S, 20, (t, l) =>
        {
            Humanoid.Turn(S, l, "LeftForeArm", Vector3.UnitX, 60);
            Humanoid.Turn(S, l, "LeftForeArm", Vector3.UnitY, side * W(t));
        });
        var arm = Elbow(0);
        Assert.Equal(-50, JointAngles.Measure(Elbow(50), arm)!["Left elbow sideways"].Lo, 1);
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Elbow(25), arm).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.JointLimits(Elbow(40), arm).Level);
        Assert.Equal(CheckLevel.Fail, MotionChecks.JointLimits(Elbow(-60), arm).Level);
        // the ankle rolled: the foot turned about the body's forward axis
        Motion Ankle(float deg) => Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "LeftFoot", Vector3.UnitY, deg * W(t)));
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Ankle(10), game).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.JointLimits(Ankle(25), game).Level);
        var roll = MotionChecks.JointLimits(Ankle(-50), game);
        Assert.Equal(CheckLevel.Fail, roll.Level);
        Assert.Equal("Left ankle roll", roll.Bone);
        // the foot pointed down about the hinge isn't a roll
        Assert.Equal(0, JointAngles.Measure(Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "LeftFoot", Vector3.UnitX, 30 * W(t))), game)!["Left ankle roll"].Hi, 1);
    }

    /// <summary>The mover's turn: snapped round in one tick fails, a quick turn past twice the game clip's largest step plus 10 degrees
    /// warns, a spin far beyond the game clip's own turn warns; a turn eased over the clip, or as the game's clip turns, passes.</summary>
    [Fact]
    public void A_mover_snapped_round_fails_and_a_spin_warns()
    {
        Motion Turning(int last, Func<int, float> deg) => Humanoid.Motion(S, last, mover: t => new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg(t) * MathF.PI / 180)));
        var game = Still(60);
        var snap = MotionChecks.MoverTurn(Turning(60, t => t >= 20 && t <= 40 ? 180 : 0), game);
        Assert.Equal(CheckLevel.Fail, snap.Level);
        Assert.Equal(20, snap.Tick);
        Assert.Equal("the mover", snap.Bone);
        Assert.Equal(CheckLevel.Fail, MotionChecks.MoverTurn(Turning(60, t => t >= 30 ? 90 : 0), game).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.MoverTurn(Turning(60, t => t >= 30 ? 20 : 0), game).Level);
        Assert.Contains(MotionChecks.Body(Turning(60, t => t >= 20 && t <= 40 ? 180 : 0), game), r => r.Check == "the mover's turn" && r.Level == CheckLevel.Fail);
        // three whole turns eased over 300 ticks: under 6 degrees a tick, but 1,080 from the game clip's
        float Eased(int t, int last, float total) => total * (1 - MathF.Cos(MathF.PI * t / last)) / 2;
        var spin = MotionChecks.MoverTurn(Turning(300, t => Eased(t, 300, 1080)), Still(300));
        Assert.Equal(CheckLevel.Warn, spin.Level);
        Assert.Contains("doesn't make", spin.Message);
        Assert.Equal(CheckLevel.Pass, MotionChecks.MoverTurn(Turning(300, t => Eased(t, 300, 90)), Still(300)).Level);
        // the game's own clip turning as much, and as fast: allowed
        Assert.Equal(CheckLevel.Pass, MotionChecks.MoverTurn(Turning(300, t => Eased(t, 300, 1080)), Turning(300, t => Eased(t, 300, 1000))).Level);
        Assert.Equal(CheckLevel.Pass, MotionChecks.MoverTurn(Turning(60, t => t >= 30 ? 90 : 0), Turning(60, t => t >= 20 ? 80 : 0)).Level);
        Assert.Equal(180, Math.Abs(MotionChecks.Heading(Turning(10, t => 180))[5]), 3);
    }

    /// <summary>The legs against each other: a thigh swung across the other leg fails where their bones meet, and warns when they come more
    /// than 5 cm nearer than the game clip's own; a small swing passes.</summary>
    [Fact]
    public void One_leg_swung_through_the_other_fails()
    {
        // the left thigh swung towards the right leg (about the forward axis, the right way being -y here)
        Motion Across(float deg) => Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "LeftUpLeg", Vector3.UnitY, -deg * MathF.Sin(MathF.PI * t / 20)));
        var game = Still(20);
        Assert.Equal(20, MotionChecks.NearestLegs(game).Cm, 1);
        Assert.Equal(CheckLevel.Pass, MotionChecks.Limbs(Across(2), game).Level);
        var w = MotionChecks.Limbs(Across(10), game);                                 // the feet 4.4 cm apart
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Contains("of each other", w.Message);
        var f = MotionChecks.Limbs(Across(30), game);
        Assert.Equal(CheckLevel.Fail, f.Level);
        Assert.StartsWith("the legs come within 0.0 cm", f.Message);
        Assert.Contains("one leg passes through the other", f.Message);
        // the game's own clip crossing its legs as far allows it
        Assert.Equal(CheckLevel.Pass, MotionChecks.Limbs(Across(30), Across(30)).Level);
    }

    /// <summary>A game clip whose own legs come within 7 cm keeps the fail limit: a leg nearer than 2 cm fails, and the line gives the
    /// limits used, the warn limit never under the fail limit.</summary>
    [Fact]
    public void A_leg_through_the_other_fails_where_the_games_legs_are_near()
    {
        Motion Across(float deg) => Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "LeftUpLeg", Vector3.UnitY, -deg * MathF.Sin(MathF.PI * t / 20)));
        var six = Across(8.95f);
        Assert.InRange(MotionChecks.NearestLegs(six).Cm, 5.5, 6.5);
        Assert.InRange(MotionChecks.NearestLegs(Across(11.87f)).Cm, 1.0, 1.9);
        var f = MotionChecks.Limbs(Across(11.87f), six);
        Assert.Equal(CheckLevel.Fail, f.Level);
        Assert.Contains("one leg passes through the other", f.Message);
        Assert.Contains("(warn under 2.0, fail under 2.0 cm)", f.Message);
        // the game's legs within 5 cm: still held to the fail limit
        var four = Across(10.24f);
        Assert.InRange(MotionChecks.NearestLegs(four).Cm, 3.5, 4.5);
        Assert.Equal(CheckLevel.Fail, MotionChecks.Limbs(Across(12.51f), four).Level);
        // as near as the game's own clip comes is allowed
        Assert.Equal(CheckLevel.Pass, MotionChecks.Limbs(Across(11.87f), Across(11.87f)).Level);
    }

    /// <summary>A mirror's result is held to the game clip mirrored: the left forearm rolled 150 degrees becomes the right forearm rolled
    /// the other way, which fails against the clip as it is and passes every body check against the clip mirrored; mirrored twice, the
    /// motion is the clip again.</summary>
    [Fact]
    public void A_mirror_is_checked_against_the_clip_mirrored()
    {
        var mirror = new Core.Edits.SkeletonMirror(S);
        var game = Humanoid.Motion(S, 20, (t, l) =>
        {
            Humanoid.Turn(S, l, "LeftForeArm", Vector3.UnitZ, t >= 10 ? 150 : 0);
            Humanoid.Turn(S, l, "Spine1", Vector3.UnitZ, 2 * t);
        }, mover: t => new Transform(new Vector3(0.01f * t, 0.02f * t, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.01f * t)));
        var result = game.Mirrored(mirror);
        int lf = Humanoid.Bone(S, "LeftForeArm"), rf = Humanoid.Bone(S, "RightForeArm");
        Assert.Equal(-MotionChecks.TwistDeg(game.Local(15, lf).Rotation, MotionChecks.TwistAxis(S, lf)),
            MotionChecks.TwistDeg(result.Local(15, rf).Rotation, MotionChecks.TwistAxis(S, rf)), 1);
        // the mover's sideways travel is turned the other way, its forward travel kept
        Assert.Equal(-0.2f, result.Mover(20).Position.X, 3);
        Assert.Equal(0.4f, result.Mover(20).Position.Y, 3);
        var plain = MotionChecks.Twist(result, game);
        Assert.Equal(CheckLevel.Fail, plain.Level);
        Assert.Equal("RightForeArm", plain.Bone);
        foreach (var r in MotionChecks.Body(result, game.Mirrored(mirror))) Assert.True(r.Level == CheckLevel.Pass, r.Line);
        var twice = result.Mirrored(mirror);
        for (int t = 0; t <= 20; t++)
            for (int b = 0; b < S.Bones.Count; b++)
                Assert.True(GltfAxes.AngleDeg(twice.World(t, b).Rotation, game.World(t, b).Rotation) < 0.01 && Vector3.Distance(twice.World(t, b).Position, game.World(t, b).Position) < 1e-4f, $"tick {t} bone {S.Bones[b].Name}");
    }

    /// <summary>Speed by region: a finger fast in the game's clip doesn't allow a fast spine.</summary>
    [Fact]
    public void Speed_is_compared_region_by_region()
    {
        Motion M(float finger, float spine) => Humanoid.Motion(S, 20, (t, l) =>
        {
            Humanoid.Turn(S, l, "LeftHandMiddle1", Vector3.UnitX, finger * t);
            Humanoid.Turn(S, l, "Spine1", Vector3.UnitY, spine * t);
        });
        var w = MotionChecks.Speed(M(0, 8), M(8, 0));
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Equal("Spine1", w.Bone);
        Assert.Equal(CheckLevel.Pass, MotionChecks.Speed(M(8, 8), M(8, 8)).Level);
    }

    [Fact]
    public void Joint_limits_warn_then_fail()
    {
        // the shin swung forward past the straight knee (a knee bent the wrong way): 15 deg is 5 past the range, 30 is 20 past
        Motion Knee(float deg) => Humanoid.Motion(S, 10, (t, l) => { if (t == 4) Humanoid.Turn(S, l, "LeftLeg", Vector3.UnitX, deg); });
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Knee(5)).Level);
        var w = MotionChecks.JointLimits(Knee(15));
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Equal("Left knee", w.Bone);
        Assert.Equal(4, w.Tick);
        Assert.Equal(CheckLevel.Fail, MotionChecks.JointLimits(Knee(30)).Level);
        // the game's own clip going as far allows it
        Assert.Equal(CheckLevel.Pass, MotionChecks.JointLimits(Knee(30), Knee(30)).Level);
    }

    [Fact]
    public void Length_is_the_slots_or_fails() =>
        Assert.Equal((CheckLevel.Pass, CheckLevel.Fail), (MotionChecks.Length(120, 120).Level, MotionChecks.Length(119, 120).Level));

    [Fact]
    public void Speed_against_the_slots_own()
    {
        Motion Nod(float degPerTick) => Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "Head", Vector3.UnitZ, degPerTick * t));
        Assert.Equal(CheckLevel.Pass, MotionChecks.Speed(Nod(4), Nod(4)).Level);
        Assert.Equal(CheckLevel.Pass, MotionChecks.Speed(Nod(5), Nod(1)).Level);       // under the 6 deg floor
        var w = MotionChecks.Speed(Nod(12), Nod(4));                                   // 12 > 1.5 x 4
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Equal("Head", w.Bone);
    }

    [Fact]
    public void Pops_warn_then_fail()
    {
        Motion Jump(float deg) => Humanoid.Motion(S, 20, (t, l) => { if (t >= 10) Humanoid.Turn(S, l, "Spine1", Vector3.UnitX, deg); });
        Assert.Equal(CheckLevel.Pass, MotionChecks.Pops(Jump(10)).Level);
        var w = MotionChecks.Pops(Jump(20));
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Equal((10, "Spine1"), (w.Tick!.Value, w.Bone!));
        Assert.Equal(CheckLevel.Fail, MotionChecks.Pops(Jump(40)).Level);
        // the game's own clip jumping as far: its step plus 5 deg is the limit
        Assert.Equal(CheckLevel.Pass, MotionChecks.Pops(Jump(40), Jump(40)).Level);
        // a jump of place: the mover 30 cm in one tick
        var hop = Humanoid.Motion(S, 20, mover: t => new Transform(new Vector3(0, t >= 10 ? 0.3f : 0, 0), Quaternion.Identity));
        Assert.Equal(CheckLevel.Fail, MotionChecks.Pops(hop).Level);
    }

    [Fact]
    public void Limbs_as_segments_see_an_arm_through_a_thigh()
    {
        // the left arm moved so its hanging bones run 3 cm from the thigh's line (warn), then through it (fail)
        Motion Arm(float x) => Humanoid.Motion(S, 10, (t, l) => { int a = Humanoid.Bone(S, "LeftArm"); l[a] = l[a] with { Position = new Vector3(x, 0, -0.4f) }; });
        Assert.Equal(CheckLevel.Pass, MotionChecks.Limbs(Still(10)).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.Limbs(Arm(-0.13f)).Level);
        var f = MotionChecks.Limbs(Arm(-0.1f));
        Assert.Equal(CheckLevel.Fail, f.Level);
        Assert.Contains("LeftArm", f.Message);
        Assert.Equal(0, MotionChecks.SegmentDistance(new(0, 0, 0), new(1, 0, 0), new(0.5f, -1, 0), new(0.5f, 1, 0)), 5);
        Assert.Equal(1, MotionChecks.SegmentDistance(new(0, 0, 0), new(1, 0, 0), new(0.5f, -1, 1), new(0.5f, 1, 1)), 5);
    }

    [Fact]
    public void Read_back_within_the_writers_tolerance()
    {
        Motion Off(float deg) => Humanoid.Motion(S, 10, (t, l) => Humanoid.Turn(S, l, "Spine", Vector3.UnitY, deg));
        Assert.Equal(CheckLevel.Pass, MotionChecks.ReadBack(Off(0), Off(0.05f)).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.ReadBack(Off(0), Off(0.5f)).Level);
        Assert.Equal(CheckLevel.Fail, MotionChecks.ReadBack(Off(0), Off(5)).Level);
        Assert.Equal(CheckLevel.Fail, MotionChecks.ReadBack(Off(0), Humanoid.Motion(S, 9)).Level);
    }

    [Fact]
    public void A_hand_moved_in_an_armed_slot_warns()
    {
        var game = Still(10);
        var moved = Humanoid.Motion(S, 10, (t, l) => Humanoid.Turn(S, l, "LeftArm", Vector3.UnitY, 20));
        Assert.Equal(CheckLevel.Pass, MotionChecks.HandMoves(moved, game, [0x12345678u]).Level);   // not armed
        Assert.Equal(CheckLevel.Pass, MotionChecks.HandMoves(game, game, [0x58A97CAEu]).Level);    // armed, the hand where it was
        var w = MotionChecks.HandMoves(moved, game, [0x58A97CAEu, 0x6C8CB68Cu]);
        Assert.Equal(CheckLevel.Warn, w.Level);
        Assert.Equal("LeftHand", w.Bone);
        Assert.Contains("58A97CAE", w.Message);
        Assert.True(HandTracks.IsArmed([0x0F208B89u]) && !HandTracks.IsArmed([0u, 0x12345678u]));
        Assert.Equal(10, HandTracks.All.Count);
    }

    [Fact]
    public void Feet_moved_during_a_plant_warn()
    {
        var game = Still(20);
        var kicked = Humanoid.Motion(S, 20, (t, l) => { if (t > 5) Humanoid.Turn(S, l, "LeftUpLeg", Vector3.UnitX, 20); });
        Assert.Equal(CheckLevel.Pass, MotionChecks.Plants(kicked, game, []).Level);
        Assert.Equal(CheckLevel.Pass, MotionChecks.Plants(kicked, game, [(0, 0, 5)]).Level);
        Assert.Equal(CheckLevel.Warn, MotionChecks.Plants(kicked, game, [(0, 0, 20)]).Level);
    }

    static Dictionary<string, FileBone> FileOf(Skeleton s, Func<Vector3, Vector3>? p = null, Func<Quaternion, Quaternion>? q = null) =>
        s.Bones.ToDictionary(b => b.Name, b => new FileBone(b.ParentIndex >= 0 ? s.Bones[b.ParentIndex].Name : null,
            (p ?? (x => x))(b.LocalRest.Position), (q ?? (x => x))(b.LocalRest.Rotation)));

    [Fact]
    public void Bone_match_finds_missing_bones_wrong_parents_a_long_bone_and_unturned_axes()
    {
        var ok = BoneMatch.Check(S, FileOf(S));
        Assert.Equal((CheckLevel.Pass, BoneMatchFault.None), (ok.Check.Level, ok.Fault));
        Assert.Equal(BoneMatchFault.Missing, BoneMatch.Check(S, FileOf(Humanoid.Skeleton(leave: "RightToeBase"))).Fault);
        var reparented = FileOf(S);
        reparented["Head"] = reparented["Head"] with { Parent = "Spine2" };
        Assert.Equal(BoneMatchFault.Parent, BoneMatch.Check(S, reparented).Fault);
        var thigh = BoneMatch.Check(S, FileOf(Humanoid.Skeleton(longer: "LeftLeg")));
        Assert.Equal((CheckLevel.Fail, BoneMatchFault.Rest), (thigh.Check.Level, thigh.Fault));
        // worded for any glTF tool: what to keep, no editor's mode or menu
        Assert.Contains("without changing their rest pose", thigh.Check.Message);
        foreach (var word in new[] { "Blender", "Edit Mode", "Pose Mode", "Object Mode", "Alt+" }) Assert.DoesNotContain(word, thigh.Check.Message);
        Assert.Contains("LeftLeg", thigh.Check.Message);
        // a file written without the turn to glTF's axes reads back turned the other way
        var axes = BoneMatch.Check(S, FileOf(S, x => GltfAxes.FromGltf(x), x => GltfAxes.FromGltf(x)));
        Assert.Equal(BoneMatchFault.Axes, axes.Fault);
        Assert.Contains("glTF's own axes (y up)", axes.Check.Message);
        Assert.DoesNotContain("Blender", axes.Check.Message);
    }

    [Fact]
    public void A_report_gives_the_worst_level_and_its_lines()
    {
        var r = new CheckReport().Add(new CheckResult("a", CheckLevel.Pass, "fine")).Add(new CheckResult("b", CheckLevel.Warn, "hm", 3, "Head"));
        Assert.Equal(CheckLevel.Warn, r.Verdict);
        Assert.Equal("WARN  b: hm (worst at tick 3, Head)", r.Results[1].Line);
        Assert.Contains("VERDICT: WARN (1 pass, 1 warn, 0 fail)", r.ToText("x"));
        Assert.Equal(CheckLevel.Fail, r.Add(new CheckResult("c", CheckLevel.Fail, "no")).Verdict);
        Assert.Equal(CheckLevel.Pass, new CheckReport().Verdict);
    }
}
