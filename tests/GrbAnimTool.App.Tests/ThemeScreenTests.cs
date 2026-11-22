using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using Xunit.Abstractions;

namespace GrbAnimTool.App.Tests;

/// <summary>The theme as the program shows it: the four screens built and laid out (in no window shown) in the light and the dark
/// theme, with the Fluent theme's own resources as WPF loads them, and every text on them measured against what is drawn under it.
/// One test, on the process's one WPF application (<see cref="Wpf"/>).</summary>
[Collection("wpf")]
public class ThemeScreenTests(ITestOutputHelper output)
{
    /// <summary>Clips as an install lists them (no record needed to lay a list out), their status words varied.</summary>
    internal static List<ClipEntry> Clips(string stem, int n, int index0) => Enumerable.Range(0, n).Select(i => new ClipEntry
    {
        Index = index0 + i, Id = 0x0000077700000000UL + (ulong)(index0 + i), Name = $"{stem}{i:D2}", Bytes = ReadOnlyMemory<byte>.Empty,
        Status = Enum.Parse<ClipStatus>(StatusOf(i)),
    }).ToList();

    /// <summary>A clip index for <paramref name="clips"/>: each with a readable name, the longest Who a list shows (three kinds), and a What.</summary>
    internal static ClipIndex IndexFor(IEnumerable<ClipEntry> clips) => new()
    {
        Entries = clips.Select(c => new ClipIndexEntry
        {
            ClipId = c.Id, ClipName = c.Name, ReadableName = "Holstered walk, standing",
            Who = new[] { ClipCategories.Teammates, ClipCategories.Enemy, ClipCategories.Npc }.Select(w => new ClipCategory(w, CategorySource.Graph, "the test's")).ToList(),
            What = [new ClipCategory(ClipCategories.Of(CategoryKind.What).First(), CategorySource.Name, "the test's")],
            Documents = [], Explained = [], WhoProbably = [],
        }).ToList(),
        Facts = new GraphFacts { Documents = [], Clips = new Dictionary<ulong, ClipGraphFacts>(), Failures = [], ClipsLookedFor = 0 },
        DocumentPlacements = [],
    };

    /// <summary>A screen's clip list given the clips and their index, its second row the clip shown.</summary>
    internal static void Fill(ClipListPanel panel, List<ClipEntry> clips)
    {
        panel.SetClips(clips);
        panel.SetIndex(IndexFor(clips));
        panel.Show(clips[1]);
    }

    /// <summary>The warnings of an import whose checks warn at length (four warnings, as a reload imported from another clip's .glb
    /// gives): taller than the Package screen's list when shown whole.</summary>
    static readonly string LongWarnings = string.Join("\n", Enumerable.Range(0, 4).Select(i =>
        $"WARN check {i}: RightHand moves up to 65.2 cm and 131.0 deg from the game's clip, in a clip holding the hand tracks 0F208B89, "
        + "447440F8, 64F9A1CB, 6C8CB68C, 8B9974FF, B663DEC6, F22AE62D, FD962D0E: the game aims the hand by the clip's own offset, which stays "
        + "as it was, so in game the hand may stay where the old clip had it (worst at tick 16, RightHand)"));
    public sealed record SourceRow(string Name);

    const double Width = 1484, Height = 861;

    /// <summary>Swap sources as the install names them: 72 characters, two of one group that differ only at their end.</summary>
    const string LongSource = "mil_gen_m_col_fm_std_V0_N_DETECTION_SUSPICIOUS_ale_ros_std_V0_S_TurnL_01";
    const string LongSourceTwin = "mil_gen_m_col_fm_std_V0_N_DETECTION_SUSPICIOUS_ale_ros_std_V0_S_TurnR_01";

    /// <summary>A clip's status words, as its list shows them.</summary>
    static readonly string[] StatusWords = ["Vanilla", "Replaced", "Added", "Unknown"];
    static string StatusOf(int i) => i is >= 2 and < 5 ? StatusWords[i - 1] : "Vanilla";

    /// <summary>A fact skipped under Windows' high contrast: the screens then take Windows' own colours, not the light and dark palettes
    /// the test measures.</summary>
    public sealed class FactOffHighContrastAttribute : FactAttribute
    {
        public FactOffHighContrastAttribute()
        {
            if (SystemParameters.HighContrast) Skip = "Windows' high contrast is on: the screens take Windows' own colours, not the light and dark palettes this test measures.";
        }
    }

    [FactOffHighContrast]
    public void TheScreensFollowTheThemeWithContrastOfAtLeast4Point5AndCompactRows()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        Exception? crash = null;
        try { Wpf.Run(() => Run(lines, failures)); }
        catch (Exception ex) { crash = ex; }
        foreach (var l in lines) output.WriteLine(l);
        Assert.Null(crash);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>A clip kept for the package, as the Package screen reads it: its slot (no record needed to lay the list out), where it
    /// came from, its verdict and its report's text.</summary>
    static PackageItem Kept(string name, int index, ulong id, ClipChange change, CheckLevel verdict, string report) =>
        new(new ClipEntry { Index = index, Id = id, Name = name, Bytes = ReadOnlyMemory<byte>.Empty }, [], change, verdict, report, DateTime.Now);

