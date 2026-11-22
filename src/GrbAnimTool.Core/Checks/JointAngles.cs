using System.Numerics;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Checks;

/// <summary>The body's joint angles measured by geometry from the joints' places, every tick, and the human ranges the joint-limit check
/// holds them to.
/// <para>The measures (degrees): the knees signed (bent forward negative), the elbows signed (bent backwards through straight negative,
/// against the elbow's own hinge as the upper arm carries it, see <see cref="Hinge"/>); the knees and elbows out of their hinge's plane
/// (sideways: the lower bone's direction against the hinge as the upper bone carries it) and the ankles' roll (the foot's copy of the knee's
/// hinge tilted towards the shin's length: the foot turned about its own length); the hips' and shoulders' bend forward in the body's
/// side view (0 hanging, 90 straight forward, 180 overhead) and their lean out of it (+ away from the body's middle); the spine's bend (the
/// pelvis against the upper chest); the neck and head (the upper chest against the head); the ankles (the shin against the foot's own
/// direction, from square: + pointed down); the wrists (the forearm against the hand, as their change from a first pose).</para></summary>
public static class JointAngles
{
    /// <summary>The human range of each measure (degrees): a rig's own ranges plus a margin for the measure's form. The elbow's low end is
    /// straight: the joint-limit check holds an elbow bent backwards past it by its own margins (<see cref="MotionChecks.ElbowBackWarnDeg"/>).
    /// The measures out of a hinge's plane aren't here: they are held to <see cref="OutOfPlane"/>.</summary>
    public static readonly IReadOnlyDictionary<string, (float Lo, float Hi)> Human = new Dictionary<string, (float, float)>
    {
        ["spine bend"] = (0, 85), ["neck and head"] = (0, 85), ["knee"] = (-10, 155), ["hip forward"] = (-25, 130), ["hip outward"] = (-42, 55),
        ["ankle"] = (-35, 55), ["elbow"] = (0, 150), ["shoulder forward"] = (-70, 185), ["shoulder outward"] = (-60, 95), ["wrist"] = (-75, 75),
    };

    /// <summary>The measures out of a hinge's plane, each with how far past the game clip's own range (none without one: 0) warns and
    /// fails: a knee bent sideways 15 and 30, an elbow bent sideways 30 and 45 (the game's own elbows leave their plane further: within 25
    /// degrees in 93% of its clips, a knee within 15 in 96%), an ankle rolled 15 and 30.</summary>
    public static readonly IReadOnlyDictionary<string, (float Warn, float Fail)> OutOfPlane = new Dictionary<string, (float, float)>
    {
        ["knee sideways"] = (15, 30), ["elbow sideways"] = (30, 45), ["ankle roll"] = (15, 30),
    };

    /// <summary>A measure's kind, without its side: "Left knee" is "knee".</summary>
    public static string Kind(string measure) => measure.Replace("Left ", "").Replace("Right ", "");

    static readonly string[] Sides = ["Left", "Right"];

    /// <summary>One measure over a motion: its lowest and highest value and the ticks they fell on.</summary>
    public sealed record Range(float Lo, float Hi, int LoTick, int HiTick);

    /// <summary>The bones the measures need, all present by name in the skeleton; null when one is missing.</summary>
    static Dictionary<string, int>? Bones(Motion m)
    {
        var names = new List<string> { "Spine", "Spine2", "Neck", "Head" };
        foreach (var s in Sides) names.AddRange([s + "UpLeg", s + "Leg", s + "Foot", s + "ToeBase", s + "Arm", s + "ForeArm", s + "Hand", s + "HandMiddle1"]);
        var d = new Dictionary<string, int>();
        foreach (var n in names)
        {
            int i = m.Bone(n);
            if (i < 0) return null;
            d[n] = i;
        }
        return d;
    }

