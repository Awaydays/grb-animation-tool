using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>The program: parses the command line (used only by automated checks), opens the main window, and turns any error no code
/// caught, on the window's thread, in background work or on any other thread, into a message in the window and a line in the error log
/// instead of a crash (<see cref="ErrorReport"/>). Under --screenshot an error ends the run with its text on stderr and a non-zero exit
/// code.</summary>
public partial class App : Application
{
    ViewerArgs _args = new();
    Timer? _watchdog;
    ErrorReport? _errors;

    /// <summary>Set before the application is made by a host that only uses its resources (the tests, which lay the screens out in a
    /// process of their own): the program then doesn't start, so it reads no settings, shows no window or box, catches no error and
    /// writes nothing in the user's folder.</summary>
    internal static bool Hosted { get; set; }

    /// <summary>The error handling the program installed when it started (null when it didn't start: <see cref="Hosted"/>).</summary>
    internal ErrorReport? Errors => _errors;

    /// <summary>An error the screens caught themselves and said on the screen (a catch-all of theirs), when it isn't one of the kinds the
    /// tool foresees (<see cref="Foreseen"/>): written to the error log with where it came from, and in a run that takes pictures to stderr,
    /// the run then ending non-zero. Nothing when the program didn't start (the tests).</summary>
    internal static void LogError(Exception ex, string where)
    {
        if (!Foreseen(ex)) LogCaught(ex, where);
    }

    /// <summary>As <see cref="LogError"/>, whatever its kind (a fault of the edits' code on a clip, which no kind makes foreseen).</summary>
    internal static void LogCaught(Exception ex, string where) => (Current as App)?._errors?.Caught(ex, where);

    /// <summary>A line in the error log (a count after the first few of many errors of a kind), nothing when the program didn't start.</summary>
    internal static void LogNote(string where, string text, bool counts = false) => (Current as App)?._errors?.Note(where, text, counts);

    /// <summary>The kinds of error the tool foresees and says plainly (a file that can't be read or written, data that isn't what it should
    /// be, a refusal, a cancellation): never logged as an error.</summary>
    internal static bool Foreseen(Exception ex) => ErrorReport.Inner(ex) is OperationCanceledException or IOException or UnauthorizedAccessException
        or InvalidDataException or System.Text.Json.JsonException or Core.Export.ExportException or Core.Records.RecordFileException
        or Core.Install.GameInstallException or Core.Oodle.OodleException or Core.Import.ImportRefusedException or Core.Edits.EditRefusedException;

    /// <summary>The error handling a test gives the program in place of a start (null: none).</summary>
    internal void UseErrorsForTests(ErrorReport? errors) => _errors = errors;

    /// <summary>Where a run's errors are logged: in the user's folder for a person's run whose command line parsed; nowhere for an
    /// automated run (which writes nothing there) or one started with a command line the program doesn't know.</summary>
    internal static string? LogPathFor(ViewerArgs args, bool parsed) => parsed && !args.Automated ? ErrorReport.DefaultLogPath : null;

    /// <summary>Makes the main window (for tests: one that fails to build).</summary>
    internal Func<ViewerArgs, Window> MakeWindow { get; set; } = a => new MainWindow(a);

    /// <summary>Ends the program with an exit code (for tests: noted instead).</summary>
    internal Action<int> End { get; set; }

    /// <summary>A message in a box of the theme's on the window's thread (for tests: noted instead).</summary>
    internal Action<string> Box { get; set; } = m => Notice.Show(m, Current?.MainWindow is { IsVisible: true } w ? w : null);

    /// <summary>Where an automated run's errors are written (stderr; for tests, a writer of their own).</summary>
    internal TextWriter ErrorOut { get; set; } = Console.Error;

