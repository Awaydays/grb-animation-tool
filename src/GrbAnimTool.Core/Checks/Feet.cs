using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Checks;

/// <summary>The feet: where each one stands, which one carries the weight, whether it is planted, how far a planted one slides, and the
/// legs bent so a planted foot stays where it is (two-bone leg IK on the thigh and the knee, the foot turned to the place's own turn; the
/// bones are never stretched: a leg that can't reach its places, however its step is timed, is left as the blend has it).</summary>
public static class Feet
{
    /// <summary>One leg: the thigh (UpLeg), the shin (Leg), the foot (Foot) and the toes (ToeBase), each the child of the one before.</summary>
    public sealed record Leg(string Side, int Hip, int Knee, int Ankle, int Toe);

    /// <summary>The weight lying on the other foot counts as this many centimetres of feet apart (<see cref="Distance"/>).</summary>
    public const double WeightCm = 5;
    /// <summary>The ankles' height difference at which the weight lies wholly on the lower foot.</summary>
    public const float WeightSplitCm = 4;
    /// <summary>A foot is on the ground within this of its lowest in the motion.</summary>
    public const float GroundSlackCm = 2.5f;
    /// <summary>A foot is planted while on the ground and slower than this across it (centimetres a tick: 30 cm/s).</summary>
    public const float PlantedCmPerTick = 0.5f;
    /// <summary>A planted foot that must move no more than this between a blend's ends moves along with the blend; farther, it steps.</summary>
    public const double SlideFreeCm = 1;
    /// <summary>A step's least lift: at a quarter of the step, where the foot starts across, it stands above the ground's slack.</summary>
    public const double StepLiftCm = 3.6;
    /// <summary>The feet slide check: a note above 2 cm, a big difference above 5 cm.</summary>
    public const double NoteCm = 2, BigCm = 5;
    public const string SlideCheck = "feet slide";

    static readonly ConditionalWeakTable<Skeleton, Leg[]> LegsOf = new();

    /// <summary>The skeleton's legs, left first; a side missing a bone, or whose bones aren't each the child of the one before, is left out.</summary>
    public static IReadOnlyList<Leg> Legs(Skeleton s) => LegsOf.GetValue(s, Find);

    static Leg[] Find(Skeleton s)
    {
        var l = new List<Leg>();
        foreach (var side in new[] { "Left", "Right" })
        {
            int Ix(string b) => s.IndexOf(Hashing.Crc32.Of(side + b));
            int hip = Ix("UpLeg"), knee = Ix("Leg"), ankle = Ix("Foot"), toe = Ix("ToeBase");
            if (hip < 0 || knee < 0 || ankle < 0 || toe < 0) continue;
            if (s.Bones[knee].ParentIndex != hip || s.Bones[ankle].ParentIndex != knee || s.Bones[toe].ParentIndex != ankle) continue;
            l.Add(new Leg(side, hip, knee, ankle, toe));
        }
        return [.. l];
    }

    /// <summary>How far apart the feet stand in two frames, in centimetres: each foot's ankle and toes across the ground (in the mover's
    /// frame, so a clip's travel doesn't count), plus <see cref="WeightCm"/> times how differently the weight lies on them. 0 when the
    /// skeleton has no legs.</summary>
    public static double Distance(Motion a, int ta, Motion b, int tb)
    {
        var legs = Legs(a.Skeleton);
        if (legs.Count == 0) return 0;
        double d = 0;
        foreach (var g in legs)
            d += (Flat(a.Model(ta, g.Ankle).Position - b.Model(tb, g.Ankle).Position) + Flat(a.Model(ta, g.Toe).Position - b.Model(tb, g.Toe).Position)) * 50;
        return d + WeightCm * Math.Abs(Weight(a, ta) - Weight(b, tb));
    }

