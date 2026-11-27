using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.Cli;

/// <summary>The import command.</summary>
static partial class Program
{
    /// <summary>import: a .glb made on the game's skeleton written into a slot's own record, checked, and written as a record file with its
    /// check report. Exit code 0 when every check passes or warns, 3 when a check fails (the files are still written)
    /// or the record files were written but the reports apart from them weren't, 1 on a refusal (nothing is written): an import whose
    /// record is the game's own clip unchanged, or what the install already holds for it, is refused unless --allow-unchanged.</summary>
    static int Import(string game, Dictionary<string, string> o)
    {
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        var slot = FindClip(g, Need(o, "clip"));
        string glb = Need(o, "glb"), outDir = Need(o, "out");
        var graph = ClipGraph.Read(g);
        ImportResult r;
        try { r = ClipImport.Run(g, slot, glb, graph); }
        catch (ImportRefusedException ex)
        {
            Console.Error.WriteLine($"refused ({ex.Reason}): {ex.Message}");
            return 1;
        }
        Console.Write(r.ReportText());
        foreach (var w in r.Report.Results.Where(x => x.Level != CheckLevel.Pass)) Console.Error.WriteLine(w.Line);
        // an import that changes nothing would write the game's own record (or the install's own) as a replacement
        if (!o.ContainsKey("allow-unchanged") && PackageEntries.Unchanged(g, slot, r.Record) is { } same)
        {
            Console.Error.WriteLine($"refused (unchanged): {slot.Name}: {same}. Nothing was written.");
            return 1;
        }
        ImportFiles files;
        try { files = ImportOutput.Write(g, outDir, [r], o.ContainsKey("overwrite"), o.GetValueOrDefault("report")); }
        catch (RecordFileException ex) when (ex.FilesWritten) { Console.Error.WriteLine("written, but: " + ex.Message); return 3; }
        catch (RecordFileException ex) { Console.Error.WriteLine("refused: " + ex.Message); return 1; }
        foreach (var f in files.Package.Files) Console.WriteLine($"wrote {f.Path}: {f.Length} B, SHA-256 {f.Sha256}");
        foreach (var p in files.Reports) Console.WriteLine($"wrote {p}");
        Console.WriteLine($"install: copy the record file into the folder the unpack of the animation datafile makes, {files.Package.DatafileFolderName} on this install ({RecordFile.DatafileFolderName(24, g.AnimationEntry.Name)} on the 2023 game files)");
        return r.Report.Verdict == CheckLevel.Fail ? 3 : 0;
    }

