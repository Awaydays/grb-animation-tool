using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace GrbAnimTool.App;

/// <summary>Every error no code of the tool caught: on the window's thread (<see cref="Dispatcher.UnhandledException"/>), in a task whose
/// fault nobody looked at (<see cref="TaskScheduler.UnobservedTaskException"/>) and on any other thread
/// (<see cref="AppDomain.UnhandledException"/>). Each is written to the error log (<see cref="LogPath"/>, kept under
/// <see cref="LogLimit"/>) and shown in the window, on the screen in front, with where its detail is; the program carries on. An error
/// on a thread of its own ends the process whatever is done (the runtime's rule): it is logged and shown in a message box first. Under
/// automation (pictures, exports) the run is told instead (<see cref="Unattended"/>): its error on stderr, and the run ends with it.</summary>
sealed class ErrorReport
{
    /// <summary>The log's file, or null: none written (an automated run's errors go to stderr only).</summary>
    public string? LogPath { get; set; }

    /// <summary>The log's size past which it starts again, the older part kept beside it (<c>.old</c>) once.</summary>
    public long LogLimit { get; init; } = 1 << 20;

    /// <summary>Shows a message in the window (on the window's thread); false when there is no window to show it in.</summary>
    public required Func<string, bool> Show { get; init; }

    /// <summary>Shows a message in a box of its own, when no window can (and for an error that ends the process).</summary>
    public required Action<string> ShowBox { get; init; }

    /// <summary>An automated run's own end on an error: given the error's text, it ends the run (null: a person's run, which carries on).</summary>
    public Action<string>? Unattended { get; set; }

    Dispatcher? _dispatcher;

    /// <summary>How many errors were reported (for tests).</summary>
    public int Count => _count;
    int _count;

    /// <summary>The program's own log in the user's folder: <c>%APPDATA%\GrbAnimTool\errors.log</c>.</summary>
    public static string DefaultLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrbAnimTool", "errors.log");

    /// <summary>Starts catching what reaches <paramref name="dispatcher"/> (the window's thread), the unobserved tasks and the other threads.</summary>
    public void Install(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        dispatcher.UnhandledException += OnDispatcher;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        AppDomain.CurrentDomain.UnhandledException += OnDomain;
    }

    public void Uninstall()
    {
        if (_dispatcher is { } d) d.UnhandledException -= OnDispatcher;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
        AppDomain.CurrentDomain.UnhandledException -= OnDomain;
        _dispatcher = null;
    }

    internal void OnDispatcher(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // handled: the window goes on, with the error shown
        e.Handled = true;
        Report(e.Exception, "on the window's thread", fatal: false);
    }

