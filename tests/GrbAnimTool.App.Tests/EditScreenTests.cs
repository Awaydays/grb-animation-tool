using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>The Edit screen's choices as it shows them, laid out in no window shown in both themes: an unavailable edit greyed whole (its
/// slider in the greyed template, its value a dash, its reason in plain words with the detail on hover), the speeds that work on the
/// slider and said under it, and the list's edits column with the clips that allow no edit left out until "Show all clips".</summary>
[Collection("wpf")]
public class EditScreenTests
{
    static EditBlock Arena => new(EditRefusal.ArenaKindMissing, "the new curves (tracks 29) need 5 units of arena kind 3 (alignment 1), and the record's head holds no entry of that kind");

    /// <summary>The cuts of a clip of 100 ticks where only those keeping its start work.</summary>
    internal static TrimChoices StartOnly => new(100, EditJobs.TrimGrid(100), [.. EditJobs.TrimGrid(100).Where(c => c.From == 0)],
        [.. EditJobs.TrimGrid(100).Where(c => c.From > 0).Select(c => (c, Arena))]);

    /// <summary>Choices with mirror and root-motion scale unavailable, the speed working from 1.05x to 1.95x only, trim (only cuts keeping
    /// the start) and swap available.</summary>
    internal static readonly ClipEditChoices Mixed = new(
    [
        EditOption.Of(EditKind.Mirror, new EditBlock(EditRefusal.HandTracks, "it holds hand tracks (58A97CAE), which a mirror in place would leave aiming the hands at the old side"), null),
        EditOption.Of(EditKind.Scale, new EditBlock(EditRefusal.NoRootMotion, "its mover travels 0.00 cm on the ground, nothing to scale"), null),
        EditOption.Of(EditKind.Speed, null, null),
        EditOption.Of(EditKind.Trim, null, null),
        EditOption.Of(EditKind.Swap, null, null),
    ], new FactorChoices(EditKind.Speed, EditJobs.SpeedGrid, EditJobs.Grid(1.05, 1.95, 0.05),
        EditJobs.SpeedGrid.Where(f => f < 1.05 || f > 1.95).Select(f => (f, Arena)).ToList()), FactorChoices.None(EditKind.Scale), StartOnly);

    /// <summary>Choices with every edit unavailable.</summary>
    internal static readonly ClipEditChoices NoEdit = new(
    [
        EditOption.Of(EditKind.Mirror, new EditBlock(EditRefusal.MotionField, "a motion field's selection reads this clip's motion"), null),
        EditOption.Of(EditKind.Scale, new EditBlock(EditRefusal.MotionField, "a motion field's selection reads this clip's motion"), null),
        EditOption.Of(EditKind.Speed, new EditBlock(EditRefusal.LengthHeld, "the clip's length is held by a motion field's segment table"), null),
        EditOption.Of(EditKind.Trim, new EditBlock(EditRefusal.LengthHeld, "the clip's length is held by a motion field's segment table"), null),
        EditOption.Of(EditKind.Swap, new EditBlock(EditRefusal.NoSwapSource, "no other clip in these game files has the same length and the identical track list"), null),
    ], FactorChoices.None(EditKind.Speed), FactorChoices.None(EditKind.Scale), TrimChoices.None);

    static void Lay(FrameworkElement view)
    {
        var w = new Window { Content = view };
        view.Measure(new Size(1484, 861));
        view.Arrange(new Rect(0, 0, 1484, 861));
        view.UpdateLayout();
        w.Content = null;
    }

