using System.IO;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.App.Tests;

/// <summary>The package list is kept between runs only until it is built: a successful Build empties it on the screen and in the kept
/// file at once, the screen showing what it built until a clip is kept again (a new list) and a restart showing an empty list; a Build
/// that is refused, fails while writing, or is left (another install opened while it waited for the clip index) keeps the list, on the
/// screen and in the file. The keeper and the screen as the window wires them, the kept file and the packages in folders of the test's own.</summary>
[Collection("wpf")]
public sealed class PackageBuiltTests
{
    static PackageItem Item(ClipEntry c, ClipChange change)
    {
        var b = c.Bytes.ToArray();
        b[^1] ^= 0x01;
        return new PackageItem(c, b, change, CheckLevel.Pass, "", DateTime.Now);
    }

    /// <summary>An install opened as the window opens one: the keeper quiet while the list is emptied, the screen given the install, the
    /// kept clips put back. With <paramref name="noIndex"/> the screen is told the clip index won't come, so Build doesn't wait for it.</summary>
    static PackageKeeper Open(string store, GameInstall g, PackageView p, bool noIndex = true)
    {
        var keeper = new PackageKeeper(store);
        keeper.Opening();
        PackageList.Current.Clear();
        p.SetInstall(g);
        p.ShowRestored(keeper.Restore(g), keeper.FilePath);
        if (noIndex) p.SetIndexError(g, "not built in this test");
        return keeper;
    }

    static string[] KeptWords(string store, GameInstall g) => [.. new PackageStore(store).Load(g).Restored.Select(i => i.Change.Words)];

