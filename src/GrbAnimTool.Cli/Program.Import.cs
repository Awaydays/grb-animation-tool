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
