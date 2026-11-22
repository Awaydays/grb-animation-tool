using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Diagnostics;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Cli;

/// <summary>The write path's commands: write, rebuild-check and the rebuild soak.</summary>
static partial class Program
{
    /// <summary>write: a clip rebuilt, with nothing replaced, with every curve of a form with a writer written again, or with the curves of
    /// a curve set, then written as a record file named from the install's own datafile. Exit code 1 when refused (nothing written), 3 when
    /// the files were written but an old file they replaced couldn't be deleted.</summary>
    static int Write(string game, Dictionary<string, string> o)
    {
        using var g = GameInstall.Open(game, o.GetValueOrDefault("forge"));
        var e = FindClip(g, Need(o, "clip"));
        byte[] source = e.Bytes.ToArray();
        string from = $"record {e.Index} of the install's animation datafile";
        if (o.TryGetValue("record-file", out var rf))
        {
            var file = File.ReadAllBytes(rf);
            if (RecordFile.IdOf(file) != e.Id) throw new ArgumentException($"{rf} doesn't hold clip 0x{e.Id:X16}");
            try { source = RecordFile.ToRecord(file, e.Name); }
            catch (RecordFileException ex) { throw new ArgumentException($"{rf}: {ex.Message}"); }
            from = $"the record file {rf} ({file.Length} B, SHA-256 {Sha256Hex.Of(file)})";
        }
        RebuiltClip built;
        string how;
        try
        {
            var clip = ClipRecord.Parse(source);
            if (o.ContainsKey("reencode"))
            {
                built = ClipRebuild.Rebuild(clip, g.SharedTable, (_, _, u) => ReferenceEquals(u.Source, g.SharedTable.Bytes) ? null : CurveReencoder.Reencode(u));
                how = "every inline curve of a form with a writer decoded and written again";
            }
            else if (o.TryGetValue("curves", out var cs))
            {
                built = ClipRebuild.Rebuild(clip, g.SharedTable, ReadCurveSet(cs, clip.Tracks.Count));
                how = $"the curves of {cs}";
            }
            else
            {
                built = ClipRebuild.Rebuild(clip, g.SharedTable);
                how = "nothing replaced";
            }
        }
        catch (InvalidDataException ex) { throw new ArgumentException($"{e.Name}: the record can't be read: {ex.Message}"); }
        catch (RebuildRefusedException ex) { Console.Error.WriteLine("refused: " + ex.Message); return 1; }
        bool same = built.Bytes.AsSpan().SequenceEqual(source);
        Console.WriteLine($"clip: {e.Index}_-_{e.Name} 0x{e.Id:X16}, {e.Duration:F3} s, {e.TrackCount} tracks; from {from}");
        Console.WriteLine($"rebuilt with {how}: {built.Rewritten} tracks rewritten, {built.Kept} keep their bytes; {source.Length} B -> {built.Bytes.Length} B; the same bytes as its source: {(same ? "yes" : "no")}");
        foreach (var a in built.Arenas)
            Console.WriteLine($"   arena kind {a.Kind} (alignment {a.Alignment}): {(a.Present ? $"{a.Before} -> {a.After}" : "not in the head")}; the curves need {a.CurvesNeedBefore} -> {a.CurvesNeedAfter}");
        RecordPackage pkg;
        byte[] sourceFile;
        try
        {
            sourceFile = RecordFile.Content(source);
            pkg = RecordFileWriter.Write(g, Need(o, "out"), [(e, built.Bytes)], o.ContainsKey("overwrite"));
        }
        catch (RecordFileException ex) when (ex.FilesWritten) { Console.Error.WriteLine("written, but: " + ex.Message); return 3; }
        catch (RecordFileException ex) { Console.Error.WriteLine("refused: " + ex.Message); return 1; }
        Console.WriteLine($"the source as a record file: {sourceFile.Length} B, SHA-256 {Sha256Hex.Of(sourceFile)}");
        foreach (var f in pkg.Files)
            Console.WriteLine($"wrote {f.Path}: {f.Length} B, SHA-256 {f.Sha256}; the same bytes as the source's record file: {(File.ReadAllBytes(f.Path).AsSpan().SequenceEqual(sourceFile) ? "yes" : "no")}");
        Console.WriteLine("install: copy the file into the folder the unpack of the animation datafile makes: "
            + string.Join("; ", RecordFile.DatafileFolders(pkg.DatafileFolderName).Select(x => $"{x.Folder} on {x.Where}")));
        return 0;
    }

    /// <summary>A curve set: JSON { "curves": [ { "track": i, "form": f, "ticks": [...], "values": [[...], ...] } ] }, one value per tick.
    /// Forms: "rotation6" and "rotation12" (values x y z w), "position" (x y z, metres), "plants" (length in whole ticks, x y z in metres),
    /// "bytes" (one whole number 0 to 255). A value out of its range, a fraction where a whole number stands, or a count of values other
    /// than the count of ticks is refused, never rounded or clamped.</summary>
    static Dictionary<int, byte[]> ReadCurveSet(string path, int tracks)
    {
        try { return CurveSet(path, tracks); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ArgumentException($"{path} isn't a curve set: {ex.Message}"); }
    }

