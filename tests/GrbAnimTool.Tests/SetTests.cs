using System.Buffers.Binary;
using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Skeletons;
using static GrbAnimTool.Tests.GraphSynthetic;

namespace GrbAnimTool.Tests;

/// <summary>Sets: read from a state's variable table on synthetic graph documents (a random selection, a blend, a node that isn't a set,
/// a table that doesn't read), the probable sets by name, each clip's own piece of one long source, the line-up's seams against a control
/// without it, and each clip its own exact length.</summary>
public sealed class SetTests
{
    const uint Table = 0xFFCBD7D4, Root = 0xF55E36AE, Random = 0xEB8D3D0C, Blend = 0x12E2CE59, IfNode = 0x8BEEE08D, Property = 0x4EEB8E90;

    static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); return b; }
    static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    static byte[] Cat(params byte[][] p) => [.. p.SelectMany(x => x)];

    /// <summary>A node's ID object holding its index.</summary>
    static byte[] NodeId(int index, ulong clip = 0) => Cat(Obj(GraphClasses.IdObject), [0x02, 0x40], Enumerable.Repeat((byte)0x11, 16).ToArray(), U16(index),
        new byte[8], [0, 0, 0x0A], U64(clip), [0, 0, 0]);

    /// <summary>A node with no mask bits and <paramref name="vars"/> values of the table.</summary>
    static byte[] Node(uint cls, int index, int vars) => Cat(Obj(cls), [0], U16(vars), U16(0), NodeId(index));

    static byte[] ClipNode(int index, ulong clip) => Cat(Obj(GraphClasses.ClipNode), ClipNodeFields, NodeId(index, clip));

    static readonly Guid Speed = new("0123456789abcdef0123456789abcdef");

    /// <summary>A state: the table, the root linking node 1, node 1 of <paramref name="chooser"/> linking the two clip nodes (after an input
    /// value for a blend), and the clip nodes; <paramref name="countOff"/> spoils the table's node count.</summary>
    static GraphDocument State(uint chooser, ulong a, ulong b, int countOff = 0)
    {
        bool input = chooser == Blend;
        var kinds = new List<uint> { 8 };
        var values = new List<byte[]> { Cat([0, 0x08], U32(0)) };
        if (input) { kinds.Add(0); values.Add(Cat([0, 0x19], Speed.ToByteArray())); }
        kinds.AddRange([8, 8]); values.Add(Cat([0, 0x08], U32(1))); values.Add(Cat([0, 0x08], U32(2)));
        for (int i = 0; i < 4; i++) { kinds.Add(7); values.Add(Cat([0, 0x07], U32(0))); }
        var table = Cat(Obj(Table), [0x03, 0x60], Varint(kinds.Count), Cat(kinds.Select(U32).ToArray()), Varint(values.Count), Cat([.. values]), [(byte)(4 + countOff)]);
        var name = System.Text.Encoding.ASCII.GetBytes("Speed");
        var prop = Cat(Obj(Property), [0x05, 0x78, (byte)name.Length, 0x01], name, U32(0), Speed.ToByteArray());
        var body = Cat(table, Node(Root, 0, 1), Node(chooser, 1, input ? 3 : 2), ClipNode(2, a), ClipNode(3, b), prop);
        var doc = Document([(GraphDocument.HandleListClass, 1), (Table, 1), (Root, 1), (chooser, 1), (GraphClasses.ClipNode, 2), (GraphClasses.IdObject, 0), (Property, 0)],
            [(1, 0), (1, 1), (1, 2), (1, 3), (1, 4), (1, 4)], body);
        return GraphDocument.Read(Record(7, GraphDocument.DocumentType, "Idles-StateMachine", doc));
    }

    static readonly (ulong, string, int)[] Names = [(ClipA, "x_idle_01", 100), (ClipB, "x_idle_02", 120), (ClipC, "x_idle_03", 90), (ClipD, "y_walk", 60)];

    [Fact]
    public void ARandomSelectionsClipsAreASetReadFromTheGraph()
    {
        var sets = ClipSets.Read([State(Random, ClipA, ClipB)], Names);
        Assert.Equal((1, 0), (sets.StatesRead, sets.StatesUnread));
        var s = Assert.Single(sets.All, x => x.FromGraph);
        Assert.Equal(ClipSetKind.Random, s.Kind);
        Assert.Equal([ClipA, ClipB], s.Clips);
        Assert.Same(s, sets.Main(ClipA));
        Assert.Equal("set (random)", s.Words);
        // no blend read here: the preview's blend is the usual 0.3 s
        Assert.Null(s.BlendSeconds);
        Assert.Equal(ClipSet.DefaultBlendSeconds, s.PreviewBlend);
        // x_idle_03 is in no graph set: alone in its name group now, so in no probable set either
        Assert.Null(sets.Main(ClipC));
    }

    [Fact]
    public void ABlendsClipsAreABlendedSetWithTheValueItReads()
    {
        var s = ClipSets.Read([State(Blend, ClipA, ClipB)], Names).Main(ClipA)!;
        Assert.Equal(ClipSetKind.Blended, s.Kind);
        Assert.Equal("Speed", s.Input);
        Assert.Equal("blended set (Speed)", s.Words);
        Assert.Equal([ClipA, ClipB], s.Clips);
    }

    /// <summary>The controls: a condition isn't a set, and its clips, which the graph read, are never grouped by name either; a state
    /// whose table doesn't read gives no graph set and leaves its clips to their names, as a probable set.</summary>
    [Fact]
    public void ANodeThatIsntASetIsNoSetAndATableThatDoesntReadLeavesTheClipsToTheirNames()
    {
        var cond = ClipSets.Read([State(IfNode, ClipA, ClipB)], Names);
        Assert.Equal(1, cond.StatesRead);
        Assert.Empty(cond.All);
        Assert.Null(cond.Main(ClipA));
        // known to be in no set (the graph read its state); a clip no state read names, not known
        Assert.True(cond.ReadByGraph(ClipA));
        Assert.False(cond.ReadByGraph(ClipD));

        var unread = ClipSets.Read([State(Random, ClipA, ClipB, countOff: 1)], Names);
        Assert.Equal((0, 1), (unread.StatesRead, unread.StatesUnread));
        Assert.False(unread.ReadByGraph(ClipA));
        Assert.DoesNotContain(unread.All, x => x.FromGraph);
        var p = unread.Main(ClipA)!;
        Assert.Equal(ClipSetKind.Probable, p.Kind);
        Assert.False(p.FromGraph);
        Assert.Equal("probable set", p.Words);
        Assert.Equal([ClipA, ClipB, ClipC], p.Clips);
        Assert.Null(unread.Main(ClipD));
    }

    [Fact]
    public void TheNameFallbackGroupsANumberedEndingOfOneFamilyAndLength()
    {
        (ulong, string, int)[] clips = [(1, "a_idle_01", 100), (2, "a_idle_02", 199), (3, "a_idle03", 120), (4, "b_01", 100), (5, "b_02", 300), (6, "c_idle", 50), (7, "c_idle2", 50), (8, "d_01", 40)];
        var sets = ClipSets.Read([], clips);
        Assert.Equal([1UL, 2UL], sets.Main(1)!.Clips);   // a_idle03 is another family: "a_idle" against "a_idle_"
        Assert.All(sets.All, s => Assert.Equal(ClipSetKind.Probable, s.Kind));
        Assert.Null(sets.Main(3));
        Assert.Null(sets.Main(4));                         // 300 ticks is over twice 100: not one length
        Assert.Null(sets.Main(6));                         // c_idle has no numbered ending
        Assert.Null(sets.Main(8));
        Assert.Single(sets.All);
        // a clip in a graph set is never put in a probable one
        Assert.Empty(ClipSets.Probable(clips, [1, 2]).Where(s => s.Clips.Contains(1UL) || s.Clips.Contains(2UL)));
    }

    // ---- pieces and the line-up, on a synthetic body ----

    static readonly Skeleton S = Humanoid.Skeleton();
    static readonly int Spine = Humanoid.Bone(S, "Spine");

    static Motion Of(int last, Func<int, double> deg) => Humanoid.Motion(S, last, (t, l) => l[Spine] = l[Spine] with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(deg(t) * Math.PI / 180)) });

    /// <summary>One long source: each clip's piece is its own, none overlapping, and their loops close together at least as well as the
    /// best of every placement in the clips' order (a brute force over all of them; any order may be taken, so it may do better).</summary>
    [Fact]
    public void EachClipTakesItsOwnPieceOfALongSource()
    {
        var src = Of(60, t => 40 * Math.Sin(t * 0.37) + 3 * Math.Cos(t * 1.3));
        int[] lasts = [10, 12, 9];
        var (st, overlap) = ClipFit.SuggestPieces(src, lasts);
        Assert.False(overlap);
        for (int i = 0; i < 3; i++) Assert.InRange(st[i], 0, src.LastTick - lasts[i]);
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                Assert.True(st[i] + lasts[i] <= st[j] || st[j] + lasts[j] <= st[i], $"pieces {i} and {j} overlap: {st[i]} and {st[j]}");
        double Cost(int i, int s) => ClipFit.MatchDistance(src, s, src, s + lasts[i]);
        double best = double.MaxValue;
        for (int a = 0; a + 10 <= 60; a++)
            for (int b = a + 10; b + 12 <= 60; b++)
                for (int c = b + 12; c + 9 <= 60; c++)
                    best = Math.Min(best, Cost(0, a) + Cost(1, b) + Cost(2, c));
        Assert.True(Cost(0, st[0]) + Cost(1, st[1]) + Cost(2, st[2]) <= best + 1e-3);
        // too short to hold them all: overlapping, but apart
        var (st2, overlap2) = ClipFit.SuggestPieces(src, [40, 35]);
        Assert.True(overlap2);
        Assert.NotEqual(st2[0], st2[1]);
        Assert.Throws<ArgumentException>(() => ClipFit.SuggestPieces(src, [61]));
    }

    /// <summary>Lined up, every clip ends on the pose the next one starts on (the seam's pose distance under 0.05); the control, the same
    /// clips not lined up, jumps by far more. Each clip keeps its own exact length, with the loop and without.</summary>
    [Fact]
    public void LiningUpTheSetClosesEverySeam()
    {
        var clips = new[] { (Of(80, t => 30 + t * 0.2), 50), (Of(90, t => -25 + t * 0.1), 61), (Of(70, t => 5 * Math.Sin(t)), 40) };
        var pose = S.RestLocals();
        pose[Spine] = pose[Spine] with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f) };
        foreach (bool loop in new[] { false, true })
        {
            List<Motion> Fit(Transform[]? shared) => clips.Select(c => ClipFit.Apply(c.Item1, c.Item2, new FitOptions { Mode = FitMode.Cut, CutStart = 3, Loop = loop, LoopFrames = 12, SharedPose = shared }).Motion).ToList();
            var lined = Fit(pose);
            var plain = Fit(null);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(clips[i].Item2, lined[i].LastTick);
                Assert.Equal(clips[i].Item2, plain[i].LastTick);
                var (a, b) = (lined[i], lined[(i + 1) % 3]);
                Assert.InRange(ClipFit.PoseDistance(a, a.LastTick, b, 0), 0, 0.05);
            }
            double worst = Enumerable.Range(0, 3).Max(i => ClipFit.PoseDistance(plain[i], plain[i].LastTick, plain[(i + 1) % 3], 0));
            Assert.True(worst > 1, $"the control's seams are {worst:F3}: the line-up isn't shown to do anything");
        }
    }

    [Fact]
    public void ThePreviewChainsTheClipsWithTheBlend()
    {
        var a = Of(20, _ => 0); var b = Of(30, _ => 40);
        var (chain, starts) = ClipSetReplace.Preview([a, b], 10);
        Assert.Equal([0, 21], starts);
        Assert.Equal(20 + 1 + 30, chain.LastTick);
        // half way through the blend the spine stands half way between the last clip's end and the next one's frame
        double Deg(int t) { var q = chain.Local(t, Spine).Rotation; return 2 * Math.Atan2(q.Z, q.W) * 180 / Math.PI; }
        Assert.Equal(0, Deg(20), 3);
        Assert.Equal(20, Deg(26), 1);
        Assert.Equal(40, Deg(31), 3);
    }

    // ---- the checks: game and look ----

    /// <summary>A clip that floats (its feet 60 cm above the game clip's, where the game's feet are down) with an ankle rolled far past
    /// its limit: the look checks say big differences, the game checks fail nothing, and it can be added to a package. A real game failure
    /// (a length not the clip's) is still refused.</summary>
    [Fact]
    public void AFloatingPoseIsALookDifferenceNotAGameFailure()
    {
        var game = Humanoid.Motion(S, 20);
        var floating = Humanoid.Motion(S, 20, (t, l) => Humanoid.Turn(S, l, "LeftFoot", Vector3.UnitX, 47), t => new Transform(new Vector3(0, 0, 0.6f), Quaternion.Identity));
        var r = new CheckReport().Add(MotionChecks.Length(20, 20)).Add(MotionChecks.FeetOffTheFloor(floating, game)).Add(MotionChecks.JointLimits(floating, game));
        Assert.Equal(CheckKind.Look, r.Results[1].Kind);
        Assert.Equal(CheckLevel.Fail, r.Results[1].Level);
        Assert.Equal("Big difference", r.Results[1].Said);
        Assert.Equal(CheckLevel.Warn, r.Results[1].Shown);
        Assert.NotEqual(CheckLevel.Pass, r.Results[2].Level);
        Assert.Equal(CheckLevel.Pass, r.Verdict);
        Assert.Null(PackageEntries.NotForPackage(r.Verdict));
        int big = r.Count(CheckLevel.Fail, CheckKind.Look), notes = r.Count(CheckLevel.Warn, CheckKind.Look);
        Assert.Equal($"Game: 1 pass, 0 warn, 0 fail. Look: {big} big difference{(big == 1 ? "" : "s")}, {notes} note{(notes == 1 ? "" : "s")}.", r.Headline);
        Assert.Contains("VERDICT: PASS (Game: 1 pass, 0 warn, 0 fail. Look:", r.ToText());
        Assert.Contains("Big difference  feet off the floor:", r.ToText());
        // the control: a game failure is still one, and is refused
        var bad = new CheckReport().Add(MotionChecks.Length(19, 20)).Add(MotionChecks.FeetOffTheFloor(floating, game));
        Assert.Equal(CheckLevel.Fail, bad.Verdict);
        Assert.Equal(PackageEntries.GameCheckFailed, PackageEntries.NotForPackage(bad.Verdict));
    }

    /// <summary>Every check by its kind: the look checks are the ones that compare the look with the game's clip; the rest are game checks.</summary>
    [Fact]
    public void EveryCheckHasItsKind()
    {
        foreach (var n in new[] { "feet in the floor", "feet off the floor", "joint limits", "speed", "the mover's turn", "twists", "pops", "limbs", "hands on a clip that aims them", "feet during the plants" })
            Assert.Equal(CheckKind.Look, CheckResult.KindOf(n));
        foreach (var n in new[] { "length", "read-back", "bone match", "fit", "tracks", "keying", "curve form", "foot sync", "key times", "motion not written", ClipImport.GraphCheckName, "animation graph" })
            Assert.Equal(CheckKind.Game, CheckResult.KindOf(n));
    }

    /// <summary>A report kept by 1.1.0 (look checks under FAIL) reads now by its game checks: PASS, with its big difference counted.</summary>
    [Fact]
    public void AnEarlierReportIsJudgedByItsGameChecks()
    {
        string text = "Import of x into y\nPASS  length: as long\nFAIL  feet off the floor: floats (worst at tick 3, Foot_L)\nWARN  joint limits: an ankle\nVERDICT: FAIL (1 pass, 1 warn, 1 fail)\n";
        var item = new Core.Import.PackageItem(new ClipEntry { Index = 5, Id = 9, Name = "y", Bytes = ReadOnlyMemory<byte>.Empty }, [], Core.Library.ClipChange.Mirror(), CheckLevel.Fail, text, DateTime.Now);
        Assert.Equal(CheckLevel.Pass, PackageEntries.GameVerdict(item));
        Assert.Equal(1, PackageEntries.BigDifferences(item));
        Assert.Equal(["Big difference", "Note", "PASS"], PackageEntries.Checks(item).Select(c => c.Said));
        Assert.Equal(["Big difference feet off the floor: floats (worst at tick 3, Foot_L)", "Note joint limits: an ankle"], PackageEntries.Warnings(item));
    }
}