    [Fact]
    public void UnavailableEditsAreGreyedWholeAndTheSpeedsThatWorkAreOffered()
    {
        Wpf.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (var choice in new[] { ThemeChoice.Light, ThemeChoice.Dark })
            {
                Theme.Apply(choice);
                string mode = ThemeChoices.Name(choice);
                var greyed = Wpf.App.FindResource("Grb.DisabledSlider");
                var disabledText = (SolidColorBrush)Wpf.App.FindResource(Theme.Key("TextDisabled"));
                var edit = new EditView();
                edit.ShowChoices(Mixed);
                Lay(edit);
                // mirror and scale: the choice, the slider and its value greyed, the value a dash, the reason in plain words, the detail on hover
                Assert.False(edit.MirrorChoice.IsEnabled);
                Assert.False(edit.ScaleChoice.IsEnabled);
                Assert.False(edit.ScaleSlider.IsEnabled);
                Assert.Same(greyed, edit.ScaleSlider.Template);
                Assert.Equal("–", edit.ScaleValue.Text);
                Assert.Equal(disabledText.Color, ((SolidColorBrush)edit.ScaleValue.Foreground).Color);
                Assert.Equal(Visibility.Visible, edit.MirrorReason.Visibility);
                Assert.Equal(EditReasons.Plain(EditKind.Mirror, EditRefusal.HandTracks), edit.MirrorReason.Text);
                Assert.Contains("58A97CAE", edit.MirrorReason.ToolTip as string);
                Assert.Equal(EditReasons.Plain(EditKind.Scale, EditRefusal.NoRootMotion), edit.ScaleReason.Text);
                // speed: usable, the slider spanning and snapping to the factors that work, what works said under it
                Assert.True(edit.SpeedSlider.IsEnabled, mode);
                Assert.NotSame(greyed, edit.SpeedSlider.Template);
                Assert.Equal((1.05, 1.95, 1.25), (edit.SpeedSlider.Minimum, edit.SpeedSlider.Maximum, edit.SpeedSlider.Value));
                Assert.Equal(Mixed.Speeds.Works, edit.SpeedSlider.Ticks);
                Assert.True(edit.SpeedSlider.IsSnapToTickEnabled);
                Assert.Equal("1.25x", edit.SpeedValue.Text);
                Assert.Equal(Visibility.Visible, edit.SpeedRangeText.Visibility);
                Assert.StartsWith("This clip's speed can be set from 1.05x to 1.95x.", edit.SpeedRangeText.Text);
                Assert.Contains("arena kind 3", edit.SpeedRangeText.ToolTip as string);
                // trim: what works said under it, the start handle held at the start as only the cuts keeping it work
                Assert.Equal(Visibility.Visible, edit.TrimRangeText.Visibility);
                Assert.StartsWith("Of the trims tried, trims that keep the clip's start can be made. The others can't:", edit.TrimRangeText.Text);
                // the handles stay free (the cuts tried are a sample; the exact cut is tried when they stop), their tooltips saying what worked
                Assert.True(edit.FromThumb.IsEnabled);
                Assert.True(edit.ToThumb.IsEnabled);
                Assert.Contains("only those keeping the clip's start work", edit.FromThumb.ToolTip as string);
                // choosing an edit shows its layout at once: scale overlaid (it isn't available here), speed side by side
                edit.SpeedChoice.IsChecked = true;
                Assert.Equal(CompareLayout.SideBySide, edit.Layout);
                Assert.Equal(1, edit.LayoutBox.SelectedIndex);
                edit.SpeedChoice.IsChecked = false;
                // a move of an available edit's slider chooses it
                Assert.False(edit.SpeedChoice.IsChecked == true);
                edit.SpeedSlider.Value = 1.5;
                Assert.True(edit.SpeedChoice.IsChecked == true);
                Assert.Equal("1.50x", edit.SpeedValue.Text);
                // the swap's source box usable, as swap is available
                Assert.True(edit.SwapPanel.IsEnabled);

                // a clip that allows no edit: every edit's controls greyed, the values dashes
                edit.ShowChoices(NoEdit);
                Lay(edit);
                foreach (var c in new UIElement[] { edit.MirrorChoice, edit.ScaleChoice, edit.SpeedChoice, edit.TrimChoice, edit.SwapChoice, edit.ScaleSlider, edit.SpeedSlider, edit.SourceBox })
                    Assert.False(c.IsEnabled, $"{mode}: {((FrameworkElement)c).Name} stays usable");
                Assert.Same(greyed, edit.SpeedSlider.Template);
                Assert.Equal(("–", "–"), (edit.ScaleValue.Text, edit.SpeedValue.Text));
                Assert.Equal(Visibility.Collapsed, edit.SpeedRangeText.Visibility);
                Assert.Equal((0.5, 2.0), (edit.SpeedSlider.Minimum, edit.SpeedSlider.Maximum));
                Assert.False(edit.ApplyButton.IsEnabled);
            }
        });
    }

    [Fact]
    public void TheListMarksEachClipsEditsAndLeavesOutTheClipsThatAllowNone()
    {
        Wpf.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Theme.Apply(ThemeChoice.Light);
            var edit = new EditView();
            var clips = ThemeScreenTests.Clips("mil_gen_m_col_fm_std_V0_N_DETECTION_", 20, 8400);
            ThemeScreenTests.Fill(edit.ClipPanel, clips);
            var panel = edit.ClipPanel;
            // the edits column after the four every list has
            var headers = ((GridView)panel.List.View).Columns.Select(c => (c.Header as GridViewColumnHeader)?.Content as string).ToList();
            Assert.Equal("Edits", headers[4]);
            Assert.Equal(20, panel.Rows.Count);
            edit.SetChoices(InstallEditChoices.From(clips.ToDictionary(c => c.Id, c => c.Index % 5 == 4 ? NoEdit : Mixed)));
            Lay(edit);
            Assert.Equal(16, panel.Rows.Count);
            Assert.DoesNotContain(panel.Rows, c => c.Index % 5 == 4);
            Assert.Contains("4 that allow no edit left out", panel.CountText.Text);
            Assert.Equal("· · S T W", Mixed.Marks);
            Assert.Contains(AllText(panel.List), t => t == "· · S T W");
            // Show all clips: every clip, those that allow none marked so
            edit.ShowAllBox.IsChecked = true;
            edit.ShowAllBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Lay(edit);
            Assert.Equal(20, panel.Rows.Count);
            Assert.Contains(AllText(panel.List), t => t == "· · · · ·");
        });
    }

    /// <summary>The edits' choices that couldn't be worked out: the list stops waiting, lists every clip, says why, and offers Retry.</summary>
    [Fact]
    public void ChoicesThatFailedAreSaidAndCanBeTriedAgain()
    {
        Wpf.Run(() =>
        {
            Theme.Apply(ThemeChoice.Light);
            var edit = new EditView();
            var clips = ThemeScreenTests.Clips("mil_gen_m_col_fm_std_V0_N_DETECTION_", 20, 8400);
            ThemeScreenTests.Fill(edit.ClipPanel, clips);
            edit.SetChoicesFailed("NullReferenceException: a record of an unforeseen shape");
            Assert.Equal(20, edit.ClipPanel.Rows.Count);
            Assert.Contains("couldn't be worked out (NullReferenceException: a record of an unforeseen shape), so every clip is listed", edit.ClipPanel.CountText.Text);
            Assert.DoesNotContain("working out which edits", edit.ClipPanel.CountText.Text);
            Assert.Equal(Visibility.Visible, edit.RetryButton.Visibility);
            // worked out after all: the list filters, and Retry goes
            edit.SetChoices(InstallEditChoices.From(clips.ToDictionary(c => c.Id, c => c.Index % 5 == 4 ? NoEdit : Mixed)));
            Assert.Equal(16, edit.ClipPanel.Rows.Count);
            Assert.Equal(Visibility.Collapsed, edit.RetryButton.Visibility);
        });
    }

    /// <summary>Marks that aren't whole (the game's graph didn't read, its reason said): the line beside the switch says so, with the
    /// reason, in the warning colour, and Retry is offered. Marks whole but for clips the tool failed on: those clips are listed (never left
    /// out as allowing no edit), the count says how many aren't known, the line says how many the tool failed on, and no Retry is offered
    /// (it would fail the same way). Whole marks after them clear all that.</summary>
    [Fact]
    public void MarksThatArentWholeAndClipsNotToldAreSaid()
    {
        Wpf.Run(() =>
        {
            Theme.Apply(ThemeChoice.Light);
            var edit = new EditView();
            var clips = ThemeScreenTests.Clips("mil_gen_m_col_fm_std_V0_N_DETECTION_", 20, 8400);
            ThemeScreenTests.Fill(edit.ClipPanel, clips);
            // 0-7 allow no edit, the rest every edit; the graph didn't read, so the marks may not hold next time
            var whole = clips.ToDictionary(c => c.Id, c => (byte)(c.Index - 8400 < 8 ? 0 : EditMarks.AllEdits));
            edit.SetMarks(EditMarks.From(whole, "the game's animation graph couldn't be read: InvalidDataException: a graph record of an odd shape"));
            Assert.Equal(12, edit.ClipPanel.Rows.Count);
            Assert.Equal(Visibility.Visible, edit.RetryButton.Visibility);
            Assert.Equal(Visibility.Visible, edit.MarksText.Visibility);
            Assert.Equal("not every clip's edits are known: the game's animation graph couldn't be read: InvalidDataException: a graph record of an odd shape",
                edit.MarksText.Text);
            Assert.Equal(((SolidColorBrush)Wpf.App.FindResource(Theme.Key("Warning"))).Color, ((SolidColorBrush)edit.MarksText.Foreground).Color);
            // 0-2 the tool failed on (not told), 3-7 allow no edit, the rest every edit
            var masks = clips.Skip(3).ToDictionary(c => c.Id, c => (byte)(c.Index - 8400 < 8 ? 0 : EditMarks.AllEdits));
            edit.SetMarks(EditMarks.From(masks, unknown: clips.Take(3).Select(c => c.Id)));
            Assert.All(clips.Take(3), c => Assert.Contains(c, edit.ClipPanel.Rows));
            Assert.Contains("which edits 3 clip(s) allow isn't known, so they are listed", edit.ClipPanel.CountText.Text);
            Assert.Equal(15, edit.ClipPanel.Rows.Count);
            Assert.Equal("the tool failed on 3 clip(s): which edits they allow isn't known (the detail is in the error log)", edit.MarksText.Text);
            Assert.Equal(Visibility.Collapsed, edit.RetryButton.Visibility);
            // two records of one ID among them (the install takes one clip): still 3 clips the tool failed on
            ThemeScreenTests.Fill(edit.ClipPanel, [.. clips, clips[0]]);
            edit.SetMarks(EditMarks.From(masks, unknown: clips.Take(3).Select(c => c.Id)));
            Assert.Equal("the tool failed on 3 clip(s): which edits they allow isn't known (the detail is in the error log)", edit.MarksText.Text);
            // clips an error of the moment left out (no marks, not among those the tool failed on) are listed and counted too: 2 the tool
            // failed on and 3 left out make 5 not known, while the line says what it is
            ThemeScreenTests.Fill(edit.ClipPanel, clips);
            edit.SetMarks(EditMarks.From(clips.Skip(5).ToDictionary(c => c.Id, c => (byte)(c.Index - 8400 < 8 ? 0 : EditMarks.AllEdits)),
                "not every clip was worked out (OutOfMemoryException: short of memory)", unknown: clips.Take(2).Select(c => c.Id)));
            Assert.Contains("which edits 5 clip(s) allow isn't known, so they are listed", edit.ClipPanel.CountText.Text);
            Assert.All(clips.Take(5), c => Assert.Contains(c, edit.ClipPanel.Rows));
            Assert.StartsWith("not every clip's edits are known: not every clip was worked out", edit.MarksText.Text);
            ThemeScreenTests.Fill(edit.ClipPanel, clips);
            edit.SetMarks(EditMarks.From(clips.ToDictionary(c => c.Id, c => (byte)(c.Index - 8400 < 8 ? 0 : EditMarks.AllEdits))));
            Assert.Equal(12, edit.ClipPanel.Rows.Count);
            Assert.Equal(Visibility.Collapsed, edit.RetryButton.Visibility);
            Assert.Equal(Visibility.Collapsed, edit.MarksText.Visibility);
            Assert.DoesNotContain("isn't known", edit.ClipPanel.CountText.Text);
        });
    }

    /// <summary>At the window's least size (860x520) every screen keeps every control reachable: its panes scroll to their least height,
    /// the Edit screen's Apply buttons and checks among them, and each list shows at least five rows.</summary>
    [Fact]
    public void AtTheLeastWindowSizeEveryControlIsReachable()
    {
        Wpf.Run(() =>
        {
            foreach (var choice in new[] { ThemeChoice.Light, ThemeChoice.Dark })
            {
                Theme.Apply(choice);
                // the window's least client area, less the tabs' row
                var size = new Size(860 - 16, 520 - 40 - 40);
                var edit = new EditView();
                ThemeScreenTests.Fill(edit.ClipPanel, ThemeScreenTests.Clips("mil_gen_m_col_fm_std_V0_N_DETECTION_", 40, 8400));
                edit.ShowChoices(Mixed);
                var library = new LibraryView();
                ThemeScreenTests.Fill(library.ClipPanel, ThemeScreenTests.Clips("mil_gen_m_ale_ros_std_V0_N_death_", 40, 8400));
                var import = new ImportView();
                ThemeScreenTests.Fill(import.SlotPanel, ThemeScreenTests.Clips("mcm_nwp_fm_std_V0_idle_", 40, 3600));
                var package = new PackageView();
                foreach (var (name, view, scroll, panel) in new (string, FrameworkElement, FitScroll, ClipListPanel)[]
                         { ("edit", edit, edit.PaneScroll, edit.ClipPanel), ("library", library, library.PaneScroll, library.ClipPanel),
                           ("import", import, import.PaneScroll, import.SlotPanel), ("package", package, package.PaneScroll, package.ItemsPanel) })
                {
                    var w = new Window { Content = view };
                    for (int i = 0; i < 4; i++)
                    {
                        view.Measure(size);
                        view.Arrange(new Rect(size));
                        view.UpdateLayout();
                    }
                    // the panes scroll, their content at least their least height
                    var content = (FrameworkElement)scroll.Content;
                    Assert.True(content.ActualHeight >= scroll.LeastHeight() - 1, $"{name}: content {content.ActualHeight:F0} under its least height {scroll.LeastHeight():F0}");
                    Assert.True(scroll.ExtentHeight >= content.ActualHeight - 1, $"{name}: extent {scroll.ExtentHeight:F0}");
                    // every list shows at least five rows
                    Assert.True(panel.List.ActualHeight >= 30 + ClipListPanel.LeastRows * 21, $"{name}: the list is {panel.List.ActualHeight:F0} high");
                    if (view == edit)
                    {
                        foreach (var b in new FrameworkElement[] { edit.ApplyKeepButton, edit.ApplyButton, edit.KeepButton, edit.ReportList })
                        {
                            var at = b.TransformToAncestor(content).TransformBounds(new Rect(b.RenderSize));
                            Assert.True(b.ActualHeight > 0 && at.Bottom <= content.ActualHeight + 0.5, $"{ThemeChoices.Name(choice)} edit: {b.Name} lies at {at.Top:F0}-{at.Bottom:F0}, past the pane's {content.ActualHeight:F0}");
                        }
                        // the edits scroll on their own: the last of them within their scroll's reach
                        var swap = edit.SwapChoice.TransformToAncestor(edit.ChoicesPanel).TransformBounds(new Rect(edit.SwapChoice.RenderSize));
                        Assert.True(swap.Bottom <= edit.ChoicesScroll.ExtentHeight + 0.5, $"edit: Swap lies past the edits' scroll ({swap.Bottom:F0} of {edit.ChoicesScroll.ExtentHeight:F0})");
                        Assert.True(edit.ChoicesScroll.ActualHeight >= 150, $"edit: the edits show {edit.ChoicesScroll.ActualHeight:F0}");
                    }
                    // a viewer's 3D view keeps a usable height
                    var frame = view switch { EditView e => e.ViewFrame, LibraryView l => l.ViewFrame, ImportView i => i.ViewFrame, _ => null };
                    if (frame is not null) Assert.True(frame.ActualHeight >= FitScroll.LeastView - 1, $"{ThemeChoices.Name(choice)} {name}: the 3D view is {frame.ActualHeight:F0} high");
                    w.Content = null;
                }
            }
        });
    }

    /// <summary>With Trim chosen the trim strip and the events strip are the viewer's width, wide enough to drag a cut (at least 200), and
    /// each handle can be hit, at the usual window size and at the least.</summary>
    [Fact]
    public void TheTrimStripIsWideEnoughToDragAtBothSizes()
    {
        Wpf.Run(() =>
        {
            Theme.Apply(ThemeChoice.Light);
            foreach (var size in new[] { new Size(1484, 861), new Size(860 - 16, 520 - 40 - 40) })
            {
                var edit = new EditView();
                edit.ShowChoices(Mixed);
                edit.TrimChoice.IsChecked = true;
                var w = new Window { Content = edit };
                for (int i = 0; i < 4; i++)
                {
                    edit.Measure(size);
                    edit.Arrange(new Rect(size));
                    edit.UpdateLayout();
                }
                Assert.Equal(Visibility.Visible, edit.TrimBorder.Visibility);
                Assert.True(edit.TrimCanvas.ActualWidth >= 200, $"{size}: the trim strip is {edit.TrimCanvas.ActualWidth:F0} wide");
                Assert.True(edit.MarkStrip.ActualWidth >= 200, $"{size}: the events strip is {edit.MarkStrip.ActualWidth:F0} wide");
                // each handle is hit where it stands, and they stand apart
                foreach (var thumb in new[] { edit.FromThumb, edit.ToThumb })
                {
                    // in the least window the strip is below the pane's fold: scrolled to, as a person would
                    thumb.BringIntoView();
                    edit.UpdateLayout();
                    var mid = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), edit);
                    // (the geometry's hit test: a window never shown makes nothing visible, so an input hit test finds nothing)
                    var hit = VisualTreeHelper.HitTest(edit, mid)?.VisualHit;
                    bool onThumb = false;
                    for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d)) if (d == thumb) { onThumb = true; break; }
                    Assert.True(onThumb, $"{size}: {thumb.Name} can't be hit at {mid}: {hit?.GetType().Name} {(hit as FrameworkElement)?.Name} is hit there");
                    Assert.True(thumb.IsHitTestVisible && thumb.IsEnabled, $"{size}: {thumb.Name} takes no mouse");
                }
                double from = edit.FromThumb.TranslatePoint(new Point(), edit.TrimCanvas).X, to = edit.ToThumb.TranslatePoint(new Point(), edit.TrimCanvas).X;
                Assert.True(to - from >= 150, $"{size}: the handles stand {to - from:F0} apart");
                w.Content = null;
            }
        });
    }

    /// <summary>On the game's files: with Follow on, the camera reset for a picture centres on the result where it stands then (not where
    /// the clip starts); and Apply and add to package, with another clip chosen while the edit runs, adds nothing and says nothing of it.</summary>
    [GameDataFact]
    public void FollowHoldsThroughAResetAndAnApplyForAClipLeftAddsNothing()
    {
        using var g = Core.Install.GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        try
        {
            Wpf.RunAsync(async () =>
            {
                Core.Import.PackageList.Current.Clear();
                var edit = new EditView();
                edit.SetInstall(g, true, new LibraryView());
                // a run that works no marks out (one that pictures no Edit screen) says so, never that they are being worked out
                var unmarked = new EditView();
                unmarked.SetInstall(g, true, new LibraryView(), marks: false);
                Assert.Contains("which edits each clip allows isn't worked out in this run, so every clip is listed", unmarked.ClipPanel.CountText.Text);
                Assert.DoesNotContain("working out", unmarked.ClipPanel.CountText.Text);
                // a clip that travels, the mover on, no ghost: the camera on the result at the picture's time
                Assert.Null(await edit.ChooseClipAsync("mcm_nwp_fm_std_V1_cycle"));
                edit.GhostBox.IsChecked = false;
                Assert.True(edit.FollowBox.IsChecked);
                var args = ViewerArgs.Parse([]);
                Assert.Null(edit.ShowForShot(new ShotSpec("a.png", null, 0f, true), args));
                var start = edit.CameraTarget;
                Assert.Null(edit.ShowForShot(new ShotSpec("b.png", null, 0.9f, true), args));
                var at = edit.ResultCentre!.Value;
                Assert.True(new Vector(at.X - start.X, at.Y - start.Y).Length > 0.2, $"the clip moves {new Vector(at.X - start.X, at.Y - start.Y).Length:F2} m");
                Assert.Equal(at.X, edit.CameraTarget.X, 3);
                Assert.Equal(at.Y, edit.CameraTarget.Y, 3);
                // Follow off: the reset goes back to where the clip starts
                edit.FollowBox.IsChecked = false;
                Assert.Null(edit.ShowForShot(new ShotSpec("c.png", null, 0.9f, true), args));
                Assert.Equal(start.X, edit.CameraTarget.X, 3);
                Assert.Equal(start.Y, edit.CameraTarget.Y, 3);

                // the handgun idle at 1.5x, then the one step again with another clip chosen meanwhile: nothing added, nothing said
                var speed = ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--edit", "fm_std_HG_V0_idle_cool_06", "--edit-kind", "speed", "--factor", "1.5"]);
                Assert.Null(await edit.EditForAutomationAsync(speed));
                var run = edit.ApplyAndKeepAsync();
                await edit.ChooseClipAsync("mca_emote_Hello");
                Assert.Null(await run);
                Assert.Empty(Core.Import.PackageList.Current.Items);
                Assert.DoesNotContain("Nothing applied", edit.StatusText.Text);
                // the same step left to finish adds the result; the status names the edit in the tool's one wording, as the view's
                // overlay does
                Assert.Null(await edit.EditForAutomationAsync(speed));
                Assert.Equal("Made: fm_std_HG_V0_idle_cool_06 (Speed 1.5x). It plays below with the original as a ghost.", edit.StatusText.Text);
                Assert.StartsWith("fm_std_HG_V0_idle_cool_06 (Speed 1.5x)\n", edit.OverlayText.Text);
                Assert.Null(await edit.ApplyAndKeepAsync());
                Assert.Equal("Added fm_std_HG_V0_idle_cool_06 (Speed 1.5x) to the package. The package holds 1.", edit.StatusText.Text);
                Assert.Equal("fm_std_HG_V0_idle_cool_06", Assert.Single(Core.Import.PackageList.Current.Items).Slot.Name);
                Assert.Equal("Speed 1.5x", Core.Package.PackageEntries.What(Core.Import.PackageList.Current.Items[0]));
            });
        }
        finally { Wpf.Run(Core.Import.PackageList.Current.Clear); }
    }

    static List<string> AllText(DependencyObject root)
    {
        var found = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock t) found.Add(t.Text);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return found;
    }
}