    /// <summary>Which foot carries the weight at a frame: 1 the left (its ankle the lower by <see cref="WeightSplitCm"/> or more), -1 the
    /// right, between them shared; 0 without both legs.</summary>
    public static double Weight(Motion m, int t)
    {
        var legs = Legs(m.Skeleton);
        if (legs.Count < 2) return 0;
        double dz = (m.Model(t, legs[1].Ankle).Position.Z - m.Model(t, legs[0].Ankle).Position.Z) * 100;
        return Math.Clamp(dz / WeightSplitCm, -1, 1);
    }

    static double Flat(Vector3 v) => new Vector2(v.X, v.Y).Length();

    /// <summary>A foot's lowest point in the world (its ankle or toes) at a frame.</summary>
    static float Low(Motion m, int t, Leg g) => Math.Min(m.World(t, g.Ankle).Position.Z, m.World(t, g.Toe).Position.Z);

    /// <summary>Where a foot stands across the ground in the world at a frame: half way between its ankle and its toes.</summary>
    static Vector2 Place(Motion m, int t, Leg g)
    {
        var p = (m.World(t, g.Ankle).Position + m.World(t, g.Toe).Position) * 0.5f;
        return new Vector2(p.X, p.Y);
    }

    /// <summary>Each foot's lowest point over the motion, by leg, the frames inside <paramref name="blends"/> left out (a blend may sink
    /// the very foot it slides; with no frame outside them, every frame counts).</summary>
    public static float[] Floors(Motion m, IReadOnlyList<(int From, int To)>? blends = null)
    {
        var legs = Legs(m.Skeleton);
        var f = new float[legs.Count];
        bool Inside(int t) => blends is not null && blends.Any(b => t > b.From && t < b.To);
        bool any = Enumerable.Range(0, m.Frames).Any(t => !Inside(t));
        for (int i = 0; i < legs.Count; i++)
        {
            float z = float.MaxValue;
            for (int t = 0; t <= m.LastTick; t++) if (!any || !Inside(t)) z = Math.Min(z, Low(m, t, legs[i]));
            f[i] = z;
        }
        return f;
    }

    /// <summary>Whether leg <paramref name="i"/>'s foot is planted at a frame: on the ground (within <see cref="GroundSlackCm"/> of
    /// <paramref name="floors"/>) and slower than <see cref="PlantedCmPerTick"/> across it, its speed read toward <paramref name="side"/>
    /// (-1 the frame before, 1 the frame after, 0 both); with no frame on that side, on the ground is enough (at a loop's or a line-up's
    /// last frame the pose blended into stands still: the blend's own slide there is what the tool keeps the foot from).</summary>
    public static bool Planted(Motion m, int t, int i, float[] floors, int side = 0)
    {
        var g = Legs(m.Skeleton)[i];
        if ((Low(m, t, g) - floors[i]) * 100 > GroundSlackCm) return false;
        int a = side > 0 ? t : Math.Max(0, t - 1), b = side < 0 ? t : Math.Min(m.LastTick, t + 1);
        return b == a || (Place(m, b, g) - Place(m, a, g)).Length() * 100 / (b - a) <= PlantedCmPerTick;
    }

    /// <summary>How far a planted foot slides between two frames (both included): for each foot, across each run of frames it stays on the
    /// ground, the farthest it gets from where the run began; a foot lifted and put down (a step) starts a new run. The worst of the feet,
    /// in centimetres, with the frame and the foot.</summary>
    public static (double Cm, int Tick, string Foot) Slide(Motion m, int from, int to, float[]? floors = null)
    {
        var legs = Legs(m.Skeleton);
        floors ??= Floors(m);
        double worst = 0; int at = from; string foot = "";
        from = Math.Max(0, from); to = Math.Min(m.LastTick, to);
        for (int i = 0; i < legs.Count; i++)
        {
            Vector2? anchor = null;
            for (int t = from; t <= to; t++)
            {
                bool ground = (Low(m, t, legs[i]) - floors[i]) * 100 <= GroundSlackCm;
                if (!ground) { anchor = null; continue; }
                var p = Place(m, t, legs[i]);
                anchor ??= p;
                double d = (p - anchor.Value).Length() * 100;
                if (d > worst) { worst = d; at = t; foot = legs[i].Side + "Foot"; }
            }
        }
        return (worst, at, foot);
    }

