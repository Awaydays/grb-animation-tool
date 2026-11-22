using System.Globalization;
using System.Numerics;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Checks;

/// <summary>The checks on a motion, each against the game clip it replaces where one is given (<c>game</c>): what the game's own clip
/// already does is never held against a new one. Each gives pass, warn or fail with a plain message and the worst tick and bone. The
/// limits are constants here, stated in each check's message.</summary>
public static class MotionChecks
{
    // ---- the limits ----

    /// <summary>Feet in the floor: a joint more than 2 cm below the floor (z = 0) warns, more than 5 cm fails, unless the game's clip goes
    /// as deep (then its depth plus 0.5 cm warns, plus 2 cm fails).</summary>
    public const float FloorWarnCm = 2, FloorFailCm = 5;
    /// <summary>Joint limits: past the allowed range by up to 10 degrees warns, further fails.</summary>
    public const float JointFailDeg = 10;
    /// <summary>Joint limits, an elbow bent backwards through straight (the signed elbow of <see cref="JointAngles"/> below 0): further back
    /// than the game clip's own clip goes that way (or than straight) by more than 10 degrees warns, by more than 25 fails.</summary>
    public const float ElbowBackWarnDeg = 10, ElbowBackFailDeg = 25;
    /// <summary>Speed: the fastest bone's turn a tick against the game clip's fastest, by this factor, and never below the floor.</summary>
    public const float SpeedFactor = 1.5f, SpeedFloorDeg = 6;
    /// <summary>Pops: a body bone turning more than this in one tick fails (the arms and hands 45); half of it warns. Where the game's clip
    /// turns that bone as fast, its own step plus 5 degrees is the limit.</summary>
    public const float PopDeg = 30, PopArmDeg = 45;
    /// <summary>Pops of place: the mover or the hips jumping more than 10 cm in one tick fails, unless the game's clip does.</summary>
    public const float PopCm = 10;
    /// <summary>Limbs as segments: legs against arms and the chest closer than 5 cm warns, 1 cm fails (the bones cross), unless the game's
    /// clip comes as close (1 cm of room).</summary>
    public const float LimbWarnCm = 5, LimbFailCm = 1;
    /// <summary>Limbs, the legs against each other (the thighs and shins as segments): nearer than the game clip's own nearest by more than
    /// 5 cm warns (without a game clip, nearer than 5 cm); nearer than 2 cm fails, the one leg through the other, unless the game's clip
    /// comes that near itself (1 cm of room).</summary>
    public const float LegsWarnCm = 5, LegsFailCm = 2;
    /// <summary>The mover's turn: its heading's change in one tick past the game clip's largest times 2 plus 10 degrees warns, past times 3
    /// plus 30 fails (the character's facing snapped round); its turn from its start heading further from the game clip's, at the same
    /// tick, than 180 degrees warns (a spin the game's clip doesn't make).</summary>
    public const float TurnWarnFactor = 2, TurnWarnDeg = 10, TurnFailFactor = 3, TurnFailDeg = 30, TurnApartDeg = 180;
    /// <summary>Read-back: 0.1 cm and 0.1 degrees pass, up to 1 cm and 1 degree warn, further fails.</summary>
    public const float ReadBackPassCm = 0.1f, ReadBackPassDeg = 0.1f, ReadBackWarnCm = 1, ReadBackWarnDeg = 1;
    /// <summary>A hand moves: its place against the game clip's further than 1 cm, or its turn further than 2 degrees.</summary>
    public const float HandCm = 1, HandDeg = 2;
    /// <summary>Feet off the floor, where the game's clip has its feet on it (its lowest foot joint within <see cref="GroundedCm"/> of the
    /// floor at some tick): the lowest foot joint over the whole clip higher than the game clip's lowest by more than 5 cm warns, by more than
    /// 10 cm fails (the body floats); and at a tick where the game's feet are down (its lowest foot within <see cref="FeetDownCm"/> of the
    /// floor), the lowest foot more than 15 cm above the game's warns, more than <see cref="FloatTickFailCm"/> fails (a float eased in and
    /// out between feet that touch down at the clip's ends). A tick where the game's own feet are up (a jump) isn't compared.</summary>
    public const float FloatWarnCm = 5, FloatFailCm = 10, FloatTickWarnCm = 15, GroundedCm = 10;
    /// <summary>See <see cref="FloatWarnCm"/>: the tick-by-tick fail, and how near the floor the game's lowest foot is where its feet are
    /// down (from the floor, or from the game clip's own lowest where that is higher).</summary>
    public const float FloatTickFailCm = 30, FeetDownCm = 5;
    /// <summary>Twists: a bone's turn about its own long axis (the swing-twist split about that axis, so a pure bend about an axis square to
    /// the bone adds none; a swing about an axis slanted to it splits partly into one, the more the larger the swing and the more slanted
    /// the axis), and
    /// the twists summed along a chain of <see cref="TwistChains"/>, past the allowed range by more than 15 degrees warns, by more than 45
    /// fails. The allowed range is the wider of the game clip's own range and the human one (<see cref="HumanTwistDeg"/> around the rest for
    /// a bone, the chain's own for a chain), so a head turned to look or a wrist rolled passes even where the game's clip holds them still;
    /// without a game clip, the human range alone.</summary>
    public const float TwistWarnDeg = 15, TwistFailDeg = 45;

    static string F(double v, string f = "F1") => v.ToString(f, CultureInfo.InvariantCulture);

    /// <summary>The body's bones: not a root frame, not an add-on bone.</summary>
    static IEnumerable<int> BodyBones(Skeleton s) => s.Bones.Where(b => !b.IsRoot && !b.FromAddOn).Select(b => b.Index);

    static bool ArmBone(string name) => name.Contains("Arm", StringComparison.Ordinal) || name.Contains("Hand", StringComparison.Ordinal);

    /// <summary>The game clip, when given, must be on the same skeleton and of the same length to be compared tick by tick.</summary>
    static Motion? Same(Motion m, Motion? game) => game is not null && game.Skeleton == m.Skeleton ? game : null;

    // ---- the checks ----

    /// <summary>Feet in the floor: the lowest body joint in the clip's world over every tick.</summary>
    public static CheckResult FeetInFloor(Motion m, Motion? game = null)
    {
        const string Name = "feet in the floor";
        var (z, t, b) = Lowest(m);
        float warnAt = -FloorWarnCm / 100, failAt = -FloorFailCm / 100;
        string own = "";
        if (Same(m, game) is { } g)
        {
            var (gz, _, _) = Lowest(g);
            warnAt = MathF.Min(warnAt, gz - 0.005f);
            failAt = MathF.Min(failAt, gz - 0.02f);
            own = $"; the game's clip goes down to {F(gz * 100)} cm";
        }
        var level = z >= warnAt ? CheckLevel.Pass : z >= failAt ? CheckLevel.Warn : CheckLevel.Fail;
        return new CheckResult(Name, level, $"the lowest joint {F(z * 100)} cm{own} (warn below {F(warnAt * 100)}, fail below {F(failAt * 100)} cm)", t, m.Skeleton.Bones[b].Name);
    }

