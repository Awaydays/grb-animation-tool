using System.Security.Cryptography;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.IO;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>The clips of every datafile beside the animation one, on the fake install: listed one row a clip with their datafiles, their
/// records read only when needed; each on its own skeleton (the body, an animal's, or none the tool can name); Replace allowed between
/// clips of one skeleton and refused across, naming both; the datafiles' cache; a package placing such a clip in its datafile's own
/// folder (and in the other datafile holding it); the guard letting a package hold such folders and nothing else; and a small datafile's
/// unpacked folder with a 1_-_ file the game doesn't load.</summary>
[Collection("install")]
public sealed class OtherDatafileTests(InstallFixture f)
{
    /// <summary>A standing civilian smoking: its own one-clip datafile (record 0) and a tag set's (record 1).</summary>
    const string Smoker = "CivM_Act_day_StandSmoking_col_std_03";
    const string Sitter = "CivM_Act_day_SitSmoking_Anxious_col_sit_02";

    static string Scratch(string what) => Path.Combine(AppContext.BaseDirectory, $"{what}-{Guid.NewGuid().ToString("N")[..8]}");

    [GameDataFact]
    public void EveryDatafileWithClipsIsListedItsRecordsReadWhenNeeded()
    {
        var g = f.Install;
        Assert.NotNull(g.Others);
        Assert.True(g.Others!.Datafiles.Count > 2000, $"{g.Others.Datafiles.Count} other datafiles");
        Assert.Contains(g.Others.Datafiles, d => !d.InPatch);
        Assert.Equal(g.Clips.Count + g.OtherClips.Count, g.AllClips.Count);
        // the animation datafile's clips stay as they were: no other row shares an ID with them
        var katya = g.Clips.Select(c => c.Id).ToHashSet();
        Assert.DoesNotContain(g.OtherClips, c => katya.Contains(c.Id));
        // one row a clip: the smoker at its own datafile, the tag set's copy in AlsoIn
        var s = Assert.Single(g.OtherClips, c => c.Name == Smoker);
        Assert.Equal((Smoker, 0, true), (s.DatafileName, s.Index, s.InPatchForge));
        var also = Assert.Single(s.AlsoIn);
        Assert.StartsWith("LoadTagAnimationsSet_", also.Name);
        Assert.Equal(1, also.RecordIndex);
        Assert.Equal(ClipSkeletonKind.Body, s.OnSkeleton.Kind);
        Assert.Equal(ClipStatus.Vanilla, s.Status);
        // what the list shows is known before the record is read
        var other = g.OtherClips.First(c => c.Name.StartsWith("CivF_Act_day_StandSmoking", StringComparison.Ordinal));
        Assert.False(other.Loaded);
        Assert.Equal(1200, other.LastTick);
        Assert.False(other.Loaded);
        // read when asked for: the record of its place, the game's own bytes
        Assert.Equal(1200, s.Record!.LastTick);
        Assert.True(s.Loaded);
        Assert.Equal(ClipStatus.Vanilla, g.Vanilla!.StatusOf(s.Id, s.Bytes.Span));
        Assert.Same(s, g.FindClip(s.Id));
        Assert.Same(s, g.FindClip(Smoker));
    }

