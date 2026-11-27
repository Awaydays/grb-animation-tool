using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Index;

/// <summary>What the index's graph part was built from: the patch forge's length and last write time, the animation datafile's forge
/// entry (ID and stored size) and the SHA-256 of its record table (each record's ID and size, as the datafile's table holds them), and
/// the version of how the facts are read (<see cref="GraphFacts.FormatVersion"/>). Any change builds the graph's part again. The index's
/// rules aren't in it: the cache holds only the facts, and the rules are applied to them on every open, so a copy of the tool with other
/// rules reuses the facts.</summary>
public sealed record IndexFingerprint(long ForgeLength, long ForgeWriteTimeUtcTicks, ulong DatafileId, uint DatafileSize, string DatafileTableSha256,
    int FactsVersion = GraphFacts.FormatVersion)
{
    /// <summary>The install's fingerprint: the patch forge as it stood when the install was opened (<see cref="GameInstall.PatchForgeStamp"/>),
    /// so a forge rewritten since never gives its stamp to what was read from the old one.</summary>
    public static IndexFingerprint Of(GameInstall g) => new(g.PatchForgeStamp.Length, g.PatchForgeStamp.WriteTimeUtcTicks, g.AnimationEntry.Id, g.AnimationEntry.Size,
        TableSha256(g.Records), GraphFacts.FormatVersion);

    /// <summary>The SHA-256 of the datafile's record table as the datafile holds it: a u16 count, then each record's u64 ID, u32 size and
    /// a u16 0, in the records' order.</summary>
    public static string TableSha256(IReadOnlyList<DatafileRecord> records)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> row = stackalloc byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(row, (ushort)records.Count);
        h.AppendData(row[..2]);
        foreach (var r in records)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(row, r.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], (uint)r.Bytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(row[12..], 0);
            h.AppendData(row);
        }
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}

/// <summary>How the graph's part of an index was had.</summary>
public enum CacheOutcome
{
    /// <summary>Read from the cache: it was built from these files, its facts read this way (the rules then applied anew).</summary>
    Reused,
    /// <summary>Built: there was no cache for this install.</summary>
    Built,
    /// <summary>Built again: the cache was for other files (the install changed) or facts read another way.</summary>
    RebuiltChanged,
    /// <summary>Built again: the cache file couldn't be read as this tool's (damaged, cut short, or not this tool's file).</summary>
    RebuiltUnreadable,
    /// <summary>Built again on request (a retry), the cache passed over.</summary>
    RebuiltOnRequest,
}

/// <summary>An index opened through the cache: the index, how its graph part was had, how long that took, and why the cache couldn't
/// be written, when it couldn't (the index is whole either way).</summary>
public sealed record CachedIndex(ClipIndex Index, CacheOutcome Outcome, TimeSpan GraphTime, string? SaveError);

/// <summary>The cache of the index's graph part (<see cref="GraphFacts"/>, the costly read of the whole graph) in a folder of the user's
/// own: by default <c>%APPDATA%\GrbAnimTool\index\</c>. One file per patch forge (named by a hash of its path), holding the fingerprint
/// it was built for (<see cref="IndexFingerprint"/>). Opening an install reads the file when its fingerprint is the install's; otherwise,
/// or when the file is damaged or not this tool's, the graph is read again and the file written anew, through the writers' guard (a
/// temporary file of its own moved over the name; never in a folder of the game's). The rules run on the facts each time, so the shipped
/// list and the rules always apply as this copy of the tool has them.</summary>
public sealed class ClipIndexCache(string folder)
{
    /// <summary>The first line of every cache file's JSON: its "magic" value.</summary>
    public const string Magic = "GRB Anim Tool clip index cache";
    public const int FileFormat = 1;

