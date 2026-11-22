using System.Globalization;

namespace GrbAnimTool.Core.Library;

/// <summary>One picture an automated run takes: the PNG to write, and the clip, time and mover setting to show in it (null: the run's
/// own <c>--clip</c>, <c>--time</c> and <c>--mover</c>), and the screen to show (<see cref="ViewerArgs.Screens"/>; null: the screen the
/// run's work shows, the Import screen after an import, the Edit screen after an edit, else the Library). On the Library, the list's
/// search text and its who- and what-filters (null: none), as spelt in the vocabulary (<see cref="Index.ClipCategories"/>). The theme to
/// switch to for this picture and the ones after it (null: the theme as it stands), so one run can picture a screen in each. On the Edit
/// and Import screens, whether the original follows the edit's progress (true) or plays in its own time (false) for this picture and the
/// ones after it (null: as it stands), without remembering it.</summary>
public sealed record ShotSpec(string Png, string? Clip, float? Time, bool? Mover, string? Screen = null, string? Search = null, string? Who = null,
    string? What = null, ThemeChoice? Theme = null, bool? Follows = null);

/// <summary>The viewer's command line, for automated checks (players start it with none): <c>--game &lt;folder&gt;</c>,
/// <c>--forge &lt;patchForge&gt;</c>, <c>--clip &lt;name|hexId&gt;</c>, <c>--time &lt;s&gt;</c>, <c>--mover on|off</c>,
/// <c>--screenshot &lt;png&gt;</c> (render the window to the file once loaded, then exit) and, any number of times,
/// <c>--shot "&lt;png&gt;|&lt;clip&gt;|&lt;time&gt;|&lt;on|off&gt;"</c> (several pictures from one run; empty parts take the run's own
/// options), <c>--export-skeleton</c> and <c>--export-clip &lt;name|hexId&gt;</c> with <c>--export-out &lt;folder&gt;</c> (the Library
/// screen's exports without their dialog, before any picture). Pictures and exports are made with the window off-screen and never
/// activated.</summary>
public sealed record ViewerArgs
{
    public const string Usage = "GrbAnimTool [--game <folder>] [--forge <patchForge>] [--clip <name|hexId>] [--time <s>] [--mover on|off] "
        + "[--screenshot <png>] [--shot \"<png>|<clip>|<time>|<on|off>\"]... [--export-skeleton] [--export-clip <name|hexId>] [--export-out <folder>] "
        + "[--import-glb <file> --import-clip <name|hexId> [--import-out <folder> [--overwrite]] [--import-keep]] "
        + "[--edit <name|hexId> --edit-kind mirror|scale|speed|trim|swap [--factor <f>] [--from <tick> --to <tick>] [--source <name|hexId>] [--edit-keep]] "
        + "[--package-out <folder> [--package-name <name>]] [--index-cache <folder>] [--package-store <folder>] [--settings <file>] [--size <width>x<height>] [--theme dark|light|system] "
        + "[--log-file <file.csv> | --log-folder <folder>]; a --shot may go on with |library, |edit, |import, |package, |log, |package-waiting, |edit-waiting or |import-waiting (the last three: its time is the progress of what the screen waits for), "
        + "then for the Library |<search>|<who>|<what> (the list's search text and its category filters; on the Log screen a who of Player shows the player's starts only), then |dark, |light or |system (the theme "
        + "from that picture on), then |follows or |own (on the Edit and Import screens, the original at the edit's progress or in its own time, "
        + "from that picture on)";

    /// <summary>The screens a picture can show, by the names <c>--shot</c> takes; <see cref="PackageWaiting"/> is the Package screen as it
    /// stands while the clip index is still being read, <see cref="EditWaiting"/> the Edit screen while the edits each clip allows are
    /// worked out, <see cref="ImportWaiting"/> the Import screen while the clip index is read.</summary>
    public static readonly IReadOnlyList<string> Screens = ["library", "edit", "import", "package", "log", PackageWaiting, EditWaiting, ImportWaiting];