    /// <summary>The feet slide look check: how far a planted foot moves during the tool's blends (<paramref name="blends"/>, each from
    /// its first frame to its last) and, for a clip of a set, across the game's switch into the next clip (<paramref name="next"/>, blended
    /// in over <paramref name="switchTicks"/> as the game does). Null when there is nothing to measure (no blend, no next clip) or no legs.</summary>
    public static CheckResult? Check(Motion m, IReadOnlyList<(int From, int To)> blends, (Motion Clip, string Name, int Ticks)? next = null) =>
        Check(m, blends, next is { } x ? [x] : []);

    /// <summary>As the check above, with the switch into each of <paramref name="nexts"/> played (a random set may switch from a clip to
    /// any other of it): the worst of them said.</summary>
    public static CheckResult? Check(Motion m, IReadOnlyList<(int From, int To)> blends, IReadOnlyList<(Motion Clip, string Name, int Ticks)> nexts)
    {
        (Motion Clip, string Name, int Ticks)? next = null;
        double worstSwitch = -1;
        if (Legs(m.Skeleton).Count > 0)
            foreach (var each in nexts)
            {
                var (chain0, starts0) = Import.ClipSetReplace.Preview([m, each.Clip], each.Ticks);
                int a0 = starts0[1] - 1, z0 = starts0[1] + each.Ticks;
                double cm0 = Slide(chain0, a0, z0, Floors(chain0, [(a0, z0)])).Cm;
                if (cm0 > worstSwitch) { worstSwitch = cm0; next = each; }
            }
        if (Legs(m.Skeleton).Count == 0 || (blends.Count == 0 && next is null)) return null;
        var parts = new List<string>();
        double worst = 0; int? tick = null; string? bone = null;
        if (blends.Count > 0)
        {
            var floors = Floors(m, blends);
            double w = 0; int wt = 0; string wf = "";
            foreach (var (f, t) in blends)
            {
                var (cm, at, foot) = Slide(m, f, t, floors);
                if (cm > w) { w = cm; wt = at; wf = foot; }
            }
            parts.Add($"{F(w)} cm during the tool's blends");
            if (w > worst) { worst = w; tick = wt; bone = wf; }
        }
        if (next is { } nx)
        {
            var (chain, starts) = Import.ClipSetReplace.Preview([m, nx.Clip], nx.Ticks);
            int a = starts[1] - 1, z = starts[1] + nx.Ticks;
            var (cm, _, foot) = Slide(chain, a, z, Floors(chain, [(a, z)]));
            parts.Add($"{F(cm)} cm at the switch into {nx.Name} ({F(nx.Ticks / 60.0)} s blend){(nexts.Count > 1 ? $", the worst of the switches into the set's {nexts.Count} other clips" : "")}");
            if (cm > worst) { worst = cm; tick = m.LastTick; bone = foot; }
        }
        var level = worst > BigCm ? CheckLevel.Fail : worst > NoteCm ? CheckLevel.Warn : CheckLevel.Pass;
        return new CheckResult(SlideCheck, level, $"a planted foot moves at most {string.Join(", and ", parts)} (a note above {F(NoteCm)} cm, a big difference above {F(BigCm)} cm)",
            level == CheckLevel.Pass ? null : tick, level == CheckLevel.Pass ? null : bone);
    }

    static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Bends one leg of a pose so its ankle reaches <paramref name="ankle"/> (in model space) with the foot turned to
    /// <paramref name="footRotation"/> (model space): the thigh and the shin turn in the plane of the hip, the place and the knee as it
    /// was; no bone changes length. Returns false when the place is out of the straight leg's reach (the ankle then stops short of it on
    /// the line from the hip) or the leg is folded flat (nothing is changed then).</summary>
    public static bool Reach(Skeleton s, Transform[] locals, Leg g, Vector3 ankle, Quaternion footRotation)
    {
        var model = new Transform[s.Bones.Count];
        s.ComputeModel(locals, model);
        Vector3 a = model[g.Hip].Position, b = model[g.Knee].Position, c = model[g.Ankle].Position;
        float la = (b - a).Length(), lb = (c - b).Length();
        var toT = ankle - a;
        float d = toT.Length();
        if (la < 1e-5f || lb < 1e-5f || d < 1e-5f) return false;
        var dir = toT / d;
        float max = (la + lb) * 0.9999f, min = Math.Abs(la - lb) * 1.0001f + 1e-4f;
        bool reached = d <= max;
        d = Math.Clamp(d, min, max);
        // the knee bends the way it bent: its offset from the leg's own hip-to-ankle line (not the line to the place, which a lifted or
        // moved place can cross, flipping the knee to the other side in a tick), squared to the line to the place
        var kb = b - a;
        var own = c - a;
        var pole = own.Length() < 1e-5f ? kb : kb - Vector3.Normalize(own) * Vector3.Dot(kb, Vector3.Normalize(own));
        pole -= dir * Vector3.Dot(pole, dir);
        if (pole.Length() < 1e-5f)
        {
            // a straight leg: bend toward the knee's own forward
            pole = Vector3.Transform(Vector3.UnitY, model[g.Knee].Rotation);
            pole -= dir * Vector3.Dot(pole, dir);
            if (pole.Length() < 1e-5f) return false;
        }
        pole = Vector3.Normalize(pole);
        float x = (la * la - lb * lb + d * d) / (2 * d);
        float h = MathF.Sqrt(Math.Max(0, la * la - x * x));
        var b2 = a + dir * x + pole * h;
        var c2 = a + dir * d;
        var q1 = FromTo(b - a, b2 - a);
        // then the thigh twisted about itself so the shin's bend plane holds the place: the knee then only bends about its own hinge
        // (two shortest turns alone can leave the shin bent sideways for a place out of the leg's plane)
        var u = Vector3.Normalize(b2 - a);
        var n1 = Vector3.Cross(u, Vector3.Transform(c - b, q1));
        var n2 = Vector3.Cross(u, c2 - b2);
        n1 -= u * Vector3.Dot(n1, u); n2 -= u * Vector3.Dot(n2, u);
        if (n1.Length() > 1e-5f && n2.Length() > 1e-5f)
        {
            n1 = Vector3.Normalize(n1); n2 = Vector3.Normalize(n2);
            float twist = MathF.Atan2(Vector3.Dot(Vector3.Cross(n1, n2), u), Math.Clamp(Vector3.Dot(n1, n2), -1, 1));
            q1 = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(u, twist) * q1);
        }
        var hipRot = Quaternion.Normalize(q1 * model[g.Hip].Rotation);
        var kneeRot1 = Quaternion.Normalize(q1 * model[g.Knee].Rotation);
        var c1 = b2 + Vector3.Transform(c - b, q1);
        var kneeRot = Quaternion.Normalize(FromTo(c1 - b2, c2 - b2) * kneeRot1);
        int parent = s.Bones[g.Hip].ParentIndex;
        var parentRot = parent >= 0 ? model[parent].Rotation : Quaternion.Identity;
        locals[g.Hip] = locals[g.Hip] with { Rotation = Quaternion.Normalize(Quaternion.Inverse(parentRot) * hipRot) };
        locals[g.Knee] = locals[g.Knee] with { Rotation = Quaternion.Normalize(Quaternion.Inverse(hipRot) * kneeRot) };
        locals[g.Ankle] = locals[g.Ankle] with { Rotation = Quaternion.Normalize(Quaternion.Inverse(kneeRot) * footRotation) };
        return reached;
    }

