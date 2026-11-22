using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Retiming;

namespace GrbAnimTool.Cli;

/// <summary>The edits' commands: census-edits and edit.</summary>
static partial class Program
{
    /// <summary>census-edits: the edit census of the install, its counts on the console, the counts and every list into a capped file.</summary>
    static int CensusEdits(string game, Dictionary<string, string> o)
    {
        string outFile = Need(o, "out");
        // the output is refused in the game's folders before the long run, and again once the install is open
        ClipExport.Guard(outFile, ClipExport.ProtectedFolders(game, o.GetValueOrDefault("forge")));
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        ClipExport.Guard(g, outFile);
        Regex? features = o.TryGetValue("features", out var fx) ? new Regex(fx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) : null;
        var sw = Stopwatch.StartNew();
        var graph = ClipGraph.Read(g);
        var census = EditCensus.Run(g, graph);
        var log = new QuietLog(outFile, 40_000_000, ClipExport.ProtectedFolders(g));
        try
        {
            CensusLines(g, census, log, sw, features, fx);
            ChoiceLines(g, graph, log, o.ContainsKey("wide-speeds"));
        }
        finally { log.Save(); }
        Console.WriteLine($"wrote {outFile}");
        int faults = EditCensus.EditNames.Sum(ed => census.Faults(ed).Count());
        return faults == 0 ? 0 : 3;
    }

    static void CensusLines(GameInstall g, EditCensus census, QuietLog log, Stopwatch sw, Regex? features, string? fx)
    {
        log.Say($"# edit census of {g.PatchForgePath}: {g.Clips.Count} clips, run in {sw.Elapsed.TotalSeconds:F1} s");
        foreach (var l in census.Summary()) { log.Say(l); Console.WriteLine(l); }
        log.Say("");
        log.Say("## swappable groups (one track list, every hash and kind, one length): ticks, then the clips");
        foreach (var grp in census.SwapGroups)
            log.Say($"{grp[0].Ticks} ticks, {grp.Count} clips: {string.Join(", ", grp.Select(c => c.Clip.Name))}");
        foreach (var ed in EditCensus.EditNames)
        {
            log.Say("");
            log.Say($"## {ed}: refused for an arena kind the head lacks or that would drop to 0");
            foreach (var c in census.Clips.Where(c => c.Edits.TryGetValue(ed, out var r) && r.Kind is EditRefusal.ArenaKindMissing or EditRefusal.ArenaKindToZero))
                log.Say($"{c.Clip.Index}_-_{c.Clip.Name}: {c.Edits[ed].Why}");
            log.Say($"## {ed}: other refusals (motion field, length held, hand tracks, no root motion and times in the byte array are counted above, not listed)");
            foreach (var c in census.Clips.Where(c => c.Edits.TryGetValue(ed, out var r) && r.Kind is { } k && k is not (EditRefusal.ArenaKindMissing or EditRefusal.ArenaKindToZero
                         or EditRefusal.MotionField or EditRefusal.LengthHeld or EditRefusal.HandTracks or EditRefusal.NoRootMotion or EditRefusal.TimesUnread)))
                log.Say($"{c.Clip.Index}_-_{c.Clip.Name}: {c.Edits[ed].Kind}: {c.Edits[ed].Why}");
            log.Say($"## {ed}: faults");
            foreach (var c in census.Faults(ed)) log.Say($"{c.Clip.Index}_-_{c.Clip.Name}: {c.Edits[ed].Why}");
        }
        log.Say("");
        log.Say("## mirrorable clips (no hand track, no motion field) the mirror succeeds on");
        foreach (var c in census.Clips.Where(c => c.Edits.TryGetValue(EditCensus.Mirror, out var r) && r.Kind is null && r.Why is null)) log.Say($"{c.Clip.Index}_-_{c.Clip.Name}");
        log.Say("");
        log.Say("## clips holding the track 1087C86F: how far it stands from the ground marker's relation to the mover");
        foreach (var c in census.Clips.Where(c => c.Marker)) log.Say(FormattableString.Invariant($"{c.Clip.Index}_-_{c.Clip.Name}: {c.MarkerFitCm:F2} cm"));
        if (features is not null)
        {
            log.Say("");
            log.Say($"## features of the clips matching '{fx}'");
            foreach (var c in census.Clips.Where(c => c.Walked && features.IsMatch(c.Clip.Name)))
                log.Say($"{c.Clip.Index}_-_{c.Clip.Name}: {c.Features()}; " + string.Join("; ", EditCensus.EditNames.Select(ed => $"{ed} {(c.Edits[ed].Kind?.ToString() ?? (c.Edits[ed].Why is null ? "ok" : "FAULT"))}")));
        }
    }

