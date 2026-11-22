using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Export;

/// <summary>The outcome of reading an export back against the decoder: the largest distance and angle over every bone at every tick, where
/// they fell, and anything missing or extra.</summary>
public sealed record RoundTripResult(int Ticks, int Bones, double WorstCm, double WorstDeg, string WorstCmAt, string WorstDegAt, IReadOnlyList<string> Problems)
{
    /// <summary>The limits of the export's control: 0.01 cm and 0.01 deg.</summary>
    public const double LimitCm = 0.01, LimitDeg = 0.01;

    public bool Passed => Problems.Count == 0 && WorstCm <= LimitCm && WorstDeg <= LimitDeg;

    public override string ToString() =>
        FormattableString.Invariant($"{Bones} bones x {Ticks} ticks: worst {WorstCm:F5} cm ({WorstCmAt}), {WorstDeg:F5} deg ({WorstDegAt})")
        + (Problems.Count > 0 ? $"; {Problems.Count} problem(s): {string.Join("; ", Problems.Take(5))}" : "")
        + (Passed ? ": PASS" : ": FAIL");
}

/// <summary>An expected value moved on purpose, the control's own control: one bone's expected local rotation turned about its own x by
/// <paramref name="Degrees"/>, from <paramref name="FromTick"/> on.</summary>
public sealed record Spoil(uint Bone, float Degrees, int FromTick = 0);

/// <summary>The re-import control: an exported file read back (<see cref="GltfAnimation"/>) and posed by its own node tree, rest and
/// channels, against the decoder's pose of the same clip (<see cref="PoseSampler"/>) at every tick: every bone's place and turn in the
/// clip's world, the mover applied. Without a clip, the file's rest pose against the skeleton's.</summary>
public static class RoundTrip
{
    public static RoundTripResult Compare(Skeleton s, GltfAnimation f, ClipRecord? clip = null, SharedCurveTable? table = null, Spoil? spoil = null)
    {
        var problems = new List<string>();
        int n = s.Bones.Count;
        // the file's bones by skeleton index, and its tree against the skeleton's
        var node = new int[n];
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < f.Names.Count; i++) byName[f.Names[i]] = i;
        var jointSet = f.Joints.ToHashSet();
        for (int i = 0; i < n; i++)
        {
            var b = s.Bones[i];
            node[i] = byName.GetValueOrDefault(b.Name, -1);
            if (node[i] < 0) { problems.Add($"bone {b.Name} isn't in the file"); continue; }
            if (!jointSet.Contains(node[i])) problems.Add($"bone {b.Name} isn't a joint of the skin");
            int fp = f.Parents[node[i]];
            string want = b.ParentIndex >= 0 ? s.Bones[b.ParentIndex].Name : f.ArmatureName;
            string got = fp >= 0 ? f.Names[fp] : "";
            if (got != want) problems.Add($"bone {b.Name}'s parent is {(got == "" ? "none" : got)}, not {(want == "" ? "none" : want)}");
        }
        if (f.Joints.Count != n) problems.Add($"{f.Joints.Count} joints in the file, {n} bones in the skeleton");
        if (f.MaxScaleOff > 1e-5) problems.Add($"a scale {f.MaxScaleOff:F6} off 1");
        if (problems.Count > 0) return new RoundTripResult(0, n, double.NaN, double.NaN, "", "", problems);

        PoseSampler? ps = null;
        int frames = 1;
        if (clip is not null)
        {
            ps = new PoseSampler(s, clip, table);
            frames = clip.LastTick + 1;
            if (!f.Ticks.SequenceEqual(Enumerable.Range(0, frames)))
                problems.Add($"the file's {f.Ticks.Count} key times aren't the ticks 0 to {clip.LastTick}");
            // every rotation and position track of a skeleton bone or the mover must have its channel
            var sampled = SampledClip.Of(s, clip, table);
            for (int i = 0; i < n; i++)
            {
                if (sampled.Rotations[i] is not null && !f.Rotations.ContainsKey(s.Bones[i].Name)) problems.Add($"no rotation channel for {s.Bones[i].Name}");
                if (sampled.Positions[i] is not null && !f.Positions.ContainsKey(s.Bones[i].Name)) problems.Add($"no position channel for {s.Bones[i].Name}");
            }
            if (sampled.MoverRotation is not null && f.MoverRotation is null) problems.Add("no rotation channel for the mover");
            if (sampled.MoverPosition is not null && f.MoverPosition is null) problems.Add("no position channel for the mover");
            if (problems.Count > 0) return new RoundTripResult(0, n, double.NaN, double.NaN, "", "", problems);
        }
        else if (f.Animations > 0) problems.Add("an animation in a skeleton file");

