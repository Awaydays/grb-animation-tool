using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Writing;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>Record files that work in game, installed by hand (GRBANIM_KNOWNGOOD names their folder): each replacement clip's file is read
/// as a clip record, rebuilt by the core with nothing replaced and written by the record-file writer, named from the user's own datafile
/// (the game folder's patch forge, as installed). The written file must be the known-good file, name and bytes. Each is also compared,
/// read-only, with the installed forge's record of the same index, which may hold another version.</summary>
[Collection("install")]
public sealed class KnownGoodTests(InstallFixture f, ITestOutputHelper output) : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "knowngood-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { } }

    static string? Folder => Environment.GetEnvironmentVariable("GRBANIM_KNOWNGOOD") is { Length: > 0 } p && Directory.Exists(p) ? p : null;

    public sealed class KnownGoodFactAttribute : FactAttribute
    {
        public KnownGoodFactAttribute()
        {
            if (!GameData.Available) Skip = GameData.SkipText;
            else if (Folder is null) Skip = "known-good record files not configured: set GRBANIM_KNOWNGOOD (a folder of record files that work in game)";
        }
    }

    /// <summary>The first record index past the game's own: a file at or above it adds a clip instead of replacing one.</summary>
    const int FirstAdded = 10588;

    [KnownGoodFact]
    public void KnownGoodRecordFilesComeOutOfTheCoreByteForByte()
    {
        var vanilla = f.Install;
        using var installed = GameInstall.Open(GameData.Game!);
        var files = Directory.GetFiles(Folder!).Select(p => new FileInfo(p)).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        int replaced = 0, equal = 0, sameAsInstalled = 0, skippedAdded = 0, skippedOther = 0;
        var notInstalled = new List<string>();
        var bad = new List<string>();
        foreach (var fi in files)
        {
            string fn = fi.Name;
            int sep = fn.IndexOf("_-_", StringComparison.Ordinal);
            if (!fn.EndsWith(RecordFile.ClipExtension, StringComparison.Ordinal) || sep <= 0 || !int.TryParse(fn.AsSpan(0, sep), out int index)) { skippedOther++; continue; }
            if (index >= FirstAdded) { skippedAdded++; continue; }
            replaced++;
            string name = fn[(sep + 3)..^RecordFile.ClipExtension.Length];
            var known = File.ReadAllBytes(fi.FullName);
            var entry = installed.Clips.FirstOrDefault(c => c.Index == index);
            var vEntry = vanilla.Clips.FirstOrDefault(c => c.Index == index);
            if (entry is null || entry.Name != name || RecordFile.IdOf(known) != entry.Id) { bad.Add($"{fn}: not clip {index} of the installed datafile"); continue; }
            if (vEntry is null || vEntry.Name != name || vEntry.Id != entry.Id) { bad.Add($"{fn}: not clip {index} of the vanilla datafile"); continue; }
            try
            {
                var clip = ClipRecord.Parse(RecordFile.ToRecord(known, name));
                var built = ClipRebuild.Rebuild(clip, installed.SharedTable);
                var pkg = RecordFileWriter.Write(installed, Path.Combine(_dir, index.ToString()), [(entry, built.Bytes)]);
                var w = Assert.Single(pkg.Files);
                var back = File.ReadAllBytes(w.Path);
                if (w.FileName == fn && back.AsSpan().SequenceEqual(known)) equal++;
                else bad.Add($"{fn}: written as {w.FileName}, {back.Length} B, SHA-256 {w.Sha256}; known-good {known.Length} B, SHA-256 {Sha256Hex.Of(known)}");
            }
            catch (Exception ex) when (ex is InvalidDataException or RebuildRefusedException or RecordFileException) { bad.Add($"{fn}: {ex.Message}"); }
            if (RecordFile.Content(entry.Bytes.Span).AsSpan().SequenceEqual(known)) sameAsInstalled++;
            else notInstalled.Add($"{fn} ({(RecordFile.Content(vEntry.Bytes.Span).AsSpan().SequenceEqual(known) ? "the vanilla record" : "not vanilla")}; installed is {(entry.Status)})");
        }
        output.WriteLine($"{files.Count} files: {replaced} replacement clips (index below {FirstAdded}), {skippedAdded} added clips and {skippedOther} other records skipped");
        output.WriteLine($"written by the core and equal to the known-good file, name and bytes: {equal} of {replaced}");
        output.WriteLine($"equal to the installed patch forge's record of the same index: {sameAsInstalled} of {replaced}; the installed datafile folder is {installed.RecordFolderName}");
        foreach (var n in notInstalled) output.WriteLine($"   differs from the installed record: {n}");
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.True(replaced > 0);
        Assert.Equal(replaced, equal);
    }

    /// <summary>Each replacement file rebuilt with every inline curve of a form with a writer decoded and written again by the core's
    /// writers (references stay references): the record's head and every curve's layout (tag, size, key count, times) come back byte for
    /// byte in every file, positions, plants and bytes exactly; rotations within their form's rounding. The files whose every byte comes
    /// back are counted.</summary>
    [KnownGoodFact]
    public void KnownGoodRecordFilesReencodedByTheCoreKeepTheirLayout()
    {
        using var installed = GameInstall.Open(GameData.Game!);
        var t = installed.SharedTable;
        int files = 0, layout = 0, everyByte = 0, curves = 0, curvesSame = 0;
        double worst6 = 0, worst12 = 0;
        var bad = new List<string>();
        foreach (var path in Directory.GetFiles(Folder!).Order(StringComparer.Ordinal))
        {
            string fn = Path.GetFileName(path);
            int sep = fn.IndexOf("_-_", StringComparison.Ordinal);
            if (!fn.EndsWith(RecordFile.ClipExtension, StringComparison.Ordinal) || sep <= 0 || !int.TryParse(fn.AsSpan(0, sep), out int index) || index >= FirstAdded) continue;
            files++;
            string name = fn[(sep + 3)..^RecordFile.ClipExtension.Length];
            var known = File.ReadAllBytes(path);
            try
            {
                var record = RecordFile.ToRecord(known, name);
                var clip = ClipRecord.Parse(record);
                var built = ClipRebuild.Rebuild(clip, t, (_, _, u) => ReferenceEquals(u.Source, t.Bytes) ? null : CurveReencoder.Reencode(u));
                var nw = built.Record;
                bool ok = built.Bytes.Length == record.Length && built.Bytes.AsSpan(0, clip.FirstCurve).SequenceEqual(record.AsSpan(0, clip.FirstCurve));
                for (int i = 0; ok && i < clip.Curves.Count; i++)
                {
                    Curve o = clip.Curves[i], w = nw.Curves[i];
                    if (o.IsReference) { ok = w.IsReference && w.Ref == o.Ref; continue; }
                    curves++;
                    var ob = record.AsSpan(o.At, o.DiskLength);
                    var wb = built.Bytes.AsSpan(w.At, w.DiskLength);
                    int lay = o.ValuesAt - o.At;
                    if (wb.Length != ob.Length || !wb[..lay].SequenceEqual(ob[..lay])) { ok = false; bad.Add($"{fn}: track {i}, tag 0x{o.Tag:X2}: the layout differs"); break; }
                    if (wb.SequenceEqual(ob)) { curvesSame++; continue; }
                    if (!CurveTags.IsRotation(o.Tag)) { ok = false; bad.Add($"{fn}: track {i}, tag 0x{o.Tag:X2}: the values differ"); break; }
                    for (int k = 0; k < o.Keys; k++)
                    {
                        double d = Angles.Deg(CurveDecoder.KeyRot(o, k)!.Value, CurveDecoder.KeyRot(w, k)!.Value);
                        if (o.ValueBytes == 12) worst12 = Math.Max(worst12, d); else worst6 = Math.Max(worst6, d);
                    }
                }
                if (!ok) { if (bad.Count == 0 || !bad[^1].StartsWith(fn, StringComparison.Ordinal)) bad.Add($"{fn}: the head or the layout differs"); continue; }
                layout++;
                if (built.Bytes.AsSpan().SequenceEqual(record)) everyByte++;
            }
            catch (Exception ex) when (ex is InvalidDataException or RebuildRefusedException or RecordFileException or ArgumentException) { bad.Add($"{fn}: {ex.Message}"); }
        }
        output.WriteLine($"{files} replacement files re-encoded by the core: head and layout byte for byte in {layout}, every byte in {everyByte}; "
            + $"{curves} inline curves, {curvesSame} every byte the same; worst rotation moved {worst6:F5} deg (6-byte forms), {worst12:F6} deg (12-byte)");
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.Equal(118, files);
        Assert.Equal(files, layout);
        Assert.Equal(103, everyByte);
        Assert.True(worst6 <= 0.01, $"6-byte rotations moved {worst6} deg");
        Assert.True(worst12 <= 1e-4, $"12-byte rotations moved {worst12} deg");
    }
}