    /// <summary>A clip index holding only <paramref name="entries"/>: who plays each, and which of them only probably.</summary>
    static ClipIndex IndexOf(params (ulong Id, string Name, string[] Who, string[] Probably)[] entries) => new()
    {
        Entries = entries.Select(e => new ClipIndexEntry
        {
            ClipId = e.Id, ClipName = e.Name, Who = e.Who.Select(w => new ClipCategory(w, CategorySource.Graph, "the test's")).ToList(), What = [],
            Documents = [], Explained = [], WhoProbably = e.Probably,
        }).ToList(),
        Facts = new GraphFacts { Documents = [], Clips = new Dictionary<ulong, ClipGraphFacts>(), Failures = [], ClipsLookedFor = 0 },
        DocumentPlacements = [],
    };

    static void Run(List<string> lines, List<string> failures)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var app = Wpf.App;
        foreach (var choice in new[] { ThemeChoice.Light, ThemeChoice.Dark })
        {
            Theme.Apply(choice);
            string mode = ThemeChoices.Name(choice);
            var palette = ThemePalette.For(choice == ThemeChoice.Dark);
            // the fact is skipped under Windows' high contrast; one turned on during the run leaves nothing to measure
            if (Theme.IsHighContrast) { lines.Add("Windows' high contrast came on during the run: nothing measured"); break; }

            // each theme brush the tool gives a palette colour is one of the theme's own, held in its dictionaries or read by its rows'
            // styles (the theme's grid view row reads a selected row's brush it doesn't hold): a key the theme neither holds nor reads
            // would colour nothing; and it resolves to the palette's colour, as the theme's templates look it up
            var themeKeys = new HashSet<object>();
            void Collect(ResourceDictionary d)
            {
                foreach (var k in d.Keys) themeKeys.Add(k);
                foreach (var m in d.MergedDictionaries) Collect(m);
            }
            foreach (var m in app.Resources.MergedDictionaries) Collect(m);
            themeKeys.UnionWith(RowBrushKeys(app.TryFindResource(typeof(ListBoxItem)) as Style));
            themeKeys.UnionWith(RowBrushKeys(app.TryFindResource(System.Windows.Controls.GridView.GridViewItemContainerStyleKey) as Style));
            foreach (var (key, name) in Theme.FluentBrushes)
            {
                if (!themeKeys.Contains(key)) failures.Add($"{mode}: the theme neither holds nor reads a brush {key}");
                if (Argb(app.TryFindResource(key)) is not { } got || got != palette[name])
                    failures.Add($"{mode}: the theme's {key} is {Argb(app.TryFindResource(key)):X8}, not the palette's {name} {palette[name]:X8}");
                // under high contrast a list's hovered and selected row takes the highlight, which the palette's text pairs measure
                // with the selected row's text
                if (key.StartsWith("ListViewItemBackground", StringComparison.Ordinal) && Theme.PaletteName(key, name, highContrast: true) != "Selected")
                    failures.Add($"{mode}: under high contrast the theme's {key} is the palette's {Theme.PaletteName(key, name, highContrast: true)}, not its Selected");
            }

            // each screen's clip list is the shared one, given clips and a clip index as an install gives them: every column filled in
            // (the index's readable name, who and what among them), its second row selected
            var library = new LibraryView();
            Fill(library.ClipPanel, Clips("mil_gen_m_ale_ros_std_V0_N_death_", 60, 8400));
            var edit = new EditView();
            var editClips = Clips("mil_gen_m_col_fm_std_V0_N_DETECTION_", 40, 8400);
            Fill(edit.ClipPanel, editClips);
            // the edits' panel as a clip several edits are unavailable on shows it (each reason in plain words, its controls greyed), the
            // speed limited to some factors; the list's edits column filled in, the clips that allow none left out
            edit.ShowChoices(EditScreenTests.Mixed);
            edit.SetChoices(InstallEditChoices.From(editClips.ToDictionary(c => c.Id, c => c.Index % 5 == 4 ? EditScreenTests.NoEdit : EditScreenTests.Mixed)));
            edit.SourceBox.ItemsSource = new[] { new SourceRow(LongSource), new SourceRow(LongSourceTwin) };
            edit.SourceBox.SelectedIndex = 0;
            edit.ReportList.ItemsSource = Enumerable.Range(0, 9).Select(i => new EditView.ReportRow(i == 3 ? "WARN" : i == 5 ? "FAIL" : "PASS",
                i == 3 ? CheckLevel.Warn : i == 5 ? CheckLevel.Fail : CheckLevel.Pass, $"check {i}", "the lowest joint -1.5 cm; the game's clip goes down to -1.5 cm")).ToList();
            edit.ReportList.SelectedIndex = 0;
            var import = new ImportView();
            Fill(import.SlotPanel, Clips("mcm_nwp_fm_std_V0_idle_", 40, 3600));
            import.ReportList.ItemsSource = Enumerable.Range(0, 17).Select(i => new ImportView.ReportRow(i == 0 ? "FAIL" : i == 1 ? "WARN" : "PASS",
                i == 0 ? CheckLevel.Fail : i == 1 ? CheckLevel.Warn : CheckLevel.Pass, $"check {i}", "the arm's curve keyed where its motion needs it (97 keys)")).ToList();
            import.ReportList.SelectedIndex = 0;
            var package = new PackageView();
            // the filters' boxes hold what the screens put in them once the install and its index are read: every category with a
            // count of four digits, the families with theirs (the install's longest family names have 11 letters, and few clips)
            var panels = new[] { library.ClipPanel, edit.ClipPanel, import.SlotPanel };
            foreach (var (box, kind) in panels.SelectMany(p => new[] { (p.WhoBox, CategoryKind.Who), (p.WhatBox, CategoryKind.What) }))
            {
                box.Items.Clear();
                box.Items.Add(new ComboBoxItem { Content = "All" });
                foreach (var c in ClipCategories.Of(kind)) box.Items.Add(new ComboBoxItem { Content = IndexColumns.FilterItem(c, 9999) });
                box.SelectedIndex = 0;
            }
            foreach (var box in panels.Select(p => p.FamilyBox))
            {
                box.Items.Clear();
                box.Items.Add(new ComboBoxItem { Content = "All families" });
                foreach (var (f, n) in new[] { ("mca", 9999), ("mcm", 9999), ("reparenting", 99), ("fallinwater", 99) })
                    box.Items.Add(new ComboBoxItem { Content = $"{f}  ({n:N0})" });
                box.SelectedIndex = 0;
            }
            // who plays the clip, and the notice of a clip several kinds of character play, as the screens show them beside the clip replaced
            // (the longest users' text there is: a sure user and three probable ones)
            var several = new ClipUsers([ClipCategories.Player], [ClipCategories.Teammates, ClipCategories.Enemy, ClipCategories.Npc], true);
            library.InfoPlayedBy.Text = several.Text;
            foreach (var (users, notice) in new[] { (edit.UsersText, edit.UsersNotice), (import.UsersText, import.UsersNotice) })
            {
                users.Text = "Played by: " + several.Text;
                notice.Text = several.Notice;
                notice.Visibility = Visibility.Visible;
            }
            // two clips, the first with warnings taller than the list: both rows, and who plays each, stay in sight; the rows and the
            // notice above Build are the screen's own, from the items kept and a clip index holding the two
            var kept = new[]
            {
                Kept("Mch_AR_Std_ReloadTactical_HK416", 231, 0x0000012300000231, ClipChange.Imported("M72_Get_chest_lean.glb"), CheckLevel.Warn, LongWarnings),
                Kept("mca_emote_Hello", 1234, 0x0000012300001234, ClipChange.Trim(54, 808), CheckLevel.Pass, ""),
            };
            var keptIndex = IndexOf((kept[0].Slot.Id, kept[0].Slot.Name, [.. several.Sure, .. several.Probably], [.. several.Probably]),
                (kept[1].Slot.Id, kept[1].Slot.Name, [ClipCategories.Player], []));
            if (package.ShowUsersFor(kept, keptIndex, null)) failures.Add($"{mode} package: Build waits for an index the screen has");
            if (!package.UsersNotice.Text.Contains($"  Mch_AR_Std_ReloadTactical_HK416: {several.Text}", StringComparison.Ordinal))
                failures.Add($"{mode} package: the notice doesn't name the clip several kinds of character play: '{package.UsersNotice.Text}'");
            package.FileList.ItemsSource = Enumerable.Range(0, 8).Select(i => new PackageFile($"file{i}.data", "the clip's record file, which replaces the game's")).ToList();
            package.FileList.SelectedIndex = 0;
            // the same screen while Build waits for the clip index: its progress beside Build, who plays the clips not known yet
            var waiting = new PackageView();
            if (!waiting.ShowUsersFor(kept, null, null, 0.42)) failures.Add($"{mode} package-waiting: Build doesn't wait for the index");
            waiting.FileList.ItemsSource = package.FileList.ItemsSource;

            // the Log screen with a log's lines: starts of the install's clips (their readable name and who from the index), of an ID it
            // doesn't hold, a line that doesn't read, its notes, and the logger's size limit reached
            var log = new LogView();
            log.UseSettings(null, automated: true);
            var logClips = Clips("mca_emote_Hello_", 6, 9000);
            var logLines = new List<Core.Logs.ClipLogLine>();
            int number = 0;
            Core.Logs.ClipLogLine Line(string s) => Core.Logs.ClipLog.Parse(System.Text.Encoding.UTF8.GetBytes(s), ++number)!;
            logLines.Add(Line("# GRB Clip Logger, DX11 build (GRB.exe), started 2026-11-18 19:10:07.379"));
            logLines.Add(Line(Core.Logs.ClipLog.Header));
            for (int i = 0; i < 12; i++)
                logLines.Add(Line(LogScreenTests.Start(i * 977, player: i % 3 != 1, clip: i == 4 ? 0x00000ABCDEF01234 : logClips[i % 6].Id, source: i % 2 == 0 ? "motion" : "clip")));
            logLines.Add(Line("2026-11-18 19:10:18.490,eleven,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip"));
            logLines.Add(Line("# mark 1 at 42.757 s: 3 starts read, 3 written, 0 repeats skipped"));
            logLines.Add(Line("# the file reached its size limit - from here on starts are counted, not written"));
            foreach (var l in logLines.Where(l => l.Kind == Core.Logs.ClipLogKind.Start))
            {
                l.Clip = logClips.FirstOrDefault(c => c.Id == l.ClipId);
                l.Lookup = l.Clip is null ? Core.Logs.ClipLookup.NotFound : Core.Logs.ClipLookup.Found;
            }
            log.ShowLinesForTests(logLines);
            log.List.Tag = IndexFor(logClips);
            log.List.SelectedIndex = 1;

            var low = new Dictionary<string, (double Ratio, string What)>();
            foreach (var (screen, view) in new (string, FrameworkElement)[] { ("library", library), ("edit", edit), ("import", import), ("package", package), ("package-waiting", waiting), ("log", log) })
            {
                var w = new Window { Content = view };
                w.SetResourceReference(Control.BackgroundProperty, Theme.Key("Surface"));
                w.SetResourceReference(Control.ForegroundProperty, Theme.Key("Text"));
                view.Measure(new Size(Width, Height));
                view.Arrange(new Rect(0, 0, Width, Height));
                view.UpdateLayout();
                uint surface = palette["Surface"];
                foreach (var tb in All<TextBlock>(view))
                {
                    if (string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0 || !Shown(tb, view) || tb.FontFamily.Source.Contains("Icons") || tb.FontFamily.Source.Contains("MDL2"))
                        continue;
                    if (tb.Foreground is not SolidColorBrush fgBrush) { failures.Add($"{mode} {screen}: '{tb.Text}' has no plain colour"); continue; }
                    uint bg = BackgroundOf(tb, view, surface);
                    uint fg = Over(fgBrush.Color, OpacityOf(tb, view), bg);
                    double r = ThemePalette.Contrast(fg, bg);
                    string kind = !tb.IsEnabled ? "greyed-out" : Ancestor<ListBoxItem>(tb) is { IsSelected: true } ? "selected row" : "other";
                    string what = $"{screen} '{Short(tb.Text)}' {fg:X8} on {bg:X8}";
                    if (!low.TryGetValue(kind, out var l) || r < l.Ratio) low[kind] = (r, what);
                    if (r < 4.5) failures.Add($"{mode} {what}: {r:F2}");
                }

                // the lists' rows as tight as before the theme: a list's row at most 24 high (the theme's are 42), a list box's row padded
                // 4 and 1 (the theme's 12 all round)
                foreach (var item in All<ListViewItem>(view).Where(x => Shown(x, view)).Take(3))
                    if (item.ActualHeight > 24) failures.Add($"{mode} {screen}: a list's row is {item.ActualHeight:F0} high");
                // the Package screen's list (the one every screen uses) shows every clip it holds, each with who plays it in its own
                // column (the whole text and what "probably" stands for on hover), a row of long warnings on one line, whole on hover
                if (screen == "package")
                {
                    var playedBy = All<TextBlock>(package.ItemsPanel.List).Where(t => t.ToolTip is string s && s.Contains(ClipUsers.PlayedByTip, StringComparison.Ordinal)).ToList();
                    if (!playedBy.Select(t => t.Text).SequenceEqual([several.Text, ClipCategories.Player]))
                        failures.Add($"{mode} package: the rows say who plays their clip as {string.Join(" / ", playedBy.Select(t => t.Text))}");
                    foreach (var t in playedBy)
                    {
                        lines.Add($"{mode} package: '{Short(t.Text)}' {t.ActualWidth:F0} x {t.ActualHeight:F0}");
                        if (!Shown(t, view)) failures.Add($"{mode} package: '{t.Text}' is out of the list's sight");
                        // what "probably" stands for on hover, as on the other screens
                        if (t.ToolTip is not string tip || !tip.Contains(ClipUsers.PlayedByTip, StringComparison.Ordinal) || !tip.StartsWith(t.Text, StringComparison.Ordinal))
                            failures.Add($"{mode} package: '{t.Text}' has no tooltip with its whole text and what \"probably\" stands for");
                    }
                    if (package.UsersNotice.ToolTip as string != ClipUsers.PlayedByTip) failures.Add($"{mode} package: the notice has no tooltip saying what \"probably\" stands for");
                    foreach (var (tip, name) in new[] { (library.InfoPlayedByLabel.ToolTip, "the clip info's Played by"), (edit.UsersText.ToolTip, "Edit's Played by"), (import.UsersText.ToolTip, "Import's Played by") })
                        if (tip as string != ClipUsers.PlayedByTip) failures.Add($"{mode}: {name} has another tooltip: '{tip}'");
                    if (All<TextBlock>(package.ItemsPanel.List).FirstOrDefault(t => t.ToolTip as string == LongWarnings) is not { } warned)
                        failures.Add($"{mode} package: the long warnings have no tooltip with their whole text");
                    else
                    {
                        lines.Add($"{mode} package: the long warnings {warned.ActualWidth:F0} x {warned.ActualHeight:F0}");
                        if (warned.ActualHeight > 24) failures.Add($"{mode} package: the long warnings take {warned.ActualHeight:F0}, more than one line");
                    }
                    // the screen's own rows read by the list
                    if (package.ShownRows.Count != 2) failures.Add($"{mode} package: {package.ShownRows.Count} rows, not 2");
                    // each clip's checks' verdict in sight without a sideways scroll, a badge on its level's colour (WARN, then PASS)
                    var badges = All<TextBlock>(package.ItemsPanel.List).Where(t => t.Text is "PASS" or "WARN" or "FAIL" && Shown(t, view)).ToList();
                    if (!badges.Select(t => t.Text).SequenceEqual(["WARN", "PASS"])) failures.Add($"{mode} package: the badges shown are {string.Join(", ", badges.Select(t => t.Text))}");
                    foreach (var t in badges)
                    {
                        if (ScrolledAway(t, view) || CutOf(t, view) is not null) failures.Add($"{mode} package: the badge '{t.Text}' isn't in sight whole");
                        if (Ancestor<Border>(t) is not { Background: SolidColorBrush b } || Argb(b) != palette[t.Text == "PASS" ? "PassBadge" : "WarnBadge"])
                            failures.Add($"{mode} package: the badge '{t.Text}' isn't on its level's colour");
                    }
                }
                // while Build waits for the clip index: greyed, the wait line with its progress whole beside it, each row saying who plays
                // its clip isn't known yet
                if (screen == "package-waiting")
                {
                    if (!Shown(waiting.WaitText, view) || !waiting.WaitText.Text.StartsWith("Waiting for the clip index: reading the game's graph, 42%", StringComparison.Ordinal))
                        failures.Add($"{mode} package-waiting: the wait line isn't shown: '{waiting.WaitText.Text}'");
                    else if (CutOf(waiting.WaitText, view) is { } why) failures.Add($"{mode} package-waiting: the wait line is cut: {why}");
                    if (Math.Abs(waiting.WaitBar.Value - 0.42) > 1e-9) failures.Add($"{mode} package-waiting: the bar stands at {waiting.WaitBar.Value}");
                    if (All<TextBlock>(waiting.ItemsPanel.List).Count(t => t.Text == ClipUsers.NotKnownText) != 2)
                        failures.Add($"{mode} package-waiting: the rows don't say who plays their clip isn't known yet");
                    lines.Add($"{mode} package-waiting: '{Short(waiting.WaitText.Text)}' {waiting.WaitText.ActualWidth:F0} x {waiting.WaitText.ActualHeight:F0}");
                }
                // the log's lines with their clip's readable name, an unknown ID said so with the ID, the notes in their strip, the limit
                if (screen == "log")
                {
                    foreach (var want in new[] { "Holstered walk, standing", Core.Logs.ClipLogLine.UnknownText, "0x00000ABCDEF01234", "unreadable line", "mca_emote_Hello_00" })
                        if (!All<TextBlock>(log.List).Any(t => t.Text == want && Shown(t, view) && !ScrolledAway(t, view)))
                            failures.Add($"{mode} log: '{want}' isn't shown in the list");
                    if (All<TextBlock>(log.NotesList).Count(t => Shown(t, view) && t.Text.StartsWith("Line ", StringComparison.Ordinal)) != 3)
                        failures.Add($"{mode} log: the notes' strip doesn't show the log's 3 notes");
                    if (!Shown(log.LimitText, view) || CutOf(log.LimitText, view) is { } cut) failures.Add($"{mode} log: the size limit isn't said whole");
                    lines.Add($"{mode} log: {log.CountText.Text}");
                }
                foreach (var item in All<ListBoxItem>(view).Where(x => x is not ListViewItem && Shown(x, view)).Take(3))
                    if (item.Padding != new Thickness(4, 1, 4, 1)) failures.Add($"{mode} {screen}: a list box's row is padded {item.Padding}");
                lines.Add($"{mode} {screen}: rows {string.Join(", ", All<ListBoxItem>(view).Where(x => Shown(x, view)).Take(2).Select(x => $"{x.GetType().Name} {x.ActualHeight:F0}"))}");
                // a column's header shows its word whole
                foreach (var h in All<GridViewColumnHeader>(view).Where(h => h.Content is string && h.ActualWidth > 0))
                    if (All<TextBlock>(h).FirstOrDefault() is { } t)
                    {
                        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (t.DesiredSize.Width > h.ActualWidth - h.Padding.Left - h.Padding.Right + 0.5)
                            failures.Add($"{mode} {screen}: the header '{h.Content}' needs {t.DesiredSize.Width:F0} and has {h.ActualWidth - h.Padding.Left - h.Padding.Right:F0}");
                    }
                // a combo box shows each of its items whole when it is the one chosen: the item's text no wider than the room the box's
                // template gives the chosen item; but Edit's swap source box, whose names run longer than any box could show: the chosen
                // one is written whole under it (below)
                foreach (var c in All<ComboBox>(view).Where(x => Shown(x, view) && x.Items.Count > 0 && x != edit.SourceBox))
                {
                    if (c.Template?.FindName("PART_ContentPresenter", c) is not FrameworkElement room) { failures.Add($"{mode} {screen}: the combo box {c.Name} has no content presenter"); continue; }
                    var widest = c.Items.Cast<object>().Select(i => i is ComboBoxItem { Content: string s } ? s : i?.ToString() ?? "")
                        .Select(s => (Text: s, Width: TextWidth(s, c))).MaxBy(x => x.Width);
                    lines.Add($"{mode} {screen}: combo box {c.Name} {c.ActualWidth:F0} wide, its longest item '{widest.Text}' {widest.Width:F0} in {room.ActualWidth:F0}");
                    if (widest.Width > room.ActualWidth + 0.5)
                        failures.Add($"{mode} {screen}: the combo box {c.Name}'s item '{widest.Text}' needs {widest.Width:F0} and has {room.ActualWidth:F0}");
                }
                foreach (var row in All<WrapPanel>(view).Where(x => All<ComboBox>(x).Count > 0))
                    lines.Add($"{mode} {screen}: a row of combo boxes {row.ActualWidth:F0} wide, {row.ActualHeight:F0} high");
                // the texts read whole: the viewers' legends, the lists' status words, the chosen swap source's name
                foreach (var t in All<TextBlock>(view).Where(t => Shown(t, view) && t.ActualWidth > 0
                             && (t.Name == "LegendText" || ((StatusWords.Contains(t.Text) || t.Text == EditScreenTests.Mixed.Marks) && Ancestor<ListViewItem>(t) is not null && !ScrolledAway(t, view)))))
                {
                    lines.Add($"{mode} {screen}: '{Short(t.Text)}' {t.ActualWidth:F0} x {t.ActualHeight:F0}");
                    if (CutOf(t, view) is { } why) failures.Add($"{mode} {screen}: '{Short(t.Text)}' is cut: {why}");
                }
                if (screen == "edit" && !All<TextBlock>(view).Any(t => t.Text == LongSource && Shown(t, view) && t.ActualWidth > 0 && CutOf(t, view) is null))
                    failures.Add($"{mode} edit: the chosen swap source '{LongSource}' is shown whole nowhere");
                // the edits each clip allows, in sight and whole in the list's own column
                if (screen == "edit" && !All<TextBlock>(view).Any(t => t.Text == EditScreenTests.Mixed.Marks && Shown(t, view) && !ScrolledAway(t, view) && CutOf(t, view) is null))
                    failures.Add($"{mode} edit: the edits column's marks '{EditScreenTests.Mixed.Marks}' are shown whole nowhere in sight");
                w.Content = null;
            }
            foreach (var (kind, (ratio, what)) in low.OrderBy(x => x.Key))
                lines.Add($"{mode}: lowest {kind} text {ratio:F2} ({what})");

            // a slider that can't be used is drawn in the tool's greyed template, in palette colours; one that can, in the theme's own
            var greyed = app.FindResource("Grb.DisabledSlider");
            var usable = new Slider { Minimum = 0, Maximum = 2, Value = 1, Width = 200 };
            var holder = new Window { Content = usable };
            usable.Measure(new Size(200, 40));
            usable.Arrange(new Rect(0, 0, 200, 40));
            if (ReferenceEquals(usable.Template, greyed)) failures.Add($"{mode}: a slider that can be used is drawn greyed");
            holder.Content = null;
            foreach (var s in new FrameworkElement[] { library, edit, import, package }.SelectMany(All<Slider>))
            {
                if (ReferenceEquals(s.Template, greyed) != !s.IsEnabled)
                    failures.Add($"{mode}: the slider {s.Name} (enabled: {s.IsEnabled}) is drawn {(ReferenceEquals(s.Template, greyed) ? "greyed" : "as the theme's")}");
                if (!s.IsEnabled)
                    foreach (var f in All<Shape>(s).Select(x => x.Fill).Concat(All<Border>(s).Select(x => x.Background)))
                        if (Argb(f) is { } c && c >> 24 != 0 && !palette.Colours.Values.Contains(c))
                            failures.Add($"{mode}: the greyed slider {s.Name} draws {c:X8}, not a palette colour");
            }
            if (!ReferenceEquals(edit.ScaleSlider.Template, greyed)) failures.Add($"{mode}: the Edit screen's scale slider, greyed before a clip, isn't drawn greyed");

            // every brush a list's row takes from the theme for its text and its fill, in any state (hovered, selected, pressed), is one the
            // tool gives a palette colour, so the pairs the palette's test measures are the ones drawn
            foreach (var (label, style) in new[] { ("list box row", app.TryFindResource(typeof(ListBoxItem)) as Style), ("list row", app.TryFindResource(System.Windows.Controls.GridView.GridViewItemContainerStyleKey) as Style) })
            {
                var keys = RowBrushKeys(style).Order().ToList();
                lines.Add($"{mode}: a {label} takes {string.Join(", ", keys)}");
                if (keys.Count == 0) failures.Add($"{mode}: no brush found in a {label}'s style");
                foreach (var key in keys)
                    if (!Theme.FluentBrushes.Any(b => b.FluentKey == key) && !key.StartsWith("Grb.", StringComparison.Ordinal))
                        failures.Add($"{mode}: a {label} takes the theme's {key}, which the tool leaves the theme's");
            }
            // a list's hovered and selected rows take the selected row's text colour (under high contrast, the highlight's text colour)
            if (!RowBrushKeys(app.TryFindResource(System.Windows.Controls.GridView.GridViewItemContainerStyleKey) as Style).Contains(Theme.Key("SelectedText")))
                failures.Add($"{mode}: a list's row doesn't take the selected row's text colour when hovered or selected");
        }
    }

