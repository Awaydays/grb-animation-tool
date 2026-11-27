using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Forge;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.IO;
using GrbAnimTool.Core.Oodle;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Install;

/// <summary>One place a clip's record is: a datafile (its forge, its index there, its ID and its name) and the record's index in it.</summary>
public sealed record DatafilePlace(string ForgePath, int Index, ulong Id, string Name, int RecordIndex)
{
    /// <summary>The folder the datafile's record files go into once unpacked: <c>index_-_name.data</c>.</summary>
    public string FolderName => RecordFile.DatafileFolderName(Index, Name);
}

/// <summary>A clip of another datafile as the tool last read it: its place in the datafile, ID, name, the SHA-256 of its whole record,
/// what the list shows of it, why it can't be read (or null), and its skeleton.</summary>
public sealed record OtherClip(int RecordIndex, ulong Id, string Name, string Sha256, float Duration, int LastTick, int Tracks, bool Walked, string? Error, ClipSkeleton Skeleton);

/// <summary>A datafile of the game's forges other than the animation one, as last read: its forge (the patch's, or the base's where the
/// patch doesn't hold its ID), its index, ID, name, where its payload stands and its SHA-256, its record count, whether it holds graph
/// documents, and its clips (none for most of the forge's entries).</summary>
public sealed record OtherDatafile(bool InPatch, int Index, ulong Id, string Name, long Offset, uint Size, string Sha256, int Records, bool HasGraph,
    IReadOnlyList<OtherClip> Clips)
{
    /// <summary>Why the entry isn't a datafile the tool reads (kept so a start with the forges unchanged needn't try it again), or null.</summary>
    public string? Error { get; init; }
}

/// <summary>Every datafile of the game's forges that holds clip records, beside the animation datafile: the patch forge's entries (all of
/// them are looked at), and the base forge's datafiles the patch doesn't override (by ID, as the game does) among those of the game's own
/// files that hold clips (<see cref="BaseDatafilesWithClips"/>). Read once into a cache in the tool's own folder: one file per patch forge,
/// each datafile kept by its payload's SHA-256, so a start with the forges unchanged reads nothing of them, and one after a change reads
/// the payloads again but decodes only those whose hash changed. The clips' records themselves are read when first needed
/// (<see cref="ReadRecord"/>).</summary>
public sealed class OtherDatafiles
{
    /// <summary>The base forge's datafiles that hold clips and that the patch forge of today's game doesn't hold: IDs only.</summary>
    public static readonly IReadOnlyList<ulong> BaseDatafilesWithClips =
        [0x00000196C1E32420, 0x000001AE22BA8FB0, 0x9E6F2251EEC00001, 0x9E6F22DFD3F00001, 0x9E962BBEF5100002, 0x9E981D84D1E00002];

    public const string Magic = "GRB Anim Tool datafile cache";
    public const int FileFormat = 1;

    /// <summary>What a cache's lists were worked out with: the format, the skeleton rule's threshold and the SHA-256 of the skeleton
    /// table built into this core. A cache of other rules or another table is read anew.</summary>
    static readonly Lazy<string> Rules = new(() =>
    {
        using var s = typeof(OtherDatafiles).Assembly.GetManifestResourceStream(ClipSkeletons.ResourceName);
        string table = s is null ? "none" : Convert.ToHexStringLower(SHA256.HashData(s));
        return $"{FileFormat}|{ClipSkeletons.BodyBones}|{table}";
    });

    public required IReadOnlyList<OtherDatafile> Datafiles { get; init; }
    /// <summary>The forge entries looked at, those decoded this time, and those that aren't datafiles the tool can read (left out).</summary>
    public int Entries { get; init; }
    public int Decoded { get; init; }
    public IReadOnlyList<string> Unreadable { get; init; } = [];
    public CacheOutcome Outcome { get; init; } = CacheOutcome.Built;
    public string? CacheError { get; init; }
    public TimeSpan Time { get; init; }