    static readonly string[] FootJoints = ["LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase"];

    /// <summary>The lowest foot joint (feet and toes) in the clip's world at each tick; null when the skeleton lacks one of them.</summary>
    static float[]? LowestFoot(Motion m)
    {
        var ix = FootJoints.Select(m.Bone).ToArray();
        if (ix.Any(i => i < 0)) return null;
        var z = new float[m.LastTick + 1];
        for (int t = 0; t <= m.LastTick; t++) z[t] = ix.Min(b => m.World(t, b).Position.Z);
        return z;
    }

    /// <summary>Feet off the floor: a body that floats where the game's clip stands. Against the game clip only (see
    /// <see cref="FloatWarnCm"/>): the lowest foot over the whole clip against the game's lowest, and tick by tick where the game's feet
    /// are down.</summary>
    public static CheckResult FeetOffTheFloor(Motion m, Motion? game = null)
    {
        const string Name = "feet off the floor";
        if (Same(m, game) is not { } g || g.LastTick != m.LastTick) return new CheckResult(Name, CheckLevel.Pass, "not measured: no game clip of the same length to compare");
        if (LowestFoot(m) is not { } mz || LowestFoot(g) is not { } gz) return new CheckResult(Name, CheckLevel.Pass, "not measured: the skeleton lacks a foot bone");
        float gMin = gz.Min(), mMin = mz.Min();
        if (gMin * 100 > GroundedCm)
            return new CheckResult(Name, CheckLevel.Pass, $"the game's clip keeps its feet {F(gMin * 100)} cm or more above the floor (a jump or a climb), so it isn't compared");
        int mAt = Array.IndexOf(mz, mMin);
        double lift = (mMin - gMin) * 100;
        string limits = $"warn past {F(FloatWarnCm, "F0")}, fail past {F(FloatFailCm, "F0")} cm";
        // tick by tick, where the game's feet are down
        float down = MathF.Max(gMin, 0) + FeetDownCm / 100;
        double worst = 0; int at = 0;
        for (int t = 0; t <= m.LastTick; t++)
            if (gz[t] <= down && (mz[t] - gz[t]) * 100 > worst) { worst = (mz[t] - gz[t]) * 100; at = t; }
        var whole = lift > FloatFailCm ? CheckLevel.Fail : lift > FloatWarnCm ? CheckLevel.Warn : CheckLevel.Pass;
        var ticks = worst > FloatTickFailCm ? CheckLevel.Fail : worst > FloatTickWarnCm ? CheckLevel.Warn : CheckLevel.Pass;
        string tickLimits = $"warn past {F(FloatTickWarnCm, "F0")}, fail past {F(FloatTickFailCm, "F0")} cm";
        if (whole != CheckLevel.Pass && whole >= ticks)
            return new CheckResult(Name, whole,
                $"the feet never come lower than {F(mMin * 100)} cm, {F(lift)} cm above the game clip's lowest ({F(gMin * 100)} cm): the body floats ({limits})", mAt, "the feet");
        if (ticks != CheckLevel.Pass)
            return new CheckResult(Name, ticks, $"at tick {at} the feet are {F(worst)} cm above the game clip's, where its feet are down (within {F(FeetDownCm, "F0")} cm of the floor): "
                + $"the body floats there ({tickLimits})", at, "the feet");
        return new CheckResult(Name, CheckLevel.Pass, $"the feet come down to {F(mMin * 100)} cm, the game clip's to {F(gMin * 100)} cm ({limits}); where its feet are down, at most {F(worst)} cm above them ({tickLimits})");
    }

    static (float Z, int Tick, int Bone) Lowest(Motion m)
    {
        float z = float.MaxValue; int tick = 0, bone = 0;
        var body = BodyBones(m.Skeleton).ToArray();
        for (int t = 0; t <= m.LastTick; t++)
            foreach (int b in body)
            {
                float v = m.World(t, b).Position.Z;
                if (v < z) { z = v; tick = t; bone = b; }
            }
        return (z == float.MaxValue ? 0 : z, tick, bone);
    }