        var pose = ps?.NewPose();
        var expLocal = s.RestLocals();
        var expModel = new Transform[n];
        var gotLocal = new Transform[n];
        var gotModel = new Transform[n];
        var restArr = new (Vector3 P, Quaternion Q)[n];
        var rotCh = new Quaternion[]?[n];
        var posCh = new Vector3[]?[n];
        for (int i = 0; i < n; i++)
        {
            string nm = s.Bones[i].Name;
            restArr[i] = f.Rest[nm];
            rotCh[i] = f.Rotations.GetValueOrDefault(nm);
            posCh[i] = f.Positions.GetValueOrDefault(nm);
        }
        int spoilAt = spoil is null ? -1 : s.IndexOf(spoil.Bone);
        if (spoil is not null && spoilAt < 0) problems.Add($"the spoiled bone {spoil.Bone:X8} isn't in the skeleton");
        double worstCm = 0, worstDeg = 0;
        string cmAt = "", degAt = "";
        for (int t = 0; t < frames; t++)
        {
            // the decoder's pose in the clip's world
            Transform mover = Transform.Identity;
            if (ps is not null)
            {
                ps.Sample(t / (float)ClipRecord.TicksPerSecond, pose!);
                for (int i = 0; i < n; i++) expModel[i] = pose!.Model[i];
                mover = pose!.Mover;
            }
            else s.ComputeModel(expLocal, expModel);
            if (spoilAt >= 0 && t >= spoil!.FromTick)
            {
                // turning a bone's local rotation by R about its own x moves it and everything under it: W' = W_b R W_b^-1 W
                var wb = expModel[spoilAt];
                var turn = wb * new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, spoil.Degrees * MathF.PI / 180)) * GltfAxes.Inverse(wb);
                for (int i = 0; i < n; i++)
                    if (i == spoilAt || Below(s, i, spoilAt)) expModel[i] = turn * expModel[i];
            }
            // the file's pose: its rest, its channels at this tick, its own tree, its mover
            for (int i = 0; i < n; i++)
            {
                var (rp, rq) = restArr[i];
                gotLocal[i] = new Transform(posCh[i] is { } pc ? pc[t] : rp, Quaternion.Normalize(rotCh[i] is { } rc ? rc[t] : rq));
                int p = s.Bones[i].ParentIndex;
                gotModel[i] = p >= 0 ? gotModel[p] * gotLocal[i] : gotLocal[i];
            }
            var gotMover = new Transform(f.MoverPosition is { } mp ? mp[t] : Vector3.Zero, f.MoverRotation is { } mr ? Quaternion.Normalize(mr[t]) : Quaternion.Identity);
            for (int i = 0; i < n; i++)
            {
                var e = mover * expModel[i];
                var g = gotMover * gotModel[i];
                double cm = Vector3.Distance(e.Position, g.Position) * 100.0;
                double deg = GltfAxes.AngleDeg(e.Rotation, g.Rotation);
                if (!(cm <= worstCm)) { worstCm = double.IsNaN(cm) ? double.PositiveInfinity : cm; cmAt = $"{s.Bones[i].Name} at tick {t}"; }
                if (!(deg <= worstDeg)) { worstDeg = double.IsNaN(deg) ? double.PositiveInfinity : deg; degAt = $"{s.Bones[i].Name} at tick {t}"; }
            }
        }
        return new RoundTripResult(frames, n, worstCm, worstDeg, cmAt, degAt, problems);
    }

    static bool Below(Skeleton s, int i, int ancestor)
    {
        for (int p = s.Bones[i].ParentIndex; p >= 0; p = s.Bones[p].ParentIndex)
            if (p == ancestor) return true;
        return false;
    }
}