    static float Ang(Vector3 a, Vector3 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-9f || lb < 1e-9f) return 0;
        return MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / (la * lb), -1, 1)) * 180 / MathF.PI;
    }

    /// <summary>Every measure over every tick of <paramref name="m"/>, keyed "Left knee", "spine bend" and so on; null when the skeleton
    /// lacks a bone the measures need. The ankles' and wrists' first pose is <paramref name="firstPose"/>'s tick 0 when given (so an edit is
    /// measured from the game clip's own start), else <paramref name="m"/>'s.</summary>
    public static Dictionary<string, Range>? Measure(Motion m, Motion? firstPose = null)
    {
        var b = Bones(m);
        if (b is null) return null;
        var f0 = firstPose ?? m;
        if (f0.Skeleton != m.Skeleton) f0 = m;
        Vector3 P(Motion x, int t, string n) => x.Model(t, b[n]).Position;
        Quaternion R(Motion x, int t, string n) => x.Model(t, b[n]).Rotation;
        Vector3 Dir(Motion x, int t, string a, string c) { var d = P(x, t, c) - P(x, t, a); return d.LengthSquared() > 1e-18f ? Vector3.Normalize(d) : Vector3.UnitZ; }

        // the foot's own direction: at the first pose its toe base's direction laid flat, carried by the foot bone's turn after that
        var flat0 = new Dictionary<string, (Vector3 Dir, Quaternion Q)>();
        var wrist0 = new Dictionary<string, float>();
        // each side's hinges in the frames that carry them: the elbow's in the upper arm (zero where neither the rest nor the first pose
        // bends it: the elbow is then measured unsigned), and for its sideways measure the chest's side axis where it has none; the knee's in
        // the thigh and in the foot, the pelvis's side axis where neither bends it
        var elbow = new Dictionary<string, (Vector3 Sign, Vector3 Plane)>();
        var knee = new Dictionary<string, (Vector3 InThigh, Vector3 InFoot)>();
        var restModel = new Transform[m.Skeleton.Bones.Count];
        m.Skeleton.ComputeModel(m.Skeleton.RestLocals(), restModel);
        var lat0 = N(P(f0, 0, "RightUpLeg") - P(f0, 0, "LeftUpLeg"));
        var latC0 = N(P(f0, 0, "RightArm") - P(f0, 0, "LeftArm"));
        foreach (var sd in Sides)
        {
            var d = Dir(f0, 0, sd + "Foot", sd + "ToeBase");
            d.Z = 0;
            flat0[sd] = (d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.UnitY, R(f0, 0, sd + "Foot"));
            wrist0[sd] = Ang(Dir(f0, 0, sd + "ForeArm", sd + "Hand"), Dir(f0, 0, sd + "Hand", sd + "HandMiddle1"));
            var e = Hinge(restModel, f0, b, sd + "Arm", sd + "ForeArm", sd + "Hand", 5, null, sd + "Arm")[0];
            elbow[sd] = (e, e != Vector3.Zero ? e : Hinge(restModel, f0, b, sd + "Arm", sd + "ForeArm", sd + "Hand", 5, latC0, sd + "Arm")[0]);
            var k = Hinge(restModel, f0, b, sd + "UpLeg", sd + "Leg", sd + "Foot", 2, -lat0, sd + "UpLeg", sd + "Foot");
            knee[sd] = (k[0], k[1]);
        }
        var o = new Dictionary<string, Range>();
        void Add(string k, float v, int t)
        {
            if (!float.IsFinite(v)) return;
            if (!o.TryGetValue(k, out var x)) o[k] = new Range(v, v, t, t);
            else o[k] = new Range(MathF.Min(x.Lo, v), MathF.Max(x.Hi, v), v < x.Lo ? t : x.LoTick, v > x.Hi ? t : x.HiTick);
        }
        // from -90 (behind the body) round through hanging 0 and forward 90 to overhead 180 and on to 270: no wrap at overhead
        static float Fwd(Vector3 d, Vector3 f, Vector3 u) { float a = MathF.Atan2(Vector3.Dot(d, f), -Vector3.Dot(d, u)) * 180 / MathF.PI; return a < -90 ? a + 360 : a; }
        static float Out(Vector3 d, Vector3 o) => MathF.Asin(Math.Clamp(Vector3.Dot(d, o), -1, 1)) * 180 / MathF.PI;
        static Vector3 N(Vector3 v) => v.LengthSquared() > 1e-18f ? Vector3.Normalize(v) : Vector3.UnitX;
        // a direction against a hinge carried by a bone's turn: its angle out of the plane square to the hinge
        float OutOfHinge(Vector3 dir, Vector3 hingeInBone, Quaternion bone) => hingeInBone == Vector3.Zero ? float.NaN : Out(dir, N(Vector3.Transform(hingeInBone, bone)));
        for (int t = 0; t <= m.LastTick; t++)
        {
            var lat = N(P(m, t, "RightUpLeg") - P(m, t, "LeftUpLeg"));
            var hm = (P(m, t, "RightUpLeg") + P(m, t, "LeftUpLeg")) / 2;
            var up = N(P(m, t, "Spine") - hm);
            var fwd = N(Vector3.Cross(up, lat));
            var latC = N(P(m, t, "RightArm") - P(m, t, "LeftArm"));
            var upC = Dir(m, t, "Spine2", "Neck");
            var fwdC = N(Vector3.Cross(upC, latC));
            Add("spine bend", Ang(up, upC), t);
            // the head bone's own x runs up the head
            Add("neck and head", Ang(upC, Vector3.Transform(Vector3.UnitX, R(m, t, "Head"))), t);
            foreach (var sd in Sides)
            {
                float sg = sd == "Left" ? -1 : 1;
                var th = Dir(m, t, sd + "UpLeg", sd + "Leg");
                var sh = Dir(m, t, sd + "Leg", sd + "Foot");
                Add(sd + " knee", Ang(th, sh) * (Vector3.Dot(Vector3.Cross(th, sh), -lat) >= 0 ? 1 : -1), t);
                // the shin out of the knee's plane (as the thigh carries the hinge), and the foot rolled (its own copy of the hinge tilted
                // towards the shin's length; a flex about the hinge or a turn about the shin leaves it square)
                Add(sd + " knee sideways", OutOfHinge(sh, knee[sd].InThigh, R(m, t, sd + "UpLeg")), t);
                Add(sd + " ankle roll", OutOfHinge(sh, knee[sd].InFoot, R(m, t, sd + "Foot")), t);
                Add(sd + " hip forward", Fwd(th, fwd, up), t);
                Add(sd + " hip outward", Out(th, lat * sg), t);
                var ft = Vector3.Transform(flat0[sd].Dir, Quaternion.Normalize(R(m, t, sd + "Foot") * Quaternion.Inverse(flat0[sd].Q)));
                Add(sd + " ankle", Ang(-sh, ft) - 90, t);
                var ua = Dir(m, t, sd + "Arm", sd + "ForeArm");
                var fa = Dir(m, t, sd + "ForeArm", sd + "Hand");
                // signed as the knee is: bent backwards through straight reads negative, against the elbow's own hinge as the upper arm
                // carries it (the hinge is fixed in the upper arm; the forearm's own frame rolls about its length, so it says nothing of the
                // bend's side)
                var armRot = R(m, t, sd + "Arm");
                bool back = Backwards(Vector3.Cross(ua, fa), Vector3.Transform(elbow[sd].Sign, armRot));
                Add(sd + " elbow", Ang(ua, fa) * (back ? -1 : 1), t);
                Add(sd + " elbow sideways", OutOfHinge(fa, elbow[sd].Plane, armRot), t);
                // with the arm out to the side by 60 degrees or more its projection on the side plane is too short to give a forward angle
                float sOut = Out(ua, latC * sg);
                if (MathF.Abs(sOut) < 60) Add(sd + " shoulder forward", Fwd(ua, fwdC, upC), t);
                Add(sd + " shoulder outward", sOut, t);
                Add(sd + " wrist", Ang(fa, Dir(m, t, sd + "Hand", sd + "HandMiddle1")) - wrist0[sd], t);
            }
        }
        return o;
    }

    /// <summary>A hinge's axis, the side its lower bone bends to (the cross of the upper bone's direction, <paramref name="upper"/> to
    /// <paramref name="lower"/>, and the lower's, <paramref name="lower"/> to <paramref name="end"/>), in the frame of each bone of
    /// <paramref name="frames"/>: from the rest pose where it bends the joint <paramref name="minRestDeg"/> degrees or more (the game's knee
    /// bends 4.9 there, its elbow 23), else from the first pose where that bends it 10 or more, else <paramref name="fallback"/> (an axis in
    /// the model at the first pose), else zero.</summary>
    static Vector3[] Hinge(Transform[] rest, Motion f0, Dictionary<string, int> b, string upper, string lower, string end, float minRestDeg, Vector3? fallback, params string[] frames)
    {
        Vector3[]? From(Func<string, Transform> x, float minDeg)
        {
            Vector3 u = x(lower).Position - x(upper).Position, l = x(end).Position - x(lower).Position;
            if (u.LengthSquared() < 1e-12f || l.LengthSquared() < 1e-12f) return null;
            var c = Vector3.Cross(Vector3.Normalize(u), Vector3.Normalize(l));
            if (c.Length() < MathF.Sin(minDeg * MathF.PI / 180)) return null;
            c = Vector3.Normalize(c);
            return [.. frames.Select(f => Vector3.Transform(c, Quaternion.Inverse(x(f).Rotation)))];
        }
        Transform First(string n) => f0.Model(0, b[n]);
        return From(n => rest[b[n]], minRestDeg) ?? From(First, 10)
            ?? [.. frames.Select(f => fallback is { } a ? Vector3.Transform(a, Quaternion.Inverse(First(f).Rotation)) : Vector3.Zero)];
    }

    /// <summary>Whether an elbow bends backwards through straight: the forearm's bend (the cross of the upper arm's and the forearm's
    /// directions, <paramref name="bend"/>) lies within 60 degrees of the far side of the hinge as the upper arm carries it. The upper arm
    /// alone: the elbow's hinge is fixed in it, while the forearm's frame rolls about its own length (a forearm turned palm down), so a
    /// sign that also asked the forearm would read a backward bend with a rolled forearm as forward. Over every third of the game's clips
    /// the upper arm's hinge reads more than 25 degrees backwards in only two, and the joint-limit check widens to the game clip's own. A
    /// bend out to the side of the hinge counts as forward here; the sideways measure holds it.</summary>
    static bool Backwards(Vector3 bend, Vector3 hingeByArm)
    {
        float l = bend.Length();
        if (l < 1e-6f || hingeByArm == Vector3.Zero) return false;
        return Vector3.Dot(bend, hingeByArm) / l < -0.5f;
    }

    /// <summary>The range a measure is allowed: the human range, widened to the game clip's own range plus 5 degrees where the game's
    /// clip goes further (a crouch starts past the standing ranges).</summary>
    public static (float Lo, float Hi) Allowed(string measure, Range? game)
    {
        string k = measure.Replace("Left ", "").Replace("Right ", "");
        var h = Human.TryGetValue(k, out var x) ? x : (float.NegativeInfinity, float.PositiveInfinity);
        return game is null ? h : (MathF.Min(h.Item1, game.Lo - 5), MathF.Max(h.Item2, game.Hi + 5));
    }
}
