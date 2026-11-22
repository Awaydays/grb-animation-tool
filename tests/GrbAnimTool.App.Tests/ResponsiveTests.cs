using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>The window stays responsive with an install's thousands of clips: every list realises only the rows in sight, whichever
/// screen is shown first and at whatever size, and the Edit screen opens at once with the edits each clip allows still being worked
/// out off the window's thread (the list showing every clip, its switch saying it waits, with the progress), then takes them.</summary>
[Collection("wpf")]
public sealed class ResponsiveTests
{
    /// <summary>The most list rows a screen may realise: a tall window shows under fifty.</summary>
    const int MostRows = 200;

    static readonly Size[] Sizes = [new(1484, 861), new(860 - 16, 520 - 40 - 40)];

    static double Lay(FrameworkElement root, Size size)
    {
        var sw = Stopwatch.StartNew();
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        return sw.Elapsed.TotalMilliseconds;
    }

    static IEnumerable<(int Tab, string Name, ClipListPanel Panel)> Screens(MainWindow w) =>
        [(0, "library", w.LibraryPage.ClipPanel), (1, "edit", w.EditPage.ClipPanel), (2, "import", w.ImportPage.SlotPanel), (3, "package", w.PackagePage.ItemsPanel)];

    /// <summary>The window's four screens, their lists given 9,970 clips before the window is first laid out, each screen in turn shown
    /// first and then the others clicked: each list's first layout realises the rows in sight only, at the usual size and at the least.</summary>
    [Fact]
    public void EveryListRealisesOnlyTheRowsInSightWhenItsScreenIsFirstShown()
    {
        Wpf.Run(() =>
        {
            Theme.Apply(ThemeChoice.Dark);
            var clips = ThemeScreenTests.Clips("mil_gen_m_ale_ros_std_V0_N_death_", 9970, 100);
            foreach (var size in Sizes)
            for (int first = 0; first < 4; first++)
            {
                // each screen in turn shown first, its list (and every other) already full when it is first laid out, then the others
                var w = new MainWindow(ViewerArgs.Parse(["--theme", "dark"]));
                var root = (FrameworkElement)w.Content;
                foreach (var (_, _, panel) in Screens(w)) ThemeScreenTests.Fill(panel, clips);
                foreach (var (tab, name, panel) in Screens(w).OrderBy(s => s.Tab == first ? -1 : s.Tab))
                {
                    w.Tabs.SelectedIndex = tab;
                    double ms = Lay(root, size);
                    int rows = ScreenTimingTests.Containers(panel.List);
                    // (the Package screen's list holds the clips kept, none here: it empties itself when shown)
                    Assert.True(rows < MostRows && (rows >= ClipListPanel.LeastRows || panel.List.Items.Count == 0),
                        $"{size} {name}: {rows} rows realised of {panel.List.Items.Count} ({ms:F0} ms)");
                    // and scrolled to its end, still only the rows in sight
                    panel.List.ScrollIntoView(clips[^1]);
                    Lay(root, size);
                    rows = ScreenTimingTests.Containers(panel.List);
                    Assert.True(rows < MostRows, $"{size} {name}: {rows} rows realised at the list's end");
                }
            }
        });
    }

    /// <summary>When only a screen's constraint changes (the Edit screen's marks arriving while the list is browsed), the row at the top of
    /// the list stays at the top; when the constraint leaves that row out, the first row after it that stays takes its place.</summary>
    [Fact]
    public void AChangedConstraintKeepsTheListWhereItWasBrowsed()
    {
        Wpf.Run(() =>
        {
            Theme.Apply(ThemeChoice.Light);
            var panel = new ClipListPanel();
            var clips = ThemeScreenTests.Clips("mcm_nwp_fm_std_V0_idle_", 3000, 100);
            panel.SetClips(clips);
            var w = new Window { Content = panel };
            var size = new Size(900, 600);
            void Lay() { panel.Measure(size); panel.Arrange(new Rect(size)); panel.UpdateLayout(); }
            Lay();
            // browsed to row 1,200 (an even index: kept by "even only")
            panel.ScrollToRow(1200);
            Lay();
            Assert.Same(clips[1200], panel.TopRow);
            panel.SetConstraint(c => c.Index % 2 == 0, "even only", keepPlace: true);
            Lay();
            Assert.Same(clips[1200], panel.TopRow);
            // all again, then browsed to an odd row, which "even only" leaves out: the next row after it, an even one, takes the top
            panel.SetConstraint(null, keepPlace: true);
            Lay();
            panel.ScrollToRow(1501);
            Lay();
            Assert.Same(clips[1501], panel.TopRow);
            panel.SetConstraint(c => c.Index % 2 == 0, "even only", keepPlace: true);
            Lay();
            Assert.Same(clips[1502], panel.TopRow);
            // browsed past every row the constraint keeps: the last row before it that stays takes the top, as near it as the list scrolls
            panel.SetConstraint(null, keepPlace: true);
            Lay();
            var all = clips.ToList();
            var firstThousand = all.Take(1000).ToHashSet();
            panel.ScrollToRow(1500);
            Lay();
            panel.SetConstraint(firstThousand.Contains, "the first thousand", keepPlace: true);
            Lay();
            Assert.Contains(panel.TopRow!, firstThousand);
            Assert.True(all.IndexOf(panel.TopRow!) > 950, $"the list went to row {all.IndexOf(panel.TopRow!)}");
            // a scroll still waiting for the layout when the constraint changes: where the list is kept
            panel.SetConstraint(null, keepPlace: true);
            Lay();
            panel.ScrollToRow(800, lay: false);
            panel.SetConstraint(_ => true, "all", keepPlace: true);
            Lay();
            Assert.Same(clips[800], panel.TopRow);
            // a constraint the person sets (a file's length, a switch): the list shows the clip shown, else its first row, as a search does
            panel.Show(clips[40]);
            panel.ScrollToRow(1000);
            Lay();
            panel.SetConstraint(c => c.Index % 4 == 0, "a quarter");
            Lay();
            Assert.Contains(clips[40], panel.Rows);
            var shownRows = panel.Rows.ToList();
            Assert.True(shownRows.IndexOf(clips[40]) - shownRows.IndexOf(panel.TopRow!) is >= 0 and < 30, "the clip shown isn't in sight");
            panel.Show(null);
            panel.ScrollToRow(400);
            Lay();
            panel.SetConstraint(c => c.Index % 3 == 0, "a third");
            Lay();
            Assert.Same(panel.Rows[0], panel.TopRow);
            // a new search (not a constraint) goes to the best match, the first row, as before
            panel.SearchBox.Text = "idle";
            panel.Apply();
            Lay();
            Assert.Same(panel.Rows[0], panel.TopRow);
            w.Content = null;
        });
    }