    public App() => End = Shutdown;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (Hosted) return;
        base.OnStartup(e);
        Start(e.Args);
    }

    /// <summary>The program's start for a command line: the error handling first, then the command line, the theme and the window. Any
    /// error on the way is the start's failure (<see cref="FailedToStart"/>): the program ends, never left running with no window.</summary>
    internal void Start(string[] args)
    {
        // until the command line is read, any option makes a run automated (it writes nothing in the user's folder, and shows no box of
        // Windows' own); then as it reads (ViewerArgs.Automated)
        bool automated = args.Length > 0;
        // caught from the start, shown in the window once it is on the screen (else in a box); where it is logged and whether it ends
        // the run follow from the command line, below
        _errors = new ErrorReport
        {
            Show = m => MainWindow is MainWindow { IsLoaded: true, IsVisible: true } w && w.ShowError(m),
            // in the theme on the window's thread; else (a thread that is ending the process, or the theme's box failing) Windows' own box,
            // which needs no window thread, or stderr for an automated run
            ShowBox = m =>
            {
                if (Dispatcher.CheckAccess())
                    try { Box(m); return; }
                    catch (Exception) { }
                if (automated) ErrorOut.WriteLine("error: " + m);
                else MessageBox.Show(m, Core.Export.ToolInfo.Name,MessageBoxButton.OK, MessageBoxImage.Error);
            },
            // a run with no command line is a person's: an error in reading it (a fault, there being none to read) is logged
            LogPath = automated ? null : ErrorReport.DefaultLogPath,
        };
        _errors.Install(Dispatcher);
        // numbers read the same everywhere in the window (the list's bindings, the panels, the timeline): a dot for decimals
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        bool unattended = args.Any(a => a is "--screenshot" or "--shot" or "--export-skeleton" or "--export-clip" or "--export-out" or "--import-glb" or "--import-clip"
            or "--import-out" or "--import-keep" or "--overwrite" or "--edit" or "--edit-kind" or "--edit-keep" or "--package-out");
        int limit = 600;
        // a run that takes pictures or exports has its watchdog before anything that may hang, and ends on an error with it on stderr: a
        // provisional limit and end until its command line is read
        if (unattended)
        {
            _watchdog = new Timer(_ => { ErrorOut.WriteLine($"error: the picture run didn't finish within {limit} s"); Environment.Exit(3); }, null, limit * 1000, Timeout.Infinite);
            _errors.Unattended = OnUnattendedError;
        }
        try
        {
            string? badArgs = null;
            try { _args = ParseArgs(args); }
            // a command line that doesn't parse: an unattended run never shows a box, it ends with the message on stderr (any other error
            // in reading it is a fault of the tool's: the start fails, below, and it is logged)
            catch (ArgumentException ex)
            {
                if (unattended) { ErrorOut.WriteLine("error: " + ex.Message); End(2); return; }
                badArgs = ex.Message;
                _args = new();
            }
            // a person's run logs its errors in the user's folder; an automated one (or one whose command line didn't parse) writes nothing
            // there, and one that takes pictures or exports ends with the error on stderr, and notes there any error the screens caught
            _errors.LogPath = LogPathFor(_args, parsed: badArgs is null);
            // from here on, automated as the command line read says (one that didn't parse: a person's, for the box)
            automated = _args.Automated;
            if (_args.Unattended)
            {
                _errors.Unattended = OnUnattendedError;
                _errors.UnattendedCaught = t => ErrorOut.WriteLine("error: caught " + t);
                // the run's own limit: it waits for the clip index first (a read of the whole graph when no cache serves it), and a run
                // that pictures the Edit screen for every clip's edit marks (with no cache, about 300 core-seconds on all but one core)
                limit = 90 + 10 * _args.Shots.Count + (_args.TakesShots ? 60 : 0) + (_args.ShotsShowEdit ? MarksSeconds : 0) + (_args.Exports ? 60 : 0) + (_args.Imports ? 120 : 0)
                    + (_args.Edits ? 150 : 0) + (_args.Packages ? 60 : 0) + (_args.Logs ? 30 : 0);
                _watchdog?.Change(limit * 1000, Timeout.Infinite);
            }
            else
            {
                // a run that turned out not to take pictures (an option's value looking like one): no watchdog, a person's end
                _watchdog?.Dispose();
                _watchdog = null;
                _errors.Unattended = null;
            }
            // the theme: the command line's, else (a person's run) the one remembered, else following Windows
            Theme.Apply(_args.Theme ?? (_args.Automated ? ThemeChoice.System : ViewerSettings.Load(ViewerSettings.DefaultPath).ThemeChoice));
            // the previews' timing choice: a person's run remembers it in the user's settings, an automated one only where --settings says
            PreviewChoices.SettingsPath = _args.Settings ?? (_args.Automated ? null : ViewerSettings.DefaultPath);
            if (badArgs is not null)
            {
                // the box is the first window: it mustn't count as the main window whose closing ends the program
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Box(badArgs);
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            var w = MakeWindow(_args);
            MainWindow = w;
            w.Show();
        }
        catch (Exception ex) { FailedToStart(ex); }
    }

    /// <summary>The time a picture run gives every clip's edit marks: about 300 core-seconds of work spread on all but one core, twice
    /// that for a busy machine, never under a minute.</summary>
    internal static int MarksSeconds => Math.Max(60, (int)Math.Ceiling(600.0 / Math.Max(1, Environment.ProcessorCount - 1)));

    /// <summary>The start failed (the window couldn't be built or shown, say): the error is logged and said (in a box; under automation on
    /// stderr), and the program ends with exit code 1, never left running with no window.</summary>
    void FailedToStart(Exception ex)
    {
        // the half-built window (WPF made it the main window) is no main window to close: the program ends itself
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _errors?.Report(ex, "while starting", fatal: true);
        // an unattended run's end comes from the report (OnUnattendedError)
        if (_errors?.Unattended is null) End(1);
    }

    /// <summary>Reads the command line (for tests: a reader that fails).</summary>
    internal Func<string[], ViewerArgs> ParseArgs { get; set; } = ViewerArgs.Parse;

    /// <summary>Whether a picture run's watchdog is set (for tests).</summary>
    internal bool HasWatchdog => _watchdog is not null;

    /// <summary>Undoes a start made by a test (which never ends the process): its error handling removed, its watchdog stopped.</summary>
    internal void UndoStartForTests()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        _errors?.Uninstall();
        _errors = null;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    /// <summary>An error no code caught in a run that takes pictures or exports: the run ends with it on stderr and a non-zero exit code.</summary>
    void OnUnattendedError(string text)
    {
        ErrorOut.WriteLine("error: " + text);
        Dispatcher.BeginInvoke(() => End(1));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Dispose();
        _errors?.Uninstall();
        base.OnExit(e);
    }
}