    /// <summary><c>--log-file</c>: the clip log the Log screen opens (read only), in place of following a folder.</summary>
    public string? LogFile { get; init; }

    /// <summary><c>--log-folder</c>: the folder whose newest clip log the Log screen follows, in place of the remembered one (or the
    /// logger's own in the Documents folder, which an automated run never reads: without either option it follows none).</summary>
    public string? LogFolder { get; init; }

    /// <summary>Whether the run gives the Log screen a log to show.</summary>
    public bool Logs => LogFile is not null || LogFolder is not null;

    /// <summary>The Package screen pictured as Build waiting for the clip index shows it: the index held back from the screen for that
    /// picture alone, the shot's time standing for the index's progress (0 to 1).</summary>
    public const string PackageWaiting = "package-waiting";

    /// <summary>The Edit screen pictured as it stands while the edits each clip allows are worked out: they are held back from the screen for
    /// that picture alone, the shot's time standing for their progress (0 to 1); the clip shown, if any, is the run's edit's.</summary>
    public const string EditWaiting = "edit-waiting";

    /// <summary>The Import screen pictured as it stands while the clip index is read: the index held back from the screen for that picture
    /// alone, the shot's time standing for its progress (0 to 1).</summary>
    public const string ImportWaiting = "import-waiting";

    /// <summary>The Edit screen's run: the clip (<c>--edit</c>), the edit (<c>--edit-kind</c>) and its settings: <c>--factor</c> for scale
    /// and speed, <c>--from</c> and <c>--to</c> (source ticks) for trim, <c>--source</c> for swap; with <c>--edit-keep</c> the result goes
    /// into the package list. Pictures then show the Edit screen at the shot's time, unless the shot names another screen.</summary>
    public string? EditClip { get; init; }
    public EditKind? EditKind { get; init; }
    public double? Factor { get; init; }
    public int? From { get; init; }
    public int? To { get; init; }
    public string? Source { get; init; }
    public bool EditKeep { get; init; }

    public bool Edits => EditClip is not null;

    /// <summary>The Package screen's build without its dialog, after any import or edit kept: the save folder and the package's name.</summary>
    public string? PackageOut { get; init; }
    public string? PackageName { get; init; }

    public bool Packages => PackageOut is not null;

    /// <summary>The Import screen's run: the .glb to import and the slot, with <c>--import-out</c> its record file exported there and with
    /// <c>--import-keep</c> kept for the package. Pictures (<c>--shot</c>, <c>--screenshot</c>) then show the Import screen at the shot's
    /// time; the shot's clip part is not used.</summary>
    public string? ImportGlb { get; init; }
    public string? ImportClip { get; init; }
    public string? ImportOut { get; init; }
    public bool ImportKeep { get; init; }
    /// <summary><c>--overwrite</c> with <c>--import-out</c>: files of the same names already in that folder are replaced, as the
    /// screen's "Replace files already there" does; without it they are refused and nothing is written.</summary>
    public bool Overwrite { get; init; }

    public bool Imports => ImportGlb is not null;

    /// <summary><c>--index-cache</c>: the folder the clip index's cache is kept in for this run. Without it a person's run keeps it in
    /// the user's own folder (<see cref="Index.ClipIndexCache.DefaultFolder"/>), and an automated run builds the index for itself and
    /// keeps nothing, as it neither reads nor writes the remembered settings.</summary>
    public string? IndexCache { get; init; }

    /// <summary><c>--package-store</c>: the folder the package list is kept in between runs (<see cref="PackageStore"/>). Without it a
    /// person's run keeps it in the user's own folder (<see cref="PackageStore.DefaultFolder"/>), and an automated run keeps none.</summary>
    public string? PackageStore { get; init; }

    /// <summary><c>--settings</c>: the settings file an automated run reads and writes the Package screen's vanilla forge backup in
    /// (without it such a run remembers none; a person's run uses <see cref="ViewerSettings.DefaultPath"/>).</summary>
    public string? Settings { get; init; }

    /// <summary><c>--size</c>: the window's size for pictures (device-independent pixels), in place of its own; the window's least
    /// size still holds.</summary>
    public (int Width, int Height)? Size { get; init; }