    /// <summary>At a scaled screen (125% and 150%, layout rounded to device pixels, so a measure and an arrange may differ by a fraction of a
    /// pixel) every screen's panes settle in a few layout passes: the content's height stops changing and the layout is valid, never a
    /// layout feeding itself.</summary>
    [Fact]
    public void ThePanesSettleAtAScaledScreen()
    {
        Wpf.Run(() =>
        {
            Theme.Apply(ThemeChoice.Light);
            var clips = ThemeScreenTests.Clips("mil_gen_m_ale_ros_std_V0_N_death_", 400, 100);
            foreach (double scale in new[] { 1.25, 1.5 })
                foreach (var size in new[] { new Size(1484, 861), new Size(844, 440), new Size(1001.3, 703.7) })
                {
                    var w = new MainWindow(ViewerArgs.Parse(["--theme", "light"]));
                    var screen = (FrameworkElement)w.Content;
                    w.Content = null;
                    var root = new Border
                    {
                        Child = screen, UseLayoutRounding = true,
                        LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale),
                    };
                    var host = new Window { Content = root };
                    System.Windows.Media.VisualTreeHelper.SetRootDpi(root, new System.Windows.DpiScale(scale, scale));
                    foreach (var (_, _, panel) in Screens(w)) ThemeScreenTests.Fill(panel, clips);
                    foreach (var (tab, name, _) in Screens(w))
                    {
                        w.Tabs.SelectedIndex = tab;
                        var scroll = tab switch { 0 => w.LibraryPage.PaneScroll, 1 => w.EditPage.PaneScroll, 2 => w.ImportPage.PaneScroll, _ => w.PackagePage.PaneScroll };
                        var content = (FrameworkElement)scroll.Content;
                        void Settle(Size at)
                        {
                            double last = double.NaN;
                            int passes = 0, steady = 0;
                            while (steady < 3)
                            {
                                Assert.True(++passes <= 10, $"{scale} {at} {name}: the panes never settle (content height {last})");
                                root.Measure(at);
                                root.Arrange(new Rect(at));
                                root.UpdateLayout();
                                steady = content.Height == last && scroll.IsMeasureValid && scroll.IsArrangeValid ? steady + 1 : 0;
                                last = content.Height;
                            }
                        }
                        Settle(size);
                        // a device pixel shorter: the content and the panes kept in view both come down, no pane left taller than the content
                        Settle(new Size(size.Width, size.Height - 0.8));
                        foreach (var (pane, _) in scroll.InView)
                            Assert.True(pane.Height <= content.Height + 0.01, $"{scale} {size} {name}: a pane {pane.Height} high in content {content.Height} high");
                    }
                    host.Content = null;
                }
        });
    }

    /// <summary>A clip list offered no height at all (a holder that measures it so) still realises only a few rows.</summary>
    [Fact]
    public void AListOfferedNoHeightTakesItsLeastHeight()
    {
        Wpf.Run(() =>
        {
            var panel = new ClipListPanel();
            ThemeScreenTests.Fill(panel, ThemeScreenTests.Clips("mcm_nwp_fm_std_V0_idle_", 9970, 100));
            var holder = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var w = new Window { Content = holder };
            holder.Measure(new Size(900, double.PositiveInfinity));
            holder.Arrange(new Rect(holder.DesiredSize));
            holder.UpdateLayout();
            Assert.True(ScreenTimingTests.Containers(panel.List) < MostRows, $"{ScreenTimingTests.Containers(panel.List)} rows realised");
            w.Content = null;
        });
    }

    /// <summary>On the game's files, the install's own forge (a modded one, with clips added) and the vanilla forge: the window opens the
    /// install; the Edit screen is shown while the edits each clip allows are still being worked out (held back here), off the window's
    /// thread: its list shows every clip with the rows in sight only, its switch greyed and saying it waits, the count and the bar with the
    /// progress; a click on a row shows its clip's edits; the other screens open at once too. Then the marks come: the list marks and
    /// filters by them, and they are kept, so the next opening of the same files reads them from the cache with no work. No error comes:
    /// none uncaught, none a screen caught itself, none logged or shown.</summary>
    [GameDataFact]
    public void OnTheGameTheEditScreenOpensAtOnceWhileTheEditsAreWorkedOut()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "responsive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var errors = new ErrorReport { LogPath = Path.Combine(dir, "errors.log"), Show = _ => true, ShowBox = _ => { } };
        int builds = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        InstallWork.BuildMarks = (g, p, cancel) =>
        {
            Interlocked.Increment(ref builds);
            p.Report(0.25);
            release.Task.Wait(cancel);
            return EditMarks.From(g.Clips.ToDictionary(c => c.Id, c => (byte)(c.Index % 3 == 0 ? 0 : EditMarks.AllEdits)));
        };
        var opened = new List<GameInstall>();
        try
        {
            // the error report the program installs: every error nobody caught, and every one a screen caught itself, reaches it
            Wpf.Run(() => { errors.Install(Dispatcher.CurrentDispatcher); Wpf.App.UseErrorsForTests(errors); });
            foreach (string? forge in new[] { null, GameDataFactAttribute.Forge })
            {
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                builds = 0;
                Wpf.RunAsync(async () =>
                {
                    Theme.Apply(ThemeChoice.Dark);
                    string cache = Path.Combine(dir, forge is null ? "own" : "vanilla");
                    // (a picture of the Edit screen: the run works the marks out)
                    var a = new List<string> { "--game", GameDataFactAttribute.Game!, "--index-cache", cache, "--shot", Path.Combine(dir, "unused.png") + "||||edit" };
                    if (forge is not null) a.AddRange(["--forge", forge]);
                    var args = ViewerArgs.Parse(a);
                    for (int opening = 0; opening < 2; opening++)
                    {
                        var w = new MainWindow(args);
                        var root = (FrameworkElement)w.Content;
                        Lay(root, Sizes[0]);
                        Assert.Null(await w.Library.LoadForShotsAsync(args));
                        var g = (GameInstall)typeof(LibraryView).GetField("_install", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.LibraryPage)!;
                        opened.Add(g);
                        string what = $"{(forge is null ? "the install's own forge" : "the vanilla forge")}, opening {opening + 1}";
                        var edit = w.EditPage;
                        // a clip that allows no edit by the marks given here, its record readable
                        var zero = g.Clips.First(c => c.Index % 3 == 0 && c.Record is not null);
                        if (opening == 0)
                        {
                            // the marks wait (held back): the screen opens with them pending, and every screen at once
                            await Wait(() => edit.MarksProgress >= 0.25, what + ": the progress");
                            // the bar follows the progress on its timer
                            await Task.Delay(400);
                            foreach (var size in Sizes)
                                foreach (var (tab, name, panel) in Screens(w))
                                {
                                    w.Tabs.SelectedIndex = tab;
                                    double ms = Lay(root, size);
                                    int rows = ScreenTimingTests.Containers(panel.List);
                                    Assert.True(rows < MostRows, $"{what} {size} {name}: {rows} rows realised of {panel.List.Items.Count}");
                                    Assert.True(ms < 3000, $"{what} {size} {name}: its first layout took {ms:F0} ms");
                                }
                            w.Tabs.SelectedIndex = 1;
                            Lay(root, Sizes[0]);
                            Assert.True(edit.MarksPending, what);
                            Assert.Equal(g.Clips.Count, edit.ClipPanel.Rows.Count);
                            Assert.False(edit.ShowAllBox.IsEnabled, what + ": the switch doesn't wait");
                            Assert.Contains("Waiting for which edits each clip allows", edit.ShowAllBox.ToolTip as string);
                            Assert.Equal(Visibility.Visible, edit.MarksBar.Visibility);
                            Assert.Equal(0.25, edit.MarksBar.Value, 3);
                            Assert.Equal("working out the edits, 25%", edit.MarksText.Text);
                            Assert.Contains("working out which edits each clip allows (25%): every clip is listed until then", edit.ClipPanel.CountText.Text);
                            // a click on a row: its clip's edits shown, off the window's thread, while the marks are still worked out
                            var clip = g.FindClip("fm_std_HG_V0_idle_cool_06")!;
                            var sw = Stopwatch.StartNew();
                            edit.ClipPanel.List.SelectedItem = clip;
                            await Wait(() => edit.ShowsClip(clip.Name) && edit.OptionsSummary.StartsWith("allows", StringComparison.Ordinal)
                                && edit.BusyPanel.Visibility == Visibility.Collapsed, what + ": the clip's edits");
                            Assert.Equal("allows Speed, Trim", edit.OptionsSummary);
                            Assert.Equal(Visibility.Collapsed, edit.ErrorBox.Visibility);
                            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"{what}: the clip's edits came in {sw.Elapsed.TotalSeconds:F1} s");
                            Assert.True(edit.MarksPending, what + ": the marks came before they were let go");
                            // a click on the Import screen's list too
                            w.Tabs.SelectedIndex = 2;
                            Lay(root, Sizes[0]);
                            w.ImportPage.SlotPanel.List.SelectedItem = clip;
                            await Dispatcher.Yield(DispatcherPriority.Background);
                            // the slot taken (who plays it said), and with no file chosen, the import asks for one
                            Assert.Same(clip, w.ImportPage.SlotPanel.Shown);
                            await Wait(() => w.ImportPage.UsersText.Text.StartsWith("Played by", StringComparison.Ordinal), what + ": the Import screen's slot");
                            Assert.Equal("Choose a .glb and the clip it replaces.", w.ImportPage.HintText.Text);
                            Assert.Equal(Visibility.Collapsed, w.ImportPage.ErrorBox.Visibility);
                            w.Tabs.SelectedIndex = 1;
                            Lay(root, Sizes[0]);
                            // a click on a clip that will allow no edit, its short wait still running when the marks come (held long here):
                            // the clip is taken then, and stays in the filtered list, selected
                            edit.SelectDelay = TimeSpan.FromSeconds(30);
                            edit.ClipPanel.List.SelectedItem = zero;
                            Assert.False(edit.ShowsClip(zero.Name));
                            release.SetResult();
                        }
                        await edit.WaitForChoicesAsync();
                        edit.SelectDelay = TimeSpan.FromMilliseconds(150);
                        Lay(root, Sizes[0]);
                        Assert.False(edit.MarksPending, what);
                        Assert.True(edit.ShowAllBox.IsEnabled, what);
                        Assert.Equal(Visibility.Collapsed, edit.MarksBar.Visibility);
                        if (opening == 0)
                        {
                            Assert.True(edit.ShowsClip(zero.Name), what + ": the click as the marks came wasn't taken");
                            Assert.Contains(zero, edit.ClipPanel.Rows);
                            Assert.Same(zero, edit.ClipPanel.SelectedClip);
                        }
                        Assert.Equal(g.Clips.Count(c => c.Index % 3 != 0) + (opening == 0 ? 1 : 0), edit.ClipPanel.Rows.Count);
                        Assert.Contains("that allow no edit left out", edit.ClipPanel.CountText.Text);
                        Assert.True(ScreenTimingTests.Containers(edit.ClipPanel.List) < MostRows, what);
                        Assert.Contains(opening == 0 ? "worked out in" : "read from the cache in", edit.MarksHad);
                        Assert.Equal(1, builds);
                        Assert.Single(Directory.GetFiles(cache, "edits-*.json"));
                        if (opening == 0)
                        {
                            // "Show all clips" ticked, a clip that allows no edit chosen, then unticked: that clip stays, selected; the
                            // one before it, no longer shown, leaves
                            var zero2 = g.Clips.First(c => c.Index % 3 == 0 && c.Record is not null && c != zero);
                            edit.ShowAllBox.IsChecked = true;
                            edit.ShowAllBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            Assert.Equal(g.Clips.Count, edit.ClipPanel.Rows.Count);
                            await edit.ChooseClipAsync(zero2);
                            edit.ShowAllBox.IsChecked = false;
                            edit.ShowAllBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            Assert.Contains(zero2, edit.ClipPanel.Rows);
                            Assert.DoesNotContain(zero, edit.ClipPanel.Rows);
                            Assert.Same(zero2, edit.ClipPanel.SelectedClip);

                            // a clip that allows no edit taken from the Library ("Use the Library's clip"): in the filtered list, selected
                            var zero3 = g.Clips.First(c => c.Index % 3 == 0 && c.Record is not null && c != zero && c != zero2);
                            w.LibraryPage.ClipPanel.List.SelectedItem = zero3;
                            Assert.Same(zero3, w.LibraryPage.CurrentClip);
                            Assert.DoesNotContain(zero3, edit.ClipPanel.Rows);
                            edit.UseLibraryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            Assert.True(edit.ShowsClip(zero3.Name));
                            Assert.Contains(zero3, edit.ClipPanel.Rows);
                            Assert.Same(zero3, edit.ClipPanel.SelectedClip);
                            // another clip chosen: the one that allows no edit was in the list only as the clip shown, and leaves it
                            var allowed = g.Clips.First(c => c.Index % 3 != 0 && c.Record is not null);
                            await edit.ChooseClipAsync(allowed);
                            Assert.DoesNotContain(zero3, edit.ClipPanel.Rows);
                            Assert.Same(allowed, edit.ClipPanel.SelectedClip);
                            await edit.ChooseClipAsync(zero3);

                            // two clips picked quickly on the Library: the second is the one shown, the first's read dropped whenever it ends
                            var (first, second) = (g.FindClip("mcm_nwp_fm_std_V1_cycle")!, g.FindClip("mca_emote_Hello")!);
                            w.LibraryPage.ClipPanel.List.SelectedItem = first;
                            w.LibraryPage.ClipPanel.List.SelectedItem = second;
                            await Wait(() => w.LibraryPage.ClipPlays, what + ": the Library's clip");
                            await Task.Delay(300);
                            Assert.Same(second, w.LibraryPage.CurrentClip);
                            Assert.Equal(second.Name, w.LibraryPage.InfoName.Text);
                            Assert.EndsWith($"/ {second.LastTick}", w.LibraryPage.TimeText.Text);

                            // a picture of the Edit screen waiting holds the marks back and gives them back; marks that come during the hold
                            // are the screen's (the hold ends)
                            var shotArgs = ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!]);
                            // (what it says of the clip shown, still being worked out, isn't what this checks)
                            edit.ShowWaitingForShot(new ShotSpec("x.png", null, 0.3f, null, ViewerArgs.EditWaiting), shotArgs);
                            Assert.True(edit.MarksPending);
                            Assert.Equal("working out the edits, 30%", edit.MarksText.Text);
                            Assert.False(edit.ShowAllBox.IsEnabled);
                            Assert.Equal(g.Clips.Count, edit.ClipPanel.Rows.Count);
                            edit.EndWaitingForShot();
                            Assert.False(edit.MarksPending);
                            Assert.True(edit.ShowAllBox.IsEnabled);
                            Assert.Contains("that allow no edit left out", edit.ClipPanel.CountText.Text);
                            edit.ShowWaitingForShot(new ShotSpec("x.png", null, 0.3f, null, ViewerArgs.EditWaiting), shotArgs);
                            edit.SetMarks(EditMarks.From(g.Clips.ToDictionary(c => c.Id, _ => EditMarks.AllEdits)));
                            edit.EndWaitingForShot();
                            Assert.False(edit.MarksPending);
                            Assert.Equal(g.Clips.Count, edit.ClipPanel.Rows.Count);
                            // the Import screen's picture waiting holds the clip index back and gives it back
                            var import = w.ImportPage;
                            Assert.Null(import.ShowWaitingForShot(new ShotSpec("y.png", null, 0.6f, null, ViewerArgs.ImportWaiting), shotArgs));
                            Assert.False(import.SlotPanel.WhoBox.IsEnabled);
                            Assert.Contains("the clip index: reading the game's graph, 60%", import.SlotPanel.CountText.Text);
                            import.EndWaitingForShot();
                            Assert.True(import.SlotPanel.WhoBox.IsEnabled);
                            Assert.DoesNotContain("the clip index:", import.SlotPanel.CountText.Text);
                        }
                        await Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g));
                    }
                });
            }
            Assert.Equal(0, errors.Count);
            Assert.Equal(0, errors.CaughtCount);
            Assert.False(File.Exists(errors.LogPath), "an error was logged");
        }
        finally
        {
            release.TrySetResult();
            InstallWork.BuildMarks = null;
            Wpf.Run(() => { errors.Uninstall(); Wpf.App.UseErrorsForTests(null); });
            Wpf.Run(Core.Import.PackageList.Current.Clear);
            foreach (var g in opened) g.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>On the game's files: the marks being worked out for an install when another is opened stop at once, end with "another
    /// install was opened", and keep nothing; the new install's marks start afresh.</summary>
    [GameDataFact]
    public void TheMarksOfAnInstallLeftStopAndKeepNothing()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "marks-cancel-" + Guid.NewGuid().ToString("N"));
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        using var other = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        var started = new ManualResetEventSlim();
        bool stoppedByCancel = false;
        InstallWork.BuildMarks = (_, p, cancel) =>
        {
            p.Report(0.1);
            started.Set();
            // works until told to stop
            stoppedByCancel = cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            cancel.ThrowIfCancellationRequested();
            throw new TimeoutException("never cancelled");
        };
        try
        {
            InstallWork.Open(g);
            var job = InstallWork.Marks(g, dir);
            var graph = Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g));
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "the marks didn't start");
            Assert.False(job.Done.IsCompleted);
            // another install opened
            InstallWork.Open(other);
            var r = job.Done.Wait(TimeSpan.FromSeconds(10)) ? job.Done.Result : throw new TimeoutException("the marks didn't stop");
            Assert.True(stoppedByCancel);
            Assert.Null(r.Marks);
            Assert.Equal("another game folder was opened", r.Error);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir, "edits-*.json").Length > 0, "the cancelled marks were kept");
            // the new install's own marks: a job of its own, which a late call for the install left (a clip's choice finishing, say) never
            // stops: such a call gets nothing and switches nothing back
            var release = new ManualResetEventSlim();
            InstallWork.BuildMarks = (h, _, cancel) =>
            {
                release.Wait(cancel);
                return EditMarks.From(h.Clips.ToDictionary(c => c.Id, _ => EditMarks.AllEdits));
            };
            var otherJob = InstallWork.Marks(other, null);
            Assert.NotSame(job, otherJob);
            Assert.Null(InstallWork.Swaps(g).GetAwaiter().GetResult());
            Assert.Null(InstallWork.Mirror(g).GetAwaiter().GetResult());
            Assert.Null(InstallWork.Index(g).GetAwaiter().GetResult());
            InstallWork.IndexDone(g, null);
            Assert.Equal("another game folder was opened", InstallWork.Marks(g, dir).Done.GetAwaiter().GetResult().Error);
            Assert.False(InstallWork.Index(other).IsCompleted, "a late call for the install left ended the open install's wait on its index");
            release.Set();
            Assert.Equal(other.Clips.Count, otherJob.Done.GetAwaiter().GetResult().Marks?.Count ?? -1);
            Task.WhenAll(graph, InstallWork.Graph(other), InstallWork.Swaps(other), InstallWork.Mirror(other)).GetAwaiter().GetResult();
        }
        finally
        {
            InstallWork.BuildMarks = null;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>On the game's files, the install's own forge and the vanilla forge: the real marks, worked out through the screens' job on
    /// the workers' threads with a cache folder, are whole (no clip the tool failed on, nothing missing) and kept, and the next opening
    /// reads them back the same.</summary>
    [GameDataFact]
    public void OnTheGameTheRealMarksAreWholeAndKept()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "marks-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string? forge in new[] { null, GameDataFactAttribute.Forge })
            {
                EditMarks built;
                using (var g = GameInstall.Open(GameDataFactAttribute.Game!, forge))
                {
                    InstallWork.Open(g);
                    var r = InstallWork.Marks(g, dir).Done.GetAwaiter().GetResult();
                    Assert.True(r.Marks is not null, r.Error);
                    built = r.Marks;
                    Assert.Null(built.Incomplete);
                    Assert.Null(r.SaveError);
                    Assert.Equal(CacheOutcome.Built, r.Outcome);
                    Assert.Equal(g.Clips.Count, built.Count);
                    if (forge is not null) Assert.Equal(1070, built.NoEdit);
                    Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g)).GetAwaiter().GetResult();
                }
                using (var again = GameInstall.Open(GameDataFactAttribute.Game!, forge))
                {
                    InstallWork.Open(again);
                    var r = InstallWork.Marks(again, dir).Done.GetAwaiter().GetResult();
                    Assert.Equal(CacheOutcome.Reused, r.Outcome);
                    Assert.Equal(built.Masks.OrderBy(k => k.Key), r.Marks!.Masks.OrderBy(k => k.Key));
                    Task.WhenAll(InstallWork.Graph(again), InstallWork.Swaps(again), InstallWork.Mirror(again)).GetAwaiter().GetResult();
                }
            }
            Assert.Equal(2, Directory.GetFiles(dir, "edits-*.json").Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>On the game's files (the install's own forge), with the real marks being worked out on all but one core (no cache): every
    /// screen is shown, with its rows in sight only, in well under a second, a clip clicked on the Edit screen shows its edits within a
    /// few seconds, and the window's thread never waits a second for a turn, from the build's start to its end.</summary>
    [GameDataFact]
    public void OnTheGameTheScreensStayResponsiveWhileTheRealMarksAreWorkedOut()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "under-load-" + Guid.NewGuid().ToString("N"));
        GameInstall? opened = null;
        try
        {
            Wpf.RunAsync(async () =>
            {
                Theme.Apply(ThemeChoice.Dark);
                // (a picture of the Edit screen: the run works the marks out)
                var args = ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--index-cache", dir, "--shot", Path.Combine(dir, "unused.png") + "||||edit"]);
                var w = new MainWindow(args);
                var root = (FrameworkElement)w.Content;
                Lay(root, Sizes[0]);
                Assert.Null(await w.Library.LoadForShotsAsync(args));
                var g = opened = (GameInstall)typeof(LibraryView).GetField("_install", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.LibraryPage)!;
                var edit = w.EditPage;
                await Wait(() => edit.MarksProgress > 0.02, "the real build's start");
                var wait = new ScreenTimingTests.Latency(w.Dispatcher);
                foreach (var (tab, name, panel) in Screens(w).OrderBy(s => s.Tab == 0 ? 9 : s.Tab))
                {
                    w.Tabs.SelectedIndex = tab;
                    double ms = Lay(root, Sizes[0]);
                    int rows = ScreenTimingTests.Containers(panel.List);
                    Assert.True(edit.MarksPending, $"{name}: the build ended before it could be measured under load");
                    Assert.True(ms < 1000, $"{name}: shown in {ms:F0} ms while the marks were worked out");
                    Assert.True(rows < MostRows, $"{name}: {rows} rows realised");
                    await Task.Delay(100);
                }
                w.Tabs.SelectedIndex = 1;
                Lay(root, Sizes[0]);
                var clip = g.FindClip("fm_std_HG_V0_idle_cool_06")!;
                var sw = Stopwatch.StartNew();
                edit.ClipPanel.List.SelectedItem = clip;
                await Wait(() => edit.ShowsClip(clip.Name) && edit.OptionsSummary.StartsWith("allows", StringComparison.Ordinal)
                    && edit.BusyPanel.Visibility == Visibility.Collapsed, "the clip's edits under load");
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the clip's edits came in {sw.Elapsed.TotalSeconds:F1} s under load");
                Assert.True(edit.MarksPending, "the build ended before the click could be measured under load");
                await edit.WaitForChoicesAsync();
                double worst = wait.Take();
                await wait.Stop();
                Assert.True(worst < 1000, $"the window's thread waited {worst:F0} ms for a turn while the marks were worked out");
                Assert.False(edit.MarksPending);
                Assert.Contains("worked out in", edit.MarksHad);
                await Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g));
            });
        }
        finally
        {
            opened?.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>On the game's files, through the screens' own job and the Edit screen: marks whose graph didn't read aren't kept, and Retry
    /// works them out from the start (the graph, the swap sources and the mirror read again, the cache passed over), the list staying as
    /// it was and the texts saying it works meanwhile; a retry that fails keeps the marks had; marks whole but for clips the tool failed
    /// on are kept, those clips not told, the first errors logged with the clip's name, and the screens' own catch-all logs too.</summary>
    [GameDataFact]
    public void RetryWorksTheMarksOutFromTheStartAndCaughtErrorsAreLogged()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "retry-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(dir, "errors.log");
        var noted = new List<string>();
        var errors = new ErrorReport { LogPath = log, Show = _ => true, ShowBox = _ => { }, UnattendedCaught = t => { lock (noted) noted.Add(t); } };
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        var steps = new Queue<Func<CancellationToken, EditMarks>>();
        int builds = 0;
        InstallWork.BuildMarks = (_, _, cancel) => { builds++; return steps.Dequeue()(cancel); };
        var allEdits = g.Clips.ToDictionary(c => c.Id, c => (byte)(c.Index % 3 == 0 ? 0 : EditMarks.AllEdits));
        string cache = Path.Combine(dir, "cache");
        try
        {
            Wpf.Run(() => Wpf.App.UseErrorsForTests(errors));
            Wpf.RunAsync(async () =>
            {
                Theme.Apply(ThemeChoice.Light);
                var edit = new EditView();
                var w = new Window { Content = edit };
                // a first job whose graph "didn't read": shown, not kept (the screen has a cache folder), Retry offered; the screens' job
                // keeps no such marks either: the next call works them out again
                var holdRetry = new ManualResetEventSlim();
                steps.Enqueue(_ => EditMarks.From(allEdits, "the game's animation graph couldn't be read"));
                edit.SetInstall(g, true, new LibraryView { IndexCacheFolder = cache });
                await edit.WaitForChoicesAsync();
                Assert.Equal(Visibility.Visible, edit.RetryButton.Visibility);
                Assert.Equal("not every clip's edits are known: the game's animation graph couldn't be read", edit.MarksText.Text);
                Assert.False(Directory.Exists(cache) && Directory.GetFiles(cache, "edits-*.json").Length > 0, "incomplete marks were kept");
                steps.Enqueue(_ => EditMarks.From(allEdits, "the game's animation graph couldn't be read"));
                int calls = builds;
                var next = await InstallWork.Marks(g, cache).Done;
                Assert.Equal(calls + 1, builds);
                Assert.NotNull(next.Marks!.Incomplete);
                Assert.False(Directory.Exists(cache) && Directory.GetFiles(cache, "edits-*.json").Length > 0, "incomplete marks were kept");
                int rows = edit.ClipPanel.Rows.Count;
                // Retry: the reads started again, the cache passed over; while it runs the list stays and the texts say it works
                var graphBefore = InstallWork.Graph(g);
                steps.Enqueue(cancel => { holdRetry.Wait(cancel); return EditMarks.From(allEdits); });
                int before = builds;
                edit.RetryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.NotSame(graphBefore, InstallWork.Graph(g));
                await Wait(() => builds == before + 1, "the retry's work");
                await Task.Delay(400);
                Assert.True(edit.MarksPending);
                Assert.StartsWith("working out the edits again", edit.MarksText.Text);
                Assert.Contains("working them out again (", edit.ClipPanel.CountText.Text);
                Assert.Contains("Working out again which edits each clip allows", edit.ShowAllBox.ToolTip as string);
                Assert.Equal(rows, edit.ClipPanel.Rows.Count);
                holdRetry.Set();
                await edit.WaitForChoicesAsync();
                Assert.Equal(Visibility.Collapsed, edit.RetryButton.Visibility);
                Assert.Equal(Visibility.Collapsed, edit.MarksText.Visibility);
                rows = edit.ClipPanel.Rows.Count;
                // a retry that fails keeps the marks had before, says so, and offers Retry again
                steps.Enqueue(_ => throw new InvalidCastException("a retry that fails"));
                edit.RetryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await edit.WaitForChoicesAsync();
                Assert.Equal(rows, edit.ClipPanel.Rows.Count);
                Assert.Contains("that allow no edit left out", edit.ClipPanel.CountText.Text);
                Assert.StartsWith("working them out again failed (InvalidCastException: a retry that fails): the marks had before are kept", edit.MarksText.Text);
                Assert.Equal(Visibility.Visible, edit.RetryButton.Visibility);

                // an edit made: an error of the code (an index out of range, an argument checked) is said and logged; a record that can't
                // be read is said only
                var hg = g.FindClip("fm_std_HG_V0_idle_cool_06")!;
                await edit.ChooseClipAsync(hg);
                edit.SpeedChoice.IsChecked = true;
                try
                {
                    EditView.ApplyForTests = (_, _, _, _) => throw new ArgumentOutOfRangeException("index", "a cut past the clip's last key");
                    Assert.StartsWith("The edit failed (ArgumentOutOfRangeException):", await edit.ApplyAsync());
                    Assert.Matches($@"while making {hg.Name} \(Speed [0-9.]+x\) \(caught, and said on the screen\)", File.ReadAllText(log));
                    EditView.ApplyForTests = (_, _, _, _) => throw new InvalidOperationException("Sequence contains no elements");
                    Assert.StartsWith("The edit failed (InvalidOperationException):", await edit.ApplyAsync());
                    Assert.Contains("Sequence contains no elements", File.ReadAllText(log));
                    int logged = errors.CaughtCount;
                    EditView.ApplyForTests = (_, _, _, _) => throw new InvalidDataException("a curve that doesn't read");
                    Assert.Equal("The edit failed: a curve that doesn't read", await edit.ApplyAsync());
                    Assert.Equal(logged, errors.CaughtCount);
                    Assert.DoesNotContain("a curve that doesn't read", File.ReadAllText(log));
                }
                finally { EditView.ApplyForTests = null; }
                w.Content = null;
            });
            Assert.Contains("a retry that fails", File.ReadAllText(log));

            // the screens' job: a catch-all's error is logged (a fault of the work, not a refusal), and noted for a picture run
            steps.Enqueue(_ => throw new NullReferenceException("a fault in working the marks out"));
            var failed = InstallWork.Retry(g, cache).Done.GetAwaiter().GetResult();
            Assert.Equal("NullReferenceException: a fault in working the marks out", failed.Error);
            Assert.Contains("while working out which edits each clip allows (caught, and said on the screen)", File.ReadAllText(log));
            // an error of the moment (memory short) that left clips out: the marks incomplete, not kept, and the error logged once, whole,
            // and counted for a picture run
            int caughtBeforeShort = errors.CaughtCount;
            Exception shortOfMemory;
            try { throw new OutOfMemoryException("short of memory for a clip's curves"); }
            catch (OutOfMemoryException ex) { shortOfMemory = ex; }
            steps.Enqueue(_ => EditMarks.From(allEdits.Skip(3).ToDictionary(), "not every clip was worked out (OutOfMemoryException: short of memory for a clip's curves)",
                transient: shortOfMemory));
            var leftOut = InstallWork.Retry(g, cache).Done.GetAwaiter().GetResult();
            Assert.NotNull(leftOut.Marks!.Incomplete);
            // the cache still holds the whole marks kept before
            Assert.Equal(allEdits.Count, new EditMarksCache(cache).Read(g, out _)!.Marks.Count);
            Assert.Equal(caughtBeforeShort + 1, errors.CaughtCount);
            string shortText = File.ReadAllText(log);
            Assert.Contains("while working out which edits each clip allows (some clips left out) (caught, and said on the screen)", shortText);
            Assert.Contains("System.OutOfMemoryException: short of memory for a clip's curves", shortText);
            Assert.Contains(nameof(RetryWorksTheMarksOutFromTheStartAndCaughtErrorsAreLogged), shortText);
            // whole but for clips the tool failed on: kept, those clips not told; the first errors logged with the clip's name, then a count
            var faulted = g.Clips.Take(7).ToList();
            var faults = faulted.Take(EditMarks.FaultsKept).Select(c => (c.Name, (Exception)new IndexOutOfRangeException($"a curve of {c.Name} out of range"))).ToList();
            steps.Enqueue(_ => EditMarks.From(allEdits.Where(k => faulted.All(c => c.Id != k.Key)).ToDictionary(), unknown: faulted.Select(c => c.Id), faults: faults));
            int caughtBefore = errors.CaughtCount;
            var withFaults = InstallWork.Retry(g, cache).Done.GetAwaiter().GetResult();
            Assert.Null(withFaults.SaveError);
            Assert.Equal(CacheOutcome.RebuiltOnRequest, withFaults.Outcome);
            Assert.True(errors.CaughtCount > caughtBefore, "the clips the tool failed on don't count toward a picture run's exit code");
            Assert.Equal(7, withFaults.Marks!.Unknown.Count);
            string text = File.ReadAllText(log);
            Assert.Contains($"while working out which edits {faulted[0].Name} allows (caught, and said on the screen)", text);
            Assert.Contains($"a curve of {faulted[4].Name} out of range", text);
            Assert.Contains("the tool failed on 7 clip(s) in all; the first 5 are logged above", text);
            lock (noted) Assert.Contains(noted, t => t.Contains(faulted[0].Name, StringComparison.Ordinal));
            Assert.Equal(7, new EditMarksCache(cache).Read(g, out _)!.Marks.Unknown.Count);
            // the same marks read from the cache by another opening of the same files: the clips not told said once this run, by name and
            // how many (their errors were logged when they were worked out), counting as a build does; not again on the next read
            InstallWork.ForgetToldForTests();
            using (var again = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!))
            {
                InstallWork.Open(again);
                caughtBefore = errors.CaughtCount;
                var read = InstallWork.Marks(again, cache).Done.GetAwaiter().GetResult();
                Assert.Equal(CacheOutcome.Reused, read.Outcome);
                Assert.Equal(7, read.Marks!.Unknown.Count);
                Assert.Equal(caughtBefore + 1, errors.CaughtCount);
                text = File.ReadAllText(log);
                Assert.Contains("the tool failed on 7 clip(s) when they were worked out, so which edits they allow isn't known: ", text);
                Assert.Contains(faulted.Select(c => c.Name).Order(StringComparer.Ordinal).First(), text);
                lock (noted) Assert.Contains(noted, t => t.Contains("the tool failed on 7 clip(s) when they were worked out", StringComparison.Ordinal));
                Task.WhenAll(InstallWork.Graph(again), InstallWork.Swaps(again), InstallWork.Mirror(again)).GetAwaiter().GetResult();
            }
            InstallWork.Open(g);
            caughtBefore = errors.CaughtCount;
            Assert.Equal(7, InstallWork.Marks(g, cache).Done.GetAwaiter().GetResult().Marks!.Unknown.Count);
            Assert.Equal(caughtBefore, errors.CaughtCount);
            // a retry passes the cache over: worked out again though it holds them, said as on request
            int built = builds;
            steps.Enqueue(_ => EditMarks.From(allEdits));
            Assert.Equal(CacheOutcome.RebuiltOnRequest, InstallWork.Retry(g, cache).Done.GetAwaiter().GetResult().Outcome);
            Assert.Equal(built + 1, builds);
            Assert.Empty(steps);

            // the graph that can't be read (a record of an odd shape): why is kept, logged, said with the marks, which aren't kept, and on
            // the Edit screen; worked out for real (no stand-in) without the graph
            InstallWork.BuildMarks = null;
            InstallWork.ReadGraph = _ => throw new InvalidDataException("a graph record of an odd shape");
            var noGraph = InstallWork.Retry(g, cache).Done.GetAwaiter().GetResult();
            Assert.Equal("the game's animation graph couldn't be read: InvalidDataException: a graph record of an odd shape", noGraph.Marks!.Incomplete);
            Assert.Equal("InvalidDataException: a graph record of an odd shape", InstallWork.WhyNoGraph(g));
            Assert.Contains("while reading the game's animation graph", File.ReadAllText(log));
            Assert.Contains("they may not hold next time: the game's animation graph couldn't be read: InvalidDataException", noGraph.SaveError);
            Wpf.RunAsync(async () =>
            {
                var edit = new EditView();
                var w = new Window { Content = edit };
                edit.SetInstall(g, true, new LibraryView());
                await edit.WaitForChoicesAsync();
                Assert.Contains("a graph record of an odd shape", edit.MarksText.Text);
                await edit.ChooseClipAsync(g.FindClip("fm_std_HG_V0_idle_cool_06")!);
                Assert.Contains("The animation graph couldn't be read (InvalidDataException: a graph record of an odd shape)", edit.OptionsText.Text);
                w.Content = null;
            });
            InstallWork.ReadGraph = null;
            Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g)).GetAwaiter().GetResult();
        }
        finally
        {
            InstallWork.ReadGraph = null;
            InstallWork.BuildMarks = null;
            Wpf.Run(() => Wpf.App.UseErrorsForTests(null));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>The swap source box's open list realises the rows in sight only (a clip may have hundreds of sources): its drop-down's
    /// content, laid out as opening it lays it out (here without the popup's own window, so nothing is shown), holds a few dozen rows of
    /// 600 at most, at the top and scrolled to the end.</summary>
    [Fact]
    public void TheSwapSourceListIsVirtualized()
    {
        Wpf.Run(() =>
        {
            Theme.Apply(ThemeChoice.Light);
            var edit = new EditView();
            var w = new Window { Content = edit };
            var size = new Size(1484, 861);
            edit.Measure(size);
            edit.Arrange(new Rect(size));
            edit.UpdateLayout();
            var box = edit.SourceBox;
            box.ItemsSource = ThemeScreenTests.Clips("mil_gen_m_ale_ros_std_V0_N_death_", 600, 100);
            box.ApplyTemplate();
            var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(box.Template.FindName("PART_Popup", box));
            var content = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            var drop = new Size(300, box.MaxDropDownHeight is var h && double.IsFinite(h) ? h : 400);
            content.Measure(drop);
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();
            int rows = ScreenTimingTests.Containers(content);
            Assert.True(rows is > 0 and < 60, $"{rows} rows realised of 600 in the open list");
            static ScrollViewer? Scroller(DependencyObject d)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    if (System.Windows.Media.VisualTreeHelper.GetChild(d, i) is var c && (c as ScrollViewer ?? Scroller(c)) is { } sv) return sv;
                return null;
            }
            var scroller = Scroller(content);
            Assert.NotNull(scroller);
            scroller!.ScrollToEnd();
            content.UpdateLayout();
            rows = ScreenTimingTests.Containers(content);
            Assert.True(rows is > 0 and < 60, $"{rows} rows realised of 600 at the open list's end");
            w.Content = null;
        });
    }

    /// <summary>Waits on the window's thread, letting it run, until <paramref name="done"/> holds (30 s at most).</summary>
    internal static async Task Wait(Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"{what}: not within 30 s");
            await Task.Delay(20);
        }
    }
}
