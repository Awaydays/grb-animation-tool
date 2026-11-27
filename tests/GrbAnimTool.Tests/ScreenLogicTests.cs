using System.Numerics;
using System.Text.RegularExpressions;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Writing;
using static GrbAnimTool.Tests.EditSynthetic;

namespace GrbAnimTool.Tests;

/// <summary>The Edit and Package screens' logic that needs no window: the trim handles, the swap sources, the clip's marks, the edits'
/// availability and their results' checks, the package's items, plan and build, and who plays a clip.</summary>
public sealed class ScreenLogicTests : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "screens-" + Guid.NewGuid().ToString("N"));

    public ScreenLogicTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { } }

    static readonly uint E4 = EditTracks.FirstPlantSlot, E5 = EditTracks.FirstPlantSlot + 1;
    const ulong Id = 0x0000012300000001;

    /// <summary>A clip that travels 1.2 m forward over 60 ticks, bends its spine, and plants the left foot from tick 20 to 30 and the
    /// right from 0 to 60.</summary>
    static byte[] Walk(string name = "walk", ulong id = Id, int last = 60) => Clip(name, id, last,
    [
        (0u, CurveWriter.PosCurve([0, last], [Vector3.Zero, new(0, 1.2f, 0)])),
        (H("Spine"), CurveWriter.RotCurve([0, last / 2, last], [Quaternion.Identity, RotX(30), RotX(60)])),
        (E4, CurveWriter.PlantCurve([new(0, 0, Vector3.Zero), new(20, 10, new(0.1f, 0.3f, 0))])),
        (E5, CurveWriter.PlantCurve([new(0, last, new(-0.1f, 0.3f, 0))])),
    ], stepTimes: [40, 80]);

    static ClipEntry Entry(byte[] rec, int index = 7)
    {
        var c = ClipRecord.Parse(rec);
        return new ClipEntry { Index = index, Id = c.Id, Name = c.Name, Bytes = rec, Record = c, Status = ClipStatus.Vanilla };
    }

    // ---------------------------------------------------------------------------------------------------------------- the trim handles

    [Fact]
    public void TrimHandlesKeepACutATrimCanMake()
    {
        var h = TrimHandles.Whole(120);
        Assert.Equal((0, 120), (h.From, h.To));
        Assert.False(h.Cuts);
        // a place on a strip 600 wide: 5 per tick
        Assert.Equal(20, TrimHandles.TickAt(100, 600, 120));
        Assert.Equal(21, TrimHandles.TickAt(102.5, 600, 120));   // halves round up
        Assert.Equal(0, TrimHandles.TickAt(-40, 600, 120));
        Assert.Equal(120, TrimHandles.TickAt(9000, 600, 120));
        Assert.Equal(0, TrimHandles.TickAt(50, 0, 120));
        Assert.Equal(300, TrimHandles.XOf(60, 600, 120));
        h = h.DragFrom(100, 600).DragTo(500, 600);
        Assert.Equal((20, 100, 80), (h.From, h.To, h.Kept));
        Assert.True(h.Cuts);
        // the start never passes the end, nor the end the start: a tick stays between them
        Assert.Equal((99, 100), (h.DragFrom(590, 600).From, h.DragFrom(590, 600).To));
        Assert.Equal((20, 21), (h.DragTo(0, 600).From, h.DragTo(0, 600).To));
        // set by ticks: clamped into the clip, a tick apart
        Assert.Equal((0, 1), (TrimHandles.At(120, -5, -9).From, TrimHandles.At(120, -5, -9).To));
        Assert.Equal((119, 120), (TrimHandles.At(120, 500, 600).From, TrimHandles.At(120, 500, 600).To));
        Assert.Equal((10, 40), (TrimHandles.At(120, 10, 40).From, TrimHandles.At(120, 10, 40).To));
        Assert.Contains("80 ticks", h.Describe());
        // every cut the handles allow, a trim's warp takes
        foreach (var x in new[] { 0.0, 1, 299, 300, 599, 600 })
        {
            var d = TrimHandles.Whole(120).DragFrom(x, 600);
            Core.Retiming.TimeWarp.Trim(120, d.From, d.To);
            var e = TrimHandles.Whole(120).DragTo(x, 600);
            Core.Retiming.TimeWarp.Trim(120, e.From, e.To);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- swap sources

    [Fact]
    public void SwapSourcesAreTheClipsOfOneLengthAndTrackList()
    {
        var a = Entry(Walk("a", 0x10), 1);
        var b = Entry(Walk("b", 0x11), 2);
        var shorter = Entry(Walk("shorter", 0x12, last: 50), 3);
        // the same hashes, but the spine's track a position: another kind
        var otherKind = Entry(Clip("kind", 0x13, 60,
        [
            (0u, CurveWriter.PosCurve([0, 60], [Vector3.Zero, new(0, 1.2f, 0)])),
            (H("Spine"), CurveWriter.PosCurve([0, 60], [Vector3.Zero, Vector3.One])),
            (E4, CurveWriter.PlantCurve([new(0, 0, Vector3.Zero)])),
            (E5, CurveWriter.PlantCurve([new(0, 60, Vector3.Zero)])),
        ]), 4);
        var c = Entry(Walk("c", 0x14), 5);
        var unread = new ClipEntry { Index = 6, Id = 0x15, Name = "unread", Bytes = new byte[4], Record = null };
        var table = Table();
        var s = SwapSources.Build([c, a, shorter, otherKind, b, unread], table);
        Assert.Equal(new[] { "b", "c" }, s.For(a).Select(x => x.Name));
        Assert.Equal(new[] { "a", "c" }, s.For(b).Select(x => x.Name));
        Assert.Empty(s.For(shorter));
        Assert.Empty(s.For(otherKind));
        Assert.Empty(s.For(unread));
        Assert.Equal(3, s.Swappable);
        // every source offered passes the swap's own test, and the ones left out don't
        foreach (var src in s.For(a)) Assert.Null(SwapEdit.Mismatch(a.Record!, src.Record!, table));
        Assert.NotNull(SwapEdit.Mismatch(a.Record!, shorter.Record!, table));
        Assert.NotNull(SwapEdit.Mismatch(a.Record!, otherKind.Record!, table));
    }

    // ---------------------------------------------------------------------------------------------------------------- the clip's marks

    [Fact]
    public void PlantsAndTheFloor()
    {
        var c = ClipRecord.Parse(Walk());
        var p = ClipMarks.Plants(c, Table());
        Assert.Equal(new[] { new PlantSpan(1, 1, 0, 60), new PlantSpan(0, 0, 20, 30) }, p);
        Assert.True(ClipMarks.Planted(p, 0, 25));
        Assert.False(ClipMarks.Planted(p, 0, 31));
        Assert.True(ClipMarks.Planted(p, 1, 60));
        Assert.Equal(new[] { (1, 0, 60), (0, 20, 10) }, ClipMarks.ForChecks(p));
        Assert.False(ClipMarks.BelowFloor(-0.019f));
        Assert.True(ClipMarks.BelowFloor(-0.021f));
        Assert.Equal(50, ClipMarks.X(0.5, 1, 100));
        Assert.Equal(100, ClipMarks.X(3, 1, 100));
        Assert.Equal(0, ClipMarks.X(1, 0, 100));
        // the step list's two times (1/120 s) as step events
        var ev = ClipMarks.Events(c);
        Assert.Equal(new[] { 40 / 120.0, 80 / 120.0 }, ev.Select(x => x.Seconds));
        Assert.All(ev, x => Assert.Equal(Core.Export.EventObjects.StepEvent, x.Class));
        Assert.StartsWith("step event", ev[0].Describe());
        Assert.Contains("sound event, class A85DEF18, type 3", new EventMark(0.5, 0, 1, 0xA85DEF18, 3).Describe());
    }

    // ---------------------------------------------------------------------------------------------------------------- the edits

    [Fact]
    public void UnavailableEditsSayWhy()
    {
        var e = Entry(Walk());
        var s = Body();
        // no graph: every edit that depends on it says so
        var none = new EditContext(s, Table(), null);
        Assert.Contains("graph wasn't read", EditJobs.PlanReason(EditKind.Mirror, e, none, 0));
        Assert.Contains("graph wasn't read", EditJobs.PlanReason(EditKind.Trim, e, none, 0));
        // a motion field: no mirror or scale; a length held: no trim or speed
        var mf = Ctx(s, MotionField(Id));
        Assert.Equal(MotionField(Id).MotionRefusal, EditJobs.PlanReason(EditKind.Mirror, e, mf, 0));
        Assert.Equal(MotionField(Id).MotionRefusal, EditJobs.PlanReason(EditKind.Scale, e, mf, 0));
        var held = Ctx(s, LengthHeld(Id));
        Assert.Contains("length is held", EditJobs.PlanReason(EditKind.Speed, e, held, 0));
        Assert.Contains("length is held", EditJobs.PlanReason(EditKind.Trim, e, held, 0));
        Assert.Null(EditJobs.PlanReason(EditKind.Mirror, e, held, 0));
        // a swap needs a source
        Assert.Contains("identical track list", EditJobs.PlanReason(EditKind.Swap, e, Ctx(s, Free(Id)), 0));
        Assert.Null(EditJobs.PlanReason(EditKind.Swap, e, Ctx(s, Free(Id)), 2));
        // an armed clip: no mirror
        var armed = Entry(Clip("armed", Id, 60, [(H("Spine"), CurveWriter.RotCurve([0, 60], [Quaternion.Identity, RotX(20)])), (0x58A97CAE, CurveWriter.RotCurve([0], [Quaternion.Identity]))]));
        Assert.Contains("hand tracks (58A97CAE)", EditJobs.PlanReason(EditKind.Mirror, armed, Ctx(s, Free(Id)), 0));
        var armedMirror = EditJobs.Options(armed, Ctx(s, Free(Id)), null, 0, trial: false).Single(x => x.Kind == EditKind.Mirror);
        Assert.Equal("Can't mirror: this clip aims the hands at the weapon, and a mirror would leave them reaching to the old side.", armedMirror.Unavailable);
        Assert.Contains("58A97CAE", armedMirror.Detail);
        // each refusal the plan finds carries its kind: the graph's, the clip's own, the swap's
        Assert.Equal(EditRefusal.GraphIncomplete, EditJobs.PlanBlock(EditKind.Mirror, e, none, 0)!.Kind);
        Assert.Equal(EditRefusal.GraphIncomplete, EditJobs.PlanBlock(EditKind.Speed, e, none, 0)!.Kind);
        Assert.Equal(EditRefusal.MotionField, EditJobs.PlanBlock(EditKind.Scale, e, mf, 0)!.Kind);
        Assert.Equal(EditRefusal.LengthHeld, EditJobs.PlanBlock(EditKind.Trim, e, held, 0)!.Kind);
        Assert.Equal(EditRefusal.NoSwapSource, EditJobs.PlanBlock(EditKind.Swap, e, Ctx(s, Free(Id)), 0)!.Kind);
        Assert.StartsWith("Can't change the length yet: this clip has timing data", EditReasons.Plain(EditKind.Speed, EditRefusal.TimesUnread));
        // a clip that doesn't travel: the scale's trial run is refused, and its reason given without the clip's name
        var still = Entry(Clip("still", Id, 60, [(0u, CurveWriter.PosCurve([0], [Vector3.Zero])), (H("Spine"), CurveWriter.RotCurve([0, 60], [Quaternion.Identity, RotX(20)]))]));
        var o = EditJobs.Options(still, Ctx(s, Free(Id)), null, 0);
        var scale = o.Single(x => x.Kind == EditKind.Scale);
        Assert.False(scale.Available);
        // in a modder's words, the technical detail apart
        Assert.Equal("Can't scale the root motion: this clip doesn't travel, so there is no travel to scale.", scale.Unavailable);
        Assert.Equal(EditRefusal.NoRootMotion, scale.Refusal);
        Assert.DoesNotContain("still:", scale.Detail);
        Assert.Contains("travels", scale.Detail);
        Assert.False(o.Single(x => x.Kind == EditKind.Swap).Available);
        Assert.Equal("its mover travels 0", EditJobs.Unnamed("still", "still: its mover travels 0"));
        // a choice superseded: the trial edits stop between edits
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => EditJobs.Options(still, Ctx(s, Free(Id)), null, 0, trial: true, cts.Token));
        // a swap with the graph unread warns as an import does; a clip whose own curves don't resolve says so
        Assert.Equal(EditJobs.SwapGraphUnknownWarning, EditJobs.WarningOf(EditKind.Swap, none));
        Assert.Null(EditJobs.WarningOf(EditKind.Swap, Ctx(s, Free(Id))));
        var unresolved = Entry(Clip("refd", Id, 60, [(H("LeftArm"), Ref(1))]));
        Assert.Contains("don't all resolve", EditJobs.PlanReason(EditKind.Swap, unresolved, Ctx(s, Free(Id)), 0));
    }

    /// <summary>A fault of the edits' own code on a clip (here a graph verdict with no list of uses, which every edit but swap trips
    /// over) is each edit's "can't tell", never an exception out of the choices; a clip chosen after another stops the first one's trial
    /// runs.</summary>
    [Fact]
    public void AClipTheEditsFailOnIsCantTellAndAChoiceStopsWhenCancelled()
    {
        var e = Entry(Walk());
        var broken = new EditContext(Body(), Table(), new ClipGraphVerdict { ClipId = Id, Uses = null!, GraphComplete = true, Scanned = true });
        var c = EditJobs.Choices(e, broken, null, 0, CancellationToken.None);
        foreach (var k in new[] { EditKind.Mirror, EditKind.Scale, EditKind.Speed, EditKind.Trim })
        {
            Assert.False(c[k].Available, $"{k}");
            Assert.Null(c[k].Refusal);
            Assert.StartsWith("Can't tell whether this clip takes", c[k].Unavailable);
            Assert.Contains("failed on this clip", c[k].Detail);
        }
        Assert.False(c.Any);
        var failed = InstallEditChoices.Failed(new NullReferenceException("a record of an unforeseen shape"));
        Assert.All(failed.Options, o => Assert.StartsWith("Can't tell", o.Unavailable));
        Assert.Equal(1, InstallEditChoices.From(new Dictionary<ulong, ClipEditChoices> { [1] = failed }).Faults);
        // another clip chosen: the speeds, scales and cuts being tried stop
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => EditJobs.Choices(e, Ctx(Body(), Free(Id)), null, 0, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => EditJobs.Speeds(e, Ctx(Body(), Free(Id)), cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => EditJobs.Trims(e, Ctx(Body(), Free(Id)), cts.Token));
    }

    /// <summary>Every kind of refusal, on every edit, reads as one sentence in a modder's words: the edit's lead, what would go wrong, and
    /// none of the record format's terms (the technical detail goes apart, on hover).</summary>
    [Fact]
    public void EveryRefusalReadsInAModdersWords()
    {
        string[] format = ["arena", "tag ", "0x", "hash", "curve", "track", "form ", "byte array", "head", "record", "graph node", "segment"];
        foreach (var edit in Enum.GetValues<EditKind>())
            foreach (var kind in Enum.GetValues<EditRefusal>().Cast<EditRefusal?>().Append(null))
            {
                string s = EditReasons.Plain(edit, kind);
                Assert.StartsWith(kind is null ? "Can't tell whether this clip takes " : kind == EditRefusal.TimesUnread && edit is EditKind.Speed or EditKind.Trim ? "Can't change the length yet: "
                    : EditReasons.Lead(edit) + ": ", s);
                Assert.EndsWith(".", s);
                Assert.DoesNotContain("..", s);
                foreach (var w in format) Assert.False(s.Contains(w, StringComparison.OrdinalIgnoreCase), $"{edit} {kind}: '{s}' holds the format's word '{w}'");
            }
        // the kinds that differ say different things
        var mirror = Enum.GetValues<EditRefusal>().Select(k => EditReasons.Plain(EditKind.Mirror, k)).ToList();
        Assert.True(mirror.Distinct().Count() >= mirror.Count - 3, string.Join("\n", mirror));
        // a refusal of the edit itself keeps its kind and its message, the clip's name taken off, as the detail
        var b = EditReasons.Of("hello", new EditRefusedException(EditRefusal.ArenaKindMissing, "hello: the new curves need arena kind 3"));
        Assert.Equal(new EditBlock(EditRefusal.ArenaKindMissing, "the new curves need arena kind 3"), b);
    }

    [Fact]
    public void AnEditsResultIsCheckedAndKeptForThePackage()
    {
        var e = Entry(Walk());
        var ctx = Ctx(Body(), Free(Id));
        var o = EditJobs.Apply(e, new EditRequest(EditKind.Speed, 2), ctx, null);
        Assert.Equal(30, o.Edited.LastTick);
        Assert.Equal(60, o.Original.LastTick);
        // the ghost plays the original's tick the result's tick comes from
        Assert.Equal(0, o.OriginalTick(0));
        Assert.Equal(20, o.OriginalTick(10));
        Assert.Equal(60, o.OriginalTick(30));
        Assert.Equal(60, o.OriginalTick(99));
        Assert.Contains(o.Report.Results, r => r.Check == "feet in the floor");
        Assert.Contains(o.Report.Results, r => r.Check == "animation graph" && r.Level == CheckLevel.Pass);
        var item = PackageEntries.ForEdit(o);
        Assert.Equal(PackageItemKind.Edit, PackageEntries.Kind(item));
        Assert.Equal("Speed 2x", PackageEntries.What(item));
        Assert.Equal(ClipChange.Speed(2), item.Change);
        Assert.Equal($"Edit of {e.Index}_-_{e.Name} 0x{e.Id:X16} (Speed 2x): {e.LastTick} ticks before, {o.Result.Record.LastTick} after; the record {o.Result.Bytes.Length} B",
            item.ReportText.Split('\n')[0].TrimEnd('\r'));
        Assert.Equal(o.Result.Bytes, item.Record);
        Assert.Equal(o.Report.Verdict, item.Verdict);
        Assert.Equal(o.Report.Results.Count(r => r.Level != CheckLevel.Pass), PackageEntries.Warnings(item).Count);
        Assert.Contains("What the edit did and kept:", item.ReportText);

        var t = EditJobs.Apply(e, new EditRequest(EditKind.Trim, 1, 10, 40), ctx, null);
        Assert.Equal(30, t.Edited.LastTick);
        Assert.Equal(10, t.OriginalTick(0));
        Assert.Equal(40, t.OriginalTick(30));
        // the trim from tick 10 moves the result's mover back to the clip's start; the ghost is moved with it, so the two stand together
        Assert.True((t.Original.Mover(10).Position - t.Edited.Mover(0).Position).Length() > 0.1f);
        foreach (int k in new[] { 0, 15, 30 })
            Assert.True((t.GhostMover(k).Position - t.Edited.Mover(k).Position).Length() < 2e-3f, $"tick {k}: {t.GhostMover(k).Position} against {t.Edited.Mover(k).Position}");
        // an edit that keeps tick 0 where it was leaves the ghost's mover as the original's
        Assert.Equal(o.Original.Mover(20), o.GhostMover(10));
        var m = EditJobs.Apply(e, new EditRequest(EditKind.Scale, 0.5), ctx, null);
        Assert.Contains(m.Report.Results, r => r.Check == "feet during the plants");
        Assert.Equal(17, m.OriginalTick(17));
    }

    [Fact]
    public void ImportItemsAndWarningsReadFromTheReport()
    {
        var e = Entry(Walk());
        var report = new CheckReport().Add(new CheckResult("length", CheckLevel.Pass, "fine"))
            .Add(new CheckResult("speed", CheckLevel.Warn, "a bit fast", 12, "Spine")).Add(new CheckResult("pops", CheckLevel.Fail, "a jump"));
        var item = new PackageItem(e, e.Bytes.ToArray(), ClipChange.Imported(Path.Combine("anims", "walk_new.glb")), report.Verdict, report.ToText("Import of walk_new.glb"), DateTime.Now);
        Assert.Equal(PackageItemKind.Import, PackageEntries.Kind(item));
        // the file's name only, never its folder
        Assert.Equal("Imported: walk_new.glb", PackageEntries.What(item));
        // speed and pops are look checks: a note and a big difference, never a WARN or FAIL, and the verdict the game checks' alone
        Assert.Equal(new[] { "Note speed: a bit fast (worst at tick 12, Spine)", "Big difference pops: a jump" }, PackageEntries.Warnings(item));
        Assert.Equal(CheckLevel.Pass, report.Verdict);
    }

    // ---------------------------------------------------------------------------------------------------------------- the package

    static byte[] Named(string name, ulong id) => Walk(name, id);

    [Fact]
    public void ThePlanNamesEveryFileAndRefusesTheGameFolders()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        (int, string, ulong)[] clips = [(102, "walk", Id), (7, "other", 0x99)];
        var plan = PackageBuild.Plan(clips, "My pack", Path.Combine(_dir, "out"), [game]);
        Assert.Null(plan.Problem);
        Assert.Equal(Path.Combine(_dir, "out", "My pack"), plan.OutputFolder);
        Assert.Equal(new[] { "1_-_walk.Animation", "1_-_other.Animation", "Original clips" + Path.DirectorySeparatorChar, Path.Combine("Original clips", "102_-_walk.Animation"),
            Path.Combine("Original clips", "7_-_other.Animation"), PackageBuild.MarkerName }, plan.Files.Select(f => f.Name));
        Assert.Equal("Original clips", PackageBuild.OriginalsFolderName);
        Assert.Equal("My pack", plan.PackageName);
        Assert.Contains("game's folders", PackageBuild.Plan(clips, "My pack", Path.Combine(game, "sub"), [game]).Problem);
        Assert.Contains("game's folders", PackageBuild.Plan(clips, "My pack", game, [game]).Problem);
        // the package's own folder is guarded too: a save folder above a game folder, with the package named like it
        var above = PackageBuild.Plan(clips, "game", _dir, [game]).Problem;
        Assert.Contains(Path.Combine(_dir, "game"), above);
        Assert.Contains("a folder the tool only reads", above);
        Assert.Contains("no clip", PackageBuild.Plan([], "My pack", _dir, [game]).Problem);
        Assert.Contains("name", PackageBuild.Plan(clips, " .. ", _dir, [game]).Problem);
        Assert.Contains("save folder", PackageBuild.Plan(clips, "x", "", [game]).Problem);
        Assert.Contains("twice", PackageBuild.Plan([(1, "a", 1), (1, "a", 1)], "x", _dir, [game]).Problem);
        Assert.Equal("a_b_c", PackageBuild.FolderNameFor(" a/b:c. "));
        Assert.Equal("_CON", PackageBuild.FolderNameFor("CON"));
    }

    PackageBuilt BuildOne(string game, string name, bool replace, Func<string, RecordPackage>? write = null, Func<string, RecordPackage>? writeOriginals = null)
    {
        var plan = PackageBuild.Plan([(102, "walk", Id)], name, Path.Combine(_dir, "out"), [game]);
        return PackageBuild.Build(plan, write ?? Writer(game, "walk"), writeOriginals ?? Writer(game, "walk-original"), replace, [game]);
    }

    /// <summary>A writer of record 102, named walk, whose record is <paramref name="content"/>'s walk.</summary>
    static Func<string, RecordPackage> Writer(string game, string content) =>
        f => RecordFileWriter.Write(f, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [new RecordFileItem(102, "walk", Id, Content(content))]);

    /// <summary>A walk record of the name walk, its curves differing by <paramref name="content"/>.</summary>
    static byte[] Content(string content) => Walk("walk", Id, content == "walk" ? 60 : 50);

    static readonly Func<string, RecordPackage> MustNotWrite = _ => throw new InvalidOperationException("must not write");

    [Fact]
    public void TheBuildWritesEveryFileOrNone()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        string folder = Path.Combine(_dir, "out", "pack");
        var built = BuildOne(game, "pack", replace: false);
        Assert.Equal(folder, built.Folder);
        // one folder: the record file, the originals folder holding the same file name, and the marker; nothing else, no text
        Assert.Equal(new[] { ".grbanimtool-package", "102_-_walk.Animation", "Original clips" }, Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        string originals = Path.Combine(folder, "Original clips");
        Assert.Equal(new[] { "102_-_walk.Animation" }, Directory.GetFileSystemEntries(originals).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(originals, PackageBuild.MarkerName)));
        Assert.Equal(RecordFile.Content(Content("walk-original")), File.ReadAllBytes(Path.Combine(originals, "102_-_walk.Animation")));
        Assert.Equal(RecordFile.Content(Content("walk")), File.ReadAllBytes(Path.Combine(folder, "102_-_walk.Animation")));
        Assert.Equal(new[] { "pack" }, Directory.GetFileSystemEntries(Path.Combine(_dir, "out")).Select(Path.GetFileName));
        Assert.Equal(new[] { "102_-_walk.Animation", Path.Combine("Original clips", "102_-_walk.Animation"), PackageBuild.MarkerName }, built.Files.Select(f => f.FileName));
        Assert.All(built.Files, f => Assert.Equal(new FileInfo(f.Path).Length, f.Length));
        Assert.All(built.Files, f => Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f.Path))), f.Sha256.ToLowerInvariant()));
        Assert.Null(built.EarlierLeftAt);
        // the marker: the tool's line, the package's name and the build time
        var marker = File.ReadAllLines(Path.Combine(folder, PackageBuild.MarkerName));
        Assert.Equal(PackageBuild.MarkerHead, marker[0]);
        Assert.Equal("name: pack", marker[1]);
        Assert.StartsWith("built: 20", marker[2]);
        Assert.True(PackageBuild.IsEarlierBuild(folder));
        // the record writer wrote into the build's own staging folder alone: the package in place, and its originals folder, are refused
        // to it, as to every writer
        var item = new RecordFileItem(102, "walk", Id, Named("walk", Id));
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
        foreach (var into in new[] { folder, originals, Path.Combine(originals, "deeper") })
            Assert.Contains("Pick a folder outside your packages", Assert.Throws<RecordFileException>(() =>
                RecordFileWriter.Write(into, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [item], overwrite: true)).Message);
        Assert.Equal(files, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList());
        // again: refused unless asked, then replaced whole, its originals folder too
        Assert.Contains("exists already", Assert.Throws<RecordFileException>(() => BuildOne(game, "pack", replace: false)).Message);
        File.WriteAllText(Path.Combine(folder, "102_-_walk.Animation"), "old");
        File.WriteAllText(Path.Combine(originals, "102_-_walk.Animation"), "old");
        // an earlier version's package, with its texts, is replaced as a package's files
        File.WriteAllText(Path.Combine(folder, "install-note.txt"), "an earlier version's note");
        File.WriteAllText(Path.Combine(folder, "README.txt"), "an earlier version's readme");
        BuildOne(game, "pack", replace: true);
        Assert.NotEqual("old", File.ReadAllText(Path.Combine(folder, "102_-_walk.Animation")));
        Assert.NotEqual("old", File.ReadAllText(Path.Combine(originals, "102_-_walk.Animation")));
        Assert.Equal(new[] { ".grbanimtool-package", "102_-_walk.Animation", "Original clips" }, Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "pack" }, Directory.GetFileSystemEntries(Path.Combine(_dir, "out")).Select(Path.GetFileName));
        // a folder holding anything else isn't replaced, in its originals folder neither
        File.WriteAllText(Path.Combine(folder, "mine.docx"), "keep me");
        Assert.Contains("mine.docx", Assert.Throws<RecordFileException>(() => BuildOne(game, "pack", replace: true)).Message);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(folder, "mine.docx")));
        File.Delete(Path.Combine(folder, "mine.docx"));
        File.WriteAllText(Path.Combine(originals, "mine.docx"), "keep me");
        Assert.Contains("Original clips", Assert.Throws<RecordFileException>(() => BuildOne(game, "pack", replace: true)).Message);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(originals, "mine.docx")));
        File.Delete(Path.Combine(originals, "mine.docx"));
        File.WriteAllText(Path.Combine(folder, "mine.docx"), "keep me");
        // nor a folder of record files that isn't a build of this tool (no marker, or another file of that name)
        string other = Path.Combine(_dir, "out", "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "102_-_walk.Animation"), "someone's");
        File.WriteAllText(Path.Combine(other, "README.txt"), "someone's readme");
        Assert.Contains("isn't a package this tool built", Assert.Throws<RecordFileException>(() => BuildOne(game, "other", replace: true)).Message);
        File.WriteAllText(Path.Combine(other, PackageBuild.MarkerName), "not this tool's");
        Assert.False(PackageBuild.IsEarlierBuild(other));
        Assert.Contains("isn't a package this tool built", Assert.Throws<RecordFileException>(() => BuildOne(game, "other", replace: true)).Message);
        Assert.Equal("someone's", File.ReadAllText(Path.Combine(other, "102_-_walk.Animation")));
        Directory.Delete(other, true);
        // a writer that fails leaves nothing behind, and the earlier build stays: the record files' writer, or the originals' after the
        // record files were written
        File.Delete(Path.Combine(folder, "mine.docx"));
        string Tree(string dir) => string.Join("\n", Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(e => Path.GetRelativePath(dir, e) + (File.Exists(e) ? " " + Convert.ToHexString(File.ReadAllBytes(e)) : "")));
        string before = Tree(folder);
        Assert.Throws<RecordFileException>(() => BuildOne(game, "pack", replace: true, _ => throw new RecordFileException("disk full")));
        Assert.Equal(before, Tree(folder));
        Assert.Contains("Nothing was written.", Assert.Throws<RecordFileException>(() => BuildOne(game, "pack", replace: true, writeOriginals: _ => throw new RecordFileException("disk full"))).Message);
        Assert.Equal(before, Tree(folder));
        Assert.Equal(new[] { "pack" }, Directory.GetFileSystemEntries(Path.Combine(_dir, "out")).Select(Path.GetFileName));
        Assert.Throws<RecordFileException>(() => BuildOne(game, "fresh", replace: false, _ => throw new RecordFileException("disk full")));
        Assert.Throws<RecordFileException>(() => BuildOne(game, "fresh", replace: false, writeOriginals: _ => throw new RecordFileException("disk full")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "out", "fresh")));
        Assert.Equal(new[] { "pack" }, Directory.GetFileSystemEntries(Path.Combine(_dir, "out")).Select(Path.GetFileName));
        // the folders made for a new save folder two deep are removed again too, innermost first
        var deep = PackageBuild.Plan([(102, "walk", Id)], "pack", Path.Combine(_dir, "new1", "new2"), [game]);
        Assert.Contains("Nothing was written.", Assert.Throws<RecordFileException>(() => PackageBuild.Build(deep, Writer(game, "walk"), _ => throw new RecordFileException("disk full"), false, [game])).Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "new1")));
        // never into the game's folders
        var plan = PackageBuild.Plan([(102, "walk", Id)], "pack", game, [game]);
        Assert.Throws<RecordFileException>(() => PackageBuild.Build(plan with { Problem = null }, MustNotWrite, MustNotWrite, false, [game]));
        Assert.Empty(Directory.GetFileSystemEntries(game));
    }

    /// <summary>The marker records the package's name and the build's kind; a build replaces only an earlier build of its own name and
    /// kind: a package named like a local undo folder an earlier version wrote never replaces it, nor a build of another name.</summary>
    [Fact]
    public void AnEarlierBuildIsReplacedOnlyByItsOwnNameAndKind()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        string folder = Path.Combine(_dir, "out", "pack");
        BuildOne(game, "pack", replace: false);
        var marker = File.ReadAllLines(Path.Combine(folder, PackageBuild.MarkerName));
        Assert.Equal("kind: package", marker[3]);
        Assert.True(PackageBuild.IsEarlierBuild(folder, "pack", restore: false));
        Assert.False(PackageBuild.IsEarlierBuild(folder, "pack", restore: true));
        Assert.False(PackageBuild.IsEarlierBuild(folder, "other", restore: false));
        // a marker of another name in the folder: refused, the folder as it was
        File.WriteAllText(Path.Combine(folder, PackageBuild.MarkerName), PackageBuild.MarkerText("someone else's pack", DateTime.UtcNow, restore: false));
        var before = Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
        Assert.Contains("its marker file names another package", Assert.Throws<RecordFileException>(() => BuildOne(game, "pack", replace: true)).Message);
        Assert.Equal(before, Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList());
        // a local undo folder's marker an earlier version wrote (also one written without a kind line, known by its name, by either of
        // its names) isn't a package's: never replaced by a build
        File.WriteAllText(Path.Combine(folder, PackageBuild.MarkerName), PackageBuild.MarkerText("pack", DateTime.UtcNow, restore: true));
        Assert.Throws<RecordFileException>(() => BuildOne(game, "pack", replace: true));
        foreach (var undoName in new[] { "X - local undo (do not share)", "X - local undo" })
        {
            File.WriteAllText(Path.Combine(folder, PackageBuild.MarkerName), PackageBuild.MarkerText(undoName, DateTime.UtcNow));
            Assert.True(PackageBuild.IsEarlierBuild(folder, undoName, restore: true));
            Assert.False(PackageBuild.IsEarlierBuild(folder, undoName, restore: false));
        }
        // its own marker again: replaced
        File.WriteAllText(Path.Combine(folder, PackageBuild.MarkerName), PackageBuild.MarkerText("pack", DateTime.UtcNow));
        BuildOne(game, "pack", replace: true);
        Assert.Equal("kind: package", File.ReadAllLines(Path.Combine(folder, PackageBuild.MarkerName))[3]);
        // a name that differs only in case names the same folder on Windows: the same build, replaced
        Assert.True(PackageBuild.IsEarlierBuild(folder, "PACK", restore: false));
        BuildOne(game, "Pack", replace: true);
        Assert.Equal("name: Pack", File.ReadAllLines(Path.Combine(folder, PackageBuild.MarkerName))[1]);
        File.WriteAllText(Path.Combine(folder, PackageBuild.MarkerName), PackageBuild.MarkerText("Pack - Local Undo (Do Not Share)", DateTime.UtcNow));
        Assert.True(PackageBuild.IsEarlierBuild(folder, "pack - local undo (do not share)", restore: true));
        // the earliest "restore" folder of the game's own files beside a package: a marker of it without a kind line is still a local undo
        // folder's, not a package's
        File.WriteAllText(Path.Combine(folder, PackageBuild.MarkerName), PackageBuild.MarkerText("X - restore", DateTime.UtcNow));
        Assert.True(PackageBuild.IsEarlierBuild(folder, "X - restore", restore: true));
        Assert.False(PackageBuild.IsEarlierBuild(folder, "X - restore", restore: false));
        File.WriteAllText(Path.Combine(folder, PackageBuild.MarkerName), PackageBuild.MarkerText("X - restore", DateTime.UtcNow, restore: false));
        Assert.True(PackageBuild.IsEarlierBuild(folder, "X - restore", restore: false));
        File.WriteAllText(Path.Combine(folder, PackageBuild.MarkerName), PackageBuild.MarkerText("X - Restore", DateTime.UtcNow));
        Assert.True(PackageBuild.IsEarlierBuild(folder, "x - restore", restore: true));
    }

    /// <summary>The local undo folders earlier versions wrote beside a package (by both their names, <c>name - local undo (do not
    /// share)</c> and <c>name - local undo</c>, and the earliest <c>name - restore</c>) carry this tool's marker: a package of the same
    /// name never replaces one, Replace ticked or not, and nothing is written into one or below it; their files stay as they were.</summary>
    [Fact]
    public void LocalUndoFoldersOfEarlierVersionsAreNeverReplacedNorWrittenInto()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        string save = Path.Combine(_dir, "out");
        (int, string, ulong)[] clips = [(102, "walk", Id)];
        foreach (var name in new[] { "Hello - local undo (do not share)", "Hello - local undo", "Hello - restore" })
        {
            string undo = Path.Combine(save, name);
            Directory.CreateDirectory(undo);
            File.WriteAllBytes(Path.Combine(undo, "102_-_walk.Animation"), RecordFile.Content(Content("walk-original")));
            File.WriteAllText(Path.Combine(undo, "README.txt"), "an earlier version's text");
            // both forms of its marker: with the kind line, and without one, known by its name
            foreach (var marker in new[] { PackageBuild.MarkerText(name, DateTime.UtcNow, restore: true), PackageBuild.MarkerText(name, DateTime.UtcNow) })
            {
                File.WriteAllText(Path.Combine(undo, PackageBuild.MarkerName), marker);
                string before = string.Join("\n", Directory.GetFiles(undo).Order(StringComparer.Ordinal).Select(f => f + " " + Convert.ToHexString(File.ReadAllBytes(f))));
                var plan = PackageBuild.Plan(clips, name, save, [game]);
                Assert.Null(plan.Problem);
                foreach (bool replace in new[] { false, true })
                    Assert.EndsWith("Nothing was written.", Assert.Throws<RecordFileException>(() => PackageBuild.Build(plan, MustNotWrite, MustNotWrite, replace, [game])).Message);
                Assert.Contains("its marker file names another package", Assert.Throws<RecordFileException>(() => PackageBuild.Build(plan, MustNotWrite, MustNotWrite, true, [game])).Message);
                // never written into, nor below it
                foreach (var inside in new[] { undo, Path.Combine(undo, "sub") })
                {
                    Assert.Contains("Pick a folder outside your packages", PackageBuild.Plan(clips, "Inner", inside, [game]).Problem);
                    Assert.Contains("Pick a folder outside your packages", Assert.Throws<RecordFileException>(() =>
                        RecordFileWriter.Write(inside, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [new RecordFileItem(102, "walk", Id, Content("walk"))])).Message);
                }
                Assert.Equal(before, string.Join("\n", Directory.GetFiles(undo).Order(StringComparer.Ordinal).Select(f => f + " " + Convert.ToHexString(File.ReadAllBytes(f)))));
            }
        }
        // the package beside them is built as ever, and leaves them as they are
        BuildOne(game, "Hello", replace: false);
        Assert.Equal(new[] { "Hello", "Hello - local undo", "Hello - local undo (do not share)", "Hello - restore" },
            Directory.GetDirectories(save).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>A save folder that is, or lies inside, a folder carrying this tool's marker (a package, or a local undo folder) is
    /// refused by the plan and by the build: the new package (and its local undo folder of the game's files) would be shared with the
    /// earlier package. A folder beside it is fine.</summary>
    [Fact]
    public void ASaveFolderInsideAPackageIsRefused()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        string outer = Path.Combine(_dir, "out", "pack");
        BuildOne(game, "pack", replace: false);
        var before = Directory.GetFileSystemEntries(outer).Order(StringComparer.Ordinal).ToList();
        (int, string, ulong)[] clips = [(102, "walk", Id)];
        foreach (var (save, says) in new[] { (outer, "it is a package"), (outer + Path.DirectorySeparatorChar, "it is a package"),
                     (Path.Combine(outer, "sub", "deeper"), $"it lies inside {outer}, a package"), (Path.Combine(outer, "Original clips"), $"it lies inside {outer}, a package") })
        {
            var plan = PackageBuild.Plan(clips, "Inner", save, [game]);
            Assert.Contains(says, plan.Problem);
            Assert.Contains("Pick a folder outside your packages", plan.Problem);
            // the build refuses it again, whatever the plan said
            Assert.Contains("Pick a folder outside your packages", Assert.Throws<RecordFileException>(() =>
                PackageBuild.Build(plan with { Problem = null }, MustNotWrite, MustNotWrite, false, [game])).Message);
        }
        Assert.Equal(before, Directory.GetFileSystemEntries(outer).Order(StringComparer.Ordinal).ToList());
        // the folder that holds the package is fine, and so is a folder beside it
        Assert.Null(PackageBuild.Plan(clips, "Inner", Path.Combine(_dir, "out"), [game]).Problem);
        Assert.Null(PackageBuild.Plan(clips, "Inner", Path.Combine(_dir, "beside"), [game]).Problem);
    }

    /// <summary>The folders a build of this package that didn't finish left beside it are found and named, by this tool's pattern only.</summary>
    [Fact]
    public void LeftoversBesideThePackageAreFound()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        string save = Path.Combine(_dir, "out");
        var plan = PackageBuild.Plan([(102, "walk", Id)], "pack", save, [game]);
        Assert.Empty(PackageBuild.LeftoverFolders(plan));
        Assert.Empty(PackageBuild.BesideNotes(plan));
        foreach (var d in new[] { "pack.0123456789.building", "pack.abcdef0123.earlier", "pack.notatag1234.building",
                     "other.0123456789.building", "pack.0123456789.old", "pack - restore.0123456789.building" })
            Directory.CreateDirectory(Path.Combine(save, d));
        var left = PackageBuild.LeftoverFolders(plan);
        Assert.Equal(new[] { Path.Combine(save, "pack.0123456789.building"), Path.Combine(save, "pack.abcdef0123.earlier") }, left);
        var notes = PackageBuild.BesideNotes(plan);
        Assert.Single(notes);
        Assert.All(left, l => Assert.Contains(l, notes[0]));
        Assert.Contains("didn't finish", notes[0]);
        Assert.DoesNotContain("share", notes[0]);
    }

    /// <summary>When the new build can't be moved into place after the earlier one was moved aside (a file of the new build held open),
    /// the earlier build is put back as it was and the message says so.</summary>
    [Fact]
    public void TheEarlierBuildIsPutBackWhenTheMoveFails()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        string folder = Path.Combine(_dir, "out", "pack");
        BuildOne(game, "pack", replace: false);
        var before = Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(f => (Path.GetFileName(f), Convert.ToHexString(File.ReadAllBytes(f)))).ToList();
        var item = new RecordFileItem(102, "walk", Id, Named("walk", Id));
        FileStream? held = null;
        try
        {
            // the writer writes the new files, then a handle to one of them stays open, so the staging folder can't be renamed
            var ex = Assert.Throws<RecordFileException>(() => BuildOne(game, "pack", replace: true, f =>
            {
                var pkg = RecordFileWriter.Write(f, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [item]);
                held = new FileStream(Path.Combine(f, "102_-_walk.Animation"), FileMode.Open, FileAccess.Read, FileShare.Read);
                return pkg;
            }));
            Assert.Contains("The earlier build is in place as it was", ex.Message);
        }
        finally { held?.Dispose(); }
        Assert.Equal(before, Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(f => (Path.GetFileName(f), Convert.ToHexString(File.ReadAllBytes(f)))).ToList());
        // the unfinished build, which couldn't be removed while held, is all that is left beside it; removed here
        foreach (var d in Directory.GetDirectories(Path.Combine(_dir, "out")).Where(d => d != folder)) Directory.Delete(d, true);
        Assert.Equal(new[] { "pack" }, Directory.GetFileSystemEntries(Path.Combine(_dir, "out")).Select(Path.GetFileName));
    }

    /// <summary>A link (here a junction) standing at a build's own place is never built through: Build refuses it, with Replace ticked or
    /// not, and deletes or moves nothing in the folder it leads to (a real earlier build of the same name), nor the link itself. A link
    /// put there while the build is being staged is refused right before the move, the same. The guard's link test knows a junction, one
    /// leading nowhere, and nothing else.</summary>
    [Fact]
    public void ALinkAtTheBuildsOwnPlaceIsNeverBuiltThrough()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        static string Files(string dir) => string.Join("\n", Directory.GetFiles(dir).Order(StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
        BuildOne(game, "pack", replace: false);
        string target = Path.Combine(_dir, "out", "pack");
        string targetBefore = Files(target);
        string save = Path.Combine(_dir, "linked"), link = Path.Combine(save, "pack");
        Directory.CreateDirectory(save);
        GuardTests.Junction(link, target);
        string dangling = Path.Combine(_dir, "dangling");
        GuardTests.Junction(dangling, Path.Combine(_dir, "gone"));
        try
        {
            Assert.True(Core.IO.FolderGuard.IsLink(link));
            Assert.True(Core.IO.FolderGuard.IsLink(dangling));
            Assert.False(Core.IO.FolderGuard.IsLink(target));
            Assert.False(Core.IO.FolderGuard.IsLink(Path.Combine(target, PackageBuild.MarkerName)));
            Assert.False(Core.IO.FolderGuard.IsLink(Path.Combine(_dir, "nothing here")));
            var item = new RecordFileItem(102, "walk", Id, Named("walk", Id));
            RecordPackage Write(string f) => RecordFileWriter.Write(f, "46_-_MIS_Y2E4_Katya_Maksimov.data", [game], [item]);
            var plan = PackageBuild.Plan([(102, "walk", Id)], "pack", save, [game]);
            Assert.Null(plan.Problem);
            foreach (bool replace in new[] { true, false })
                Assert.Equal($"{link} is a link (a junction or a symbolic link) to another folder, so it isn't replaced and nothing is deleted or moved through it: "
                    + "remove the link, or give the package another name. Nothing was written.",
                    Assert.Throws<RecordFileException>(() => PackageBuild.Build(plan, Write, Write, replace, [game])).Message);
            Assert.Equal(targetBefore, Files(target));
            Assert.True(Core.IO.FolderGuard.IsLink(link));
            Assert.Equal(new[] { link }, Directory.GetFileSystemEntries(save));
            // a real earlier build there, swapped for a link to it while the new build is written: refused before the move, the earlier
            // build where the link leads left as it is, and nothing of the new build left
            Directory.Delete(link);
            PackageBuild.Build(plan, Write, Write, false, [game]);
            string away = Path.Combine(_dir, "away");
            Directory.CreateDirectory(away);
            var ex = Assert.Throws<RecordFileException>(() => PackageBuild.Build(plan, f =>
            {
                Directory.Move(link, Path.Combine(away, "pack"));
                GuardTests.Junction(link, Path.Combine(away, "pack"));
                return Write(f);
            }, Write, true, [game]));
            Assert.Contains($"{link} is a link (a junction or a symbolic link) to another folder, so it isn't replaced", ex.Message);
            Assert.EndsWith("Nothing was written.", ex.Message);
            Assert.Equal(new[] { "pack" }, Directory.GetFileSystemEntries(away).Select(Path.GetFileName));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(away, "pack")).Length);
            Assert.Equal(new[] { link }, Directory.GetFileSystemEntries(save));
            Assert.True(Core.IO.FolderGuard.IsLink(link));
        }
        finally
        {
            // the links removed as themselves first, never followed
            foreach (var l in new[] { link, dangling })
                if (Core.IO.FolderGuard.IsLink(l)) Directory.Delete(l);
        }
    }

    /// <summary>A save folder holding a character Windows refuses in a folder name is a path error, never a link: the plan says the
    /// name can't be used and why, the build refuses it the same whatever the plan said, nothing is written, and the guard's link test
    /// says no link stands there.</summary>
    [Fact]
    public void ASaveFolderNameWindowsRefusesIsSaidAsSuchNeverAsALink()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        (int, string, ulong)[] clips = [(102, "walk", Id)];
        foreach (char bad in new[] { '?', '*', '|', '<', '"', '>' })
        {
            string save = Path.Combine(_dir, $"Mine{bad}"), full = Path.GetFullPath(save);
            Assert.False(Core.IO.FolderGuard.IsLink(save, out string? unread));
            Assert.Null(unread);
            Assert.False(Core.IO.FolderGuard.IsLink(Path.Combine(save, "pack")));
            string says = $"The package can't be written in {full}: this folder name can't be used: it holds '{bad}', a character Windows refuses in a folder name. "
                + "Choose another save folder.";
            var plan = PackageBuild.Plan(clips, "pack", save, [game]);
            Assert.Equal(says, plan.Problem);
            Assert.DoesNotContain("link", plan.Problem);
            Assert.Equal(says, Assert.Throws<RecordFileException>(() =>
                PackageBuild.Build(plan with { Problem = null }, MustNotWrite, MustNotWrite, true, [game])).Message);
            // in a folder above the package's, too
            Assert.StartsWith("The package can't be written in", PackageBuild.Plan(clips, "pack", Path.Combine(save, "deeper"), [game]).Problem);
            Assert.Contains($"this folder name can't be used: it holds '{bad}'", PackageBuild.Plan(clips, "pack", Path.Combine(save, "deeper"), [game]).Problem);
        }
        // a control character the same; the drive's colon and the separators are a path's own
        Assert.Contains("this folder name can't be used: it holds a control character (U+0009)", PackageBuild.Plan(clips, "pack", Path.Combine(_dir, "a\tb"), [game]).Problem);
        Assert.Null(Core.IO.FolderGuard.RefusedNameChar(Path.Combine(_dir, "fine name (1)")));
        Assert.Null(Core.IO.FolderGuard.RefusedNameChar(@"\\?\" + Path.Combine(_dir, "y")));
        Assert.Equal(new[] { "game" }, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName));
    }

    // ---------------------------------------------------------------------------------------------------------------- who plays a clip

    [Fact]
    public void WhoPlaysAClipInWordsAndTheNotice()
    {
        // sure ones first, then the probable ones in a clause of their own, so "probably" covers exactly them; each part in the
        // vocabulary's order
        Assert.Equal("Player, Teammates and Enemy", new ClipUsers(["Player", "Teammates", "Enemy"], [], true).Text);
        Assert.Equal("Player", new ClipUsers(["Player"], [], true).Text);
        var shared = new ClipUsers(["Player"], ["Teammates", "Enemy"], true);
        Assert.Equal("Player; probably also Teammates and Enemy", shared.Text);
        Assert.Equal("Player and Enemy; probably also Teammates and NPC", new ClipUsers(["Player", "Enemy"], ["Teammates", "NPC"], true).Text);
        Assert.Equal("probably Enemy and NPC", new ClipUsers([], ["Enemy", "NPC"], true).Text);
        Assert.Equal(ClipUsers.UncategorisedText, new ClipUsers([], [], true).Text);
        // the notice: several kinds of character, one only probably, or none placed; never for one kind, sure
        Assert.True(shared.Several);
        Assert.Equal("More than one kind of character plays this clip: Player; probably also Teammates and Enemy. Replacing it changes it for all of them.", shared.Notice);
        Assert.Null(new ClipUsers(["Player"], [], true).Notice);
        var unsure = new ClipUsers([], ["Player"], true);
        Assert.True(unsure.OneProbable);
        Assert.Equal("The tool isn't sure who plays this clip (probably Player): others may play it too, and replacing it changes it for every one of them.", unsure.Notice);
        Assert.Equal("The tool can't tell who plays this clip, so it can't say who else a replacement changes.", new ClipUsers([], [], true).Notice);
        // without an index entry: not known, and why, the notice saying so plainly (never nothing, so no build goes without it)
        Assert.Equal(ClipUsers.NotKnownText, ClipUsers.Of(null, 1).Text);
        Assert.Equal("Who plays this clip isn't known: the clip index isn't ready yet. A replaced clip changes for every kind of character that plays it.", ClipUsers.Of(null, 1).Notice);
        var failed = ClipUsers.Of(null, 1, "IOException: the disk is full.");
        Assert.Equal(ClipUsers.NotBuiltText, failed.Text);
        Assert.Equal("Who plays this clip isn't known: the clip index couldn't be built (IOException: the disk is full). A replaced clip changes for every kind of character that plays it.", failed.Notice);
        Assert.Equal("Played by: " + ClipUsers.NotBuiltText, ClipIndexView.PlayedByLine(null, 1, "IOException: the disk is full."));
        var empty = ClipIndex.Build([], new GraphFacts { Documents = [], Clips = new Dictionary<ulong, ClipGraphFacts>(), Failures = [], ClipsLookedFor = 0 }, null);
        Assert.Equal(ClipUsers.NotHeldText, ClipUsers.Of(empty, 1).Text);
        // what "probably" stands for covers its three cases, the same words in the index's lines and the screens' tooltips
        Assert.Contains("from the clip's name, from a machine several kinds of character share, or where the name's family says the graph's user is unlikely", ClipUsers.ProbablyMeaning);
        Assert.EndsWith(ClipUsers.ProbablyMeaning["\"probably\"".Length..] + ".", ClipUsers.PlayedByTip);
        Assert.StartsWith("Who plays this clip isn't known: the clip index doesn't hold it.", ClipUsers.Of(empty, 1).Notice);
        // the Package screen's notice: before the index, that who plays the clips isn't known yet and Build opens once it is; when it
        // couldn't be built, why, and that Build goes on
        Assert.StartsWith("Who plays these clips isn't known yet: the clip index isn't ready", ClipIndexView.PackageNotice(null, [("a", 1), ("b", 2)]));
        Assert.Contains("Build opens once it is", ClipIndexView.PackageNotice(null, [("a", 1)]));
        Assert.StartsWith("Who plays this clip isn't known: the clip index couldn't be built (IOException: the disk is full).",
            ClipIndexView.PackageNotice(null, [("a", 1)], "IOException: the disk is full."));
        Assert.EndsWith("It doesn't stop Build.", ClipIndexView.PackageNotice(null, [("a", 1)], "IOException: the disk is full."));
        Assert.Null(ClipIndexView.PackageNotice(null, []));
        Assert.Equal("Who plays a isn't known: the clip index doesn't hold it.", ClipIndexView.PackageNotice(empty, [("a", 1)]));
        Assert.Equal("A, B and C", ClipUsers.List(["A", "B", "C"]));
    }

    // ---------------------------------------------------------------------------------------------------------------- the command line

    /// <summary>The Log screen's options: a log opened or a folder followed (one of the two), a picture of the Log screen with the player
    /// only (its who part) or all; either option makes a run automated, so it never reads the remembered folder nor the Documents folder.</summary>
    [Fact]
    public void TheLogOptions()
    {
        var a = ViewerArgs.Parse(["--game", "g", "--log-file", "clips.csv", "--shot", "l.png||||log", "--shot", "p.png||||log||player|"]);
        Assert.Equal(("clips.csv", null, true, true), (a.LogFile, a.LogFolder, a.Logs, a.Automated));
        Assert.Equal(new[] { new ShotSpec("l.png", null, null, null, "log"), new ShotSpec("p.png", null, null, null, "log", Who: "Player") }, a.Shots);
        Assert.False(a.ShotsShowEdit);
        // with the theme and the previews' timing after it (all ten parts): each part keeps its place
        Assert.Equal(new ShotSpec("t.png", null, null, null, "log", Who: "Player", Theme: ThemeChoice.Dark, Follows: false),
            ViewerArgs.Parse(["--game", "g", "--log-folder", "logs", "--shot", "t.png||||log||player||dark|own"]).Shots.Single());
        Assert.Equal(true, ViewerArgs.Parse(["--game", "g", "--log-file", "c.csv", "--shot", "e.png||||edit|||||follows", "--shot", "l.png||||log"]).Shots[0].Follows);
        var f = ViewerArgs.Parse(["--log-folder", "logs"]);
        Assert.Equal(("logs", true, false), (f.LogFolder, f.Automated, f.Unattended));
        Assert.Contains("--log-folder", ViewerArgs.Usage);
        Assert.Contains("log", ViewerArgs.Screens);
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--log-file", "a.csv", "--log-folder", "b"]));
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--log-file"]));
        Assert.False(ViewerArgs.Parse([]).Logs);
        // the folder followed is remembered with the other settings, one written wrong losing only itself
        var s = ViewerSettings.FromJson("""{ "GameFolder": "g", "LogFolder": "logs", "OriginalFollowsEdit": true }""");
        Assert.Equal(("g", "logs", (bool?)true), (s.GameFolder, s.LogFolder, s.OriginalFollowsEdit));
        Assert.Equal(("logs", (bool?)true), (ViewerSettings.FromJson(s.ToJson()).LogFolder, ViewerSettings.FromJson(s.ToJson()).OriginalFollowsEdit));
        Assert.Equal(("g", null), (ViewerSettings.FromJson("""{ "GameFolder": "g", "LogFolder": 7 }""").GameFolder, ViewerSettings.FromJson("""{ "GameFolder": "g", "LogFolder": 7 }""").LogFolder));
        // a change is saved only over settings that read: none (a first save) or a file of settings; a file that doesn't read isn't
        // written over
        string dir = Path.Combine(AppContext.BaseDirectory, "settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            Assert.True(ViewerSettings.TryLoad(path, out var none, out _));
            Assert.Null(none.GameFolder);
            File.WriteAllText(path, """{ "GameFolder": "g" }""");
            Assert.True(ViewerSettings.TryLoad(path, out var read, out _));
            Assert.Equal("g", read.GameFolder);
            // a file that isn't settings (a hand edit gone wrong): kept aside under a name of its own, said with both files, the defaults
            // given to save over it; the same file again isn't kept twice
            File.WriteAllText(path, "{ \"GameFolder\": \"g\", ");
            Assert.True(ViewerSettings.TryLoad(path, out var defaults, out var why));
            Assert.Null(defaults.GameFolder);
            var aside = Assert.Single(Directory.GetFiles(dir, ViewerSettings.UnreadableName + "*.json"));
            Assert.Equal("{ \"GameFolder\": \"g\", ", File.ReadAllText(aside));
            Assert.Contains(path, why);
            Assert.Contains(aside, why);
            Assert.True(ViewerSettings.TryLoad(path, out _, out _));
            Assert.Single(Directory.GetFiles(dir, ViewerSettings.UnreadableName + "*.json"));
            Assert.Contains(aside, ViewerSettings.SetAsideIfUnreadable(path));
            // settings saved with a byte order mark (UTF-8 or UTF-16) read as settings, never kept aside
            foreach (var withMark in new System.Text.Encoding[] { new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode })
            {
                File.WriteAllText(path, """{ "GameFolder": "g" }""", withMark);
                Assert.Null(ViewerSettings.SetAsideIfUnreadable(path));
                Assert.True(ViewerSettings.TryLoad(path, out var marked, out var none2));
                Assert.Equal(("g", null), (marked.GameFolder, none2));
            }
            Assert.Single(Directory.GetFiles(dir, ViewerSettings.UnreadableName + "*.json"));
            // a file held by another program: refused, said, never written over
            File.WriteAllText(path, """{ "GameFolder": "g" }""");
            Assert.Null(ViewerSettings.SetAsideIfUnreadable(path));
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(ViewerSettings.TryLoad(path, out _, out var held));
                Assert.Contains("is in use or can't be opened now", held);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheEditAndPackageOptions()
    {
        var a = ViewerArgs.Parse(["--game", "g", "--edit", "walk", "--edit-kind", "TRIM", "--from", "10", "--to", "40", "--edit-keep", "--package-out", "out",
            "--package-name", "P", "--shot", "e.png||0.5||edit", "--shot", "p.png||||package"]);
        Assert.Equal(EditKind.Trim, a.EditKind);
        Assert.Equal((10, 40), (a.From, a.To));
        Assert.True(a.EditKeep && a.Edits && a.Packages && a.Unattended && a.Automated);
        Assert.Equal(new[] { new ShotSpec("e.png", null, 0.5f, null, "edit"), new ShotSpec("p.png", null, null, null, "package") }, a.Shots);
        Assert.Equal("edit", a.ScreenFor(new ShotSpec("x.png", null, null, null)));
        // the Package screen waiting for the clip index, its time the index's progress
        Assert.Equal(new ShotSpec("w.png", null, 0.4f, null, ViewerArgs.PackageWaiting), ViewerArgs.Parse(["--game", "g", "--shot", "w.png||0.4||package-waiting"]).Shots.Single());
        // the Edit screen waiting for the edits each clip allows, the Import screen for the clip index, each time the progress
        Assert.Equal(new ShotSpec("e.png", "c", 0.3f, null, ViewerArgs.EditWaiting), ViewerArgs.Parse(["--game", "g", "--shot", "e.png|c|0.3||edit-waiting"]).Shots.Single());
        Assert.Equal(new ShotSpec("i.png", null, 0.6f, null, ViewerArgs.ImportWaiting), ViewerArgs.Parse(["--game", "g", "--shot", "i.png||0.6||import-waiting"]).Shots.Single());
        Assert.Equal("library", ViewerArgs.Parse(["--game", "g"]).ScreenFor(new ShotSpec("x.png", null, null, null)));
        // only a picture of the Edit screen (its own, waiting, or a run's edit's) waits for every clip's edit marks
        Assert.True(a.ShotsShowEdit);
        Assert.True(ViewerArgs.Parse(["--game", "g", "--shot", "e.png|c|0.3||edit-waiting"]).ShotsShowEdit);
        Assert.False(ViewerArgs.Parse(["--game", "g", "--shot", "l.png", "--shot", "p.png||||package", "--shot", "i.png||||import"]).ShotsShowEdit);
        // and only then are they worked out at all in such a run; a person's run always works them out
        Assert.True(a.WorksMarksOut);
        Assert.False(ViewerArgs.Parse(["--game", "g", "--shot", "l.png"]).WorksMarksOut);
        Assert.False(ViewerArgs.Parse(["--game", "g", "--export-skeleton", "--export-out", "out"]).WorksMarksOut);
        Assert.True(ViewerArgs.Parse([]).WorksMarksOut);
        Assert.True(ViewerArgs.Parse(["--game", "g"]).WorksMarksOut);
        Assert.Equal(1.5, ViewerArgs.Parse(["--game", "g", "--edit", "w", "--edit-kind", "scale", "--factor", "1.5"]).Factor);
        foreach (var bad in new[]
        {
            new[] { "--edit", "w", "--edit-kind", "mirror" },
            ["--game", "g", "--edit", "w"],
            ["--game", "g", "--edit", "w", "--edit-kind", "flip"],
            ["--game", "g", "--edit", "w", "--edit-kind", "speed"],
            ["--game", "g", "--edit", "w", "--edit-kind", "trim", "--from", "1"],
            ["--game", "g", "--edit", "w", "--edit-kind", "swap"],
            ["--game", "g", "--edit", "w", "--edit-kind", "scale", "--factor", "-1"],
            ["--game", "g", "--edit", "w", "--edit-kind", "trim", "--from", "1.5", "--to", "3"],
            ["--game", "g", "--edit-kind", "mirror"],
            ["--game", "g", "--edit-keep"],
            ["--package-out", "x"],
            ["--game", "g", "--package-name", "x"],
            ["--game", "g", "--shot", "a.png||||elsewhere"],
        })
            Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(bad));
    }
}

