using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>The decoders against every key of the game's clips, the continuity control, and a known number.</summary>
[Collection("install")]
public sealed class DecodeTests(InstallFixture f, ITestOutputHelper output)
{
    static double AngleDeg(Quaternion a, Quaternion b)
    {
        double d = Math.Abs((double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W);
        return 2 * Math.Acos(Math.Min(1.0, d)) * 180 / Math.PI;
    }

    static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

    /// <summary>Every key of every rotation and position curve of every clip: a rotation is finite (the decoder normalises every key, so
    /// its unit length only shows it isn't NaN), its stored smallest-three triple no longer than 1 (a four-float key near unit length, a
    /// one-value key within -1..1): that is the real check of the stored values. A position is finite.</summary>
    [GameDataFact]
    public void EveryKeyIsValid()
    {
        var g = f.Install;
        long rot = 0, pos = 0, plants = 0, tripleMax = 0;
        float worstTriple = 0, plantFar = 0;
        var bad = new List<string>();
        foreach (var e in g.Clips)
        {
            var c = e.Record!;
            foreach (var cv in c.Curves)
            {
                var u = SharedCurveTable.Resolve(cv, g.SharedTable)!;
                for (int k = 0; k < u.Keys; k++)
                {
                    if (CurveTags.IsRotation(u.Tag))
                    {
                        Assert.True(CurveDecoder.TryKeyRot(u, k, out var q, out float stored), $"{e.Name}: tag 0x{u.Tag:X2} key {k} not decoded");
                        rot++;
                        bool unit = float.IsFinite(q.X + q.Y + q.Z + q.W) && MathF.Abs(q.Length() - 1) < 1e-4f;
                        bool storedOk = u.ValueBytes == 16 ? MathF.Abs(stored - 1) < 0.01f : stored <= 1.0005f;
                        if (u.ValueBytes != 16 && u.Tag is not (>= 0x12 and <= 0x17)) { worstTriple = MathF.Max(worstTriple, stored); if (stored > 0.75f) tripleMax++; }
                        if ((!unit || !storedOk) && bad.Count < 20) bad.Add($"{e.Name} tag 0x{u.Tag:X2} key {k}: |q| {q.Length()}, stored {stored}");
                    }
                    else if (CurveTags.IsPosition(u.Tag))
                    {
                        var p = CurveDecoder.KeyPos(u, k);
                        Assert.NotNull(p);
                        pos++;
                        if (!float.IsFinite(p.Value.X + p.Value.Y + p.Value.Z) && bad.Count < 20) bad.Add($"{e.Name} tag 0x{u.Tag:X2} key {k}: {p}");
                    }
                    else if (CurveDecoder.Plant(u, k) is { } pl)
                    {
                        plants++;
                        plantFar = MathF.Max(plantFar, new Vector2(pl.Point.X, pl.Point.Y).Length());
                        if (pl.LengthTicks < 0 && bad.Count < 20) bad.Add($"{e.Name}: plant key {k} {pl}");
                    }
                }
            }
        }
        output.WriteLine($"rotation keys {rot}, position keys {pos}, plant keys {plants}; largest smallest-three triple (squared) {worstTriple}; triples over 0.75 (not the largest dropped) {tripleMax}; farthest plant point {plantFar:F2} m from the origin");
        Assert.Empty(bad);
        Assert.True(rot > 1_000_000 && pos > 100_000);
    }

    /// <summary>Consecutive keys of decoded rotation curves move little per tick; the same curves read one byte out of step move a lot. A
    /// 1-in-20 sample of the rotation curves of 3 keys or more, by value size.</summary>
    [GameDataFact]
    public void ContinuityControl()
    {
        var g = f.Install;
        var good = new Dictionary<int, List<double>>();
        var bad = new Dictionary<int, List<double>>();
        var rnd = new Random(1);
        foreach (var e in g.Clips)
            foreach (var cv in e.Record!.Curves)
            {
                var u = SharedCurveTable.Resolve(cv, g.SharedTable)!;
                if (!CurveTags.IsRotation(u.Tag) || u.Keys < 3 || u.ValueBytes == 16 || rnd.Next(20) != 0) continue;
                var gl = good.TryGetValue(u.ValueBytes, out var x) ? x : good[u.ValueBytes] = [];
                var bl = bad.TryGetValue(u.ValueBytes, out var y) ? y : bad[u.ValueBytes] = [];
                var sh = u with { ValuesAt = u.ValuesAt + 1 };
                for (int k = 0; k + 1 < u.Keys; k++)
                {
                    int dt = Math.Max(1, CurveDecoder.KeyTick(u, k + 1) - CurveDecoder.KeyTick(u, k));
                    gl.Add(AngleDeg(CurveDecoder.KeyRot(u, k)!.Value, CurveDecoder.KeyRot(u, k + 1)!.Value) / dt);
                    if (CurveDecoder.KeyRot(sh, k) is { } a && CurveDecoder.KeyRot(sh, k + 1) is { } b) bl.Add(AngleDeg(a, b) / dt);
                }
            }
        foreach (var e in good.Keys.Order())
        {
            double gm = Median(good[e]), bm = Median(bad[e]);
            output.WriteLine($"{e} B a value: decoded n={good[e].Count} median {gm:F3} deg/tick; out of step n={bad[e].Count} median {bm:F3}");
            Assert.True(gm < 2, $"{e}-byte form: decoded keys move {gm:F2} deg a tick at the median");
            Assert.True(bm > 5 && bm > 5 * gm, $"{e}-byte form: read out of step the keys move only {bm:F2} deg a tick (decoded {gm:F2})");
        }
        Assert.Equal([2, 4, 6, 8, 12], good.Keys.Order());
    }

    static readonly int[][] Orders = Permutations([0, 1, 2, 3]).ToArray();

    static IEnumerable<int[]> Permutations(int[] a)
    {
        if (a.Length == 1) { yield return a; yield break; }
        for (int i = 0; i < a.Length; i++)
            foreach (var rest in Permutations([.. a[..i], .. a[(i + 1)..]]))
                yield return [a[i], .. rest];
    }

    static Quaternion Reorder(Quaternion q, int[] p)
    {
        float[] c = [q.X, q.Y, q.Z, q.W];
        return new Quaternion(c[p[0]], c[p[1]], c[p[2]], c[p[3]]);
    }

    /// <summary>A known-value check of each rotation form's component order. Many clips hold a bone at the skeleton's stored local rotation,
    /// so key 0 of a bone's rotation track often equals that rest rotation to within the form's precision. For each form and each other
    /// order of the four components, among the keys where that order gives another rotation (by more than 1 deg), count the keys that land
    /// on the rest rotation (within 0.5 deg) as decoded and as reordered: as decoded must win by far, for every order.</summary>
    [GameDataFact]
    public void ComponentOrderMatchesTheRestPose()
    {
        var g = f.Install;
        var s = g.Skeleton;
        // per form: keys seen, keys at rest as decoded, and per other order: keys it changes, of those at rest as decoded, at rest reordered
        var seen = new Dictionary<int, long>();
        var atRest = new Dictionary<int, long>();
        var changed = new Dictionary<int, (long N, long Decoded, long Reordered)[]>();
        foreach (var e in g.Clips)
        {
            var c = e.Record!;
            for (int i = 0; i < c.Curves.Count; i++)
            {
                int bone = s.IndexOf(c.Tracks[i]);
                if (bone < 0) continue;
                var u = SharedCurveTable.Resolve(c.Curves[i], g.SharedTable)!;
                if (!CurveTags.IsRotation(u.Tag)) continue;
                var q = CurveDecoder.KeyRot(u)!.Value;
                var rest = Quaternion.Normalize(s.Bones[bone].LocalRest.Rotation);
                bool home = AngleDeg(q, rest) < 0.5;
                seen[u.Form] = seen.GetValueOrDefault(u.Form) + 1;
                if (home) atRest[u.Form] = atRest.GetValueOrDefault(u.Form) + 1;
                var row = changed.TryGetValue(u.Form, out var r) ? r : changed[u.Form] = new (long, long, long)[Orders.Length];
                for (int p = 1; p < Orders.Length; p++)
                {
                    var o = Reorder(q, Orders[p]);
                    if (AngleDeg(o, q) <= 1) continue;
                    row[p] = (row[p].N + 1, row[p].Decoded + (home ? 1 : 0), row[p].Reordered + (AngleDeg(o, rest) < 0.5 ? 1 : 0));
                }
            }
        }
        // on the game's files (keys / at rest as decoded; the closest other order: keys it changes, at rest as decoded / reordered):
        // form 3 (4 B) 209,027 / 5,747, 1032: 204,333, 1,274 / 17; form 4 (6 B) 220,107 / 12,255, 1203: 209,503, 2,405 / 4;
        // form 5 (8 B) 348 / 48, 1023: 257, 5 / 0; form 7 (2 B about z) 124,828 / 8,010, 0132: 120,841, 4,061 / 0;
        // form 8 (12 B) 4,802 / 3,801, 1023: 1,038, 177 / 0
        Assert.Equal([3, 4, 5, 7, 8], seen.Keys.Order());
        foreach (var form in seen.Keys.Order())
        {
            var row = changed[form];
            int worst = Enumerable.Range(1, Orders.Length - 1).Where(p => row[p].N > 0).MaxBy(p => (row[p].Reordered + 1.0) / (row[p].Decoded + 1.0));
            var (n, dec, re) = row[worst];
            output.WriteLine($"form {form}: {seen[form]} keys, at rest as decoded {atRest.GetValueOrDefault(form)}; closest other order {string.Join("", Orders[worst])}: "
                + $"of {n} keys it changes, at rest {dec} as decoded, {re} reordered");
            for (int p = 1; p < Orders.Length; p++)
                Assert.True(row[p].N == 0 || (row[p].Decoded >= 5 && row[p].Decoded >= 4 * (row[p].Reordered + 1)),
                    $"form {form}, order {string.Join("", Orders[p])}: at rest {row[p].Decoded} as decoded, {row[p].Reordered} reordered");
        }
    }

    /// <summary>mca_emote_Hello sampled at every tick: 63 rotation and 11 position tracks; its largest step between ticks is 13.09 deg, on
    /// LeftHand (B675F36C).</summary>
    [GameDataFact]
    public void HelloKnownNumber()
    {
        var g = f.Install;
        var e = g.FindClip("mca_emote_Hello")!;
        var c = e.Record!;
        int rotTracks = 0, posTracks = 0;
        for (int i = 0; i < c.Curves.Count && i < c.Tracks.Count; i++)
        {
            var u = SharedCurveTable.Resolve(c.Curves[i], g.SharedTable)!;
            if (CurveTags.IsRotation(u.Tag)) rotTracks++; else if (CurveTags.IsPosition(u.Tag)) posTracks++;
        }
        Assert.Equal(63, rotTracks);
        Assert.Equal(11, posTracks);
        var s = new ClipSampler(c, g.SharedTable);
        Assert.Equal(63, s.Channels.Count(x => x.Rotation));
        Assert.Equal(11, s.Channels.Count(x => !x.Rotation));
        double worst = 0; uint worstTrack = 0;
        Quaternion[]? prev = null;
        for (int t = 0; t <= c.LastTick; t++)
        {
            s.At(t / 60f);
            if (prev is not null)
                for (int i = 0; i < s.Channels.Count; i++)
                    if (s.Channels[i].Rotation && AngleDeg(prev[i], s.Rotations[i]) is var a && a > worst) { worst = a; worstTrack = s.Channels[i].Track; }
            prev = (Quaternion[])s.Rotations.Clone();
        }
        output.WriteLine($"{e.Name} 0x{e.Id:X16}: {c.Duration:F3} s, {c.LastTick} ticks; largest step {worst:F4} deg on {worstTrack:X8} ({g.Names.Display(worstTrack)})");
        Assert.Equal(0xB675F36Cu, worstTrack);
        Assert.Equal("LeftHand", g.Names.Display(worstTrack));
        Assert.Equal(13.09, Math.Round(worst, 2));
    }

    /// <summary>The pose sampler on the merged skeleton: every bone's model and world transform is finite, the add-on hangs from the body,
    /// and the mover is kept apart from the skeleton.</summary>
    [GameDataFact]
    public void PoseSampling()
    {
        var g = f.Install;
        Assert.Equal(65, g.BodySkeleton.Bones.Count);
        Assert.Equal(75, g.Skeleton.Bones.Count);
        Assert.False(g.Skeleton.Contains(0));
        var gun = g.Skeleton.Bones[g.Skeleton.IndexOf(0x826846F3)];
        Assert.True(gun.FromAddOn);
        Assert.Equal("Spine2", g.Skeleton.Bones[gun.ParentIndex].Name);
        Assert.Equal(10, g.Skeleton.Bones.Count(b => b.FromAddOn));
        Assert.All(g.Skeleton.Bones.Where(b => b.FromAddOn), b => Assert.False(b.IsRoot));

        var hello = g.CreateSampler(g.FindClip("mca_emote_Hello")!);
        Assert.True(hello.HasMover);
        var p = hello.Sample(1.0f);
        Assert.All(p.Model, t => Assert.True(float.IsFinite(t.Position.X + t.Position.Y + t.Position.Z + t.Rotation.W)));
        // the emote stands in place: its mover doesn't move, so world and model agree
        Assert.True(p.Mover.Position.Length() < 1e-3f);
        int hips = g.Skeleton.IndexOf(0xDED10611);
        Assert.InRange(p.Model[hips].Position.Z, 0.8f, 1.1f);   // hips about 96 cm over the floor

        // a holstered walk cycle travels on its mover, and the body stays over the mover
        var walk = g.CreateSampler(g.FindClip("mcm_nwp_fm_std_V1_cycle")!);
        var a = walk.Sample(0); var b = walk.Sample(walk.Clip.Duration);
        float travel = Vector3.Distance(a.Mover.Position, b.Mover.Position);
        output.WriteLine($"walk cycle: {walk.Clip.Duration:F3} s, mover travels {travel * 100:F1} cm; hips in model space {b.Model[hips].Position * 100}");
        Assert.True(travel > 0.3f);
        Assert.True(new Vector2(b.Model[hips].Position.X, b.Model[hips].Position.Y).Length() < 0.3f);
        Assert.True(Vector3.Distance(b.World(hips).Position, b.Mover.Apply(b.Model[hips].Position)) < 1e-5f);
    }

    /// <summary>The viewer's handedness rests on this: in the hello emote at 1 s the waving hand is the left one, on the character's
    /// left (-x) and raised over head height, the right hand on its right and lower.</summary>
    [GameDataFact]
    public void TheHelloWaveIsTheLeftHandOnTheLeft()
    {
        var g = f.Install;
        var p = g.CreateSampler(g.FindClip("mca_emote_Hello")!).Sample(1.0f);
        var left = p.World(g.Skeleton.IndexOf(Crc32.Of("LeftHand"))).Position;
        var right = p.World(g.Skeleton.IndexOf(Crc32.Of("RightHand"))).Position;
        output.WriteLine($"hello at 1 s: LeftHand {left}, RightHand {right}");
        Assert.True(left.X < 0, $"LeftHand x {left.X}");
        Assert.True(left.Z > 1.6f, $"LeftHand z {left.Z}");
        Assert.True(right.X > 0, $"RightHand x {right.X}");
        Assert.True(right.Z < left.Z);
    }

    static Transform Inverse(Transform t)
    {
        var r = Quaternion.Conjugate(Quaternion.Normalize(t.Rotation));
        return new Transform(-Vector3.Transform(t.Position, r), r);
    }

    static Transform[] Composed(Skeleton s)
    {
        var model = new Transform[s.Bones.Count];
        s.ComputeModel(s.RestLocals(), model);
        return model;
    }

    /// <summary>The stored rest pose: the body's and the add-on's local transforms compose to their stored model transforms. The add-on's
    /// model space has its own root (its copy of Spine at the origin), so in the merged skeleton each add-on bone is compared in the frame
    /// of the body bone it hangs from (Spine2).</summary>
    [GameDataFact]
    public void RestPoseComposes()
    {
        var g = f.Install;
        foreach (var s in new[] { g.BodySkeleton, g.AddOnSkeleton })
        {
            var model = Composed(s);
            double worstCm = 0, worstDeg = 0;
            for (int i = 0; i < s.Bones.Count; i++)
            {
                worstCm = Math.Max(worstCm, Vector3.Distance(model[i].Position, s.Bones[i].ModelRest.Position) * 100);
                worstDeg = Math.Max(worstDeg, AngleDeg(model[i].Rotation, Quaternion.Normalize(s.Bones[i].ModelRest.Rotation)));
            }
            output.WriteLine($"{s.Bones.Count} bones: worst {worstCm:F4} cm, {worstDeg:F4} deg");
            Assert.True(worstCm < 0.01 && worstDeg < 0.1, $"{worstCm} cm, {worstDeg} deg");
        }
        var m = g.Skeleton;
        var mm = Composed(m);
        uint spine2 = 0x1630ABF4;
        var a = g.AddOnSkeleton;
        var attachInAddOn = Inverse(a.Bones[a.IndexOf(spine2)].ModelRest);
        var attachInBody = Inverse(mm[m.IndexOf(spine2)]);
        double cmA = 0, degA = 0;
        foreach (var b in m.Bones.Where(b => b.FromAddOn))
        {
            var want = attachInAddOn * a.Bones[a.IndexOf(b.Hash)].ModelRest;
            var got = attachInBody * mm[b.Index];
            cmA = Math.Max(cmA, Vector3.Distance(want.Position, got.Position) * 100);
            degA = Math.Max(degA, AngleDeg(want.Rotation, got.Rotation));
        }
        output.WriteLine($"add-on bones against Spine2, merged: worst {cmA:F4} cm, {degA:F4} deg");
        Assert.True(cmA < 0.01 && degA < 0.1, $"{cmA} cm, {degA} deg");
    }
}