    /// <summary>replace: another clip's motion (--source) or a .glb's (--glb) put on a clip, fitted to its exact length (--fit cut, speed or
    /// hold, a cut from --start, or the suggested start when none is given), with --loop [frames] and --root-scale; written as a record file
    /// with its check report, as import writes one. Exit codes as import's.</summary>
    static int Replace(string game, Dictionary<string, string> o)
    {
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        var slot = FindClip(g, Need(o, "clip"));
        string outDir = Need(o, "out");
        var src = o.TryGetValue("source", out var sn) ? FindClip(g, sn) : null;
        string? glb = o.GetValueOrDefault("glb");
        if ((src is null) == (glb is null)) return Fail("error: replace takes one of --source <clip> and --glb <file>");
        var mode = o.GetValueOrDefault("fit") switch
        {
            null or "cut" => FitMode.Cut, "speed" => FitMode.Speed, "hold" => FitMode.Hold,
            var x => throw new ArgumentException($"--fit {x}: cut, speed or hold"),
        };
        int loopFrames = o.TryGetValue("loop", out var lf) && int.TryParse(lf, out int lv) ? lv : ClipFit.DefaultLoopFrames;
        double root = o.TryGetValue("root-scale", out var rs) ? double.Parse(rs, System.Globalization.CultureInfo.InvariantCulture) : 1;
        var fit = new FitOptions { Mode = mode, Loop = o.ContainsKey("loop"), LoopFrames = loopFrames, RootScale = root, KeepFeet = KeepFeet(o) };
        var graph = ClipGraph.Read(g);
        ImportResult r;
        try
        {
            if (g.ReplaceProblem(slot, src) is { } why) throw new ImportRefusedException(why, src is null ? ImportRefusal.Slot : ImportRefusal.File);
            var sk = g.SkeletonOf(slot)!;
            ImportResult Run(FitOptions f) => src is not null ? ClipReplace.FromClip(sk, g.SharedTable, slot, src, f, graph.Verdict(slot.Id), true)
                : ClipImport.Run(g, slot, glb!, graph, f);
            if (o.TryGetValue("start", out var st)) fit = fit with { CutStart = int.Parse(st, System.Globalization.CultureInfo.InvariantCulture) };
            else if (mode == FitMode.Cut && Run(fit with { Mode = FitMode.Hold, Loop = false }).Unfitted is { } u && ClipFit.SuggestCut(u, slot.LastTick) is int at)
            {
                fit = fit with { CutStart = at };
                Console.WriteLine($"suggested cut start: frame {at}");
            }
            r = Run(fit);
        }
        catch (ImportRefusedException ex)
        {
            Console.Error.WriteLine($"refused ({ex.Reason}): {ex.Message}");
            return 1;
        }
        Console.Write(r.ReportText());
        var (filled, dropped, kept) = ClipReplace.TrackMap(r, g.SkeletonOf(slot) ?? g.Skeleton);
        Console.WriteLine($"track map: filled with the rest pose {filled.Count} ({string.Join(", ", filled)}); dropped {dropped.Count} ({string.Join(", ", dropped)}); kept {kept.Count}");
        foreach (var w in r.Report.Results.Where(x => x.Level != CheckLevel.Pass)) Console.Error.WriteLine(w.Line);
        if (PackageEntries.Unchanged(g, slot, r.Record) is { } same)
        {
            Console.Error.WriteLine($"refused (unchanged): {slot.Name}: {same}. Nothing was written.");
            return 1;
        }
        ImportFiles files;
        try { files = ImportOutput.Write(g, outDir, [r], o.ContainsKey("overwrite"), o.GetValueOrDefault("report")); }
        catch (RecordFileException ex) when (ex.FilesWritten) { Console.Error.WriteLine("written, but: " + ex.Message); return 3; }
        catch (RecordFileException ex) { Console.Error.WriteLine("refused: " + ex.Message); return 1; }
        foreach (var f in files.Package.Files) Console.WriteLine($"wrote {f.Path}: {f.Length} B, SHA-256 {f.Sha256}");
        foreach (var p in files.Reports) Console.WriteLine($"wrote {p}");
        return r.Report.Verdict == CheckLevel.Fail ? 3 : 0;
    }

    /// <summary>--keep-feet on (the default) or off: keep planted feet in place during the tool's blends.</summary>
    static bool KeepFeet(Dictionary<string, string> o) => o.GetValueOrDefault("keep-feet") switch
    {
        null or "on" => true, "off" => false,
        var x => throw new ArgumentException($"--keep-feet {x}: on or off"),
    };

