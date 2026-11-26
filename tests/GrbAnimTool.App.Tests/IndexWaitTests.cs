using System.IO;
using System.Numerics;
using System.Reflection;
using System.Windows;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;
using static GrbAnimTool.Core.Index.ClipCategories;

namespace GrbAnimTool.App.Tests;

/// <summary>Who plays each clip is on the screen before anything is written: the Package screen's Build and the Import screen's
/// export are greyed while the clip index is read, with its progress; a Build or an export already started waits for it, then writes
/// with the users on the screen. The index comes as the Library gives it, through the window's wiring (its progress, then the index
/// ready, or its failure); a failure that arrives while Build and the export wait lets them go on, the screen saying who plays the clip
/// isn't known, and why. On the vanilla install, opened twice (each opening's index is a wait of its own), with the window built (never
/// shown) and its screens given the install as the Library gives it; the index is one of the test's own.</summary>
[Collection("wpf")]
public sealed class IndexWaitTests
{
    /// <summary>The install whose background reads were started last: they end before the installs are closed.</summary>
    static GameInstall? _started;

    [GameDataFact]
    public void BuildAndExportWaitForTheClipIndexAndGoOnWhenItCouldntBeBuilt()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        using var again = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "index-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Wpf.RunAsync(() => Run(g, again, dir)); }
        finally
        {
            if (_started is { } s) Task.WhenAll(InstallWork.Graph(s), InstallWork.Swaps(s), InstallWork.Mirror(s), InstallWork.Marks(s, null).Done).GetAwaiter().GetResult();
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>The screen as it stood when a write started.</summary>
    sealed record Seen(string Notice, string PlayedBy, bool Waiting, bool Written);

    static async Task Run(GameInstall g, GameInstall again, string dir)
    {
        // the package list kept between runs goes into the test's own folder, never the user's
        var w = new MainWindow(ViewerArgs.Parse(["--package-store", Path.Combine(dir, "kept")]));
        var hello = g.FindClip("mca_emote_Hello")!;
        var idle = g.FindClip("mcm_nwp_fm_std_V0_idle_03")!;
        string glb = Path.Combine(dir, "idle_03_head.glb");
        File.WriteAllBytes(glb, HeadTurned(g, idle));
        var p = w.PackagePage;
        var import = w.ImportPage;

        // ---- the index couldn't be built, and the failure comes while Build and the export wait: the window passes it on, both go on
        // with the reason on the screen
        await Open(w, g, import, glb, idle);
        Keep(hello);
        p.NameBox.Text = "Failed";
        p.FolderBox.Text = dir;
        p.Refresh();
        Assert.False(p.BuildButton.IsEnabled);
        Assert.False(import.ExportButton.IsEnabled);
        Assert.True(import.KeepButton.IsEnabled);
        Assert.StartsWith("Waiting for the clip index: reading the game's graph, 0%.", import.ExportWaitText.Text);
        string packageFailed = Path.Combine(dir, PackageBuild.FolderNameFor("Failed")), exportFailed = Path.Combine(dir, "export-failed");
        Seen? built = null, exported = null;
        p.WriteStarting = () => built = new(p.UsersNotice.Text, Row(p).PlayedBy, p.WaitPanel.Visibility == Visibility.Visible, Directory.Exists(packageFailed));
        import.WriteStarting = () => exported = new(import.UsersNotice.Text, import.UsersText.Text, import.ExportWaitText.Visibility == Visibility.Visible,
            Directory.Exists(exportFailed));
        var build = p.BuildAsync();
        var export = import.ExportAsync(exportFailed, false, commandLine: true);
        await Task.Delay(300);
        Assert.False(build.IsCompleted, "Build didn't wait for the clip index");
        Assert.False(export.IsCompleted, "the export didn't wait for the clip index");
        Assert.Null(built);
        Assert.Null(exported);
        w.LibraryPage.IndexBuildFailed(g, new IOException("the disk is full."));
        Assert.Null(await build);
        Assert.Null(await export);
        const string failedNotice = "Who plays this clip isn't known: the clip index couldn't be built (IOException: the disk is full).";
        Assert.NotNull(built);
        Assert.StartsWith(failedNotice, built.Notice);
        Assert.Equal("Played by: " + ClipUsers.NotBuiltText, built.PlayedBy);
        Assert.False(built.Waiting);
        Assert.False(built.Written);
        Assert.NotNull(exported);
        Assert.StartsWith(failedNotice, exported.Notice);
        Assert.Equal("Played by: " + ClipUsers.NotBuiltText, exported.PlayedBy);
        Assert.False(exported.Waiting);
        Assert.False(exported.Written);
        // built: the list is done with and the screen shows what was built; a clip kept again opens Build (the failed index doesn't
        // keep it greyed)
        Assert.True(p.ShowsBuilt);
        Assert.Empty(PackageList.Current.Items);
        PackageList.Current.Keep(new PackageItem(hello, Edited(hello), ClipChange.Mirror(), CheckLevel.Pass, "", DateTime.Now));
        p.Refresh();
        Assert.True(p.BuildButton.IsEnabled, $"Build stays greyed after the index failed: {p.ProblemText.Text}");
        Assert.True(import.ExportButton.IsEnabled);
        Assert.Equal(Visibility.Collapsed, import.ExportWaitText.Visibility);
        Assert.Equal("IOException: the disk is full.", typeof(EditView).GetField("_indexError", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.EditPage));
        // the package: its record file, its original clips and its marker, no text; the export: the record file and its check report
        Assert.Equal(new[] { PackageBuild.MarkerName, RecordFile.ClipFileName(PackageBuild.PackageFileIndex, hello.Name, hello.Id), PackageBuild.OriginalsFolderName }.Order(StringComparer.Ordinal), Names(packageFailed));
        Assert.Equal(new[] { $"{idle.Index}_-_{idle.Name}.checks.txt", RecordFile.ClipFileName(idle.Index, idle.Name, idle.Id) }.Order(StringComparer.Ordinal), Names(exportFailed));
        // the first opening's background reads end before the second's start
        await Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g), InstallWork.Marks(g, null).Done);

