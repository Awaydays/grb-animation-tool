using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Logs;

namespace GrbAnimTool.App.Tests;

/// <summary>Picking clips on the fake install, in no window shown: the Datafile filter found by typed words with its fixed choices on top;
/// a log line of another datafile's clip jumping to it in the Library; Replace's source list holding only the clips the clips replaced can
/// take (the others, with Show every clip, greyed with why); several clips selected in the Library taken by the Edit screen as Replace's
/// clips. Nothing is written outside the test's own folder.</summary>
[Collection("wpf")]
public sealed class PickingTests : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "picking-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    const string ChickenIdle = "TGT_ANM_Chicken_Idle_Std_01", ChickenIdle2 = "TGT_ANM_Chicken_Idle_Std_02", ChickenDeath = "TGT_ANM_Chicken_Death_L";
    const string Bootstrap = "Game Bootstrap Settings";

    static string Label(object o) => o is ComboBoxItem i ? $"{i.Content}" : $"{o}";

    static ViewerArgs Args() => ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--forge", GameDataFactAttribute.Forge!]);

    /// <summary>The Datafile filter takes typed words: its list narrows to the datafiles whose names hold them, All, the animation
    /// datafile and Every other datafile staying at its top; a datafile chosen keeps its clips; the animation datafile and Every other
    /// datafile split the rows; the main search still finds clips by their datafile's name.</summary>
    [GameDataFact]
    public void TheDatafileFilterFindsADatafileByTypedWords() => Wpf.RunAsync(async () =>
    {
        var lib = new LibraryView();
        Assert.Null(await lib.LoadForShotsAsync(Args()));
        var panel = lib.ClipPanel;
        var box = panel.DatafileBox;
        int all = panel.Rows.Count;
        Assert.True(box.IsEditable, "the Datafile filter takes typed words");
        var labels = box.Items.Cast<object>().Select(Label).ToList();
        Assert.True(labels.Count > 2000, $"{labels.Count} choices");
        Assert.Equal("All datafiles", labels[0]);
        Assert.StartsWith("MIS_Y2E4_Katya_Maksimov (the animation datafile)", labels[1]);
        Assert.StartsWith("Every other datafile", labels[2]);
        // words typed (in any case, in part) narrow the list; nothing is filtered until a choice is made
        box.Text = "bootstrap sett";
        var shown = box.Items.Cast<object>().ToList();
        Assert.Equal(["All datafiles", labels[1], labels[2]], shown.Take(3).Select(Label));
        Assert.Single(shown.Skip(3));
        Assert.StartsWith(Bootstrap + "  (", Label(shown[3]));
        Assert.Equal(all, panel.Rows.Count);
        box.SelectedItem = shown[3];
        Assert.Contains(panel.Rows, c => c.Name == ChickenIdle);
        Assert.All(panel.Rows, c => Assert.True(c.DatafileName == Bootstrap || c.AlsoIn.Any(p => p.Name == Bootstrap)));
        // words matching no datafile: the fixed choices (and the one chosen) only
        box.Text = "no such datafile at all";
        Assert.Equal(4, box.Items.Count);
        // the animation datafile, and every other: the rows split between them
        box.SelectedItem = box.Items.Cast<object>().ElementAt(1);
        int katya = panel.Rows.Count;
        Assert.All(panel.Rows, c => Assert.True(c.InAnimationDatafile));
        box.SelectedItem = box.Items.Cast<object>().ElementAt(2);
        Assert.All(panel.Rows, c => Assert.False(c.InAnimationDatafile));
        Assert.Contains(panel.Rows, c => c.Name == ChickenIdle);
        Assert.Equal(all, katya + panel.Rows.Count);
        box.SelectedItem = box.Items.Cast<object>().ElementAt(0);
        Assert.Equal(all, panel.Rows.Count);
        // the main search still matches datafile names
        panel.SearchText = Bootstrap;
        Assert.Contains(panel.Rows, c => c.Name == ChickenIdle);
    });

    /// <summary>A log line of another datafile's clip (a chicken's idle, in the bootstrap datafile) jumps to the clip in the Library, as
    /// the animation datafile's do; a line of an ID no datafile lists opens nothing, and its tooltip says why.</summary>
    [GameDataFact]
    public void ALogLineOfAnotherDatafilesClipJumpsToIt()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "clips_DX11_2026-11-27_120000.csv");
        File.WriteAllText(path, LogScreenTests.Head("DX11", "2026-11-27 12:00:00.000") + LogScreenTests.Start(1, player: false, clip: 0x000001A3850044A5) + "\n"
            + LogScreenTests.Start(2, clip: 0x00000ABCDEF01234) + "\n" + LogScreenTests.Start(3) + "\n");
        Wpf.RunAsync(async () =>
        {
            Theme.Apply(ThemeChoice.Light);
            var args = ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--forge", GameDataFactAttribute.Forge!, "--index-cache", Path.Combine(_dir, "cache"),
                "--package-store", Path.Combine(_dir, "kept"), "--settings", Path.Combine(_dir, "settings.json"),
                "--shot", Path.Combine(_dir, "unused.png") + "||||log", "--log-file", path]);
            var w = new MainWindow(args);
            try
            {
                Assert.Null(await w.LibraryPage.LoadForShotsAsync(args));
                Assert.Null(await w.LogPage.StartForShotsAsync(args));
                w.Tabs.SelectedIndex = 4;
                var lines = w.LogPage.Lines;
                Assert.Equal(3, lines.Count);
                Assert.Equal((ClipLookup.Found, ChickenIdle, true), (lines[0].Lookup, lines[0].ClipText, lines[0].Opens));
                Assert.Equal(Bootstrap, lines[0].Clip!.DatafileName);
                // the Library showing a set on its own, which doesn't hold the chicken
                var lib = w.LibraryPage;
                for (int i = 0; i < 1200 && lib.Sets is null; i++) await Task.Delay(100);
                Assert.NotNull(lib.ClipPanel.Select("fm_std_V0_idle_cool_01"));
                lib.ToggleSet();
                Assert.True(lib.ClipPanel.ShowingSet);
                // the chicken's line: the Library shown with the chicken chosen (the set left)
                w.LogPage.Open(lines[0]);
                Assert.False(lib.ClipPanel.ShowingSet);
                Assert.Equal(0, w.Tabs.SelectedIndex);
                Assert.Equal(ChickenIdle, w.LibraryPage.ClipPanel.SelectedClip?.Name);
                Assert.Equal(ChickenIdle, w.LibraryPage.CurrentClip?.Name);
                // the animation datafile's line: the same
                w.LogPage.Open(lines[2]);
                Assert.Equal("mca_emote_Hello", w.LibraryPage.CurrentClip?.Name);
                // an ID no datafile lists: nothing opens, and the line's tooltip says why
                Assert.False(lines[1].Opens);
                Assert.Contains("the Library doesn't list it", lines[1].Tip);
                w.LogPage.Open(lines[1]);
                Assert.Equal("mca_emote_Hello", w.LibraryPage.CurrentClip?.Name);
            }
            finally { w.LogPage.Stop(); }
        });
    }

    static IReadOnlyList<object> SourceItems(EditView e) => ((IEnumerable?)e.ReplaceList.ItemsSource)?.Cast<object>().ToList() ?? [];
    static ClipEntry ClipOf(object o) => o as ClipEntry ?? (ClipEntry)o.GetType().GetProperty("Clip")!.GetValue(o)!;
    static bool Takes(object o) => o is ClipEntry || (bool)o.GetType().GetProperty("Takes")!.GetValue(o)!;
    static string? Refusal(object o) => o.GetType().GetProperty("Refusal")?.GetValue(o) as string;

    /// <summary>Replace's sources for a chicken's idle: the chicken's own clips, never a human's; Show every clip lists the rest greyed,
    /// each with why (both skeletons named), and a greyed one can't be chosen; several clips replaced at once take only what every one can.</summary>
    [GameDataFact]
    public void ReplacesSourcesAreTheClipsTheClipCanTake()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        try
        {
            Wpf.RunAsync(async () =>
            {
                var edit = new EditView();
                edit.SetInstall(g, true, new LibraryView(), marks: false);
                Assert.Null(await edit.ChooseClipAsync(ChickenIdle));
                var chicken = g.FindClip(ChickenIdle)!;
                edit.ReplaceChoice.IsChecked = true;
                // a human's clips: none offered for the chicken
                edit.ReplaceSearch.Text = "CivM_Act_day_SitSmoking";
                Assert.DoesNotContain(SourceItems(edit), o => ClipOf(o).OnSkeleton != chicken.OnSkeleton && Takes(o));
                Assert.Empty(SourceItems(edit));
                // the chicken's own: offered
                edit.ReplaceSearch.Text = "TGT_ANM_Chicken";
                Assert.Contains(SourceItems(edit), o => ClipOf(o).Name == ChickenDeath && Takes(o));
                Assert.All(SourceItems(edit), o => Assert.Null(g.ReplaceProblem(chicken, ClipOf(o))));
                // Show every clip: the human's too, greyed, each with why, both skeletons named
                var every = Assert.IsType<CheckBox>(edit.FindName("ShowEveryClipBox"));
                every.IsChecked = true;
                edit.ReplaceSearch.Text = "CivM_Act_day_SitSmoking_Anxious_col_sit_02";
                var human = SourceItems(edit).First(o => ClipOf(o).Name == "CivM_Act_day_SitSmoking_Anxious_col_sit_02");
                Assert.False(Takes(human));
                Assert.Equal(g.ReplaceProblem(chicken, ClipOf(human)), Refusal(human));
                Assert.Contains("the human body", Refusal(human));
                Assert.Contains(g.SkeletonName(chicken.OnSkeleton), Refusal(human));
                // a greyed row chosen: not taken as the source
                edit.ReplaceList.SelectedItem = human;
                Assert.Equal("", edit.ReplaceSourceText.Text);
                edit.ReplaceSearch.Text = ChickenDeath;
                edit.ReplaceList.SelectedItem = SourceItems(edit).First(o => ClipOf(o).Name == ChickenDeath);
                Assert.NotEqual("", edit.ReplaceSourceText.Text);
                every.IsChecked = false;
                // two chickens selected: the chicken's death offered; a chicken and a human: neither's clips
                var two = edit.ClipPanel.Clips.Where(c => c.Name is ChickenIdle or ChickenIdle2).ToList();
                edit.TakeSelection(two);
                Assert.Equal(ClipSetKind.Chosen, edit.SetShown!.Kind);
                Assert.Contains(SourceItems(edit), o => ClipOf(o).Name == ChickenDeath);
                edit.TakeSelection([two[0], g.FindClip("mca_emote_Clap")!]);
                Assert.DoesNotContain(SourceItems(edit), o => ClipOf(o).Name == ChickenDeath);
                edit.ReplaceSearch.Text = "mca_emote_Hello";
                Assert.DoesNotContain(SourceItems(edit), o => ClipOf(o).Name == "mca_emote_Hello");
            });
        }
        finally
        {
            Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g), InstallWork.Sets(g)).GetAwaiter().GetResult();
        }
    }

    /// <summary>Several clips selected in the Library (Ctrl or Shift): the Edit screen, shown, takes them as Replace's clips, as a selection
    /// in its own list, the Library's clip shown; a single selection works as before; the same selection isn't taken again on the next
    /// showing (what was chosen on the screen since stays), but Use the Library's clip takes it again.</summary>
    [GameDataFact]
    public void ALibrarySelectionOfSeveralIsTheEditScreensReplaceClips() => Wpf.RunAsync(async () =>
    {
        var lib = new LibraryView();
        Assert.Null(await lib.LoadForShotsAsync(Args()));
        var g = (GameInstall)typeof(LibraryView).GetField("_install", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lib)!;
        // the sets worked out first: their arrival works the Library's rows out anew, which ends a selection of several there
        for (int i = 0; i < 1200 && lib.Sets is null; i++) await Task.Delay(100);
        Assert.NotNull(lib.Sets);
        // the Edit screen as the window holds it: the Library's own screen beside it
        {
            var edit = new EditView();
            edit.SetInstall(g, true, lib, marks: false);
            var three = lib.ClipPanel.Rows.Where(c => c.Name is "mca_emote_Clap" or "mca_emote_Hello" or "mca_emote_Sad").ToList();
            Assert.Equal(3, three.Count);
            lib.ClipPanel.SelectMany(three);
            for (int i = 0; i < 200 && lib.CurrentClip != three[^1]; i++) await Task.Delay(50);
            Assert.Same(three[^1], lib.CurrentClip);
            edit.Shown();
            for (int i = 0; i < 200 && edit.SetShown?.Kind != ClipSetKind.Chosen; i++) await Task.Delay(50);
            Assert.Equal(ClipSetKind.Chosen, edit.SetShown?.Kind);
            Assert.Equal(three.Select(c => c.Name).Order(StringComparer.Ordinal), edit.SetRows.Select(r => r.Clip.Name).Order(StringComparer.Ordinal));
            Assert.True(edit.WholeSetBox.IsChecked);
            Assert.Equal(three.Select(c => c.Name).Order(StringComparer.Ordinal), edit.ClipPanel.SelectedClips.Select(c => c.Name).Order(StringComparer.Ordinal));
            for (int i = 0; i < 200 && edit.ClipPanel.Shown != three[^1]; i++) await Task.Delay(50);
            Assert.Same(three[^1], edit.ClipPanel.Shown);
            // one clip chosen on the Edit screen, then the screen shown again: the same selection isn't taken again
            edit.TakeSelection([three[0]]);
            Assert.NotEqual(ClipSetKind.Chosen, edit.SetShown?.Kind);
            edit.Shown();
            await Task.Delay(300);
            Assert.NotEqual(ClipSetKind.Chosen, edit.SetShown?.Kind);
            // Use the Library's clip: taken again
            edit.UseLibraryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 200 && edit.SetShown?.Kind != ClipSetKind.Chosen; i++) await Task.Delay(50);
            Assert.Equal(ClipSetKind.Chosen, edit.SetShown?.Kind);
            // a single clip selected in the Library: the Edit screen, which shows a clip, keeps it, as before
            Assert.NotNull(lib.ClipPanel.Select(three[0]));
            edit.Shown();
            await Task.Delay(300);
            Assert.Equal(ClipSetKind.Chosen, edit.SetShown?.Kind);
            // another clip chosen on the Edit screen while the Library's are taken: the clip chosen stands
            lib.ClipPanel.SelectMany(three);
            edit.Shown();
            Assert.Null(await edit.ChooseClipAsync("fm_std_V0_idle_cool_01"));
            // the take's end awaited (by reflection, so the file builds on code without it), then the list's own wait for a row picked
            if (typeof(EditView).GetProperty("LibrarySelectionTaken", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(edit) is Task taking) await taking;
            await Task.Delay(800);
            Assert.Equal("fm_std_V0_idle_cool_01", edit.ClipPanel.Shown?.Name);
            Assert.NotEqual(ClipSetKind.Chosen, edit.SetShown?.Kind);
            // past the most a selection takes (Ctrl+A): none selected row by row on the screen, which says why
            var many = lib.ClipPanel.Rows.Take(EditView.MaxChosen + 50).ToList();
            lib.ClipPanel.SelectMany(many);
            edit.Shown();
            for (int i = 0; i < 200 && !edit.EditSetText.Text.StartsWith("Over ", StringComparison.Ordinal); i++) await Task.Delay(50);
            Assert.StartsWith($"Over {EditView.MaxChosen} clips selected", edit.EditSetText.Text);
            Assert.True(edit.ClipPanel.SelectedClips.Count <= 1);
        }
    });
}