/// <summary>Sets on the fake vanilla install: the standing idle set of fm_std_V0 read from the graph, and a set Replace from one long
/// source: each clip exactly its own length and its own piece, lined up, against a control not lined up; and the set's package.</summary>
[Collection("install")]
public sealed class SetGameTests(InstallFixture f)
{
    static readonly string[] StandingIdles = ["fm_std_V0_idle_cool_01", "fm_std_V0_idle_cool_04", "fm_std_V0_idle_cool_05", "fm_std_V0_idle_cool_06"];

    ClipSets Sets() => ClipSets.Read(ClipGraph.Read(f.Install), f.Install.ForgeClips.Select(c => (c.Id, c.Name, c.LastTick)));

    [GameDataFact]
    public void TheStandingIdleSetIsReadFromTheGraph()
    {
        var g = f.Install;
        var sets = Sets();
        var s = sets.Main(g.FindClip("fm_std_V0_idle_cool_01")!.Id)!;
        Assert.Equal(ClipSetKind.Random, s.Kind);
        Assert.Equal(StandingIdles, s.Clips.Select(id => g.FindClip(id)!.Name));
        Assert.Equal(0.3, s.BlendSeconds);
        // _02 isn't in it (a state of its own in the graph), though its name would put it there
        Assert.DoesNotContain(g.FindClip("fm_std_V0_idle_cool_02")!.Id, s.Clips);
        Assert.InRange(sets.StatesUnread, 1, sets.StatesRead / 100);
    }

