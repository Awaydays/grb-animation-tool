using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Library;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>The checks on the game's own clips: mirror results, checked as the Edit screen checks them (against the clip mirrored).</summary>
[Collection("install")]
public sealed class ChecksGameTests(InstallFixture f, ITestOutputHelper output)
{
    /// <summary>A mirror is exact by construction, so over a sample of the clips the graph allows a mirror on (every 12th), no twist, joint
    /// limit or pop fails when each bone is held to its counterpart in the clip; held to the same bone in the unmirrored clip, some twists
    /// would.</summary>
    [GameDataFact]
    public void Mirrors_pass_the_twist_joint_and_pop_checks_against_the_clip_mirrored()
    {
        var g = f.Install;
        var graph = ClipGraph.Read(g);
        var mirror = new SkeletonMirror(g.Skeleton);
        int n = 0, refused = 0, unmirroredFails = 0;
        var fails = new List<string>();
        var counts = new Dictionary<string, int[]>();
        var sample = g.Clips.Where(c => c.Record is { WalkedToEnd: true } && c.LastTick is > 2 and <= 3000 && graph.Verdict(c.Id).CanChangeMotion).Where((_, i) => i % 12 == 0);
        foreach (var c in sample)
        {
            EditOutcome o;
            try { o = EditJobs.Apply(c, new EditRequest(EditKind.Mirror), new EditContext(g.Skeleton, g.SharedTable, graph.Verdict(c.Id)), mirror); }
            catch (EditRefusedException) { refused++; continue; }
            n++;
            foreach (var r in o.Report.Results.Where(r => r.Check is "twists" or "joint limits" or "pops"))
            {
                if (!counts.TryGetValue(r.Check, out var k)) counts[r.Check] = k = new int[3];
                k[(int)r.Level]++;
                if (r.Level == CheckLevel.Fail) fails.Add($"{c.Name}: {r.Line}");
            }
            if (MotionChecks.Twist(o.Edited, o.Original).Level == CheckLevel.Fail) unmirroredFails++;
        }
        output.WriteLine($"{n} mirrors ({refused} refused); twists failing against the unmirrored clip: {unmirroredFails}");
        foreach (var (k, v) in counts) output.WriteLine($"{k}: pass {v[0]}, warn {v[1]}, fail {v[2]}");
        Assert.True(n >= 100, $"only {n} mirrors in the sample");
        Assert.True(fails.Count == 0, string.Join("\n", fails.Take(10)));
        Assert.True(unmirroredFails > 0, "no mirror in the sample tells the two references apart");
    }

    /// <summary>A copy of the game's clip with each bone's local turn and the mover changed.</summary>
    static Motion Changed(Motion gm, Func<int, int, Transform, Transform> local, Func<int, Transform, Transform>? mover = null)
    {
        int n = gm.Skeleton.Bones.Count, last = gm.LastTick;
        var l = new Transform[last + 1][];
        var mv = new Transform[last + 1];
        for (int t = 0; t <= last; t++)
        {
            l[t] = new Transform[n];
            for (int b = 0; b < n; b++) l[t][b] = local(t, b, gm.Local(t, b));
            mv[t] = mover is null ? gm.Mover(t) : mover(t, gm.Mover(t));
        }
        return new Motion(gm.Skeleton, l, mv, Enumerable.Repeat(true, n).ToArray(), gm.PositionKeyed, gm.HasMover);
    }