    /// <summary><c>--theme</c>: the theme for this run, in place of the remembered one (which an automated run neither reads nor writes;
    /// without the option it follows Windows).</summary>
    public ThemeChoice? Theme { get; init; }

    public string? Game { get; init; }
    public string? Forge { get; init; }
    public string? Clip { get; init; }
    public float? Time { get; init; }
    public bool? Mover { get; init; }
    public string? Screenshot { get; init; }
    public IReadOnlyList<ShotSpec> ExtraShots { get; init; } = [];
    public bool ExportSkeleton { get; init; }
    public string? ExportClip { get; init; }
    public string? ExportOut { get; init; }

    /// <summary>Whether the run exports and exits.</summary>
    public bool Exports => ExportSkeleton || ExportClip is not null;

    /// <summary>Whether the run takes pictures or exports, then exits: the window then stays off-screen and never takes the focus.</summary>
    public bool Unattended => TakesShots || Exports || Imports || Edits || Packages;

    /// <summary>Every picture to take, in order: <c>--screenshot</c> first, then each <c>--shot</c>.</summary>
    public IReadOnlyList<ShotSpec> Shots
    {
        get
        {
            var l = new List<ShotSpec>();
            if (Screenshot is not null) l.Add(new ShotSpec(Screenshot, null, null, null));
            l.AddRange(ExtraShots);
            return l;
        }
    }

    /// <summary>Whether the run takes pictures and exits (the window then stays off-screen and never takes the focus).</summary>
    public bool TakesShots => Screenshot is not null || ExtraShots.Count > 0;

    /// <summary>Whether the viewer runs under automation: any option given. It then neither reads nor writes the remembered settings.</summary>
    public bool Automated => Game is not null || Forge is not null || Clip is not null || Time is not null || Mover is not null || Unattended || ExportOut is not null || ImportOut is not null
        || IndexCache is not null || PackageStore is not null || Settings is not null || Theme is not null || Size is not null || Logs;

    /// <summary>The screen a picture shows: the shot's own, else the Import screen after an import, the Edit screen after an edit, else the Library.</summary>
    public string ScreenFor(ShotSpec shot) => shot.Screen ?? (Imports ? "import" : Edits ? "edit" : "library");

    /// <summary>Whether a picture shows the Edit screen (whose list marks each clip's edits: the run waits for them first).</summary>
    public bool ShotsShowEdit => Shots.Any(s => ScreenFor(s) is "edit" or EditWaiting);

    /// <summary>Whether the run works every clip's edit marks out: a person's always (the Edit screen may be opened), one that takes
    /// pictures or exports only when a picture shows the Edit screen.</summary>
    public bool WorksMarksOut => !Unattended || ShotsShowEdit;