    [GameDataFact]
    public void ASetFromOneLongSourceIsLinedUpEachClipItsOwnLengthAndPiece()
    {
        var g = f.Install;
        var slots = StandingIdles.Select(n => g.FindClip(n)!).ToList();
        var src = g.FindClip("mcm_ar_fm_std_wade_low_V0_01")!;
        var graph = ClipGraph.Read(g);
        SetReplaceResult Run(bool lineUp) => ClipSetReplace.Run(g, graph, slots, [SetSource.Of(src)],
            new SetReplaceOptions { Fit = new FitOptions { Mode = FitMode.Cut, Loop = true, LoopFrames = 30 }, LineUp = lineUp });
        var lined = Run(true);
        var plain = Run(false);
        for (int i = 0; i < slots.Count; i++)
        {
            var rec = lined.Results[i].Rebuilt.Record;
            Assert.Equal((slots[i].Id, slots[i].LastTick, slots[i].Record!.Duration), (rec.Id, rec.LastTick, rec.Duration));
            Assert.Equal(slots[i].Record!.Tracks, rec.Tracks);
            Assert.NotEqual(CheckLevel.Fail, lined.Results[i].Report.Verdict);
        }
        // each its own piece: the idles (2,532 ticks together) are longer than the source (2,194), so the pieces overlap, but by no more
        // than they must (338 ticks; each piece at its own best start would share 968), none inside another, their starts well apart, and
        // the notes say so
        Assert.True(slots.Sum(s => s.LastTick) > src.LastTick);
        var starts = lined.CutStarts.Select(s => s!.Value).ToList();
        var lasts = slots.Select(s => s.LastTick).ToList();
        for (int i = 0; i < starts.Count; i++)
        {
            Assert.InRange(starts[i], 0, src.LastTick - slots[i].LastTick);
            for (int j = i + 1; j < starts.Count; j++) Assert.True(Math.Abs(starts[i] - starts[j]) >= 60, $"starts {starts[i]} and {starts[j]} closer than 60 ticks");
        }
        Assert.Equal(lasts.Sum() - src.LastTick, ClipFit.SharedTicks(starts, lasts));
        Assert.False(ClipFit.AnyInside(starts, lasts));
        Assert.Contains(lined.Notes, n => n.Contains("its 4 pieces share 338 ticks of it (the least their lengths allow: 338)"));
        Assert.Equal(968, ClipFit.SharedTicks([1385, 1068, 222, 709], lasts));
        // the shortest three fit one after another: then none overlaps
        var three = ClipSetReplace.Run(g, graph, slots.Take(3).ToList(), [SetSource.Of(src)], new SetReplaceOptions { Fit = new FitOptions { Mode = FitMode.Cut }, LineUp = false });
        var pieces = Enumerable.Range(0, 3).Select(i => (Start: three.CutStarts[i]!.Value, End: three.CutStarts[i]!.Value + slots[i].LastTick)).OrderBy(p => p.Start).ToList();
        for (int i = 1; i < pieces.Count; i++) Assert.True(pieces[i - 1].End <= pieces[i].Start, $"pieces {pieces[i - 1]} and {pieces[i]} overlap");
        Assert.DoesNotContain(three.Notes, n => n.Contains("overlap"));
        var seams = ClipSetReplace.Seams(lined.Results);
        var controls = ClipSetReplace.Seams(plain.Results);
        Assert.All(seams, d => Assert.InRange(d, 0, 0.5));
        Assert.True(controls.Max() > 2, $"the control's seams {string.Join(", ", controls.Select(d => d.ToString("F2")))}: the line-up isn't shown to do anything");
    }

