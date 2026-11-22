using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>The screens' clip lists are one component with the same columns, search and filters, and its number boxes keep what is typed
/// in sight (no theme clear button over them). The screens are built and laid out in no window shown, in both themes.</summary>
[Collection("wpf")]
public class ClipListTests(Xunit.Abstractions.ITestOutputHelper output)
{
    const double Width = 1484, Height = 861;

    static void Lay(FrameworkElement view)
    {
        var w = new Window { Content = view };
        view.Measure(new Size(Width, Height));
        view.Arrange(new Rect(0, 0, Width, Height));
        view.UpdateLayout();
        w.Content = null;
    }

    static List<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        void Walk(DependencyObject d)
        {
            if (d is T t) found.Add(t);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return found;
    }

    /// <summary>A list's columns as their headers name them: the sort a header click makes, or the header's word for a column of a
    /// screen's own, with each width.</summary>
    static List<(string Column, double Width)> Columns(ClipListPanel p) => ((GridView)p.List.View).Columns
        .Select(c => (c.Header is GridViewColumnHeader { Tag: string tag } ? tag : ((GridViewColumnHeader)c.Header).Content as string ?? "", c.Width)).ToList();

    [Fact]
    public void EveryScreensClipListIsTheSharedOneWithTheSameColumnsSearchAndFilters()
    {
        Wpf.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (var choice in new[] { ThemeChoice.Light, ThemeChoice.Dark })
            {
                Theme.Apply(choice);
                var library = new LibraryView();
                var edit = new EditView();
                var import = new ImportView();
                var package = new PackageView();
                var screens = new (string Name, FrameworkElement View, ClipListPanel Panel)[]
                {
                    ("library", library, library.ClipPanel), ("edit", edit, edit.ClipPanel), ("import", import, import.SlotPanel),
                    ("package", package, package.ItemsPanel),
                };
                var clips = ThemeScreenTests.Clips("mil_gen_m_ale_ros_std_V0_N_death_", 30, 8400)
                    .Concat(ThemeScreenTests.Clips("mca_emote_Hello_", 5, 9000)).ToList();
                foreach (var (name, view, panel) in screens)
                {
                    panel.SetClips(clips);
                    panel.SetIndex(ThemeScreenTests.IndexFor(clips));
                    Lay(view);
                    // the screen's one clip list, and no other list of the install's clips beside it
                    Assert.Same(panel, Assert.Single(All<ClipListPanel>(view)));
                    Assert.Single(All<ListView>(view));
                    // the four common columns first, then the rest; a column of the screen's own (the Edit screen's edits) after the four
                    var cols = Columns(panel).Select(c => c.Column).Where(c => ClipListPanel.CommonColumns.Contains(c) || ClipListPanel.OtherColumns.Contains(c)).ToList();
                    Assert.Equal([.. ClipListPanel.CommonColumns, .. ClipListPanel.OtherColumns], cols);
                    Assert.Equal(ClipListPanel.CommonColumns, Columns(panel).Take(4).Select(c => c.Column));
                    // every filter there and in sight: search, status, family, length, who, what
                    foreach (var f in new FrameworkElement[] { panel.SearchBox, panel.StatusBox, panel.FamilyBox, panel.MinBox, panel.MaxBox, panel.WhoBox, panel.WhatBox })
                        Assert.True(f.IsVisible || f.Visibility == Visibility.Visible && f.ActualWidth > 0, $"{name}: the filter {f.Name} isn't shown");
                    Assert.All(new[] { panel.WhoBox, panel.WhatBox }, b => Assert.True(b.IsEnabled, $"{name}: {b.Name} stays greyed with the index given"));
                    // the readable name, who and what cells filled in from the index
                    Assert.Contains(All<TextBlock>(panel.List), t => t.Text == "Holstered walk, standing");
                }
                // the same widths everywhere
                var first = Columns(screens[0].Panel).Where(c => ClipListPanel.CommonColumns.Contains(c.Column) || ClipListPanel.OtherColumns.Contains(c.Column)).ToList();
                foreach (var (name, _, panel) in screens.Skip(1))
                    Assert.Equal(first, Columns(panel).Where(c => first.Any(f => f.Column == c.Column)).ToList());
                // the same search and filters give the same rows on every screen
                foreach (var (_, _, panel) in screens)
                {
                    panel.SearchBox.Text = "death 1";
                    panel.MaxBox.Text = "5";
                    panel.Apply();
                }
                var rows = screens[0].Panel.Rows.Select(c => c.Name).ToList();
                Assert.NotEmpty(rows);
                Assert.DoesNotContain(rows, r => r.StartsWith("mca", StringComparison.Ordinal));
                foreach (var (name, _, panel) in screens) Assert.Equal(rows, panel.Rows.Select(c => c.Name));
                // a fixed constraint narrows the rows on top of them, and the count says so
                import.SlotPanel.SetConstraint(c => c.Index % 2 == 0, "only the file's length, 198 ticks");
                Assert.All(import.SlotPanel.Rows, c => Assert.Equal(0, c.Index % 2));
                Assert.EndsWith("· only the file's length, 198 ticks", import.SlotPanel.CountText.Text);
                // the Package screen's list is the same one, its own columns (its checks' badge first, then who plays each clip, what was
                // changed in it and its warnings) after the four every list has
                var own = Columns(package.ItemsPanel).Skip(4).Take(4).Select(c => c.Column).ToList();
                Assert.Equal(["Checks", "Played by", "Change", "Warnings"], own);
            }
        });
    }

    /// <summary>The Package screen's column Change says what was changed in each clip in the tool's one wording, an edit's or an import's,
    /// the .glb's name only; a trim's exact ticks on hover; a change an earlier version kept in words this one doesn't read as it was kept.</summary>
    [Fact]
    public void ThePackageListsChangeColumnShowsEachChangeInTheToolsWording()
    {
        Wpf.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Theme.Apply(ThemeChoice.Dark);
            ClipChange[] changes = [ClipChange.Mirror(), ClipChange.Scale(0.5), ClipChange.Speed(1.25), ClipChange.Trim(54, 484),
                ClipChange.Swap("mca_emote_Shotgun"), ClipChange.Imported(System.IO.Path.GetFullPath(System.IO.Path.Combine("anims", "walk.glb"))),
                ClipChange.FromKeptText("edit: trim, ticks 3 to 40")];
            string[] words = ["Mirrored", "Root-motion scale 0.5x", "Speed 1.25x", "Trim 0.9 s to 8.07 s", "Swap: motion from mca_emote_Shotgun", "Imported: walk.glb",
                "edit: trim, ticks 3 to 40"];
            var items = changes.Select((c, i) => new Core.Import.PackageItem(new ClipEntry { Index = 100 + i, Id = 0x1000UL + (ulong)i, Name = $"clip_{i}", Bytes = ReadOnlyMemory<byte>.Empty },
                [], c, Core.Checks.CheckLevel.Pass, "", DateTime.Now)).ToList();
            var package = new PackageView();
            package.ShowUsersFor(items, null, "not built here");
            Assert.Equal(words, package.ShownRows.Select(r => r.What));
            var column = ((GridView)package.ItemsPanel.List.View).Columns.Single(c => ((GridViewColumnHeader)c.Header).Content as string == "Change");
            Assert.Equal("What was changed in the clip: the edit made on it, or the .glb imported into it", ((GridViewColumnHeader)column.Header).ToolTip);
            Assert.DoesNotContain(Columns(package.ItemsPanel), c => c.Column == "Made by");
            Lay(package);
            var cells = All<TextBlock>(package.ItemsPanel.List).Where(t => words.Contains(t.Text)).ToList();
            Assert.Equal(words.Order(StringComparer.Ordinal), cells.Select(t => t.Text).Distinct().Order(StringComparer.Ordinal));
            Assert.Equal("Trim 0.9 s to 8.07 s, ticks 54 to 484 of the original clip", cells.First(t => t.Text == words[3]).ToolTip);
            Assert.Equal("Speed 1.25x", cells.First(t => t.Text == words[2]).ToolTip);
            Assert.DoesNotContain(cells, t => t.Text.Contains("anims", StringComparison.Ordinal));
            Assert.Equal(6, items.Count(i => Core.Package.PackageEntries.Kind(i) == Core.Package.PackageItemKind.Edit));
        });
    }

    /// <summary>In a window 1500 px wide (the window's usual size) and 1440 px wide (the window opened on a screen that wide), with a clip
    /// of each kind of change kept (swaps from "mca_emote_Shotgun" and "mca_emote_Diamond_Wedge" and an import among them) and every clip
    /// played by several kinds of character, the four columns every list starts with, the checks, who plays each clip and the change are
    /// in sight with no sideways scroll: each header whole; the checks' badges and who plays each clip in sight in their cells (several
    /// kinds ending in an ellipsis, whole on hover); every change ends inside the list, whole or in an ellipsis with the whole on hover,
    /// never cut by the list's edge; at 1500 px every change here is whole, both swaps among them. A longer change than the column's most
    /// ends in an ellipsis inside the list, whole on hover. The warnings and the columns every list has after them are a sideways scroll
    /// away.</summary>
    [Theory]
    [InlineData(1500)]
    [InlineData(1440)]
    public void ThePackageListsChecksPlayedByAndChangeAreInSightAtTheUsualSize(double windowWidth)
    {
        // the screen's room: the window's client area less the tabs' margins
        double Width = windowWidth - 16;
        bool usual = windowWidth >= 1500;
        var lines = new List<string>();
        Wpf.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (var choice in new[] { ThemeChoice.Dark, ThemeChoice.Light })
            {
                Theme.Apply(choice);
                string mode = ThemeChoices.Name(choice);
                ClipChange[] changes = [ClipChange.Mirror(), ClipChange.Scale(0.5), ClipChange.Speed(1.25), ClipChange.Trim(54, 484),
                    ClipChange.Swap("mca_emote_Diamond_Wedge"), ClipChange.Imported(System.IO.Path.GetFullPath("M72_Get_chest_lean.glb")),
                    ClipChange.Swap("mca_emote_Shotgun")];
                var items = changes.Select((c, i) => new Core.Import.PackageItem(new ClipEntry { Index = 100 + i, Id = 0x2000UL + (ulong)i, Name = $"mca_fm_dive_V2_AR_RHS_{i}", Bytes = ReadOnlyMemory<byte>.Empty },
                    [], c, i == 1 ? Core.Checks.CheckLevel.Warn : Core.Checks.CheckLevel.Pass, i == 1 ? "WARN  limbs: the legs come within 4.0 cm of each other" : "", DateTime.Now)).ToList();
                var package = new PackageView();
                // the second clip played by several kinds of character, the longest "Played by" a list shows
                var index = ThemeScreenTests.IndexFor(items.Select(i => i.Slot));
                package.ShowUsersFor(items, index, null);
                // the screen's room in the window at its usual size: the window's client area less the tabs' margins and their row
                var w = new Window { Content = package };
                package.Measure(new Size(Width - 16, Height - 40));
                package.Arrange(new Rect(0, 0, Width - 16, Height - 40));
                package.UpdateLayout();
                w.Content = null;
                var list = package.ItemsPanel.List;
                var viewer = All<ScrollViewer>(list).First();
                double view = viewer.ViewportWidth;
                Rect At(FrameworkElement e) => e.TransformToAncestor(viewer).TransformBounds(new Rect(e.RenderSize));
                double Needs(TextBlock t)
                {
                    var m = new TextBlock { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontStyle = t.FontStyle, FontWeight = t.FontWeight };
                    m.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    return m.DesiredSize.Width;
                }
                Assert.Equal(0, viewer.HorizontalOffset);
                // the header of each of the four every list has, the checks, who plays each clip and the change whole, in sight (the
                // warnings and the columns every list has after them are a sideways scroll away)
                var shown = ((GridView)list.View).Columns.Take(ClipListPanel.CommonColumns.Count + 3).Select(c => c.Header).ToHashSet();
                foreach (var h in All<GridViewColumnHeader>(list).Where(h => h.Content is string && h.ActualWidth > 0 && shown.Contains(h)))
                {
                    var t = All<TextBlock>(h).First();
                    var at = At(t);
                    // the header's word, from where it starts
                    double end = at.Left + Needs(t);
                    lines.Add($"{mode}: header '{h.Content}' {at.Left:F0} to {end:F0} of {view:F0}");
                    Assert.True(end <= view + 0.5 && Needs(t) <= t.ActualWidth + 0.5, $"{mode}: the header '{h.Content}' isn't whole in sight ({end:F0} of {view:F0})");
                }
                var rows = ((GridView)list.View).Columns;
                // each row's cells of the package's own columns: the badge, who plays it and the change in sight; a value cut ends in an
                // ellipsis with its whole text on hover
                foreach (var row in All<ListViewItem>(list))
                {
                    var c = (ClipEntry)row.DataContext;
                    var item = items.Single(i => i.Slot == c);
                    var texts = All<TextBlock>(row).Where(t => t.ActualWidth > 0).ToList();
                    // (the cell holds a badge for each level, only its row's own shown)
                    var badge = texts.Single(t => t.Text is "PASS" or "WARN" or "FAIL" && VisualTreeHelper.GetParent(t) is Border { Visibility: Visibility.Visible });
                    Assert.True(At(badge).Right <= view + 0.5, $"{mode}: {c.Name}'s badge out of sight");
                    var change = texts.Single(t => t.Text == item.Change.Words);
                    Assert.True(At(change).Right <= view + 0.5, $"{mode}: {c.Name}'s change ends at {At(change).Right:F0}, past {view:F0}");
                    Assert.Equal(item.Change.Detail, change.ToolTip);
                    // at the usual size every change here whole, the two swaps among them; narrower, one cut ends in an ellipsis inside
                    // the list (its cell ends in sight, above), whole on hover
                    if (usual) Assert.True(Needs(change) <= change.ActualWidth + 0.5, $"{mode}: '{change.Text}' is cut ({Needs(change):F0} in {change.ActualWidth:F0})");
                    Assert.Equal(TextTrimming.CharacterEllipsis, change.TextTrimming);
                    var played = texts.Single(t => t.ToolTip is string s && s.Contains(ClipUsers.PlayedByTip, StringComparison.Ordinal));
                    Assert.True(At(played).Right <= view + 0.5, $"{mode}: {c.Name}'s Played by ends at {At(played).Right:F0}, past {view:F0}");
                    Assert.StartsWith(played.Text, (string)played.ToolTip!, StringComparison.Ordinal);
                    lines.Add($"{mode}: {c.Name}: change '{change.Text}' {At(change).Left:F0} to {At(change).Right:F0} (needs {Needs(change):F0}); played by '{played.Text}' to {At(played).Right:F0}");
                }
                Assert.Contains(items, i => i.Change.Words == "Swap: motion from mca_emote_Shotgun");
                Assert.Contains(items, i => i.Change.Words == "Swap: motion from mca_emote_Diamond_Wedge");
            }
            // a change longer than the column's most: an ellipsis, whole on hover
            var longer = new Core.Import.PackageItem(new ClipEntry { Index = 7, Id = 0x3000, Name = "x", Bytes = ReadOnlyMemory<byte>.Empty }, [],
                ClipChange.Swap("Mch_Crh_Alerted_PickUp_LD_AR_with_an_even_longer_name"), Core.Checks.CheckLevel.Pass, "", DateTime.Now);
            var p = new PackageView();
            p.ShowUsersFor([longer], null, "none here");
            var lw = new Window { Content = p };
            p.Measure(new Size(Width - 16, Height - 40));
            p.Arrange(new Rect(0, 0, Width - 16, Height - 40));
            p.UpdateLayout();
            lw.Content = null;
            var cell = All<TextBlock>(p.ItemsPanel.List).Single(t => t.Text == longer.Change.Words);
            double changeWidth = ((GridView)p.ItemsPanel.List.View).Columns.Single(c => ((GridViewColumnHeader)c.Header).Content as string == "Change").Width;
            if (usual) Assert.Equal(PackageView.ChangeMost, changeWidth);
            else Assert.True(changeWidth <= PackageView.ChangeMost);
            var pv = All<ScrollViewer>(p.ItemsPanel.List).First();
            double cellEnd = cell.TransformToAncestor(pv).TransformBounds(new Rect(cell.RenderSize)).Right;
            lines.Add($"longer change: column {changeWidth:F0}, cell ends at {cellEnd:F0} of {pv.ViewportWidth:F0}");
            Assert.True(cellEnd <= pv.ViewportWidth + 0.5, $"the longer change ends at {cellEnd:F0}, past {pv.ViewportWidth:F0}");
            Assert.Equal(TextTrimming.CharacterEllipsis, cell.TextTrimming);
            Assert.Equal(longer.Change.Detail, cell.ToolTip);
        });
        foreach (var l in lines) output.WriteLine(l);
    }

    /// <summary>The Package screen's summary names the clips whose checks fail, so a FAIL is seen before Build whatever the list shows.</summary>
    [Fact]
    public void ThePackageSummaryNamesTheClipsThatFail()
    {
        static Core.Import.PackageItem Kept(string name, Core.Checks.CheckLevel verdict) => new(new ClipEntry
        {
            Index = 1, Id = (ulong)name.GetHashCode(), Name = name, Bytes = ReadOnlyMemory<byte>.Empty,
        }, [], ClipChange.Imported("x.glb"), verdict, "", DateTime.Now);
        Assert.Equal("", PackageView.Failing([Kept("a", Core.Checks.CheckLevel.Pass), Kept("b", Core.Checks.CheckLevel.Warn)]));
        Assert.Equal(" 1 clip fails its checks: b.", PackageView.Failing([Kept("a", Core.Checks.CheckLevel.Pass), Kept("b", Core.Checks.CheckLevel.Fail)]));
        Assert.Equal(" 2 clips fail their checks: a, b.", PackageView.Failing([Kept("a", Core.Checks.CheckLevel.Fail), Kept("b", Core.Checks.CheckLevel.Fail)]));
    }

    [Fact]
    public void NumberBoxesKeepWhatIsTypedInSightWithNoClearButtonOverIt()
    {
        Wpf.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (var choice in new[] { ThemeChoice.Light, ThemeChoice.Dark, ThemeChoice.Light })
            {
                Theme.Apply(choice);
                var panel = new ClipListPanel();
                var plain = new TextBox { Width = 64, Text = "12.345" };
                var holder = new StackPanel();
                holder.Children.Add(panel);
                holder.Children.Add(plain);
                panel.MinBox.Text = "12.345";
                panel.MaxBox.Text = "123.45";
                Lay(holder);
                // the theme's text box holds the clear button the number boxes hide (hidden until the box has the keyboard)
                Assert.IsType<Button>(plain.Template.FindName(NumberBox.ClearButtonName, plain));
                foreach (var box in new[] { panel.MinBox, panel.MaxBox })
                {
                    var clear = Assert.IsType<Button>(box.Template.FindName(NumberBox.ClearButtonName, box));
                    Assert.Equal(Visibility.Collapsed, clear.Visibility);
                    // a local value: it outranks the template's trigger that shows the button while the box has the keyboard
                    Assert.Equal(BaseValueSource.Local, DependencyPropertyHelper.GetValueSource(clear, UIElement.VisibilityProperty).BaseValueSource);
                    // the box keeps the theme's own look
                    Assert.Same(Wpf.App.TryFindResource(typeof(TextBox)), box.Style);
                    // what is typed fits in the room the box gives its text
                    var host = Assert.IsAssignableFrom<Control>(box.Template.FindName("PART_ContentHost", box));
                    var t = new TextBlock { Text = box.Text, FontFamily = box.FontFamily, FontSize = box.FontSize };
                    t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double room = host.ActualWidth - host.Padding.Left - host.Padding.Right;
                    Assert.True(t.DesiredSize.Width <= room, $"{ThemeChoices.Name(choice)}: '{box.Text}' needs {t.DesiredSize.Width:F0} and the box gives {room:F0}");
                }
            }
        });
    }
}