    /// <summary>The theme's resource keys a row's style and template (with the styles it is built on) set its text and fill from, in
    /// its setters and its triggers.</summary>
    static IEnumerable<string> RowBrushKeys(Style? style)
    {
        var keys = new HashSet<string>();
        void FromSetters(IEnumerable<SetterBase> setters)
        {
            foreach (var s in setters.OfType<Setter>())
            {
                if (s.Value is DynamicResourceExtension { ResourceKey: string k }
                    && (s.Property == Control.BackgroundProperty || s.Property == Control.ForegroundProperty || s.Property == Border.BackgroundProperty
                        || s.Property == TextElement.ForegroundProperty))
                    keys.Add(k);
                if (s.Value is ControlTemplate t) FromTriggers(t.Triggers);
            }
        }
        void FromTriggers(IEnumerable<TriggerBase> triggers)
        {
            foreach (var t in triggers)
                FromSetters(t switch { Trigger x => x.Setters, MultiTrigger x => x.Setters, DataTrigger x => x.Setters, MultiDataTrigger x => x.Setters, _ => [] });
        }
        for (var s = style; s is not null; s = s.BasedOn)
        {
            FromSetters(s.Setters);
            FromTriggers(s.Triggers);
        }
        return keys;
    }

    /// <summary>The width of a text in a control's font.</summary>
    static double TextWidth(string text, Control c)
    {
        var t = new TextBlock { Text = text, FontFamily = c.FontFamily, FontSize = c.FontSize, FontWeight = c.FontWeight, FontStyle = c.FontStyle, FontStretch = c.FontStretch };
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return t.DesiredSize.Width;
    }