        // ---- the index on its way: Build and Export greyed with the progress the Library reports; Build and an export already
        // started wait, then write once the Library's index is on the screen
        (hello, idle) = (again.FindClip(hello.Name)!, again.FindClip(idle.Name)!);
        await Open(w, again, import, glb, idle);
        Keep(hello);
        p.NameBox.Text = "Waits";
        p.Refresh();
        Assert.Equal(Visibility.Visible, p.WaitPanel.Visibility);
        Assert.False(p.BuildButton.IsEnabled);
        Assert.False(import.ExportButton.IsEnabled);
        Assert.StartsWith("Waiting for the clip index: reading the game's graph, 0%.", p.WaitText.Text);
        w.LibraryPage.IndexBar.Value = 0;
        w.LibraryPage.IndexReadProgress(again, 0.5);
        Assert.StartsWith("Waiting for the clip index: reading the game's graph, 50%.", p.WaitText.Text);
        Assert.Equal(0.5, p.WaitBar.Value);
        Assert.StartsWith("Waiting for the clip index: reading the game's graph, 50%. Export opens once it is ready", import.ExportWaitText.Text);
        Assert.Equal("Played by: " + ClipUsers.NotKnownText, Row(p).PlayedBy);
        Assert.StartsWith("Who plays this clip isn't known yet: the clip index isn't ready", p.UsersNotice.Text);
        Assert.StartsWith("Who plays this clip isn't known: the clip index isn't ready", import.UsersNotice.Text);

        string packageWaits = Path.Combine(dir, PackageBuild.FolderNameFor("Waits")), exportWaits = Path.Combine(dir, "export");
        built = exported = null;
        p.WriteStarting = () => built = new(p.UsersNotice.Text, Row(p).PlayedBy, p.WaitPanel.Visibility == Visibility.Visible, Directory.Exists(packageWaits));
        import.WriteStarting = () => exported = new(import.UsersNotice.Text, import.UsersText.Text, import.ExportWaitText.Visibility == Visibility.Visible,
            Directory.Exists(exportWaits));
        build = p.BuildAsync();
        export = import.ExportAsync(exportWaits, false, commandLine: true);
        await Task.Delay(500);
        Assert.False(build.IsCompleted, "Build didn't wait for the clip index");
        Assert.False(export.IsCompleted, "the export didn't wait for the clip index");
        Assert.False(Directory.Exists(packageWaits));
        Assert.False(Directory.Exists(exportWaits));
        Assert.StartsWith("Waiting for the clip index", import.StatusText.Text);