    /// <summary>The Edit screen's choices on every clip (<see cref="Core.Library.InstallEditChoices"/>): the clips that allow no edit, by
    /// why; the speed slider's steps that work (<see cref="Core.Library.FactorChoices"/>) on the clips the speed change may take: at
    /// every step, at some (with what refuses the others), at none; the same for the cuts of the trim's grid
    /// (<see cref="Core.Library.TrimChoices"/>) and the root-motion scale's sample. With <paramref name="wide"/> the speeds are tried
    /// again over the speed change's whole range (0.25 to 4 by 0.05).</summary>
    static void ChoiceLines(GameInstall g, ClipGraph graph, QuietLog log, bool wide)
    {
        var sw = Stopwatch.StartNew();
        var swaps = Core.Library.SwapSources.Build(g.Clips, g.SharedTable);
        var mirror = new SkeletonMirror(g.Skeleton);
        // the screen's own pass (each slider and the trim stop at the first setting that works), then every setting of each
        var screen = Core.Library.InstallEditChoices.Of(g, graph, swaps, mirror, CancellationToken.None);
        double screenSeconds = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var all = Core.Library.InstallEditChoices.Of(g, graph, swaps, mirror, CancellationToken.None, full: true);
        void Say(string s) { log.Say(s); Console.WriteLine(s); }
        log.Say("");
        Say($"## the Edit screen's choices on every clip (the screen's pass {screenSeconds:F1} s, allowing no edit {screen.NoEdit}; every setting {sw.Elapsed.TotalSeconds:F1} s)");
        Say($"clips allowing no edit: {all.NoEdit} of {all.Count}");
        foreach (var (reasons, n) in all.NoEditByReason()) Say($"   {n}: {reasons}");
        foreach (var k in Enum.GetValues<Core.Library.EditKind>())
            Say($"   {Core.Library.EditJobs.Name(k)} allowed on {all.All.Count(c => c[k].Available)}");
        if (all.Faults > 0) Say($"   clips the tool failed on for some edit: {all.Faults}");
        // clips an error of the moment (memory short) left out: said, never a census silently short
        int clips = Core.Library.InstallEditChoices.ClipsOf(g).Count;
        foreach (var (pass, r) in new[] { ("the screen's pass", screen), ("every setting", all) })
            if (r.Transient is { } t) Say($"   clips left out of {pass} by an error of the moment: {clips - r.Count} ({t.GetType().Name}: {t.Message})");
        SpeedLines("the speed slider's steps (0.5 to 2 by 0.05, 1 left out)", all.All.Select(c => c.Speeds).Where(s => s.Grid.Count > 0).ToList(), Say);
        SpeedLines("the root-motion scale slider's steps (0.25 to 2 by 0.05, 1 left out)", all.All.Select(c => c.Scales).Where(s => s.Grid.Count > 0).ToList(), Say);
        var trims = all.All.Select(c => c.Trims).Where(t => t.Grid.Count > 0).ToList();
        Say($"cuts over the trim's grid (each end kept, or cut by a tenth, a quarter or a half), on the {trims.Count} clips the trim may take: every cut works on "
            + $"{trims.Count(t => t.All)}, some on {trims.Count(t => !t.All && t.Works.Count > 0)} (only those keeping the start on {trims.Count(t => !t.All && t.StartOnly)}, "
            + $"only those keeping the end on {trims.Count(t => !t.All && t.EndOnly)}), none on {trims.Count(t => t.Works.Count == 0)}");
        if (!wide) return;
        sw.Restart();
        var grid = Core.Library.EditJobs.Grid(RetimeEdit.MinSpeed, RetimeEdit.MaxSpeed, Core.Library.EditJobs.SpeedStep);
        var bag = new System.Collections.Concurrent.ConcurrentBag<Core.Library.FactorChoices>();
        Parallel.ForEach(g.Clips, c =>
        {
            var s = Core.Library.EditJobs.Speeds(c, new EditContext(g.Skeleton, g.SharedTable, graph.Verdict(c.Id)), CancellationToken.None, grid, parallelism: 1);
            if (s.Grid.Count > 0) bag.Add(s);
        });
        SpeedLines($"the speed change's whole range (0.25 to 4 by 0.05, 1 left out; {sw.Elapsed.TotalSeconds:F1} s)", bag.ToList(), Say);
    }