    /// <summary>Parses the command line; throws <see cref="ArgumentException"/> with a message for the user on anything it doesn't know.</summary>
    public static ViewerArgs Parse(IReadOnlyList<string> args)
    {
        var a = new ViewerArgs();
        var shots = new List<ShotSpec>();
        for (int i = 0; i < args.Count; i++)
        {
            string k = args[i];
            string V() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{k} needs a value. Usage: {Usage}");
            switch (k)
            {
                case "--game": a = a with { Game = V() }; break;
                case "--forge": a = a with { Forge = V() }; break;
                case "--clip": a = a with { Clip = V() }; break;
                case "--time": a = a with { Time = Seconds(V(), k) }; break;
                case "--mover": a = a with { Mover = OnOff(V()) }; break;
                case "--screenshot": a = a with { Screenshot = V() }; break;
                case "--shot": shots.Add(Shot(V())); break;
                case "--export-skeleton": a = a with { ExportSkeleton = true }; break;
                case "--export-clip": a = a with { ExportClip = V() }; break;
                case "--export-out": a = a with { ExportOut = V() }; break;
                case "--import-glb": a = a with { ImportGlb = V() }; break;
                case "--import-clip": a = a with { ImportClip = V() }; break;
                case "--import-out": a = a with { ImportOut = V() }; break;
                case "--import-keep": a = a with { ImportKeep = true }; break;
                case "--overwrite": a = a with { Overwrite = true }; break;
                case "--edit": a = a with { EditClip = V() }; break;
                case "--edit-kind":
                    {
                        string v = V();
                        a = a with
                        {
                            EditKind = v.ToLowerInvariant() switch
                            {
                                "mirror" => Library.EditKind.Mirror,
                                "scale" => Library.EditKind.Scale,
                                "speed" => Library.EditKind.Speed,
                                "trim" => Library.EditKind.Trim,
                                "swap" => Library.EditKind.Swap,
                                _ => throw new ArgumentException($"--edit-kind takes mirror, scale, speed, trim or swap, not '{v}'."),
                            },
                        };
                        break;
                    }
                case "--factor":
                    {
                        string v = V();
                        a = a with { Factor = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && double.IsFinite(f) && f > 0 ? f : throw new ArgumentException($"--factor takes a number above 0, not '{v}'.") };
                        break;
                    }
                case "--from": a = a with { From = Tick(V(), k) }; break;
                case "--to": a = a with { To = Tick(V(), k) }; break;
                case "--source": a = a with { Source = V() }; break;
                case "--edit-keep": a = a with { EditKeep = true }; break;
                case "--package-out": a = a with { PackageOut = V() }; break;
                case "--package-name": a = a with { PackageName = V() }; break;
                case "--index-cache": a = a with { IndexCache = V() }; break;
                case "--package-store": a = a with { PackageStore = V() }; break;
                case "--settings": a = a with { Settings = V() }; break;
                case "--log-file": a = a with { LogFile = V() }; break;
                case "--log-folder": a = a with { LogFolder = V() }; break;
                case "--size":
                    {
                        string v = V();
                        var p = v.Split('x', 'X');
                        a = a with
                        {
                            Size = p.Length == 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                                && w is >= 100 and <= 10000 && h is >= 100 and <= 10000 ? (w, h) : throw new ArgumentException($"--size takes <width>x<height> in pixels, such as 860x520, not '{v}'."),
                        };
                        break;
                    }
                case "--theme":
                    {
                        string v = V();
                        a = a with { Theme = ThemeChoices.Parse(v) ?? throw new ArgumentException($"--theme takes dark, light or system, not '{v}'.") };
                        break;
                    }
                default: throw new ArgumentException($"Unknown option '{k}'. Usage: {Usage}");
            }
        }
        a = a with { ExtraShots = shots };
        if ((a.ImportGlb is null) != (a.ImportClip is null)) throw new ArgumentException("--import-glb and --import-clip go together.");
        if (a.Imports && a.Game is null) throw new ArgumentException("--import-glb needs --game.");
        if ((a.ImportOut is not null || a.ImportKeep) && !a.Imports) throw new ArgumentException("--import-out and --import-keep need --import-glb and --import-clip.");
        if (a.Overwrite && a.ImportOut is null) throw new ArgumentException("--overwrite goes with --import-out: it replaces files of the same names in that folder.");
        if (a.TakesShots && a.Game is null) throw new ArgumentException("--screenshot and --shot need --game.");
        if (a.Exports && (a.Game is null || a.ExportOut is null)) throw new ArgumentException("--export-skeleton and --export-clip need --game and --export-out.");
        if (a.ExportOut is not null && !a.Exports) throw new ArgumentException("--export-out needs --export-skeleton or --export-clip.");
        if ((a.Clip is not null || a.Time is not null || a.Mover is not null || a.Forge is not null || a.IndexCache is not null) && a.Game is null)
            throw new ArgumentException("--forge, --clip, --time, --mover and --index-cache need --game.");
        if (a.Edits)
        {
            if (a.Game is null) throw new ArgumentException("--edit needs --game.");
            switch (a.EditKind)
            {
                case null: throw new ArgumentException("--edit needs --edit-kind mirror, scale, speed, trim or swap.");
                case Library.EditKind.Scale or Library.EditKind.Speed when a.Factor is null: throw new ArgumentException("--edit-kind scale and speed need --factor.");
                case Library.EditKind.Trim when a.From is null || a.To is null: throw new ArgumentException("--edit-kind trim needs --from and --to (ticks).");
                case Library.EditKind.Swap when a.Source is null: throw new ArgumentException("--edit-kind swap needs --source.");
            }
        }
        else if (a.EditKind is not null || a.Factor is not null || a.From is not null || a.To is not null || a.Source is not null || a.EditKeep)
            throw new ArgumentException("--edit-kind, --factor, --from, --to, --source and --edit-keep need --edit.");
        if (a.Packages && a.Game is null) throw new ArgumentException("--package-out needs --game.");
        if (a.PackageName is not null && !a.Packages) throw new ArgumentException("--package-name needs --package-out.");
        if (a.LogFile is not null && a.LogFolder is not null) throw new ArgumentException("--log-file and --log-folder go one at a time: a log opened, or a folder's newest followed.");
        // every picture's path, and a file in the index's cache folder, is guarded here, before the install loads, and again when it is
        // written
        var protect = Export.ClipExport.ProtectedFolders(a.Game, a.Forge);
        foreach (var p in a.Shots.Select(s => s.Png).Append(a.IndexCache is { } cache ? Path.Combine(cache, "graph.json") : null).OfType<string>())
        {
            try { Export.ClipExport.Guard(p, protect); }
            catch (Export.ExportException ex) { throw new ArgumentException(ex.Message, ex); }
        }
        return a;
    }