    /// <summary>The wade idle on the standing idle set, lined up with the loop: with the feet kept planted no planted foot moves more than
    /// 2 cm in any blend or switch (each clip's feet slide check OK); the control, each piece at its own best start, lined up on the
    /// source's frame 1385 without it, slides the
    /// right foot far at the end of _04 (a big difference), as seen in the game.</summary>
    [GameDataFact]
    public void TheWadeSetsFeetDontSlide()
    {
        var g = f.Install;
        var slots = StandingIdles.Select(n => g.FindClip(n)!).ToList();
        var src = SetSource.Of(g.FindClip("mcm_ar_fm_std_wade_low_V0_01")!);
        var fit = new FitOptions { Mode = FitMode.Cut, Loop = true, LoopFrames = 30 };
        var now = ClipSetReplace.Run(g, null, slots, [src], new SetReplaceOptions { Fit = fit });
        var apart = ClipSetReplace.Run(g, null, slots, [src], new SetReplaceOptions
        {
            Fit = fit with { KeepFeet = false }, Starts = [1385, 1068, 222, 709], PoseSource = src, PoseFrame = 1385,
        });
        CheckResult Slide(SetReplaceResult r, int i) => r.Results[i].Report.Results.Single(c => c.Check == Feet.SlideCheck);
        for (int i = 0; i < slots.Count; i++) Assert.Equal(CheckLevel.Pass, Slide(now, i).Level);
        // the legs bent to keep the feet never snap: no bone turns fast enough for the pops check to note it (a step landing out of the
        // leg's reach once snapped _06's right knee straight, 16.7 deg in a tick)
        // nor bend out of the knee's hinge; also on the same pieces without the line-up (where a loop's step once bent _04's knee 44 deg
        // sideways and turned its shin 53 deg in a tick)
        var noLine = ClipSetReplace.Run(g, null, slots, [src], new SetReplaceOptions { Fit = fit, LineUp = false, Starts = now.CutStarts.ToList() });
        foreach (var (r, how) in new[] { (now, "lined up"), (noLine, "not lined up") })
            for (int i = 0; i < slots.Count; i++)
                foreach (var check in new[] { "pops", "joint limits" })
                {
                    var c = r.Results[i].Report.Results.Single(x => x.Check == check);
                    Assert.True(c.Level == CheckLevel.Pass, $"{slots[i].Name}, {how}: {c.Message}");
                }
        Assert.Equal(CheckLevel.Fail, Slide(apart, 1).Level);
        Assert.Equal("RightFoot", Slide(apart, 1).Bone);
        Assert.Equal([1385, 1068, 222, 709], apart.CutStarts.Select(s => s!.Value));
        // a start out of its source is refused
        Assert.Throws<ImportRefusedException>(() => ClipSetReplace.Run(g, null, slots, [src], new SetReplaceOptions { Fit = fit, Starts = [null, 2000, null, null] }));
    }

