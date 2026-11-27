using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>Sets on the screens, on the fake install: the Library's Set column and Show the set; the Edit screen's set, its Replace for
/// the whole set from one long source lined up, its preview with the graph's blend, and its results kept together; the Package screen's
/// look mark. No window is shown; nothing is written outside the test's own folders.</summary>
[Collection("wpf")]
public sealed class SetScreenTests
{
    static readonly string[] StandingIdles = ["fm_std_V0_idle_cool_01", "fm_std_V0_idle_cool_04", "fm_std_V0_idle_cool_05", "fm_std_V0_idle_cool_06"];

    [GameDataFact]
    public void TheLibraryShowsTheSetAndNarrowsTheListToIt() => Wpf.RunAsync(async () =>
    {
        var lib = new LibraryView();
        Assert.Null(await lib.LoadForShotsAsync(ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--forge", GameDataFactAttribute.Forge!])));
        for (int i = 0; i < 1200 && lib.Sets is null; i++) await Task.Delay(100);
        Assert.NotNull(lib.Sets);
        int all = lib.ClipPanel.Rows.Count;
        var idle = lib.ClipPanel.Rows.First(c => c.Name == StandingIdles[0]);
        Assert.NotNull(lib.ClipPanel.Select(idle));
        Assert.StartsWith("Set (random) of 4 clips, at ReferenceStateMachines-Healthy_V0 / Idle_Explo; switches blend over 0.3 s.", lib.SetText.Text);
        Assert.Equal(Visibility.Collapsed, lib.ProbableSetHelp.Visibility);
        Assert.True(lib.ShowSetButton.IsEnabled);
        lib.ToggleSet();
        Assert.Equal(StandingIdles, lib.ClipPanel.Rows.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Show every clip", lib.ShowSetButton.Content);
        lib.ToggleSet();
        Assert.Equal(all, lib.ClipPanel.Rows.Count);
        Assert.Equal("Show the set", lib.ShowSetButton.Content);
    });

    [GameDataFact]
    public void TheEditScreenReplacesTheWholeSetPreviewsItAndKeepsItTogether()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        try
        {
            Wpf.RunAsync(async () =>
            {
                PackageList.Current.Clear();
                var edit = new EditView();
                edit.SetInstall(g, true, new LibraryView(), marks: false);
                await InstallWork.Sets(g);
                Assert.Null(await edit.ChooseClipAsync(StandingIdles[0]));
                for (int i = 0; i < 100 && edit.SetShown is null; i++) await Task.Delay(50);
                var set = Assert.IsType<ClipSet>(edit.SetShown);
                Assert.Equal(StandingIdles, edit.SetRows.Select(r => r.Clip.Name));
                Assert.True(edit.WholeSetBox.IsEnabled);

                // the game's own set previewed: its clips one after another, with the graph's blend
                Assert.Null(await edit.PreviewSetAsync());
                Assert.True(edit.Previewing);
                Assert.StartsWith("Set preview: fm_std_V0_idle_cool_01 (1 of 4), 0.3 s blends (the graph's)", edit.OverlayText.Text);
                Assert.Null(await edit.PreviewSetAsync());
                Assert.False(edit.Previewing);

                // the whole set from the wade, cut, looped and lined up
                edit.ReplaceChoice.IsChecked = true;
                edit.WholeSetBox.IsChecked = true;
                edit.ReplaceSearch.Text = "mcm_ar_fm_std_wade_low_V0_01";
                edit.ChooseSource("mcm_ar_fm_std_wade_low_V0_01");
                edit.LoopFitBox.IsChecked = true;
                Assert.True(edit.LineUpBox.IsChecked);
                Assert.True(edit.DistinctPiecesBox.IsChecked);
                Assert.Null(await edit.ApplyAsync());
                var outs = Assert.IsAssignableFrom<IReadOnlyList<EditOutcome>>(edit.SetOutcomes);
                Assert.Equal(StandingIdles, outs.Select(o => o.Clip.Name));
                Assert.All(outs, o => Assert.Equal(o.Clip.LastTick, o.Edited.LastTick));
                Assert.Contains("Into the next:", edit.SetResultText.Text);
                Assert.Contains("lined up on the pose closest to all the pieces", edit.SetResultText.Text);
                Assert.Contains("Feet slide: OK.", edit.SetResultText.Text);
                Assert.StartsWith("Game: ", edit.VerdictText.Text);
                // an option changed after Apply: the results are dropped, so nothing the screen no longer shows can be kept
                edit.LineUpBox.IsChecked = false;
                Assert.Null(edit.SetOutcomes);
                Assert.Equal("Nothing applied to add.", edit.Keep());
                Assert.Empty(PackageList.Current.Items);
                edit.LineUpBox.IsChecked = true;
                // changed while it runs: what it made is dropped when it ends, said, and never kept
                var running = edit.ApplyAsync();
                edit.LineUpBox.IsChecked = false;
                edit.LineUpBox.IsChecked = true;
                Assert.Equal("The set's options changed while it was being made: Apply makes it again with them.", await running);
                Assert.Null(edit.SetOutcomes);
                Assert.Equal("Nothing applied to add.", edit.Keep());
                Assert.Null(await edit.ApplyAsync());
                Assert.Equal(4, edit.SetOutcomes!.Count);
                // the results previewed, then kept together
                Assert.Null(await edit.PreviewSetAsync());
                Assert.True(edit.Previewing);
                Assert.Null(edit.Keep());
                Assert.Equal(StandingIdles, PackageList.Current.Items.Select(i => i.Slot.Name));
                Assert.StartsWith("Added the set's 4 clips to the package.", edit.StatusText.Text);
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g), InstallWork.Sets(g)).GetAwaiter().GetResult();
        }
    }

    /// <summary>The Library's sets and datafiles: the Set filter's kinds, Show the set putting the search and the filters aside under a bar
    /// and bringing them back, the search finding a set by its state's name and a clip by its datafile's, the Datafile filter (a clip
    /// counted in every datafile holding it), and several clips selected with Ctrl exported together.</summary>
    [GameDataFact]
    public void TheLibraryFiltersBySetAndDatafileAndSelectsSeveral() => Wpf.RunAsync(async () =>
    {
        var lib = new LibraryView();
        Assert.Null(await lib.LoadForShotsAsync(ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--forge", GameDataFactAttribute.Forge!])));
        for (int i = 0; i < 1200 && lib.Sets is null; i++) await Task.Delay(100);
        var sets = Assert.IsType<ClipSets>(lib.Sets);
        var panel = lib.ClipPanel;
        int all = panel.Rows.Count;
        // the set filter: each kind keeps the clips in a set of that kind (the control: "not in a set" none of them)
        panel.SetPlaceFilters(4, null);
        Assert.NotEmpty(panel.Rows);
        Assert.All(panel.Rows, c => Assert.Contains(sets.Of(c.Id), s => s.Kind == ClipSetKind.Blended));
        panel.SetPlaceFilters(3, null);
        Assert.All(panel.Rows, c => Assert.Contains(sets.Of(c.Id), s => s.Kind is ClipSetKind.Random or ClipSetKind.Sequence));
        panel.SetPlaceFilters(1, null);
        int inSet = panel.Rows.Count;
        panel.SetPlaceFilters(2, null);
        Assert.All(panel.Rows, c => Assert.Empty(sets.Of(c.Id)));
        Assert.Equal(all, inSet + panel.Rows.Count);
        panel.SetPlaceFilters(0, null);
        // the search finds a set by its state's name
        panel.SearchText = "Idle_Explo";
        Assert.True(StandingIdles.All(n => panel.Rows.Any(c => c.Name == n)));
        // Show the set: the search and the filters put aside, a bar naming the set; leaving it brings them back
        var idle = panel.Rows.First(c => c.Name == StandingIdles[0]);
        Assert.NotNull(panel.Select(idle));
        panel.SearchText = "smoking";
        int smoking = panel.Rows.Count;
        Assert.True(smoking > 0);
        lib.ToggleSet();
        Assert.Equal(StandingIdles, panel.Rows.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Set: Idle_Explo (4 clips)", panel.SetBarLabel);
        Assert.Equal("", panel.SearchText);
        lib.ToggleSet();
        Assert.Equal("", panel.SetBarLabel);
        Assert.Equal("smoking", panel.SearchText);
        Assert.Equal(smoking, panel.Rows.Count);
        // the datafile filter and the search by a datafile's name: the smoker is in its own datafile and a tag set's
        panel.SearchText = "";
        var smoker = panel.Clips.First(c => c.Name == "CivM_Act_day_StandSmoking_col_std_03");
        panel.SetPlaceFilters(0, smoker.AlsoIn[0].Name);
        Assert.Contains(smoker, panel.Rows);
        Assert.All(panel.Rows, c => Assert.True(c.DatafileName == smoker.AlsoIn[0].Name || c.AlsoIn.Any(p => p.Name == smoker.AlsoIn[0].Name)));
        panel.SetPlaceFilters(0, "MIS_Y2E4_Katya_Maksimov");
        Assert.DoesNotContain(smoker, panel.Rows);
        panel.SetPlaceFilters(0, null);
        panel.SearchText = smoker.AlsoIn[0].Name;
        Assert.Contains(smoker, panel.Rows);
        // several selected: exported together, each its own .glb
        panel.SearchText = "fm_std_V0_idle_cool";
        var two = panel.Rows.Where(c => c.Name is "fm_std_V0_idle_cool_01" or "fm_std_V0_idle_cool_04").ToList();
        panel.SelectMany(two);
        Assert.Equal(2, panel.SelectedClips.Count);
        string dir = Path.Combine(AppContext.BaseDirectory, "multi-export-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.Null(await lib.ExportClipsAsync(panel.SelectedClips, dir));
            Assert.Equal(2, Directory.GetFiles(dir, "*.glb").Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    });

    /// <summary>The Edit screen's set: each clip's start beside it (the single start box gone), Suggest filling them all, a start typed
    /// taken as it is; the list as tall as its clips, no scroll bar of its own; a blended set taking a source per clip by default, and the
    /// warning with one source for all; the state's name in full; and clips selected together in the list replaced as a set.</summary>
    [GameDataFact]
    public void TheEditScreensSetTakesEachClipsStart()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        try
        {
            Wpf.RunAsync(async () =>
            {
                PackageList.Current.Clear();
                var edit = new EditView();
                edit.SetInstall(g, true, new LibraryView(), marks: false);
                var sets = (await InstallWork.Sets(g))!;
                Assert.Null(await edit.ChooseClipAsync(StandingIdles[0]));
                for (int i = 0; i < 100 && edit.SetShown is null; i++) await Task.Delay(50);
                Assert.Contains("\"Idle_Explo\"", edit.EditSetText.Text);
                edit.ReplaceChoice.IsChecked = true;
                edit.WholeSetBox.IsChecked = true;
                edit.ReplaceSearch.Text = "mcm_ar_fm_std_wade_low_V0_01";
                edit.ChooseSource("mcm_ar_fm_std_wade_low_V0_01");
                edit.LoopFitBox.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, edit.CutPanel.Visibility);
                Assert.All(edit.SetRows, r => Assert.Equal(Visibility.Visible, r.StartShown));
                // the list has no scroll bar of its own: the side panel's scrolls it
                Assert.Empty(Descendants<ScrollViewer>(edit.SetClipList));
                Assert.True(double.IsPositiveInfinity(edit.SetClipList.MaxHeight));
                // Suggest: every start, as the core's distribution gives them
                Assert.Null(await edit.SuggestPiecesAsync());
                Assert.All(edit.SetRows, r => Assert.Matches("^[0-9]+$", r.Start));
                // a start typed is taken as it is; the others as suggested
                edit.SetRows[0].Start = "700";
                Assert.Null(await edit.ApplyAsync());
                var outs = edit.SetOutcomes!;
                Assert.Contains("fm_std_V0_idle_cool_01: source frames 700 to 836", edit.SetResultText.Text);
                Assert.Equal("700", edit.SetRows[0].Start);
                Assert.Equal(4, outs.Count);
                // another source (a civilian's sitting smoke, 1,496 ticks: the wade's start 802 for _05 lies past its end): the starts
                // belonged to the wade, so they go (each suggested anew), and so do the results
                edit.ReplaceSearch.Text = "CivM_Act_day_SitSmoking_Anxious_col_sit_02";
                edit.ChooseSource("CivM_Act_day_SitSmoking_Anxious_col_sit_02");
                Assert.All(edit.SetRows, r => Assert.Equal("", r.Start));
                Assert.Null(edit.SetOutcomes);
                // a start that isn't a number refuses the set, saying so
                edit.SetRows[1].Start = "abc";
                Assert.StartsWith("A clip's start", await edit.ApplyAsync());
                edit.SetRows[1].Start = "";
                Assert.Null(await edit.ApplyAsync());
                Assert.All(edit.SetRows, r => Assert.Matches("^[0-9]+$", r.Start));

                // a blended set: a source per clip by default; with one source for all, the warning naming its value
                var blended = sets.All.First(s => s.Kind == ClipSetKind.Blended && s.Input is not null && ReferenceEquals(sets.Main(s.Clips[0]), s) && g.FindClip(s.Clips[0]) is not null);
                Assert.Null(await edit.ChooseClipAsync(g.FindClip(blended.Clips[0])!.Name));
                for (int i = 0; i < 100 && edit.SetShown?.Kind != ClipSetKind.Blended; i++) await Task.Delay(50);
                Assert.True(edit.PerClipSourceChoice.IsChecked);
                edit.WholeSetBox.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, edit.BlendedOneSourceWarning.Visibility);
                edit.OneSourceChoice.IsChecked = true;
                Assert.Equal(Visibility.Visible, edit.BlendedOneSourceWarning.Visibility);
                Assert.Equal(HelpTexts.OneSourceOnBlended(blended.Input!), edit.BlendedOneSourceWarning.Text);

                // two clips selected together in the list: Replace takes them as a set's clips
                edit.ClipPanel.ClearFilters();
                var two = edit.ClipPanel.Rows.Where(c => c.Name is "mca_emote_Clap" or "mca_emote_Hello").ToList();
                Assert.Equal(2, two.Count);
                edit.ClipPanel.SelectMany(two);
                // the selection's change, as a click raises it
                edit.TakeSelection(edit.ClipPanel.SelectedClips);
                Assert.Equal(ClipSetKind.Chosen, edit.SetShown!.Kind);
                Assert.Equal(two.Select(c => c.Name).Order(StringComparer.Ordinal), edit.SetRows.Select(r => r.Clip.Name).Order(StringComparer.Ordinal));
                Assert.True(edit.WholeSetBox.IsChecked);
                Assert.StartsWith("2 clips selected", edit.EditSetText.Text);
                // the list's selection changed by the screen (a clip chosen from the Library's): the two no longer stand
                Assert.Null(await edit.ChooseClipAsync("mca_emote_Sad"));
                Assert.NotEqual(ClipSetKind.Chosen, edit.SetShown?.Kind);
                Assert.DoesNotContain("clips selected", edit.EditSetText.Text);
                // and by a search typed
                edit.ClipPanel.SelectMany(two);
                Assert.Equal(ClipSetKind.Chosen, edit.SetShown!.Kind);
                edit.ClipPanel.SearchText = "mca_emote_Clap";
                Assert.NotEqual(ClipSetKind.Chosen, edit.SetShown?.Kind);
                // the selection shrunk to the clip of a game set: "Replace the set" isn't left ticked for that set
                Assert.Null(await edit.ChooseClipAsync("fm_std_V0_idle_cool_01"));
                edit.ClipPanel.ClearFilters();
                var idle = edit.ClipPanel.Rows.Single(c => c.Name == "fm_std_V0_idle_cool_01");
                Assert.Equal(ClipSetKind.Random, edit.SetShown!.Kind);
                edit.WholeSetBox.IsChecked = false;
                edit.TakeSelection([idle, two[0]]);
                Assert.True(edit.WholeSetBox.IsChecked);
                edit.TakeSelection([idle]);
                Assert.False(edit.WholeSetBox.IsChecked == true);
                // past the most a selection takes (Ctrl+A): no set of them built, and the screen says why
                // (with the shown clip's own set ticked before: Replace isn't left on that set either)
                edit.WholeSetBox.IsChecked = true;
                edit.TakeSelection(edit.ClipPanel.Rows.Take(EditView.MaxChosen + 1).ToList());
                Assert.NotEqual(ClipSetKind.Chosen, edit.SetShown?.Kind);
                Assert.False(edit.WholeSetBox.IsChecked == true);
                Assert.StartsWith($"Over {EditView.MaxChosen} clips selected", edit.EditSetText.Text);
                edit.TakeSelection(edit.ClipPanel.Rows.Take(EditView.MaxChosen).ToList());
                Assert.Equal(EditView.MaxChosen, edit.SetRows.Count);
            });
        }
        finally
        {
            Wpf.Run(PackageList.Current.Clear);
            Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g), InstallWork.Sets(g)).GetAwaiter().GetResult();
        }
    }

    static IEnumerable<T> Descendants<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    /// <summary>A package row with a big difference in its look checks: its game verdict as the badge, and the mark beside it.</summary>
    [Fact]
    public void APackageRowShowsTheGameVerdictAndTheLookMark() => Wpf.Run(() =>
    {
        string report = "PASS  length: as long as the clip\nBig difference  feet off the floor: 60.5 cm above\nNote  joint limits: an ankle\nVERDICT: PASS (Game: 1 pass, 0 warn, 0 fail. Look: 1 big difference, 1 note.)\n";
        var item = new PackageItem(new ClipEntry { Index = 94, Id = 0x94, Name = "mca_emote_Clap", Bytes = ReadOnlyMemory<byte>.Empty }, [], ClipChange.Mirror(), CheckLevel.Pass, report, DateTime.Now);
        var row = Assert.Single(PackageView.Rows([item], null, "not built here"));
        Assert.Equal(("PASS", "1 big difference"), (row.Verdict, row.Look));
        var p = new PackageView();
        p.ShowUsersFor([item], null, "not built here");
        Assert.NotNull(p.ItemsPanel.Select(item.Slot));
        Assert.Equal("Checks of mca_emote_Clap: PASS, look: 1 big difference", p.ChecksTitle.Text);
        var rows = Assert.IsType<List<EditView.ReportRow>>(p.CheckList.ItemsSource);
        Assert.Equal(["Big difference", "Note", "PASS"], rows.Select(r => r.Word));
        Assert.Equal(CheckLevel.Warn, rows[0].Level);
        Assert.Equal("", PackageView.Failing([item]));
    });
}