    internal void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Report(e.Exception, "in background work", fatal: false);
    }

    internal void OnDomain(object sender, UnhandledExceptionEventArgs e) =>
        Report(e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "an unknown error"), "on a thread of its own", fatal: e.IsTerminating);

    /// <summary>The same error again (its kind, message and the place it was thrown) within this long of its last time is a repeat:
    /// counted, neither logged nor shown again. An error thrown on every layout pass would otherwise be shown, change the layout, be thrown
    /// again, and so on at full speed. (WPF lays out again after a layout pass that threw, so such an error may still keep the window busy;
    /// quieting it keeps the log and the screen from feeding the loop.)</summary>
    public TimeSpan RepeatWithin { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>At most this many errors are shown in a second; the rest are logged only.</summary>
    public int MostPerSecond { get; init; } = 5;

    /// <summary>The clock (for tests).</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>A repeat seen this many times in a row (one error on every layout pass, the window kept busy) is said once more, as one
    /// that keeps happening; then quiet again.</summary>
    public int KeepsHappeningAfter { get; init; } = 100;

    /// <summary>How many different errors are remembered as recent, so errors that come by turns (two bindings failing one after the
    /// other on every pass) are repeats too.</summary>
    const int RecentKinds = 32;

    readonly object _sync = new();
    readonly Dictionary<string, (DateTime At, int Repeats)> _recentKinds = [];
    int _held;
    readonly Queue<DateTime> _recent = new();

    /// <summary>How many errors were counted and neither logged nor shown (repeats of recent ones).</summary>
    public int Quieted => _quieted;
    int _quieted;

    /// <summary>How many errors were logged but not shown (past <see cref="MostPerSecond"/>).</summary>
    public int NotShown => _notShown;
    int _notShown;

    enum Verdict { Report, LogOnly, Quiet, KeepsHappening }

    /// <summary>Whether this error is reported, logged only (a new error past the cap of the second), only counted (a repeat of a recent
    /// one), or said once more as one that keeps happening; with how many repeats were held back since the last one logged.</summary>
    Verdict Judge(Exception inner, out int heldBefore, bool shown = true)
    {
        string key = $"{inner.GetType().FullName}|{inner.Message}|{inner.StackTrace?.Split('\n', 2)[0].Trim()}";
        lock (_sync)
        {
            var now = Now();
            heldBefore = 0;
            foreach (var old in _recentKinds.Where(k => now - k.Value.At >= RepeatWithin).Select(k => k.Key).ToList()) _recentKinds.Remove(old);
            if (_recentKinds.TryGetValue(key, out var seen))
            {
                // the window slides: an error repeated without a pause stays quiet however long it goes on, but for one word after a
                // long run of it
                int repeats = seen.Repeats + 1;
                _recentKinds[key] = (now, repeats);
                if (repeats == KeepsHappeningAfter)
                {
                    heldBefore = _held;
                    _held = 0;
                    return Verdict.KeepsHappening;
                }
                _held++;
                _quieted++;
                return Verdict.Quiet;
            }
            // a new error: remembered as recent (its repeats quiet), logged; shown unless past the cap of the second
            if (_recentKinds.Count >= RecentKinds) _recentKinds.Remove(_recentKinds.MinBy(k => k.Value.At).Key);
            _recentKinds[key] = (now, 0);
            heldBefore = _held;
            _held = 0;
            // (an error caught and said by the screen itself is never shown here: it takes no place in the second's cap)
            if (!shown) return Verdict.LogOnly;
            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromSeconds(1)) _recent.Dequeue();
            if (_recent.Count >= MostPerSecond)
            {
                _notShown++;
                return Verdict.LogOnly;
            }
            _recent.Enqueue(now);
            return Verdict.Report;
        }
    }

    /// <summary>Logs the error and shows it: in the window when the program goes on (from any thread: shown on the window's), in a box when it
    /// ends; under automation, the run's own end instead. A repeat of a recent error is only counted; a new one past
    /// <see cref="MostPerSecond"/> is logged, not shown; a repeat that goes on (<see cref="KeepsHappeningAfter"/>) is said once more as one
    /// that keeps happening. Never throws.</summary>
    public void Report(Exception ex, string where, bool fatal)
    {
        Interlocked.Increment(ref _count);
        try
        {
            var inner = Inner(ex);
            var verdict = Judge(inner, out int before);
            // the error that ends the process is always said
            if (verdict == Verdict.Quiet && !fatal) return;
            bool keeps = verdict == Verdict.KeepsHappening && !fatal;
            string? logged = Log(ex.ToString(), keeps ? where + $", again and again ({KeepsHappeningAfter} times in a row, still going)"
                : verdict == Verdict.LogOnly && !fatal ? where + " (one of too many at once to show)" : where, before);
            if (Unattended is { } end) { end($"{where}: {ex}"); return; }
            if (verdict == Verdict.LogOnly && !fatal) return;
            string at = logged is null ? "" : $" The detail is in {logged}.";
            if (fatal)
            {
                ShowBox($"The tool hit an error {where} it can't carry on from, and has to close: {inner.GetType().Name}: {Sentence(inner.Message)}{at}");
                return;
            }
            string message = keeps
                ? $"An error keeps happening {where} ({inner.GetType().Name}): {Sentence(inner.Message)} The screen may stay slow while it does; switching to another screen or closing the tool and opening it again may stop it.{at}"
                : $"Something went wrong {where} ({inner.GetType().Name}): {Sentence(inner.Message)} The tool carries on; what you were doing may not have finished.{at}";
            var d = _dispatcher;
            if (d is null || d.CheckAccess()) ShowNow(message);
            else d.BeginInvoke(() => ShowNow(message));
        }
        catch (Exception) { }   // the report itself never takes the program down
    }

    /// <summary>How many errors the screens caught themselves and logged here (<see cref="Caught"/>).</summary>
    public int CaughtCount => _caught;
    int _caught;

    /// <summary>An automated run's note of an error the screens caught (written to stderr; the run goes on, and ends non-zero).</summary>
    public Action<string>? UnattendedCaught { get; set; }

    /// <summary>An error the screens caught themselves and already said on the screen (a catch-all of theirs): logged with where it came
    /// from (a repeat of a recent one only counted), never shown again; under automation also noted (<see cref="UnattendedCaught"/>).
    /// Never throws.</summary>
    public void Caught(Exception ex, string where)
    {
        Interlocked.Increment(ref _caught);
        try
        {
            // repeats are told apart by where they came from too (each clip's own fault is logged), and kept apart from the errors no code
            // caught (one caught here never quiets the same error escaping later)
            var inner = Inner(ex);
            string key = $"{where}|{inner.GetType().FullName}|{inner.Message}|{inner.StackTrace?.Split('\n', 2)[0].Trim()}";
            lock (_sync)
            {
                var now = Now();
                foreach (var old in _recentCaught.Where(k => now - k.Value >= RepeatWithin).Select(k => k.Key).ToList()) _recentCaught.Remove(old);
                bool repeat = _recentCaught.ContainsKey(key);
                if (!repeat && _recentCaught.Count >= RecentKinds) _recentCaught.Remove(_recentCaught.MinBy(k => k.Value).Key);
                _recentCaught[key] = now;
                if (repeat) { _quieted++; return; }
            }
            Log(ex.ToString(), where + " (caught, and said on the screen)", 0);
            UnattendedCaught?.Invoke($"{where}: {ex}");
        }
        catch (Exception) { }
    }

    readonly Dictionary<string, DateTime> _recentCaught = [];

    /// <summary>A plain line in the log (how many more of a kind of error came after the first few logged, the clips the cache says the
    /// tool failed on, a read that failed for a reason the tool foresees). With <paramref name="counts"/> it counts as an error caught (an
    /// automated run then ends non-zero), and is noted under automation. Never throws.</summary>
    public void Note(string where, string text, bool counts = false)
    {
        try
        {
            if (counts) Interlocked.Increment(ref _caught);
            Log(text, where, 0);
            if (counts) UnattendedCaught?.Invoke($"{where}: {text}");
        }
        catch (Exception) { }
    }

    /// <summary>Whether an error is being shown on the window's thread now: an error while it is (a modal box pumps the thread) is only
    /// logged, never a second box over the first.</summary>
    bool _showing;

    void ShowNow(string message)
    {
        if (_showing) return;
        _showing = true;
        try
        {
            bool shown;
            try { shown = Show(message); }
            catch (Exception) { shown = false; }
            if (!shown) ShowBox(message);
        }
        catch (Exception) { }
        finally { _showing = false; }
    }

    static string Sentence(string s) => s.TrimEnd().EndsWith('.') ? s.TrimEnd() : s.TrimEnd() + ".";

    /// <summary>The error itself: a task's fault is an aggregate of it, a call made by reflection wraps it.</summary>
    public static Exception Inner(Exception ex) => ex switch
    {
        AggregateException { InnerExceptions.Count: 1 } a => Inner(a.InnerExceptions[0]),
        System.Reflection.TargetInvocationException { InnerException: { } i } => Inner(i),
        _ => ex,
    };

    readonly object _logLock = new();

    /// <summary>Appends the error to the log (with how many were quieted before it), starting the log again, the older part kept as
    /// <c>.old</c>, when the entry would take it past <see cref="LogLimit"/>: the log never holds more. Returns the log's path, or null when
    /// there is none or it couldn't be written.</summary>
    string? Log(string text, string where, int quietedBefore)
    {
        if (LogPath is not { } path) return null;
        try
        {
            // one error's text never fills more than a quarter of the log (a UTF-8 character takes at most three bytes)
            long most = LogLimit / 4 / 3;
            if (text.Length > most) text = text[..(int)most] + " [cut]";
            string entry = string.Create(CultureInfo.InvariantCulture,
                $"{(quietedBefore > 0 ? $"({quietedBefore} repeat(s) of errors already logged came before this one, not logged again)\n" : "")}{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}\n{text}\n\n");
            long bytes = Encoding.UTF8.GetByteCount(entry);
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length + bytes > LogLimit) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, entry, new UTF8Encoding(false));
            }
            return path;
        }
        catch (Exception) { return null; }
    }
}
