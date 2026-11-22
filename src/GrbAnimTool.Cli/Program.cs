using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GrbAnimTool.Core.Diagnostics;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Oodle;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Cli;

/// <summary>The command-line harness for tests and automation. Every command reads the install read-only.</summary>
static partial class Program
{
    const string Usage = """
        grbanim-cli check <gameFolder> [--forge <patchForge>]
            Opens the game folder: finds the forge, checks the Oodle DLL's pin, loads the animation datafile, prints counts and the census.
        grbanim-cli list <gameFolder> [--forge <patchForge>] [--search <text>] [--armed | --unarmed]
            Lists the clips (index, ID, length, ticks, tracks, status, armed, name) whose name or hex ID matches the search, as the viewer's
            search does (typos forgiven), best match first; in index order without a search. Armed: the clip holds a hand track (an IK
            offset, a hand helper or an add-on hand target); --armed and --unarmed list only those that do or don't.
        grbanim-cli soak <gameFolder> --forge <patchForge> --passes <n> --log <file> [--minutes <m>] [--decode-datafile-each-pass]
            Repeats the full decode pass in one process and logs each pass (the log is capped). With --decode-datafile-each-pass, each pass
            first decodes the animation datafile again through the Oodle library already loaded.
        grbanim-cli hash-table <vanillaForge> --game <gameFolder> --out <file>
            Writes the table of SHA-256 hashes of every clip record of a vanilla forge's animation datafile.
        grbanim-cli graph <gameFolder> [--forge <patchForge>] [--clip <name or hex ID>]
            Reads the animation graph: with --clip, every graph place naming that clip and the edits it allows; without, the summary.
        grbanim-cli export <gameFolder> [--forge <patchForge>] --clip <name|hexId> --out <folder>
            Exports one clip as <name>.glb with its sidecar <name>.grbanim.json, then reads the .glb back and compares it with the
            decoder at every tick (exit code 3 when that control fails).
        grbanim-cli export-skeleton <gameFolder> [--forge <patchForge>] --out <folder> [--name <file.glb>] [--fault axes|thigh]
            Exports the skeleton alone, at its rest pose, and reads it back. --fault writes a deliberately faulty copy (the axes not turned,
            or one thigh 2% long) for the controls that must fail.
        grbanim-cli pose <gameFolder> [--forge <patchForge>] [--clip <name|hexId>] [--ticks <t,t,...>] --out <file.json>
            Writes the tool's own pose of every bone at the ticks given (the rest pose without --clip), in the clip's world with the
            mover applied, for checks made outside the tool.
        grbanim-cli write <gameFolder> [--forge <patchForge>] --clip <name | 0xID> --out <folder> [--record-file <file>]
                [--reencode | --curves <curveSet.json>] [--overwrite]
            Rebuilds a clip (from the game folder, or from a record file of it) with nothing replaced, with every curve of a form with a writer
            written again, or with a curve set's curves, and writes it as a record file named from the game folder's own datafile into <folder>
            (never a folder of the game's, nor a package this tool built or a local undo folder a pre-release build of it wrote). Every
            command that writes a file refuses one inside such a package or local undo folder too.
        grbanim-cli import <gameFolder> [--forge <patchForge>] --glb <file> --clip <name | 0xID> --out <folder> [--report <folder>] [--overwrite]
                [--allow-unchanged]
            Imports a .glb made on the game's skeleton (the tool's export, edited) into the clip's own record, runs the checks,
            and writes the record file and its check report (into --report when given), with every warning, into <folder>
            (never a folder of the game's, the report folder neither), whole or not at all; the warnings also go to the error stream.
            An import whose record is the game's own clip unchanged, or what the game folder already holds for it, is refused unless
            --allow-unchanged (for tests). Exit code 1 when the import is refused (nothing written), 3 when a check fails (the files are
            written) or, rarely, when the files were written but something after it failed: an old file they replaced (with --overwrite)
            couldn't be deleted, or the reports in --report couldn't be written (the message says which).
        grbanim-cli compare <gameFolder> [--forge <patchForge>] --clip <name | 0xID> --record-file <file> [--top <n>]
            Reads a record file back and compares it with the game folder's own clip: per bone, its own rotation and its place in the
            clip's world at their worst tick, the bones that differ most first, then the mover.
        grbanim-cli rebuild-check <gameFolder> [--forge <patchForge>]
            Rebuilds every clip with nothing replaced (each must give its record back byte for byte) and with every writable curve
            written again (each must read back with the same sizes, keys and times).
        grbanim-cli soak <gameFolder> --forge <patchForge> --rebuild --passes <n> --log <file> [--minutes <m>] [--decode-datafile-each-pass]
            Repeats the rebuild pass in one process and logs each pass (the log is capped). Each pass's digest covers every decoded key
            and both rebuilds of every clip. With --decode-datafile-each-pass, each pass first decodes the animation datafile again
            through the Oodle library already loaded.
        grbanim-cli census-edits <gameFolder> [--forge <patchForge>] --out <file> [--features <regex>] [--wide-speeds]
            The edit census: swappable groups, mirrorable clips, the clips trim, speed and scale may take, and every edit run on every
            clip with its refusals; the counts on the console, the counts and lists into the file (capped). With --features, each matching
            clip's tracks, events, mover and verdicts. Then the Edit screen's choices: the clips that allow no edit, by why, and the speed
            slider's steps that work on each clip (with --wide-speeds, also every speed from 0.25 to 4 by 0.05).
        grbanim-cli edit <gameFolder> [--forge <patchForge>] --clip <name|hexId> --edit mirror|scale|speed|trim|swap --out <folder>
                [--factor <f>] [--from <tick> --to <tick>] [--source <name|hexId>] [--overwrite] [--allow-unchanged]
            One edit on one clip (scale is root-motion scale; scale and speed take --factor, to three decimals; trim --from and --to in the
            clip's ticks, swap --source), written as a
            record file named from the game folder's own datafile into <folder> (never a folder of the game's), whole or not at all; the
            edit's account goes to the console. An edit whose record is the game's own
            clip unchanged, or what the game folder already holds for it, is refused unless --allow-unchanged (for tests). Exit code 1 when
            the edit or the write is refused (nothing written), 3 when the files were written but something after it failed (the message
            says what).
        grbanim-cli index <gameFolder> [--forge <patchForge>] [--clip <name|hexId>] [--out <file>] [--cache <folder>]
            The clip index's census: clips per who- and what-category, Uncategorised per kind, how many each source decided (the game's
            graph, the tool's list, the name), readable names; with --clip, that clip's categories with their sources and reasons and the
            graph documents that name it; with --out, also every document's placement and every clip's line (capped). With --cache, the
            graph's part through the cache in that folder, saying whether it was reused or built. Exit code 3 when a graph document
            didn't read.
        """;

    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
            var opts = Options(args.Skip(1).ToArray(), out var pos);
            // the options each command reads: any other (a mistyped name, which would be passed over and the command run without it) is
            // refused before any work
            string[]? reads = args[0] switch
            {
                "check" or "rebuild-check" => ["forge"],
                "list" => ["forge", "search", "armed", "unarmed"],
                "import" => ["forge", "glb", "clip", "out", "report", "overwrite", "allow-unchanged"],
                "compare" => ["forge", "clip", "record-file", "top"],
                "soak" => ["forge", "rebuild", "passes", "log", "minutes", "decode-datafile-each-pass"],
                "hash-table" => ["game", "out"],
                "graph" => ["forge", "clip"],
                "export" => ["forge", "clip", "out"],
                "export-skeleton" => ["forge", "out", "name", "fault"],
                "pose" => ["forge", "clip", "ticks", "out"],
                "write" => ["forge", "clip", "out", "record-file", "reencode", "curves", "overwrite"],
                "census-edits" => ["forge", "out", "features", "wide-speeds"],
                "edit" => ["forge", "clip", "edit", "out", "factor", "from", "to", "source", "overwrite", "allow-unchanged"],
                "index" => ["forge", "clip", "out", "cache"],
                _ => null,
            };
            if (reads is not null && opts.Keys.FirstOrDefault(k => !reads.Contains(k)) is { } unknown)
                return Fail($"error: {args[0]} takes no option --{unknown}");
            return args[0] switch
            {
                "check" when pos.Count == 1 => Check(pos[0], opts.GetValueOrDefault("forge")),
                "list" when pos.Count == 1 => List(pos[0], opts.GetValueOrDefault("forge"), opts.GetValueOrDefault("search") ?? "",
                    opts.ContainsKey("armed") ? true : opts.ContainsKey("unarmed") ? false : null),
                "import" when pos.Count == 1 => Import(pos[0], opts),
                "compare" when pos.Count == 1 => Compare(pos[0], opts),
                "soak" when pos.Count == 1 && opts.ContainsKey("rebuild") => SoakRebuild(pos[0], opts),
                "soak" when pos.Count == 1 => Soak(pos[0], opts),
                "hash-table" when pos.Count == 1 => HashTable(pos[0], Need(opts, "game"), Need(opts, "out")),
                "graph" when pos.Count == 1 => Graph(pos[0], opts.GetValueOrDefault("forge"), opts.GetValueOrDefault("clip")),
                "export" when pos.Count == 1 => Export(pos[0], opts.GetValueOrDefault("forge"), Need(opts, "clip"), Need(opts, "out")),
                "export-skeleton" when pos.Count == 1 => ExportSkeleton(pos[0], opts),
                "pose" when pos.Count == 1 => Pose(pos[0], opts),
                "write" when pos.Count == 1 => Write(pos[0], opts),
                "rebuild-check" when pos.Count == 1 => RebuildCheck(pos[0], opts.GetValueOrDefault("forge")),
                "census-edits" when pos.Count == 1 => CensusEdits(pos[0], opts),
                "edit" when pos.Count == 1 => Edit(pos[0], opts),
                "index" when pos.Count == 1 => IndexCommand(pos[0], opts),
                _ => Fail(Usage),
            };
        }
        catch (Exception ex) when (ex is GameInstallException or OodleException or ArgumentException or ExportException or InvalidOperationException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
        {
            Console.Error.WriteLine($"error: the pattern '{ex.Pattern}' took longer than {ex.MatchTimeout.TotalSeconds:F0} s on a clip name; give a simpler one");
            return 1;
        }
        catch (LogCapReached ex)
        {
            Console.Error.WriteLine("stopped: " + ex.Message);
            return 4;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("error reading or writing a file: " + ex.Message);
            return 5;
        }
    }

    sealed class LogCapReached(string message) : Exception(message);

    static int Int(Dictionary<string, string> o, string k) =>
        int.TryParse(Need(o, k), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) && v > 0
            ? v : throw new ArgumentException($"--{k} needs a whole number above 0");

    static double Minutes(Dictionary<string, string> o) =>
        !o.TryGetValue("minutes", out var m) ? double.PositiveInfinity
        : double.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0
            ? v : throw new ArgumentException("--minutes needs a number above 0");

    static int Fail(string s) { Console.Error.WriteLine(s); return 2; }

    static string Need(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");

    static Dictionary<string, string> Options(string[] a, out List<string> positional)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        positional = [];
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(a[i]); continue; }
            string k = a[i][2..];
            if (k is "decode-datafile-each-pass" or "rebuild" or "reencode" or "overwrite" or "armed" or "unarmed" or "allow-unchanged" or "wide-speeds") { d[k] = "1"; continue; }
            if (i + 1 >= a.Length) throw new ArgumentException($"--{k} needs a value");
            d[k] = a[++i];
        }
        return d;
    }

    static int Check(string game, string? forge)
    {
        using var g = GameInstall.Open(game, forge);
        var t = g.Timings;
        Console.WriteLine($"forge: {g.PatchForgePath}");
        Console.WriteLine($"Oodle: {g.Oodle.DllPath}, SHA-256 {g.Oodle.Sha256} (pinned), "
            + (g.ControlDecodeOn is { } on ? $"control decode passed on {on}" : "no compressed chunk to check: the control decode did not run"));
        Console.WriteLine($"animation datafile: 0x{GameInstall.AnimationDatafileId:X16} '{g.AnimationEntry.Name}', index {g.AnimationDatafileIndex} in the forge (record folder {g.RecordFolderName})");
        Console.WriteLine($"records {g.Records.Count}, clips {g.Clips.Count}");
        var st = g.Clips.GroupBy(c => c.Status).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}");
        Console.WriteLine($"clip status: {string.Join(", ", st)}" + (g.Vanilla is null ? " (no table of the game's clips in this build)" : $"; the game's clips missing here: {g.VanillaClipsMissing}"));
        foreach (var c in g.Clips.Where(c => c.Status is ClipStatus.Replaced or ClipStatus.Added).Take(40))
            Console.WriteLine($"   {c.Status}: {c.Index}_-_{c.Name} 0x{c.Id:X16}");
        Console.WriteLine($"skeleton: body {g.BodySkeleton.Bones.Count} bones, add-on {g.AddOnSkeleton.Bones.Count}, merged {g.Skeleton.Bones.Count}; named {g.Skeleton.Bones.Count(b => g.Names.NameOf(b.Hash) is not null)}");
        Console.WriteLine($"opening took {t.Total.TotalSeconds:F2} s (Oodle {t.Oodle.TotalSeconds:F2}, forge index {t.ForgeIndex.TotalSeconds:F2}, datafile {t.AnimationDatafile.TotalSeconds:F2}, shared table {t.SharedTable.TotalSeconds:F2}, skeletons {t.Skeletons.TotalSeconds:F2}, clips {t.Clips.TotalSeconds:F2})");
        var census = ClipCensus.Run(g);
        foreach (var l in census.Lines()) Console.WriteLine(l);
        GC.Collect();
        var p = Process.GetCurrentProcess();
        Console.WriteLine($"memory: managed {GC.GetTotalMemory(false) / 1048576} MB after a collection, private {p.PrivateMemorySize64 / 1048576} MB, peak working set {p.PeakWorkingSet64 / 1048576} MB");
        return 0;
    }

    static int List(string game, string? forge, string search, bool? armed)
    {
        using var g = GameInstall.Open(game, forge);
        static bool Armed(ClipEntry c) => c.Record is { } r && Core.Checks.HandTracks.IsArmed(r.Tracks);
        var rows = new Core.Library.ClipQuery { Text = search }.Apply(g.Clips, Core.Library.ClipSort.Relevance).Where(c => armed is null || Armed(c) == armed).ToList();
        foreach (var c in rows)
            Console.WriteLine(FormattableString.Invariant($"{c.Index,6} 0x{c.Id:X16} {c.Duration,8:F3} s {c.LastTick,6} {c.TrackCount,4} {c.Status,-8} {(Armed(c) ? "armed" : "-"),-5} {c.Name}")
                + (Armed(c) ? "  hand tracks " + string.Join(" ", Core.Checks.HandTracks.In(c.Record!.Tracks).Select(h => h.ToString("X8"))) : ""));
        Console.WriteLine($"{rows.Count} of {g.Clips.Count} clips");
        return 0;
    }

    static int HashTable(string forge, string game, string outFile)
    {
        var protect = ClipExport.ProtectedFolders(game, forge);
        // with no install open, the guard's content rule runs on the table of the game's clips built into the tool
        ClipExport.Guard(outFile, protect);
        using var oodle = OodleLibrary.Load(game);
        var (e, recs) = GameInstall.ReadAnimationDatafile(forge, oodle);
        var list = recs.Where(r => r.Type == Core.Clips.ClipRecord.Type).Select(r => (r.Id, SHA256.HashData(r.Bytes.Span))).ToList();
        ClipExport.WriteFile(outFile, protect, fs => VanillaClipHashes.Write(fs, list));
        Console.WriteLine($"{forge}: datafile index {e.Index}, {recs.Count} records, {list.Count} clips; wrote {new FileInfo(outFile).Length} B to {outFile}");
        return 0;
    }

    static int Graph(string game, string? forge, string? clip)
    {
        using var g = GameInstall.Open(game, forge);
        var sw = Stopwatch.StartNew();
        var graph = ClipGraph.Read(g);
        foreach (var f in graph.Failures) Console.WriteLine($"not read: 0x{f.DocumentId:X16} {f.Name}: {f.Error}");
        if (clip is null)
        {
            var sinceRead = sw.Elapsed;
            foreach (var l in graph.Census(g.Clips.Select(c => (c.Id, c.Duration))).Lines()) Console.WriteLine(l);
            Console.WriteLine($"read in {sinceRead.TotalSeconds:F2} s");
            return graph.Complete ? 0 : 3;
        }
        var c = FindClip(g, clip);
        var v = graph.Verdict(c.Id);
        Console.WriteLine(FormattableString.Invariant($"{c.Index}_-_{c.Name} 0x{c.Id:X16}, {c.Duration:F3} s"));
        foreach (var u in v.Uses) Console.WriteLine("   " + u.Describe());
        if (!v.HasNodeUse) Console.WriteLine("   no graph node names it");
        string unknown = !v.GraphComplete ? "the graph couldn't be read whole" : "this clip wasn't part of the graph read";
        Console.WriteLine($"motion field: {(!v.MotionFieldKnown ? $"unknown ({unknown})" : v.IsMotionField ? "yes" : "no")}");
        Console.WriteLine($"under a blend's child: {(v.UnderBlendDirectly ? "yes" : v.UnderBlendIndirectly ? $"yes, 1 to {ClipGraph.BlendLevels - 1} levels above the clip node's parent (by the order of the objects)" : "no")}");
        string warned = v.MotionWarning is { } w ? $"allowed with a warning: {w}" : "allowed";
        // the graph's word on the length, then the duration writer's: a record whose byte array holds lists of times can't be retimed yet
        string? writerRefusal = c.Record is not { } rec ? $"the record can't be read ({c.ParseError})"
            : Core.Writing.DurationWriter.CanChange(rec, out string why) ? null : why;
        // each refusal in a modder's words, then its technical detail
        var ctx = new Core.Edits.EditContext(g.Skeleton, g.SharedTable, v);
        string Refused(Core.Library.EditKind k, Core.Library.EditBlock b) => $"{Core.Library.EditReasons.Plain(k, b)} (detail: {b.Detail})";
        string Length(Core.Library.EditKind k, string allowed) => !v.CanChangeLength ? "refused: " + Refused(k, Core.Library.EditJobs.PlanBlock(k, c, ctx, 0)!)
            : writerRefusal is null ? allowed : $"allowed by the graph, refused: {Refused(k, new(Core.Edits.EditRefusal.TimesUnread, writerRefusal))}";
        Console.WriteLine($"trim: {Length(Core.Library.EditKind.Trim, "allowed")}");
        Console.WriteLine($"speed: {Length(Core.Library.EditKind.Speed, warned)}");
        // the graph's word on a change of motion, then the edit's own, as the Edit screen finds it (what the clip holds, then a trial run):
        // a mirror refuses hand tracks, curves of 256 keys or more and a clip with no motion; a scale, a mover that doesn't travel
        string MotionEdit(Core.Library.EditKind k) => !v.CanChangeMotion ? "refused: " + Refused(k, Core.Library.EditJobs.PlanBlock(k, c, ctx, 0)!)
            : (Core.Library.EditJobs.PlanBlock(k, c, ctx, 0) ?? Core.Library.EditJobs.TrialBlock(c, Core.Library.EditJobs.Starting(k, c), ctx, null)) is { } own
                ? $"allowed by the graph, refused: {Refused(k, own)}" : warned;
        Console.WriteLine($"mirror: {MotionEdit(Core.Library.EditKind.Mirror)}");
        Console.WriteLine($"root-motion scale: {MotionEdit(Core.Library.EditKind.Scale)}");
        Console.WriteLine("swap of another length: not offered (a swap must be of the same length)");
        Console.WriteLine($"import, swap: {(v.ReplaceWarning is { } r ? "allowed with a warning: " + r : "allowed")}");
        return graph.Complete ? 0 : 3;
    }

    static ClipEntry FindClip(GameInstall g, string clip) =>
        Core.Library.ClipQuery.Find(g.Clips, clip) ?? throw new ArgumentException($"no clip named or with the ID '{clip}' in this game folder");

    static int Export(string game, string? forge, string clip, string outDir)
    {
        using var g = GameInstall.Open(game, forge);
        var c = FindClip(g, clip);
        var r = ClipExport.Clip(g, c, Path.Combine(outDir, ClipExport.FileNameFor(c.Name)));
        Console.WriteLine($"wrote {r.GlbPath} ({new FileInfo(r.GlbPath).Length} B)");
        Console.WriteLine($"wrote {r.SidecarPath} ({new FileInfo(r.SidecarPath!).Length} B)");
        Console.WriteLine($"{c.Name}: {c.LastTick} ticks, {c.TrackCount} tracks; read back against the decoder: {r.Check}");
        return r.Check.Passed ? 0 : 3;
    }

    static int ExportSkeleton(string game, Dictionary<string, string> o)
    {
        var fault = o.GetValueOrDefault("fault") switch
        {
            null => ExportFault.None,
            "axes" => ExportFault.AxesNotTurned,
            "thigh" => ExportFault.ThighLong,
            var f => throw new ArgumentException($"--fault takes axes or thigh, not '{f}'"),
        };
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        string name = o.GetValueOrDefault("name") ?? ClipExport.SkeletonFileName;
        var r = ClipExport.Skeleton(g, Path.Combine(Need(o, "out"), name), fault);
        Console.WriteLine($"wrote {r.GlbPath} ({new FileInfo(r.GlbPath).Length} B): {g.Skeleton.Bones.Count} bones{(fault == ExportFault.None ? "" : $", deliberately faulty ({fault})")}");
        Console.WriteLine($"read back against the skeleton: {r.Check}");
        // a faulty copy must fail its control
        return r.Check.Passed == (fault == ExportFault.None) ? 0 : 3;
    }

    static int Pose(string game, Dictionary<string, string> o)
    {
        string outFile = Need(o, "out");
        ClipExport.Guard(outFile, ClipExport.ProtectedFolders(game, o.GetValueOrDefault("forge")));
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        ClipExport.Guard(g, outFile);
        var s = g.Skeleton;
        ClipEntry? c = o.TryGetValue("clip", out var name) ? FindClip(g, name) : null;
        var ticks = (o.GetValueOrDefault("ticks") ?? "0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) && v >= 0
                ? v : throw new ArgumentException($"--ticks takes whole numbers 0 or more, not '{t}'")).ToList();
        var sampler = c is null ? null : g.CreateSampler(c);
        if (c is not null && ticks.FirstOrDefault(t => t > c.LastTick, -1) is var over and >= 0) throw new ArgumentException($"tick {over} is past {c.Name}'s last, {c.LastTick}");
        var frames = new List<object>();
        foreach (int t in ticks)
        {
            var model = new Transform[s.Bones.Count];
            var mover = Transform.Identity;
            if (sampler is not null)
            {
                var p = sampler.Sample(t / (float)Core.Clips.ClipRecord.TicksPerSecond);
                p.Model.CopyTo(model, 0);
                mover = p.Mover;
            }
            else s.ComputeModel(s.RestLocals(), model);
            var world = model.Select(m => mover * m).ToList();
            frames.Add(new
            {
                tick = t,
                mover = new { p = V(mover.Position), q = Q(mover.Rotation) },
                p = world.Select(w => V(w.Position)).ToList(),
                q = world.Select(w => Q(w.Rotation)).ToList(),
            });
        }
        var doc = new
        {
            clip = c?.Name,
            lastTick = c?.LastTick ?? 0,
            bones = s.Bones.Select(b => new { name = b.Name, hash = b.Hash.ToString("X8"), parent = b.ParentIndex }).ToList(),
            frames,
        };
        ClipExport.WriteFile(outFile, ClipExport.ProtectedFolders(g), fs => System.Text.Json.JsonSerializer.Serialize(fs, doc));
        Console.WriteLine($"wrote {outFile}: {s.Bones.Count} bones at {ticks.Count} tick(s){(c is null ? " (rest pose)" : $" of {c.Name}")}");
        return 0;

        static float[] V(System.Numerics.Vector3 v) => [v.X, v.Y, v.Z];
        static float[] Q(System.Numerics.Quaternion q) => [q.X, q.Y, q.Z, q.W];
    }

    /// <summary>A log that stops the run once it has written its cap. The path is guarded like every output, before any install is open
    /// (the guard knows the game's own clip files by the table built into the tool, so a folder of them is refused here too), and a file
    /// already there is deleted and made new rather than written into, so a hard link or file link at the path is never followed.</summary>
    sealed class CappedLog(string path, long cap, IReadOnlyList<string> protect) : IDisposable
    {
        readonly StreamWriter _w = new(Fresh(path, protect), new UTF8Encoding(false));

        static FileStream Fresh(string path, IReadOnlyList<string> protect)
        {
            ClipExport.Guard(path, protect);
            if (File.Exists(path)) File.Delete(path);
            ClipExport.Guard(path, protect);
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        long _n;

        public void Say(string s)
        {
            _n += s.Length + 2;
            if (_n > cap) throw new LogCapReached($"the log reached its cap of {cap} B");
            _w.WriteLine(s);
            _w.Flush();
            Console.WriteLine(s);
        }

        public void Dispose() => _w.Dispose();
    }

    static int Soak(string game, Dictionary<string, string> o)
    {
        string forge = Need(o, "forge");
        int passes = Int(o, "passes");
        double minutes = Minutes(o);
        bool decodeEach = o.ContainsKey("decode-datafile-each-pass");
        using var log = new CappedLog(Need(o, "log"), 4_000_000, ClipExport.ProtectedFolders(game, forge));
        var clock = Stopwatch.StartNew();
        log.Say($"# soak start {DateTime.Now:yyyy-MM-ddTHH:mm:ss}; pid {Environment.ProcessId}; runtime {Environment.Version}; gcConcurrent env {Environment.GetEnvironmentVariable("DOTNET_gcConcurrent") ?? "(unset)"}; "
            + $"server GC {System.Runtime.GCSettings.IsServerGC}; datafile decoded each pass {decodeEach}; passes {passes}; minutes {minutes}");
        using var g = GameInstall.Open(game, forge);
        log.Say($"# opened in {g.Timings.Total.TotalSeconds:F2} s: {g.Clips.Count} clips, {g.Skeleton.Bones.Count} bones");
        double? first = null;
        int done = 0;
        for (int i = 1; i <= passes && clock.Elapsed.TotalMinutes < minutes; i++)
        {
            var sw = Stopwatch.StartNew();
            var recs = decodeEach ? g.DecodeAnimationDatafileAgain() : null;
            var r = DecodePass.Run(g, recs);
            first ??= r.Checksum;
            bool same = r.Checksum == first;
            log.Say($"pass {i}: {sw.Elapsed.TotalSeconds:F1} s; clips {r.Clips}, keys {r.Keys}, poses {r.Poses}; checksum {r.Checksum:R}{(same ? "" : " MISMATCH")}; "
                + $"GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}; managed {GC.GetTotalMemory(false) / 1048576} MB");
            if (!same) { log.Say("# stopped: a pass decoded differently"); return 3; }
            done = i;
        }
        log.Say($"# soak done: {done} passes in {clock.Elapsed.TotalMinutes:F1} min");
        return 0;
    }
}