/// <summary>The screens' logic on the game's own clips.</summary>
[Collection("install")]
public sealed class ScreenLogicGameTests(InstallFixture f)
{
    static ClipGraph? _graph;
    ClipGraph G => _graph ??= ClipGraph.Read(f.Install);
    static ClipIndex? _index;
    static readonly object ReadOnce = new();
    ClipIndex X { get { lock (ReadOnce) return _index ??= ClipIndex.Build(f.Install, GraphFacts.Read(f.Install.Records, f.Install.Clips.Select(c => c.Id)), ClipIndexList.Embedded()); } }

    [GameDataFact]
    public void SwapSourcesOnTheInstall()
    {
        var g = f.Install;
        var s = SwapSources.Build(g.Clips, g.SharedTable);
        Assert.True(s.Swappable > 1000, $"{s.Swappable} clips with a swap source");
        int checkedPairs = 0;
        foreach (var c in g.Clips.Where(c => s.For(c).Count > 0).Take(60))
            foreach (var src in s.For(c).Take(3))
            {
                Assert.Null(SwapEdit.Mismatch(c.Record!, src.Record!, g.SharedTable));
                checkedPairs++;
            }
        Assert.True(checkedPairs > 60);
    }

    [GameDataFact]
    public void TheEditsOfAnArmedEmoteAndWhoPlaysADeath()
    {
        var g = f.Install;
        var hello = g.FindClip("mca_emote_Hello")!;
        var ctx = new EditContext(g.Skeleton, g.SharedTable, G.Verdict(hello.Id));
        var mirror = EditJobs.Options(hello, ctx, null, 0).Single(o => o.Kind == EditKind.Mirror);
        Assert.False(mirror.Available);
        Assert.Contains("hand tracks", mirror.Detail);
        Assert.Equal(EditReasons.Plain(EditKind.Mirror, EditRefusal.HandTracks), mirror.Unavailable);
        var death = g.FindClip("mil_gen_m_ale_ros_std_V0_N_death_regular_head_front_01")!;
        var lightHit = g.FindClip("pmc_rif_m_ale_ros_std_V0_N_hit_regular_chest_back")!;
        // who plays each by the clip index, as the Package screen's rows say it before Build
        Assert.Equal(new[] { "Player", "Teammates, Enemy and NPC", "Teammates, Enemy and NPC" }, new[] { hello, death, lightHit }.Select(c => ClipUsers.Of(X, c.Id).Text));
    }