    /// <summary>Broken motions on two of the game's clips (a standing idle and an armed one), each eased in and out, fail against the clip:
    /// the head turned round split over the neck and the head, the torso turned 180 over the spine, about 350 degrees from the hips to the
    /// head, the elbow bent backwards about its hinge (with the forearm rolled palm down too), the body floating 1 m between feet down at
    /// the ends, a knee and an elbow bent sideways out of their hinge's plane, the mover's heading snapped round, one thigh swung through the
    /// other leg. Natural turns pass, and a pure fold of the elbow about its hinge isn't read as a twist.</summary>
    [GameDataFact]
    public void Broken_motions_on_the_games_clips_fail_and_natural_ones_pass()
    {
        var g = f.Install;
        var s = g.Skeleton;
        foreach (var name in new[] { "mcm_nwp_fm_std_V0_idle_03", "Mch_M72_Std_Get" })
        {
            var gm = Motion.FromClip(s, g.FindClip(name)!.Record!, g.SharedTable);
            int last = gm.LastTick;
            float W(int t) => MathF.Sin(MathF.PI * t / last);
            Motion Twisted(params (string Bone, float Deg)[] parts)
            {
                var map = parts.Select(p => (B: gm.Bone(p.Bone), A: MotionChecks.TwistAxis(s, gm.Bone(p.Bone)), p.Deg)).ToArray();
                return Changed(gm, (t, b, x) =>
                {
                    foreach (var m in map) if (m.B == b) return x with { Rotation = Quaternion.Normalize(x.Rotation * Quaternion.CreateFromAxisAngle(m.A, W(t) * m.Deg * MathF.PI / 180)) };
                    return x;
                });
            }
            // the elbow turned about its own hinge (as the game's first pose bends it) in the forearm's frame, made square to the forearm's
            // length so the turn is a pure bend at every tick
            int ua = gm.Bone("LeftArm"), fa = gm.Bone("LeftForeArm"), hd = gm.Bone("LeftHand");
            var hinge = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(gm.Model(0, fa).Position - gm.Model(0, ua).Position), Vector3.Normalize(gm.Model(0, hd).Position - gm.Model(0, fa).Position)));
            var along = MotionChecks.TwistAxis(s, fa);
            Vector3 Square(Vector3 h) => Vector3.Normalize(h - Vector3.Dot(h, along) * along);
            Motion Elbow(float deg) => Changed(gm, (t, b, x) => b != fa ? x : x with
            {
                Rotation = Quaternion.Normalize(x.Rotation * Quaternion.CreateFromAxisAngle(Square(Vector3.Transform(hinge, Quaternion.Inverse(gm.Model(t, fa).Rotation))), W(t) * deg * MathF.PI / 180)),
            });
            CheckLevel Twist(Motion m) => MotionChecks.Twist(m, gm).Level;
            var bendBack = JointAngles.Measure(gm, gm)!["Left elbow"].Lo;
            // a bone turned about a model axis at its joint, at each tick (post-multiplied in its own frame)
            Motion About(int bone, Func<int, Vector3> axis, float deg) => Changed(gm, (t, b, x) => b != bone ? x : x with
            {
                Rotation = Quaternion.Normalize(x.Rotation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Transform(axis(t), Quaternion.Inverse(gm.Model(t, bone).Rotation))), W(t) * deg * MathF.PI / 180)),
            });
            Vector3 Pos(int t, string n) => gm.Model(t, gm.Bone(n)).Position;
            Vector3 Dir(int t, string a, string z) => Vector3.Normalize(Pos(t, z) - Pos(t, a));
            // the body's forward axis at the hips, and the elbow's hinge from the rest as the upper arm carries it
            Vector3 Fwd(int t) => Vector3.Normalize(Vector3.Cross(Dir(t, "LeftUpLeg", "Spine") + Dir(t, "RightUpLeg", "Spine"), Dir(t, "LeftUpLeg", "RightUpLeg")));
            var rest = new Transform[s.Bones.Count];
            s.ComputeModel(s.RestLocals(), rest);
            var hingeInArm = Vector3.Transform(Vector3.Normalize(Vector3.Cross(Vector3.Normalize(rest[fa].Position - rest[ua].Position), Vector3.Normalize(rest[hd].Position - rest[fa].Position))), Quaternion.Inverse(rest[ua].Rotation));
            Vector3 OutOfPlane(int t) => Vector3.Normalize(Vector3.Cross(Vector3.Transform(hingeInArm, gm.Model(t, ua).Rotation), Dir(t, "LeftForeArm", "LeftHand")));
            // the elbow opened 120 about its hinge with the forearm rolled 100 about its own length on top (a forearm turned palm down)
            Motion BackRolled() => Changed(gm, (t, b, x) => b != fa ? x : x with
            {
                Rotation = Quaternion.Normalize(x.Rotation * Quaternion.CreateFromAxisAngle(Square(Vector3.Transform(hinge, Quaternion.Inverse(gm.Model(t, fa).Rotation))), -W(t) * 120 * MathF.PI / 180)
                    * Quaternion.CreateFromAxisAngle(along, W(t) * 100 * MathF.PI / 180)),
            });
            // the mover's heading snapped round for the middle third
            Motion Snapped(float deg) => Changed(gm, (t, b, x) => x, (t, m) => t >= last / 3 && t <= 2 * last / 3
                ? m with { Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg * MathF.PI / 180) * m.Rotation) } : m);
            int thigh = gm.Bone("LeftUpLeg"), shin = gm.Bone("LeftLeg");
            var cases = new (string What, Func<CheckResult> Run, CheckLevel Want)[]
            {
                ("Neck 90 + Head 90", () => MotionChecks.Twist(Twisted(("Neck", 90), ("Head", 90)), gm), CheckLevel.Fail),
                ("Neck 75 + Head 105", () => MotionChecks.Twist(Twisted(("Neck", 75), ("Head", 105)), gm), CheckLevel.Fail),
                ("the spine 3 x 60", () => MotionChecks.Twist(Twisted(("Spine", 60), ("Spine1", 60), ("Spine2", 60)), gm), CheckLevel.Fail),
                ("the hips to the head, 355", () => MotionChecks.Twist(Twisted(("Spine", 60), ("Spine1", 60), ("Spine2", 60), ("Neck", 75), ("Head", 100)), gm), CheckLevel.Fail),
                ("the elbow opened 120 about its hinge (30 to 90 backwards)", () => MotionChecks.JointLimits(Elbow(-120), gm), CheckLevel.Fail),
                ("the mover raised 1 m, eased", () => MotionChecks.FeetOffTheFloor(Changed(gm, (t, b, x) => x, (t, m) => m with { Position = m.Position + new Vector3(0, 0, W(t)) }), gm), CheckLevel.Fail),
                ("natural: Head 60", () => MotionChecks.Twist(Twisted(("Head", 60)), gm), CheckLevel.Pass),
                ("natural: Neck 30 + Head 45", () => MotionChecks.Twist(Twisted(("Neck", 30), ("Head", 45)), gm), CheckLevel.Pass),
                ("natural: the spine 3 x 15", () => MotionChecks.Twist(Twisted(("Spine", 15), ("Spine1", 15), ("Spine2", 15)), gm), CheckLevel.Pass),
                ("natural: LeftForeArm 70", () => MotionChecks.Twist(Twisted(("LeftForeArm", 70)), gm), CheckLevel.Pass),
                ("the elbow folded 20 further", () => MotionChecks.JointLimits(Elbow(20), gm), CheckLevel.Pass),
                ("the knee bent sideways 45 (the shin about the body's forward)", () => MotionChecks.JointLimits(About(shin, Fwd, 45), gm), CheckLevel.Fail),
                ("the knee bent sideways 45 the other way", () => MotionChecks.JointLimits(About(shin, Fwd, -45), gm), CheckLevel.Fail),
                ("the elbow bent sideways 60, out of its hinge's plane", () => MotionChecks.JointLimits(About(fa, OutOfPlane, 60), gm), CheckLevel.Fail),
                ("the elbow bent sideways 20", () => MotionChecks.JointLimits(About(fa, OutOfPlane, 20), gm), CheckLevel.Pass),
                ("the elbow opened 120 with the forearm rolled 100", () => MotionChecks.JointLimits(BackRolled(), gm), CheckLevel.Fail),
                ("the mover's heading snapped 180 for the middle third", () => MotionChecks.MoverTurn(Snapped(180), gm), CheckLevel.Fail),
                ("the mover's heading snapped 90 for the middle third", () => MotionChecks.MoverTurn(Snapped(90), gm), CheckLevel.Fail),
                ("the left thigh swung 45 through the right leg", () => MotionChecks.Limbs(About(thigh, Fwd, -45), gm), CheckLevel.Fail),
                ("natural: the left thigh swung 35 out", () => MotionChecks.Limbs(About(thigh, Fwd, 35), gm), CheckLevel.Pass),
                ("natural: the mover's heading eased 90 round", () => MotionChecks.MoverTurn(Changed(gm, (t, b, x) => x, (t, m) => m with
                    { Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 90 * (1 - MathF.Cos(MathF.PI * t / last)) / 2 * MathF.PI / 180) * m.Rotation) }), gm), CheckLevel.Pass),
            };
            foreach (var (what, run, want) in cases)
            {
                var r = run();
                output.WriteLine($"{name}, {what}: {r.Line}");
                Assert.True(r.Level == want, $"{name}, {what}: {r.Line}");
            }
            // a pure fold or opening about the hinge isn't read as a twist (split from the rest pose, a bend's path read as one)
            Assert.Equal(CheckLevel.Pass, Twist(Elbow(120)));
            Assert.Equal(CheckLevel.Pass, Twist(Elbow(-60)));
            Assert.True(bendBack > 0, $"{name}: the game's own elbow reads {bendBack:F0}");
        }
    }
}
