using System.IO;
using System.Windows;
using System.Windows.Threading;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>An error no code caught never closes the tool: thrown on the window's thread (a handler, an awaited one among them), in a
/// background task whose fault nobody looked at, or on a thread of its own, it is written to the error log and shown on the screen in
/// front, and the window's thread goes on. One on a thread of its own, which ends the process whatever is done, is logged and shown in a
/// box first; the log keeps under its limit.</summary>
[Collection("wpf")]
public sealed class ErrorTests
{
    /// <summary>A game folder that isn't there is said as such, without the advice to verify the game files, which can't make it appear.</summary>
    [Fact]
    public void AGameFolderThatIsntThereIsSaidWithoutTheAdviceToVerifyIt()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, "no-game-" + Guid.NewGuid().ToString("N"));
        Wpf.RunAsync(async () =>
            Assert.Equal($"The game folder {missing} doesn't exist.", await new LibraryView().LoadForShotsAsync(ViewerArgs.Parse(["--game", missing]))));
    }

    /// <summary>On a window thread of its own (a dispatcher running, as the program's): a handler's error, an awaited handler's and an
    /// unobserved task's are each logged and shown on that thread, and the thread runs what comes after them.</summary>
    [Fact]
    public void ErrorsOnTheWindowsThreadAndInBackgroundWorkAreShownAndItGoesOn()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "errors-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(dir, "errors.log");
        var shown = new List<(string Message, bool OnItsThread)>();
        Dispatcher? d = null;
        ErrorReport? report = null;
        bool after = false;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            d = Dispatcher.CurrentDispatcher;
            report = new ErrorReport { LogPath = log, Show = m => { lock (shown) shown.Add((m, d.CheckAccess())); return true; }, ShowBox = _ => { } };
            report.Install(d);
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "a window's thread" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        try
        {
            // a handler's error
            d!.BeginInvoke(new Action(() => throw new InvalidOperationException("a click's handler failed")));
            // an awaited handler (async void) that throws after its await
            d.BeginInvoke(new Action(Throws));
            static async void Throws()
            {
                await Task.Yield();
                throw new ArgumentException("an awaited handler failed");
            }
            d.BeginInvoke(new Action(() => after = true));
            Wait(() => Volatile.Read(ref after) && Shown().Count == 2, "the handlers' errors");
            Assert.Contains("Something went wrong on the window's thread (InvalidOperationException): a click's handler failed. The tool carries on", Shown()[0].Message);
            Assert.Contains("The tool carries on", Shown()[0].Message);
            Assert.Contains($"The detail is in {log}", Shown()[0].Message);
            Assert.Contains("(ArgumentException): an awaited handler failed", Shown()[1].Message);
            // a background task's fault nobody looked at: shown on the window's thread once the task is collected
            Faulted();
            Wait(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return Shown().Any(s => s.Message.Contains("lost in the background", StringComparison.Ordinal));
            }, "the unobserved task's error");
            Assert.Contains(Shown(), s => s.Message.Contains("Something went wrong in background work (IOException): lost in the background", StringComparison.Ordinal));
            Assert.All(Shown(), s => Assert.True(s.OnItsThread, $"shown off the window's thread: {s.Message}"));
            // the thread still runs what is given it
            Assert.Equal(42, d.Invoke(() => 42));
            string text = File.ReadAllText(log);
            foreach (var m in new[] { "a click's handler failed", "an awaited handler failed", "lost in the background" }) Assert.Contains(m, text);
            Assert.True(report!.Count >= 3);
        }
        finally
        {
            report?.Uninstall();
            d?.InvokeShutdown();
            t.Join();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        List<(string Message, bool OnItsThread)> Shown() { lock (shown) return [.. shown]; }

        static void Faulted()
        {
            var task = Task.Run(new Action(() => throw new IOException("lost in the background")));
            ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
        }
    }

    /// <summary>The window shows an error on the screen in front, whichever it is; one on a thread of its own, which ends the process, is
    /// said in a box and logged.</summary>
    [Fact]
    public void TheWindowShowsTheErrorOnTheScreenInFront()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "errors-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(dir, "errors.log");
        var boxes = new List<string>();
        try
        {
            Wpf.Run(() =>
            {
                Theme.Apply(ThemeChoice.Light);
                var w = new MainWindow(ViewerArgs.Parse(["--theme", "light"]));
                var at = new DateTime(2026, 1, 1);
                var report = new ErrorReport { LogPath = log, Now = () => at += TimeSpan.FromSeconds(1), Show = w.ShowError, ShowBox = boxes.Add };
                foreach (var (tab, error) in new (int, Func<System.Windows.Controls.TextBlock>)[]
                         { (0, () => w.LibraryPage.ErrorText), (1, () => w.EditPage.ErrorText), (2, () => w.ImportPage.ErrorText), (3, () => w.PackagePage.ErrorText) })
                {
                    w.Tabs.SelectedIndex = tab;
                    report.Report(new InvalidOperationException($"an error on tab {tab}"), "on the window's thread", fatal: false);
                    Assert.Contains($"(InvalidOperationException): an error on tab {tab}", error().Text);
                }
                Assert.Equal(Visibility.Visible, w.EditPage.ErrorBox.Visibility);
                // a thread of its own: the process ends whatever is done (the runtime's rule), so it is logged and said in a box first
                report.OnDomain(this, new UnhandledExceptionEventArgs(new NullReferenceException("a worker thread failed"), isTerminating: true));
                Assert.Contains("can't carry on from, and has to close: NullReferenceException: a worker thread failed", Assert.Single(boxes));
                // not ending it (the runtime doesn't say so on .NET, but should it): shown in the window like the others
                w.Tabs.SelectedIndex = 0;
                report.OnDomain(this, new UnhandledExceptionEventArgs(new NullReferenceException("a thread failed"), isTerminating: false));
                Assert.Contains("on a thread of its own (NullReferenceException): a thread failed", w.LibraryPage.ErrorText.Text);
                Assert.Equal(6, report.Count);
            });
            Assert.Contains("a worker thread failed", File.ReadAllText(log));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheErrorLogKeepsUnderItsLimitAndAnAutomatedRunEndsWithItsError()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "errors-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(dir, "errors.log");
        try
        {
            var shown = new List<string>();
            // a second between errors: none is held back as too many at once
            var at = new DateTime(2026, 1, 1);
            var report = new ErrorReport { LogPath = log, LogLimit = 4096, Now = () => at += TimeSpan.FromSeconds(1), Show = m => { shown.Add(m); return true; }, ShowBox = _ => { } };
            for (int i = 0; i < 200; i++)
            {
                report.Report(new InvalidOperationException($"error {i} " + new string(i % 2 == 0 ? 'x' : 'é', 300)), "on the window's thread", fatal: false);
                // never past its limit, in bytes, the older part kept once
                Assert.True(new FileInfo(log).Length <= 4096, $"the log is {new FileInfo(log).Length} B after {i + 1} errors");
            }
            Assert.True(File.Exists(log + ".old"));
            Assert.True(new FileInfo(log + ".old").Length <= 4096);
            Assert.Contains("error 199", File.ReadAllText(log));
            Assert.Equal(200, shown.Count);
            // one error's text cut to a quarter of the log
            report.Report(new InvalidOperationException(new string('ü', 5000)), "on the window's thread", fatal: false);
            Assert.True(new FileInfo(log).Length <= 4096);
            Assert.Contains("[cut]", File.ReadAllText(log));
            shown.RemoveAt(shown.Count - 1);
            // no log (an automated run's): shown all the same, with no path in it
            var none = new ErrorReport { Show = m => { shown.Add(m); return true; }, ShowBox = _ => { } };
            none.Report(new InvalidOperationException("no log"), "on the window's thread", fatal: false);
            Assert.DoesNotContain("The detail is in", shown[^1]);
            // a run that takes pictures: its own end instead of the window, with the whole error
            var ended = new List<string>();
            var unattended = new ErrorReport { Show = m => { shown.Add(m); return true; }, ShowBox = _ => { }, Unattended = ended.Add };
            unattended.Report(new InvalidOperationException("in a picture run"), "on the window's thread", fatal: false);
            Assert.StartsWith("on the window's thread: System.InvalidOperationException: in a picture run", Assert.Single(ended));
            Assert.Equal(201, shown.Count);
            // a window that can't show it: a box instead; a box that fails: nothing more, never an error of its own
            var boxed = new List<string>();
            new ErrorReport { Show = _ => false, ShowBox = boxed.Add }.Report(new InvalidOperationException("no window"), "on the window's thread", fatal: false);
            Assert.Contains("no window", Assert.Single(boxed));
            new ErrorReport { Show = _ => throw new InvalidOperationException(), ShowBox = _ => throw new InvalidOperationException() }
                .Report(new InvalidOperationException("nowhere to show"), "on the window's thread", fatal: false);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>An error thrown on every layout pass (the same error from the same place, again and again): logged and shown once, the
    /// repeats only counted, so showing it can't drive the window round at full speed; the next other error's log line says how many
    /// were held back. At most five a second are logged and shown; an error while one is being shown is logged only, and a window that
    /// fails to show one gives a box instead.</summary>
    [Fact]
    public void AnErrorRepeatedOnEveryPassIsShownOnceAndErrorsAreCappedPerSecond()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "errors-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(dir, "errors.log");
        try
        {
            var at = new DateTime(2026, 1, 1);
            var shown = new List<string>();
            var report = new ErrorReport { LogPath = log, Now = () => at, Show = m => { shown.Add(m); return true; }, ShowBox = _ => { } };
            static Exception Thrown(string m) { try { throw new InvalidOperationException(m); } catch (Exception e) { return e; } }
            var everyPass = Thrown("a cell's converter failed");
            for (int i = 0; i < 1000; i++)
            {
                report.Report(everyPass, "on the window's thread", fatal: false);
                at += TimeSpan.FromMilliseconds(16);   // a frame apart, for 16 s: still one run of the same error
            }
            // shown once, and once more after a hundred in a row as one that keeps happening (the window may stay busy: WPF lays out again
            // after a pass that threw), the rest counted
            Assert.Equal(2, shown.Count);
            Assert.StartsWith("An error keeps happening on the window's thread (InvalidOperationException): a cell's converter failed.", shown[1]);
            Assert.Contains("The screen may stay slow while it does", shown[1]);
            Assert.Equal(998, report.Quieted);
            Assert.Equal(2, File.ReadAllText(log).Split("a cell's converter failed").Length - 1);
            Assert.Contains("again and again (100 times in a row, still going)", File.ReadAllText(log));
            // the same error after a pause is shown again; another error at once is shown, and the log says what was held back
            at += TimeSpan.FromSeconds(3);
            report.Report(Thrown("another"), "on the window's thread", fatal: false);
            Assert.Equal(3, shown.Count);
            Assert.Contains("(899 repeat(s) of errors already logged came before this one, not logged again)", File.ReadAllText(log));
            // twenty other errors in the same instant: four more shown (five in the second), the rest logged all the same
            for (int i = 0; i < 20; i++) report.Report(Thrown($"burst {i}"), "on the window's thread", fatal: false);
            Assert.Equal(7, shown.Count);
            Assert.Equal(998, report.Quieted);
            Assert.Equal(16, report.NotShown);
            string logged = File.ReadAllText(log);
            Assert.All(Enumerable.Range(0, 20), i => Assert.Contains($"burst {i}", logged));
            Assert.Contains("(one of too many at once to show)", logged);
            // two errors by turns on every pass (two bindings failing one after the other): each shown once, then both quiet
            var shownTurns = new List<string>();
            var turns = new ErrorReport { Now = () => at += TimeSpan.FromMilliseconds(8), Show = m => { shownTurns.Add(m); return true; }, ShowBox = _ => { } };
            var (a, b) = (Thrown("binding A failed"), Thrown("binding B failed"));
            for (int i = 0; i < 90; i++)
            {
                turns.Report(a, "on the window's thread", fatal: false);
                turns.Report(b, "on the window's thread", fatal: false);
            }
            Assert.Equal(2, shownTurns.Count);
            Assert.Equal(178, turns.Quieted);
            // the error that ends the process is always said
            var boxes = new List<string>();
            var fatal = new ErrorReport { Now = () => at, MostPerSecond = 0, Show = _ => true, ShowBox = boxes.Add };
            fatal.Report(Thrown("the end"), "on a thread of its own", fatal: true);
            Assert.Single(boxes);
            // an error while one is shown (a modal box pumps the thread): logged, never a second box over the first
            ErrorReport? nested = null;
            var shownNested = new List<string>();
            nested = new ErrorReport
            {
                LogPath = log, Now = () => at += TimeSpan.FromSeconds(1),
                Show = m => { shownNested.Add(m); nested!.Report(Thrown("while showing"), "on the window's thread", fatal: false); return true; }, ShowBox = _ => { },
            };
            nested.Report(Thrown("first"), "on the window's thread", fatal: false);
            Assert.Single(shownNested);
            Assert.Contains("while showing", File.ReadAllText(log));
            // a window that throws while showing one: a box instead
            var boxed = new List<string>();
            new ErrorReport { Show = _ => throw new InvalidOperationException("the strip failed"), ShowBox = boxed.Add }
                .Report(Thrown("shown in a box"), "on the window's thread", fatal: false);
            Assert.Contains("shown in a box", Assert.Single(boxed));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>The tests' host makes the program's application for its resources only: the program doesn't start there, so it reads no
    /// settings, shows no box (the host's own command line isn't the program's), installs no error handling and logs nothing in the
    /// user's folder. A run logs there only when it is a person's and its command line parsed.</summary>
    [Fact]
    public void UnderTheTestsTheProgramDoesntStartAndLogsOnlyForAPersonsRun()
    {
        Wpf.RunAsync(async () =>
        {
            // the application's start, queued when it was made, has had its turn
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(App.Hosted);
            Assert.Null(Wpf.App.Errors);
            // the start itself, called as WPF calls it, with a command line the program doesn't know (the host's own): nothing installed,
            // no window, no box (a box would hold this call until closed)
            var startup = (StartupEventArgs)Activator.CreateInstance(typeof(StartupEventArgs), nonPublic: true)!;
            int windows = Wpf.App.Windows.Count;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            typeof(App).GetMethod("OnStartup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(Wpf.App, [startup]);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Null(Wpf.App.Errors);
            Assert.Equal(windows, Wpf.App.Windows.Count);
            // no window of the program's own (the windows here are the tests', never shown)
            Assert.DoesNotContain(Wpf.App.Windows.Cast<Window>(), w => w.IsVisible);
        });
        Assert.Equal(ErrorReport.DefaultLogPath, App.LogPathFor(ViewerArgs.Parse([]), parsed: true));
        // in the run's own folder for the tool's files (ToolData), never the user's
        Assert.Equal(Path.Combine(TestToolData.Folder, "errors.log"), ErrorReport.DefaultLogPath);
        // a command line that didn't parse (the tests' host's own, say): nowhere
        Assert.Null(App.LogPathFor(new ViewerArgs(), parsed: false));
        // an automated run: nowhere, whatever it does
        foreach (var a in new[]
                 {
                     new[] { "--game", "g" }, ["--theme", "dark"], ["--game", "g", "--shot", "x.png"], ["--game", "g", "--index-cache", "c"],
                     ["--package-store", "p"], ["--settings", "s.json"], ["--size", "1200x800"], ["--log-file", "clips.csv"], ["--log-folder", "logs"],
                 })
        {
            Assert.True(ViewerArgs.Parse(a).Automated, string.Join(" ", a));
            Assert.Null(App.LogPathFor(ViewerArgs.Parse(a), parsed: true));
        }
    }

    /// <summary>A start whose window fails to build (a screen's constructor throwing): the program ends with exit code 1 and says why
    /// (a run that takes pictures on stderr, a person's in a box), at once, never left running with no window; nothing is written in the
    /// user's folder (both runs are automated, so they log nowhere) and nothing is shown (the box and the end are the test's own).</summary>
    [Fact]
    public void AStartWhoseWindowFailsEndsWithAnErrorAndExitCodeOne()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "start-" + Guid.NewGuid().ToString("N"));
        Wpf.RunAsync(async () =>
        {
            var app = Wpf.App;
            var (make, end, box, err) = (app.MakeWindow, app.End, app.Box, app.ErrorOut);
            try
            {
                foreach (bool pictures in new[] { true, false })
                {
                    var codes = new List<int>();
                    var boxes = new List<string>();
                    var stderr = new StringWriter();
                    app.MakeWindow = _ => throw new InvalidOperationException("a screen failed to build");
                    app.End = codes.Add;
                    app.Box = boxes.Add;
                    app.ErrorOut = stderr;
                    string[] args = pictures
                        ? ["--game", Path.Combine(dir, "game"), "--shot", Path.Combine(dir, "x.png"), "--theme", "light"]
                        : ["--theme", "light"];
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    app.Start(args);
                    // the run's end comes on the window's thread's next turn
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    try
                    {
                        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the start took {sw.Elapsed.TotalSeconds:F1} s");
                        Assert.Equal([1], codes);
                        Assert.Null(app.Errors!.LogPath);
                        if (pictures)
                        {
                            Assert.Contains("error: while starting: System.InvalidOperationException: a screen failed to build", stderr.ToString());
                            Assert.Empty(boxes);
                        }
                        else
                        {
                            Assert.Contains("while starting it can't carry on from, and has to close: InvalidOperationException: a screen failed to build.", Assert.Single(boxes));
                            Assert.Equal("", stderr.ToString());
                            // a later error, with no window loaded and shown (the main window is one never shown): a box, never a strip
                            // nobody sees
                            app.Errors!.Report(new InvalidOperationException("after the start"), "on the window's thread", fatal: false);
                            Assert.Equal(2, boxes.Count);
                            Assert.Contains("after the start", boxes[1]);
                        }
                    }
                    finally { app.UndoStartForTests(); }
                }
                // an option's value that looks like a picture run's option (a clip named "--edit"): a person's run after all, so its
                // provisional watchdog goes (it would end the program after ten minutes)
                {
                    var codes = new List<int>();
                    (app.MakeWindow, app.End, app.Box, app.ErrorOut) = (_ => throw new InvalidOperationException("no window here"), codes.Add, _ => { }, new StringWriter());
                    try
                    {
                        app.Start(["--game", Path.Combine(dir, "game"), "--clip", "--edit", "--theme", "light"]);
                        Assert.False(app.HasWatchdog);
                        Assert.Equal([1], codes);
                    }
                    finally { app.UndoStartForTests(); }
                    // a picture run's watchdog gives every clip's edit marks their time: at least a minute, more on fewer cores
                    Assert.True(App.MarksSeconds >= 60);
                }
                // a picture run's command line that doesn't parse (inside the start's guard): exit code 2, the message on stderr, no box
                {
                    var codes = new List<int>();
                    var boxes = new List<string>();
                    var stderr = new StringWriter();
                    (app.End, app.Box, app.ErrorOut) = (codes.Add, boxes.Add, stderr);
                    try
                    {
                        app.Start(["--shot", Path.Combine(dir, "x.png"), "--no-such-option"]);
                        Assert.Equal([2], codes);
                        Assert.Contains("error: Unknown option '--no-such-option'", stderr.ToString());
                        Assert.Empty(boxes);
                    }
                    finally { app.UndoStartForTests(); }
                }
                // a fault in reading the command line (no bad option, a fault of the tool's): the start fails as the tool's error, in a
                // picture run on stderr whole, exit code 1, no box
                {
                    var codes = new List<int>();
                    var boxes = new List<string>();
                    var stderr = new StringWriter();
                    (app.End, app.Box, app.ErrorOut) = (codes.Add, boxes.Add, stderr);
                    app.ParseArgs = _ => throw new NullReferenceException("a fault in reading the options");
                    try
                    {
                        app.Start(["--shot", Path.Combine(dir, "x.png")]);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        Assert.Equal([1], codes);
                        Assert.Contains("error: while starting: System.NullReferenceException: a fault in reading the options", stderr.ToString());
                        Assert.Empty(boxes);
                    }
                    finally { app.ParseArgs = ViewerArgs.Parse; app.UndoStartForTests(); }
                }
                // the theme's box failing as the start does: an automated run says it on stderr (a person's, in Windows' own box)
                {
                    var codes = new List<int>();
                    var stderr = new StringWriter();
                    (app.MakeWindow, app.End, app.Box, app.ErrorOut) = (_ => throw new InvalidOperationException("a screen failed to build"), codes.Add,
                        _ => throw new InvalidOperationException("the box failed too"), stderr);
                    try
                    {
                        app.Start(["--theme", "light"]);
                        Assert.Equal([1], codes);
                        Assert.Contains("error: The tool hit an error while starting it can't carry on from, and has to close: InvalidOperationException: a screen failed to build.", stderr.ToString());
                    }
                    finally { app.UndoStartForTests(); }
                }
            }
            finally
            {
                app.ParseArgs = ViewerArgs.Parse;
                (app.MakeWindow, app.End, app.Box, app.ErrorOut) = (make, end, box, err);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        });
    }

    /// <summary>An error a screen caught itself and said in its own words: logged with where it came from (its repeats only counted), never
    /// shown again; in a run that takes pictures noted on stderr, the run then failing. One of the kinds the tool foresees (a file that
    /// can't be read, a refusal) isn't logged at all; a call made by reflection is unwrapped to the error itself.</summary>
    [Fact]
    public void AnErrorAScreenCaughtIsLoggedButNotShownAgain()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "caught-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(dir, "errors.log");
        try
        {
            var shown = new List<string>();
            var noted = new List<string>();
            var at = new DateTime(2026, 1, 1);
            var report = new ErrorReport { LogPath = log, Now = () => at += TimeSpan.FromMilliseconds(10), Show = m => { shown.Add(m); return true; }, ShowBox = shown.Add,
                UnattendedCaught = noted.Add };
            static Exception Thrown(Exception e) { try { throw e; } catch (Exception c) { return c; } }
            var bug = Thrown(new NullReferenceException("a clip of an odd shape"));
            for (int i = 0; i < 10; i++) report.Caught(bug, "while checking which edits mca_emote_Hello takes");
            Assert.Empty(shown);
            Assert.Equal(10, report.CaughtCount);
            Assert.Equal(9, report.Quieted);
            string text = File.ReadAllText(log);
            Assert.Contains("while checking which edits mca_emote_Hello takes (caught, and said on the screen)", text);
            Assert.Contains("System.NullReferenceException: a clip of an odd shape", text);
            Assert.Contains("   at ", text);
            Assert.StartsWith("while checking which edits mca_emote_Hello takes: System.NullReferenceException: a clip of an odd shape", Assert.Single(noted));
            report.Note("while working out which edits each clip allows", "the tool failed on 37 clip(s) in all; the first 5 are logged above");
            Assert.Contains("the tool failed on 37 clip(s) in all", File.ReadAllText(log));
            // the kinds foreseen are said by the screens and never logged as errors; others are, a task's or a reflected call's unwrapped
            Assert.True(App.Foreseen(new AggregateException(new IOException("the disk is full"))));
            Assert.True(App.Foreseen(new GrbAnimTool.Core.Edits.EditRefusedException(GrbAnimTool.Core.Edits.EditRefusal.HandTracks, "hand tracks")));
            Assert.True(App.Foreseen(new OperationCanceledException()));
            Assert.False(App.Foreseen(new NullReferenceException()));
            Assert.False(App.Foreseen(new System.Reflection.TargetInvocationException(new ArgumentOutOfRangeException("x"))));
            Assert.IsType<ArgumentOutOfRangeException>(ErrorReport.Inner(new System.Reflection.TargetInvocationException(new ArgumentOutOfRangeException("x"))));
            // a reflected call's error is shown as the error itself
            report.Report(new System.Reflection.TargetInvocationException(new InvalidCastException("not a clip")), "on the window's thread", fatal: false);
            Assert.Contains("(InvalidCastException): not a clip.", Assert.Single(shown));
            // the program's own entry (no program started under the tests: nothing logged, nothing thrown)
            App.LogError(new NullReferenceException(), "nowhere");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    static void Wait(Func<bool> done, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"{what}: not within 30 s");
            Thread.Sleep(50);
        }
    }
}
