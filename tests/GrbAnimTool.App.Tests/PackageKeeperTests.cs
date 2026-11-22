using System.IO;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>The package list kept between runs as the window keeps it: saved as it changes, put back after a restart (a new keeper on the
/// same folder, the install opened again), and the Package screen saying what came back and what was left out. In a folder of the test's
/// own, never the user's.</summary>
[Collection("wpf")]
public sealed class PackageKeeperTests
{
    [GameDataFact]
    public void TheListIsKeptAndComesBackAfterARestart()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "keeper-" + Guid.NewGuid().ToString("N"));
        try
        {
            Wpf.Run(() =>
            {
                var hello = g.FindClip("mca_emote_Hello")!;
                var b = hello.Bytes.ToArray();
                b[^1] ^= 0x01;
                // the first run: an install opened (nothing kept yet), then a clip kept: the list is saved
                var first = new PackageKeeper(dir);
                first.Opening();
                PackageList.Current.Clear();
                Assert.Empty(first.Restore(g).Restored);
                PackageList.Current.Keep(new PackageItem(hello, b, ClipChange.Imported("M72.glb"), CheckLevel.Pass, "the report", DateTime.Now));
                Assert.True(File.Exists(first.FilePath));
                first.Opening();   // the first run ends: its keeper saves nothing more

                // the next run: the list starts empty, the install opens, the kept clip comes back
                var next = new PackageKeeper(dir);
                next.Opening();
                PackageList.Current.Clear();
                var r = next.Restore(g);
                var item = Assert.Single(PackageList.Current.Items);
                Assert.Same(hello, item.Slot);
                Assert.Equal(b, item.Record);
                var package = new PackageView();
                package.SetInstall(g);
                package.ShowRestored(r, next.FilePath);
                Assert.StartsWith("Back from the last run: 1 kept clip,", package.RestoreText.Text);
                Assert.Equal(System.Windows.Visibility.Collapsed, package.DroppedText.Visibility);
                // what was left out is said plainly
                package.ShowRestored(new PackageRestore([], ["mca_emote_Goodbye: these game files have no clip of its ID"], ["2 clips kept for the game folder X:\\Other (X:\\Other\\DataPC_patch_01.forge)"], null), next.FilePath);
                Assert.Contains("Not shown, as they don't fit the game files open now (1); they stay kept until you forget them:\n  mca_emote_Goodbye: these game files have no clip of its ID", package.DroppedText.Text);
                Assert.Contains("Kept for other game files, and shown when those are open: 2 clips kept for the game folder X:\\Other", package.DroppedText.Text);
                // and those that don't fit can be forgotten: the keeper asked, the screen says so; a failure is said
                Assert.Equal(System.Windows.Visibility.Collapsed, package.ForgetButton.Visibility);   // no keeper to ask: no button
                var asked = new List<string?>();
                string? answer = "The kept clips couldn't be forgotten (IOException): the disk is full";
                package.ForgetRequested = () => { asked.Add(answer); return answer; };
                var dropped = new PackageRestore([], ["mca_emote_Goodbye: these game files have no clip of its ID"], ["2 clips kept for the game folder X:\\Other (X:\\Other\\DataPC_patch_01.forge)"], null);
                package.ShowRestored(dropped, next.FilePath);
                Assert.Equal(System.Windows.Visibility.Visible, package.ForgetButton.Visibility);
                package.ForgetButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(System.Windows.Visibility.Visible, package.ErrorBox.Visibility);
                Assert.Equal(answer, package.ErrorText.Text);
                Assert.Contains("mca_emote_Goodbye", package.DroppedText.Text);
                answer = null;
                package.ForgetButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(2, asked.Count);
                Assert.DoesNotContain("mca_emote_Goodbye", package.DroppedText.Text);
                Assert.Contains("Kept for other game files", package.DroppedText.Text);
                Assert.Equal(System.Windows.Visibility.Collapsed, package.ForgetButton.Visibility);
                // the window's keeper forgets in its kept file: nothing kept here that doesn't fit, so nothing forgotten and no error
                Assert.Null(next.ForgetNotFitting());
                // removing the clip saves the empty list: a third run finds nothing
                PackageList.Current.Remove(hello.Id);
                next.Opening();
                var third = new PackageKeeper(dir);
                third.Opening();
                PackageList.Current.Clear();
                Assert.Empty(third.Restore(g).Restored);
                third.Opening();
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>The clips of a build taken out of the kept file after another running copy of the tool changed this game folder's list
    /// there: the other copy's file is kept aside, and the keeper says so once, as a save does.</summary>
    [GameDataFact]
    public void ClipsBuiltTakenOutOfAListAnotherCopyChangedSaySo()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "keeper-elsewhere-" + Guid.NewGuid().ToString("N"));
        try
        {
            Wpf.Run(() =>
            {
                PackageItem Edited(string name)
                {
                    var c = g.FindClip(name)!;
                    var b = c.Bytes.ToArray();
                    b[^1] ^= 0x01;
                    return new PackageItem(c, b, ClipChange.Mirror(), CheckLevel.Pass, "the report", DateTime.Now);
                }
                var keeper = new PackageKeeper(dir);
                var said = new List<string>();
                keeper.SaveFailed += said.Add;
                keeper.Opening();
                PackageList.Current.Clear();
                Assert.Empty(keeper.Restore(g).Restored);
                // another running copy keeps a clip on these game files since
                new PackageStore(dir).Save(g, [Edited("mca_emote_Hello")]);
                // a build of another clip ends (nothing of it on the screen): the file is written, the other copy's kept aside, and said once
                keeper.RemoveBuilt(g, [Edited("mcm_nwp_fm_std_V0_idle_03")]);
                Assert.StartsWith("Another running copy of the tool changed the package list kept between runs.", Assert.Single(said));
                Assert.Single(Directory.GetFiles(dir, "kept-clips.*-*.json"));
                keeper.Opening();
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>Opening game files writes nothing to the kept file, whatever it holds (here another game folder's list, which the open
    /// says is kept for those files); and the vanilla forge backup is remembered in the settings, back on the next run, and planned with.</summary>
    [GameDataFact]
    public void AnOpenNeverWritesTheKeptFileAndTheBackupIsRemembered()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "keeper-open-" + Guid.NewGuid().ToString("N"));
        try
        {
            Wpf.RunAsync(async () =>
            {
                var hello = g.FindClip("mca_emote_Hello")!;
                var b = hello.Bytes.ToArray();
                b[^1] ^= 0x01;
                // the kept file holds a list of another game folder's
                var store = new PackageStore(dir);
                store.Save(g, [new PackageItem(hello, b, ClipChange.Imported("M72.glb"), CheckLevel.Pass, "the report", DateTime.Now)]);
                var n = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!;
                n["Lists"]![0]!["GameFolder"] = @"X:\Other install";
                File.WriteAllText(store.FilePath, n.ToJsonString());
                var before = File.ReadAllBytes(store.FilePath);
                // an open: nothing comes back, the other list is said, the file is as it was
                var keeper = new PackageKeeper(dir);
                keeper.Opening();
                PackageList.Current.Clear();
                var r = keeper.Restore(g);
                Assert.Empty(r.Restored);
                Assert.Single(r.Elsewhere);
                Assert.Equal(before, File.ReadAllBytes(store.FilePath));
                keeper.Opening();

                // the backup: remembered in the settings file, back in a new screen (the next run), and the plan made with it
                string settings = Path.Combine(dir, "settings.json");
                var package = new PackageView();
                package.UseSettings(settings);
                package.SetInstall(g);
                Assert.Null(package.Backup);
                await package.SetBackup(GameDataFactAttribute.Forge);
                Assert.Equal(GameDataFactAttribute.Forge, ViewerSettings.Load(settings).BackupForge);
                var next = new PackageView();
                next.UseSettings(settings);
                next.SetInstall(g);
                Assert.Equal(GameDataFactAttribute.Forge, next.Backup);
                Assert.Equal(GameDataFactAttribute.Forge, next.BackupBox.Text);
                Assert.Equal(GameDataFactAttribute.Forge, next.PlannedWith?.Backup);
                Assert.True(next.ClearBackupButton.IsEnabled);
                await next.SetBackup(null);
                Assert.Null(ViewerSettings.Load(settings).BackupForge);
                Assert.Null(next.PlannedWith?.Backup);
                Assert.DoesNotContain(GameDataFactAttribute.Forge!, (string)next.BackupBox.ToolTip);

                // a settings file that can't be written (a folder stands at its name): said on the screen, and the backup still shown,
                // in its box's help, and planned with
                string blocked = Path.Combine(dir, "blocked");
                Directory.CreateDirectory(blocked);
                var third = new PackageView();
                third.UseSettings(blocked);
                third.SetInstall(g);
                await third.SetBackup(GameDataFactAttribute.Forge);
                Assert.Equal(System.Windows.Visibility.Visible, third.ErrorBox.Visibility);
                Assert.StartsWith("The vanilla forge backup couldn't be remembered for the next run:", third.ErrorText.Text);
                Assert.Equal(GameDataFactAttribute.Forge, third.Backup);
                Assert.Equal(GameDataFactAttribute.Forge, third.PlannedWith?.Backup);
                // the install here is that very forge, so the tooltip says it is the install's own, not a backup from before modding
                Assert.StartsWith(GameDataFactAttribute.Forge + ": that is your game folder's own forge", (string)third.BackupBox.ToolTip);
                Assert.True(Directory.Exists(blocked));
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