    /// <summary>Joint limits: every measure of <see cref="JointAngles"/> within the human range, or the game clip's own; a knee or an elbow
    /// bent out of its hinge's plane, and an ankle rolled, within the game clip's own range by the margins of
    /// <see cref="JointAngles.OutOfPlane"/>.</summary>
    public static CheckResult JointLimits(Motion m, Motion? game = null)
    {
        const string Name = "joint limits";
        var g = Same(m, game);
        var mm = JointAngles.Measure(m, g);
        if (mm is null) return new CheckResult(Name, CheckLevel.Pass, "not measured: the skeleton lacks a bone the measures need");
        var gm = g is null ? null : JointAngles.Measure(g, g);
        var level = CheckLevel.Pass; float worst = 0; string what = "", joint = ""; int at = 0;
        void Take(CheckLevel l, float past, int tick, string k, string text)
        {
            if (l < level || (l == level && past <= worst)) return;
            level = l; worst = past; at = tick; joint = k; what = text;
        }
        string limits = $"warn up to {F(JointFailDeg, "F0")}, fail beyond";
        foreach (var (k, v) in mm)
        {
            var gk = gm?.GetValueOrDefault(k);
            if (JointAngles.OutOfPlane.TryGetValue(JointAngles.Kind(k), out var op))
            {
                // out of the hinge's plane (a knee or an elbow bent sideways, an ankle rolled): held to the game clip's own range, with the
                // hinge's own margins
                float glo = MathF.Min(0, gk?.Lo ?? 0), ghi = MathF.Max(0, gk?.Hi ?? 0), pl = glo - v.Lo, ph = v.Hi - ghi;
                float past = MathF.Max(pl, ph);
                var ol = past > op.Fail ? CheckLevel.Fail : past > op.Warn ? CheckLevel.Warn : CheckLevel.Pass;
                if (ol != CheckLevel.Pass)
                {
                    float val = pl >= ph ? v.Lo : v.Hi;
                    string how = JointAngles.Kind(k) == "ankle roll" ? "the foot turned about its own length" : "bent out of its hinge's plane";
                    Take(ol, past, pl >= ph ? v.LoTick : v.HiTick, k, $"{k} {F(val, "F0")} deg ({how}), "
                        + (g is null ? "none without a game clip" : $"the game clip's own {F(glo, "F0")} to {F(ghi, "F0")}")
                        + $": {F(past)} deg past it (warn past {F(op.Warn, "F0")}, fail past {F(op.Fail, "F0")})");
                }
                continue;
            }
            var (lo, hi) = JointAngles.Allowed(k, gk);
            float under = lo - v.Lo, over = v.Hi - hi;
            CheckLevel Past(float p) => p <= 0.01f ? CheckLevel.Pass : p <= JointFailDeg ? CheckLevel.Warn : CheckLevel.Fail;
            if (k.EndsWith("elbow", StringComparison.Ordinal))
            {
                // bent backwards through straight: held to the game clip's own backward bend (none, where it never bends that way)
                float own = MathF.Max(0, -(gk?.Lo ?? 0)), back = -v.Lo - own;
                var bl = back > ElbowBackFailDeg ? CheckLevel.Fail : back > ElbowBackWarnDeg ? CheckLevel.Warn : CheckLevel.Pass;
                if (bl != CheckLevel.Pass)
                    Take(bl, back, v.LoTick, k, $"{k} {F(v.Lo, "F0")} deg: bent backwards {F(-v.Lo, "F0")} deg through straight, the game clip's own {F(own, "F0")}: {F(back)} deg past it "
                        + $"(warn past {F(ElbowBackWarnDeg, "F0")}, fail past {F(ElbowBackFailDeg, "F0")})");
            }
            else Take(Past(under), under, v.LoTick, k, $"{k} {F(v.Lo, "F0")} deg, allowed {F(lo, "F0")} to {F(hi, "F0")}: {F(under)} deg past it ({limits})");
            Take(Past(over), over, v.HiTick, k, $"{k} {F(v.Hi, "F0")} deg, allowed {F(lo, "F0")} to {F(hi, "F0")}: {F(over)} deg past it ({limits})");
        }
        if (level == CheckLevel.Pass) return new CheckResult(Name, CheckLevel.Pass, $"every joint within a human range{(g is null ? "" : " or the game clip's own")}");
        return new CheckResult(Name, level, what, at, joint);
    }

    /// <summary>Length: the motion's last tick against the slot's.</summary>
    public static CheckResult Length(int ticks, int slotTicks)
    {
        const string Name = "length";
        return ticks == slotTicks
            ? new CheckResult(Name, CheckLevel.Pass, $"{ticks} ticks ({F(ticks / 60.0, "F3")} s), as long as the clip it replaces")
            : new CheckResult(Name, CheckLevel.Fail, $"{ticks} ticks ({F(ticks / 60.0, "F3")} s), the clip it replaces lasts {slotTicks} ({F(slotTicks / 60.0, "F3")} s): it must be the same length");
    }

    /// <summary>The fastest local turn a tick of any keyed bone, its bone and tick.</summary>
    static (double Deg, int Tick, int Bone) Fastest(Motion m, Func<int, bool>? only = null)
    {
        double best = 0; int bt = 0, bb = 0;
        for (int b = 0; b < m.Skeleton.Bones.Count; b++)
        {
            if (!m.RotationKeyed[b] || (only is not null && !only(b))) continue;
            for (int t = 1; t <= m.LastTick; t++)
            {
                double d = GltfAxes.AngleDeg(m.Local(t - 1, b).Rotation, m.Local(t, b).Rotation);
                if (d > best) { best = d; bt = t; bb = b; }
            }
        }
        return (best, bt, bb);
    }

    static double MoverStep(Motion m, out int tick)
    {
        double best = 0; tick = 0;
        for (int t = 1; t <= m.LastTick; t++)
        {
            double d = Vector3.Distance(m.Mover(t - 1).Position, m.Mover(t).Position) * 100;
            if (d > best) { best = d; tick = t; }
        }
        return best;
    }

    /// <summary>The body region a bone's speed is compared within: so a fast finger in the game's clip doesn't allow a fast spine.</summary>
    static string Region(string name) =>
        name.Contains("Hand", StringComparison.Ordinal) && !name.EndsWith("Hand", StringComparison.Ordinal) ? "fingers"
        : name.Contains("Arm", StringComparison.Ordinal) || name.Contains("Shoulder", StringComparison.Ordinal) || name.EndsWith("Hand", StringComparison.Ordinal) ? "arms"
        : name.Contains("Leg", StringComparison.Ordinal) || name.Contains("Foot", StringComparison.Ordinal) || name.Contains("Toe", StringComparison.Ordinal) ? "legs"
        : name.Contains("Neck", StringComparison.Ordinal) || name.Contains("Head", StringComparison.Ordinal) ? "neck and head"
        : name.Contains("Spine", StringComparison.Ordinal) || name.Contains("Hips", StringComparison.Ordinal) ? "spine and hips"
        : "others";

    /// <summary>Speed against the slot's own, region by region (spine and hips, neck and head, arms, legs, fingers, the rest): each region's
    /// fastest bone's turn a tick against the game clip's fastest in the same region times <see cref="SpeedFactor"/> (at least
    /// <see cref="SpeedFloorDeg"/>), and the mover's travel a tick against the game clip's times the same factor (at least 1 cm). Faster
    /// warns: the game may blend it badly or the eye may see a jerk.</summary>
    public static CheckResult Speed(Motion m, Motion? game = null)
    {
        const string Name = "speed";
        var (d, t, b) = Fastest(m);
        double mv = MoverStep(m, out int mt);
        if (Same(m, game) is not { } g)
            return new CheckResult(Name, CheckLevel.Pass, $"the fastest bone turns {F(d, "F2")} deg a tick, the mover travels {F(mv, "F2")} cm a tick (no game clip to compare)", t, m.Skeleton.Bones[b].Name);
        var s = m.Skeleton;
        double worstRatio = -1; string text = ""; int at = t; string bone = s.Bones[b].Name; bool fast = false;
        foreach (var region in s.Bones.Select(x => Region(x.Name)).Distinct())
        {
            bool In(int i) => Region(s.Bones[i].Name) == region;
            var (rd, rt, rb) = Fastest(m, In);
            if (rd <= 0) continue;
            var (gd, _, gb) = Fastest(g, In);
            double limit = Math.Max(gd * SpeedFactor, SpeedFloorDeg), ratio = rd / limit;
            if (ratio <= worstRatio) continue;
            worstRatio = ratio; at = rt; bone = s.Bones[rb].Name; fast = rd > limit + 1e-6;
            text = $"the fastest in the {region}, {bone}, turns {F(rd, "F2")} deg a tick, the game clip's fastest there"
                + (gd > 0 ? $" ({s.Bones[gb].Name}) {F(gd, "F2")}" : " 0") + $" (limit {F(limit, "F2")})";
        }
        if (text.Length == 0) text = "no bone turns";
        double gmv = MoverStep(g, out _);
        double mlimit = Math.Max(gmv * SpeedFactor, 1);
        text += $"; the mover {F(mv, "F2")} cm a tick, the game clip's {F(gmv, "F2")} (limit {F(mlimit, "F2")})";
        bool moverFast = mv > mlimit + 1e-6;
        bool ok = !fast && !moverFast;
        return new CheckResult(Name, ok ? CheckLevel.Pass : CheckLevel.Warn, text, fast || ok ? at : mt, fast || ok ? bone : "the mover");
    }

    /// <summary>The mover's heading at every tick: its turn about the world's up (the swing-twist split about it, so a lean doesn't count),
    /// followed through ±180 so a turn past it reads on.</summary>
    public static double[] Heading(Motion m)
    {
        var h = new double[m.LastTick + 1];
        for (int t = 0; t <= m.LastTick; t++)
        {
            double v = TwistDeg(m.Mover(t).Rotation, Vector3.UnitZ);
            if (t > 0) { while (v - h[t - 1] > 180) v -= 360; while (v - h[t - 1] < -180) v += 360; }
            h[t] = v;
        }
        return h;
    }

    static (double Deg, int Tick) LargestStep(double[] h)
    {
        double best = 0; int at = 0;
        for (int t = 1; t < h.Length; t++) if (Math.Abs(h[t] - h[t - 1]) > best) { best = Math.Abs(h[t] - h[t - 1]); at = t; }
        return (best, at);
    }

    /// <summary>The mover's turn (see <see cref="TurnWarnFactor"/>): its heading's change in one tick against the game clip's largest, and
    /// its turn from its start against the game clip's at the same tick (on a clip of another length, the furthest each turns from its
    /// start). Neither pops nor speed sees it: they read the bones' own turns and the mover's place.</summary>
    public static CheckResult MoverTurn(Motion m, Motion? game = null)
    {
        const string Name = "the mover's turn";
        var h = Heading(m);
        var (step, st) = LargestStep(h);
        var g = Same(m, game);
        var gh = g is null ? null : Heading(g);
        double gstep = gh is null ? 0 : LargestStep(gh).Deg;
        double warn = gstep * TurnWarnFactor + TurnWarnDeg, fail = gstep * TurnFailFactor + TurnFailDeg;
        string stepText = $"the mover's heading turns at most {F(step, "F2")} deg in one tick" + (gh is null ? "" : $", the game clip's {F(gstep, "F2")}")
            + $" (warn past {F(warn)}, fail past {F(fail)})";
        if (step > warn)
            return new CheckResult(Name, step > fail ? CheckLevel.Fail : CheckLevel.Warn, $"{stepText}: the character's facing snaps round there", st, "the mover");
        if (gh is null) return new CheckResult(Name, CheckLevel.Pass, stepText + " (no game clip to compare its whole turn)");
        // the whole turn: from the start heading, against the game clip's at the same tick (or the furthest each turns, lengths apart)
        double apart = 0; int at = 0; string turnText;
        if (gh.Length == h.Length)
        {
            for (int t = 0; t < h.Length; t++)
            {
                double d = Math.Abs(h[t] - h[0] - (gh[t] - gh[0]));
                if (d > apart) { apart = d; at = t; }
            }
            turnText = $"its turn from its start stays within {F(apart, "F0")} deg of the game clip's at the same tick";
        }
        else
        {
            double Far(double[] x, out int tick) { double f = 0; tick = 0; for (int t = 0; t < x.Length; t++) if (Math.Abs(x[t] - x[0]) > f) { f = Math.Abs(x[t] - x[0]); tick = t; } return f; }
            double mf = Far(h, out at), gf = Far(gh, out _);
            apart = mf - gf;
            turnText = $"it turns at most {F(mf, "F0")} deg from its start, the game clip's {F(gf, "F0")}";
        }
        turnText += $" (warn past {F(TurnApartDeg, "F0")} apart)";
        if (apart > TurnApartDeg) return new CheckResult(Name, CheckLevel.Warn, $"{turnText}: a turn the game's clip doesn't make; {stepText}", at, "the mover");
        return new CheckResult(Name, CheckLevel.Pass, $"{stepText}; {turnText}");
    }

    /// <summary>The human twist range (plus and minus, degrees, from the rest) about a bone's own long axis, by its region: allowed always,
    /// and widened to the game clip's own range where that goes further.</summary>
    public static readonly IReadOnlyDictionary<string, float> HumanTwistDeg = new Dictionary<string, float>
    {
        ["Head"] = 80, ["Neck"] = 60, ["ForeArm"] = 90, ["Arm"] = 90, ["Spine"] = 45, ["UpLeg"] = 45, ["Leg"] = 30, ["Foot"] = 30, ["Hand"] = 45, ["Shoulder"] = 30,
    };

    /// <summary>The bones the twist check measures, with their human range: the spine, neck and head, and each side's shoulder, arm,
    /// forearm, hand, thigh, shin and foot, where the skeleton has them.</summary>
    static IEnumerable<(int Bone, float Human)> TwistBones(Skeleton s)
    {
        var names = new List<(string, string)> { ("Spine", "Spine"), ("Spine1", "Spine"), ("Spine2", "Spine"), ("Neck", "Neck"), ("Head", "Head") };
        foreach (var sd in new[] { "Left", "Right" })
            foreach (var k in new[] { "Shoulder", "Arm", "ForeArm", "Hand", "UpLeg", "Leg", "Foot" }) names.Add((sd + k, k));
        foreach (var (n, k) in names)
            if (s.IndexOf(Hashing.Crc32.Of(n)) is var i and >= 0) yield return (i, HumanTwistDeg[k]);
    }

    /// <summary>A bone's own long axis in its own frame: towards its one child where it has exactly one (a limb), else along its own
    /// offset from its parent (the spine, the head, a hand with its fingers).</summary>
    public static Vector3 TwistAxis(Skeleton s, int bone)
    {
        var kids = s.Bones.Where(x => x.ParentIndex == bone && !x.FromAddOn && x.LocalRest.Position.LengthSquared() > 1e-10f).ToList();
        var r = s.Bones[bone].LocalRest;
        Vector3 a = kids.Count == 1 ? kids[0].LocalRest.Position : Vector3.Transform(r.Position, Quaternion.Inverse(r.Rotation));
        return a.LengthSquared() > 1e-12f ? Vector3.Normalize(a) : Vector3.UnitX;
    }

    /// <summary>The twist of <paramref name="q"/> (a turn in the bone's own frame) about <paramref name="axis"/>, by the swing-twist split,
    /// in degrees from -180 to 180.</summary>
    public static double TwistDeg(Quaternion q, Vector3 axis)
    {
        double d = q.X * axis.X + q.Y * axis.Y + q.Z * axis.Z;
        double a = 2 * Math.Atan2(d, q.W) * 180 / Math.PI;
        while (a > 180) a -= 360;
        while (a <= -180) a += 360;
        return a;
    }

    /// <summary>The twist chains: bones whose twists add up to one turn of the body (the head turned on the neck, the chest on the pelvis),
    /// each with its human range (plus and minus, degrees) for the twists summed along it. A turn split over several bones, each within its
    /// own range, is held to the chain's: the head turned right round as Neck 90 and Head 90 fails here. Widened to the game clip's own sum
    /// where that goes further.</summary>
    public static readonly IReadOnlyList<(string Name, string[] Bones, float HumanDeg)> TwistChains =
    [
        ("the neck and head", ["Neck", "Head"], 90),
        ("the spine", ["Spine", "Spine1", "Spine2"], 60),
        ("the hips to the head", ["Spine", "Spine1", "Spine2", "Neck", "Head"], 120),
    ];

    /// <summary>The bone a twist bone's turn is measured against: its parent, but the hips for a lowest spine bone that hangs from the root
    /// beside them (the game's skeleton does that), so the spine's twist is the chest's against the pelvis.</summary>
    static int TwistParent(Skeleton s, int bone)
    {
        int p = s.Bones[bone].ParentIndex;
        if (s.Bones[bone].Name != "Spine" || p < 0 || !s.Bones[p].IsRoot) return p;
        int hips = s.IndexOf(Hashing.Crc32.Of("Hips"));
        return hips >= 0 && hips != bone && s.Bones[hips].ParentIndex == p ? hips : p;
    }

    /// <summary>A bone's turn against its twist parent's (<see cref="TwistParent"/>), at a tick; the rest pose's when <paramref name="m"/> is
    /// null.</summary>
    static Quaternion TwistLocal(Skeleton s, Motion? m, Transform[] restModel, int bone, int parent, int tick)
    {
        if (m is null) return parent == s.Bones[bone].ParentIndex ? s.Bones[bone].LocalRest.Rotation
            : Quaternion.Normalize(Quaternion.Inverse(restModel[parent].Rotation) * restModel[bone].Rotation);
        return parent == s.Bones[bone].ParentIndex ? m.Local(tick, bone).Rotation
            : Quaternion.Normalize(Quaternion.Inverse(m.Model(tick, parent).Rotation) * m.Model(tick, bone).Rotation);
    }

    /// <summary>Each twist bone's twist over every tick, followed through ±180 so a turn past it reads as one. Without a reference: the
    /// swing-twist split of the bone's turn from its rest. With a reference of the same length (the game's clip), the reference's own twist
    /// from the rest plus the twist of the change from the reference's turn at that tick (<c>reference(t)^-1 * m(t)</c>), split about the
    /// bone's own long axis: so a pure bend added to the game's pose (an elbow folded further) adds no twist, where a split from the rest
    /// would read the swing's path as one.</summary>
    static Dictionary<int, double[]> TwistSeries(Motion m, Motion? reference, Dictionary<int, double[]>? referenceSeries = null)
    {
        var s = m.Skeleton;
        var restModel = new Transform[s.Bones.Count];
        s.ComputeModel(s.RestLocals(), restModel);
        var refSeries = reference is not null && reference.Skeleton == s && reference.LastTick == m.LastTick ? referenceSeries ?? TwistSeries(reference, null) : null;
        var o = new Dictionary<int, double[]>();
        foreach (var (b, _) in TwistBones(s))
        {
            var axis = TwistAxis(s, b);
            int p = TwistParent(s, b);
            var rest = Quaternion.Inverse(TwistLocal(s, null, restModel, b, p, 0));
            var a = new double[m.LastTick + 1];
            double prev = 0;
            for (int t = 0; t <= m.LastTick; t++)
            {
                var q = TwistLocal(s, m, restModel, b, p, t);
                double v = refSeries is null ? TwistDeg(Quaternion.Normalize(rest * q), axis)
                    : TwistDeg(Quaternion.Normalize(Quaternion.Inverse(TwistLocal(s, reference, restModel, b, p, t)) * q), axis);
                if (t > 0) { while (v - prev > 180) v -= 360; while (v - prev < -180) v += 360; }
                prev = v;
                a[t] = v;
            }
            if (refSeries is not null) for (int t = 0; t <= m.LastTick; t++) a[t] += refSeries[b][t];
            o[b] = a;
        }
        return o;
    }

    static (double Lo, double Hi, int LoTick, int HiTick) Extent(double[] a)
    {
        double lo = double.MaxValue, hi = double.MinValue; int lt = 0, ht = 0;
        for (int t = 0; t < a.Length; t++)
        {
            if (a[t] < lo) { lo = a[t]; lt = t; }
            if (a[t] > hi) { hi = a[t]; ht = t; }
        }
        return (lo, hi, lt, ht);
    }

    /// <summary>A chain's twists summed tick by tick, over the bones of it the skeleton has; null when it lacks one.</summary>
    static double[]? ChainSum(Skeleton s, Dictionary<int, double[]> series, string[] bones)
    {
        var ix = bones.Select(n => s.IndexOf(Hashing.Crc32.Of(n))).ToArray();
        if (ix.Any(i => i < 0 || !series.ContainsKey(i))) return null;
        var sum = new double[series[ix[0]].Length];
        foreach (int i in ix) for (int t = 0; t < sum.Length; t++) sum[t] += series[i][t];
        return sum;
    }

    /// <summary>Twists: each bone's turn about its own long axis (the head's yaw, a forearm's roll), and the twists summed along each chain
    /// of <see cref="TwistChains"/> (a turn split over the neck and the head, or over the spine), against the wider of the game clip's own
    /// range and the human range, which no joint angle measured from the joints' places sees (see <see cref="TwistWarnDeg"/>). Against a
    /// game clip of the same length the twist is the game's own plus what the change from the game's pose adds (see
    /// <see cref="TwistSeries"/>).</summary>
    public static CheckResult Twist(Motion m, Motion? game = null)
    {
        const string Name = "twists";
        var g = Same(m, game);
        var gs = g is null ? null : TwistSeries(g, null);
        var ms = TwistSeries(m, g, gs);
        if (ms.Count == 0) return new CheckResult(Name, CheckLevel.Pass, "not measured: the skeleton has none of the bones measured");
        var human = TwistBones(m.Skeleton).ToDictionary(x => x.Bone, x => x.Human);
        double worst = 0; int at = 0; string bone = "", what = "";
        string Range(double lo, double hi, double h) =>
            $"{F(lo, "F0")} to {F(hi, "F0")} deg{(gs is null ? ", the human range" : $", the wider of the game clip's own and the human range ({F(h, "F0")} either way)")}";
        foreach (var (b, series) in ms)
        {
            var r0 = Extent(series);
            double h = human[b];
            var (lo, hi) = gs is not null && gs.TryGetValue(b, out var gx) && Extent(gx) is var x ? (Math.Min(x.Lo, -h), Math.Max(x.Hi, h)) : (-h, h);
            // a bone's twist of 180 reads as -180 as well (the same turn): the new range is taken a whole turn either way where that lies nearer
            var r = new[] { -360.0, 0, 360 }.Select(k => (Lo: r0.Lo + k, Hi: r0.Hi + k, r0.LoTick, r0.HiTick))
                .MinBy(c => Math.Max(Math.Max(lo - c.Lo, c.Hi - hi), 0) + (c.Lo == r0.Lo ? 0 : 1e-9));
            double under = lo - r.Lo, over = r.Hi - hi;
            string name = m.Skeleton.Bones[b].Name;
            if (under > worst) { worst = under; at = r.LoTick; bone = name; what = $"{name} twists {F(r.Lo, "F0")} deg about its own axis, against {Range(lo, hi, h)}"; }
            if (over > worst) { worst = over; at = r.HiTick; bone = name; what = $"{name} twists {F(r.Hi, "F0")} deg about its own axis, against {Range(lo, hi, h)}"; }
        }
        // the chains: several bones' twists in a row are one turn, and no whole turn is taken off (each bone's own is a real twist)
        foreach (var (chain, bones, hf) in TwistChains)
        {
            if (ChainSum(m.Skeleton, ms, bones) is not { } sum) continue;
            var r = Extent(sum);
            double h = hf;
            var (lo, hi) = gs is not null && ChainSum(m.Skeleton, gs, bones) is { } gsum && Extent(gsum) is var x ? (Math.Min(x.Lo, -h), Math.Max(x.Hi, h)) : (-h, h);
            double under = lo - r.Lo, over = r.Hi - hi;
            string label = $"{chain} ({string.Join(" + ", bones)})";
            if (under > worst) { worst = under; at = r.LoTick; bone = bones[^1]; what = $"{label} twist {F(r.Lo, "F0")} deg together, against {Range(lo, hi, h)}"; }
            if (over > worst) { worst = over; at = r.HiTick; bone = bones[^1]; what = $"{label} twist {F(r.Hi, "F0")} deg together, against {Range(lo, hi, h)}"; }
        }
        string limits = $"warn past it by {F(TwistWarnDeg, "F0")}, fail by {F(TwistFailDeg, "F0")}";
        if (worst <= TwistWarnDeg)
            return new CheckResult(Name, CheckLevel.Pass, worst <= 0.01 ? $"every twist and chain within {(g is null ? "the human range" : "the game clip's own range or the human range")} ({limits})"
                : $"the largest twist past its range: {what}, {F(worst)} deg past it ({limits})");
        return new CheckResult(Name, worst <= TwistFailDeg ? CheckLevel.Warn : CheckLevel.Fail, $"{what}: {F(worst)} deg past it ({limits})", at, bone);
    }

    /// <summary>Pops: sudden jumps from one tick to the next. A body bone's local turn in one tick past <see cref="PopDeg"/> (arms and
    /// hands <see cref="PopArmDeg"/>) fails, past half of it warns; the mover's or the hips' place jumping past <see cref="PopCm"/> fails.
    /// Where the game's clip moves a bone as fast, its own step (plus 5 degrees, or 2 cm) is the limit.</summary>
    public static CheckResult Pops(Motion m, Motion? game = null)
    {
        const string Name = "pops";
        var g = Same(m, game);
        var s = m.Skeleton;
        double worstRatio = 0; string what = "nothing jumps"; int at = 0; string? bone = null;
        var level = CheckLevel.Pass;
        foreach (int b in BodyBones(s))
        {
            if (!m.RotationKeyed[b]) continue;
            double own = 0;
            if (g is not null && g.RotationKeyed[b])
                for (int t = 1; t <= g.LastTick; t++) own = Math.Max(own, GltfAxes.AngleDeg(g.Local(t - 1, b).Rotation, g.Local(t, b).Rotation));
            double baseLimit = ArmBone(s.Bones[b].Name) ? PopArmDeg : PopDeg;
            double fail = Math.Max(baseLimit, own + 5), warn = Math.Max(baseLimit / 2, own + 5);
            for (int t = 1; t <= m.LastTick; t++)
            {
                double d = GltfAxes.AngleDeg(m.Local(t - 1, b).Rotation, m.Local(t, b).Rotation);
                var l = d > fail ? CheckLevel.Fail : d > warn ? CheckLevel.Warn : CheckLevel.Pass;
                double r = d / fail;
                if (l < level || (l == level && r <= worstRatio)) continue;
                level = l; worstRatio = r; at = t; bone = s.Bones[b].Name;
                what = $"{bone} turns {F(d)} deg in one tick (fail past {F(fail, "F0")}, warn past {F(warn, "F0")})";
            }
        }
        // the places: the mover, and the hips' local place
        int hips = m.Bone("Hips");
        foreach (var (label, f) in new (string, Func<Motion, int, Vector3>)[]
        {
            ("the mover", (x, t) => x.Mover(t).Position),
            ("Hips", (x, t) => hips >= 0 ? x.Model(t, hips).Position : Vector3.Zero),
        })
        {
            double own = 0;
            if (g is not null) for (int t = 1; t <= g.LastTick; t++) own = Math.Max(own, Vector3.Distance(f(g, t - 1), f(g, t)) * 100);
            double limit = Math.Max(PopCm, own + 2);
            for (int t = 1; t <= m.LastTick; t++)
            {
                double d = Vector3.Distance(f(m, t - 1), f(m, t)) * 100;
                if (d > limit && level != CheckLevel.Fail) { level = CheckLevel.Fail; at = t; bone = label; what = $"{label} jumps {F(d)} cm in one tick (fail past {F(limit, "F0")} cm)"; }
            }
        }
        if (level == CheckLevel.Pass) what = $"no jump: the largest turn in one tick is {F(worstRatio * 100, "F0")}% of its limit";
        return new CheckResult(Name, level, what, level == CheckLevel.Pass ? null : at, level == CheckLevel.Pass ? null : bone);
    }

    /// <summary>The segments compared by <see cref="Limbs"/>: each leg's bones against each arm's, and against the chest's and neck's (the
    /// thighs not against the lowest spine bone, which they meet at the hips).</summary>
    static readonly (string A, string B)[] Chest = [("Spine", "Spine1"), ("Spine1", "Spine2"), ("Spine2", "Neck"), ("Neck", "Head")];

    static (string, string)[] Leg(string s) => [(s + "UpLeg", s + "Leg"), (s + "Leg", s + "Foot"), (s + "Foot", s + "ToeBase")];
    static (string, string)[] Arm(string s) => [(s + "Arm", s + "ForeArm"), (s + "ForeArm", s + "Hand"), (s + "Hand", s + "HandMiddle1")];

    /// <summary>The nearest any leg bone comes to an arm or chest bone, as line segments between joints (a joint-to-joint distance can't see
    /// a wrist inside a thigh), over every tick; NaN when the skeleton lacks a bone.</summary>
    public static (double Cm, int Tick, string What) NearestLimbs(Motion m)
    {
        var pairs = new List<((int, int) A, (int, int) B, string What)>();
        int I(string n) => m.Bone(n);
        foreach (var leg in new[] { "Left", "Right" })
            foreach (var (a1, a2) in Leg(leg))
                foreach (var (b1, b2) in Arm("Left").Concat(Arm("Right")).Concat(Chest))
                {
                    // the thigh and the lowest spine bone meet at the hips: their nearness is the body's build, not a crossing
                    if (a1.EndsWith("UpLeg", StringComparison.Ordinal) && b1 == "Spine") continue;
                    int[] ix = [I(a1), I(a2), I(b1), I(b2)];
                    if (ix.Any(x => x < 0)) return (double.NaN, 0, "");
                    pairs.Add(((ix[0], ix[1]), (ix[2], ix[3]), $"{a1}-{a2} and {b1}-{b2}"));
                }
        double best = double.MaxValue; int bt = 0; string bw = "";
        for (int t = 0; t <= m.LastTick; t++)
            foreach (var (a, b, w) in pairs)
            {
                double d = SegmentDistance(m.Model(t, a.Item1).Position, m.Model(t, a.Item2).Position, m.Model(t, b.Item1).Position, m.Model(t, b.Item2).Position) * 100;
                if (d < best) { best = d; bt = t; bw = w; }
            }
        return (best, bt, bw);
    }

    /// <summary>The nearest two line segments come, by the usual clamped solution.</summary>
    public static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        if (a <= 1e-12f && e <= 1e-12f) return r.Length();
        float s, t;
        if (a <= 1e-12f) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-12f) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                s = den > 1e-12f ? Math.Clamp((b * f - c * e) / den, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (p1 + d1 * s - (p2 + d2 * t)).Length();
    }

