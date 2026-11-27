using System.IO;
using System.Windows;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>The Package screen: no Modded only filter (every list elsewhere has it); a click on a row shows that row's every check, as
/// the Edit screen lists them; the save folder Build starts in is the one set under Settings, else the one last built in, else the
/// tool's own, each remembered in a settings file of the test's own. And the Import screen: Keep pressed inside the fit options' delay
/// keeps the result the options give. No window is shown; nothing is written outside the test's own folder.</summary>
[Collection("wpf")]
public sealed class PackageScreenTests
{
    static string NewDir(string what) => Path.Combine(AppContext.BaseDirectory, what + "-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ThePackageScreenHasNoModdedOnlyFilter() => Wpf.Run(() =>
    {
        Assert.Equal(Visibility.Collapsed, new PackageView().ItemsPanel.ModdedRow.Visibility);
        // the control: the list every other screen has keeps it
        Assert.Equal(Visibility.Visible, new ClipListPanel().ModdedRow.Visibility);
    });

    [Fact]
    public void ARowClickedShowsItsEveryCheck() => Wpf.Run(() =>
    {
        string report = "Edit of 5_-_clip_0 0x0000000000001000 (mirror): 10 ticks before, 10 after; the record 0 B\n"
            + "PASS  Bone lengths: every bone keeps its length\n"
            + "WARN  Foot sliding: a foot slides 4.2 cm (worst at tick 7, Foot_L)\n"
            + "FAIL  Hand reach: a hand can't reach its target (worst at tick 3)\n"
            + "VERDICT: FAIL (1 pass, 1 warn, 1 fail)\n"
            + "What the edit did and kept:\n   PASS-like note that isn't a check\n";
        PackageItem Item(int i, string text) => new(new ClipEntry { Index = 5 + i, Id = 0x1000UL + (ulong)i, Name = $"clip_{i}", Bytes = ReadOnlyMemory<byte>.Empty },
            [], ClipChange.Mirror(), i == 0 ? CheckLevel.Fail : CheckLevel.Pass, text, DateTime.Now);
        var items = new List<PackageItem> { Item(0, report), Item(1, "PASS  Bone lengths: every bone keeps its length\n") };
        var p = new PackageView();
        p.ShowUsersFor(items, null, "not built here");
        Assert.Equal(Visibility.Collapsed, p.ChecksPanel.Visibility);
        Assert.NotNull(p.ItemsPanel.Select(items[0].Slot));
        Assert.Equal(Visibility.Visible, p.ChecksPanel.Visibility);
        Assert.Equal("Checks of clip_0: FAIL", p.ChecksTitle.Text);
        var rows = Assert.IsType<List<EditView.ReportRow>>(p.CheckList.ItemsSource);
        // in the Edit screen's words: "(tick 7, Foot_L)"
        Assert.Equal(["FAIL Hand reach: a hand can't reach its target (tick 3)", "WARN Foot sliding: a foot slides 4.2 cm (tick 7, Foot_L)",
            "PASS Bone lengths: every bone keeps its length"], rows.Select(r => $"{r.Word} {r.Check}: {r.Text}"));
        // another row: its own checks
        p.ItemsPanel.Select(items[1].Slot);
        Assert.Equal("Checks of clip_1: PASS", p.ChecksTitle.Text);
        Assert.Single(Assert.IsType<List<EditView.ReportRow>>(p.CheckList.ItemsSource));
        // a search that hides the selected row: no row selected, no checks shown
        p.ItemsPanel.SearchBox.Text = "clip_0";
        p.ItemsPanel.Apply();
        Assert.DoesNotContain(items[1].Slot, p.ItemsPanel.Rows);
        Assert.Equal(Visibility.Collapsed, p.ChecksPanel.Visibility);
    });