    /// <summary>The user's own folder for the cache: <c>%APPDATA%\GrbAnimTool\index</c> (<see cref="Library.ToolData"/>).</summary>
    public static string DefaultFolder => Path.Combine(Library.ToolData.Folder, "index");

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary>The cache file for an install's patch forge: <c>graph-</c> and 16 hex digits of the SHA-256 of its full path, case
    /// ignored as Windows ignores it.</summary>
    public string FileFor(GameInstall g)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(g.PatchForgePath).ToUpperInvariant()));
        return Path.Combine(Folder, $"graph-{Convert.ToHexStringLower(h)[..16]}.json");
    }

    /// <summary>The install's index: the graph's part from the cache when it is the install's, else read (with
    /// <paramref name="progress"/> from 0 to 1) and saved; then the rules and <paramref name="list"/> (the list built into the core when
    /// not given) applied.</summary>
    public CachedIndex Open(GameInstall g, IProgress<double>? progress = null, CancellationToken cancel = default, ClipIndexList? list = null)
    {
        var sw = Stopwatch.StartNew();
        var fp = IndexFingerprint.Of(g);
        string file = FileFor(g);
        var clipIds = g.ForgeClips.Select(c => c.Id).ToHashSet();
        var outcome = CacheOutcome.Built;
        GraphFacts? facts = null;
        if (File.Exists(file))
        {
            switch (TryRead(file, fp, clipIds, out facts))
            {
                case ReadResult.Ok: outcome = CacheOutcome.Reused; break;
                case ReadResult.OtherFingerprint: outcome = CacheOutcome.RebuiltChanged; break;
                default: outcome = CacheOutcome.RebuiltUnreadable; break;
            }
        }
        string? saveError = null;
        if (facts is null)
        {
            facts = GraphFacts.Read(g.Records, clipIds, progress is null ? null : GraphShare(progress), cancel);
            try { ClipExport.WriteFile(file, ClipExport.ProtectedFolders(g), fs => Write(fs, fp, facts), makeFolder: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExportException) { saveError = ex.Message; }
        }
        var graphTime = sw.Elapsed;
        var index = ClipIndex.Build(g, facts, list ?? ClipIndexList.Embedded(), fp);
        progress?.Report(1.0);
        return new CachedIndex(index, outcome, graphTime, saveError);
    }

    /// <summary>The share of an index's progress that the graph's read takes; the rest is the rules and the list applied, after which
    /// <see cref="Open"/> reports 1.</summary>
    public const double GraphReadShare = 0.97;

    /// <summary>A progress for the graph's read (0 to 1) passed on as 0 to <see cref="GraphReadShare"/> of the whole.</summary>
    public static IProgress<double> GraphShare(IProgress<double> whole) => new Scaled(whole, GraphReadShare);

    /// <summary>Passes a share of 0 to 1 on as 0 to <c>top</c>.</summary>
    sealed class Scaled(IProgress<double> inner, double top) : IProgress<double>
    {
        public void Report(double value) => inner.Report(value * top);
    }

    internal enum ReadResult { Ok, OtherFingerprint, Unreadable }

    /// <summary>The longest cache file read: the game's is about a third of a megabyte.</summary>
    const long LongestFile = 64L << 20;

    /// <summary>Reads a cache file for an install of fingerprint <paramref name="fp"/> and clips <paramref name="clipIds"/>: the facts
    /// when the file is this tool's, whole, for that fingerprint and those clips; otherwise why not.</summary>
    internal static ReadResult TryRead(string file, IndexFingerprint fp, HashSet<ulong> clipIds, out GraphFacts? facts)
    {
        facts = null;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > LongestFile) return ReadResult.Unreadable;
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "magic") != Magic || Int(root, "format") != FileFormat) return ReadResult.Unreadable;
            if (ReadFingerprint(root.GetProperty("fingerprint")) != fp) return ReadResult.OtherFingerprint;
            var docs = new List<IndexDocument>();
            foreach (var d in root.GetProperty("documents").EnumerateArray())
                docs.Add(new IndexDocument(U64(d, "id"), Str(d, "name") ?? throw new InvalidDataException("a document without a name"),
                    Ints(d, "children"), Ints(d, "references"), Int(d, "clips")));
            bool InRange(IReadOnlyList<int> l) => l.All(i => i >= 0 && i < docs.Count);
            if (docs.Count == 0 || !docs.All(d => InRange(d.Children) && InRange(d.References) && d.Clips >= 0)) return ReadResult.Unreadable;
            var clips = new Dictionary<ulong, ClipGraphFacts>();
            foreach (var c in root.GetProperty("clips").EnumerateArray())
            {
                ulong id = U64(c, "id");
                var cf = new ClipGraphFacts(Ints(c, "documents"), Ints(c, "motionFields"));
                // a clip the install doesn't hold, or the same clip twice: not this install's cache
                if (!clipIds.Contains(id) || !InRange(cf.Documents) || !InRange(cf.MotionFieldDocuments) || !clips.TryAdd(id, cf)) return ReadResult.Unreadable;
            }
            var failures = root.GetProperty("failures").EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("a failure that isn't a text")).ToList();
            int looked = Int(root, "clipsLookedFor");
            if (looked != clipIds.Count) return ReadResult.Unreadable;
            facts = new GraphFacts { Documents = docs, Clips = clips, Failures = failures, ClipsLookedFor = looked };
            return ReadResult.Ok;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException
            or OverflowException or IOException or UnauthorizedAccessException)
        {
            return ReadResult.Unreadable;
        }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();
    static ulong U64(JsonElement e, string name) => e.GetProperty(name).GetUInt64();
    static int[] Ints(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetInt32()).ToArray();

    /// <summary>The fingerprint a cache file holds. A file written before the facts' version was kept holds none: version 0, so it is
    /// built again as made from other files. A rules' version that an earlier copy wrote is passed over.</summary>
    static IndexFingerprint ReadFingerprint(JsonElement f) => new(
        f.GetProperty("forgeLength").GetInt64(), f.GetProperty("forgeWriteTimeUtcTicks").GetInt64(), U64(f, "datafileId"),
        f.GetProperty("datafileSize").GetUInt32(), Str(f, "datafileTableSha256") ?? "",
        f.TryGetProperty("factsVersion", out var v) ? v.GetInt32() : 0);

    /// <summary>Writes the graph's facts and the fingerprint they were built for as the cache's JSON.</summary>
    internal static void Write(Stream s, IndexFingerprint fp, GraphFacts facts)
    {
        using var w = new Utf8JsonWriter(s);
        w.WriteStartObject();
        w.WriteString("magic", Magic);
        w.WriteNumber("format", FileFormat);
        w.WriteStartObject("fingerprint");
        w.WriteNumber("forgeLength", fp.ForgeLength);
        w.WriteNumber("forgeWriteTimeUtcTicks", fp.ForgeWriteTimeUtcTicks);
        w.WriteNumber("datafileId", fp.DatafileId);
        w.WriteNumber("datafileSize", fp.DatafileSize);
        w.WriteString("datafileTableSha256", fp.DatafileTableSha256);
        w.WriteNumber("factsVersion", fp.FactsVersion);
        w.WriteEndObject();
        w.WriteNumber("clipsLookedFor", facts.ClipsLookedFor);
        w.WriteStartArray("failures");
        foreach (var f in facts.Failures) w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteStartArray("documents");
        foreach (var d in facts.Documents)
        {
            w.WriteStartObject();
            w.WriteNumber("id", d.Id);
            w.WriteString("name", d.Name);
            Numbers(w, "children", d.Children);
            Numbers(w, "references", d.References);
            w.WriteNumber("clips", d.Clips);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("clips");
        foreach (var (id, cf) in facts.Clips.OrderBy(k => k.Key))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            Numbers(w, "documents", cf.Documents);
            Numbers(w, "motionFields", cf.MotionFieldDocuments);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
    }

    static void Numbers(Utf8JsonWriter w, string name, IReadOnlyList<int> l)
    {
        w.WriteStartArray(name);
        foreach (int i in l) w.WriteNumberValue(i);
        w.WriteEndArray();
    }

    /// <summary>The cache file's fingerprint text, for a message.</summary>
    public static string Describe(IndexFingerprint fp) => string.Create(CultureInfo.InvariantCulture,
        $"forge {fp.ForgeLength} B written {new DateTime(fp.ForgeWriteTimeUtcTicks, DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss} UTC, datafile 0x{fp.DatafileId:X16} {fp.DatafileSize} B, table {fp.DatafileTableSha256[..Math.Min(16, fp.DatafileTableSha256.Length)]}, facts {fp.FactsVersion}");
}