    /// <summary>Why a text isn't shown whole, or null: its lines need more room than it was given, or the screen's edge or a parent's
    /// clip (a cell's, a column's) cuts it.</summary>
    static string? CutOf(TextBlock tb, FrameworkElement root)
    {
        var m = new TextBlock
        {
            Text = tb.Text, FontFamily = tb.FontFamily, FontSize = tb.FontSize, FontWeight = tb.FontWeight, FontStyle = tb.FontStyle,
            FontStretch = tb.FontStretch, TextWrapping = tb.TextWrapping, Padding = tb.Padding,
        };
        m.Measure(new Size(tb.ActualWidth, double.PositiveInfinity));
        if (m.DesiredSize.Width > tb.ActualWidth + 0.5 || m.DesiredSize.Height > tb.ActualHeight + 0.5)
            return $"it needs {m.DesiredSize.Width:F0} x {m.DesiredSize.Height:F0} and has {tb.ActualWidth:F0} x {tb.ActualHeight:F0}";
        var onScreen = tb.TransformToAncestor(root).TransformBounds(new Rect(tb.RenderSize));
        if (onScreen.Left < -0.5 || onScreen.Top < -0.5 || onScreen.Right > root.ActualWidth + 0.5 || onScreen.Bottom > root.ActualHeight + 0.5)
            return $"it lies at {onScreen.Left:F0}-{onScreen.Right:F0} on a screen {root.ActualWidth:F0} wide";
        for (DependencyObject? d = tb; d is not null && d != root; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement fe && System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(fe) is { } clip)
            {
                var b = tb.TransformToAncestor(fe).TransformBounds(new Rect(tb.RenderSize));
                var c = clip.Bounds;
                if (b.Left < c.Left - 0.5 || b.Right > c.Right + 0.5 || b.Top < c.Top - 0.5 || b.Bottom > c.Bottom + 0.5)
                    return $"a {fe.GetType().Name}'s clip ({c.Left:F0}-{c.Right:F0}) cuts it ({b.Left:F0}-{b.Right:F0})";
            }
        return null;
    }