    /// <summary>The nearest the two legs come to each other, the left thigh and shin against the right's as line segments, over every tick;
    /// NaN when the skeleton lacks a leg bone.</summary>
    public static (double Cm, int Tick, string What) NearestLegs(Motion m)
    {
        var pairs = new List<((int, int) A, (int, int) B, string What)>();
        (string, string)[] Bones(string s) => [(s + "UpLeg", s + "Leg"), (s + "Leg", s + "Foot")];
        foreach (var (a1, a2) in Bones("Left"))
            foreach (var (b1, b2) in Bones("Right"))
            {
                int[] ix = [m.Bone(a1), m.Bone(a2), m.Bone(b1), m.Bone(b2)];
                if (ix.Any(x => x < 0)) return (double.NaN, 0, "");
                pairs.Add(((ix[0], ix[1]), (ix[2], ix[3]), $"{a1}-{a2} and {b1}-{b2}"));
            }
        double best = double.MaxValue; int bt = 0; string bw = "";
        for (int t = 0; t <= m.LastTick; t++)
            foreach (var (a, b, w) in pairs)
            {
                double d = SegmentDistance(m.Model(t, a.Item1).Position, m.Model(t, a.Item2).Position, m.Model(t, b.Item1).Position, m.Model(t, b.Item2).Position) * 100;
                if (d < best) { best = d; bt = t; bw = w; }
            }
        return (best, bt, bw);
    }