    /// <summary>Moving sources against their control, the same pieces with the feet left to the blend: a walk on walks (no line-up) and
    /// turns on the spot on standing idles (lined up). Keeping the feet adds no frame with both feet off the ground (a step once came
    /// during the other foot's own swing, its step refused), and no pops or joint limits note the control doesn't have (a turned step once
    /// bent the knee out of its hinge and turned the foot 25 deg in a tick).</summary>
    [GameDataFact]
    public void MovingSourcesKeepTheFeetNoWorseThanTheBlend()
    {
        var g = f.Install;
        var runs = new (string[] Clips, string Source, bool LineUp, int Blend)[]
        {
            (["fm_std_V2_fast_cool_cycle", "fm_std_HG_V2_fast_ale_cycle", "fm_std_V3_N_cycle_ale"], "fm_std_V1_cool_fast_cycle_01", false, 30),
            (["fm_std_V0_idle_cool_01", "fm_std_V0_ale_idle_neutral_01", "fm_std_HG_V0_ale_idle_neutral_01"], "fm_std_cool_V0_TurnOnSpot_90", true, 30),
            (["fm_std_V0_idle_cool_01", "fm_std_HG_V0_idle_cool_01", "fm_std_V0_ale_idle_neutral_02"], "fm_std_turn_on_spot_circle_cool", true, 30),
            // a blend shortened: a step that quick once popped the knee 34 deg in a tick
            (StandingIdles, "mcm_ar_fm_std_wade_low_V0_01", true, 6),
            (StandingIdles, "mcm_ar_fm_std_wade_low_V0_01", true, 10),
        };
        // frames where every foot (its ankle or toes, whichever is lower) stands over 3 cm above its own lowest in the clip
        static int BothUp(Motion m)
        {
            var legs = Feet.Legs(m.Skeleton);
            float Foot(int t, Feet.Leg l) => Math.Min(m.World(t, l.Ankle).Position.Z, m.World(t, l.Toe).Position.Z);
            var low = legs.Select(l => Enumerable.Range(0, m.Frames).Min(t => Foot(t, l))).ToArray();
            return Enumerable.Range(0, m.Frames).Count(t => legs.Select((l, i) => Foot(t, l) - low[i]).All(z => z > 0.03f));
        }
        foreach (var (clips, source, lineUp, blend) in runs)
        {
            var slots = clips.Select(n => g.FindClip(n)!).ToList();
            var src = SetSource.Of(g.FindClip(source)!);
            var fit = new FitOptions { Mode = FitMode.Cut, Loop = true, LoopFrames = blend };
            var on = ClipSetReplace.Run(g, null, slots, [src], new SetReplaceOptions { Fit = fit, LineUp = lineUp });
            var off = ClipSetReplace.Run(g, null, slots, [src], new SetReplaceOptions { Fit = fit with { KeepFeet = false }, LineUp = lineUp, Starts = on.CutStarts.ToList() });
            for (int i = 0; i < slots.Count; i++)
            {
                string what = $"{slots[i].Name} from {source}";
                Assert.True(BothUp(on.Results[i].Imported) <= BothUp(off.Results[i].Imported), $"{what}: {on.Results[i].Report.Results.Single(x => x.Check == "fit").Message}");
                foreach (var check in new[] { "pops", "joint limits" })
                {
                    var a = on.Results[i].Report.Results.Single(x => x.Check == check);
                    var b = off.Results[i].Report.Results.Single(x => x.Check == check);
                    Assert.True(a.Level <= b.Level, $"{what}, {check}: {a.Message} (left to the blend: {b.Message})");
                }
            }
        }
    }