    static Dictionary<int, byte[]> CurveSet(string path, int tracks)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var d = new Dictionary<int, byte[]>();
        foreach (var c in doc.RootElement.GetProperty("curves").EnumerateArray())
        {
            int track = c.GetProperty("track").GetInt32();
            if (track < 0 || track >= tracks) throw new ArgumentException($"{path}: there is no track {track} (the clip has {tracks})");
            string form = c.GetProperty("form").GetString() ?? "";
            var ticks = c.GetProperty("ticks").EnumerateArray().Select(x => x.GetInt32()).ToList();
            var raw = c.GetProperty("values").EnumerateArray().Select(v => v.EnumerateArray().ToArray()).ToList();
            if (raw.Count != ticks.Count) throw new ArgumentException($"{path}: track {track}: {ticks.Count} ticks and {raw.Count} values; each tick needs one value");
            float[] V(int i, int n) => raw[i].Length == n ? raw[i].Select(x => x.GetSingle()).ToArray() : throw new ArgumentException($"{path}: track {track}, value {i}: {n} numbers expected");
            int Whole(int i, int at, int max, string what)
            {
                if (raw[i].Length <= at) throw new ArgumentException($"{path}: track {track}, value {i}: no {what}");
                double d = raw[i][at].GetDouble();
                return d == Math.Floor(d) && d >= 0 && d <= max ? (int)d
                    : throw new ArgumentException($"{path}: track {track}, value {i}: {what} {raw[i][at].GetRawText()} isn't a whole number from 0 to {max}");
            }
            byte[] curve = form switch
            {
                "rotation6" => CurveWriter.RotCurve(ticks, ticks.Select((_, i) => { var v = V(i, 4); return new Quaternion(v[0], v[1], v[2], v[3]); }).ToList()),
                "rotation12" => CurveWriter.RotCurve12(ticks, ticks.Select((_, i) => { var v = V(i, 4); return new Quaternion(v[0], v[1], v[2], v[3]); }).ToList()),
                "position" => CurveWriter.PosCurve(ticks, ticks.Select((_, i) => { var v = V(i, 3); return new Vector3(v[0], v[1], v[2]); }).ToList()),
                "plants" => CurveWriter.PlantCurve(ticks.Select((t, i) => { var v = V(i, 4); return new FootPlant(t, Whole(i, 0, ushort.MaxValue, "the length"), new Vector3(v[1], v[2], v[3])); }).ToList()),
                "bytes" => CurveWriter.ByteCurve(ticks, ticks.Select((_, i) => { V(i, 1); return (byte)Whole(i, 0, 255, "the byte"); }).ToList()),
                _ => throw new ArgumentException($"{path}: track {track}: form '{form}' (rotation6, rotation12, position, plants, bytes)"),
            };
            if (!d.TryAdd(track, curve)) throw new ArgumentException($"{path}: track {track} is given twice");
        }
        return d;
    }

    /// <summary>rebuild-check: one rebuild pass over every clip, its counts and first faults.</summary>
    static int RebuildCheck(string game, string? forge)
    {
        using var g = GameInstall.Open(game, forge);
        var sw = Stopwatch.StartNew();
        var r = RebuildPass.Run(g);
        Console.WriteLine($"clips {g.Clips.Count}: rebuilt {r.Clips}, skipped {r.Skipped} (their curves don't read to the end)");
        Console.WriteLine($"rebuilt with nothing replaced, the same bytes: {r.SameBytes} of {r.Clips}");
        Console.WriteLine($"rebuilt with every inline curve of a form with a writer written again ({r.CurvesReencoded} curves): sound {r.ReencodedSound} of {r.Clips}");
        Console.WriteLine($"keys decoded {r.Keys}; digest {r.Digest}; {sw.Elapsed.TotalSeconds:F1} s");
        foreach (var f in r.Faults) Console.WriteLine("   fault: " + f);
        Console.WriteLine(r.Clean ? "RESULT: PASS" : "RESULT: FAIL");
        return r.Clean ? 0 : 3;
    }

    /// <summary>The rebuild soak: <see cref="RebuildPass"/> repeated in one process, each pass logged (the log is capped).</summary>
    static int SoakRebuild(string game, Dictionary<string, string> o)
    {
        string forge = Need(o, "forge");
        int passes = Int(o, "passes");
        double minutes = Minutes(o);
        bool decodeEach = o.ContainsKey("decode-datafile-each-pass");
        using var log = new CappedLog(Need(o, "log"), 4_000_000, Core.Export.ClipExport.ProtectedFolders(game, forge));
        var clock = Stopwatch.StartNew();
        log.Say($"# rebuild soak start {DateTime.Now:yyyy-MM-ddTHH:mm:ss}; pid {Environment.ProcessId}; runtime {Environment.Version}; gcConcurrent env {Environment.GetEnvironmentVariable("DOTNET_gcConcurrent") ?? "(unset)"}; "
            + $"TieredCompilation env {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "(unset)"}; server GC {System.Runtime.GCSettings.IsServerGC}; datafile decoded each pass {decodeEach}; passes {passes}; minutes {minutes}");
        using var g = GameInstall.Open(game, forge);
        log.Say($"# opened in {g.Timings.Total.TotalSeconds:F2} s: {g.Clips.Count} clips");
        string? first = null;
        int done = 0;
        for (int i = 1; i <= passes && clock.Elapsed.TotalMinutes < minutes; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = RebuildPass.Run(g, decodeEach ? g.DecodeAnimationDatafileAgain() : null);
            first ??= r.Digest;
            bool same = r.Digest == first;
            log.Say($"pass {i}: {sw.Elapsed.TotalSeconds:F1} s; clips {r.Clips}, same bytes {r.SameBytes}, re-encoded curves {r.CurvesReencoded}, sound {r.ReencodedSound}, keys {r.Keys}; digest {r.Digest}{(same ? "" : " MISMATCH")}; "
                + $"GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}; managed {GC.GetTotalMemory(false) / 1048576} MB");
            foreach (var f in r.Faults) log.Say("   fault: " + f);
            if (!same || !r.Clean) { log.Say("# stopped: a pass rebuilt differently or found a fault"); return 3; }
            done = i;
        }
        log.Say($"# rebuild soak done: {done} passes in {clock.Elapsed.TotalMinutes:F1} min");
        return 0;
    }
}