    [GameDataFact]
    public void EachClipIsShownAndExportedOnItsOwnSkeleton()
    {
        var g = f.Install;
        var own = g.OtherClips.First(c => !c.FromFile && c.OnSkeleton.Kind == ClipSkeletonKind.Own && c.Record is { WalkedToEnd: true, LastTick: > 10 });
        var sk = g.SkeletonOf(own);
        Assert.NotNull(sk);
        Assert.NotSame(g.Skeleton, sk);
        // its own skeleton holds every bone it keys (the control: the body holds hardly any of them)
        var bones = ClipSkeletons.BoneTracks(own.Record!, g.SharedTable);
        Assert.All(bones, b => Assert.True(sk!.Contains(b)));
        Assert.True(ClipSkeletons.Shared(bones, g.Skeleton) < ClipSkeletons.MapShare);
        Assert.Same(sk, g.CreateSampler(own).Skeleton);
        Assert.DoesNotContain("can't", g.SkeletonName(own.OnSkeleton));
        string dir = Scratch("other-export");
        try
        {
            var r = ClipExport.Clip(g, own, Path.Combine(dir, ClipExport.FileNameFor(own.Name)));
            Assert.True(r.Check.Passed, r.Check.ToString());
            // a body clip of another datafile exports on the body
            var smoker = g.FindClip(Smoker)!;
            Assert.True(ClipExport.Clip(g, smoker, Path.Combine(dir, ClipExport.FileNameFor(smoker.Name))).Check.Passed);
            // a clip whose skeleton the tool can't name: listed, not exported, not edited
            var unknown = g.OtherClips.First(c => !c.FromFile && c.OnSkeleton.Kind == ClipSkeletonKind.Unknown && c.Record is { WalkedToEnd: true });
            Assert.Null(g.SkeletonOf(unknown));
            Assert.Throws<ExportException>(() => ClipExport.Clip(g, unknown, Path.Combine(dir, "unknown.glb")));
            Assert.Contains("can't determine", g.ReplaceProblem(unknown, null));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void ReplaceGoesBetweenClipsOfOneSkeletonAndIsRefusedAcross()
    {
        var g = f.Install;
        var smoker = g.FindClip(Smoker)!;
        var sitter = g.FindClip(Sitter)!;
        var wade = g.FindClip("mcm_ar_fm_std_wade_low_V0_01")!;
        // the body to the body: a civilian's clip from another civilian's or from the player's
        Assert.Null(g.ReplaceProblem(smoker, sitter));
        Assert.Null(g.ReplaceProblem(smoker, wade));
        Assert.Null(g.ReplaceProblem(wade, smoker));
        // an animal's clip and a person's: refused both ways, each skeleton named
        var animal = g.OtherClips.First(c => !c.FromFile && c.OnSkeleton.Kind == ClipSkeletonKind.Own && c.Record is { WalkedToEnd: true });
        string across = g.ReplaceProblem(smoker, animal)!;
        Assert.Contains("the human body", across);
        Assert.Contains(g.SkeletonName(animal.OnSkeleton), across);
        Assert.NotNull(g.ReplaceProblem(animal, smoker));
        // two clips of that animal's skeleton map onto each other
        if (g.OtherClips.FirstOrDefault(c => !c.FromFile && c.Id != animal.Id && c.OnSkeleton == animal.OnSkeleton) is { } twin) Assert.Null(g.ReplaceProblem(animal, twin));
        // the replace itself, the civilian's own record kept: its ID, length and track list, no game check failing
        var sk = g.SkeletonOf(smoker)!;
        var r = ClipReplace.FromClip(sk, g.SharedTable, smoker, sitter, new FitOptions { Mode = FitMode.Cut, CutStart = 100, Loop = true });
        Assert.Equal((smoker.Id, smoker.LastTick), (r.Rebuilt.Record.Id, r.Rebuilt.Record.LastTick));
        Assert.Equal(smoker.Record!.Tracks, r.Rebuilt.Record.Tracks);
        Assert.NotEqual(Core.Checks.CheckLevel.Fail, r.Report.Verdict);
        // the set Replace refuses a set across skeletons
        Assert.Throws<ImportRefusedException>(() => ClipSetReplace.Run(g, null, [smoker, animal], [SetSource.Of(sitter)], new SetReplaceOptions { Fit = new FitOptions { Mode = FitMode.Hold } }));
    }

    [GameDataFact]
    public void TheDatafilesAreReadThroughTheirCacheKeptByEachPayloadsHash()
    {
        string cache = Scratch("datafile-cache");
        try
        {
            OtherDatafiles Open() { using var g = GameInstall.Open(GameData.Game!, GameData.Forge!, "", cache, ""); return g.Others!; }
            var first = Open();
            Assert.Equal(Core.Index.CacheOutcome.Built, first.Outcome);
            Assert.True(first.Decoded > 2000);
            var again = Open();
            Assert.Equal(Core.Index.CacheOutcome.Reused, again.Outcome);
            Assert.Equal(0, again.Decoded);
            Assert.Equal(first.Datafiles.Count, again.Datafiles.Count);
            string file = Directory.GetFiles(cache, "datafiles-*.json").Single();
            string json = File.ReadAllText(file);
            // the forge's stamp changed: every payload hashed again, and none decoded, their hashes the same
            File.WriteAllText(file, json.Replace("\"patch\":{\"length\":", "\"patch\":{\"length\":1"));
            var stamped = Open();
            Assert.Equal(Core.Index.CacheOutcome.RebuiltChanged, stamped.Outcome);
            Assert.Equal(0, stamped.Decoded);
            // the control: one datafile's hash not its payload's any more, and the stamp changed again: that one alone is decoded
            json = File.ReadAllText(file);
            string sha = first.Datafiles[0].Sha256;
            File.WriteAllText(file, json.Replace(sha, new string('0', 64)).Replace("\"patch\":{\"length\":", "\"patch\":{\"length\":2"));
            Assert.Equal(1, Open().Decoded);
            // a damaged cache: read again from the forges
            File.WriteAllText(file, "{ not json");
            Assert.Equal(Core.Index.CacheOutcome.RebuiltUnreadable, Open().Outcome);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    /// <summary>A package of the civilian's clip: its file in its own datafile's folder under the record's own number (record 0: 1_-_
    /// wouldn't load first), and in the tag set's folder as 1_-_ (record 1); each original in the originals folder's copy of its datafile's
    /// folder; a clip of the animation datafile beside them as ever, 1_-_ at the top. Built again over itself; and refused while a row
    /// fails a game check, the row named.</summary>
    [GameDataFact]
    public void AnotherDatafilesClipGoesInItsOwnFolderInThePackage()
    {
        var g = f.Install;
        var smoker = g.FindClip(Smoker)!;
        var r = ClipReplace.FromClip(g.SkeletonOf(smoker)!, g.SharedTable, smoker, g.FindClip(Sitter)!, new FitOptions { Mode = FitMode.Cut, CutStart = 100, Loop = true });
        var idle = g.FindClip("fm_std_V0_idle_cool_01")!;
        var r2 = ClipReplace.FromClip(g.Skeleton, g.SharedTable, idle, g.FindClip("mcm_ar_fm_std_wade_low_V0_01")!, new FitOptions { Mode = FitMode.Cut, CutStart = 674, Loop = true });
        var items = new[] { r, r2 }.Select(x => new PackageItem(x.Slot, x.Record, Core.Library.ClipChange.Replaced("sit"), x.Report.Verdict, x.ReportText(), DateTime.Now)).ToList();
        string dir = Scratch("other-pkg");
        try
        {
            var plan = PackageBuild.Plan(g, items, "Smoker", dir);
            Assert.Null(plan.Problem);
            string own = RecordFile.DatafileFolderName(smoker.DatafileIndex, smoker.DatafileName), tag = smoker.AlsoIn[0].FolderName;
            Assert.Contains(plan.Files, x => x.Name == Path.Combine(own, $"0_-_{Smoker}.Animation"));
            Assert.Contains(plan.Files, x => x.Name == Path.Combine(tag, $"1_-_{Smoker}.Animation"));
            for (int pass = 0; pass < 2; pass++)
            {
                var built = PackageBuild.Build(g, plan, items, replace: pass > 0);
                string top = built.Folder;
                Assert.Equal(r.Record, RecordFile.ToRecord(File.ReadAllBytes(Path.Combine(top, own, $"0_-_{Smoker}.Animation")), Smoker));
                Assert.Equal(r.Record, RecordFile.ToRecord(File.ReadAllBytes(Path.Combine(top, tag, $"1_-_{Smoker}.Animation")), Smoker));
                Assert.True(File.Exists(Path.Combine(top, $"1_-_{idle.Name}.Animation")));
                var orig = RecordFile.ToRecord(File.ReadAllBytes(Path.Combine(top, PackageBuild.OriginalsFolderName, own, $"0_-_{Smoker}.Animation")), Smoker);
                Assert.Equal(smoker.Bytes.ToArray(), orig);
                Assert.True(File.Exists(Path.Combine(top, PackageBuild.OriginalsFolderName, tag, $"1_-_{Smoker}.Animation")));
                // no other datafile's file at the top, where the animation datafile's go
                Assert.DoesNotContain(Directory.GetFiles(top), x => x.Contains(Smoker, StringComparison.Ordinal));
            }
            // a row failing a game check (here, as a 1.1.0 package kept it: its report says FAIL) refuses the build, naming it
            var failing = items[0] with { ReportText = "Import of x into y\nFAIL  length: not the clip's length\nVERDICT: FAIL (0 pass, 0 warn, 1 fail)\n", Verdict = CheckLevel.Fail };
            var bad = PackageBuild.Plan(g, [failing, items[1]], "Smoker", dir);
            Assert.Contains(Smoker, bad.Problem);
            var ex = Assert.Throws<RecordFileException>(() => PackageBuild.Build(g, plan, [failing, items[1]], replace: true));
            Assert.Contains(Smoker, ex.Message);
            Assert.Contains("failed a game check", ex.Message);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheGuardLetsAPackageHoldADatafilesFolderAndNothingElse()
    {
        string root = Scratch("guard-datafile");
        try
        {
            string pkg = Path.Combine(root, "pkg"), plain = Path.Combine(root, "plain");
            Directory.CreateDirectory(pkg);
            Directory.CreateDirectory(plain);
            string folder = "526_-_CivM_Act_day_StandSmoking_col_std_03.data";
            // the control: a folder of a datafile's name anywhere else stays refused
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(Path.Combine(plain, folder)));
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(Path.Combine(pkg, folder)));
            File.WriteAllText(Path.Combine(pkg, PackageBuild.MarkerName), PackageBuild.MarkerText("p", DateTime.UtcNow));
            Assert.Null(FolderGuard.UnpackedDatafileRefusal(Path.Combine(pkg, folder)));
            Assert.Null(FolderGuard.UnpackedDatafileRefusal(Path.Combine(pkg, PackageBuild.OriginalsFolderName, folder)));
            // the same once the folders exist (the rule that follows where a folder really leads), and the control beside them
            Directory.CreateDirectory(Path.Combine(pkg, folder));
            Directory.CreateDirectory(Path.Combine(pkg, PackageBuild.OriginalsFolderName, folder));
            Directory.CreateDirectory(Path.Combine(plain, folder));
            Assert.Null(FolderGuard.UnpackedDatafileRefusal(Path.Combine(pkg, folder)));
            Assert.Null(FolderGuard.UnpackedDatafileRefusal(Path.Combine(pkg, PackageBuild.OriginalsFolderName, folder, "0_-_x.Animation"), file: true));
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(Path.Combine(plain, folder)));
            // one level deeper, or under another folder of the package, it is refused again
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(Path.Combine(pkg, "x", folder)));
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(Path.Combine(pkg, folder, "7_-_x.data")));
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(Path.Combine(plain, PackageBuild.OriginalsFolderName, folder)));
            // a marker inside an unpack's own tree (a package's files copied in whole, its marker too) lets nothing through
            string unpack = Path.Combine(root, "Extracted", "DataPC_patch_01.forge", "Extracted");
            Directory.CreateDirectory(Path.Combine(unpack, folder));
            File.WriteAllText(Path.Combine(unpack, PackageBuild.MarkerName), PackageBuild.MarkerText("p", DateTime.UtcNow));
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(Path.Combine(unpack, folder)));
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(Path.Combine(unpack, folder, "0_-_x.Animation"), file: true));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>A small datafile unpacked: the game's own file is record 0 (0_-_...), and a 1_-_ file of another clip's motion beside it.
    /// Both are listed as the animation datafile's files are; the 1_-_ file is said not to be used, the repack loading the 0_-_ file
    /// first, and the game's own file isn't called a mod.</summary>
    [GameDataFact]
    public void ASmallDatafilesUnpackedFolderIsRead()
    {
        var g = f.Install;
        var smoker = g.FindClip(Smoker)!;
        var r = ClipReplace.FromClip(g.SkeletonOf(smoker)!, g.SharedTable, smoker, g.FindClip(Sitter)!, new FitOptions { Mode = FitMode.Cut, CutStart = 100 });
        string root = Scratch("unpacked-small");
        try
        {
            string folder = Path.Combine(root, RecordFile.DatafileFolderName(smoker.DatafileIndex, smoker.DatafileName));
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"0_-_{Smoker}.Animation"), RecordFile.Content(smoker.Bytes.Span));
            File.WriteAllBytes(Path.Combine(folder, $"1_-_{Smoker}.Animation"), RecordFile.Content(r.Record));
            using var u = GameInstall.Open(GameData.Game!, GameData.Forge!, "", null, root);
            var rows = u.OtherClips.Where(c => c.Id == smoker.Id).ToList();
            Assert.Equal(2, rows.Count);
            var game = rows.Single(c => c.FileNumber == 0);
            var mod = rows.Single(c => c.FileNumber == 1);
            Assert.False(game.Modded);
            Assert.Equal(ClipStatus.Vanilla, game.Status);
            Assert.True(mod.FromFile);
            Assert.Equal(ClipStatus.Replaced, mod.Status);
            Assert.Contains("isn't used", mod.Note);
            Assert.Equal(smoker.DatafileName, mod.DatafileName);
            Assert.Equal(ClipSkeletonKind.Body, mod.OnSkeleton.Kind);
            Assert.Empty(u.OtherUnpackedErrors);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>The clip's copy in its second datafile, unpacked: the game's own file there (record 1 of the tag set, named 1_-_) is no
    /// row of its own and no mod; a replaced record put in its place is, marked as the tag set's file.</summary>
    [GameDataFact]
    public void AClipsCopyInItsSecondDatafileIsReadAsThatDatafilesOwn()
    {
        var g = f.Install;
        var smoker = g.FindClip(Smoker)!;
        var tag = smoker.AlsoIn[0];
        var r = ClipReplace.FromClip(g.SkeletonOf(smoker)!, g.SharedTable, smoker, g.FindClip(Sitter)!, new FitOptions { Mode = FitMode.Cut, CutStart = 100 });
        string root = Scratch("unpacked-both");
        try
        {
            string own = Path.Combine(root, RecordFile.DatafileFolderName(smoker.DatafileIndex, smoker.DatafileName));
            string tagFolder = Path.Combine(root, tag.FolderName);
            Directory.CreateDirectory(own);
            Directory.CreateDirectory(tagFolder);
            File.WriteAllBytes(Path.Combine(own, $"0_-_{Smoker}.Animation"), RecordFile.Content(smoker.Bytes.Span));
            File.WriteAllBytes(Path.Combine(tagFolder, $"1_-_{Smoker}.Animation"), RecordFile.Content(g.RecordAt(tag, smoker.Id)!));
            int Rows(out List<ClipEntry> rows)
            {
                using var u = GameInstall.Open(GameData.Game!, GameData.Forge!, "", null, root);
                rows = u.OtherClips.Where(c => c.Id == smoker.Id).ToList();
                return rows.Count;
            }
            // the game's own file in both folders: the one row, no mod
            Assert.Equal(1, Rows(out var vanillaRows));
            Assert.False(vanillaRows[0].Modded);
            Assert.Equal(ClipStatus.Vanilla, vanillaRows[0].Status);
            // a replaced record in the tag set's folder, in the game's file's place: a row of that folder, a mod
            File.WriteAllBytes(Path.Combine(tagFolder, $"1_-_{Smoker}.Animation"), RecordFile.Content(r.Record));
            Assert.Equal(2, Rows(out var modRows));
            var mod = modRows.Single(c => c.FromFile);
            Assert.True(mod.Modded);
            Assert.Equal(ClipStatus.Replaced, mod.Status);
            Assert.Equal(tag.Name, mod.DatafileName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
