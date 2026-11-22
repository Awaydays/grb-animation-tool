using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Logs;

namespace GrbAnimTool.App.Tests;

/// <summary>The Log screen, built and laid out in no window shown: a folder's newest log followed as it is written (line by line and in
/// parts of lines), a newer log switched to, a log cut short or replaced read again; a log at the logger's 32 MB limit loaded off the
/// window's thread with its progress, its list realising only the rows in sight; a click on a line choosing its clip in the Library.
/// Every log here is synthetic, in the test's own folder, but for the real logs GRBANIM_CLIPLOGS names (read only).</summary>
[Collection("wpf")]
public sealed class LogScreenTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "log-screen-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    public const ulong Greetings = 0x000001A625068E05;

    /// <summary>A start line as the logger writes it.</summary>
    internal static string Start(int i, bool player = true, ulong clip = Greetings, string source = "clip") =>
        FormattableString.Invariant($"2026-11-18 19:{10 + i / 60000 % 50:D2}:{i / 1000 % 60:D2}.{i % 1000:D3},{i * 0.001:F3},{(player ? "player" : "other")},1891C424E60,00000000F0010660,0x{clip:X16},{source}");

    internal static string Head(string build, string started) => $"# GRB Clip Logger, {build} build, started {started}\n"
        + "# settings: the player only; repeat window 1500 ms; motion matching: a pick logged when its clip changes\n" + ClipLog.Header + "\n";

    static double Lay(FrameworkElement root, Size size)
    {
        var sw = Stopwatch.StartNew();
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        return sw.Elapsed.TotalMilliseconds;
    }

    static LogView NewLog()
    {
        var log = new LogView();
        // an automated run's screen: it follows nothing of its own (never the Documents folder); the reads are the test's
        log.UseSettings(null, automated: true);
        log.PollInterval = TimeSpan.FromHours(1);
        return log;
    }

    /// <summary>A folder that isn't there is said plainly and never made; once a log is written in it, it is followed as it grows, line by
    /// line and in parts of lines, each line taken once, whole and in order; a newer log is switched to; the log cut short, then replaced,
    /// is read again from its start; and the screen's own reads (its timer) take what is added.</summary>
    [Fact]
    public void TheNewestLogIsFollowedAsItIsWrittenAndANewerOneIsSwitchedTo()
    {
        string folder = Path.Combine(_dir, ClipLog.FolderName);
        Directory.CreateDirectory(_dir);
        Wpf.RunAsync(async () =>
        {
            var log = NewLog();
            try
            {
                log.Follow(folder);
                await log.WaitForReadAsync();
                Assert.StartsWith($"The folder {folder} isn't there.", log.SourceText.Text);
                Assert.Contains("the tool creates nothing there", log.SourceText.Text);
                await log.PollAsync();
                Assert.False(Directory.Exists(folder), "the folder followed was made");
                Directory.CreateDirectory(folder);
                await log.PollAsync();
                Assert.StartsWith("No clip log in", log.SourceText.Text);

                // the first log, written as the logger writes it: its file kept open and shared, a few bytes at a time
                string first = Path.Combine(folder, "clips_DX11_2026-11-18_191002.csv");
                var text = new StringBuilder(Head("DX11", "2026-11-18 19:10:07.379"));
                for (int i = 0; i < 150; i++)
                    text.Append(i % 40 == 9 ? $"# mark {i / 40 + 1} at {i}.000 s: written\n" : Start(i, player: i % 4 != 3, clip: 0x000001A625000000UL + (ulong)i) + (i % 5 == 0 ? "\r\n" : "\n"));
                var bytes = Encoding.UTF8.GetBytes(text.ToString());
                var rnd = new Random(11);
                using (var w = new FileStream(first, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    int at = 0, step = 0;
                    while (at < bytes.Length)
                    {
                        // line by line at first, then in parts of lines
                        int n = step++ < 6 ? Array.IndexOf(bytes, (byte)'\n', at) + 1 - at : Math.Min(bytes.Length - at, rnd.Next(1, 70));
                        w.Write(bytes, at, n);
                        w.Flush();
                        at += n;
                        await log.PollAsync();
                        // the lines whole so far, and only those: each once, in order
                        var whole = Encoding.UTF8.GetString(bytes, 0, Array.LastIndexOf(bytes, (byte)'\n', at - 1) + 1)
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
                        var starts = whole.Where(l => !l.StartsWith('#') && l != ClipLog.Header).ToList();
                        var notes = whole.Where(l => l.StartsWith('#')).Select(l => l[2..]).ToList();
                        Assert.Equal(starts.Select(s => ulong.Parse(s.Split(',')[5][2..], System.Globalization.NumberStyles.HexNumber)), log.Lines.Select(l => l.ClipId));
                        Assert.All(log.Lines, l => Assert.Equal(ClipLogKind.Start, l.Kind));
                        Assert.Equal(notes, log.NoteLines.Select(l => l.Text));
                        Assert.Equal(log.Lines, log.ShownLines);
                    }
                }
                Assert.Equal(146, log.Lines.Count);
                Assert.Equal(first, log.Followed);
                Assert.Equal("Following clips_DX11_2026-11-18_191002.csv, the newest log in the folder, as the game writes it.", log.SourceText.Text);
                // the player only
                log.ShowBox.SelectedIndex = 1;
                Assert.Equal(log.Lines.Where(l => l.Player), log.ShownLines);
                Assert.Contains(log.Lines, l => !l.Player);
                log.ShowBox.SelectedIndex = 0;
                Assert.Equal(log.Lines, log.ShownLines);

                // a newer log starts: switched to, its lines in place of the first's
                string second = Path.Combine(folder, "clips_Vulkan_2026-11-18_192150.csv");
                string head2 = Head("Vulkan", "2026-11-18 19:21:50.437");
                File.WriteAllText(second, head2 + string.Concat(Enumerable.Range(0, 3).Select(i => Start(1000 + i) + "\n")));
                await log.PollAsync();
                Assert.Equal(second, log.Followed);
                Assert.Equal(new[] { 1.0, 1.001, 1.002 }, log.Lines.Select(l => Math.Round(l.Seconds, 3)));
                Assert.StartsWith("Following clips_Vulkan_2026-11-18_192150.csv", log.SourceText.Text);
                // cut short: read again from its start
                File.WriteAllText(second, head2 + Start(2000) + "\n");
                await log.PollAsync();
                Assert.Equal(2.0, Assert.Single(log.Lines).Seconds, 6);
                Assert.Contains("replaced or cut short, so it was read again from its start", log.SourceText.Text);
                // replaced by another file moved over it, longer
                File.WriteAllText(second + ".part", Head("Vulkan", "2026-11-18 19:40:00.000") + string.Concat(Enumerable.Range(0, 5).Select(i => Start(3000 + i) + "\n")));
                File.Move(second + ".part", second, overwrite: true);
                await log.PollAsync();
                Assert.Equal(new[] { 3.0, 3.001, 3.002, 3.003, 3.004 }, log.Lines.Select(l => Math.Round(l.Seconds, 3)));
                Assert.Equal(2, log.NoteLines.Count);
                // the screen's own reads take what is added, with no call of the test's
                log.PollInterval = TimeSpan.FromMilliseconds(40);
                File.AppendAllText(second, Start(4000) + "\n" + Start(4001));
                var sw = Stopwatch.StartNew();
                while (log.Lines.Count < 6 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                Assert.Equal(6, log.Lines.Count);
                await Task.Delay(200);
                Assert.Equal(6, log.Lines.Count);
                File.AppendAllText(second, "\n");
                sw.Restart();
                while (log.Lines.Count < 7 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                Assert.Equal(new[] { 4.0, 4.001 }, log.Lines.Skip(5).Select(l => Math.Round(l.Seconds, 3)));
                // the screen hidden (another tab shown): no read while it is (one the timer started just before ends first), then what was
                // added taken at once when it is shown again
                log.Hidden();
                await log.WaitForReadAsync();
                File.AppendAllText(second, Start(5000) + "\n");
                await Task.Delay(400);
                Assert.Equal(7, log.Lines.Count);
                log.Shown();
                sw.Restart();
                while (log.Lines.Count < 8 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                Assert.Equal(5.0, log.Lines[^1].Seconds, 6);
                // nothing was made in the folder followed
                Assert.Equal(new[] { first, second }, Directory.GetFileSystemEntries(folder).Order(StringComparer.OrdinalIgnoreCase));
            }
            finally { log.Stop(); }
        });
    }

    /// <summary>A read that ends before its await looks at it (done here on the window's thread, as a read that ends at once can): it is
    /// not taken for one still running, so the waits end and the screen reads on after it, a log opened and a folder followed alike.</summary>
    [Fact]
    public void AReadEndedBeforeItsAwaitLeavesTheScreenReadingOn()
    {
        string folder = Path.Combine(_dir, ClipLog.FolderName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "clips_DX11_2026-11-18_191002.csv");
        File.WriteAllText(path, Head("DX11", "2026-11-18 19:10:07.379") + string.Concat(Enumerable.Range(0, 3).Select(i => Start(i) + "\n")));
        Wpf.RunAsync(async () =>
        {
            var log = NewLog();
            log.ReadInlineForTests = true;
            try
            {
                log.OpenFile(path);
                Assert.False(log.Reading, "a read that ended is still taken for one running");
                await log.WaitForReadAsync();
                Assert.Equal(3, log.Lines.Count);
                File.AppendAllText(path, Start(3) + "\n");
                await log.PollAsync();
                Assert.False(log.Reading);
                Assert.Equal(4, log.Lines.Count);
                // the folder followed in its place: its newest log read at once, and read on as it grows
                log.Follow(folder);
                Assert.False(log.Reading);
                await log.WaitForReadAsync();
                Assert.Equal(path, log.Followed);
                Assert.Equal(4, log.Lines.Count);
                File.AppendAllText(path, Start(4) + "\n");
                await log.PollAsync();
                Assert.Equal(5, log.Lines.Count);
            }
            finally { log.Stop(); }
        });
    }

    /// <summary>A followed log's list, narrower than its columns and scrolled sideways: the lines that come take it to its last line and
    /// leave it where it was scrolled sideways.</summary>
    [Fact]
    public void LinesThatComeKeepTheListWhereItWasScrolledSideways()
    {
        string folder = Path.Combine(_dir, ClipLog.FolderName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "clips_DX11_2026-11-18_191002.csv");
        File.WriteAllText(path, Head("DX11", "2026-11-18 19:10:07.379") + string.Concat(Enumerable.Range(0, 100).Select(i => Start(i) + "\n")));
        Wpf.RunAsync(async () =>
        {
            var log = NewLog();
            var win = new Window { Content = log };
            var size = new Size(600, 600);
            async Task Settle()
            {
                for (int k = 0; k < 2; k++)
                {
                    Lay(log, size);
                    await log.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                }
                Lay(log, size);
            }
            try
            {
                Lay(log, size);
                log.Follow(folder);
                await log.WaitForReadAsync();
                await Settle();
                var sv = All<ScrollViewer>(log.List).First();
                Assert.True(sv.ScrollableWidth > 150, $"the list scrolls sideways by {sv.ScrollableWidth:F0} only");
                sv.ScrollToHorizontalOffset(150);
                await Settle();
                Assert.Equal(150, sv.HorizontalOffset);
                File.AppendAllText(path, string.Concat(Enumerable.Range(100, 20).Select(i => Start(i) + "\n")));
                await log.PollAsync();
                await Settle();
                Assert.Equal(120, log.Lines.Count);
                Assert.Equal(150, sv.HorizontalOffset);
                Assert.Contains(All<ListViewItem>(log.List), i => i.DataContext == log.ShownLines[^1]);
            }
            finally
            {
                log.Stop();
                win.Content = null;
            }
        });
    }

    /// <summary>Follow unticked while a read is under way, the folder's first read and a read of the growing log: every line that read
    /// took comes, once and in order, and the first read ends with its log shown.</summary>
    [Fact]
    public void UntickingFollowDuringAReadLosesNoLine()
    {
        string folder = Path.Combine(_dir, ClipLog.FolderName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "clips_DX11_2026-11-18_191002.csv");
        static string Lines(int from, int n) => string.Concat(Enumerable.Range(from, n).Select(i => Start(i, clip: 0x000001A625000000UL + (ulong)i) + "\n"));
        File.WriteAllText(path, Head("DX11", "2026-11-18 19:10:07.379") + Lines(0, 100_000));
        Wpf.RunAsync(async () =>
        {
            var log = NewLog();
            void Untick()
            {
                log.FollowBox.IsChecked = false;
                log.FollowBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            try
            {
                // the folder's first read under way
                log.Follow(folder);
                Untick();
                await log.WaitForReadAsync();
                Assert.Equal(100_000, log.Lines.Count);
                Assert.Null(log.Followed);
                Assert.StartsWith("Log: ", log.SourceText.Text);
                // followed again, then a read of what the log grew by under way
                log.Follow(folder);
                await log.WaitForReadAsync();
                File.AppendAllText(path, Lines(100_000, 60_000));
                var read = log.PollAsync();
                Untick();
                await read;
                await log.PollAsync();
                Assert.Equal(160_000, log.Lines.Count);
                Assert.Equal(Enumerable.Range(0, 160_000).Select(i => 0x000001A625000000UL + (ulong)i), log.Lines.Select(l => l.ClipId));
            }
            finally { log.Stop(); }
        });
    }

    /// <summary>A log at the logger's limit (300,000 lines, about 32 MB) opens with its progress shown, read off the window's thread, which
    /// stays free for input; its list realises only the rows in sight, at its start, at its end and with the player filter.</summary>
    [Fact]
    public void ALogAtTheLoggersLimitLoadsWithItsProgressAndTheListRealisesOnlyTheRowsInSight()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "clips_DX11_2026-11-18_200000.csv");
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            w.Write(Head("DX11", "2026-11-18 20:00:00.000"));
            for (int i = 0; i < 300_000; i++)
            {
                if (i % 1000 == 500) w.WriteLine($"# mark {i / 1000} at {i * 0.001:F3} s: {i} starts read");
                w.WriteLine(Start(i, player: i % 3 != 2, clip: 0x000001A625000000UL + (ulong)(i % 7000), source: i % 4 == 0 ? "motion" : "clip"));
            }
            w.WriteLine("# the file reached its size limit - from here on starts are counted, not written");
        }
        long bytes = new FileInfo(path).Length;
        Wpf.RunAsync(async () =>
        {
            Theme.Apply(ThemeChoice.Dark);
            var log = NewLog();
            var win = new Window { Content = log };
            var size = new Size(1484, 861 - 40);
            Lay(log, size);
            var progress = new List<double>();
            log.LoadBar.ValueChanged += (_, e) => progress.Add(e.NewValue);
            var wait = new ScreenTimingTests.Latency(log.Dispatcher);
            try
            {
                wait.Take();
                var sw = Stopwatch.StartNew();
                log.OpenFile(path);
                await log.WaitForReadAsync();
                double loaded = sw.Elapsed.TotalMilliseconds, worst = wait.Take();
                double first = Lay(log, size);
                int rows = ScreenTimingTests.Containers(log.List);
                output.WriteLine(FormattableString.Invariant($"300,000-line log ({bytes:N0} bytes): read and shown in {loaded:F0} ms, the window thread's longest wait {worst:F0} ms; first layout {first:F0} ms, {rows} rows realised; {progress.Count} progress steps"));
                Assert.Equal(300_000, log.Lines.Count);
                Assert.Equal(300_000, log.ShownLines.Count);
                Assert.Equal(300 + 3, log.NoteLines.Count);
                Assert.True(log.Counts.ReachedLimit);
                Assert.Equal(Visibility.Visible, log.LimitText.Visibility);
                Assert.True(progress.Count >= 3 && progress.Any(p => p > 0 && p < 1), $"the progress went {string.Join(", ", progress.Select(p => p.ToString("F2")))}");
                Assert.Equal(Visibility.Collapsed, log.LoadPanel.Visibility);
                Assert.True(rows is >= 5 and < 200, $"{rows} rows realised at the start");
                Assert.True(worst < 1000, $"the window thread was held {worst:F0} ms");
                log.List.ScrollIntoView(log.ShownLines[^1]);
                Lay(log, size);
                rows = ScreenTimingTests.Containers(log.List);
                Assert.True(rows is >= 5 and < 200, $"{rows} rows realised at the end");
                sw.Restart();
                log.ShowBox.SelectedIndex = 1;
                double filtered = sw.Elapsed.TotalMilliseconds;
                Lay(log, size);
                rows = ScreenTimingTests.Containers(log.List);
                output.WriteLine(FormattableString.Invariant($"the player filter: {filtered:F0} ms, {log.ShownLines.Count:N0} lines, {rows} rows realised; the window thread's longest wait {wait.Take():F0} ms"));
                Assert.Equal(200_000, log.ShownLines.Count);
                Assert.True(rows is >= 5 and < 200, $"{rows} rows realised with the filter");
                // the log grows by a line: added, not read again whole
                long was = new FileInfo(path).Length;
                File.AppendAllText(path, Start(300_001) + "\n");
                sw.Restart();
                await log.PollAsync();
                output.WriteLine(FormattableString.Invariant($"a line added to the 300,000-line log: taken in {sw.Elapsed.TotalMilliseconds:F0} ms"));
                Assert.Equal(300_001, log.Lines.Count);
                Assert.Equal(200_001, log.ShownLines.Count);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"{sw.Elapsed.TotalMilliseconds:F0} ms for one line");
                Assert.True(was > 25_000_000, $"{was:N0} bytes");
            }
            finally
            {
                await wait.Stop();
                log.Stop();
                win.Content = null;
            }
        });
    }

    // ---- with the game's files ----

    static GameInstall InstallOf(MainWindow w) => (GameInstall)typeof(LibraryView).GetField("_install", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.LibraryPage)!;

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

    /// <summary>A left click on a row, as the mouse raises it: the tunnelling mouse-down, then the bubbling mouse-up.</summary>
    static void Click(ListViewItem item)
    {
        Press(item);
        Release(item);
    }

    static void Press(ListViewItem item) =>
        item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });

    static void Release(ListViewItem item) =>
        item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });

    /// <summary>A key pressed on <paramref name="target"/>, as the keyboard raises it (its preview first).</summary>
    static void PressKey(UIElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new NoSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    /// <summary>The source a key event is said to come from (a window never shown has none of its own).</summary>
    sealed class NoSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    /// <summary>A test that needs the game data and real clip logs (GRBANIM_CLIPLOGS); it skips cleanly without them.</summary>
    public sealed class ClipLogsFactAttribute : FactAttribute
    {
        public ClipLogsFactAttribute()
        {
            if (GameDataFactAttribute.Forge is null || GameDataFactAttribute.Game is null)
                Skip = "game data not configured: set GRBANIM_FORGE (a vanilla DataPC_patch_01.forge) and GRBANIM_GAME (the game folder)";
            else if (RealLogs().Count == 0) Skip = "no real clip logs named: set GRBANIM_CLIPLOGS to clip log files or folders (separated by ';')";
        }
    }

    static List<string> RealLogs() => (Environment.GetEnvironmentVariable("GRBANIM_CLIPLOGS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, ClipLogFolder.Pattern) : File.Exists(p) ? new[] { p } : Array.Empty<string>()).ToList();

    /// <summary>On the game's files, a log followed while lines come: a line pressed is the one opened when the button is let go on it,
    /// though a read lands meanwhile (the list holds still, and goes to its end once the button is let go); a press on one line let go on
    /// another opens nothing.</summary>
    [GameDataFact]
    public void ALinePressedIsTheOneOpenedThoughLinesComeWhileItIsPressed()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string folder = Path.Combine(_dir, ClipLog.FolderName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "clips_DX11_2026-11-18_191002.csv");
        var ids = g.Clips.Where(c => c.Record is not null).DistinctBy(c => c.Id).Take(4).Select(c => c.Id).ToArray();
        File.WriteAllText(path, Head("DX11", "2026-11-18 19:10:07.379") + string.Concat(Enumerable.Range(0, 200).Select(i => Start(i, clip: ids[i % 4]) + "\n")));
        Wpf.RunAsync(async () =>
        {
            Theme.Apply(ThemeChoice.Dark);
            var log = NewLog();
            log.SetInstall(g);
            var win = new Window { Content = log };
            var size = new Size(1484, 821);
            try
            {
                var opened = new List<ClipEntry>();
                log.ClipOpened += opened.Add;
                // the screen laid out, as one in sight is, before its log comes
                Lay(log, size);
                log.Follow(folder);
                await log.WaitForReadAsync();
                // (a scroll asked for before the list's rows were made runs once they are: a layout, the queued scroll, a layout)
                async Task Settle()
                {
                    for (int k = 0; k < 2; k++)
                    {
                        Lay(log, size);
                        await log.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    Lay(log, size);
                }
                await Settle();
                // at its end, as a log followed is
                Assert.Contains(All<ListViewItem>(log.List), i => i.DataContext == log.ShownLines[^1]);
                var item = All<ListViewItem>(log.List).Single(i => i.DataContext == log.ShownLines[^3]);
                var line = (ClipLogLine)item.DataContext;
                Assert.True(line.Opens);
                Press(item);
                // a read lands while the line is pressed, its lines of other clips: the list holds still under the pointer
                File.AppendAllText(path, string.Concat(Enumerable.Range(200, 30).Select(i => Start(i, clip: ids[i % 4]) + "\n")));
                await log.PollAsync();
                await Settle();
                Assert.Equal(230, log.Lines.Count);
                Assert.Same(line, item.DataContext);
                Release(item);
                Assert.Same(line.Clip, Assert.Single(opened));
                // let go, the list goes to its end
                await Settle();
                Assert.Contains(All<ListViewItem>(log.List), i => i.DataContext == log.ShownLines[^1]);
                // pressed on one line, let go on another: nothing opened
                var rows = All<ListViewItem>(log.List).Where(i => i.DataContext is ClipLogLine { Opens: true }).ToList();
                Press(rows[0]);
                Release(rows[1]);
                Assert.Single(opened);
            }
            finally
            {
                log.Stop();
                win.Content = null;
            }
        });
    }

    /// <summary>On the game's files: a start the logger gives as from a place in the game's code it doesn't name ("other GRB.exe+…",
    /// "other GRB_vulkan.exe+…") is a start like any other: its clip named, its source "other" with the place on hover, and a click on it
    /// opens its clip.</summary>
    [GameDataFact]
    public void AStartFromAnUnnamedPlaceInTheGamesCodeIsNamedAndOpens()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "clips_DX11_2026-11-18_191002.csv");
        File.WriteAllText(path, Head("DX11", "2026-11-18 19:10:07.379") + Start(1, source: "other GRB.exe+41C2D0") + "\n"
            + Start(2, player: false, source: "other GRB_vulkan.exe+3679DD") + "\n");
        Wpf.RunAsync(async () =>
        {
            var log = NewLog();
            log.SetInstall(g);
            var win = new Window { Content = log };
            var size = new Size(1484, 821);
            try
            {
                var opened = new List<ClipEntry>();
                log.ClipOpened += opened.Add;
                Lay(log, size);
                log.OpenFile(path);
                await log.WaitForReadAsync();
                Lay(log, size);
                Assert.Equal(2, log.Lines.Count);
                Assert.Equal(0, log.Counts.Unreadable);
                foreach (var (l, at) in log.Lines.Zip(new[] { "GRB.exe+41C2D0", "GRB_vulkan.exe+3679DD" }))
                {
                    Assert.Equal((ClipLogKind.Start, "mca_emote_Hello", "other", true), (l.Kind, l.ClipText, l.SourceText, l.Opens));
                    Assert.Contains($"(at {at} in the game's code)", l.Tip);
                }
                var item = All<ListViewItem>(log.List).Single(i => i.DataContext == log.Lines[0]);
                Assert.Contains(All<TextBlock>(item), t => t.Text == "mca_emote_Hello");
                Click(item);
                Assert.Same(g.FindClip(Greetings), Assert.Single(opened));
            }
            finally
            {
                log.Stop();
                win.Content = null;
            }
        });
    }

    /// <summary>On the game's files: the lines of a log shown are looked up again when another install is opened, each named by the new
    /// install's clips (an ID only the new one holds named, the same ID's clip the new install's own).</summary>
    [GameDataFact]
    public void AnotherInstallOpenedLooksTheLinesUpInIt()
    {
        using var g1 = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        using var g2 = GameInstall.Open(GameDataFactAttribute.Game!);
        // an ID only the game folder's own forge holds (a clip added there), when it holds one
        ulong? onlyIn2 = g2.Clips.FirstOrDefault(c => g1.FindClip(c.Id) is null)?.Id;
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "clips_DX11_2026-11-18_191002.csv");
        File.WriteAllText(path, Head("DX11", "2026-11-18 19:10:07.379") + Start(1) + "\n" + (onlyIn2 is { } x ? Start(2, clip: x) + "\n" : ""));
        Wpf.RunAsync(async () =>
        {
            var log = NewLog();
            try
            {
                log.SetInstall(g1);
                log.OpenFile(path);
                await log.WaitForReadAsync();
                Assert.Same(g1.FindClip(Greetings), log.Lines[0].Clip);
                if (onlyIn2 is not null) Assert.Equal(ClipLookup.NotFound, log.Lines[1].Lookup);
                log.SetInstall(g2);
                var sw = Stopwatch.StartNew();
                while (!ReferenceEquals(log.Lines[0].Clip, g2.FindClip(Greetings)) && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
                Assert.Same(g2.FindClip(Greetings), log.Lines[0].Clip);
                if (onlyIn2 is { } id)
                {
                    Assert.Same(g2.FindClip(id), log.Lines[1].Clip);
                    Assert.Equal((2, 0), (log.Counts.Found, log.Counts.NotFound));
                }
                output.WriteLine(onlyIn2 is null ? "the two installs hold the same clips" : $"0x{onlyIn2:X16} named only by the second install: {g2.FindClip(onlyIn2.Value)!.Name}");
            }
            finally { log.Stop(); }
        });
    }

    /// <summary>The folder the settings name: an automated run's screen has it in place without following it; a folder chosen is
    /// remembered in those settings and followed; a person's run follows the folder its settings name once the screen is shown. Every
    /// path is the test's own.</summary>
    [Fact]
    public void TheFolderIsRememberedInTheSettingsNamedAndFollowedWhenChosen()
    {
        string settings = Path.Combine(_dir, "settings.json");
        string a = Path.Combine(_dir, "a"), b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(_dir);
        new ViewerSettings { LogFolder = a }.Save(settings);
        Wpf.RunAsync(async () =>
        {
            var log = new LogView();
            try
            {
                log.PollInterval = TimeSpan.FromHours(1);
                log.UseSettings(settings, automated: true);
                log.Shown();
                Assert.False(log.FollowBox.IsChecked);
                Assert.Equal($"Folder: {a}", log.FolderText.Text);
                Assert.Null(log.Followed);
                Assert.StartsWith("No log open", log.SourceText.Text);
                log.UseFolder(b);
                await log.WaitForReadAsync();
                Assert.Equal(b, ViewerSettings.Load(settings).LogFolder);
                Assert.True(log.FollowBox.IsChecked);
                Assert.StartsWith($"The folder {b} isn't there.", log.SourceText.Text);
                Assert.False(Directory.Exists(b));
                log.Stop();

                var person = new LogView();
                person.PollInterval = TimeSpan.FromHours(1);
                person.UseSettings(settings, automated: false);
                Assert.True(person.FollowBox.IsChecked);
                Assert.StartsWith("No log open", person.SourceText.Text);
                person.Shown();
                await person.WaitForReadAsync();
                Assert.StartsWith($"The folder {b} isn't there.", person.SourceText.Text);
                person.Stop();

                // a settings file that isn't settings: kept aside, said with both files, and the folder remembered in the defaults
                string badDir = Path.Combine(_dir, "bad");
                Directory.CreateDirectory(badDir);
                string bad = Path.Combine(badDir, "settings.json");
                File.WriteAllText(bad, "{ \"GameFolder\": \"g\", ");
                var third = new LogView();
                third.PollInterval = TimeSpan.FromHours(1);
                third.UseSettings(bad, automated: true);
                third.UseFolder(a);
                await third.WaitForReadAsync();
                var aside = Assert.Single(Directory.GetFiles(badDir, ViewerSettings.UnreadableName + "*.json"));
                Assert.Equal("{ \"GameFolder\": \"g\", ", File.ReadAllText(aside));
                Assert.Equal(a, ViewerSettings.Load(bad).LogFolder);
                Assert.Contains(aside, third.ErrorText.Text);
                Assert.Equal(Visibility.Visible, third.ErrorBox.Visibility);
                Assert.True(third.FollowBox.IsChecked);
                third.Stop();
                // one held by another program: not written over, said
                using (File.Open(bad, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var fourth = new LogView();
                    fourth.PollInterval = TimeSpan.FromHours(1);
                    fourth.UseSettings(null, automated: true);
                    fourth.UseSettings(bad, automated: true);
                    fourth.UseFolder(b);
                    await fourth.WaitForReadAsync();
                    Assert.Contains("is in use or can't be opened now", fourth.ErrorText.Text);
                    fourth.Stop();
                }
                Assert.Equal(a, ViewerSettings.Load(bad).LogFolder);

                // a folder of spaces only (a hand edit): none, and following it is refused plainly
                string spaces = Path.Combine(_dir, "spaces.json");
                new ViewerSettings { LogFolder = "   " }.Save(spaces);
                var fifth = new LogView();
                fifth.PollInterval = TimeSpan.FromHours(1);
                fifth.UseSettings(spaces, automated: true);
                fifth.Shown();
                Assert.StartsWith("Folder: none", fifth.FolderText.Text);
                fifth.Follow("   ");
                Assert.Contains("can't be followed", fifth.ErrorText.Text);
                Assert.Null(fifth.Followed);
                fifth.Stop();
            }
            finally { log.Stop(); }
        });
    }

    /// <summary>A run's <c>--log-folder</c> that isn't there, or holds no log, fails the run with why, as a <c>--log-file</c> that can't be
    /// read does; a folder with a log doesn't.</summary>
    [Fact]
    public void ALogFolderNamedThatIsntThereOrHoldsNoLogFailsTheRun()
    {
        string missing = Path.Combine(_dir, "not-there"), empty = Path.Combine(_dir, "empty"), full = Path.Combine(_dir, "full");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "clips_DX11_2026-11-18_191002.csv"), Head("DX11", "2026-11-18 19:10:07.379") + Start(0) + "\n");
        Wpf.RunAsync(async () =>
        {
            foreach (var (folder, expected) in new[] { (missing, $"the log folder {missing} isn't there"), (empty, $"the log folder {empty} holds no clip log"), (full, null) })
            {
                var log = NewLog();
                try { Assert.Equal(expected, await log.StartForShotsAsync(ViewerArgs.Parse(["--log-folder", folder]))); }
                finally { log.Stop(); }
            }
            Assert.False(Directory.Exists(missing));
        });
    }

    /// <summary>On the game's files: a log's Greetings start is mca_emote_Hello, its readable name and who from the clip index; a click
    /// on its line chooses the clip in the Library and shows the Library, as a click on its row there would (the clip read and shown); a
    /// click on an unknown ID's line, or a line that doesn't read, does nothing.</summary>
    [GameDataFact]
    public void AClickOnALineChoosesItsClipInTheLibrary()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "clips_DX11_2026-11-21_160529.csv");
        File.WriteAllText(path, Head("DX11", "2026-11-21 16:05:34.430") + Start(1, clip: 0x000001A625001091) + "\n" + Start(2, clip: 0x00000ABCDEF01234) + "\n"
            + "2026-11-21 16:06:09.015,not a number,player,1BE1D1CE100,00000000F0010716,0x000001A625068E05,clip\n" + Start(3) + "\n" + Start(4, player: false, clip: 0x000001A625001091, source: "layer") + "\n");
        Wpf.RunAsync(async () =>
        {
            Theme.Apply(ThemeChoice.Light);
            var args = ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--forge", GameDataFactAttribute.Forge!, "--index-cache", Path.Combine(_dir, "cache"),
                "--package-store", Path.Combine(_dir, "kept"), "--settings", Path.Combine(_dir, "settings.json"),
                "--shot", Path.Combine(_dir, "unused.png") + "||||log", "--log-file", path]);
            var w = new MainWindow(args);
            try
            {
                var root = (FrameworkElement)w.Content;
                var size = new Size(1484, 861);
                Lay(root, size);
                Assert.Null(await w.LibraryPage.LoadForShotsAsync(args));
                var g = InstallOf(w);
                Assert.Null(await w.LogPage.StartForShotsAsync(args));
                w.Tabs.SelectedIndex = 4;
                Lay(root, size);
                var hello = g.FindClip(Greetings)!;
                Assert.Equal("mca_emote_Hello", hello.Name);
                var lines = w.LogPage.Lines;
                Assert.Equal(5, lines.Count);
                Assert.Equal(new[] { ClipLookup.Found, ClipLookup.NotFound, ClipLookup.NotLooked, ClipLookup.Found, ClipLookup.Found }, lines.Select(l => l.Lookup));
                Assert.Equal(ClipLogKind.Unreadable, lines[2].Kind);
                Assert.Equal(("unknown clip", "0x00000ABCDEF01234"), (lines[1].ClipText, lines[1].IdText));
                // the clip index's readable name and who in the line's cells, as the Library's list shows them
                var index = (await InstallWork.Index(g))!;
                var item = All<ListViewItem>(w.LogPage.List).Single(i => i.DataContext == lines[3]);
                var texts = All<TextBlock>(item).Select(t => t.Text).ToList();
                Assert.Contains("mca_emote_Hello", texts);
                Assert.Contains(ClipIndexView.Cell(index, Greetings, ClipSort.Who), texts);
                if (index.Find(Greetings)?.ReadableName is { } readable) Assert.Contains(readable, texts);
                output.WriteLine($"the Greetings line's cells: {string.Join(" | ", texts.Where(t => t.Length > 0))}");
                Assert.Contains("1 of an unknown clip (1 ID)", w.LogPage.CountText.Text);
                Assert.Contains("1 unreadable line", w.LogPage.CountText.Text);

                // an unknown ID's line and a line that doesn't read: nothing
                foreach (var l in new[] { lines[1], lines[2] })
                {
                    Click(All<ListViewItem>(w.LogPage.List).Single(i => i.DataContext == l));
                    Assert.Equal(4, w.Tabs.SelectedIndex);
                }
                Assert.NotEqual(hello, w.LibraryPage.CurrentClip);
                // the Greetings line: the Library shown, the clip chosen there as a click on its row (hidden by a search: cleared)
                w.LibraryPage.ClipPanel.SearchBox.Text = "death";
                w.LibraryPage.ClipPanel.Apply();
                Assert.DoesNotContain(hello, w.LibraryPage.ClipPanel.Rows);
                Click(item);
                Assert.Equal(0, w.Tabs.SelectedIndex);
                Assert.Same(hello, w.LibraryPage.ClipPanel.SelectedClip);
                Assert.Same(hello, w.LibraryPage.CurrentClip);
                Assert.Equal("", w.LibraryPage.ClipPanel.SearchBox.Text);
                var sw = Stopwatch.StartNew();
                while (!w.LibraryPage.ClipPlays && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
                Assert.True(w.LibraryPage.ClipPlays, "the clip chosen from the log isn't read and shown");
                Assert.Equal("mca_emote_Hello", w.LibraryPage.InfoName.Text);
                // the other's line of another clip, by Enter on it: the same
                w.Tabs.SelectedIndex = 4;
                Lay(root, size);
                w.LogPage.List.SelectedItem = lines[4];
                PressKey(w.LogPage.List, Key.Enter);
                Assert.Equal(0, w.Tabs.SelectedIndex);
                Assert.Equal(0x000001A625001091UL, w.LibraryPage.CurrentClip!.Id);
            }
            finally { w.LogPage.Stop(); }
        });
    }

    /// <summary>The real logs GRBANIM_CLIPLOGS names, on the screen through the game's files: every line reads, the counts are the log's,
    /// each Greetings start is mca_emote_Hello, and how long each takes to show is written out.</summary>
    [ClipLogsFact]
    public void TheRealLogsShowTheirClipsNamed()
    {
        var logs = RealLogs();
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        Wpf.RunAsync(async () =>
        {
            var log = NewLog();
            var win = new Window { Content = log };
            var size = new Size(1484, 821);
            try
            {
                // a log shown before the install is open: no clip named, none guessed; then every line looked up once it is
                log.OpenFile(logs[0]);
                await log.WaitForReadAsync();
                Assert.All(log.Lines.Where(l => l.Kind == ClipLogKind.Start), l => Assert.Equal((ClipLookup.NotLooked, "(no game folder open)"), (l.Lookup, l.ClipText)));
                Assert.Contains("open the game folder in the Library", log.CountText.Text);
                int lines = log.Counts.Lines;
                log.SetInstall(g);
                var sw0 = Stopwatch.StartNew();
                while (log.Lines.Any(l => l.Kind == ClipLogKind.Start && l.Lookup == ClipLookup.NotLooked) && sw0.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
                Assert.Equal(log.Counts.Starts, log.Counts.Found + log.Counts.NotFound);
                Assert.Equal(lines, log.Counts.Lines);
                Assert.All(log.Lines.Where(l => l.ClipId == Greetings), l => Assert.Equal("mca_emote_Hello", l.ClipText));
                int greetings = 0;
                foreach (var path in logs)
                {
                    var sw = Stopwatch.StartNew();
                    log.OpenFile(path);
                    await log.WaitForReadAsync();
                    double ms = sw.Elapsed.TotalMilliseconds;
                    double lay = Lay(log, size);
                    var c = log.Counts;
                    output.WriteLine(FormattableString.Invariant($"{Path.GetFileName(path)} ({new FileInfo(path).Length:N0} bytes): shown in {ms:F0} ms (first layout {lay:F0} ms); {c.Lines} lines, {c.Starts} starts, {c.Found} named, {c.NotFound} unknown ({c.UnknownIds.Count} IDs), {c.Notes} notes, {c.Unreadable} unreadable; {log.CountText.Text}"));
                    Assert.Equal(0, c.Unreadable);
                    Assert.Equal(c.Starts, c.Found + c.NotFound);
                    var hello = log.Lines.Where(l => l.ClipId == Greetings).ToList();
                    greetings += hello.Count;
                    Assert.All(hello, l => Assert.Equal("mca_emote_Hello", l.ClipText));
                    Assert.True(ScreenTimingTests.Containers(log.List) < 200);
                }
                // the logs named hold a Greetings start between them (each needn't)
                Assert.True(greetings > 0, "no Greetings start in the logs named");
            }
            finally
            {
                log.Stop();
                win.Content = null;
            }
        });
    }
}