    /// <summary>The shortest turn taking direction <paramref name="u"/> to direction <paramref name="v"/>.</summary>
    static Quaternion FromTo(Vector3 u, Vector3 v)
    {
        u = Vector3.Normalize(u); v = Vector3.Normalize(v);
        float dot = Math.Clamp(Vector3.Dot(u, v), -1, 1);
        if (dot > 0.9999999f) return Quaternion.Identity;
        if (dot < -0.9999999f)
        {
            var axis = Vector3.Cross(u, Vector3.UnitX);
            if (axis.Length() < 1e-4f) axis = Vector3.Cross(u, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(u, v);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + dot));
    }

    /// <summary>What <see cref="KeepPlanted"/> did in one blend: the foot, and whether it stepped (lifted and put down
    /// <see cref="StepCm"/> away) or was left as the blend has it (<paramref name="Short"/>: the leg can't reach its places there).</summary>
    public sealed record Kept(string Foot, int From, int To, double StepCm, bool Short);

    /// <summary>How far past a blend's ends a foot's plant is looked for (half a second): a foot still settling there is planted where it
    /// comes to rest.</summary>
    public const int PlantLookTicks = 30;

    /// <summary>The farthest a kept foot's place may lie from the hip, as a share of the leg's length: nearer straight, the knee would
    /// snap to reach it.</summary>
    public const float MaxReach = 0.999f;