    [Fact]
    public void TheSaveFolderIsTheSettingsElseTheLastBuiltInElseTheToolsOwn()
    {
        string dir = NewDir("package-folder");
        Directory.CreateDirectory(dir);
        try
        {
            Wpf.Run(() =>
            {
                string settings = Path.Combine(dir, "settings.json");
                string set = Path.Combine(dir, "set"), last = Path.Combine(dir, "last"), chosen = Path.Combine(dir, "chosen");
                var none = new PackageView();
                none.UseSettings(settings);
                Assert.Equal(PackageView.DefaultSaveFolder, none.FolderBox.Text);
                Assert.Null(none.PackageFolderSetting);

                new ViewerSettings { LastPackageFolder = last, BackupForge = Path.Combine(dir, "backup.forge") }.Save(settings, []);
                var lastOnly = new PackageView();
                lastOnly.UseSettings(settings);
                Assert.Equal(last, lastOnly.FolderBox.Text);
                Assert.Null(lastOnly.PackageFolderSetting);

                new ViewerSettings { PackageFolder = set, LastPackageFolder = last }.Save(settings, []);
                var both = new PackageView();
                both.UseSettings(settings);
                Assert.Equal(set, both.FolderBox.Text);
                Assert.Equal(set, both.PackageFolderSetting);

                // chosen under Settings: in the box at once, and in the file, the file's other settings kept
                both.SetPackageFolder(chosen);
                Assert.Equal(chosen, both.FolderBox.Text);
                var s = ViewerSettings.Load(settings);
                Assert.Equal((chosen, last), (s.PackageFolder, s.LastPackageFolder));
                // none: the file's setting gone, so the next run starts in the folder last built in
                both.SetPackageFolder(null);
                Assert.Null(ViewerSettings.Load(settings).PackageFolder);
                var next = new PackageView();
                next.UseSettings(settings);
                Assert.Equal(last, next.FolderBox.Text);
                // a run without a settings file remembers nothing: the file keeps the folder set before, and the usual settings file
                // (the run's own folder's, ToolData) is as it was, so a fall back to it would fail here
                next.SetPackageFolder(set);
                Assert.Equal(set, ViewerSettings.Load(settings).PackageFolder);
                string usual = ViewerSettings.DefaultPath;
                Assert.StartsWith(TestToolData.Folder, usual);
                byte[]? before = File.Exists(usual) ? File.ReadAllBytes(usual) : null;
                var automated = new PackageView();
                automated.UseSettings(null);
                automated.SetPackageFolder(chosen);
                Assert.Equal(chosen, automated.FolderBox.Text);
                Assert.Equal(set, ViewerSettings.Load(settings).PackageFolder);
                Assert.Equal(before, File.Exists(usual) ? File.ReadAllBytes(usual) : null);
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A Build remembers the folder it wrote into, and the next run starts there; a Build that is refused remembers nothing.</summary>
    [GameDataFact]
    public void ABuildRemembersTheFolderItWroteInto()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = NewDir("package-built-in");
        Directory.CreateDirectory(dir);
        try
        {
            Wpf.RunAsync(async () =>
            {
                string settings = Path.Combine(dir, "settings.json");
                new ViewerSettings { LastPackageFolder = Path.Combine(dir, "before") }.Save(settings, []);
                var p = new PackageView();
                p.UseSettings(settings);
                PackageList.Current.Clear();
                p.SetInstall(g);
                p.SetIndexError(g, "not built in this test");
                // refused (nothing to build): nothing remembered
                p.FolderBox.Text = Path.Combine(dir, "refused");
                Assert.NotNull(await p.BuildAsync());
                Assert.Equal(Path.Combine(dir, "before"), ViewerSettings.Load(settings).LastPackageFolder);

                var c = g.FindClip("mca_emote_Hello")!;
                var b = c.Bytes.ToArray();
                b[^1] ^= 0x01;
                PackageList.Current.Keep(new PackageItem(c, b, ClipChange.Mirror(), CheckLevel.Pass, "", DateTime.Now));
                string folder = Path.Combine(dir, "built");
                p.FolderBox.Text = folder;
                p.Refresh();
                Assert.Null(await p.BuildAsync());
                Assert.True(Directory.Exists(Path.Combine(folder, p.NameBox.Text)));
                Assert.Equal(Path.GetFullPath(folder), ViewerSettings.Load(settings).LastPackageFolder);
                var next = new PackageView();
                next.UseSettings(settings);
                Assert.Equal(Path.GetFullPath(folder), next.FolderBox.Text);
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Keep pressed inside the fit options' delay (the option changed, its re-run not started yet) keeps the result of the
    /// options as they stand, not the one before; so does an export.</summary>
    [GameDataFact]
    public void KeepInsideTheFitDelayKeepsTheResultTheOptionsGive()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = NewDir("import-fit-delay");
        Directory.CreateDirectory(dir);
        try
        {
            Wpf.RunAsync(async () =>
            {
                PreviewChoices.SettingsPath = null;
                ClipEntry hello = g.FindClip("mca_emote_Hello")!, idle = g.FindClip("mcm_nwp_fm_std_V0_idle_03")!;
                Assert.NotEqual(hello.LastTick, idle.LastTick);
                // the longer one on the shorter, so each fit (a cut too) applies
                if (hello.LastTick < idle.LastTick) (hello, idle) = (idle, hello);
                string glb = Path.Combine(dir, "source.glb");
                File.WriteAllBytes(glb, GlbWriter.Clip(g.Skeleton, SampledClip.Of(g.Skeleton, hello.Record!, g.SharedTable), hello.Name));
                PackageList.Current.Clear();
                var import = new ImportView();
                import.SetInstall(g, true);
                import.SetIndexError(g, "not built in this test");
                await import.ChooseGlbAsync(glb, pickNamed: false);
                Assert.Null(await import.ChooseSlotAsync(idle.Name));
                var first = import.Result ?? throw new InvalidOperationException($"the first import failed: {import.LastError}");
                Assert.Equal(Visibility.Visible, import.FitRow.Visibility);
                Assert.True(import.CutChoice.IsChecked);

                // Speed chosen, and Keep at once, inside the delay
                import.StretchChoice.IsChecked = true;
                Assert.Null(await import.KeepAsync());
                var expected = ClipImport.Run(g, idle, glb, await InstallWork.Graph(g),
                    new FitOptions { Mode = FitMode.Speed, Loop = import.LoopFitBox.IsChecked == true });
                var kept = Assert.Single(PackageList.Current.Items);
                Assert.Equal(expected.Record, kept.Record);
                // the control: the result before the change is another record
                Assert.NotEqual(first.Record, kept.Record);

                // Hold chosen, and an export at once: the file is Hold's result
                import.HoldChoice.IsChecked = true;
                string outDir = Path.Combine(dir, "out");
                Assert.Null(await import.ExportAsync(outDir, false));
                var hold = ClipImport.Run(g, idle, glb, await InstallWork.Graph(g),
                    new FitOptions { Mode = FitMode.Hold, Loop = import.LoopFitBox.IsChecked == true });
                Assert.Equal(hold.Record, import.Result!.Record);
                Assert.NotEqual(expected.Record, hold.Record);
                // and the file written is that record's file
                string written = Assert.Single(Directory.GetFiles(outDir, $"*_-_{idle.Name}.Animation"));
                Assert.Equal(Core.Records.RecordFile.Content(hold.Record), File.ReadAllBytes(written));
                Assert.NotEqual(Core.Records.RecordFile.Content(expected.Record), File.ReadAllBytes(written));

                // a file chosen again: while it is read, the result shown is the file before's, so Keep and Export wait
                Assert.True(import.KeepButton.IsEnabled);
                var reading = import.ChooseGlbAsync(glb, pickNamed: false);
                Assert.False(import.KeepButton.IsEnabled);
                Assert.False(import.ExportButton.IsEnabled);
                await reading;
                Assert.True(import.KeepButton.IsEnabled, import.LastError);
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g)).GetAwaiter().GetResult();
            Directory.Delete(dir, true);
        }
    }
}