    /// <summary>A walk cycle on other walk cycles, looped, without the line-up: no step of the walk's stride (once 84 cm in 6 ticks, the
    /// other foot in the air, a pop noted) is made, none over the longest step the tool makes; every clip's pops and joint limits stay OK.
    /// The control: the same clips with the feet left to the blend are OK too.</summary>
    [GameDataFact]
    public void AWalkCycleOnWalkCyclesTakesNoStrideAsAStep()
    {
        var g = f.Install;
        var slots = new[] { "fm_std_V2_fast_N_cycle_ale", "fm_std_V2_slow_N_cycle_ale", "fm_std_V2_fast_cool_cycle" }.Select(n => g.FindClip(n)!).ToList();
        var src = SetSource.Of(g.FindClip("fm_std_V1_fast_N_cycle_ale")!);
        var fit = new FitOptions { Mode = FitMode.Cut, Loop = true, LoopFrames = 30 };
        foreach (bool keep in new[] { true, false })
        {
            var r = ClipSetReplace.Run(g, null, slots, [src], new SetReplaceOptions { Fit = fit with { KeepFeet = keep }, LineUp = false });
            for (int i = 0; i < slots.Count; i++)
            {
                var report = r.Results[i].Report.Results;
                foreach (var check in new[] { "pops", "joint limits" })
                {
                    var c = report.Single(x => x.Check == check);
                    Assert.True(c.Level == CheckLevel.Pass, $"{slots[i].Name}, keep feet {keep}: {c.Message}");
                }
                foreach (System.Text.RegularExpressions.Match mt in System.Text.RegularExpressions.Regex.Matches(report.Single(x => x.Check == "fit").Message, @"steps ([0-9.]+) cm"))
                    Assert.True(double.Parse(mt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) <= Feet.MaxStepCm, $"{slots[i].Name}: {mt.Value}");
            }
        }
    }