    static int Tick(string s, string option) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : throw new ArgumentException($"{option} takes a tick (a whole number, 0 or more), not '{s}'.");

    /// <summary>A <c>--shot</c> value: "png|clip|time|on-or-off|screen|search|who|what|theme|follows-or-own", every part after the PNG
    /// optional; a category in any case, given back as the vocabulary spells it.</summary>
    static ShotSpec Shot(string v)
    {
        var p = v.Split('|');
        if (p.Length > 10 || p[0].Trim().Length == 0)
            throw new ArgumentException($"--shot takes \"<png>|<clip>|<time>|<on|off>|<screen>|<search>|<who>|<what>|<theme>|<follows|own>\", not '{v}'.");
        string? Part(int i) => i < p.Length && p[i].Trim().Length > 0 ? p[i].Trim() : null;
        string? screen = Part(4)?.ToLowerInvariant();
        if (screen is not null && !Screens.Contains(screen))
            throw new ArgumentException($"--shot's screen is library, edit, import, package, log, {PackageWaiting}, {EditWaiting} or {ImportWaiting}, not '{Part(4)}'.");
        string? Category(int i, Index.CategoryKind kind)
        {
            if (Part(i) is not { } c) return null;
            return Index.ClipCategories.Of(kind).FirstOrDefault(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"--shot's {kind.ToString().ToLowerInvariant()} is one of {string.Join(", ", Index.ClipCategories.Of(kind))}, not '{c}'.");
        }
        ThemeChoice? theme = Part(8) is { } th
            ? ThemeChoices.Parse(th) ?? throw new ArgumentException($"--shot's theme is dark, light or system, not '{th}'.")
            : null;
        bool? follows = Part(9)?.ToLowerInvariant() switch
        {
            null => null,
            "follows" => true,
            "own" => false,
            _ => throw new ArgumentException($"--shot's last part is follows (the original at the edit's progress) or own (in its own time), not '{Part(9)}'."),
        };
        return new ShotSpec(p[0].Trim(), Part(1), Part(2) is { } t ? Seconds(t, "--shot") : null, Part(3) is { } m ? OnOff(m) : null, screen,
            Part(5), Category(6, Index.CategoryKind.Who), Category(7, Index.CategoryKind.What), theme, follows);
    }

    static bool OnOff(string v) => v.ToLowerInvariant() switch
    {
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException($"--mover takes on or off, not '{v}'."),
    };

    static float Seconds(string s, string option) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) && v >= 0
            ? v : throw new ArgumentException($"{option} takes seconds (0 or more), not '{s}'.");
}