    [GameDataFact]
    public void TheTeammatesOwnDeathAndHitMachinesNameTheirClipsAndTheIndexSaysTeammates()
    {
        var g = f.Install;
        // the teammates' death and hit machines, found from the teammates' layer graph by the IDs its records name, checked by ID
        Assert.StartsWith("Hit-StateMachine", G.Documents.Single(d => d.Id == 0x000001A2B8DBF323).Name);
        Assert.StartsWith("Death-StateMachine", G.Documents.Single(d => d.Id == 0x000001A2B8DBF324).Name);
        var named = g.Clips.Where(c => G.Verdict(c.Id).NodeUses.Any(u => u.DocumentId is 0x000001A2B8DBF323 or 0x000001A2B8DBF324)).Select(c => c.Id).ToHashSet();
        Assert.Equal(238, named.Count);
        // every one of them plays for the teammates by the graph (a document of the teammates' layer alone names it): sure, but for the
        // 19 heavies' deaths, whose family rules the teammates out (a teammate is never a heavy), only probably
        var probable = named.Where(id => ClipUsers.Of(X, id).Probably.Contains(ClipCategories.Teammates)).ToList();
        Assert.Equal(19, probable.Count);
        Assert.All(probable, id => Assert.StartsWith("pmc_hvy_", g.Clips.First(c => c.Id == id).Name, StringComparison.OrdinalIgnoreCase));
        Assert.All(named.Except(probable), id => Assert.Contains(ClipCategories.Teammates, ClipUsers.Of(X, id).Sure));
    }