    /// <summary>The set's package: every clip a 1_-_ file of its own, and its original in the originals folder.</summary>
    [GameDataFact]
    public void TheSetsPackageHoldsEveryClip()
    {
        var g = f.Install;
        var slots = StandingIdles.Select(n => g.FindClip(n)!).ToList();
        var r = ClipSetReplace.Run(g, null, slots, [SetSource.Of(g.FindClip("mcm_ar_fm_std_wade_low_V0_01")!)], new SetReplaceOptions { Fit = new FitOptions { Mode = FitMode.Cut } });
        var items = r.Results.Select(x => new PackageItem(x.Slot, x.Record, Core.Library.ClipChange.Imported("wade"), x.Report.Verdict, x.ReportText(), DateTime.Now)).ToList();
        string dir = Path.Combine(AppContext.BaseDirectory, "setpkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var plan = PackageBuild.Plan(g, items, "Wade idles", dir);
            Assert.Null(plan.Problem);
            PackageBuild.Build(g, plan, items, replace: false);
            string folder = Path.Combine(dir, PackageBuild.FolderNameFor("Wade idles"));
            foreach (var s in slots)
            {
                Assert.True(File.Exists(Path.Combine(folder, Core.Records.RecordFile.ClipFileName(PackageBuild.PackageFileIndex, s.Name, s.Id))), s.Name);
                Assert.Single(Directory.GetFiles(Path.Combine(folder, PackageBuild.OriginalsFolderName), $"*_-_{s.Name}.Animation"));
            }
            Assert.Equal(slots.Count, Directory.GetFiles(folder, "1_-_*.Animation").Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