    /// <summary>Reads the datafiles: through the cache in <paramref name="cacheFolder"/> when given (null: no cache, every entry decoded).
    /// The cache is written through the writers' guard, never in <paramref name="protectedFolders"/>.</summary>
    public static OtherDatafiles Read(string patch, ForgeLayout patchLayout, (long, long) patchStamp, string baseForge, ForgeLayout baseLayout, (long, long) baseStamp,
        OodleLibrary oodle, SharedCurveTable table, Skeleton body, string? cacheFolder, IReadOnlyCollection<string> protectedFolders)
    {
        var sw = Stopwatch.StartNew();
        var patchIds = patchLayout.Entries.Select(e => e.Id).ToHashSet();
        var entries = patchLayout.Entries.Where(e => e.Id != GameInstall.AnimationDatafileId).Select(e => (InPatch: true, Entry: e))
            .Concat(baseLayout.Entries.Where(e => BaseDatafilesWithClips.Contains(e.Id) && !patchIds.Contains(e.Id)).Select(e => (InPatch: false, Entry: e)))
            // one entry an ID and forge: the first, as the reader finds entries
            .DistinctBy(x => (x.InPatch, x.Entry.Id)).ToList();
        string? file = cacheFolder is null ? null : FileFor(cacheFolder, patch);
        bool present = false;
        var cached = file is null ? null : TryRead(file, out present);
        var outcome = file is null ? CacheOutcome.Built : cached is null ? (present ? CacheOutcome.RebuiltUnreadable : CacheOutcome.Built) : CacheOutcome.Reused;
        bool sameForges = cached is not null && cached.PatchStamp == patchStamp && cached.BaseStamp == baseStamp;
        var byKey = cached?.Datafiles.ToDictionary(d => (d.InPatch, d.Id)) ?? [];

        var results = new OtherDatafile?[entries.Count];
        var failed = new string?[entries.Count];
        int decoded = 0;
        // per thread, one stream on each forge
        using var streams = new ThreadLocal<(FileStream P, FileStream B)>(() => (ForgeReader.OpenRead(patch), ForgeReader.OpenRead(baseForge)), trackAllValues: true);
        try
        {
            Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                var (inPatch, e) = entries[i];
                // the forges as they were and the entry where it was: the datafile as it was read then
                if (sameForges && byKey.TryGetValue((inPatch, e.Id), out var was) && was.Offset == e.Offset && was.Size == e.Size && was.Index == e.Index)
                {
                    results[i] = was;
                    return;
                }
                byte[] payload;
                try { payload = ForgeReader.ReadPayload(inPatch ? streams.Value.P : streams.Value.B, e); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failed[i] = $"{e.Index}_-_{e.Name}: {ex.Message}"; return; }
                string sha = Convert.ToHexStringLower(SHA256.HashData(payload));
                if (byKey.TryGetValue((inPatch, e.Id), out var same) && same.Sha256 == sha)
                {
                    results[i] = same with { Index = e.Index, Offset = e.Offset, Size = e.Size, Name = e.Name };
                    return;
                }
                Interlocked.Increment(ref decoded);
                // any entry that doesn't read is left out with its reason (a modded datafile the tool can't parse never ends the open)
                try { results[i] = Scan(inPatch, e, payload, sha, oodle, table, body); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    results[i] = new OtherDatafile(inPatch, e.Index, e.Id, e.Name, e.Offset, e.Size, sha, 0, false, []) { Error = ex.Message };
                }
            });
        }
        finally { foreach (var (p, b) in streams.Values) { p.Dispose(); b.Dispose(); } }

        for (int i = 0; i < results.Length; i++)
            if (results[i] is { Error: { } err } bad) failed[i] = $"{bad.Index}_-_{bad.Name}: {err}";
        var all = results.OfType<OtherDatafile>().ToList();
        string? saveError = null;
        bool changed = cached is null || !sameForges || decoded > 0 || all.Count != cached.Datafiles.Count;
        if (file is not null && changed)
        {
            if (cached is not null && outcome == CacheOutcome.Reused) outcome = CacheOutcome.RebuiltChanged;
            try { Export.ClipExport.WriteFile(file, protectedFolders, fs => Write(fs, patchStamp, baseStamp, all), makeFolder: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Export.ExportException) { saveError = ex.Message; }
        }
        return new OtherDatafiles
        {
            Datafiles = all.Where(d => d.Clips.Count > 0).ToList(), Entries = entries.Count, Decoded = decoded,
            Unreadable = failed.OfType<string>().ToList(), Outcome = outcome, CacheError = saveError, Time = sw.Elapsed,
        };
    }

    /// <summary>Decodes one datafile and reads what the list needs of each clip record in it (the records themselves aren't kept). A payload
    /// that isn't a datafile the tool reads throws <see cref="InvalidDataException"/>.</summary>
    static OtherDatafile Scan(bool inPatch, ForgeEntry e, byte[] payload, string sha, OodleLibrary oodle, SharedCurveTable table, Skeleton body)
    {
        var d = DatafileCodec.Decode(payload, oodle, out int tableBytes, out _);
        var recs = DatafileCodec.Parse(d, tableBytes);
        var clips = new List<OtherClip>();
        foreach (var r in recs.Where(r => r.Type == ClipRecord.Type))
        {
            var bytes = r.Bytes.ToArray();
            string rsha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ClipRecord? c = null; string? err = null;
            try { c = ClipRecord.Parse(bytes); }
            catch (InvalidDataException ex) { err = ex.Message; }
            if (c is not null && c.Id != r.Id) { err = $"the clip's own ID 0x{c.Id:X16} isn't the datafile table's 0x{r.Id:X16}"; c = null; }
            var skel = c is null ? ClipSkeleton.Unknown : ClipSkeletons.Of(r.Id, c, table, body);
            clips.Add(new OtherClip(r.Index, r.Id, r.Name, rsha, c is { DurationValid: true } ? c.Duration : 0, c?.LastTick ?? 0, c?.Tracks.Count ?? 0, c?.WalkedToEnd ?? false, err, skel));
        }
        return new OtherDatafile(inPatch, e.Index, e.Id, e.Name, e.Offset, e.Size, sha, recs.Count, recs.Any(r => GraphDocument.IsDocumentType(r.Type)), clips);
    }

    /// <summary>A datafile's records, read from its forge now: its payload must still hash as when the list was made (else the game's files
    /// changed since, and <see cref="InvalidDataException"/> says so).</summary>
    public static List<DatafileRecord> ReadRecords(string forgePath, OtherDatafile d, OodleLibrary oodle)
    {
        byte[] payload;
        using (var fs = ForgeReader.OpenRead(forgePath)) payload = Bin.ReadAt(fs, d.Offset, checked((int)d.Size));
        if (Convert.ToHexStringLower(SHA256.HashData(payload)) != d.Sha256)
            throw new InvalidDataException($"{d.Index}_-_{d.Name} has changed in the game's files since the tool read them: open the game folder again");
        var x = DatafileCodec.Decode(payload, oodle, out int t, out _);
        return DatafileCodec.Parse(x, t);
    }

    /// <summary>One clip's record from a datafile's records: the record of that index and ID, its parse, or why it can't be read.</summary>
    public static (ReadOnlyMemory<byte> Bytes, ClipRecord? Record, string? Error) RecordOf(IReadOnlyList<DatafileRecord> recs, int index, ulong id)
    {
        var r = recs.FirstOrDefault(x => x.Index == index && x.Id == id);
        if (r is null || r.Type != ClipRecord.Type) return (ReadOnlyMemory<byte>.Empty, null, $"record {index} of the datafile isn't the clip 0x{id:X16}");
        var bytes = r.Bytes.ToArray();
        try
        {
            var c = ClipRecord.Parse(bytes);
            return c.Id != id ? (bytes, null, $"the clip's own ID 0x{c.Id:X16} isn't the datafile table's 0x{id:X16}") : (bytes, c, null);
        }
        catch (InvalidDataException ex) { return (bytes, null, ex.Message); }
    }

    /// <summary>The cache file for a patch forge: <c>datafiles-</c> and 16 hex digits of the SHA-256 of its full path.</summary>
    public static string FileFor(string folder, string patch)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(patch).ToUpperInvariant()));
        return Path.Combine(Path.GetFullPath(folder), $"datafiles-{Convert.ToHexStringLower(h)[..16]}.json");
    }

    sealed record Cached((long, long) PatchStamp, (long, long) BaseStamp, List<OtherDatafile> Datafiles);

    const long LongestFile = 64L << 20;

    static Cached? TryRead(string file, out bool present)
    {
        present = File.Exists(file);
        if (!present) return null;
        try
        {
            if (new FileInfo(file).Length > LongestFile) return null;
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.GetProperty("magic").GetString() != Magic || root.GetProperty("format").GetInt32() != FileFormat) return null;
            if (!root.TryGetProperty("rules", out var rules) || rules.GetString() != Rules.Value) return null;
            var p = root.GetProperty("patch"); var b = root.GetProperty("base");
            var list = new List<OtherDatafile>();
            foreach (var d in root.GetProperty("datafiles").EnumerateArray())
            {
                var clips = new List<OtherClip>();
                foreach (var c in d.GetProperty("clips").EnumerateArray())
                {
                    var sk = c[9].GetString() switch
                    {
                        "body" => ClipSkeleton.Body,
                        "unknown" => ClipSkeleton.Unknown,
                        _ => new ClipSkeleton(ClipSkeletonKind.Own, c[10].GetUInt64(), c[11].GetUInt64()),
                    };
                    clips.Add(new OtherClip(c[0].GetInt32(), c[1].GetUInt64(), c[2].GetString()!, c[3].GetString()!, BitConverter.Int32BitsToSingle(c[4].GetInt32()),
                        c[5].GetInt32(), c[6].GetInt32(), c[7].GetBoolean(), c[8].ValueKind == JsonValueKind.Null ? null : c[8].GetString(), sk));
                }
                list.Add(new OtherDatafile(d.GetProperty("patch").GetBoolean(), d.GetProperty("index").GetInt32(), d.GetProperty("id").GetUInt64(), d.GetProperty("name").GetString()!,
                    d.GetProperty("offset").GetInt64(), d.GetProperty("size").GetUInt32(), d.GetProperty("sha256").GetString()!, d.GetProperty("records").GetInt32(),
                    d.GetProperty("graph").GetBoolean(), clips)
                { Error = d.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null });
            }
            return new Cached((p.GetProperty("length").GetInt64(), p.GetProperty("ticks").GetInt64()), (b.GetProperty("length").GetInt64(), b.GetProperty("ticks").GetInt64()), list);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException
            or OverflowException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Writes the cache: the two forges' stamps, then every entry looked at (those without clips too, so a changed forge needn't
    /// decode them again) with its clips.</summary>
    static void Write(Stream s, (long Length, long Ticks) patch, (long Length, long Ticks) baseStamp, IReadOnlyList<OtherDatafile> all)
    {
        using var w = new Utf8JsonWriter(s);
        w.WriteStartObject();
        w.WriteString("magic", Magic);
        w.WriteNumber("format", FileFormat);
        w.WriteString("rules", Rules.Value);
        w.WriteStartObject("patch"); w.WriteNumber("length", patch.Length); w.WriteNumber("ticks", patch.Ticks); w.WriteEndObject();
        w.WriteStartObject("base"); w.WriteNumber("length", baseStamp.Length); w.WriteNumber("ticks", baseStamp.Ticks); w.WriteEndObject();
        w.WriteStartArray("datafiles");
        foreach (var d in all)
        {
            w.WriteStartObject();
            w.WriteBoolean("patch", d.InPatch);
            w.WriteNumber("index", d.Index);
            w.WriteNumber("id", d.Id);
            w.WriteString("name", d.Name);
            w.WriteNumber("offset", d.Offset);
            w.WriteNumber("size", d.Size);
            w.WriteString("sha256", d.Sha256);
            w.WriteNumber("records", d.Records);
            w.WriteBoolean("graph", d.HasGraph);
            if (d.Error is { } er) w.WriteString("error", er);
            w.WriteStartArray("clips");
            foreach (var c in d.Clips)
            {
                w.WriteStartArray();
                w.WriteNumberValue(c.RecordIndex);
                w.WriteNumberValue(c.Id);
                w.WriteStringValue(c.Name);
                w.WriteStringValue(c.Sha256);
                w.WriteNumberValue(BitConverter.SingleToInt32Bits(c.Duration));
                w.WriteNumberValue(c.LastTick);
                w.WriteNumberValue(c.Tracks);
                w.WriteBooleanValue(c.Walked);
                if (c.Error is null) w.WriteNullValue(); else w.WriteStringValue(c.Error);
                w.WriteStringValue(c.Skeleton.Kind switch { ClipSkeletonKind.Body => "body", ClipSkeletonKind.Unknown => "unknown", _ => "own" });
                w.WriteNumberValue(c.Skeleton.SkeletonId);
                w.WriteNumberValue(c.Skeleton.EntryId);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
    }
}