    static string[] Entries(string dir) => [.. Directory.GetFileSystemEntries(dir).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal)];

    /// <summary>A package on game files where its clips are the game's own: one folder holding the record files, "Original clips" with
    /// the install's own record of each clip under the same file name (each hashing as the game's own record), and the marker; no text, and
    /// no folder beside it. The originals folder carries no marker, isn't taken for an unpack, and every writer refuses it as inside a
    /// package. Built again with Replace, it is replaced whole.</summary>
    [GameDataFact]
    public void APackagesOriginalClipsAreTheInstallsOwnRecordsOfTheGame()
    {
        var g = f.Install;
        var hello = g.FindClip("mca_emote_Hello")!;
        var face = g.FindClip("Mch_Face_Idle_Neutral01")!;
        var items = new List<PackageItem> { Item(hello, Edited(hello)), Item(face, Edited(face)) };
        string save = Path.Combine(AppContext.BaseDirectory, "originals-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plan = PackageBuild.Plan(g, items, "Originals", save);
            Assert.Null(plan.Problem);
            Assert.Equal(new[] { PackageBuild.FromInstall, PackageBuild.FromInstall }, plan.Originals.Select(o => o.From));
            Assert.Null(PackageBuild.OriginalsNote(plan.Originals));
            Assert.Contains(plan.Files, x => x.Name == Path.Combine("Original clips", FileName(hello)) && x.What == $"the game's own {hello.Name}");
            var built = PackageBuild.Build(g, plan, items, replace: false);
            string folder = Path.Combine(save, "Originals"), originals = Path.Combine(folder, "Original clips");
            Assert.Equal(folder, built.Folder);
            Assert.Equal(new[] { PackageBuild.MarkerName, PackageFile(face), PackageFile(hello), "Original clips" }.Order(StringComparer.Ordinal), Entries(folder));
            Assert.Equal(new[] { FileName(face), FileName(hello) }.Order(StringComparer.Ordinal), Entries(originals));
            foreach (var c in new[] { hello, face })
            {
                var file = File.ReadAllBytes(Path.Combine(originals, FileName(c)));
                Assert.Equal(RecordFile.Content(c.Bytes.Span), file);
                // the record the file gives back hashes as the game's own, by the table built into the tool
                Assert.Equal(ClipStatus.Vanilla, g.Vanilla!.StatusOf(c.Id, RecordFile.ToRecord(file, c.Name)));
                Assert.Equal(RecordFile.Content(Edited(c)), File.ReadAllBytes(Path.Combine(folder, PackageFile(c))));
                Assert.Contains(built.Files, x => x.FileName == Path.Combine("Original clips", FileName(c)) && x.Sha256 == Core.Hashing.Sha256Hex.Of(file));
            }
            Assert.Equal(new[] { PackageBuild.FromInstall, PackageBuild.FromInstall }, built.Originals.Select(o => o.From));
            // the package alone: no local undo folder beside it, and no text in it
            Assert.Equal(new[] { "Originals" }, Entries(save));
            Assert.DoesNotContain(Directory.GetFiles(save, "*", SearchOption.AllDirectories), p => Path.GetExtension(p) == ".txt");
            Assert.False(PackageBuild.IsEarlierBuild(originals));
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(originals));
            Assert.Contains("Pick a folder outside your packages", Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(g, originals, [(hello, Edited(hello))], overwrite: true)).Message);
            // again: refused without Replace, nothing changed; with Replace, replaced whole
            string before = Tree(save);
            Assert.Throws<RecordFileException>(() => PackageBuild.Build(g, plan, items, replace: false));
            Assert.Equal(before, Tree(save));
            PackageBuild.Build(g, plan, items, replace: true);
            Assert.Equal(new[] { "Originals" }, Entries(save));
            Assert.Equal(new[] { FileName(face), FileName(hello) }.Order(StringComparer.Ordinal), Entries(originals));
        }
        finally { if (Directory.Exists(save)) Directory.Delete(save, true); }
    }

    /// <summary>On an install whose clip isn't the game's own (the game folder's own patch forge, read only, where clips of other packages
    /// are installed): an item of what that install holds for the clip, or of the game's own record of it (known by the table of the game's
    /// clips although the install's clip is another), is no change and is never packaged; a clip of the builder's own package kept again on
    /// the install that holds it is refused as what the install holds, never as the game's own clip nor to be removed, with the way out:
    /// game files that don't hold it, or the earlier package folder kept. An edit of that modded slot takes its original from the base
    /// forge when it holds the game's own record (a vanilla patch forge stands in for it here, as the game's own base forge holds none of
    /// the game's records by their hashes): taken from there, its SHA-256 the game's own, and said; where it isn't found, the plan names
    /// the clip and why before Build (once the base forge was read), and Build refuses it with nothing written.</summary>
    [GameDataFact]
    public void AModdedSlotsOriginalComesFromTheBaseForgeOrTheBuildIsRefused()
    {
        using var installed = GameInstall.Open(GameData.Game!);
        var c = installed.Clips.FirstOrDefault(x => x.Status == ClipStatus.Replaced && Writable(x) && f.Install.FindClip(x.Id) is { } v && Writable(v));
        Assert.True(c is not null, "the game folder's patch forge holds no clip of the game's that isn't the game's own");
        // what the install holds for the clip, and the game's own record of it, are no change on this install; the first is said as not
        // the game's own clip (the table knows it isn't), which the install may hold from an earlier build of this package, with the
        // import's way out for an import, and as an edit that changes nothing for an edit
        Assert.Equal(PackageEntries.NotTheGamesOwn + PackageEntries.ImportInstallHoldsIt, PackageEntries.Unchanged(installed, c!, c!.Bytes.Span));
        Assert.Equal(PackageEntries.EditChangesNothing, PackageEntries.Unchanged(installed, c, c.Bytes.Span, PackageItemKind.Edit));
        var vanilla = f.Install.FindClip(c.Id)!;
        Assert.Equal(PackageEntries.GamesOwnUnchanged, PackageEntries.Unchanged(installed, c, vanilla.Bytes.Span));
        Assert.Null(PackageEntries.Unchanged(installed, c, Edited(c)));
        string save = Path.Combine(AppContext.BaseDirectory, "norestore-" + Guid.NewGuid().ToString("N"));
        var heldItem = new List<PackageItem> { new(c, c.Bytes.ToArray(), ClipChange.Imported("x.glb"), CheckLevel.Pass, "", DateTime.Now) };
        string held = $"{c.Name}: this isn't the game's own clip unchanged, but your game folder already holds this clip exactly as it is here (perhaps from an earlier "
            + "build of this package installed in the game), so the tool can't tell it from what's installed; build from game files that don't hold it (verify the "
            + "game files or put your forge backup back first), or keep the earlier package folder as it is.";
        Assert.Equal(held, PackageBuild.Plan(installed, heldItem, "Mine", save).Problem);
        Assert.Equal(held, Assert.Throws<RecordFileException>(() => PackageBuild.Build(installed, PackageBuild.Plan(installed, heldItem, "Mine", save) with { Problem = null }, heldItem, replace: false)).Message);
        Assert.Equal($"{c.Name}: {PackageEntries.GamesOwnUnchanged}, so it replaces nothing. Remove it from the package.",
            PackageBuild.Plan(installed, [new(c, vanilla.Bytes.ToArray(), ClipChange.Imported("x.glb"), CheckLevel.Pass, "", DateTime.Now)], "Mine", save).Problem);
        foreach (var record in new[] { c.Bytes.ToArray(), vanilla.Bytes.ToArray() })
        {
            var same = new List<PackageItem> { new(c, record, ClipChange.Imported("x.glb"), CheckLevel.Pass, "", DateTime.Now) };
            var plan = PackageBuild.Plan(installed, same, "Mine", save);
            Assert.Throws<RecordFileException>(() => PackageBuild.Build(installed, plan with { Problem = null }, same, replace: false));
            Assert.False(Directory.Exists(save));
        }
        // the install's record isn't said as the game's own clip, nor to be dropped from the package
        Assert.DoesNotContain("Remove it", held);
        Assert.DoesNotContain(PackageEntries.GamesOwnUnchanged + ",", held);
        var other = installed.Clips.First(x => x.Id != c.Id && x.Status == ClipStatus.Vanilla && Writable(x));
        var items = new List<PackageItem> { Item(c, Edited(c)), Item(other, Edited(other)) };
        try
        {
            // the builder's own package built on the game's own files, then built again on the install that holds it (this install stands
            // for it: it holds exactly that clip), the same clip kept and a new one added: refused with the install's reason, not as the
            // game's own clip, and nothing written
            var mine = new List<PackageItem> { Item(vanilla, c.Bytes.ToArray()) };
            PackageBuild.Build(f.Install, PackageBuild.Plan(f.Install, mine, "Mine", save), mine, replace: false);
            var rebuild = new List<PackageItem> { Item(c, c.Bytes.ToArray()), Item(other, Edited(other)) };
            string saveBefore = Tree(save);
            var rebuildPlan = PackageBuild.Plan(installed, rebuild, "Mine", save);
            Assert.Equal(held, rebuildPlan.Problem);
            Assert.Equal(held, Assert.Throws<RecordFileException>(() => PackageBuild.Build(installed, rebuildPlan with { Problem = null }, rebuild, replace: true)).Message);
            Assert.Equal(saveBefore, Tree(save));
            Directory.Delete(save, true);

            // before the base forge is read for this install: the modded slot's original is still to be looked for, the screen says so
            // (and greys Build while it reads it); the other's is the install's own
            var before = PackageBuild.Plan(installed, items, "Mods", save);
            Assert.Null(before.Problem);
            Assert.True(before.Originals[0].Pending);
            Assert.Equal(PackageBuild.FromInstall, before.Originals[1].From);
            // and that the install's mod of it is replaced by the package, kept in the originals as 1_-_
            Assert.Equal($"Your game folder holds a modded clip in place of {c.Name}: looking for the game's own record for \"Original clips\"... "
                + PackageBuild.ReplacesModNote(before.Originals), PackageBuild.OriginalsNote(before.Originals));
            Assert.Contains($"already hold a mod of {c.Name}", PackageBuild.ReplacesModNote(before.Originals));
            Assert.Same(c, before.Originals[0].Current);
            Assert.Null(before.Originals[1].Current);
            // the game's base forge read: it holds the animation datafile, but not the game's own record of this clip by its hash
            var found = PackageBuild.Originals(installed, items, readForges: true);
            if (found[0].Record is { } fromBase)
            {
                Assert.Equal(PackageBuild.FromBaseForge, found[0].From);
                Assert.Equal(ClipStatus.Vanilla, installed.Vanilla!.StatusOf(c.Id, fromBase));
            }
            else
            {
                Assert.Equal("your game folder holds a modded clip there, no vanilla forge backup is named, DataPC.forge doesn't hold it either", found[0].Missing);
                // never refused: the package is built without that clip's original, and the screen names the clip
                var plan = PackageBuild.Plan(installed, items, "Mods", save);
                Assert.Null(plan.Problem);
                Assert.Equal($"No original included for {c.Name}: it can't be uninstalled with this package; {found[0].Missing}. To include it, name a vanilla "
                    + "backup of DataPC_patch_01.forge that holds it, or build on game files where that clip is the game's own.", PackageBuild.MissingNote(found));
                Assert.Contains(PackageBuild.MissingNote(found)!, PackageBuild.OriginalsNote(plan.Originals));
                Assert.DoesNotContain(plan.Files, x => x.Name == Path.Combine("Original clips", FileName(c)));
                var without = PackageBuild.Build(installed, plan, items, replace: false);
                Assert.False(File.Exists(Path.Combine(without.Folder, "Original clips", FileName(c))));
                Assert.Equal(RecordFile.Content(other.Bytes.Span), File.ReadAllBytes(Path.Combine(without.Folder, "Original clips", FileName(other))));
                Assert.Equal(RecordFile.Content(Edited(c)), File.ReadAllBytes(Path.Combine(without.Folder, PackageFile(c))));
                Assert.Equal(RecordFile.Content(c.Bytes.Span), File.ReadAllBytes(Path.Combine(without.Folder, "Original clips", PackageFile(c))));
                Assert.NotNull(without.Originals[0].Missing);
                Directory.Delete(save, true);
                // no game's own record found for any clip: the originals folder holds only the install's mod, as 1_-_
                var alone = new List<PackageItem> { items[0] };
                var lonely = PackageBuild.Build(installed, PackageBuild.Plan(installed, alone, "Mods", save), alone, replace: false);
                Assert.Equal(new[] { PackageFile(c) }, Directory.GetFiles(Path.Combine(lonely.Folder, "Original clips")).Select(Path.GetFileName));
                Assert.True(File.Exists(Path.Combine(lonely.Folder, PackageFile(c))));
                Directory.Delete(save, true);
            }

            // a base forge that can't be read (a missing file stands in for one the game holds open): the failure is said and not kept as
            // the result, so the next read tries again
            PackageBuild.BaseForgeInTests.Value = Path.Combine(AppContext.BaseDirectory, "no-such-" + Guid.NewGuid().ToString("N"), "DataPC.forge");
            try
            {
                Assert.NotNull(PackageBuild.Originals(installed, items, readForges: true)[0].Missing);
                Assert.NotNull(PackageBuild.ForgeReadFailure(installed));
                // until the next read it stands as failed, said with the clip (never as still to be looked for, which would leave the
                // screen looking for ever); the next read (Build's) tries again
                var after = PackageBuild.Plan(installed, items, "Mods", save).Originals[0];
                Assert.False(after.Pending);
                Assert.Contains("DataPC.forge couldn't be read: ", after.Missing);
            }
            finally { PackageBuild.BaseForgeInTests.Value = null; }

            // a named backup that isn't there any more (a drive not plugged in, a moved file): said plainly, the clip without its original
            {
                string gone = Path.Combine(AppContext.BaseDirectory, "no-such-" + Guid.NewGuid().ToString("N"), "DataPC_patch_01.forge");
                var o = PackageBuild.Originals(installed, items, readForges: true, backupForge: gone)[0];
                Assert.Contains($"your vanilla forge backup isn't there any more: {gone}", o.Missing);
                Assert.False(PackageBuild.Plan(installed, items, "Mods", save, backupForge: gone).Originals[0].Pending);
            }

            // the user's vanilla forge backup (the vanilla patch forge stands in for it): the original is taken from there, before the base
            // forge, checked against the game's own hash, and said
            {
                var plan = PackageBuild.Plan(installed, items, "Mods", save, backupForge: GameData.Forge);
                Assert.True(plan.Originals[0].Pending);
                var built = PackageBuild.Build(installed, plan, items, replace: false, backupForge: GameData.Forge);
                Assert.Equal(new[] { PackageBuild.FromBackupForge, PackageBuild.FromInstall }, built.Originals.Select(o => o.From));
                Assert.Equal(RecordFile.Content(vanilla.Bytes.Span), File.ReadAllBytes(Path.Combine(built.Folder, "Original clips", FileName(c))));
                Assert.Equal($"\"Original clips\": {c.Name} is taken from your vanilla forge backup: your game folder holds a modded clip there. "
                    + PackageBuild.ReplacesModNote(built.Originals), PackageBuild.OriginalsNote(built.Originals));
                Directory.Delete(save, true);
                // a backup that isn't vanilla in that slot (the modded install's own forge): not taken, the clip left without its original
                var notVanilla = PackageBuild.Originals(installed, items, readForges: true, backupForge: Path.Combine(GameData.Game!, "DataPC_patch_01.forge"));
                Assert.Null(notVanilla[0].Record);
                Assert.Contains("your vanilla forge backup doesn't hold the game's own record of it", notVanilla[0].Missing);
            }

            // a base forge that holds the game's own record (a vanilla patch forge stands in for it): the original is taken from there,
            // byte for byte the game's own, said on the screen and in the result
            PackageBuild.BaseForgeInTests.Value = GameData.Forge;
            try
            {
                // the same file was read above as the backup: what a forge holds is kept by its path, so it isn't read again
                Assert.False(PackageBuild.Plan(installed, items, "Mods", save).Originals[0].Pending);
                var built = PackageBuild.Build(installed, PackageBuild.Plan(installed, items, "Mods", save), items, replace: false);
                Assert.Equal(new[] { PackageBuild.FromBaseForge, PackageBuild.FromInstall }, built.Originals.Select(o => o.From));
                var file = File.ReadAllBytes(Path.Combine(built.Folder, "Original clips", FileName(c)));
                Assert.Equal(RecordFile.Content(vanilla.Bytes.Span), file);
                Assert.Equal(ClipStatus.Vanilla, installed.Vanilla!.StatusOf(c.Id, RecordFile.ToRecord(file, c.Name)));
                Assert.Equal(RecordFile.Content(other.Bytes.Span), File.ReadAllBytes(Path.Combine(built.Folder, "Original clips", FileName(other))));
                Assert.Equal(RecordFile.Content(Edited(c)), File.ReadAllBytes(Path.Combine(built.Folder, PackageFile(c))));
                string note = $"\"Original clips\": {c.Name} is taken from the game's base files: your game folder holds a modded clip there. "
                    + PackageBuild.ReplacesModNote(built.Originals);
                Assert.Equal(note, PackageBuild.OriginalsNote(built.Originals));
                var after = PackageBuild.Plan(installed, items, "Mods", save);
                Assert.Equal(note, PackageBuild.OriginalsNote(after.Originals));
                Assert.Contains(after.Files, x => x.Name == Path.Combine("Original clips", FileName(c)) && x.What.EndsWith(PackageBuild.FromBaseForgeWhy, StringComparison.Ordinal));
                Assert.Equal(new[] { "Mods" }, Entries(save));
            }
            finally { PackageBuild.BaseForgeInTests.Value = null; }
        }
        finally { if (Directory.Exists(save)) Directory.Delete(save, true); }
    }

    /// <summary>Clips of the install that are the game's own and that a record file names unchanged, the first <paramref name="n"/>.</summary>
    static List<ClipEntry> GameClips(GameInstall g, int n)
    {
        var l = g.Clips.Where(c => c.Status == ClipStatus.Vanilla && RecordFile.NameRuleBroken(c.Name, c.Id) is null && RecordFile.HeadRuleBroken(c.Bytes.Span) is null)
            .DistinctBy(c => c.Index).Take(n).ToList();
        Assert.Equal(n, l.Count);
        return l;
    }

    /// <summary>A clip's record with its last byte changed: an edit, as far as the bytes go.</summary>
    static byte[] Edited(ClipEntry c)
    {
        var b = c.Bytes.ToArray();
        b[^1] ^= 0x01;
        return b;
    }

    static PackageItem Item(ClipEntry c, byte[] record) => new(c, record, ClipChange.Imported("x.glb"), CheckLevel.Pass, "", DateTime.Now);

    /// <summary>A package of 100 clips builds, with its 100 original clips (the game's own files, in a folder inside the package, which
    /// carries the marker), and again with Replace; a package of the game's own bytes is refused, by the plan and by the build, and nothing
    /// of it is written; the record writer writes 100 edited clips and writes them again with overwrite, and 100 of the game's own clips
    /// written loose, with no marker, are written again too (far below an unpack's count).</summary>
    [GameDataFact]
    public void AHundredClipPackageItsOriginalsAndItsRewritesBuild()
    {
        var g = f.Install;
        var clips = GameClips(g, 100);
        var items = clips.Select(c => Item(c, Edited(c))).ToList();
        string save = Path.Combine(AppContext.BaseDirectory, "hundred-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plan = PackageBuild.Plan(g, items, "Hundred", save);
            Assert.Null(plan.Problem);
            var built = PackageBuild.Build(g, plan, items, replace: false);
            Assert.Equal(200, built.Files.Count(x => x.FileName.EndsWith(RecordFile.ClipExtension, StringComparison.Ordinal)));
            string originals = Path.Combine(built.Folder, "Original clips");
            Assert.Equal(100, Directory.GetFiles(originals).Length);
            Assert.Equal(RecordFile.Content(clips[42].Bytes.Span), File.ReadAllBytes(Path.Combine(originals, RecordFile.ClipFileName(clips[42].Index, clips[42].Name, clips[42].Id))));
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(originals));
            // again with Replace: replaced
            PackageBuild.Build(g, plan, items, replace: true);
            Assert.Equal(new[] { "Hundred" }, Directory.GetDirectories(save).Select(Path.GetFileName));
            // a package of the game's own bytes is never built: the plan names the first clip, and the build refuses it whatever the plan
            var same = clips.Select(c => Item(c, c.Bytes.ToArray())).ToList();
            var samePlan = PackageBuild.Plan(g, same, "Same", save);
            Assert.Equal($"{clips[0].Name}: {PackageEntries.GamesOwnUnchanged}, so it replaces nothing. Remove it from the package.", samePlan.Problem);
            Assert.Equal(samePlan.Problem, PackageBuild.UnchangedProblem(g, same));
            Assert.Throws<RecordFileException>(() => PackageBuild.Build(g, samePlan, same, replace: false));
            Assert.Throws<RecordFileException>(() => PackageBuild.Build(g, samePlan with { Problem = null }, same, replace: false));
            // one unchanged clip among edited ones is enough, and is named
            var mixed = items.Take(5).Append(Item(clips[50], clips[50].Bytes.ToArray())).ToList();
            Assert.StartsWith($"{clips[50].Name}: {PackageEntries.GamesOwnUnchanged}", PackageBuild.Plan(g, mixed, "Same", save).Problem);
            Assert.Throws<RecordFileException>(() => PackageBuild.Build(g, PackageBuild.Plan(g, mixed, "Same", save) with { Problem = null }, mixed, replace: false));
            Assert.Equal(new[] { "Hundred" }, Directory.GetDirectories(save).Select(Path.GetFileName));
            Assert.Null(PackageBuild.UnchangedProblem(g, items));
            // the record writer: 100 edited clips, then the same again with overwrite
            string loose = Path.Combine(save, "loose");
            var edited = items.Select(i => (i.Slot, i.Record)).ToList();
            Assert.Equal(100, RecordFileWriter.Write(g, loose, edited).Files.Count);
            Assert.Equal(100, RecordFileWriter.Write(g, loose, edited, overwrite: true).Files.Count);
            // the game's own bytes written loose, with no marker: 100 of them are a folder of the player's own, written again freely
            string own = Path.Combine(save, "own");
            var vanilla = clips.Select(c => (c, c.Bytes.ToArray())).ToList();
            RecordFileWriter.Write(g, own, vanilla);
            Assert.Equal(100, RecordFileWriter.Write(g, own, vanilla, overwrite: true).Files.Count);
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(own));
        }
        finally { if (Directory.Exists(save)) Directory.Delete(save, true); }
    }

    /// <summary>The <paramref name="n"/> smallest clips of the install that are the game's own, that a record file names unchanged, and
    /// whose name holds a capital (so a name in lower case is another spelling): the content rule's folders of game files are made of
    /// these at little cost.</summary>
    static List<ClipEntry> SmallGameClips(GameInstall g, int n)
    {
        var l = g.Clips.Where(c => c.Status == ClipStatus.Vanilla && RecordFile.NameRuleBroken(c.Name, c.Id) is null && RecordFile.HeadRuleBroken(c.Bytes.Span) is null
                && c.Name.Any(char.IsAsciiLetterUpper))
            .DistinctBy(c => c.Index).OrderBy(c => c.Bytes.Length).ThenBy(c => c.Index).Take(n).ToList();
        Assert.Equal(n, l.Count);
        return l;
    }

    /// <summary>Runs the command-line harness in this process, on this thread, as its command line would; its exit code.</summary>
    static int Cli(params string[] args) =>
        (int)System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("grbanim-cli")).EntryPoint!.Invoke(null, [args])!;

    /// <summary>The command-line harness refuses an option its command doesn't read (a mistyped name), with the usage's exit code and
    /// before any work, and takes every option each command reads. The game folder named isn't there, so no command gets further than
    /// opening it, and nothing is written.</summary>
    [Fact]
    public void TheCommandLineRefusesAnOptionItsCommandDoesntRead()
    {
        string none = Path.Combine(AppContext.BaseDirectory, "no-game-" + Guid.NewGuid().ToString("N"));
        string o = Path.Combine(none, "out"), file = Path.Combine(none, "out", "file");
        Assert.Equal(2, Cli("check", none, "--forse", "x"));
        Assert.Equal(2, Cli("list", none, "--serach", "x"));
        Assert.Equal(2, Cli("list", none, "--arm", "--unarmed"));
        Assert.Equal(2, Cli("export", none, "--clip", "x", "--out", o, "--overwrite"));
        foreach (string[] a in new string[][]
                 {
                     ["check", none, "--forge", "f"],
                     ["list", none, "--forge", "f", "--search", "x", "--armed", "--unarmed"],
                     ["import", none, "--forge", "f", "--glb", "x.glb", "--clip", "x", "--out", o, "--report", o, "--overwrite", "--allow-unchanged"],
                     ["compare", none, "--forge", "f", "--clip", "x", "--record-file", file, "--top", "3"],
                     ["soak", none, "--forge", "f", "--passes", "1", "--log", file, "--minutes", "1", "--decode-datafile-each-pass"],
                     ["soak", none, "--forge", "f", "--rebuild", "--passes", "1", "--log", file, "--minutes", "1", "--decode-datafile-each-pass"],
                     ["hash-table", "f", "--game", none, "--out", file],
                     ["graph", none, "--forge", "f", "--clip", "x"],
                     ["export", none, "--forge", "f", "--clip", "x", "--out", o],
                     ["export-skeleton", none, "--forge", "f", "--out", o, "--name", "s.glb", "--fault", "axes"],
                     ["pose", none, "--forge", "f", "--clip", "x", "--ticks", "0,1", "--out", file],
                     ["write", none, "--forge", "f", "--clip", "x", "--out", o, "--record-file", file, "--reencode", "--curves", file, "--overwrite"],
                     ["rebuild-check", none, "--forge", "f"],
                     ["census-edits", none, "--forge", "f", "--out", file, "--features", "x", "--wide-speeds"],
                     ["edit", none, "--forge", "f", "--clip", "x", "--edit", "trim", "--out", o, "--factor", "1", "--from", "0", "--to", "1", "--source", "x",
                         "--overwrite", "--allow-unchanged"],
                     ["index", none, "--forge", "f", "--clip", "x", "--out", file, "--cache", o],
                 })
            Assert.True(Cli(a) is 1 or 5, string.Join(" ", a));
        Assert.False(Directory.Exists(none));
    }

    /// <summary>Every file under a folder, folders below it included, by its path and SHA-256, for "left as it was".</summary>
    static string Tree(string dir) =>
        string.Join("\n", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => p + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))));

    /// <summary>The names and bytes of every file right in a folder, for "left as it was".</summary>
    static List<(string, string)> Snapshot(string dir) =>
        Directory.GetFileSystemEntries(dir).Order(StringComparer.Ordinal)
            .Select(p => (Path.GetFileName(p), File.Exists(p) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : "folder")).ToList();

    /// <summary>A folder of another name holding 1,000 of the game's own clip files (a renamed unpack, made here from the vanilla forge's
    /// smallest clips) is refused by every writer by the table of the game's clips alone, no install looked at: the record writer, the
    /// plain files, the export's files and its guard, the settings, a picture's path, the package's plan and build (as save folder and as
    /// the package's own folder), and the command-line harness's soak and rebuild soak logs, hash table, pose and edit census, each before
    /// it opens anything; the folder is left as it was, and the message says what was found and the way out. Allowed: an ordinary folder
    /// inside it, 1,000 edited files of the same names, the tool's marker in it, and 999 of the game's files. The index doesn't count for
    /// the table (leading zeros, or game files of another date); a name spelt in another case is counted by an open install. As a
    /// package's "Original clips" folder, right inside a folder carrying the marker, it is allowed.</summary>
    [GameDataFact]
    public void TheGamesOwnFilesInAFolderOfAnotherNameAreRefused()
    {
        var g = f.Install;
        int count = Core.IO.FolderGuard.UnpackedRecordFiles;
        Assert.Equal(1000, count);
        var clips = SmallGameClips(g, count);
        Assert.True(g.Clips.Max(c => c.Bytes.Length) < 64 << 20, "a clip longer than the guard reads");
        string root = Path.Combine(AppContext.BaseDirectory, "unpacked-" + Guid.NewGuid().ToString("N"));
        string renamed = Path.Combine(root, "renamed unpack"), edits = Path.Combine(root, "my edits");
        string Name(ClipEntry c) => RecordFile.ClipFileName(c.Index, c.Name, c.Id);
        try
        {
            Directory.CreateDirectory(renamed);
            Directory.CreateDirectory(edits);
            foreach (var c in clips)
            {
                File.WriteAllBytes(Path.Combine(renamed, Name(c)), RecordFile.Content(c.Bytes.Span));
                File.WriteAllBytes(Path.Combine(edits, Name(c)), RecordFile.Content(Edited(c)));
            }
            var one = clips[0];
            var protect = PackageBuild.Protected(g);
            string advice = Core.IO.FolderGuard.UnpackedDatafileAdvice;
            string found = $"the folder {renamed} holds 1,000 or more of the game's own clip files, as an unpacked animation datafile does; {advice}";
            var before = Snapshot(renamed);
            Core.IO.FolderGuard.InstallsIgnoredForTest = true;
            try
            {
                Assert.Empty(Core.IO.FolderGuard.Installs());
                Assert.Equal(found, Core.IO.FolderGuard.UnpackedDatafileRefusal(renamed));
                Assert.Contains(found, Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(g, renamed, [(one, Edited(one))], overwrite: true)).Message);
                Assert.Contains(found, RecordFileWriter.OutputRefusal(renamed, protect));
                Assert.Contains(found, Assert.Throws<RecordFileException>(() => RecordFileWriter.WriteFiles(renamed, protect, [("a.checks.txt", [1])])).Message);
                Assert.Contains(found, Assert.Throws<Core.Export.ExportException>(() => Core.Export.ClipExport.WriteFile(Path.Combine(renamed, "x.png"), protect, s => s.WriteByte(1))).Message);
                Assert.Throws<Core.Export.ExportException>(() => Core.Export.ClipExport.Write(Path.Combine(renamed, "c.glb"), [1], Path.Combine(renamed, "c.grbanim.json"), [2], protect));
                Assert.Throws<Core.Export.ExportException>(() => Core.Export.ClipExport.Guard(g, Path.Combine(renamed, "c.glb")));
                Assert.Throws<IOException>(() => new ViewerSettings { GameFolder = g.GameFolder }.Save(Path.Combine(renamed, "settings.json"), protect));
                Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", GameData.Game!, "--forge", GameData.Forge!, "--screenshot", Path.Combine(renamed, "x.png")]));
                var items = new List<PackageItem> { Item(one, Edited(one)) };
                var asSave = PackageBuild.Plan(g, items, "pack", renamed);
                Assert.Contains(found, asSave.Problem);
                Assert.Throws<RecordFileException>(() => PackageBuild.Build(g, asSave with { Problem = null }, items, replace: true));
                Assert.Contains(found, PackageBuild.Plan(g, items, "renamed unpack", root).Problem);
                // the command-line harness: every writer of a file refuses it before it opens the install or reads the forge
                string game = GameData.Game!, forge = GameData.Forge!;
                Assert.Equal(1, Cli("soak", game, "--forge", forge, "--passes", "1", "--minutes", "0.001", "--log", Path.Combine(renamed, "soak.log")));
                Assert.Equal(1, Cli("soak", game, "--forge", forge, "--rebuild", "--passes", "1", "--minutes", "0.001", "--log", Path.Combine(renamed, "soak-rebuild.log")));
                Assert.Equal(1, Cli("hash-table", forge, "--game", game, "--out", Path.Combine(renamed, "hashes.txt")));
                Assert.Equal(1, Cli("pose", game, "--forge", forge, "--out", Path.Combine(renamed, "pose.json")));
                Assert.Equal(1, Cli("census-edits", game, "--forge", forge, "--out", Path.Combine(renamed, "census.txt")));
                Assert.Equal(before, Snapshot(renamed));
                // the index isn't the table's: leading zeros (or game files of another date) are still the game's files
                foreach (var c in clips) File.Move(Path.Combine(renamed, Name(c)), Path.Combine(renamed, $"{c.Index:D6}_-_{c.Name}{RecordFile.ClipExtension}"));
                Assert.Equal(found, Core.IO.FolderGuard.UnpackedDatafileRefusal(renamed));
                // a name spelt in another case: the table can't rebuild the game's record from it
                foreach (var c in clips)
                    File.Move(Path.Combine(renamed, $"{c.Index:D6}_-_{c.Name}{RecordFile.ClipExtension}"), Path.Combine(renamed, $"{c.Index:D6}_-_{c.Name}{RecordFile.ClipExtension}".ToLowerInvariant() + ".tmp"));
                foreach (var t in Directory.GetFiles(renamed, "*.tmp")) File.Move(t, t[..^4]);
                Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(renamed));
            }
            finally { Core.IO.FolderGuard.InstallsIgnoredForTest = false; }
            // ... but the open install, which takes names in any case, counts them
            Assert.Contains(g, Core.IO.FolderGuard.Installs());
            Assert.Equal(found, Core.IO.FolderGuard.UnpackedDatafileRefusal(renamed));
            foreach (var p in Directory.GetFiles(renamed)) File.Delete(p);
            foreach (var c in clips) File.WriteAllBytes(Path.Combine(renamed, Name(c)), RecordFile.Content(c.Bytes.Span));
            // an ordinary folder inside it is allowed, empty or new
            string inner = Path.Combine(renamed, "mine");
            Directory.CreateDirectory(inner);
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(inner));
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(Path.Combine(renamed, "new")));
            RecordFileWriter.Write(g, inner, [(one, Edited(one))]);
            // 1,000 edited files of the same names are the player's own: allowed, also rewritten
            RecordFileWriter.Write(g, edits, [(one, Edited(one))], overwrite: true);
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(edits));
            // the tool's marker (an earlier version's local undo folder's) in it: allowed
            File.WriteAllText(Path.Combine(renamed, PackageBuild.MarkerName), PackageBuild.MarkerText("x - local undo (do not share)", DateTime.UtcNow, restore: true));
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(renamed));
            File.Delete(Path.Combine(renamed, PackageBuild.MarkerName));
            // as a package's "Original clips" folder, right inside a folder carrying the marker: allowed; the same name elsewhere isn't
            string pack = Path.Combine(root, "pack"), inPack = Path.Combine(pack, "Original clips");
            Directory.CreateDirectory(pack);
            Directory.Move(renamed, inPack);
            Assert.Contains("holds 1,000 or more of the game's own clip files", Core.IO.FolderGuard.UnpackedDatafileRefusal(inPack));
            File.WriteAllText(Path.Combine(pack, PackageBuild.MarkerName), PackageBuild.MarkerText("pack", DateTime.UtcNow, restore: false));
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(inPack));
            Directory.Move(inPack, renamed);
            // one file fewer than the rule's count: allowed
            File.Delete(Path.Combine(renamed, Name(one)));
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(renamed));
            Assert.Null(RecordFileWriter.OutputRefusal(renamed, protect));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>A folder the tool fills itself with the game's own clips written unchanged (as the harness's write with nothing replaced
    /// and an import of an unedited export give them) stays usable up to the content rule's count: 999 written at once, then the 1,000th
    /// beside them, are allowed; then the folder holds as many of the game's files as the rule counts, and a further write is refused,
    /// saying what was found and the way out.</summary>
    [GameDataFact]
    public void AFolderTheToolFillsWithTheGamesOwnClipsStaysUsableUpToTheCount()
    {
        var g = f.Install;
        int count = Core.IO.FolderGuard.UnpackedRecordFiles;
        var clips = SmallGameClips(g, count);
        string own = Path.Combine(AppContext.BaseDirectory, "filled-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(count - 1, RecordFileWriter.Write(g, own, clips.Take(count - 1).Select(c => (c, c.Bytes.ToArray())).ToList()).Files.Count);
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(own));
            // the 1,000th beside them
            Assert.Single(RecordFileWriter.Write(g, own, [(clips[^1], clips[^1].Bytes.ToArray())], overwrite: true).Files);
            var ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(g, own, [(clips[0], clips[0].Bytes.ToArray())], overwrite: true));
            Assert.Contains($"the folder {own} holds 1,000 or more of the game's own clip files, as an unpacked animation datafile does; {Core.IO.FolderGuard.UnpackedDatafileAdvice}", ex.Message);
            Assert.Equal(count, Directory.GetFiles(own).Length);

            // the count kept for the folder follows its listing: one file moved out (into a folder below, which isn't counted), the
            // folder is allowed; moved back, refused again
            string first = Path.Combine(own, RecordFile.ClipFileName(clips[0].Index, clips[0].Name, clips[0].Id));
            string below = Path.Combine(own, "below", Path.GetFileName(first));
            Directory.CreateDirectory(Path.GetDirectoryName(below)!);
            File.Move(first, below);
            Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(own));
            File.Move(below, first);
            Assert.NotNull(Core.IO.FolderGuard.UnpackedDatafileRefusal(own));
            // and the installs looked at: names spelt in lower case are the game's files only to an open install, so the folder is
            // refused while it is looked at, allowed while no install is (as with none open), and refused again once it is
            foreach (var p in Directory.GetFiles(own, "*" + RecordFile.ClipExtension))
            {
                string name = Path.GetFileName(p);
                File.Move(p, Path.Combine(own, name[..^RecordFile.ClipExtension.Length].ToLowerInvariant() + RecordFile.ClipExtension));
            }
            Assert.NotNull(Core.IO.FolderGuard.UnpackedDatafileRefusal(own));
            Core.IO.FolderGuard.InstallsIgnoredForTest = true;
            try { Assert.Null(Core.IO.FolderGuard.UnpackedDatafileRefusal(own)); }
            finally { Core.IO.FolderGuard.InstallsIgnoredForTest = false; }
            Assert.NotNull(Core.IO.FolderGuard.UnpackedDatafileRefusal(own));
        }
        finally { if (Directory.Exists(own)) Directory.Delete(own, true); }
    }

    /// <summary>The installs the guard knows: the install is noted when it opens, and forgotten as its Dispose forgets it; noting and
    /// forgetting on two threads while six others read the list loses nothing and throws nothing.</summary>
    [GameDataFact]
    public void TheGuardKnowsTheOpenInstalls()
    {
        var g = f.Install;
        Assert.Contains(g, Core.IO.FolderGuard.Installs());
        try
        {
            Core.IO.FolderGuard.Forget(g);
            Assert.DoesNotContain(g, Core.IO.FolderGuard.Installs());
            Core.IO.FolderGuard.Remember(g);
            Core.IO.FolderGuard.Remember(g);
            Core.IO.FolderGuard.Forget(g);
            Assert.DoesNotContain(g, Core.IO.FolderGuard.Installs());
            int errors = 0;
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                for (int k = 0; k < 500; k++)
                    try
                    {
                        if (i < 2) { Core.IO.FolderGuard.Remember(g); Core.IO.FolderGuard.Forget(g); }
                        else _ = Core.IO.FolderGuard.Installs().Count;
                    }
                    catch (Exception) { Interlocked.Increment(ref errors); }
            });
            Assert.Equal(0, errors);
            Assert.DoesNotContain(g, Core.IO.FolderGuard.Installs());
        }
        finally { Core.IO.FolderGuard.Remember(g); }
        Assert.Single(Core.IO.FolderGuard.Installs(), x => ReferenceEquals(x, g));
    }

    /// <summary>A clip a record file names unchanged, whose record reads to its end.</summary>
    static bool Writable(ClipEntry x) => x.Record is { WalkedToEnd: true } && RecordFile.NameRuleBroken(x.Name, x.Id) is null && RecordFile.HeadRuleBroken(x.Bytes.Span) is null;

    static string FileName(ClipEntry c) => RecordFile.ClipFileName(c.Index, c.Name, c.Id);

    /// <summary>A package's own file of a clip: <c>1_-_name.Animation</c>, loaded before the game's own file.</summary>
    static string PackageFile(ClipEntry c) => RecordFile.ClipFileName(PackageBuild.PackageFileIndex, c.Name, c.Id);

    /// <summary>Three clips, each as game files (<paramref name="g"/>, the vanilla forge) and as <paramref name="installed"/> (the game
    /// folder's own forge, read only, where other packages' clips are installed) hold it: <c>Y</c> the game's own on game files but not on
    /// that install (another package's clip there), <c>X</c> and <c>W</c> the game's own on both.</summary>
    static ((ClipEntry G, ClipEntry I) X, (ClipEntry G, ClipEntry I) Y, (ClipEntry G, ClipEntry I) W) ThreeClips(GameInstall g, GameInstall installed)
    {
        var y = installed.Clips.FirstOrDefault(c => c.Status == ClipStatus.Replaced && Writable(c) && g.FindClip(c.Id) is { Status: ClipStatus.Vanilla } v && Writable(v));
        Assert.True(y is not null, "the game folder's patch forge holds no clip of the game's that isn't the game's own");
        var own = installed.Clips.Where(c => c.Id != y!.Id && c.Status == ClipStatus.Vanilla && Writable(c) && g.FindClip(c.Id) is { Status: ClipStatus.Vanilla } v && Writable(v))
            .DistinctBy(c => c.Index).Take(2).ToList();
        Assert.Equal(2, own.Count);
        return ((g.FindClip(own[0].Id)!, own[0]), (g.FindClip(y!.Id)!, y), (g.FindClip(own[1].Id)!, own[1]));
    }

    /// <summary>An edit that changes nothing, on a clip the install holds from another package (the game folder's own forge, read only):
    /// a clip swapped with itself, or edited by a factor of 1.00 (its speed, else its root-motion scale), rebuilds to the install's own
    /// record. The Edit screen's reason, Add's
    /// refusal, the plan of a package holding such an edit and the harness's edit all say it is an edit that changes nothing, with no
    /// advice to verify the game files; only an import of the same record gets the import's way out, and a package's import the build's.</summary>
    [GameDataFact]
    public void AnEditThatChangesNothingIsSaidAsAnEditOnAnInstallThatHoldsAPackagesClip()
    {
        using var installed = GameInstall.Open(GameData.Game!);
        var ctx = new EditContext(installed.Skeleton, installed.SharedTable, null);
        bool SwapsToItself(ClipEntry x)
        {
            try { return EditPackage.Run(x.Record!, ctx, "swap", out _, source: x.Record!).Bytes.AsSpan().SequenceEqual(x.Bytes.Span); }
            catch (EditRefusedException) { return false; }
        }
        // Hello when another package's clip of it is installed, else the first such clip a swap with itself rebuilds byte for byte
        var c = new[] { installed.FindClip("mca_emote_Hello") }.Concat(installed.Clips.Where(x => x.Status == ClipStatus.Replaced).Take(40))
            .FirstOrDefault(x => x is { Status: ClipStatus.Replaced } && Writable(x) && SwapsToItself(x));
        Assert.True(c is not null, "the game folder's patch forge holds no clip of another package's that a swap with itself rebuilds");
        var self = EditPackage.Run(c!.Record!, ctx, "swap", out _, source: c.Record!);
        Assert.Equal(c.Bytes.ToArray(), self.Bytes);
        // a factor of 1.00, as the screen's sliders allow: the speed (where the graph leaves the clip's length free), else the scale
        var withGraph = new EditContext(installed.Skeleton, installed.SharedTable, G.Verdict(c.Id));
        EditedClip? factorOne = null;
        foreach (var edit in new[] { "speed", "scale" })
        {
            try { factorOne = EditPackage.Run(c.Record!, withGraph, edit, out _, factor: 1.0); }
            catch (EditRefusedException) { continue; }
            break;
        }
        Assert.True(factorOne is not null, $"{c.Name} takes neither a speed nor a scale");
        Assert.Equal(c.Bytes.ToArray(), factorOne!.Bytes);
        const string Edit = "this edit changes nothing: the result is the clip as your game folder already holds it";
        Assert.Equal(Edit, PackageEntries.EditChangesNothing);
        Assert.Equal(Edit, PackageEntries.Unchanged(installed, c, self.Bytes, PackageItemKind.Edit));
        Assert.Equal(Edit, PackageEntries.Unchanged(installed, c, factorOne.Bytes, PackageItemKind.Edit));
        Assert.DoesNotContain("verify", Edit);
        // a package holding it as an edit: refused as an edit that changes nothing; as an import: the build's way out
        string save = Path.Combine(AppContext.BaseDirectory, "noop-" + Guid.NewGuid().ToString("N"));
        var asEdit = new PackageItem(c, self.Bytes, ClipChange.Swap(c.Name), CheckLevel.Pass, "", DateTime.Now);
        Assert.Equal($"{c.Name}: {Edit}. Remove it from the package.", PackageBuild.Plan(installed, [asEdit], "Mine", save).Problem);
        Assert.Equal($"{c.Name}: {PackageEntries.NotTheGamesOwn}{PackageEntries.InstallHoldsIt}.", PackageBuild.Plan(installed, [Item(c, self.Bytes)], "Mine", save).Problem);
        // the import of the same record: the import's own way out, an import on game files that don't hold it
        string import = PackageEntries.Unchanged(installed, c, self.Bytes)!;
        Assert.Equal(PackageEntries.NotTheGamesOwn + PackageEntries.ImportInstallHoldsIt, import);
        Assert.Contains("import it on game files that don't hold it (verify the game files or put your forge backup back first)", import);
        Assert.DoesNotContain("build from", import);
        Assert.False(Directory.Exists(save));
        // the harness's edit on that install (its own forge): refused with the edit's reason, nothing written
        string root = Path.Combine(AppContext.BaseDirectory, "cli-noop-" + Guid.NewGuid().ToString("N"));
        var was = Console.Error;
        var err = new StringWriter();
        try
        {
            Console.SetError(TextWriter.Synchronized(err));
            Assert.Equal(1, Cli("edit", GameData.Game!, "--clip", c.Name, "--edit", "swap", "--source", c.Name, "--out", Path.Combine(root, "swap")));
        }
        finally { Console.SetError(was); }
        Assert.False(Directory.Exists(root));
        string said = err.ToString();
        Assert.Contains($"refused (unchanged): {c.Name}: {Edit}. Nothing was written.", said);
        Assert.DoesNotContain("verify", said);
    }

    /// <summary>Every writer of the command-line harness refuses a folder that is, or lies inside, a package or a local undo folder of this
    /// tool, writing nothing there: write (the game's own record rebuilt with nothing replaced), import (with --allow-unchanged too), edit,
    /// export, export-skeleton and pose. Write still writes the game's own record into an ordinary folder: it is a tool for tests, not
    /// shipped with the program.</summary>
    [GameDataFact]
    public void TheCommandLinesWritersRefuseAFolderInsideAPackage()
    {
        var g = f.Install;
        var hello = g.FindClip("mca_emote_Hello")!;
        var pickup = g.FindClip("mch_pickup_passL_TO_PassR")!;
        string root = Path.Combine(AppContext.BaseDirectory, "cli-pack-" + Guid.NewGuid().ToString("N"));
        string game = GameData.Game!, forge = GameData.Forge!;
        try
        {
            string pkg = Path.Combine(root, "Pack"), undo = Path.Combine(root, "Pack - local undo (do not share)");
            Directory.CreateDirectory(pkg);
            Directory.CreateDirectory(undo);
            File.WriteAllText(Path.Combine(pkg, PackageBuild.MarkerName), PackageBuild.MarkerText("Pack", DateTime.UtcNow, restore: false));
            File.WriteAllText(Path.Combine(undo, PackageBuild.MarkerName), PackageBuild.MarkerText("Pack - local undo (do not share)", DateTime.UtcNow, restore: true));
            string glb = Path.Combine(root, Core.Export.ClipExport.FileNameFor(hello.Name));
            Assert.True(Core.Export.ClipExport.Clip(g, hello, glb).Check.Passed);
            string before = Tree(pkg) + "\n" + Tree(undo);
            foreach (var dir in new[] { pkg, undo, Path.Combine(undo, "sub") })
            {
                Assert.Equal(1, Cli("write", game, "--forge", forge, "--clip", hello.Name, "--out", dir, "--overwrite"));
                Assert.Equal(1, Cli("import", game, "--forge", forge, "--glb", glb, "--clip", hello.Name, "--out", dir, "--allow-unchanged", "--overwrite"));
                Assert.Equal(1, Cli("edit", game, "--forge", forge, "--clip", pickup.Name, "--edit", "mirror", "--out", dir, "--overwrite"));
                Assert.Equal(1, Cli("export", game, "--forge", forge, "--clip", hello.Name, "--out", dir));
                Assert.Equal(1, Cli("export-skeleton", game, "--forge", forge, "--out", dir));
                Assert.Equal(1, Cli("pose", game, "--forge", forge, "--out", Path.Combine(dir, "pose.json")));
            }
            Assert.Equal(before, Tree(pkg) + "\n" + Tree(undo));
            Assert.Equal(new[] { PackageBuild.MarkerName }, Directory.GetFileSystemEntries(undo).Select(Path.GetFileName));
            // an ordinary folder: write gives the game's own record, as a test tool may
            string plain = Path.Combine(root, "plain");
            Assert.Equal(0, Cli("write", game, "--forge", forge, "--clip", hello.Name, "--out", plain));
            Assert.Equal(RecordFile.Content(hello.Bytes.Span), File.ReadAllBytes(Path.Combine(plain, RecordFile.ClipFileName(hello.Index, hello.Name, hello.Id))));
            // the record file alone: no install note is written
            Assert.Equal(new[] { RecordFile.ClipFileName(hello.Index, hello.Name, hello.Id) }, Directory.GetFileSystemEntries(plain).Select(Path.GetFileName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>The command-line harness's import and edit refuse a result that changes nothing (the tool's own export imported back
    /// untouched; a clip swapped with itself), writing nothing, unless --allow-unchanged; a changed import still goes through.</summary>
    [GameDataFact]
    public void TheCommandLinesImportAndEditRefuseAResultThatChangesNothing()
    {
        var g = f.Install;
        var hello = g.FindClip("mca_emote_Hello")!;
        string root = Path.Combine(AppContext.BaseDirectory, "cli-same-" + Guid.NewGuid().ToString("N"));
        string game = GameData.Game!, forge = GameData.Forge!;
        try
        {
            Directory.CreateDirectory(root);
            string glb = Path.Combine(root, Core.Export.ClipExport.FileNameFor(hello.Name));
            Assert.True(Core.Export.ClipExport.Clip(g, hello, glb).Check.Passed);
            // the import of the export itself: the game's own record, refused, nothing written
            string refused = Path.Combine(root, "import-refused");
            Assert.Equal(1, Cli("import", game, "--forge", forge, "--glb", glb, "--clip", hello.Name, "--out", refused));
            Assert.False(Directory.Exists(refused));
            string allowed = Path.Combine(root, "import-allowed");
            Assert.Contains(Cli("import", game, "--forge", forge, "--glb", glb, "--clip", hello.Name, "--out", allowed, "--allow-unchanged"), new[] { 0, 3 });
            string file = Path.Combine(allowed, RecordFile.ClipFileName(hello.Index, hello.Name, hello.Id));
            Assert.Equal(RecordFile.Content(hello.Bytes.Span), File.ReadAllBytes(file));
            // a clip swapped with itself rebuilds to its own record: refused the same way
            var self = EditPackage.Run(hello.Record!, new EditContext(g.Skeleton, g.SharedTable, null), "swap", out _, source: hello.Record!);
            Assert.Equal(hello.Bytes.ToArray(), self.Bytes);
            string editRefused = Path.Combine(root, "edit-refused");
            Assert.Equal(1, Cli("edit", game, "--forge", forge, "--clip", hello.Name, "--edit", "swap", "--source", hello.Name, "--out", editRefused));
            Assert.False(Directory.Exists(editRefused));
            string editAllowed = Path.Combine(root, "edit-allowed");
            Assert.Equal(0, Cli("edit", game, "--forge", forge, "--clip", hello.Name, "--edit", "swap", "--source", hello.Name, "--out", editAllowed, "--allow-unchanged"));
            Assert.Equal(RecordFile.Content(hello.Bytes.Span), File.ReadAllBytes(Path.Combine(editAllowed, RecordFile.ClipFileName(hello.Index, hello.Name, hello.Id))));
            // an edit that changes the clip goes through as ever
            var pickup = g.FindClip("mch_pickup_passL_TO_PassR")!;
            string mirrored = Path.Combine(root, "edit-mirror");
            Assert.Equal(0, Cli("edit", game, "--forge", forge, "--clip", pickup.Name, "--edit", "mirror", "--out", mirrored));
            Assert.NotEqual(RecordFile.Content(pickup.Bytes.Span), File.ReadAllBytes(Path.Combine(mirrored, RecordFile.ClipFileName(pickup.Index, pickup.Name, pickup.Id))));
            // the import writes the record file and its check report, the edit the record file alone: no install note
            Assert.Equal(new[] { $"{hello.Index}_-_{hello.Name}.checks.txt", RecordFile.ClipFileName(hello.Index, hello.Name, hello.Id) }.Order(StringComparer.Ordinal),
                Directory.GetFileSystemEntries(allowed).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal(new[] { RecordFile.ClipFileName(pickup.Index, pickup.Name, pickup.Id) }, Directory.GetFileSystemEntries(mirrored).Select(Path.GetFileName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