    /// <summary>sets: the sets the graph holds (and the probable sets by name), or with --clip the sets that clip is in, each with its kind,
    /// its place, its blend and its clips.</summary>
    static int Sets(string game, Dictionary<string, string> o)
    {
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        var sets = ClipSets.ReadAll(g, ClipGraph.Read(g));
        var names = g.AllClips.GroupBy(c => c.Id).ToDictionary(x => x.Key, x => x.First().Name);
        string N(ulong id) => names.GetValueOrDefault(id, $"0x{id:X16}");
        var shown = o.TryGetValue("clip", out var cn) ? sets.Of(FindClip(g, cn).Id) : sets.All;
        Console.WriteLine($"states read {sets.StatesRead}, not read {sets.StatesUnread}; sets {sets.All.Count}: "
            + string.Join(", ", sets.All.GroupBy(s => s.Kind).Select(x => $"{x.Key} {x.Count()}")));
        foreach (var s in shown)
            Console.WriteLine(FormattableString.Invariant($"{s.Words}, {s.Where}{(s.Kind == ClipSetKind.Blended ? "" : s.BlendSeconds is double b ? $", blend {b} s (the graph's)" : $", blend {ClipSet.DefaultBlendSeconds} s (not read)")}: {string.Join(", ", s.Clips.Select(N))}"));
        return 0;
    }

    /// <summary>replace-set: Replace for a set, the set of --set's clip (its first set) or the clips of --clips (comma-separated), from one
    /// source (--source or --glb), fitted as replace fits; each clip its own piece of the source unless --same-piece; lined up unless
    /// --no-line-up. Every record file and report written as replace writes one. Exit codes as replace's.</summary>
    static int ReplaceSet(string game, Dictionary<string, string> o)
    {
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        var graph = ClipGraph.Read(g);
        List<ClipEntry> slots;
        bool blended = false;
        int switchTicks = SetReplaceOptions.DefaultSwitchTicks;
        if (o.TryGetValue("set", out var sn))
        {
            var sets = ClipSets.ReadAll(g, graph);
            var set = sets.Main(FindClip(g, sn).Id) ?? throw new ArgumentException($"{sn} is in no set");
            // a blended set needs matching poses: the same piece for every clip, as the Edit screen does
            blended = set.Kind == ClipSetKind.Blended;
            if (set.BlendSeconds is double bs) switchTicks = (int)Math.Round(bs * Core.Clips.ClipRecord.TicksPerSecond);
            slots = set.Clips.Select(id => g.FindClip(id) ?? throw new ArgumentException($"0x{id:X16} isn't in the install")).ToList();
            Console.WriteLine($"{set.Words} at {set.Where}: {string.Join(", ", slots.Select(c => c.Name))}");
        }
        else slots = Need(o, "clips").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(n => FindClip(g, n)).ToList();
        var src = o.TryGetValue("source", out var srcName) ? SetSource.Of(FindClip(g, srcName)) : o.TryGetValue("glb", out var glb) ? SetSource.OfGlb(glb) : null;
        if (src is null || (o.ContainsKey("source") && o.ContainsKey("glb"))) return Fail("error: replace-set takes one of --source <clip> and --glb <file>");
        var mode = o.GetValueOrDefault("fit") switch
        {
            null or "cut" => FitMode.Cut, "speed" => FitMode.Speed, "hold" => FitMode.Hold,
            var x => throw new ArgumentException($"--fit {x}: cut, speed or hold"),
        };
        int loopFrames = o.TryGetValue("loop", out var lf) && int.TryParse(lf, out int lv) ? lv : ClipFit.DefaultLoopFrames;
        double root = o.TryGetValue("root-scale", out var rs) ? double.Parse(rs, System.Globalization.CultureInfo.InvariantCulture) : 1;
        IReadOnlyList<int?>? starts = null;
        if (o.TryGetValue("starts", out var sts))
        {
            // one entry a clip, in the set's order; "-" leaves that clip's start to the suggestion
            starts = sts.Split(',', StringSplitOptions.TrimEntries).Select(x => x == "-" ? (int?)null : int.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            if (starts.Count != slots.Count) return Fail($"error: --starts gives {starts.Count} starts for {slots.Count} clips");
        }
        if (o.TryGetValue("blend-ticks", out var bt)) switchTicks = int.Parse(bt, System.Globalization.CultureInfo.InvariantCulture);
        var opts = new SetReplaceOptions
        {
            Fit = new FitOptions { Mode = mode, Loop = o.ContainsKey("loop"), LoopFrames = loopFrames, RootScale = root, KeepFeet = KeepFeet(o) },
            DistinctPieces = !o.ContainsKey("same-piece") && !blended,
            LineUp = !o.ContainsKey("no-line-up"),
            Starts = starts,
            SwitchTicks = switchTicks,
            PoseSource = o.ContainsKey("pose-frame") ? src : null,
            PoseFrame = o.TryGetValue("pose-frame", out var pf) ? int.Parse(pf, System.Globalization.CultureInfo.InvariantCulture) : 0,
        };
        SetReplaceResult sr;
        try { sr = ClipSetReplace.Run(g, graph, slots, [src], opts); }
        catch (ImportRefusedException ex) { Console.Error.WriteLine($"refused ({ex.Reason}): {ex.Message}"); return 1; }
        foreach (var n in sr.Notes) Console.WriteLine(n);
        var seams = ClipSetReplace.Seams(sr.Results);
        for (int i = 0; i < seams.Count; i++)
            Console.WriteLine(FormattableString.Invariant($"seam {slots[i].Name} -> {slots[(i + 1) % slots.Count].Name}: {seams[i]:F2} (pose distance, the end against the next start)"));
        foreach (var r in sr.Results)
            if (r.Report.Results.FirstOrDefault(x => x.Check == Feet.SlideCheck) is { } fs) Console.WriteLine($"feet {r.Slot.Name}: {fs.Line}");
        foreach (var r in sr.Results)
        {
            Console.Write(r.ReportText());
            if (PackageEntries.Unchanged(g, r.Slot, r.Record) is { } same) { Console.Error.WriteLine($"refused (unchanged): {r.Slot.Name}: {same}. Nothing was written."); return 1; }
        }
        ImportFiles files;
        try { files = ImportOutput.Write(g, Need(o, "out"), sr.Results, o.ContainsKey("overwrite"), o.GetValueOrDefault("report")); }
        catch (RecordFileException ex) when (ex.FilesWritten) { Console.Error.WriteLine("written, but: " + ex.Message); return 3; }
        catch (RecordFileException ex) { Console.Error.WriteLine("refused: " + ex.Message); return 1; }
        foreach (var f in files.Package.Files) Console.WriteLine($"wrote {f.Path}: {f.Length} B, SHA-256 {f.Sha256}");
        foreach (var p in files.Reports) Console.WriteLine($"wrote {p}");
        return sr.Results.Any(r => r.Report.Verdict == CheckLevel.Fail) ? 3 : 0;
    }

    /// <summary>compare: a record file read back by the core against the install's own clip of the same ID: per bone, how far its local
    /// rotation and its place in the clip's world differ, at their worst tick, the bones that differ most first; then the mover.</summary>
    static int Compare(string game, Dictionary<string, string> o)
    {
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        var slot = FindClip(g, Need(o, "clip"));
        string rf = Need(o, "record-file");
        var file = File.ReadAllBytes(rf);
        if (RecordFile.IdOf(file) != slot.Id) throw new ArgumentException($"{rf} doesn't hold clip 0x{slot.Id:X16}");
        Core.Clips.ClipRecord rec;
        try { rec = Core.Clips.ClipRecord.Parse(RecordFile.ToRecord(file, slot.Name)); }
        catch (Exception ex) when (ex is RecordFileException or InvalidDataException) { throw new ArgumentException($"{rf} can't be read: {ex.Message}"); }
        if (!rec.WalkedToEnd) throw new ArgumentException($"{rf}'s curves don't read to the record's end: {rec.Stop}");
        var a = Motion.FromClip(g.Skeleton, slot.Record!, g.SharedTable);
        var b = Motion.FromClip(g.Skeleton, rec, g.SharedTable);
        Console.WriteLine($"{rf}: {file.Length} B, SHA-256 {Core.Hashing.Sha256Hex.Of(file)}; reads as {rec.Name} 0x{rec.Id:X16}, {rec.LastTick} ticks, {rec.Tracks.Count} tracks, its curves to the record's end");
        Console.WriteLine($"against the install's {slot.Name} ({slot.Status}): {slot.LastTick} ticks, {slot.TrackCount} tracks; the same track list: {(rec.Tracks.SequenceEqual(slot.Record!.Tracks) ? "yes" : "NO")}");
        if (slot.Status != ClipStatus.Vanilla)
            Console.WriteLine($"   the install's clip isn't the game's own ({slot.Status}): to compare with the game's clip, give --forge the path of an untouched DataPC_patch_01.forge");
        if (a.LastTick != b.LastTick) { Console.WriteLine("the lengths differ"); return 3; }
        var rows = new List<(string Bone, double Deg, int DegTick, double Cm, int CmTick)>();
        for (int i = 0; i < g.Skeleton.Bones.Count; i++)
        {
            double d = 0, c = 0; int dt = 0, ct = 0;
            for (int t = 0; t <= a.LastTick; t++)
            {
                double x = Core.Export.GltfAxes.AngleDeg(a.Local(t, i).Rotation, b.Local(t, i).Rotation);
                double y = System.Numerics.Vector3.Distance(a.World(t, i).Position, b.World(t, i).Position) * 100;
                if (x > d) { d = x; dt = t; }
                if (y > c) { c = y; ct = t; }
            }
            rows.Add((g.Skeleton.Bones[i].Name, d, dt, c, ct));
        }
        int changed = rows.Count(r => r.Deg > 0.1), moved = rows.Count(r => r.Cm > 0.1);
        Console.WriteLine($"bones whose own rotation differs by more than 0.1 deg somewhere: {changed} of {rows.Count}; whose place in the world differs by more than 0.1 cm: {moved}");
        foreach (var r in rows.OrderByDescending(r => r.Deg).Take(o.TryGetValue("top", out var top) && int.TryParse(top, out int n) ? n : 12))
            Console.WriteLine(FormattableString.Invariant($"   {r.Bone,-22} own rotation up to {r.Deg,7:F2} deg (tick {r.DegTick,4}); place in the world up to {r.Cm,6:F2} cm (tick {r.CmTick,4})"));
        double md = 0, mc = 0;
        for (int t = 0; t <= a.LastTick; t++)
        {
            md = Math.Max(md, Core.Export.GltfAxes.AngleDeg(a.Mover(t).Rotation, b.Mover(t).Rotation));
            mc = Math.Max(mc, System.Numerics.Vector3.Distance(a.Mover(t).Position, b.Mover(t).Position) * 100);
        }
        Console.WriteLine(FormattableString.Invariant($"the mover: up to {md:F3} deg and {mc:F3} cm"));
        return 0;
    }
}