    /// <summary>Whether a text is scrolled out of a list's view: in a row the list's foot shows only in part, or in a column wholly
    /// further right than the list's panel shows.</summary>
    static bool ScrolledAway(TextBlock tb, FrameworkElement root)
    {
        for (DependencyObject? d = tb; d is not null && d != root; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollContentPresenter p && System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(p) is { } clip)
            {
                var b = tb.TransformToAncestor(p).TransformBounds(new Rect(tb.RenderSize));
                var c = clip.Bounds;
                if (b.Top < c.Top - 0.5 || b.Bottom > c.Bottom + 0.5 || b.Right <= c.Left || b.Left >= c.Right) return true;
            }
        return false;
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "..." : s;

    static uint? Argb(object? brush) => brush is SolidColorBrush b ? (uint)b.Color.A << 24 | (uint)b.Color.R << 16 | (uint)b.Color.G << 8 | b.Color.B : null;

    /// <summary>A colour drawn at <paramref name="opacity"/> over an opaque one, as an opaque colour.</summary>
    static uint Over(Color top, double opacity, uint under)
    {
        double a = top.A / 255.0 * opacity;
        uint Mix(byte t, int shift) => (uint)Math.Round(t * a + ((under >> shift) & 0xFF) * (1 - a));
        return 0xFF000000 | Mix(top.R, 16) << 16 | Mix(top.G, 8) << 8 | Mix(top.B, 0);
    }

    /// <summary>What is drawn under an element: the fills of the elements under it in its panels (those before it that cover its
    /// middle) and of its parents, from the nearest opaque one up, over the window's surface.</summary>
    static uint BackgroundOf(FrameworkElement e, FrameworkElement root, uint surface)
    {
        var mid = e.TransformToAncestor(root).Transform(new Point(e.ActualWidth / 2, e.ActualHeight / 2));
        var layers = new List<(Color Colour, double Opacity)>();
        bool opaque = false;
        DependencyObject cur = e;
        while (!opaque && cur != root && VisualTreeHelper.GetParent(cur) is { } parent)
        {
            for (int i = IndexIn(parent, cur) - 1; i >= 0 && !opaque; i--)
                if (VisualTreeHelper.GetChild(parent, i) is FrameworkElement sib && Fill(sib) is { } f && Shown(sib, root)
                    && sib.TransformToAncestor(root).TransformBounds(new Rect(sib.RenderSize)).Contains(mid))
                    Add(f, sib.Opacity);
            if (!opaque && parent is FrameworkElement pe && Fill(pe) is { } pf) Add(pf, pe.Opacity);
            cur = parent;
        }
        uint bg = surface;
        for (int i = layers.Count - 1; i >= 0; i--) bg = Over(layers[i].Colour, layers[i].Opacity, bg);
        return bg;

        void Add(Color c, double opacity)
        {
            if (c.A == 0 || opacity == 0) return;
            layers.Add((c, opacity));
            opaque = c.A == 255 && opacity >= 1;
        }
    }

    static Color? Fill(FrameworkElement e) => (e switch
    {
        Border b => b.Background,
        Panel p => p.Background,
        Rectangle r => r.Fill,
        _ => null,
    }) is SolidColorBrush s ? s.Color : null;

    static int IndexIn(DependencyObject parent, DependencyObject child)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (VisualTreeHelper.GetChild(parent, i) == child) return i;
        return 0;
    }

    static double OpacityOf(UIElement e, UIElement root)
    {
        double o = 1;
        for (DependencyObject? d = e; d is not null && d != root; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement u) o *= u.Opacity;
        return o;
    }

    /// <summary>Whether the element and every parent up to the screen are visible (a window never shown holds no element IsVisible).</summary>
    static bool Shown(UIElement e, UIElement root)
    {
        for (DependencyObject? d = e; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is UIElement u && (u.Visibility != Visibility.Visible || u.Opacity == 0)) return false;
            if (d == root) return true;
        }
        return true;
    }

    static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
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
}
