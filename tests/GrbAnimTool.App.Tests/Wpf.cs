using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GrbAnimTool.App.Tests;

/// <summary>The test process's one WPF application (a process holds only one), on an STA thread of its own that runs its dispatcher:
/// every test that builds the screens runs its work there, one after another (<see cref="WpfCollection"/>).</summary>
static class Wpf
{
    static readonly Lazy<Dispatcher> Ui = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    static Dispatcher Start()
    {
        Dispatcher? d = null;
        Exception? failed = null;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                // the program's resources only: it doesn't start (no settings read, no window or box, no error handling, nothing written)
                App.Hosted = true;
                var app = new App();
                app.InitializeComponent();
                d = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex) { failed = ex; }
            ready.Set();
            if (failed is null) Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF tests" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        if (failed is not null) throw new InvalidOperationException("the WPF application couldn't start", failed);
        return d!;
    }

    /// <summary>The application, with the theme's resources the screens take.</summary>
    public static App App => (App)Application.Current;

    /// <summary>Runs <paramref name="work"/> on the application's thread; its exception comes back to the caller.</summary>
    public static void Run(Action work) => Ui.Value.Invoke(work);

    /// <summary>Runs <paramref name="work"/> on the application's thread and gives its value back.</summary>
    public static T RunGet<T>(Func<T> work) => Ui.Value.Invoke(work);

    /// <summary>Runs <paramref name="work"/> on the application's thread, its awaits there too, and waits for it to end.</summary>
    public static void RunAsync(Func<Task> work) => Ui.Value.InvokeAsync(work).Task.Unwrap().GetAwaiter().GetResult();
}

/// <summary>The tests that use the WPF application, never in parallel.</summary>
[CollectionDefinition("wpf", DisableParallelization = true)]
public sealed class WpfCollection;

/// <summary>A test that needs the game data (GRBANIM_FORGE, a vanilla DataPC_patch_01.forge; GRBANIM_GAME, the game folder); it skips
/// cleanly without it.</summary>
public sealed class GameDataFactAttribute : FactAttribute
{
    public static string? Forge => Existing(Environment.GetEnvironmentVariable("GRBANIM_FORGE"), File.Exists);
    public static string? Game => Existing(Environment.GetEnvironmentVariable("GRBANIM_GAME"), Directory.Exists);

    public GameDataFactAttribute()
    {
        if (Forge is null || Game is null)
            Skip = "game data not configured: set GRBANIM_FORGE (a vanilla DataPC_patch_01.forge) and GRBANIM_GAME (the game folder)";
    }

    static string? Existing(string? p, Func<string, bool> exists) => !string.IsNullOrWhiteSpace(p) && exists(p) ? p : null;
}