    /// <summary>Limbs as segments: the legs against the arms and the chest, and the legs against each other (<see cref="LegsWarnCm"/>). The
    /// mesh isn't read, so this is the bones' distance, not the skin's (flesh meets at roughly 10 cm).</summary>
    public static CheckResult Limbs(Motion m, Motion? game = null)
    {
        const string Name = "limbs";
        var (cm, t, what) = NearestLimbs(m);
        if (double.IsNaN(cm)) return new CheckResult(Name, CheckLevel.Pass, "not measured: the skeleton lacks a limb bone");
        double warn = LimbWarnCm, fail = LimbFailCm;
        string own = "";
        var g = Same(m, game);
        if (g is not null && NearestLimbs(g) is var (gcm, _, _) && !double.IsNaN(gcm))
        {
            warn = Math.Min(warn, gcm - 1);
            fail = Math.Min(fail, gcm - 1);
            own = $"; the game's clip comes to {F(gcm)} cm";
        }
        var level = cm >= warn ? CheckLevel.Pass : cm >= fail ? CheckLevel.Warn : CheckLevel.Fail;
        string arms = $"the legs' bones come within {F(cm)} cm of the arms' or the chest's ({what}){own} (warn under {F(warn)}, fail under {F(fail)} cm)";
        // the legs against each other: held to the game clip's own nearest
        var (lcm, lt, lwhat) = NearestLegs(m);
        if (double.IsNaN(lcm)) return new CheckResult(Name, level, arms, t, what.Split(' ')[0]);
        double lwarn = LegsWarnCm, lfail = LegsFailCm;
        string lown = "";
        if (g is not null && NearestLegs(g) is var (glcm, _, _) && !double.IsNaN(glcm))
        {
            lfail = Math.Min(LegsFailCm, glcm - 1);
            // where the game's legs come within 7 cm the warn limit would fall under the fail limit: it stops there
            lwarn = Math.Max(glcm - LegsWarnCm, lfail);
            lown = $", the game's clip's {F(glcm)} cm";
        }
        var llevel = lcm < lfail ? CheckLevel.Fail : lcm < lwarn ? CheckLevel.Warn : CheckLevel.Pass;
        string legs = $"the legs come within {F(lcm)} cm of each other ({lwhat}){lown} (warn under {F(lwarn)}, fail under {F(lfail)} cm)";
        if (llevel > level)
            return new CheckResult(Name, llevel, $"{legs}{(llevel == CheckLevel.Fail ? ": one leg passes through the other" : "")}; {arms}", lt, lwhat.Split(' ')[0]);
        return new CheckResult(Name, level, $"{arms}; {legs}", t, what.Split(' ')[0]);
    }