    /// <summary>The longest step the tool makes: a foot planted farther apart than this at a blend's two ends moves with the body (a
    /// walk), and is left as the blend has it.</summary>
    public const double MaxStepCm = 40;

    /// <summary>The most a step turns the foot: a foot turned farther between its plants (a turn on the spot) is left as the blend has
    /// it, since the leg's two turns can't follow it without bending the knee out of its hinge.</summary>
    public const double MaxStepTurnDeg = 20;

    /// <summary>The quickest step the tool makes, in ticks (a fifth of a second, as two steps take in the usual 30-frame blend): a foot
    /// that must move in less is left as the blend has it, and a blend shorter than this keeps no foot (the fit doesn't call
    /// <see cref="KeepPlanted"/> for it).</summary>
    public const int MinStepTicks = 12;

    static double TurnDeg(Quaternion a, Quaternion b) => 2 * Math.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))) * 180 / Math.PI;

    /// <summary>Keeps planted feet in place through a blend of <paramref name="locals"/>: between frame <paramref name="pureA"/> (the
    /// pose blended from, whole) and <paramref name="pureB"/> (the pose blended into, whole). A foot planted at both ends (or within
    /// <see cref="PlantLookTicks"/> past them, where it comes to rest) stays where it stood, the legs bent to it (<see cref="Reach"/>);
    /// where its two places lie more than <see cref="SlideFreeCm"/> apart it steps from one to the other (lifted and put down in the
    /// blend's middle, or later or earlier where the hip only then comes within reach; with both feet stepping, one after the other)
    /// instead of sliding, never while the other foot is in the air. A leg the blend already keeps within a few millimetres of that
    /// path, one that can't reach it however the step is timed, and a step longer than <see cref="MaxStepCm"/>, are left as they are.
    /// The mover is left alone.</summary>
    public static List<Kept> KeepPlanted(Skeleton s, Transform[][] locals, Transform[] mover, bool[] rotationKeyed, int pureA, int pureB,
        IReadOnlyList<(int From, int To)>? allBlends = null)
    {
        var done = new List<Kept>();
        var legs = Legs(s);
        if (legs.Count == 0 || pureB - pureA < 2) return done;
        var m = new Motion(s, locals, mover, rotationKeyed, new bool[s.Bones.Count], true);
        // the floor from the frames no blend made
        var floors = Floors(m, allBlends ?? [(pureA, pureB)]);
        var paths = new List<(int Leg, int A, int B, Vector3 PA, Vector3 PB, Quaternion RA, Quaternion RB, double D)>();
        foreach (var (g, i) in legs.Select((g, i) => (g, i)))
        {
            if (!rotationKeyed[g.Hip] || !rotationKeyed[g.Knee] || !rotationKeyed[g.Ankle]) continue;
            int fa = pureA, fb = pureB;
            while (fa > Math.Max(0, pureA - PlantLookTicks) && !Planted(m, fa, i, floors, -1)) fa--;
            while (fb < Math.Min(m.LastTick, pureB + PlantLookTicks) && !Planted(m, fb, i, floors, 1)) fb++;
            if (!Planted(m, fa, i, floors, -1) || !Planted(m, fb, i, floors, 1)) continue;
            var a = m.World(fa, g.Ankle); var b = m.World(fb, g.Ankle);
            paths.Add((i, fa, fb, a.Position, b.Position, a.Rotation, b.Rotation, Flat(a.Position - b.Position) * 100));
        }
        // the feet kept where they stand first, then the steps, the farther first and the other after it
        var steps = paths.Where(p => p.D > SlideFreeCm).OrderByDescending(p => p.D).Select(p => p.Leg).ToList();
        var targets = new Dictionary<int, (int A, (Vector3 P, Quaternion R)[] Path)>();
        var taken = new List<(int From, int To)>();
        // the legs whose foot stays down where it stands (kept there, or the blend keeps it there); the steps made, and those refused
        var down = new HashSet<int>();
        var stepAt = new Dictionary<int, (int From, int To)>();
        var refused = new HashSet<int>();
        // a leg the tool neither keeps nor steps is down in a step's frames only where the blend has its foot on the ground
        bool Grounded(int j, int from, int to)
        {
            for (int t = Math.Max(0, from); t <= Math.Min(m.LastTick, to); t++)
                if ((Low(m, t, legs[j]) - floors[j]) * 100 > GroundSlackCm) return false;
            return true;
        }
        foreach (var p in paths.OrderBy(p => steps.IndexOf(p.Leg)))
        {
            int k = steps.IndexOf(p.Leg), n = p.B - p.A;
            var leg = legs[p.Leg];
            (double S0, double S1) phase = k < 0 ? (0, 1) : steps.Count == 1 ? (0.2, 0.8) : k == 0 ? (0.1, 0.5) : (0.5, 0.9);
            // a step lifts the foot clear of the ground (StepLiftCm and more for a longer step) before it moves across, and puts it down after
            float lift = k < 0 ? 0 : (float)Math.Min(0.06, (StepLiftCm + p.D * 0.15) / 100);
            (Vector3 P, Quaternion R)[] Path(int from, int to)
            {
                var path = new (Vector3, Quaternion)[n + 1];
                for (int t = p.A; t <= p.B; t++)
                {
                    float v = (float)Smooth(Math.Clamp((t - from) / (double)(to - from), 0, 1));
                    float across = k < 0 ? v : (float)Smooth(Math.Clamp((v - 0.25) / 0.5, 0, 1));
                    var pos = Vector3.Lerp(p.PA, p.PB, across) + Vector3.UnitZ * lift * MathF.Sin(MathF.PI * v);
                    path[t - p.A] = (pos, Quaternion.Normalize(Quaternion.Slerp(p.RA, p.RB, across)));
                }
                return path;
            }
            // the IK only turns the leg, so the hip stays where the blend has it: a place farther from it than the leg reaches (99.9% of
            // its length, or as far as it stands at the path's ends) would snap the knee straight, so such a path isn't taken
            var hip = new Vector3[n + 1];
            for (int t = p.A; t <= p.B; t++) hip[t - p.A] = m.World(t, leg.Hip).Position;
            float len = (m.Model(p.A, leg.Knee).Position - m.Model(p.A, leg.Hip).Position).Length() + (m.Model(p.A, leg.Ankle).Position - m.Model(p.A, leg.Knee).Position).Length();
            float limit = Math.Max(len * MaxReach, Math.Max((hip[0] - p.PA).Length(), (hip[n] - p.PB).Length()));
            bool Fits((Vector3 P, Quaternion R)[] path) => Enumerable.Range(1, Math.Max(0, n - 1)).All(i => (path[i].P - hip[i]).Length() <= limit);
            (Vector3 P, Quaternion R)[]? chosen = null;
            if (k < 0) { var path = Path(p.A, p.B); if (Fits(path)) { chosen = path; down.Add(p.Leg); } }
            else if (p.D <= MaxStepCm && TurnDeg(p.RA, p.RB) <= MaxStepTurnDeg)
            {
                // the step at its usual time, else the nearest time its places are in reach, never while the other foot is in the air
                // (another step's, or the blend's own lift of a foot the tool doesn't keep or whose step it doesn't make)
                int dur = Math.Max(2, (int)Math.Round((phase.S1 - phase.S0) * n)), at = p.A + (int)Math.Round(phase.S0 * n);
                // a step quicker than MinStepTicks (a blend shortened) would snap the knee: none is made
                if (dur < MinStepTicks) dur = n + 1;
                foreach (int go in Enumerable.Range(p.A, Math.Max(0, n - dur + 1)).OrderBy(x => Math.Abs(x - at)))
                {
                    if (taken.Any(w => go < w.To && go + dur > w.From)) continue;
                    if (Enumerable.Range(0, legs.Count).Any(j => j != p.Leg && !down.Contains(j) && !(steps.Contains(j) && !refused.Contains(j)) && !Grounded(j, go, go + dur))) continue;
                    var path = Path(go, go + dur);
                    if (!Fits(path)) continue;
                    chosen = path; taken.Add((go, go + dur)); stepAt[p.Leg] = (go, go + dur);
                    break;
                }
            }
            if (chosen is null)
            {
                if (k >= 0) refused.Add(p.Leg);
                done.Add(new Kept(leg.Side + "Foot", p.A, p.B, p.D > SlideFreeCm ? p.D : 0, true));
                continue;
            }
            // a leg the blend already keeps on its path is left alone
            double off = 0;
            for (int t = p.A; t <= p.B; t++) off = Math.Max(off, (m.World(t, leg.Ankle).Position - chosen[t - p.A].P).Length() * 100);
            if (off <= 0.3) continue;
            targets[p.Leg] = (p.A, chosen);
        }
        // a step refused after another was timed moves as the blend has it: a step made while that foot is up is dropped too (and so on)
        for (bool again = true; again;)
        {
            again = false;
            foreach (var (leg, w) in stepAt.ToList())
            {
                if (!refused.Any(j => !Grounded(j, w.From, w.To))) continue;
                stepAt.Remove(leg); targets.Remove(leg); refused.Add(leg); again = true;
                var p = paths.First(x => x.Leg == leg);
                done.Add(new Kept(legs[leg].Side + "Foot", p.A, p.B, p.D, true));
            }
        }
        if (targets.Count == 0) return done;
        var shortOf = new HashSet<int>();
        int first = targets.Values.Min(x => x.A), end = targets.Values.Max(x => x.A + x.Path.Length - 1);
        for (int t = first + 1; t < end; t++)
        {
            var inv = Inverse(mover[t]);
            var l = (Transform[])locals[t].Clone();
            foreach (var (leg, (a, path)) in targets)
            {
                if (t <= a || t >= a + path.Length - 1) continue;
                var (pw, rw) = path[t - a];
                if (!Reach(s, l, legs[leg], inv.Apply(pw), Quaternion.Normalize(inv.Rotation * rw))) shortOf.Add(leg);
            }
            locals[t] = l;
        }
        foreach (var leg in targets.Keys)
        {
            var p = paths.First(x => x.Leg == leg);
            done.Add(new Kept(legs[leg].Side + "Foot", p.A, p.B, p.D > SlideFreeCm ? p.D : 0, shortOf.Contains(leg)));
        }
        return done;
    }

    static double Smooth(double x) => x * x * (3 - 2 * x);

    static Transform Inverse(Transform t)
    {
        var iq = Quaternion.Inverse(t.Rotation);
        return new Transform(Vector3.Transform(-t.Position, iq), iq);
    }
}