        // the index is ready, as the Library gives it
        w.LibraryPage.IndexBuilt(again, IndexOf((hello, [Player, Teammates], []), (idle, [Player, Teammates], [Teammates])), "ready", "ready");
        Assert.Null(await build);
        Assert.Null(await export);
        // before either wrote, who plays each clip was on the screen
        Assert.NotNull(built);
        Assert.Contains("  mca_emote_Hello: Player and Teammates", built.Notice);
        Assert.Equal("Played by: Player and Teammates", built.PlayedBy);
        Assert.False(built.Waiting);
        Assert.False(built.Written);
        Assert.NotNull(exported);
        Assert.StartsWith("Played by: Player; probably also Teammates", exported.PlayedBy);
        Assert.StartsWith("More than one kind of character plays this clip: Player; probably also Teammates.", exported.Notice);
        Assert.False(exported.Waiting);
        Assert.False(exported.Written);
        Assert.True(import.ExportButton.IsEnabled);
        Assert.True(File.Exists(Path.Combine(packageWaits, PackageBuild.OriginalsFolderName, RecordFile.ClipFileName(hello.Index, hello.Name, hello.Id))));
        Assert.True(File.Exists(Path.Combine(exportWaits, RecordFile.ClipFileName(idle.Index, idle.Name, idle.Id))));

        // the button's export shows an early refusal on the screen, not only to its caller
        p.WriteStarting = import.WriteStarting = null;
        import.SetInstall(again, true);
        Assert.Equal("Nothing imported to export.", await import.ExportShownAsync(Path.Combine(dir, "nothing"), false));
        Assert.Equal(Visibility.Visible, import.ErrorBox.Visibility);
        Assert.Equal("Nothing imported to export.", import.ErrorText.Text);
    }

    /// <summary>The screens given the install as the Library's InstallShown gives it (who plays a clip not known yet), and the .glb
    /// imported into the idle on the Import screen.</summary>
    static async Task Open(MainWindow w, GameInstall g, ImportView import, string glb, ClipEntry idle)
    {
        _started = g;
        import.SetInstall(g, true);
        w.EditPage.SetInstall(g, true, w.LibraryPage);
        w.PackagePage.SetInstall(g);
        await import.ChooseGlbAsync(glb, pickNamed: false);
        Assert.Null(await import.ChooseSlotAsync(idle.Name));
        Assert.NotNull(import.Result);
    }

    /// <summary>The package holding the clip alone, a byte of it changed (an install opened empties the list).</summary>
    static void Keep(ClipEntry c)
    {
        PackageList.Current.Clear();
        PackageList.Current.Keep(new PackageItem(c, Edited(c), ClipChange.Mirror(), CheckLevel.Pass, "", DateTime.Now));
    }

    static PackageView.PackageRow Row(PackageView p) => Assert.Single(p.ShownRows);

    static string[] Names(string folder) => [.. Directory.GetFileSystemEntries(folder).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal)];

    /// <summary>The clip's record with its last byte changed: a change the package takes (its checks aren't the test's).</summary>
    static byte[] Edited(ClipEntry c)
    {
        var b = c.Bytes.ToArray();
        b[^1] ^= 0x01;
        return b;
    }

    /// <summary>The clip as the tool exports it, its head turned up to 30 degrees and back over its length.</summary>
    static byte[] HeadTurned(GameInstall g, ClipEntry c)
    {
        var sc = SampledClip.Of(g.Skeleton, c.Record!, g.SharedTable);
        var r = sc.Rotations[g.Skeleton.IndexOf(Crc32.Of("Head"))]!;
        for (int t = 0; t < r.Length; t++)
            r[t] = Quaternion.Normalize(r[t] * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 30 * MathF.Sin(MathF.PI * t / (r.Length - 1)) * MathF.PI / 180));
        return GlbWriter.Clip(g.Skeleton, sc, c.Name);
    }

    /// <summary>A clip index holding only these clips: who plays each, and which of them only probably.</summary>
    static ClipIndex IndexOf(params (ClipEntry Clip, string[] Who, string[] Probably)[] entries) => new()
    {
        Entries = entries.Select(e => new ClipIndexEntry
        {
            ClipId = e.Clip.Id, ClipName = e.Clip.Name, Who = e.Who.Select(x => new ClipCategory(x, CategorySource.Graph, "the test's")).ToList(), What = [],
            Documents = [], Explained = [], WhoProbably = e.Probably,
        }).ToList(),
        Facts = new GraphFacts { Documents = [], Clips = new Dictionary<ulong, ClipGraphFacts>(), Failures = [], ClipsLookedFor = 0 },
        DocumentPlacements = [],
    };
}