    /// <summary>Read-back: <paramref name="written"/> (a record decoded back) against <paramref name="expected"/> (the motion meant to be
    /// written), every bone's place and turn in the clip's world at every tick, the mover included.</summary>
    public static CheckResult ReadBack(Motion expected, Motion written)
    {
        const string Name = "read-back";
        if (expected.Skeleton != written.Skeleton || expected.LastTick != written.LastTick)
            return new CheckResult(Name, CheckLevel.Fail, $"the record reads back as {written.LastTick} ticks, {expected.LastTick} were written");
        double wc = 0, wd = 0; int tc = 0, td = 0; string bc = "", bd = "";
        var s = expected.Skeleton;
        for (int t = 0; t <= expected.LastTick; t++)
            for (int b = 0; b < s.Bones.Count; b++)
            {
                var e = expected.World(t, b);
                var g = written.World(t, b);
                double cm = Vector3.Distance(e.Position, g.Position) * 100, deg = GltfAxes.AngleDeg(e.Rotation, g.Rotation);
                if (!(cm <= wc)) { wc = double.IsNaN(cm) ? double.PositiveInfinity : cm; tc = t; bc = s.Bones[b].Name; }
                if (!(deg <= wd)) { wd = double.IsNaN(deg) ? double.PositiveInfinity : deg; td = t; bd = s.Bones[b].Name; }
            }
        var level = wc <= ReadBackPassCm && wd <= ReadBackPassDeg ? CheckLevel.Pass : wc <= ReadBackWarnCm && wd <= ReadBackWarnDeg ? CheckLevel.Warn : CheckLevel.Fail;
        bool byCm = wc / ReadBackPassCm >= wd / ReadBackPassDeg;
        string Where(double v, string b, int t) => v > 0 ? $" ({b}, tick {t})" : "";
        return new CheckResult(Name, level, $"the written record decodes back to the motion within {F(wc, "F4")} cm{Where(wc, bc, tc)} and {F(wd, "F4")} deg{Where(wd, bd, td)}, "
            + $"every bone at every tick (pass within {F(ReadBackPassCm)} cm and {F(ReadBackPassDeg)} deg, warn within {F(ReadBackWarnCm, "F0")} and {F(ReadBackWarnDeg, "F0")})",
            wc + wd > 0 ? (byCm ? tc : td) : null, wc + wd > 0 ? (byCm ? bc : bd) : null);
    }

