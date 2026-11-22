using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Retiming;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>The edits on the game's own clips: the census's counts, the edits that must give a clip back (swap with itself, speed 1, trim of
/// nothing, scale 1, mirror twice), the raw writer's control on every non-bone curve, a known event re-timed, damaged records refused cleanly.</summary>
[Collection("install")]
public sealed class EditGameTests(InstallFixture f, ITestOutputHelper output)
{
    static ClipGraph? _graph;
    static readonly object Gate = new();

    ClipGraph Graph() { lock (Gate) return _graph ??= ClipGraph.Read(f.Install); }

    EditContext Ctx(ClipEntry e) => new(f.Install.Skeleton, f.Install.SharedTable, Graph().Verdict(e.Id));

    /// <summary>The Edit screen's choices on every clip, as its list takes them (each slider and the trim stopping at the first setting
    /// that works): the clips that allow no edit, by why; then, tried at every setting, the speeds that work, at the slider's steps and
    /// over the speed change's whole range, on the 1,563 clips not refused at every speed for their timing data (or their length), and the
    /// cuts of the trim's grid that work.</summary>
    [GameDataFact]
    public void TheEditScreensChoicesAndTheSpeedsAndCutsThatWork()
    {
        var g = f.Install;
        var all = Within.Seconds(600, () => Core.Library.InstallEditChoices.Of(g, Graph(), Core.Library.SwapSources.Build(g.Clips, g.SharedTable), new SkeletonMirror(g.Skeleton), CancellationToken.None));
        foreach (var (reasons, n) in all.NoEditByReason()) output.WriteLine($"{n}: {reasons}");
        Assert.Equal(9938, all.Count);
        Assert.Equal(0, all.Faults);
        // one clip fewer than with one trial cut and one trial scale: a clip whose trial cut is refused but another cut works
        Assert.Equal(1070, all.NoEdit);
        var groups = all.NoEditByReason().ToDictionary(x => x.Reasons, x => x.Clips);
        Assert.Equal(557, groups["Mirror MotionField, Root-motion scale MotionField, Speed LengthHeld, Trim LengthHeld, Swap NoSwapSource"]);
        Assert.Equal(465, groups["Mirror HandTracks, Root-motion scale NoRootMotion, Speed TimesUnread, Trim TimesUnread, Swap NoSwapSource"]);
        // trim: 5 fewer than when a clip of two ticks' length was tried on the whole clip (a trial that cut nothing); its cuts of one
        // tick are tried now
        Assert.Equal((2387, 4645, 1515, 1492, 6800), (all.All.Count(c => c[Core.Library.EditKind.Mirror].Available), all.All.Count(c => c[Core.Library.EditKind.Scale].Available),
            all.All.Count(c => c[Core.Library.EditKind.Speed].Available), all.All.Count(c => c[Core.Library.EditKind.Trim].Available), all.All.Count(c => c[Core.Library.EditKind.Swap].Available)));
        // a clip that allows no edit says so in its marks; one that allows some names them
        Assert.All(all.All.Where(c => !c.Any).Take(20), c => Assert.Equal("· · · · ·", c.Marks));
        // the marks the Edit screen's list keeps, worked out on threads of their own below the normal priority, are these choices', and
        // their progress goes up to the whole
        var progress = new EditMarksTests.Reports();
        var marks = Within.Seconds(600, () => Core.Library.EditMarks.BuildAsync(g, Graph(), Core.Library.SwapSources.Build(g.Clips, g.SharedTable), new SkeletonMirror(g.Skeleton),
            progress, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(all.Count, marks.Count);
        Assert.All(all.Clips, k => Assert.Equal(Core.Library.EditMarks.MaskOf(k.Value), marks.Masks[k.Key]));
        Assert.Equal(1070, marks.NoEdit);
        // whole: every input read, no clip the tool failed on
        Assert.Null(marks.Incomplete);
        Assert.Empty(marks.Unknown);
        Assert.Empty(marks.Faults);
        Assert.Equal(1.0, progress.Values.Max());
        Assert.True(progress.Values.Count >= 50, $"{progress.Values.Count} reports");
        // the work ran on the workers' own threads, below the normal priority, never the thread pool's
        Assert.All(progress.Threads, t => Assert.True(t.Name?.StartsWith("Edit marks ", StringComparison.Ordinal) == true && t.Priority == ThreadPriority.BelowNormal && !t.Pool,
            $"a report from {t.Name ?? "a thread with no name"} ({t.Priority}, pool {t.Pool})"));
        // the speeds that work, every step tried: the slider's own (0.5 to 2), then the whole range (0.25 to 4)
        var clips = g.Clips.Where(c => all.For(c)!.Speeds is { Grid.Count: > 0 } s && !Core.Library.EditJobs.AtEverySetting(s.Refused.FirstOrDefault().Block?.Kind)).ToList();
        Assert.Equal(1563, clips.Count);
        foreach (var (grid, counts) in new[] { (Core.Library.EditJobs.SpeedGrid, (1386, 129, 48)),
                     (Core.Library.EditJobs.Grid(RetimeEdit.MinSpeed, RetimeEdit.MaxSpeed, Core.Library.EditJobs.SpeedStep), (1301, 218, 44)) })
        {
            var s = new System.Collections.Concurrent.ConcurrentBag<Core.Library.FactorChoices>();
            Within.Seconds(600, () => Parallel.ForEach(clips, c => s.Add(Core.Library.EditJobs.Speeds(c, Ctx(c), CancellationToken.None, grid, parallelism: 1))));
            Assert.Equal(counts, (s.Count(x => x.All), s.Count(x => !x.All && x.Works.Count > 0), s.Count(x => x.Works.Count == 0)));
        }
        // the cuts of the trim's grid that work, every cut tried, on the clips not refused at every cut: every cut, some (of them those
        // that work only keeping the clip's start), none
        var trimmed = g.Clips.Where(c => all.For(c)!.Trims is { Grid.Count: > 0 } t && !Core.Library.EditJobs.AtEverySetting(t.Main?.Kind)).ToList();
        var trims = new System.Collections.Concurrent.ConcurrentBag<Core.Library.TrimChoices>();
        Within.Seconds(600, () => Parallel.ForEach(trimmed, c => trims.Add(Core.Library.EditJobs.Trims(c, Ctx(c), CancellationToken.None, parallelism: 1))));
        output.WriteLine($"cuts: every {trims.Count(t => t.All)}, some {trims.Count(t => !t.All && t.Works.Count > 0)}, none {trims.Count(t => t.Works.Count == 0)} "
            + string.Join(", ", trims.Where(t => t.Works.Count == 0).GroupBy(t => t.Main?.Kind).Select(x => $"{x.Key} {x.Count()}")));
        // (the clips of two ticks' length among them: their cuts of one tick, every one of which works on 61 of them)
        Assert.Equal((1390, 102, 82), (trims.Count(t => t.All), trims.Count(t => !t.All && t.Works.Count > 0), trims.Count(t => !t.All && t.StartOnly)));
    }

    /// <summary>The standing handgun idle works at the speeds from 1.05x to 1.95x of the slider only: 0.5x is refused before Apply, for
    /// a kind of stored data its head lacks, and the screen says what works.</summary>
    [GameDataFact]
    public void TheHandgunIdlesSpeedsThatWork()
    {
        var c = f.Install.FindClip("fm_std_HG_V0_idle_cool_06")!;
        var s = Core.Library.EditJobs.Speeds(c, Ctx(c), CancellationToken.None);
        Assert.Equal(Core.Library.EditJobs.Grid(1.05, 1.95, 0.05), s.Works);
        Assert.False(s.All);
        var half = s.Refused.Single(r => r.Factor == 0.5).Block;
        Assert.Equal(EditRefusal.ArenaKindMissing, half.Kind);
        Assert.Contains("arena kind 3", half.Detail);
        Assert.Equal(EditRefusal.ArenaKindMissing, s.Refused.Single(r => r.Factor == 2.0).Block.Kind);
        Assert.Equal("This clip's speed can be set from 1.05x to 1.95x. At the other speeds a clip at another speed would need a kind of stored data this clip's file has no room for, and the tool can't add that room yet.", s.Plain());
        Assert.Equal(1.25, s.Nearest(1.25));
        Assert.Equal(1.05, s.Nearest(0.5));
        // the screen's choices: speed available, as some speeds work; trim too, as the cuts that keep the clip's start work (a later
        // start needs stored data the clip's head has no room for)
        var choices = Core.Library.EditJobs.Choices(c, Ctx(c), null, 0, CancellationToken.None);
        Assert.True(choices[Core.Library.EditKind.Speed].Available);
        Assert.True(choices[Core.Library.EditKind.Trim].Available);
        Assert.Equal("· · S T ·", choices.Marks);
        var t = choices.Trims;
        Assert.True(t.StartOnly);
        Assert.Contains((0, 490), t.Works);
        Assert.Contains(t.Refused, r => r.Cut == (54, 490) && r.Block.Kind == EditRefusal.ArenaKindMissing);
        Assert.Equal("Of the trims tried, trims that keep the clip's start can be made. The others can't: a trimmed clip would need a kind of stored data this clip's file has no room for, "
            + "and the tool can't add that room yet. Each trim is tried when the handles stop.", t.Plain());
    }

    [GameDataFact]
    public void TheCensusOnTheGamesFiles()
    {
        var census = Within.Seconds(600, () => EditCensus.Run(f.Install, Graph()));
        foreach (var l in census.Summary()) output.WriteLine(l);
        foreach (var ed in EditCensus.EditNames) Assert.Empty(census.Faults(ed).Select(c => $"{c.Clip.Name}: {c.Edits[ed].Why}"));
        Assert.Equal(9938, census.Walked);
        Assert.True(census.GraphComplete);
        // the counts on the vanilla datafile (2026-10-05 files)
        Assert.Equal(1771, census.SwapGroups.Count);
        Assert.Equal(6800, census.SwapClips);
        Assert.Equal(35150, census.SwapPairs);
        Assert.Equal(7052, census.Armed);
        Assert.Equal(1044, census.MotionFieldClips);
        Assert.Equal(2852, census.Mirrorable);
        Assert.Equal(8840, census.LengthFree);
        Assert.Equal(8894, census.Scalable);
        Assert.Equal(EditCensusCounts.Mirror, census.Succeeded(EditCensus.Mirror));
        Assert.Equal(EditCensusCounts.Scale, census.Succeeded(EditCensus.Scale));
        Assert.Equal(EditCensusCounts.Trim, census.Succeeded(EditCensus.Trim));
        Assert.Equal(EditCensusCounts.SpeedUp, census.Succeeded(EditCensus.SpeedUp));
        Assert.Equal(EditCensusCounts.SlowDown, census.Succeeded(EditCensus.SlowDown));
        Assert.Equal(EditCensusCounts.UnderBlend, census.UnderBlend);
        Assert.Equal(EditCensusCounts.ArenaTrim, census.Refusals(EditCensus.Trim).GetValueOrDefault(EditRefusal.ArenaKindMissing));
        Assert.Equal(EditCensusCounts.ArenaMirror, census.Refusals(EditCensus.Mirror).GetValueOrDefault(EditRefusal.ArenaKindMissing));
        Assert.Equal(EditCensusCounts.NoMotionMirror, census.Refusals(EditCensus.Mirror).GetValueOrDefault(EditRefusal.NoMotion));
        Assert.Equal(EditCensusCounts.LongCurveMirror, census.Refusals(EditCensus.Mirror).GetValueOrDefault(EditRefusal.LongCurve));
        Assert.Equal(ArrayTimesCounts.Clips, census.ArrayTimes);
        Assert.Equal(EditCensusCounts.ArrayTimesLengthFree, census.ArrayTimesLengthFree);
        // refused for them by each edit (a trim or speed that moves no tick leaves the record as it was, so isn't refused)
        Assert.Equal(EditCensusCounts.TimesUnread, new[] { census.Refusals(EditCensus.Trim).GetValueOrDefault(EditRefusal.TimesUnread),
            census.Refusals(EditCensus.SpeedUp).GetValueOrDefault(EditRefusal.TimesUnread), census.Refusals(EditCensus.SlowDown).GetValueOrDefault(EditRefusal.TimesUnread) });
    }

    [GameDataFact]
    public void SwapWithItselfIsTheRecord()
    {
        var g = f.Install;
        int n = 0;
        foreach (var e in g.Clips)
        {
            var r = SwapEdit.Apply(e.Record!, e.Record!, Ctx(e));
            Assert.True(r.Bytes.AsSpan().SequenceEqual(e.Bytes.Span), e.Name);
            n++;
        }
        Assert.Equal(9938, n);
    }

    [GameDataFact]
    public void SpeedOneAndTrimOfNothingAreTheRecord()
    {
        int n = 0;
        foreach (var e in f.Install.Clips.Where(e => Graph().Verdict(e.Id).CanChangeLength))
        {
            Assert.True(RetimeEdit.Speed(e.Record!, Ctx(e), 1).Bytes.AsSpan().SequenceEqual(e.Bytes.Span), e.Name);
            Assert.True(RetimeEdit.Trim(e.Record!, Ctx(e), 0, e.LastTick).Bytes.AsSpan().SequenceEqual(e.Bytes.Span), e.Name);
            n++;
        }
        Assert.Equal(8840, n);
    }

    [GameDataFact]
    public void ScaleOneIsTheRecord()
    {
        int n = 0;
        foreach (var e in f.Install.Clips)
        {
            ScaledClip r;
            try { r = RootScaleEdit.Apply(e.Record!, Ctx(e), 1f, measure: false); }
            catch (EditRefusedException ex) when (ex.Kind is EditRefusal.NoRootMotion or EditRefusal.MotionField) { continue; }
            Assert.True(r.Bytes.AsSpan().SequenceEqual(e.Bytes.Span), e.Name);
            n++;
        }
        output.WriteLine($"scale 1 on {n} clips with root motion: the record each time");
        Assert.True(n > 4000);
    }

    /// <summary>Mirror twice: every curve comes back with its own times and values within the writers' rounding (0.02 deg, 0.001 cm, 1 mm
    /// for plants). The mirror writes no curve of 256 keys or more: a clip where it would rewrite one is refused with its own reason (the
    /// standing idle <c>mcm_nwp_fm_std_V0_idle_03</c>, 750 ticks, among them), and no curve it does rewrite holds more than 255 keys.</summary>
    [GameDataFact]
    public void MirrorTwiceIsTheClip()
    {
        var g = f.Install;
        var mirror = new SkeletonMirror(g.Skeleton);
        int clips = 0, curves = 0, longKept = 0;
        double worstDeg = 0, worstCm = 0;
        var longRefused = new List<string>();
        foreach (var e in g.Clips)
        {
            EditedClip once;
            try { once = MirrorEdit.Apply(e.Record!, Ctx(e), mirror); }
            catch (EditRefusedException ex)
            {
                if (ex.Kind == EditRefusal.LongCurve)
                {
                    Assert.Contains("this clip holds curves of 256 keys or more; the tool doesn't write such curves until one is seen playing", ex.Message);
                    longRefused.Add(e.Name);
                }
                continue;
            }
            var twice = MirrorEdit.Apply(once.Record, Ctx(e), mirror);
            var a = e.Record!; var z = twice.Record; var o = once.Record;
            clips++;
            for (int i = 0; i < a.Tracks.Count; i++)
            {
                if (a.Tracks[i] == EditTracks.Marker) continue;   // written again from the mover each time
                var u = SharedCurveTable.Resolve(a.Curves[i], g.SharedTable)!; var v = SharedCurveTable.Resolve(z.Curves[i], g.SharedTable)!;
                if (u.Kind is not (TrackKind.Rotation or TrackKind.Position or TrackKind.Plant)) continue;
                var ut = CurveDecoder.KeyTicks(u); var vt = CurveDecoder.KeyTicks(v);
                Assert.True(ut.SequenceEqual(vt), $"{e.Name} track {i}: the times differ");
                if (ut.Length > CurveWriter.MaxShortKeys)
                {
                    // a long curve in a mirrored clip is one the mirror left as it was
                    var w = SharedCurveTable.Resolve(o.Curves[i], g.SharedTable)!;
                    Assert.True(u.Source.AsSpan(u.At, u.DiskLength).SequenceEqual(w.Source.AsSpan(w.At, w.DiskLength)), $"{e.Name} track {i}: a curve of {ut.Length} keys rewritten");
                    longKept++;
                }
                curves++;
                for (int k = 0; k < v.Keys; k++)
                {
                    if (u.Kind == TrackKind.Rotation)
                    {
                        var want = CurveDecoder.KeyRot(u, k)!.Value;
                        double d = CurveValues.Deg(want, CurveDecoder.KeyRot(v, k)!.Value);
                        worstDeg = Math.Max(worstDeg, d);
                        Assert.True(d < 0.02, $"{e.Name} track {i} key {k}: {d} deg");
                    }
                    else if (u.Kind == TrackKind.Position)
                    {
                        var want = CurveDecoder.KeyPos(u, k)!.Value;
                        double d = (want - CurveDecoder.KeyPos(v, k)!.Value).Length() * 100;
                        worstCm = Math.Max(worstCm, d);
                        Assert.True(d < 0.001, $"{e.Name} track {i} key {k}: {d} cm");
                    }
                    else
                    {
                        var p = CurveDecoder.Plant(u, k)!.Value; var q = CurveDecoder.Plant(v, k)!.Value;
                        Assert.True(p.Tick == q.Tick && p.LengthTicks == q.LengthTicks && (p.Point - q.Point).Length() <= 0.0011f, $"{e.Name} plant track {i} key {k}");
                    }
                }
            }
        }
        output.WriteLine($"mirrored twice: {clips} clips, {curves} curves ({longKept} of more than 255 keys, each left as it was); worst {worstDeg:F4} deg, {worstCm:F5} cm");
        output.WriteLine($"refused for a curve of 256 keys or more the mirror would rewrite: {longRefused.Count} clips");
        Assert.Equal(EditCensusCounts.Mirror, clips);
        Assert.Equal(EditCensusCounts.LongCurveMirror, longRefused.Count);
        Assert.Contains("mcm_nwp_fm_std_V0_idle_03", longRefused);
    }

    /// <summary>The mirror in the world, checked apart from its own rule (<see cref="EditSynthetic.MirrorWorldError"/>): on every clip the
    /// mirror takes (the in-game mirror clips among them), each paired bone of the mirrored clip stands at every tick where
    /// its counterpart stood, reflected through the plane x = 0 of the mover's first place (1 cm), turned from its rest as the counterpart
    /// was, reflected (1 deg), and the mover's travel has x negated, y kept and its turn reversed. The rest pose is carried into the clip
    /// frame by +90 deg about z here, as a constant.</summary>
    [GameDataFact]
    public void MirrorIsAReflectionInTheWorld()
    {
        var g = f.Install;
        var mirror = new SkeletonMirror(g.Skeleton);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Assert.True(CurveValues.Deg(mirror.RestTurn, turn) < 0.01, $"rest turn {mirror.RestTurn}");
        Assert.Equal(1, mirror.RestLateralAxis);
        Assert.True(mirror.RestWorstCm < 0.1);
        // the named sides pair by name
        foreach (var b in g.Skeleton.Bones.Where(b => !b.FromAddOn && b.Name.StartsWith("Left", StringComparison.Ordinal)))
            Assert.Equal(g.Skeleton.Bones.First(x => x.Name == "Right" + b.Name[4..]).Index, mirror.Pair[b.Index]);
        var named = NamedMirrorClips;
        int n = 0;
        double cm = 0, deg = 0, mcm = 0, mdeg = 0; string worst = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in g.Clips)
        {
            bool always = named.Contains(e.Name);
            EditedClip r;
            try { r = MirrorEdit.Apply(e.Record!, Ctx(e), mirror); }
            catch (EditRefusedException) { Assert.False(always, $"{e.Name} is refused"); continue; }
            var x = EditSynthetic.MirrorWorldError(e.Record!, r.Record, g.Skeleton, g.SharedTable, mirror.Pair, turn);
            if (x.Cm > cm) worst = e.Name;
            cm = Math.Max(cm, x.Cm); deg = Math.Max(deg, x.Deg); mcm = Math.Max(mcm, x.MoverCm); mdeg = Math.Max(mdeg, x.MoverDeg);
            Assert.True(x.Cm <= 1 && x.Deg <= 1 && x.MoverCm <= 1 && x.MoverDeg <= 1, $"{e.Name}: {x}");
            n++;
        }
        output.WriteLine(FormattableString.Invariant($"mirror in the world: all {n} clips mirrored, in {sw.Elapsed.TotalSeconds:F0} s; worst {cm:F4} cm ({worst}), {deg:F4} deg; mover {mcm:F4} cm, {mdeg:F4} deg"));
        Assert.Equal(EditCensusCounts.Mirror, n);
    }

    /// <summary>Two named player clips the mirror must take: one turns, one moves sideways.</summary>
    static readonly string[] NamedMirrorClips = ["Mch_Bik_Driv_IntoR", "mch_pickup_passL_TO_PassR"];

    /// <summary>Two named player idles speed must take, both at 1.25 times the speed.</summary>
    static readonly string[] NamedSpeedClips = ["fm_std_HG_V0_idle_cool_06", "fm_crh_HG_V0_cool_idle_neutral_05"];

    /// <summary>The foot plants against the feet. A plant's point stands in the mover's frame at its start tick; read so, on the game's
    /// clips it stands near its foot. On every clip of a spread holding plants (every third, and the in-game test clips), scaled by 0.5,
    /// trimmed by a tenth at each end, played at 1.25 and 0.8 times the speed and mirrored, each written plant point stands from its foot
    /// (the nearer of the foot and the toes, on the ground, at the plant's middle tick) no more than 2 cm further than the game's point
    /// stood from the same foot at the same moment of the game's clip (for the mirror, the counterpart's point and foot; for a trim or a
    /// speed change, the game's key whose start the warp takes to this key's tick, and the source time the warp plays there).</summary>
    [GameDataFact]
    public void PlantPointsStayWithTheirFeet()
    {
        var g = f.Install;
        var sk = g.Skeleton; var table = g.SharedTable;
        var mirror = new SkeletonMirror(sk);
        int Bone(string n) => sk.Bones.First(b => b.Name == n && !b.FromAddOn).Index;
        int[][] feet = [[Bone("LeftFoot"), Bone("LeftToeBase")], [Bone("RightFoot"), Bone("RightToeBase")]];
        var named = new[] { "Mcm_Ar_Ale_Fm_Std_V0_IsExhausted_In" }.Concat(NamedSpeedClips).Concat(NamedMirrorClips).ToHashSet();
        static List<(uint Slot, int Key, FootPlant P)> PlantsOf(ClipRecord c, SharedCurveTable t)
        {
            var l = new List<(uint, int, FootPlant)>();
            for (int i = 0; i < c.Tracks.Count; i++)
            {
                if (!EditTracks.IsPlantSlot(c.Tracks[i])) continue;
                var u = SharedCurveTable.Resolve(c.Curves[i], t);
                if (u is null || u.Form != 22) continue;
                for (int k = 0; k < u.Keys; k++)
                    if (CurveDecoder.Plant(u, k) is { } p && !(p.LengthTicks == 0 && p.Point == Vector3.Zero)) l.Add((c.Tracks[i], k, p));
            }
            return l;
        }
        // a point's distance from its side's foot on the ground (cm): the point in the mover at tick t0, the foot at tick tm
        double Dist(ClipMotion m, Dictionary<int, Transform[]> worlds, int side, Vector3 p, int t0, int tm)
        {
            t0 = Math.Clamp(t0, 0, m.Last); tm = Math.Clamp(tm, 0, m.Last);
            if (!worlds.TryGetValue(tm, out var w)) worlds[tm] = w = m.World(tm);
            var q = m.Mover[t0].Apply(p);
            return feet[side].Min(b => new Vector2(w[b].Position.X - q.X, w[b].Position.Y - q.Y).Length()) * 100;
        }
        var sample = g.Clips.Where(e => e.Record is { WalkedToEnd: true } c && PlantsOf(c, table).Count > 0).Where((e, i) => i % 3 == 0 || named.Contains(e.Name)).ToList();
        string[] edits = ["scale", "trim", "speed 1.25", "speed 0.8", "mirror"];
        var checkedKeys = edits.ToDictionary(x => x, _ => 0);
        var worst = edits.ToDictionary(x => x, _ => (Cm: double.NegativeInfinity, Clip: ""));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in sample)
        {
            var c = e.Record!;
            int last = c.LastTick, cut = Math.Max(1, last / 10);
            int from = last > 2 * cut ? cut : 0, to = last > 2 * cut ? last - cut : last;
            var game = ClipMotion.Sample(c, sk, table);
            var gw = new Dictionary<int, Transform[]>();
            var gp = PlantsOf(c, table);
            // each re-timing's warp, the one the edit runs, made once the edit has taken the clip
            foreach (var (edit, run, warpOf) in new (string, Func<EditedClip>, Func<TimeWarp>?)[]
            {
                ("scale", () => RootScaleEdit.Apply(c, Ctx(e), 0.5f, measure: false), null),
                ("trim", () => RetimeEdit.Trim(c, Ctx(e), from, to), () => TimeWarp.Trim(last, from, to)),
                ("speed 1.25", () => RetimeEdit.Speed(c, Ctx(e), 1.25), () => TimeWarp.Speed(last, 1.25)),
                ("speed 0.8", () => RetimeEdit.Speed(c, Ctx(e), 0.8), () => TimeWarp.Speed(last, 0.8)),
                ("mirror", () => MirrorEdit.Apply(c, Ctx(e), mirror), null),
            })
            {
                EditedClip r;
                try { r = run(); }
                catch (EditRefusedException ex)
                {
                    // the named clips above are each taken by their own edit
                    bool must = edit switch
                    {
                        "scale" => e.Name == "Mcm_Ar_Ale_Fm_Std_V0_IsExhausted_In",
                        "mirror" => NamedMirrorClips.Contains(e.Name),
                        "trim" => e.Name == "fm_crh_HG_V0_cool_idle_neutral_05",
                        "speed 1.25" => NamedSpeedClips.Contains(e.Name),
                        _ => false,
                    };
                    Assert.False(must, $"{e.Name}: {edit} refused: {ex.Message}");
                    continue;
                }
                var warp = warpOf?.Invoke();
                var ed = ClipMotion.Sample(r.Record, sk, table);
                var ew = new Dictionary<int, Transform[]>();
                foreach (var (slot, _, p) in PlantsOf(r.Record, table))
                {
                    uint gslot = edit == "mirror" ? EditTracks.FirstPlantSlot + ((slot - EditTracks.FirstPlantSlot) ^ 1) : slot;
                    // the game's key this one came from: the same place in its slot's list; under a warp, the first key whose start the warp
                    // takes to this tick (a key the cut starts goes to tick 0), among the keys the new clip still plays
                    var src = gp.Where(x => x.Slot == gslot).Select(x => x.P).ToList();
                    FootPlant? k = warp is { } w
                        ? src.Where(x => x.Tick + Math.Max(0, x.LengthTicks) >= w.SourceOf(0) - 1e-6 && x.Tick <= w.SourceOf(w.NewLast) + 1e-6)
                            .Select(x => (FootPlant?)x).FirstOrDefault(x => (x!.Value.Tick <= w.SourceOf(0) + 1e-6 ? 0 : w.FirstReaching(x.Value.Tick)) == p.Tick)
                        : src.Where(x => x.Tick == p.Tick && x.LengthTicks == p.LengthTicks).Select(x => (FootPlant?)x).FirstOrDefault();
                    Assert.True(k is not null, $"{e.Name} {edit}: no game key for slot {slot - EditTracks.FirstPlantSlot} tick {p.Tick}");
                    int tm = p.Tick + Math.Max(0, p.LengthTicks) / 2;
                    // the same moment of the game's clip: the source time the warp plays at the middle tick
                    int gm = warp is null ? tm : (int)Math.Round(warp.SourceOf(tm));
                    double de = Dist(ed, ew, EditTracks.PlantSide(slot), p.Point, p.Tick, tm);
                    double dg = Dist(game, gw, EditTracks.PlantSide(gslot), k!.Value.Point, k.Value.Tick, gm);
                    if (de - dg > worst[edit].Cm) worst[edit] = (de - dg, $"{e.Name} slot {slot - EditTracks.FirstPlantSlot} tick {p.Tick}: {de:F1} cm against the game's {dg:F1}");
                    Assert.True(de <= dg + 2, $"{e.Name} {edit}, slot {slot - EditTracks.FirstPlantSlot} tick {p.Tick}: the point stands {de:F1} cm from its foot, the game's {dg:F1} cm");
                    checkedKeys[edit]++;
                }
            }
        }
        foreach (var (edit, n) in checkedKeys) output.WriteLine(FormattableString.Invariant($"{edit}: {n} plant keys; the most a point stands further from its foot than the game's: {worst[edit].Cm:F2} cm ({worst[edit].Clip})"));
        output.WriteLine(FormattableString.Invariant($"{sample.Count} clips with plants, in {sw.Elapsed.TotalSeconds:F0} s"));
        Assert.All(checkedKeys.Values, n => Assert.True(n > 100, $"{n} keys checked"));
    }

    /// <summary>The edit command's path end to end (<see cref="EditPackage.Run"/> then <see cref="EditPackage.Write"/>, as the command line
    /// does): trim, mirror and speed each written into a folder of the test's own as one record file, nothing else left there, the edit's
    /// account in its lines; the same file again is refused without overwrite and leaves the folder as it was, and another clip's file goes
    /// beside it.</summary>
    [GameDataFact]
    public void TheEditPathWritesTheRecordFileAlone()
    {
        var g = f.Install;
        string root = Path.Combine(AppContext.BaseDirectory, "edit-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool Takes(ClipEntry x)
            {
                if (!Graph().Verdict(x.Id).CanChangeLength || ByteArrayTimes.HoldsTimes(x.Record!) || x.LastTick <= 40) return false;
                try { RetimeEdit.Trim(x.Record!, Ctx(x), 5, x.LastTick - 5); RetimeEdit.Speed(x.Record!, Ctx(x), 1.25); return true; }
                catch (EditRefusedException) { return false; }
            }
            var length = g.Clips.First(Takes);
            var mirrored = g.FindClip("mch_pickup_passL_TO_PassR")!;
            var runs = new (string Edit, ClipEntry Clip, double? Factor, int? From, int? To)[]
            {
                ("trim", length, null, 5, length.LastTick - 5), ("mirror", mirrored, null, null, null), ("speed", length, 1.25, null, null),
            };
            foreach (var (edit, e, factor, from, to) in runs)
            {
                string dir = Path.Combine(root, edit);
                var r = EditPackage.Run(e.Record!, Ctx(e), edit, out var desc, factor, from, to);
                var pkg = EditPackage.Write(g, dir, e, r);
                Assert.Equal(new[] { pkg.Files[0].FileName }, Directory.GetFiles(dir).Select(x => Path.GetFileName(x)));
                Assert.Equal(RecordFile.Content(r.Bytes), File.ReadAllBytes(pkg.Files[0].Path));
                var account = EditPackage.AccountLines(e, r, desc, Path.GetFileName(g.PatchForgePath));
                Assert.Contains($"edit: {e.Name} ({desc})", account);
                Assert.Contains(account, l => l.StartsWith("done or kept: ", StringComparison.Ordinal));
                Assert.Contains(account, l => l.StartsWith("arena kind ", StringComparison.Ordinal));
                output.WriteLine($"{edit} {e.Name}: {pkg.Files[0].Length} B");
            }
            // the same file again into the mirror's folder: refused as it stands, the folder left as it was
            string mdir = Path.Combine(root, "mirror");
            var before = Directory.GetFiles(mdir).ToDictionary(x => x, File.ReadAllBytes);
            var again = EditPackage.Run(mirrored.Record!, Ctx(mirrored), "mirror", out _);
            Assert.Throws<RecordFileException>(() => EditPackage.Write(g, mdir, mirrored, again));
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(mdir).Order());
            foreach (var (p, b) in before) Assert.Equal(b, File.ReadAllBytes(p));
            // another clip's file goes beside it; with overwrite the same file is replaced
            var other = EditPackage.Run(length.Record!, Ctx(length), "trim", out _, from: 5, to: length.LastTick - 5);
            EditPackage.Write(g, mdir, length, other);
            EditPackage.Write(g, mdir, mirrored, again, overwrite: true);
            Assert.Equal(2, Directory.GetFiles(mdir).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>The raw writer's control: every inline curve of the forms it writes by key time (13, 14, 15, 16, 18, 19), its keys and values
    /// written again, gives the game's bytes in the one- and two-byte time forms, and the same times and values from a bitmap form.</summary>
    [GameDataFact]
    public void TheRawWriterGivesTheGamesCurvesBack()
    {
        var g = f.Install;
        var forms = new SortedDictionary<int, (int Same, int Bitmap)>();
        var curves = g.Clips.SelectMany(e => e.Record!.Curves).Concat(g.SharedTable.Curves).Where(c => !c.IsReference && c.Form is 13 or 14 or 15 or 16 or 18 or 19);
        foreach (var c in curves)
        {
            var ticks = CurveDecoder.KeyTicks(c);
            var vals = Enumerable.Range(0, (int)c.Keys).Select(k => ScalarDecoder.KeyRaw(c, k)!).ToList();
            var w = CurveRetimer.WriteRaw(c.Form, ticks, vals);
            var x = forms.GetValueOrDefault(c.Form);
            if (c.Tag % 3 != 2)
            {
                Assert.True(w.AsSpan().SequenceEqual(c.Source.AsSpan(c.At, c.DiskLength)), $"tag 0x{c.Tag:X2}, {c.Keys} keys: not the game's bytes");
                x.Same++;
            }
            else
            {
                var back = CurveWalker.Walk(w, 0, 1, out _, out _)[0];
                Assert.Equal(ticks, CurveDecoder.KeyTicks(back));
                for (int k = 0; k < c.Keys; k++) Assert.Equal(vals[k], ScalarDecoder.KeyRaw(back, k));
                x.Bitmap++;
            }
            forms[c.Form] = x;
        }
        foreach (var (form, (same, bitmap)) in forms) output.WriteLine($"form {form}: {same} curves the game's bytes, {bitmap} bitmap curves the same times and values");
        Assert.True(forms.Values.Sum(v => v.Same) > 1000);
    }

    /// <summary>A timed list (events of class 0x83EC6C6D) re-timed with the clip: each time follows the new length. The greeting, whose
    /// byte array holds times too, is refused for them.</summary>
    [GameDataFact]
    public void ATimedListIsRetimed()
    {
        var hello = f.Install.FindClip("mca_emote_Hello")!;
        Assert.Equal(EditRefusal.TimesUnread, Assert.Throws<EditRefusedException>(() => RetimeEdit.Speed(hello.Record!, Ctx(hello), 1.25)).Kind);
        Assert.Equal(EditRefusal.TimesUnread, Assert.Throws<EditRefusedException>(() => RetimeEdit.Trim(hello.Record!, Ctx(hello), 10, 110)).Kind);
        var e = f.Install.Clips.First(x => Graph().Verdict(x.Id).CanChangeLength && !ByteArrayTimes.HoldsTimes(x.Record!)
            && Core.Export.EventObjects.Groups(x.Record!).Any(gr => gr.TimesAt >= 0 && gr.Times.Count > 0 && gr.Times.Min() >= 24 && gr.Events.Any(ev => ev.Class == 0x83EC6C6D)));
        var c = e.Record!;
        var before = EventObjects(c);
        var r = RetimeEdit.Speed(c, Ctx(e), 1.25);
        var after = EventObjects(r.Record);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
            Assert.True(Math.Abs(after[i] - before[i] * (double)r.Record.LastTick / c.LastTick) <= 1, $"{e.Name}: {before[i]} -> {after[i]}, {c.LastTick} -> {r.Record.LastTick} ticks");
        output.WriteLine($"{e.Name}: [{string.Join(" ", before)}] at {c.LastTick} ticks -> [{string.Join(" ", after)}] at {r.Record.LastTick}");
    }

    /// <summary>The in-game test clips: each edit succeeds on the game's own clip with no warning from the graph (no motion field, no blend
    /// parent), and the trim and speed clips hold the track forms the re-timing writes. The earlier trim and speed clips hold times in their
    /// byte arrays, so both are refused on them until those are read.</summary>
    [GameDataFact]
    public void TheInGameTestClipsEdit()
    {
        var g = f.Install;
        ClipEntry C(string n) => g.FindClip(n) ?? throw new InvalidOperationException(n);
        foreach (var run in new Func<EditedClip>[]
        {
            () => RetimeEdit.Trim(C("Mch_HG_Std_ReloadTactical_P227").Record!, Ctx(C("Mch_HG_Std_ReloadTactical_P227")), 12, 78),
            () => RetimeEdit.Speed(C("mca_pickup_carry_npc").Record!, Ctx(C("mca_pickup_carry_npc")), 1.25),
            () => RetimeEdit.Speed(C("mca_pickup_carry_npc_FaceDown").Record!, Ctx(C("mca_pickup_carry_npc_FaceDown")), 1.25),
        })
            Assert.Equal(EditRefusal.TimesUnread, Assert.Throws<EditRefusedException>(run).Kind);
        var results = new List<(string, EditedClip)>
        {
            ("trim", RetimeEdit.Trim(C("fm_crh_HG_V0_cool_idle_neutral_05").Record!, Ctx(C("fm_crh_HG_V0_cool_idle_neutral_05")), 54, 484)),
            ("trim", RetimeEdit.Trim(C("Mch_Face_React_Hit03").Record!, Ctx(C("Mch_Face_React_Hit03")), 4, 36)),
            ("speed", RetimeEdit.Speed(C("fm_std_HG_V0_idle_cool_06").Record!, Ctx(C("fm_std_HG_V0_idle_cool_06")), 1.25)),
            ("speed", RetimeEdit.Speed(C("fm_crh_HG_V0_cool_idle_neutral_05").Record!, Ctx(C("fm_crh_HG_V0_cool_idle_neutral_05")), 1.25)),
            ("swap", SwapEdit.Apply(C("mca_emote_Freeze").Record!, C("mca_emote_Diamond_Wedge").Record!, Ctx(C("mca_emote_Freeze")))),
            ("mirror", MirrorEdit.Apply(C("Mch_Bik_Driv_IntoR").Record!, Ctx(C("Mch_Bik_Driv_IntoR")))),
            ("mirror", MirrorEdit.Apply(C("mch_pickup_passL_TO_PassR").Record!, Ctx(C("mch_pickup_passL_TO_PassR")))),
            ("scale", RootScaleEdit.Apply(C("mca_fm_dive_V2_AR_RHS").Record!, Ctx(C("mca_fm_dive_V2_AR_RHS")), 0.5f)),
            ("scale", RootScaleEdit.Apply(C("mca_fm_dive_V2_NoW").Record!, Ctx(C("mca_fm_dive_V2_NoW")), 0.5f)),
            ("scale", RootScaleEdit.Apply(C("Mcm_Ar_Ale_Fm_Std_V0_IsExhausted_In").Record!, Ctx(C("Mcm_Ar_Ale_Fm_Std_V0_IsExhausted_In")), 0.5f)),
        };
        foreach (var (edit, r) in results)
        {
            // the bike mount holds two tracks shaped like an IK offset (the hands on the handlebars, likely): the mirror names them
            if (r.Record.Name == "Mch_Bik_Driv_IntoR") Assert.Single(r.Warnings, w => w.Contains("IK offset") && w.Contains("4DE55211") && w.Contains("C89B57B1"));
            else Assert.Empty(r.Warnings);
            Assert.False(EditVerdicts.UnderBlend(Graph().Verdict(r.Record.Id)), r.Record.Name);
            if (r is ScaledClip s) Assert.True(s.ResidualSlideCm < 2.5, $"{r.Record.Name}: {s.ResidualSlideCm} cm");
            output.WriteLine($"{edit} {r.Record.Name}: {r.Record.LastTick} ticks, {r.Rebuilt.Rewritten} curves rewritten");
        }
        // between them the trim and speed clips hold each form the re-timing writes (the form-14 and bitmap curves only in the face's clip)
        var probes = new[] { "fm_crh_HG_V0_cool_idle_neutral_05", "Mch_Face_React_Hit03", "fm_std_HG_V0_idle_cool_06" }.Select(n => C(n).Record!).ToList();
        var tags = probes.SelectMany(c => Enumerable.Range(0, c.Curves.Count).Select(i => SharedCurveTable.Resolve(c.Curves[i], g.SharedTable)!)).Where(u => u.Keys > 1).Select(u => u.Tag).ToHashSet();
        Assert.Contains(tags, t => t / 3 == 13);
        Assert.Contains(tags, t => t / 3 == 18);
        Assert.Contains(tags, t => t / 3 is 14 or 15);
        Assert.Contains(tags, t => t is 0x29 or 0x2C);
        Assert.Contains(probes, c => Core.Export.EventObjects.Groups(c).Any(x => x.TimesAt >= 0 && x.Events.Any(e => e.Class == 0x83EC6C6D)));
        Assert.Contains(probes, c => Core.Export.EventObjects.Groups(c).Any(x => x.Events.Any(e => e.Class == Core.Export.EventObjects.StepEvent)));
        Assert.Contains(probes, c => c.LastTick > 255);
    }

    static List<int> EventObjects(ClipRecord c) => Core.Export.EventObjects.Groups(c).SelectMany(g => g.Times).ToList();

    /// <summary>Every edit's output, on a spread of clips with non-trivial parameters, walks to its end and samples to finite values at
    /// every tick.</summary>
    [GameDataFact]
    public void EveryOutputDecodesAndWalksToItsEnd()
    {
        var g = f.Install;
        var mirror = new SkeletonMirror(g.Skeleton);
        int outputs = 0;
        foreach (var e in g.Clips.Where((_, i) => i % 11 == 0))
        {
            var c = e.Record!;
            var runs = new List<Func<EditedClip>>
            {
                () => MirrorEdit.Apply(c, Ctx(e), mirror), () => RootScaleEdit.Apply(c, Ctx(e), 1.7f, measure: false),
                () => RetimeEdit.Speed(c, Ctx(e), 0.7), () => RetimeEdit.Speed(c, Ctx(e), 1.9), () => RetimeEdit.Trim(c, Ctx(e), c.LastTick / 3, c.LastTick - c.LastTick / 5),
            };
            foreach (var run in runs)
            {
                EditedClip r;
                try { r = run(); }
                catch (EditRefusedException) { continue; }
                var back = ClipRecord.Parse(r.Bytes);
                Assert.True(back.WalkedToEnd, $"{e.Name}: {back.Stop}");
                Assert.Null(EditRebuild.Check(r.Bytes, g.SharedTable).Why);
                var s = new ClipSampler(back, g.SharedTable);
                for (int t = 0; t <= back.LastTick; t++)
                {
                    s.At(t / 60f);
                    foreach (var q in s.Rotations) Assert.True(float.IsFinite(q.X + q.Y + q.Z + q.W), e.Name);
                    foreach (var p in s.Positions) Assert.True(float.IsFinite(p.X + p.Y + p.Z), e.Name);
                }
                outputs++;
            }
        }
        output.WriteLine($"{outputs} edited records read back whole");
        Assert.True(outputs > 1000);   // fewer trims and speed changes since clips with times in their byte array are refused
    }

    /// <summary>Damaged copies of the game's records: every edit ends in a result that reads back whole or in a refusal, nothing else.</summary>
    [GameDataFact]
    public void DamagedRecordsAreRefusedCleanly()
    {
        var g = f.Install;
        var mirror = new SkeletonMirror(g.Skeleton);
        var rnd = new Random(31);
        var names = new[] { "mca_emote_Hello", "mca_fm_dive_V2_NoW", "mca_emote_Much_Obliged", "Mch_Bik_Driv_IntoR", "mca_pickup_carry_npc" };
        var sample = names.Select(n => g.FindClip(n)!).Concat(Enumerable.Range(0, 40).Select(_ => g.Clips[rnd.Next(g.Clips.Count)])).ToList();
        int runs = 0, refused = 0;
        Within.Seconds(600, () =>
        {
            foreach (var e in sample)
            {
                var b = e.Bytes.ToArray();
                var damaged = new List<byte[]> { b[..(b.Length / 2)], b[..(b.Length - 1)] };
                for (int i = 0; i < 30; i++) { var x = (byte[])b.Clone(); x[rnd.Next(x.Length)] ^= (byte)(1 << rnd.Next(8)); damaged.Add(x); }
                for (int i = 0; i < 5; i++) { var x = (byte[])b.Clone(); int s = rnd.Next(x.Length); x.AsSpan(s, Math.Min(32, x.Length - s)).Fill(0xFF); damaged.Add(x); }
                foreach (var x in damaged)
                {
                    ClipRecord c;
                    try { c = EditRebuild.Parse(x); }
                    catch (EditRefusedException) { refused++; runs++; continue; }
                    var other = g.FindClip(e.Id)!.Record!;
                    foreach (var run in new Func<EditedClip>[]
                    {
                        () => MirrorEdit.Apply(c, Ctx(e), mirror), () => RootScaleEdit.Apply(c, Ctx(e), 0.6f, measure: false),
                        () => RetimeEdit.Speed(c, Ctx(e), 1.3), () => RetimeEdit.Trim(c, Ctx(e), 1, Math.Max(2, c.LastTick - 1)),
                        () => SwapEdit.Apply(other, c, Ctx(e)), () => SwapEdit.Apply(c, other, Ctx(e)),
                    })
                    {
                        runs++;
                        try { Assert.Null(EditRebuild.Check(run().Bytes, g.SharedTable).Why); }
                        catch (EditRefusedException) { refused++; }
                    }
                }
            }
            return 0;
        });
        output.WriteLine($"{runs} edits of damaged records, {refused} refused, the rest read back whole");
        Assert.True(refused > 0);
    }
}

/// <summary>The census's edit counts on the vanilla datafile (2026-10-05 files), shared by the tests that check them.</summary>
static class EditCensusCounts
{
    /// <summary>Mirror: 2,852 clips with no hand track and no motion field; refused for an arena kind, for a track with no counterpart
    /// track, for holding no bone or mover track (the face and dialogue clips), and for a curve of 256 keys or more it would rewrite.</summary>
    public const int Mirror = 2387, Scale = 4641, Trim = 1408, SpeedUp = 1480, SlowDown = 1414, UnderBlend = 1986, ArenaTrim = 143, ArenaMirror = 32;
    /// <summary>Mirror refusals: no bone or mover track; a curve of 256 keys or more the mirror would rewrite.</summary>
    public const int NoMotionMirror = 205, LongCurveMirror = 221;
    /// <summary>Clips no graph place holds the length of, refused trim and speed for times in their byte arrays.</summary>
    public const int ArrayTimesLengthFree = 7277;
    /// <summary>Refused for them by trim, speed 1.25 and speed 0.8.</summary>
    public static readonly int[] TimesUnread = [7277, 7277, 7277];
}