    /// <summary>The refresh a typed name or save folder starts (the plan made and the folder looked at off the window's thread) greys
    /// Build until it ends, and is dropped when the screen was refreshed again meanwhile: the screen shows the folder typed last.</summary>
    [GameDataFact]
    public void ARefreshOffTheWindowsThreadIsDroppedWhenTheScreenWasRefreshedSince()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "typed-" + Guid.NewGuid().ToString("N"));
        try
        {
            Wpf.RunAsync(async () =>
            {
                var p = new PackageView();
                var keeper = Open(Path.Combine(dir, "kept"), g, p);
                PackageList.Current.Keep(Item(g.FindClip("mca_emote_Hello")!, ClipChange.Mirror()));
                // the refresh the list's change starts goes first, so the refreshes below are the latest
                await p.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                p.NameBox.Text = "Typed";
                p.FolderBox.Text = Path.Combine(dir, "first");
                p.Refresh();
                Assert.True(p.BuildButton.IsEnabled, p.ProblemText.Text);
                p.FolderBox.Text = Path.Combine(dir, "second");
                var second = p.RefreshOffThreadAsync();
                Assert.False(p.BuildButton.IsEnabled);
                Assert.Contains(Path.Combine(dir, "first"), p.PlanFolderText.Text);
                p.FolderBox.Text = Path.Combine(dir, "third");
                p.Refresh();
                await second;
                Assert.Contains(Path.Combine(dir, "third"), p.PlanFolderText.Text);
                Assert.Equal(Path.Combine(dir, "third"), p.PlannedWith!.Value.Plan.SaveFolder);
                Assert.True(p.BuildButton.IsEnabled, p.ProblemText.Text);
                keeper.Opening();
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>A refresh an event starts (here the clip index's failure arriving) looks at the save folder off the window's thread, as a
    /// typed folder's does: Build greyed and the plan as it was until the look ends, then the plan of the folder in the box.</summary>
    [GameDataFact]
    public void ARefreshAnEventStartsLooksAtTheSaveFolderOffTheWindowsThread()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "event-" + Guid.NewGuid().ToString("N"));
        try
        {
            Wpf.RunAsync(async () =>
            {
                var p = new PackageView();
                var keeper = Open(Path.Combine(dir, "kept"), g, p);
                PackageList.Current.Keep(Item(g.FindClip("mca_emote_Hello")!, ClipChange.Mirror()));
                await p.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                p.NameBox.Text = "Event";
                p.FolderBox.Text = Path.Combine(dir, "first");
                p.Refresh();
                Assert.True(p.BuildButton.IsEnabled, p.ProblemText.Text);
                p.FolderBox.Text = Path.Combine(dir, "second");
                p.SetIndexError(g, "still not built in this test");
                Assert.False(p.BuildButton.IsEnabled);
                Assert.Contains(Path.Combine(dir, "first"), p.PlanFolderText.Text);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!p.BuildButton.IsEnabled && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(10);
                Assert.True(p.BuildButton.IsEnabled, p.ProblemText.Text);
                Assert.Contains(Path.Combine(dir, "second"), p.PlanFolderText.Text);
                keeper.Opening();
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [GameDataFact]
    public void ABuildEmptiesTheKeptListShowsWhatItBuiltAndAClipKeptAfterStartsANewList()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "built-" + Guid.NewGuid().ToString("N"));
        string store = Path.Combine(dir, "kept");
        try
        {
            Wpf.RunAsync(async () =>
            {
                var p = new PackageView();
                var keeper = Open(store, g, p);
                p.BuiltHere = keeper.RemoveBuiltHere;
                var hello = g.FindClip("mca_emote_Hello")!;
                var idle = g.FindClip("mcm_nwp_fm_std_V0_idle_03")!;
                PackageList.Current.Keep(Item(hello, ClipChange.Speed(1.25)));
                PackageList.Current.Keep(Item(idle, ClipChange.Imported(Path.Combine(dir, "idle_03_head.glb"))));
                Assert.Equal(["Speed 1.25x", "Imported: idle_03_head.glb"], KeptWords(store, g));
                p.NameBox.Text = "Built";
                p.FolderBox.Text = dir;
                p.Refresh();
                Assert.True(p.BuildButton.IsEnabled, p.ProblemText.Text);
                // Remove follows the selection: a kept row selected can be removed, until a search hides it
                Assert.NotNull(p.ItemsPanel.Select(hello));
                Assert.True(p.RemoveButton.IsEnabled);
                p.ItemsPanel.SearchBox.Text = "idle_03";
                p.ItemsPanel.Apply();
                Assert.DoesNotContain(hello, p.ItemsPanel.Rows);
                Assert.False(p.RemoveButton.IsEnabled);
                p.ItemsPanel.SearchBox.Text = "";
                p.ItemsPanel.Apply();
                Assert.Null(await p.BuildAsync());

                // the package folder as before: the record files, the original clips and the marker
                string folder = Path.Combine(dir, PackageBuild.FolderNameFor("Built"));
                Assert.True(File.Exists(Path.Combine(folder, PackageBuild.MarkerName)));
                Assert.True(File.Exists(Path.Combine(folder, RecordFile.ClipFileName(PackageBuild.PackageFileIndex, hello.Name, hello.Id))));
                Assert.True(File.Exists(Path.Combine(folder, RecordFile.ClipFileName(PackageBuild.PackageFileIndex, idle.Name, idle.Id))));
                Assert.True(Directory.Exists(Path.Combine(folder, PackageBuild.OriginalsFolderName)));
                // the list and the kept file empty at once
                Assert.Empty(PackageList.Current.Items);
                Assert.Empty(KeptWords(store, g));
                // the screen shows what was built, plainly as built, with the folder and nothing left to build
                p.Refresh();
                Assert.True(p.ShowsBuilt);
                Assert.Equal(["Speed 1.25x", "Imported: idle_03_head.glb"], p.ShownRows.Select(r => r.What));
                Assert.Equal("Built, no longer kept", p.ItemsTitle.Text);
                Assert.Equal("2 clips built; nothing left to build", p.CountText.Text);
                Assert.StartsWith("Built into ", p.SummaryText.Text);
                Assert.Contains(PackageBuild.FolderNameFor("Built"), p.SummaryText.Text);
                Assert.Contains("Nothing left to build", p.SummaryText.Text);
                Assert.Contains(PackageBuild.FolderNameFor("Built"), p.ResultText.Text);
                Assert.False(p.BuildButton.IsEnabled);
                Assert.Equal("", p.ProblemText.Text);
                Assert.Equal(System.Windows.Visibility.Collapsed, p.UsersNotice.Visibility);
                // a built clip selected can't be removed: it is in no list
                p.ItemsPanel.List.SelectedIndex = 0;
                Assert.False(p.RemoveButton.IsEnabled);
                keeper.Opening();   // this run ends

                // a restart: the list is empty
                var next = new PackageView();
                var nextKeeper = Open(store, g, next);
                Assert.Empty(PackageList.Current.Items);
                Assert.Empty(next.ShownRows);
                Assert.False(next.ShowsBuilt);
                Assert.Equal("In the package", next.ItemsTitle.Text);
                Assert.Equal(System.Windows.Visibility.Collapsed, next.RestoreText.Visibility);

                // a clip kept after a build starts a new list: the build's view gives way to it, and it is kept for the next run
                PackageList.Current.Keep(Item(hello, ClipChange.Mirror()));
                p.Refresh();
                Assert.False(p.ShowsBuilt);
                Assert.Null(p.LastBuilt);
                Assert.Equal(["Mirrored"], p.ShownRows.Select(r => r.What));
                Assert.Equal("In the package", p.ItemsTitle.Text);
                Assert.Equal("1 clip: 0 imports, 1 edit", p.CountText.Text);
                Assert.Equal(["Mirrored"], KeptWords(store, g));
                // removing it leaves an empty list, not the build's view again
                PackageList.Current.Remove(hello.Id);
                p.Refresh();
                Assert.False(p.ShowsBuilt);
                Assert.Empty(p.ShownRows);
                nextKeeper.Opening();
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>A clip kept while Build runs stays kept once it has built (only the clips built leave the list); and a build that ends after
    /// another install of the same game files was opened takes the clips it built out of that install's list too, on the screen and in the
    /// kept file, a clip kept there meanwhile staying.</summary>
    [GameDataFact]
    public void AClipKeptWhileBuildRunsStaysAndABuildEndingAfterAnotherInstallTakesItsClipsOutOfTheirList()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        using var again = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "built-while-" + Guid.NewGuid().ToString("N"));
        string store = Path.Combine(dir, "kept");
        try
        {
            Wpf.RunAsync(async () =>
            {
                var p = new PackageView();
                var keeper = Open(store, g, p);
                p.BuiltElsewhere = keeper.RemoveBuilt;
                p.BuiltHere = keeper.RemoveBuiltHere;
                var hello = g.FindClip("mca_emote_Hello")!;
                var idle = g.FindClip("mcm_nwp_fm_std_V0_idle_03")!;
                var cycle = g.FindClip("mcm_nwp_fm_std_V1_cycle")!;
                p.FolderBox.Text = dir;

                // a clip kept while the build runs: the clip built leaves the list, the one kept meanwhile stays, on the screen and in the file
                PackageList.Current.Keep(Item(hello, ClipChange.Speed(1.25)));
                p.NameBox.Text = "First";
                p.Refresh();
                var hold = new TaskCompletionSource();
                p.HoldBuildForTests = hold.Task;
                var build = p.BuildAsync();
                await WaitFor(() => Directory.Exists(Path.Combine(dir, PackageBuild.FolderNameFor("First"), PackageBuild.OriginalsFolderName)) || build.IsCompleted);
                PackageList.Current.Keep(Item(idle, ClipChange.Mirror()));
                hold.SetResult();
                Assert.Null(await build);
                Assert.Equal(["Mirrored"], PackageList.Current.Items.Select(i => i.Change.Words));
                Assert.Equal(["Mirrored"], KeptWords(store, g));

                // a build that ends after another install of the same game files was opened (as the window opens one), a clip kept there
                // meanwhile: the clip built leaves that list too, the one kept there stays
                p.NameBox.Text = "Second";
                p.Refresh();
                hold = new TaskCompletionSource();
                p.HoldBuildForTests = hold.Task;
                build = p.BuildAsync();
                await WaitFor(() => Directory.Exists(Path.Combine(dir, PackageBuild.FolderNameFor("Second"), PackageBuild.OriginalsFolderName)) || build.IsCompleted);
                keeper.Opening();
                PackageList.Current.Clear();
                p.SetInstall(again);
                p.ShowRestored(keeper.Restore(again), keeper.FilePath);
                p.SetIndexError(again, "not built in this test");
                Assert.Equal(["Mirrored"], PackageList.Current.Items.Select(i => i.Change.Words));
                PackageList.Current.Keep(Item(again.FindClip(cycle.Id)!, ClipChange.Trim(10, 40)));
                hold.SetResult();
                Assert.Null(await build);
                Assert.Equal(["Trim 0.17 s to 0.67 s"], PackageList.Current.Items.Select(i => i.Change.Words));
                Assert.Equal(["Trim 0.17 s to 0.67 s"], KeptWords(store, again));
                Assert.Null(p.LastBuilt);
                Assert.Contains("the clips built are out of the list now", p.ResultText.Text);
                keeper.Opening();
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        static async Task WaitFor(Func<bool> done)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && sw.Elapsed < TimeSpan.FromSeconds(120)) await Task.Delay(20);
            Assert.True(done(), "the build didn't get to its end");
        }
    }

    /// <summary>The same install's game files opened from another game folder: an install sharing <paramref name="g"/>'s data under
    /// another folder (its list is another), for a build ending after another game folder was opened.</summary>
    static GameInstall Elsewhere(GameInstall g, string folder)
    {
        var ctor = typeof(GameInstall).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
        return (GameInstall)ctor.Invoke([folder, Path.Combine(folder, GameInstall.PatchForgeName), Path.Combine(folder, GameInstall.BaseForgeName), g.Oodle,
            g.AnimationEntry, g.Records.ToList(), g.Clips.ToList(), g.SharedTable, g.BodySkeleton, g.AddOnSkeleton, g.Names, g.Vanilla, g.Timings]);
    }

    /// <summary>A build that ends after another game folder was opened (as the window opens one, letting the built install go): the
    /// built install stays whole until the build ends (its library isn't unloaded under it), the build ends well, and its clips leave the
    /// list kept for the game folder it was built from, the screen saying so; the other game folder's list is untouched. Then the built
    /// install is let go.</summary>
    [GameDataFact]
    public void ABuildEndingAfterAnotherGameFolderWasOpenedTakesItsClipsOutOfItsOwnList()
    {
        var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        using var lender = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "built-elsewhere-" + Guid.NewGuid().ToString("N"));
        string store = Path.Combine(dir, "kept");
        var other = Elsewhere(lender, Path.Combine(dir, "Other install", "Ghost Recon Breakpoint"));
        try
        {
            Wpf.RunAsync(async () =>
            {
                var p = new PackageView();
                var keeper = Open(store, g, p);
                p.BuiltElsewhere = keeper.RemoveBuilt;
                p.BuiltHere = keeper.RemoveBuiltHere;
                var hello = g.FindClip("mca_emote_Hello")!;
                PackageList.Current.Keep(Item(hello, ClipChange.Speed(1.25)));
                Assert.Equal(["Speed 1.25x"], KeptWords(store, g));
                p.NameBox.Text = "Elsewhere";
                p.FolderBox.Text = dir;
                p.Refresh();
                var hold = new TaskCompletionSource();
                p.HoldBuildForTests = hold.Task;
                // the built install let go (as opening another does) before the build reads the forges for the originals
                bool letGoAtOnce = true;
                p.BeforeBuildReadsForTests = () => { g.Dispose(); letGoAtOnce = g.IsDisposed; };
                var build = p.BuildAsync();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!Directory.Exists(Path.Combine(dir, PackageBuild.FolderNameFor("Elsewhere"), PackageBuild.OriginalsFolderName)) && !build.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(120))
                    await Task.Delay(20);
                Assert.False(letGoAtOnce, "the built install was let go while its build ran");

                // another game folder opened, as the window opens one: the built install let go, its list's keeper on the other folder
                keeper.Opening();
                PackageList.Current.Clear();
                p.SetInstall(other);
                p.ShowRestored(keeper.Restore(other), keeper.FilePath);
                p.SetIndexError(other, "not built in this test");
                PackageList.Current.Keep(Item(other.FindClip(hello.Id)!, ClipChange.Mirror()));
                Assert.False(g.IsDisposed, "the built install was let go while its build ran");
                hold.SetResult();
                Assert.Null(await build);
                Assert.True(g.IsDisposed);
                // the build read its originals with the install's library still loaded
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(dir, PackageBuild.FolderNameFor("Elsewhere"), PackageBuild.OriginalsFolderName)));
                Assert.Contains($"the 1 clip built is out of the list kept for {g.GameFolder}", p.ResultText.Text);
                Assert.Empty(KeptWords(store, g));
                Assert.Equal(["Mirrored"], KeptWords(store, other));
                Assert.Equal(["Mirrored"], PackageList.Current.Items.Select(i => i.Change.Words));
                keeper.Opening();
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            g.Dispose();
            typeof(GameInstall).Assembly.GetType("GrbAnimTool.Core.IO.FolderGuard")!
                .GetMethod("Forget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [other]);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>After a build, a save of the list kept between runs that fails (the kept file held open by another program) is said
    /// plainly on the Package screen: the next run would show the clips built again. An error in what follows a build's end (taking the
    /// clips built out of the list) never makes the package written read as not built: it is built, and the screen says the list couldn't
    /// be updated.</summary>
    [GameDataFact]
    public void ASaveThatFailsAfterABuildIsSaid()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "built-unsaved-" + Guid.NewGuid().ToString("N"));
        string store = Path.Combine(dir, "kept");
        try
        {
            Wpf.RunAsync(async () =>
            {
                var p = new PackageView();
                var keeper = Open(store, g, p);
                p.BuiltHere = keeper.RemoveBuiltHere;
                PackageList.Current.Keep(Item(g.FindClip("mca_emote_Hello")!, ClipChange.Speed(1.25)));
                p.NameBox.Text = "Unsaved";
                p.FolderBox.Text = dir;
                p.Refresh();
                // the kept file held open, unshared: the save after the build fails
                using (new FileStream(Path.Combine(store, PackageStore.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.Null(await p.BuildAsync());
                Assert.True(p.ShowsBuilt);
                Assert.Contains("the list kept between runs couldn't be changed", p.SummaryText.Text);
                Assert.DoesNotContain("the next run starts empty", p.SummaryText.Text);
                Assert.Equal(["Speed 1.25x"], KeptWords(store, g));

                // the list's update after a build failing in the code: built all the same, and said
                p.BuiltHere = _ => throw new InvalidOperationException("a fault after the build");
                PackageList.Current.Keep(Item(g.FindClip("mca_emote_Hello")!, ClipChange.Mirror()));
                p.NameBox.Text = "Handler";
                p.Refresh();
                Assert.Null(await p.BuildAsync());
                Assert.True(File.Exists(Path.Combine(dir, PackageBuild.FolderNameFor("Handler"), PackageBuild.MarkerName)));
                Assert.StartsWith("Built ", p.ResultText.Text);
                Assert.Contains("Built, but the package list couldn't be updated (InvalidOperationException): a fault after the build", p.ResultText.Text);
                Assert.Equal(System.Windows.Visibility.Collapsed, p.ErrorBox.Visibility);
                keeper.Opening();
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [GameDataFact]
    public void ABuildRefusedFailedOrLeftKeepsTheList()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        using var again = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "not-built-" + Guid.NewGuid().ToString("N"));
        string store = Path.Combine(dir, "kept");
        Directory.CreateDirectory(dir);
        try
        {
            Wpf.RunAsync(async () =>
            {
                var p = new PackageView();
                var keeper = Open(store, g, p);
                var hello = g.FindClip("mca_emote_Hello")!;
                PackageList.Current.Keep(Item(hello, ClipChange.Trim(54, 484)));
                void StillKept(PackageView view, GameInstall install)
                {
                    Assert.Equal(["Trim 0.9 s to 8.07 s"], KeptWords(store, install));
                    Assert.Single(PackageList.Current.Items);
                    view.Refresh();
                    Assert.False(view.ShowsBuilt);
                    Assert.Null(view.LastBuilt);
                    Assert.Equal(["Trim 0.9 s to 8.07 s"], view.ShownRows.Select(r => r.What));
                    Assert.Equal("In the package", view.ItemsTitle.Text);
                }

                // refused: the package's folder is there and isn't a build of this tool
                p.NameBox.Text = "Taken";
                p.FolderBox.Text = dir;
                Directory.CreateDirectory(Path.Combine(dir, PackageBuild.FolderNameFor("Taken"), "a folder of its own"));
                p.Refresh();
                Assert.NotNull(await p.BuildAsync());
                StillKept(p, g);

                // failed while writing: the save folder can't be made, a file standing in its way
                string blocked = Path.Combine(dir, "blocked");
                File.WriteAllText(blocked, "a file where the save folder's parent would be");
                p.NameBox.Text = "Blocked";
                p.FolderBox.Text = Path.Combine(blocked, "packages");
                p.Refresh();
                Assert.NotNull(await p.BuildAsync());
                Assert.True(File.Exists(blocked));
                StillKept(p, g);

                // left: Build waits for the clip index, and another install is opened meanwhile (as the window opens one): nothing is
                // built, and the list comes back for the install opened, still kept
                keeper.Opening();
                var waits = new PackageView();
                InstallWork.Open(g);
                await Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g));
                keeper = Open(store, g, waits, noIndex: false);
                waits.NameBox.Text = "Left";
                waits.FolderBox.Text = dir;
                waits.Refresh();
                var build = waits.BuildAsync();
                await Task.Delay(200);
                Assert.False(build.IsCompleted, "Build didn't wait for the clip index");
                InstallWork.Open(again);
                keeper.Opening();
                PackageList.Current.Clear();
                waits.SetInstall(again);
                waits.ShowRestored(keeper.Restore(again), keeper.FilePath);
                Assert.Equal("Another game folder was opened while Build waited for the clip index.", await build);
                Assert.False(Directory.Exists(Path.Combine(dir, PackageBuild.FolderNameFor("Left"))));
                StillKept(waits, again);
                keeper.Opening();
                await Task.WhenAll(InstallWork.Graph(again), InstallWork.Swaps(again), InstallWork.Mirror(again));
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