    /// <summary>A hand moves in an armed slot: the hands' places and turns relative to the mover against the game clip's. In a slot holding
    /// hand tracks (<see cref="HandTracks"/>) the game aims the hand by the slot's own offset, so a moved hand may be pulled back in game.</summary>
    public static CheckResult HandMoves(Motion m, Motion game, IReadOnlyCollection<uint> slotTracks)
    {
        const string Name = "hands on a clip that aims them";
        var held = HandTracks.In(slotTracks);
        if (held.Count == 0) return new CheckResult(Name, CheckLevel.Pass, "this clip has no hand track");
        if (Same(m, game) is not { } g || g.LastTick != m.LastTick) return new CheckResult(Name, CheckLevel.Warn, "the game's clip couldn't be compared");
        double wc = 0, wd = 0; int at = 0; string hand = "";
        foreach (var name in new[] { "LeftHand", "RightHand" })
        {
            int b = m.Bone(name);
            if (b < 0) continue;
            for (int t = 0; t <= m.LastTick; t++)
            {
                var a = m.Model(t, b); var o = g.Model(t, b);
                double cm = Vector3.Distance(a.Position, o.Position) * 100, deg = GltfAxes.AngleDeg(a.Rotation, o.Rotation);
                if (cm / HandCm + deg / HandDeg > wc / HandCm + wd / HandDeg) { wc = cm; wd = deg; at = t; hand = name; }
            }
        }
        string tracks = string.Join(", ", held.Select(h => h.ToString("X8")));
        if (wc <= HandCm && wd <= HandDeg)
            return new CheckResult(Name, CheckLevel.Pass, $"the hands stay where the game's clip has them (within {F(wc, "F2")} cm, {F(wd, "F2")} deg); the clip's hand tracks {tracks} stay as they are");
        return new CheckResult(Name, CheckLevel.Warn, $"{hand} moves up to {F(wc)} cm and {F(wd)} deg from the game's clip, in a clip holding the hand tracks {tracks}: "
            + "the game aims the hand by the clip's own offset, which stays as it was, so in game the hand may stay where the old clip had it", at, hand);
    }

