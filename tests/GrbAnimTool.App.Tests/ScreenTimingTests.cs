using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>A timing probe of the screens on real data, run only when GRBANIM_TIMING_LOG names a log file: the window built (never
/// shown) at the size GRBANIM_TIMING_SIZE gives (default 1484x861), the install of GRBANIM_GAME opened (with GRBANIM_TIMING_FORGE's patch
/// forge when set) with its caches in GRBANIM_TIMING_CACHE, then each screen laid out as a click on its tab lays it out and a clip chosen,
/// with the time of each step, the list rows realised and the window thread's longest wait in each step written to the log.</summary>
[Collection("wpf")]
public class ScreenTimingTests
{
    static StreamWriter? _log;
    static readonly Stopwatch Since = Stopwatch.StartNew();

    static void Log(string s)
    {
        _log!.WriteLine($"{Since.Elapsed.TotalSeconds,8:F2} s  {s}");
        _log.Flush();
    }

    /// <summary>The list rows realised under <paramref name="root"/>.</summary>
    internal static int Containers(DependencyObject root)
    {
        int n = 0;
        void Walk(DependencyObject d)
        {
            if (d is ListBoxItem) n++;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return n;
    }

    /// <summary>The window thread's longest wait for a turn at input priority since the last <see cref="Take"/>, sampled every 20 ms
    /// from a thread of its own above the normal priority (never the thread pool's, which background work may keep busy), so the wait
    /// measured is the window thread's alone.</summary>
    internal sealed class Latency
    {
        double _worst;
        volatile bool _stop;
        readonly Thread _run;

        public Latency(Dispatcher d)
        {
            _run = new Thread(() =>
            {
                while (!_stop)
                {
                    var t = Stopwatch.StartNew();
                    d.Invoke(() => { }, DispatcherPriority.Input);
                    double ms = t.Elapsed.TotalMilliseconds;
                    double was;
                    do was = Volatile.Read(ref _worst); while (ms > was && Interlocked.CompareExchange(ref _worst, ms, was) != was);
                    Thread.Sleep(20);
                }
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "latency probe" };
            _run.Start();
        }

        public double Take() => Interlocked.Exchange(ref _worst, 0);

        public Task Stop()
        {
            _stop = true;
            // the probe's last Invoke needs the window thread: it ends once this returns to the dispatcher
            return Task.Run(_run.Join);
        }
    }

    /// <summary>The probe runs only when asked for: skipped (said so, never passed) without GRBANIM_TIMING_LOG and GRBANIM_GAME.</summary>
    public sealed class TimingFactAttribute : FactAttribute
    {
        public TimingFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("GRBANIM_TIMING_LOG") is null || GameDataFactAttribute.Game is null)
                Skip = "the timing probe runs only with GRBANIM_TIMING_LOG (its log) and GRBANIM_GAME (the game folder) set";
        }
    }

    [TimingFact]
    public void Probe()
    {
        string logPath = Environment.GetEnvironmentVariable("GRBANIM_TIMING_LOG")!;
        string game = GameDataFactAttribute.Game!;
        string? forge = Environment.GetEnvironmentVariable("GRBANIM_TIMING_FORGE");
        string cache = Environment.GetEnvironmentVariable("GRBANIM_TIMING_CACHE") ?? Path.Combine(Path.GetDirectoryName(logPath)!, "cache");
        var dims = (Environment.GetEnvironmentVariable("GRBANIM_TIMING_SIZE") ?? "1484x861").Split('x');
        var size = new Size(double.Parse(dims[0]), double.Parse(dims[1]));
        using var log = _log = new StreamWriter(logPath, append: true);
        bool cold = !Directory.Exists(cache) || Directory.GetFiles(cache, "edits-*.json").Length == 0;
        Log($"---- {(forge is null ? "the install's own forge" : "forge " + Path.GetFileName(forge))}, {size.Width}x{size.Height}, {(cold ? "no cache" : "the cache present")}");
        Wpf.RunAsync(async () =>
        {
            var a = new List<string> { "--game", game, "--index-cache", cache, "--shot", Path.Combine(cache, "unused.png") + "||||edit" };
            if (forge is not null) a.AddRange(["--forge", forge]);
            var args = ViewerArgs.Parse(a);
            var w = new MainWindow(args);
            var root = (FrameworkElement)w.Content;
            var wait = new Latency(w.Dispatcher);
            double Lay()
            {
                var sw = Stopwatch.StartNew();
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                return sw.Elapsed.TotalMilliseconds;
            }
            Lay();
            wait.Take();
            var all = Stopwatch.StartNew();
            string? err = await w.Library.LoadForShotsAsync(args);
            var g = (GameInstall)typeof(LibraryView).GetField("_install", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.LibraryPage)!;
            Log($"{g.Clips.Count} clips: the install opened and its clip index in {all.Elapsed.TotalSeconds:F1} s{(err is null ? "" : ", " + err)}; the window thread's longest wait {wait.Take():F0} ms");
            Log($"Library laid out in {Lay():F0} ms, {Containers(w.LibraryPage.ClipPanel.List)} rows realised");
            var sel = Stopwatch.StartNew();
            w.LibraryPage.ClipPanel.List.SelectedItem = g.FindClip("mcm_nwp_fm_std_V1_cycle");
            double clicked = sel.Elapsed.TotalMilliseconds;
            while (!w.LibraryPage.ClipPlays && sel.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
            Log($"a clip clicked on Library: the click handled in {clicked:F0} ms, the clip read and playing {sel.Elapsed.TotalMilliseconds:F0} ms after; laid out in {Lay():F0} ms");
            wait.Take();

            foreach (var (tab, name, list) in new[] { (1, "Edit", w.EditPage.ClipPanel.List), (2, "Import", w.ImportPage.SlotPanel.List), (3, "Package", w.PackagePage.ItemsPanel.List) })
            {
                w.Tabs.SelectedIndex = tab;
                double first = Lay();
                var input = Stopwatch.StartNew();
                await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                Log($"{name} shown: its first layout {first:F0} ms, then an input handled {input.Elapsed.TotalMilliseconds:F0} ms later; {Containers(list)} rows realised of {list.Items.Count}"
                    + (tab == 1 ? $"; the edit marks {(w.EditPage.MarksPending ? "pending" : "ready")}, the list's count: {w.EditPage.ClipPanel.CountText.Text}" : ""));
            }
            w.Tabs.SelectedIndex = 1;
            Lay();
            Log($"while the tabs were shown, the window thread's longest wait {wait.Take():F0} ms");
            // a click on a row of Edit's list: the selection handled at once, the clip's edits shown once worked out
            var c = g.FindClip("fm_std_HG_V0_idle_cool_06")!;
            sel.Restart();
            w.EditPage.ClipPanel.List.SelectedItem = c;
            double handled = sel.Elapsed.TotalMilliseconds;
            sel.Restart();
            await w.EditPage.ChooseClipAsync(c);
            Lay();
            Log($"a clip clicked on Edit: the click handled in {handled:F0} ms, its edits shown {sel.Elapsed.TotalMilliseconds:F0} ms after (with the marks {(w.EditPage.MarksPending ? "still being worked out" : "ready")}); "
                + $"the window thread's longest wait {wait.Take():F0} ms; {w.EditPage.OptionsSummary}");
            sel.Restart();
            await w.EditPage.WaitForChoicesAsync();
            Lay();
            Log($"the edit marks had {all.Elapsed.TotalSeconds:F1} s after the install was asked for ({w.EditPage.MarksHad}); the window thread's longest wait meanwhile {wait.Take():F0} ms; "
                + $"{Containers(w.EditPage.ClipPanel.List)} rows realised of {w.EditPage.ClipPanel.List.Items.Count}; {w.EditPage.ClipPanel.CountText.Text}");
            foreach (var n in new[] { "mca_emote_Hello", "mcm_nwp_fm_std_V1_cycle", "fm_std_HG_V0_idle_cool_06" })
            {
                sel.Restart();
                await w.EditPage.ChooseClipAsync(g.FindClip(n)!);
                Lay();
                Log($"{n} chosen on Edit, its edits shown in {sel.Elapsed.TotalMilliseconds:F0} ms; {w.EditPage.OptionsSummary}");
            }
            // the window thread's own steps when an install opens and its index comes, each timed again on its own
            var index = (await InstallWork.Index(g))!;
            string Time(Action a) { var t = Stopwatch.StartNew(); a(); return FormattableString.Invariant($"{t.Elapsed.TotalMilliseconds:F0} ms"); }
            Log("the window thread's steps: "
                + $"the Library's list given the clips {Time(() => w.LibraryPage.ClipPanel.SetClips(g.Clips))}, "
                + $"the Import screen given the install {Time(() => w.ImportPage.SetInstall(g, true))}, "
                + $"the Edit screen given it {Time(() => w.EditPage.SetInstall(g, true, w.LibraryPage))}, "
                + $"the Package screen given it {Time(() => w.PackagePage.SetInstall(g))}, "
                + $"the index given to the four lists {Time(() => { w.LibraryPage.ClipPanel.SetIndex(index); w.ImportPage.SetIndex(g, index); w.EditPage.SetIndex(g, index); w.PackagePage.SetIndex(g, index); })}, "
                + $"the Package screen refreshed {Time(w.PackagePage.Refresh)}");
            await w.EditPage.WaitForChoicesAsync();
            var panel = w.LibraryPage.ClipPanel;
            foreach (var q in new[] { "hello emote", "deth head back", "walk north east", "0x0000", "holstered walk" })
                Log($"the search '{q}' on the Library's list: {Time(() => { panel.SearchBox.Text = q; panel.Apply(); })}, {panel.Rows.Count} rows");
            Log($"the Import screen's length filter: {Time(() => w.ImportPage.SlotPanel.SetConstraint(c => c.LastTick == 120, "only the file's length, 120 ticks"))}");
            Log($"the window thread's longest wait over these steps {wait.Take():F0} ms");
            await wait.Stop();
        });
    }
}