    static void SpeedLines(string what, IReadOnlyList<Core.Library.FactorChoices> speeds, Action<string> say)
    {
        say($"{(speeds.FirstOrDefault()?.Kind == Core.Library.EditKind.Scale ? "scales" : "speeds")} over {what}, on the {speeds.Count} clips the edit may take: every step works on {speeds.Count(s => s.All)}, "
            + $"some on {speeds.Count(s => !s.All && s.Works.Count > 0)}, none on {speeds.Count(s => s.Works.Count == 0)}");
        // what refuses the steps that don't work, per clip (a clip counted once per kind, an arena kind by its number)
        var by = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in speeds.Where(s => s.Refused.Count > 0))
            foreach (var key in s.Refused.Select(r => r.Block.Kind == EditRefusal.ArenaKindMissing
                         && Regex.Match(r.Block.Detail, @"arena kind (\d+)") is { Success: true } m ? $"ArenaKindMissing {m.Groups[1].Value}" : r.Block.Kind?.ToString() ?? "fault").Distinct())
                by[key] = by.GetValueOrDefault(key) + 1;
        foreach (var (k, n) in by.OrderByDescending(x => x.Value)) say($"   clips refused at some step for {k}: {n}");
    }

    /// <summary>edit: one edit on one clip of the install, written as a record file, its account on the console. An edit whose record is the
    /// game's own clip unchanged, or what the install already holds for it, is refused unless --allow-unchanged.</summary>
    static int Edit(string game, Dictionary<string, string> o)
    {
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        var e = FindClip(g, Need(o, "clip"));
        var graph = ClipGraph.Read(g);
        var ctx = new EditContext(g.Skeleton, g.SharedTable, graph.Verdict(e.Id));
        var clip = e.Record ?? throw new ArgumentException($"{e.Name} can't be read: {e.ParseError}");
        string what = Need(o, "edit");
        double? factor = what is "scale" or "speed" ? Float(o, "factor") : null;
        int? from = what == "trim" ? Ticks(o, "from") : null, to = what == "trim" ? Ticks(o, "to") : null;
        ClipRecord? source = null;
        if (what == "swap") { var src = FindClip(g, Need(o, "source")); source = src.Record ?? throw new ArgumentException($"{src.Name} can't be read"); }
        EditedClip r;
        string desc;
        try { r = EditPackage.Run(clip, ctx, what, out desc, factor, from, to, source); }
        catch (EditRefusedException ex)
        {
            // in a modder's words, then the technical detail
            int at = Array.IndexOf(EditPackage.Edits, what);
            if (at >= 0) Console.Error.WriteLine("refused: " + Core.Library.EditReasons.Plain((Core.Library.EditKind)at, ex.Kind));
            Console.Error.WriteLine($"refused ({ex.Kind}): {ex.Message}");
            return 1;
        }
        // an edit that changes nothing would write the game's own record (or the install's own) as a replacement; said as an edit's
        // reason, never with the import's way out
        if (!o.ContainsKey("allow-unchanged") && Core.Package.PackageEntries.Unchanged(g, e, r.Bytes, Core.Package.PackageItemKind.Edit) is { } same)
        {
            Console.Error.WriteLine($"refused (unchanged): {e.Name}: {same}. Nothing was written.");
            return 1;
        }
        RecordPackage pkg;
        // the record file, written whole or not at all into a folder the guard allows
        try { pkg = EditPackage.Write(g, Need(o, "out"), e, r, o.ContainsKey("overwrite")); }
        // the files are in place but something after failed (an old copy left beside them, say): never said to be unwritten
        catch (RecordFileException ex) when (ex.FilesWritten) { Console.Error.WriteLine("written, but: " + ex.Message); return 3; }
        catch (RecordFileException ex) { Console.Error.WriteLine("refused: " + ex.Message); return 1; }
        var f0 = pkg.Files[0];
        foreach (var l in EditPackage.AccountLines(e, r, desc, Path.GetFileName(g.PatchForgePath))) Console.WriteLine("   " + l);
        Console.WriteLine($"wrote {f0.Path}: {f0.Length} B, SHA-256 {f0.Sha256}");
        return 0;
    }

    /// <summary>A file log that stops the run once it has written its cap, writing nothing to the console. Its lines are kept until
    /// <see cref="Save"/>, which writes them through the guard with the one protected set: a temporary file beside the path, moved over it,
    /// so a hard link or file link at the path is replaced, never followed.</summary>
    sealed class QuietLog(string path, long cap, IReadOnlyList<string> protect)
    {
        readonly StringBuilder _s = new();
        long _n;

        public void Say(string s)
        {
            _n += s.Length + 2;
            if (_n > cap) throw new LogCapReached($"the file reached its cap of {cap} B");
            _s.AppendLine(s);
        }

        public void Save() => ClipExport.WriteFile(path, protect, fs =>
        {
            using var w = new StreamWriter(fs, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
            w.Write(_s);
        });
    }

    static float Float(Dictionary<string, string> o, string k) =>
        float.TryParse(Need(o, k), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : throw new ArgumentException($"--{k} needs a number");

    static int Ticks(Dictionary<string, string> o, string k) =>
        int.TryParse(Need(o, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0 ? v : throw new ArgumentException($"--{k} needs a whole number of ticks, 0 or more");
}