    /// <summary>The foot plants: where the slot holds a plant track, how far the toe moves from the game clip's while its plant says
    /// planted. The plants are kept as they are, so a foot moved during them may be pinned back by the game.</summary>
    public static CheckResult Plants(Motion m, Motion game, IReadOnlyList<(int Slot, int Tick, int Length)> plants)
    {
        const string Name = "feet during the plants";
        if (plants.Count == 0) return new CheckResult(Name, CheckLevel.Pass, "this clip has no foot plant");
        if (Same(m, game) is not { } g || g.LastTick != m.LastTick) return new CheckResult(Name, CheckLevel.Warn, "the game's clip couldn't be compared");
        double worst = 0; int at = 0; string toe = "";
        foreach (var (slot, tick, len) in plants)
        {
            string name = slot == 0 ? "LeftToeBase" : "RightToeBase";
            int b = m.Bone(name);
            if (b < 0) continue;
            for (int t = Math.Max(0, tick); t <= Math.Min(m.LastTick, tick + len); t++)
            {
                double d = Vector3.Distance(m.World(t, b).Position, g.World(t, b).Position) * 100;
                if (d > worst) { worst = d; at = t; toe = name; }
            }
        }
        return worst <= 3
            ? new CheckResult(Name, CheckLevel.Pass, $"while planted the toes stay within {F(worst)} cm of the game clip's (limit 3 cm)")
            : new CheckResult(Name, CheckLevel.Warn, $"{toe} moves {F(worst)} cm from the game clip's place while the clip's plant holds it (limit 3 cm): the plants are kept as they are, so the game may pin the foot", at, toe);
    }

    /// <summary>The motion checks that need no file: feet in the floor, feet off it, joint limits, twists, speed, pops, the mover's turn and
    /// limbs, each against the game clip when given. For a mirror's result give the game clip mirrored (<see cref="Motion.Mirrored"/>), so
    /// each bone is held to its counterpart's motion.</summary>
    public static IEnumerable<CheckResult> Body(Motion m, Motion? game = null)
    {
        yield return FeetInFloor(m, game);
        yield return FeetOffTheFloor(m, game);
        yield return JointLimits(m, game);
        yield return Twist(m, game);
        yield return Speed(m, game);
        yield return Pops(m, game);
        yield return MoverTurn(m, game);
        yield return Limbs(m, game);
    }
}
