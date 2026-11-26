using System.Buffers.Binary;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.Tests;

/// <summary>The unpacked datafile folder players install into by hand: a fake one, made in a temporary folder from the test forge's own
/// clips, with the game's files and the players' 1_-_ files beside them. Each kind of file gets its row (an edited 1_-_ file, one the
/// game's own byte for byte, one whose record is another clip's under this ID, one that doesn't read, one with no index), and every
/// action works on each row: export, the edits' choices, import, replace, swap and package.</summary>
[Collection("install")]
public sealed class UnpackedFolderTests(InstallFixture f) : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "grbanim-unpacked-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static byte[] Content(ClipEntry c) => RecordFile.Content(c.Bytes.Span);

    void Put(string name, byte[] content) => File.WriteAllBytes(Path.Combine(_dir, name), content);

    /// <summary>A record file's content with another ID after its head (the players' ID swap).</summary>
    static byte[] WithId(byte[] content, ulong id)
    {
        var c = (byte[])content.Clone();
        int h = c[0] == 1 ? 8 + 12 * BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(4)) : 1;
        BinaryPrimitives.WriteUInt64LittleEndian(c.AsSpan(h), id);
        return c;
    }

    sealed record Setup(GameInstall G, ClipEntry Edited, ClipEntry Same, ClipEntry Swapped, ClipEntry Unreadable, ClipEntry NoIndex, byte[] EditedFile);

    /// <summary>The fake folder and the install opened with it.</summary>
    Setup Make()
    {
        var g0 = f.Install;
        Directory.CreateDirectory(_dir);
        var readable = g0.ForgeClips.Where(c => c.Record is { WalkedToEnd: true, DurationValid: true } && c.LastTick > 20 && RecordFile.NameRuleBroken(c.Name, c.Id) is null).ToList();
        var hello = readable.First(c => c.Name == "mca_emote_Hello");
        var others = readable.Where(c => c != hello).ToList();
        ClipEntry same = others[200], swapped = others[300], swapSource = others.First(c => c.Name != swapped.Name && c.LastTick != swapped.LastTick),
            unreadable = others[400], noIndex = others[500], motion = others[600];
        // an edited record of a clip: another clip's motion put on it, as a player's mod is
        byte[] EditedOf(ClipEntry slot) => RecordFile.Content(ClipReplace.FromClip(g0.Skeleton, g0.SharedTable, slot, motion, new FitOptions { Mode = FitMode.Hold }).Record);
        var edited = EditedOf(hello);
        foreach (var c in new[] { hello, same, swapped, unreadable, noIndex }) Put(RecordFile.ClipFileName(c.Index, c.Name, c.Id), Content(c));
        Put($"1_-_{hello.Name}.Animation", edited);
        Put($"1_-_{same.Name}.Animation", Content(same));
        Put($"1_-_{swapped.Name}.Animation", WithId(Content(swapSource), swapped.Id));
        Put($"1_-_{unreadable.Name}.Animation", [1, 2, 3, 4, 5]);
        Put($"x_-_{noIndex.Name}.Animation", EditedOf(noIndex));
        var g = GameInstall.Open(GameData.Game!, GameData.Forge!, _dir);
        return new Setup(g, hello, same, swapped, unreadable, noIndex, edited);
    }

    static List<ClipEntry> RowsOf(GameInstall g, ClipEntry c) => g.Clips.Where(x => x.Id == c.Id).ToList();

    [GameDataFact]
    public void EachKindOfFileHasItsRow()
    {
        var s = Make();
        using var g = s.G;
        Assert.Equal(_dir, g.UnpackedFolderPath);
        Assert.Null(g.UnpackedFolderError);
        Assert.Equal(f.Install.ForgeClips.Count, g.ForgeClips.Count);
        Assert.Equal(4, g.Clips.Count(c => c.FromFile));

        // an edited 1_-_ file: the game's own row stays, with its file, and the mod is a row of its own, marked
        var hello = RowsOf(g, s.Edited);
        Assert.Equal(2, hello.Count);
        var game = hello.Single(c => !c.FromFile);
        Assert.Equal((ClipStatus.Vanilla, false, s.Edited.Index), (game.Status, game.Modded, game.FileNumber));
        var mod = hello.Single(c => c.FromFile);
        Assert.Equal((ClipStatus.Replaced, true, 1, 1), (mod.Status, mod.Modded, mod.Index, mod.FileNumber));
        Assert.Equal("Modded", mod.StatusText);
        Assert.Equal(s.EditedFile, RecordFile.Content(mod.Bytes.Span));
        Assert.Equal(s.Edited.LastTick, mod.LastTick);
        Assert.NotEqual(mod.Id, mod.Key);
        Assert.Equal(1UL, mod.Key >> 63);
        // the game loads the forge's row; the lists' search finds both
        Assert.Same(game, g.FindClip(s.Edited.Id));

        // a 1_-_ file that is the game's own byte for byte: one row, marked as such
        var same = Assert.Single(RowsOf(g, s.Same));
        Assert.True(same.Modded);
        Assert.Equal((ClipStatus.Vanilla, 1), (same.Status, same.FileNumber));
        Assert.Equal("Modded (vanilla)", same.StatusText);

        // a 1_-_ file whose record is another clip's under this clip's ID: read by its own content
        var swap = RowsOf(g, s.Swapped).Single(c => c.FromFile);
        Assert.Equal((ClipStatus.Replaced, true), (swap.Status, swap.Modded));
        Assert.NotNull(swap.Record);
        Assert.Equal(s.Swapped.Id, swap.Record!.Id);
        Assert.NotEqual(s.Swapped.LastTick, swap.LastTick);

        // a 1_-_ file that doesn't read: refused alone, with why; the game's own row works as before
        var bad = RowsOf(g, s.Unreadable).Single(c => c.FromFile);
        Assert.Null(bad.Record);
        Assert.Contains("record head", bad.ParseError);
        Assert.Equal(ClipStatus.Unknown, bad.Status);
        Assert.NotNull(RowsOf(g, s.Unreadable).Single(c => !c.FromFile).Record);

        // a file with no index before _-_: its row says where a repack puts it isn't known
        var noIndex = RowsOf(g, s.NoIndex).Single(c => c.FromFile);
        // modded by its bytes, though its name gives no number
        Assert.Equal((-1, (int?)null, true), (noIndex.Index, noIndex.FileNumber, noIndex.Modded));
        Assert.Contains("has no index", noIndex.Note);
        Assert.Contains("which of them the game loads isn't known", noIndex.Note);
    }

    [GameDataFact]
    public void TheListsGroupEachClipsRowsAndFilterTheModdedOnes()
    {
        var s = Make();
        using var g = s.G;
        var rows = new ClipQuery().Apply(g.Clips);
        int at = rows.FindIndex(c => c.Id == s.Edited.Id);
        Assert.Equal(s.Edited.Id, rows[at + 1].Id);
        // sorted by index, the mod (index 1) would stand far from the game's row: it is kept beside it
        var byIndex = new ClipQuery().Apply(g.Clips, ClipSort.Index);
        int i = byIndex.FindIndex(c => c.Id == s.Edited.Id);
        Assert.Equal(s.Edited.Id, byIndex[i + 1].Id);
        var modded = new ClipQuery { ModdedOnly = true }.Apply(g.Clips);
        Assert.Equal(g.Clips.Count(c => c.Modded), modded.Count);
        Assert.Equal(5, modded.Count);
        Assert.All(modded, c => Assert.True(c.Modded));
        // the search finds a mod by its name, beside the game's row
        var hits = new ClipQuery { Text = s.Edited.Name }.Apply(g.Clips, ClipSort.Relevance);
        Assert.Equal(2, hits.Count(c => c.Id == s.Edited.Id));
    }

    [GameDataFact]
    public void EveryActionWorksOnEachRow()
    {
        var s = Make();
        using var g = s.G;
        var rows = new[] { s.Edited, s.Same, s.Swapped, s.NoIndex }.SelectMany(c => RowsOf(g, c)).ToList();
        Assert.Equal(7, rows.Count);
        var outDir = Path.Combine(_dir + "-out");
        Directory.CreateDirectory(outDir);
        try
        {
            var swaps = SwapSources.Build(g.Clips, g.SharedTable);
            var source = s.Same;
            int n = 0;
            foreach (var row in rows)
            {
                string tag = $"{row.Name} {(row.FromFile ? "file" : "forge")}";
                // export, and import of its own export (the exact-length path)
                var glb = Path.Combine(outDir, $"{n++}.glb");
                var ex = ClipExport.Clip(g, row, glb);
                Assert.True(File.Exists(ex.GlbPath), tag);
                var back = ClipImport.Run(g, row, glb);
                Assert.Equal(row.LastTick, back.Rebuilt.Record.LastTick);
                // the edits' choices, from the row's own record
                var choices = EditJobs.Choices(row, new EditContext(g.Skeleton, g.SharedTable, null), null, swaps.For(row).Count, CancellationToken.None);
                Assert.NotEmpty(choices.Options);
                // replace with another clip, fitted
                var fit = source.LastTick > row.LastTick ? new FitOptions { Mode = FitMode.Cut } : new FitOptions { Mode = FitMode.Hold };
                var rep = ClipReplace.FromClip(g.Skeleton, g.SharedTable, row, source, fit);
                Assert.Equal(row.LastTick, rep.Rebuilt.Record.LastTick);
                Assert.Equal(row.Id, rep.Rebuilt.Record.Id);
                // the result written as a record file with its report (the Import screen's Export, the command line): a file row's is
                // named as its file is, 1 for one with no number
                var files = ImportOutput.Write(g, Path.Combine(outDir, $"rec{n}"), [rep]);
                int number = row.FromFile ? row.FileNumber ?? 1 : row.Index;
                Assert.Equal(RecordFile.ClipFileName(number, row.Name, row.Id), Assert.Single(files.Package.Files).FileName);
                Assert.Equal($"{number}_-_{row.Name}.checks.txt", Path.GetFileName(Assert.Single(files.Reports)));
            }
            // the row that doesn't read: every action refuses it with its reason
            var bad = RowsOf(g, s.Unreadable).Single(c => c.FromFile);
            Assert.Throws<ExportException>(() => ClipExport.Clip(g, bad, Path.Combine(outDir, "bad.glb")));
            Assert.Throws<ImportRefusedException>(() => ClipReplace.FromClip(g.Skeleton, g.SharedTable, bad, source, new FitOptions { Mode = FitMode.Hold }));
            // the edits' marks: the file rows worked out by their own records, each under its own key
            var fileRows = g.Clips.Where(c => c.FromFile).ToList();
            var marks = InstallEditChoices.Of(g, null, swaps, null, CancellationToken.None, only: fileRows);
            Assert.Equal(fileRows.Select(c => c.Key).Order(), marks.Clips.Keys.Order());
            Assert.All(fileRows, c => Assert.NotNull(marks.For(c)));
        }
        finally { try { Directory.Delete(outDir, true); } catch (IOException) { } }
    }

    [GameDataFact]
    public void APackageNamesItsFiles1AndKeepsTheGamesFileAndThePlayersMod()
    {
        var s = Make();
        using var g = s.G;
        var mod = RowsOf(g, s.Edited).Single(c => c.FromFile);
        // the player's mod edited again: another clip's motion on it
        var rep = ClipReplace.FromClip(g.Skeleton, g.SharedTable, mod, s.Same, new FitOptions { Mode = s.Same.LastTick > mod.LastTick ? FitMode.Cut : FitMode.Hold });
        var item = new PackageItem(mod, rep.Record, ClipChange.Replaced("test"), rep.Report.Verdict, rep.ReportText(), DateTime.Now);
        var save = _dir + "-save";
        try
        {
            var plan = PackageBuild.Plan(g, [item], "test package", save);
            Assert.Null(plan.Problem);
            Assert.Contains(plan.Files, x => x.Name == $"1_-_{s.Edited.Name}.Animation");
            Assert.Contains(plan.Files, x => x.Name == Path.Combine(PackageBuild.OriginalsFolderName, $"{s.Edited.Index}_-_{s.Edited.Name}.Animation"));
            Assert.Contains(plan.Files, x => x.Name == Path.Combine(PackageBuild.OriginalsFolderName, $"1_-_{s.Edited.Name}.Animation"));
            var o = Assert.Single(plan.Originals);
            Assert.Same(mod, o.Current);
            Assert.Equal(s.Edited.Index, o.Index);
            Assert.Contains("already hold a mod of", PackageBuild.OriginalsNote(plan.Originals));

            var built = PackageBuild.Build(g, plan, [item], replace: false);
            var folder = built.Folder;
            Assert.Equal(rep.Record.AsSpan(12 + s.Edited.Name.Length).ToArray(), File.ReadAllBytes(Path.Combine(folder, $"1_-_{s.Edited.Name}.Animation")));
            // the originals: the game's own file under its own number, and the player's mod as it was
            Assert.Equal(Content(s.Edited), File.ReadAllBytes(Path.Combine(folder, PackageBuild.OriginalsFolderName, $"{s.Edited.Index}_-_{s.Edited.Name}.Animation")));
            Assert.Equal(s.EditedFile, File.ReadAllBytes(Path.Combine(folder, PackageBuild.OriginalsFolderName, $"1_-_{s.Edited.Name}.Animation")));
            Assert.False(File.Exists(Path.Combine(folder, $"{s.Edited.Index}_-_{s.Edited.Name}.Animation")));
        }
        finally { try { Directory.Delete(save, true); } catch (IOException) { } }
    }

    [GameDataFact]
    public void TwoBrokenFilesOfTheSameBytesAreTwoRowsAndTheMarksStillWorkOut()
    {
        var s = Make();
        s.G.Dispose();
        // the same broken bytes under the names of two clips (an interrupted copy, say)
        Put($"1_-_{s.Same.Name}.Animation", [7, 7, 7]);
        Put($"1_-_{s.Swapped.Name}.Animation", [7, 7, 7]);
        using var g = GameInstall.Open(GameData.Game!, GameData.Forge!, _dir);
        var broken = g.Clips.Where(c => c.FromFile && c.Record is null && c.ParseError is not null).ToList();
        Assert.True(broken.Count >= 3);
        Assert.Equal(broken.Count, broken.Select(c => c.Key).Distinct().Count());
        var marks = InstallEditChoices.Of(g, null, null, null, CancellationToken.None, only: [.. g.Clips.Where(c => c.FromFile)]);
        Assert.Equal(g.Clips.Count(c => c.FromFile), marks.Count);
    }

    [GameDataFact]
    public void AFileCopiedOverTheGamesOwnUnderItsNumberIsTheModTheOriginalsKeep()
    {
        Directory.CreateDirectory(_dir);
        var g0 = f.Install;
        var clip = g0.FindClip("mca_emote_Hello")!;
        var motion = g0.FindClip("mca_emote_Clap")!;
        var mod = ClipReplace.FromClip(g0.Skeleton, g0.SharedTable, clip, motion, new FitOptions { Mode = FitMode.Cut }).Record;
        // the player's mod written over the game's file, under the game's number: no 1_-_ file
        Put(RecordFile.ClipFileName(clip.Index, clip.Name, clip.Id), RecordFile.Content(mod));
        using var g = GameInstall.Open(GameData.Game!, GameData.Forge!, _dir);
        var row = RowsOf(g, clip).Single(c => c.FromFile);
        Assert.True(row.Modded);
        Assert.Equal(ClipStatus.Replaced, row.Status);
        var edit = ClipReplace.FromClip(g.Skeleton, g.SharedTable, row, g.FindClip("mca_emote_Freeze")!, new FitOptions { Mode = FitMode.Hold });
        var item = new PackageItem(row, edit.Record, ClipChange.Replaced("test"), edit.Report.Verdict, edit.ReportText(), DateTime.Now);
        var o = Assert.Single(PackageBuild.Plan(g, [item], "p", _dir + "-save3").Originals);
        // the replaced mod is kept as 1_-_, and the game's own goes back under its own number
        Assert.Same(row, o.Current);
        Assert.Equal(clip.Index, o.Index);
        Assert.Equal(PackageBuild.FromInstall, o.From);
    }

    [GameDataFact]
    public void AModNamedAfterAnotherClipGetsItsOriginalUnderTheClipsOwnName()
    {
        Directory.CreateDirectory(_dir);
        var g0 = f.Install;
        var clip = g0.FindClip("mca_emote_Hello")!;
        var other = g0.FindClip("mca_emote_Clap")!;
        var mod = ClipReplace.FromClip(g0.Skeleton, g0.SharedTable, clip, other, new FitOptions { Mode = FitMode.Cut }).Record;
        // the greeting's record in a file named after the clap: the repack names the record by the file, the game finds it by its ID
        Put($"1_-_{other.Name}.Animation", RecordFile.Content(mod));
        Put(RecordFile.ClipFileName(clip.Index, clip.Name, clip.Id), Content(clip));
        string save = _dir + "-save4";
        using var g = GameInstall.Open(GameData.Game!, GameData.Forge!, _dir);
        try
        {
            var row = RowsOf(g, clip).Single(c => c.FromFile);
            Assert.Equal(other.Name, row.Name);
            Assert.Contains("its record is the clip", row.Note);
            var edit = ClipReplace.FromClip(g.Skeleton, g.SharedTable, row, g.FindClip("mca_emote_Freeze")!, new FitOptions { Mode = FitMode.Hold });
            var item = new PackageItem(row, edit.Record, ClipChange.Replaced("test"), edit.Report.Verdict, edit.ReportText(), DateTime.Now);
            var plan = PackageBuild.Plan(g, [item], "named", save);
            Assert.Null(plan.Problem);
            string orig = Path.Combine(PackageBuild.OriginalsFolderName, RecordFile.ClipFileName(clip.Index, clip.Name, clip.Id));
            Assert.Contains(plan.Files, x => x.Name == orig);
            Assert.DoesNotContain(plan.Files, x => x.Name == Path.Combine(PackageBuild.OriginalsFolderName, $"{clip.Index}_-_{other.Name}.Animation"));
            var built = PackageBuild.Build(g, plan, [item], replace: false);
            // every file the plan listed is written, and only those
            Assert.Equal(plan.Files.Where(x => !x.Name.EndsWith(Path.DirectorySeparatorChar)).Select(x => x.Name).Order(), built.Files.Select(x => x.FileName).Order());
            Assert.Equal(Content(clip), File.ReadAllBytes(Path.Combine(built.Folder, orig)));
            Assert.True(File.Exists(Path.Combine(built.Folder, $"1_-_{other.Name}.Animation")));
        }
        finally { try { Directory.Delete(save, true); } catch (IOException) { } }
    }

    [GameDataFact]
    public void AKeptEditOfAModdedRowIsRestoredAfterTheFolderIsRepacked()
    {
        var s = Make();
        string store = _dir + "-store";
        try
        {
            var kept = new PackageStore(store);
            PackageItem item;
            using (var g = s.G)
            {
                kept.Load(g);
                var mod = RowsOf(g, s.Edited).Single(c => c.FromFile);
                var rep = ClipReplace.FromClip(g.Skeleton, g.SharedTable, mod, s.Same, new FitOptions { Mode = s.Same.LastTick > mod.LastTick ? FitMode.Cut : FitMode.Hold });
                item = new PackageItem(mod, rep.Record, ClipChange.Replaced("test"), rep.Report.Verdict, rep.ReportText(), DateTime.Now);
                kept.Save(g, [item]);
            }
            // after a repack the forge holds the 1_-_ file's bytes: the file is then the forge's row, at the forge's index, file number 1
            // (stood for here by a 1_-_ file of the forge's own bytes)
            Put($"1_-_{s.Edited.Name}.Animation", Content(s.Edited));
            using var again = GameInstall.Open(GameData.Game!, GameData.Forge!, _dir);
            var row = Assert.Single(RowsOf(again, s.Edited));
            Assert.Equal((s.Edited.Index, (int?)1), (row.Index, row.FileNumber));
            var restored = new PackageStore(store).Load(again);
            Assert.Empty(restored.NotFitting);
            Assert.Same(row, Assert.Single(restored.Restored).Slot);
        }
        finally { try { Directory.Delete(store, true); } catch (IOException) { } }
    }

    [GameDataFact]
    public void AModWhoseNameARecordFileCantKeepIsSaidNotKept()
    {
        Directory.CreateDirectory(_dir);
        var g0 = f.Install;
        var clip = g0.FindClip("mca_emote_Hello")!;
        var mod = ClipReplace.FromClip(g0.Skeleton, g0.SharedTable, clip, g0.FindClip("mca_emote_Clap")!, new FitOptions { Mode = FitMode.Cut }).Record;
        // a browser's duplicate name: the name after _-_ holds " (1)", which a record file's name can't keep
        Put($"1_-_{clip.Name} (1).Animation", RecordFile.Content(mod));
        Put(RecordFile.ClipFileName(clip.Index, clip.Name, clip.Id), Content(clip));
        using var g = GameInstall.Open(GameData.Game!, GameData.Forge!, _dir);
        var row = RowsOf(g, clip).Single(c => c.FromFile);
        Assert.True(row.Modded);
        var game = RowsOf(g, clip).Single(c => !c.FromFile);
        var edit = ClipReplace.FromClip(g.Skeleton, g.SharedTable, game, g.FindClip("mca_emote_Freeze")!, new FitOptions { Mode = FitMode.Hold });
        var item = new PackageItem(game, edit.Record, ClipChange.Replaced("test"), edit.Report.Verdict, edit.ReportText(), DateTime.Now);
        var plan = PackageBuild.Plan(g, [item], "p", _dir + "-save5");
        Assert.Null(plan.Problem);
        var note = PackageBuild.ReplacesModNote(plan.Originals);
        Assert.Contains("is NOT kept", note);
        Assert.DoesNotContain("putting the originals back restores", note);
        Assert.DoesNotContain(plan.Files, x => x.Name.StartsWith(Path.Combine(PackageBuild.OriginalsFolderName, "1_-_"), StringComparison.Ordinal));
    }

    [GameDataFact]
    public void APackageOfAClipWithNoModKeepsOnlyTheGamesFile()
    {
        var s = Make();
        using var g = s.G;
        var game = RowsOf(g, s.Same).Single();
        var rep = ClipReplace.FromClip(g.Skeleton, g.SharedTable, game, s.Edited, new FitOptions { Mode = s.Edited.LastTick > game.LastTick ? FitMode.Cut : FitMode.Hold });
        var item = new PackageItem(game, rep.Record, ClipChange.Replaced("test"), rep.Report.Verdict, rep.ReportText(), DateTime.Now);
        var plan = PackageBuild.Plan(g, [item], "test package", _dir + "-save2");
        var o = Assert.Single(plan.Originals);
        Assert.Null(o.Current);
        // the 1_-_ file there is the game's own: its original is named by the forge's index, never 1
        Assert.Equal(s.Same.Index, o.Index);
        Assert.Null(PackageBuild.ReplacesModNote(plan.Originals));
    }
}
