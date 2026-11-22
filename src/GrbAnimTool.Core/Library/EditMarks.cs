using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Library;

/// <summary>Which edits each clip of an install allows, as the Edit screen's list marks and filters by them: one bit per edit, in
/// <see cref="EditKind"/>'s order, from every clip's choices (<see cref="InstallEditChoices"/>, each slider and the trim stopping at the
/// first setting that works). Small enough to keep in a cache (<see cref="EditMarksCache"/>); the clip chosen on the screen is tried
/// whole when it is chosen.</summary>
public sealed class EditMarks
{
    readonly Dictionary<ulong, byte> _of;

    EditMarks(Dictionary<ulong, byte> of, HashSet<ulong>? unknown = null) => (_of, _unknown) = (of, unknown ?? []);

    /// <summary>The bits of every edit.</summary>
    public const byte AllEdits = (1 << 5) - 1;

    public static byte Bit(EditKind k) => (byte)(1 << (int)k);

    /// <summary>A clip's bits: each edit its choices allow.</summary>
    public static byte MaskOf(ClipEditChoices c) => (byte)c.Options.Where(o => o.Available).Aggregate(0, (m, o) => m | Bit(o.Kind));

    public static EditMarks From(InstallEditChoices all) => new(all.Clips.ToDictionary(k => k.Key, k => MaskOf(k.Value)));

    /// <summary>Works out every clip's marks on the install (<see cref="InstallEditChoices.OfInBackground"/>, on all but one of the
    /// processor's cores, threads of their own below the normal priority), with the share of the clips done reported to
    /// <paramref name="progress"/>. A clip the tool failed on isn't told (<see cref="Unknown"/>, its first errors kept in
    /// <see cref="Faults"/> for the log): the same files and build fail the same way, so such marks are kept all the same. Marks worked out
    /// without the graph, the swap groups or the mirror (each null when it couldn't be read, which may not happen next time) say so
    /// (<see cref="Incomplete"/>): they are shown, never kept.</summary>
    /// <remarks><paramref name="why"/>: why each of the graph, the swap groups and the mirror that is null couldn't be had, said with it.
    /// An error that said nothing of the clips it met (memory short, say: <see cref="InstallEditChoices.Transient"/>) leaves them out, and
    /// the marks incomplete.</remarks>
    public static async Task<EditMarks> BuildAsync(GameInstall g, ClipGraph? graph, SwapSources? swaps, SkeletonMirror? mirror, IProgress<double>? progress,
        CancellationToken cancel, (string? Graph, string? Swaps, string? Mirror) why = default)
    {
        var all = await InstallEditChoices.OfInBackground(g, graph, swaps, mirror, cancel, progress: progress);
        var faulted = all.Clips.Where(k => InstallEditChoices.IsFault(k.Value)).Select(k => k.Key).ToHashSet();
        // one name per ID: two records of one ID are one clip (the first found)
        var names = InstallEditChoices.ClipsOf(g).ToDictionary(c => c.Id, c => c.Name);
        static string With(string what, string? reason) => reason is null ? what : $"{what}: {reason}";
        return new EditMarks(all.Clips.Where(k => !faulted.Contains(k.Key)).ToDictionary(k => k.Key, k => MaskOf(k.Value)), faulted)
        {
            Incomplete = graph is null ? With("the game's animation graph couldn't be read", why.Graph)
                : swaps is null ? With("the swap sources couldn't be worked out", why.Swaps)
                : mirror is null ? With("the skeleton's mirror couldn't be made", why.Mirror)
                : all.Transient is { } t ? $"not every clip was worked out ({t.GetType().Name}: {t.Message})"
                : all.Count != names.Count ? "not every clip was worked out"
                : null,
            Transient = all.Transient,
            Faults = [.. all.Clips.Where(k => faulted.Contains(k.Key)).OrderBy(k => names.GetValueOrDefault(k.Key), StringComparer.Ordinal).Take(FaultsKept)
                .Select(k => (names.GetValueOrDefault(k.Key) ?? $"0x{k.Key:X16}", k.Value.Options.First(o => !o.Available && o.Refusal is null).Error
                    ?? new InvalidOperationException(k.Value.Options.First(o => !o.Available && o.Refusal is null).Detail ?? "a fault with no error kept")))],
        };
    }

    /// <summary>How many of the clips the tool failed on keep their error (<see cref="Faults"/>): the log gives those whole, and a count.</summary>
    public const int FaultsKept = 5;

    /// <summary>Why these marks may not hold on the next start (the graph, the swap groups or the mirror couldn't be read this time), so
    /// they aren't kept in the cache; null when they are whole (clips the tool failed on are whole: <see cref="Unknown"/>).</summary>
    public string? Incomplete { get; private init; }

    /// <summary>The clips the tool failed on, whose edits aren't told (listed as ones that may allow an edit, their Edits column empty).</summary>
    public IReadOnlySet<ulong> Unknown => _unknown;
    readonly HashSet<ulong> _unknown;

    /// <summary>The first few clips the tool failed on (by name), each with its error: for the log. Empty when read from the cache.</summary>
    public IReadOnlyList<(string Clip, Exception Error)> Faults { get; private init; } = [];

    /// <summary>The first error of the moment that left clips out (<see cref="InstallEditChoices.Transient"/>), for the log; null when
    /// none came.</summary>
    public Exception? Transient { get; private init; }

    /// <summary>Bits already worked out, by clip ID, with the clips not told (<paramref name="unknown"/>) and why the marks may not hold
    /// next time, if they may not.</summary>
    public static EditMarks From(IReadOnlyDictionary<ulong, byte> masks, string? incomplete = null, IEnumerable<ulong>? unknown = null,
        IReadOnlyList<(string Clip, Exception Error)>? faults = null, Exception? transient = null) =>
        new(new Dictionary<ulong, byte>(masks), unknown?.ToHashSet() ?? []) { Incomplete = incomplete, Faults = faults ?? [], Transient = transient };

    /// <summary>How many clips are told (<see cref="Masks"/>); the clips not told are apart (<see cref="Unknown"/>).</summary>
    public int Count => _of.Count;

    /// <summary>Every clip's bits, by clip ID.</summary>
    public IReadOnlyDictionary<ulong, byte> Masks => _of;

    /// <summary>The clip's bits, or null when it wasn't worked out.</summary>
    public byte? MaskFor(ClipEntry c) => _of.TryGetValue(c.Id, out var m) ? m : null;

    /// <summary>Whether the clip allows at least one edit, or null when it wasn't worked out.</summary>
    public bool? Any(ClipEntry c) => MaskFor(c) is byte m ? m != 0 : null;

    /// <summary>The list's marks for the clip (<see cref="Text"/>), or "" when it wasn't worked out.</summary>
    public string Marks(ClipEntry c) => MaskFor(c) is byte m ? Text(m) : "";

    /// <summary>The marks' meaning for the clip (<see cref="Meaning"/>), or "" when it wasn't worked out.</summary>
    public string MarksText(ClipEntry c) => MaskFor(c) is byte m ? Meaning(m) : "";

    /// <summary>How many clips allow no edit.</summary>
    public int NoEdit => _of.Values.Count(m => m == 0);

    /// <summary>The marks as the Edit screen's list shows them: a letter for each edit allowed (M mirror, R root-motion scale, S speed,
    /// T trim, W swap), a dot for each one not.</summary>
    public static string Text(byte mask) => string.Join(" ", Enum.GetValues<EditKind>().Select(k => (mask & Bit(k)) != 0 ? ClipEditChoices.Letter(k) : "·"));

    /// <summary>The marks' meaning, for their tooltip: the edits allowed by name, or that none is.</summary>
    public static string Meaning(byte mask) => Enum.GetValues<EditKind>().Where(k => (mask & Bit(k)) != 0).Select(EditJobs.Name).ToList() is { Count: > 0 } l
        ? "Allows: " + string.Join(", ", l) : "Allows no edit";
}

/// <summary>What an install's edit marks were worked out from: the install's files as the clip index's cache sees them
/// (<see cref="IndexFingerprint"/>: the patch forge's length and last write time, the animation datafile's entry and its record table),
/// the base forge's length and last write time and the skeleton records (the skeleton may come from either forge), and how the marks are
/// worked out (<see cref="Method"/>: the cache's format and the edits' code, by the core library's module version, so any other build of
/// the edits works them out again). Any change builds the marks again.</summary>
public sealed record EditMarksFingerprint(IndexFingerprint Install, long BaseForgeLength, long BaseForgeWriteTimeUtcTicks, string SkeletonSha256, string Method)
{
    /// <summary>The version of how the marks are worked out and kept, beside the code's own module version.</summary>
    public const int FormatVersion = 2;

    /// <summary>This build's way of working the marks out: the format version and the core library's module version ID.</summary>
    public static string ThisMethod => string.Create(CultureInfo.InvariantCulture, $"{FormatVersion}/{typeof(EditJobs).Assembly.ManifestModule.ModuleVersionId:N}");

    /// <summary>The install's fingerprint, its forges as they stood when it was opened (<see cref="GameInstall.PatchForgeStamp"/>,
    /// <see cref="GameInstall.BaseForgeStamp"/>): a forge rewritten while the tool is open never gives its stamp to marks of the old one.</summary>
    public static EditMarksFingerprint Of(GameInstall g)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(g.BodySkeletonRecord);
        h.AppendData(g.AddOnSkeletonRecord);
        return new EditMarksFingerprint(IndexFingerprint.Of(g), g.BaseForgeStamp.Length, g.BaseForgeStamp.WriteTimeUtcTicks, Convert.ToHexStringLower(h.GetHashAndReset()),
            ThisMethod);
    }
}

/// <summary>An install's edit marks had through the cache: the marks, how they were had, how long that took, and why the cache couldn't
/// be written, when it couldn't (the marks are whole either way).</summary>
public sealed record CachedEditMarks(EditMarks Marks, CacheOutcome Outcome, TimeSpan Time, string? SaveError);

/// <summary>The cache of an install's edit marks (<see cref="EditMarks"/>: working them out tries the edits on every clip, about a quarter
/// of a minute on all but one core on today's files), in the clip index's cache folder: one file per patch forge (<c>edits-</c> and 16 hex digits of the SHA-256 of its full
/// path), holding the fingerprint it was built for (<see cref="EditMarksFingerprint"/>). <see cref="Read"/> gives the marks when the
/// file's fingerprint is the install's; otherwise, or when the file is damaged or not this tool's, the marks are worked out again and
/// <see cref="Save"/> writes the file anew, through the writers' guard (never in a folder of the game's).</summary>
public sealed class EditMarksCache(string folder)
{
    public const string Magic = "GRB Anim Tool edit marks cache";
    /// <summary>The file's format: 2 holds the clips the tool failed on as [ID, -1].</summary>
    public const int FileFormat = 2;

    public string Folder { get; } = Path.GetFullPath(folder);

    public string FileFor(GameInstall g)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(g.PatchForgePath).ToUpperInvariant()));
        return Path.Combine(Folder, $"edits-{Convert.ToHexStringLower(h)[..16]}.json");
    }

    /// <summary>The install's edit marks from the cache when it holds them for the install's files and this build's edits; else null, with
    /// how they will be had (<paramref name="outcome"/>: no cache, one for other files or another build, or one that doesn't read).</summary>
    public CachedEditMarks? Read(GameInstall g, out CacheOutcome outcome)
    {
        var sw = Stopwatch.StartNew();
        outcome = CacheOutcome.Built;
        string file = FileFor(g);
        if (!File.Exists(file)) return null;
        switch (TryRead(file, EditMarksFingerprint.Of(g), g.Clips.Select(c => c.Id).ToHashSet(), out var marks))
        {
            case ReadResult.Ok: outcome = CacheOutcome.Reused; return new CachedEditMarks(marks!, outcome, sw.Elapsed, null);
            case ReadResult.OtherFingerprint: outcome = CacheOutcome.RebuiltChanged; return null;
            default: outcome = CacheOutcome.RebuiltUnreadable; return null;
        }
    }

    /// <summary>Keeps marks just worked out from the files <paramref name="fp"/> describes as they stood before the work (had as
    /// <paramref name="outcome"/> says, in <paramref name="time"/>), through the writers' guard; the clips the tool failed on are kept as not
    /// told (the same files and build fail the same way). Marks that may not hold on the next start (<see cref="EditMarks.Incomplete"/>), or
    /// that don't cover exactly the install's clips, told or not, aren't kept: the save error says why (they are worked out again next
    /// time).</summary>
    public CachedEditMarks Save(GameInstall g, EditMarksFingerprint fp, EditMarks marks, CacheOutcome outcome, TimeSpan time)
    {
        string? saveError = marks.Incomplete is { } why ? $"they may not hold next time: {why}"
            : marks.Count + marks.Unknown.Count != g.Clips.Select(c => c.Id).Distinct().Count() || g.Clips.Any(c => marks.MaskFor(c) is null && !marks.Unknown.Contains(c.Id))
                ? "they don't cover every clip in the game files"
            : null;
        if (saveError is null)
        {
            try { ClipExport.WriteFile(FileFor(g), ClipExport.ProtectedFolders(g), fs => Write(fs, fp, marks), makeFolder: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExportException) { saveError = ex.Message; }
        }
        return new CachedEditMarks(marks, outcome, time, saveError);
    }

    internal enum ReadResult { Ok, OtherFingerprint, Unreadable }

    /// <summary>The longest cache file read: the game's is about a quarter of a megabyte.</summary>
    const long LongestFile = 16L << 20;

    /// <summary>Reads a cache file for an install of fingerprint <paramref name="fp"/> and clips <paramref name="clipIds"/>: the marks when
    /// the file is this tool's, whole, for that fingerprint and exactly those clips; otherwise why not.</summary>
    internal static ReadResult TryRead(string file, EditMarksFingerprint fp, HashSet<ulong> clipIds, out EditMarks? marks)
    {
        marks = null;
        try
        {
            if (new FileInfo(file).Length > LongestFile) return ReadResult.Unreadable;
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "magic") != Magic) return ReadResult.Unreadable;
            // this tool's file of another format (an earlier version's): marks worked out another way, not a damaged file
            if (root.GetProperty("format").GetInt32() != FileFormat) return ReadResult.OtherFingerprint;
            if (ReadFingerprint(root.GetProperty("fingerprint")) != fp) return ReadResult.OtherFingerprint;
            var of = new Dictionary<ulong, byte>();
            var unknown = new HashSet<ulong>();
            foreach (var c in root.GetProperty("clips").EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() != 2) return ReadResult.Unreadable;
                ulong id = c[0].GetUInt64();
                int mask = c[1].GetInt32();
                // -1: a clip the tool failed on, not told
                if (mask is < -1 or > EditMarks.AllEdits || !clipIds.Contains(id) || of.ContainsKey(id) || unknown.Contains(id)) return ReadResult.Unreadable;
                if (mask < 0) unknown.Add(id); else of.Add(id, (byte)mask);
            }
            if (of.Count + unknown.Count != clipIds.Count) return ReadResult.Unreadable;
            marks = EditMarks.From(of, unknown: unknown);
            return ReadResult.Ok;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException
            or OverflowException or IOException or UnauthorizedAccessException)
        {
            return ReadResult.Unreadable;
        }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static EditMarksFingerprint ReadFingerprint(JsonElement f) => new(
        new IndexFingerprint(f.GetProperty("forgeLength").GetInt64(), f.GetProperty("forgeWriteTimeUtcTicks").GetInt64(), f.GetProperty("datafileId").GetUInt64(),
            f.GetProperty("datafileSize").GetUInt32(), Str(f, "datafileTableSha256") ?? "", f.GetProperty("factsVersion").GetInt32()),
        f.GetProperty("baseForgeLength").GetInt64(), f.GetProperty("baseForgeWriteTimeUtcTicks").GetInt64(), Str(f, "skeletonSha256") ?? "", Str(f, "method") ?? "");

    /// <summary>Writes the marks and the fingerprint they were worked out for as the cache's JSON: each clip as [ID, bits], by ID, a clip
    /// not told as [ID, -1].</summary>
    internal static void Write(Stream s, EditMarksFingerprint fp, EditMarks marks)
    {
        using var w = new Utf8JsonWriter(s);
        w.WriteStartObject();
        w.WriteString("magic", Magic);
        w.WriteNumber("format", FileFormat);
        w.WriteStartObject("fingerprint");
        w.WriteNumber("forgeLength", fp.Install.ForgeLength);
        w.WriteNumber("forgeWriteTimeUtcTicks", fp.Install.ForgeWriteTimeUtcTicks);
        w.WriteNumber("datafileId", fp.Install.DatafileId);
        w.WriteNumber("datafileSize", fp.Install.DatafileSize);
        w.WriteString("datafileTableSha256", fp.Install.DatafileTableSha256);
        w.WriteNumber("factsVersion", fp.Install.FactsVersion);
        w.WriteNumber("baseForgeLength", fp.BaseForgeLength);
        w.WriteNumber("baseForgeWriteTimeUtcTicks", fp.BaseForgeWriteTimeUtcTicks);
        w.WriteString("skeletonSha256", fp.SkeletonSha256);
        w.WriteString("method", fp.Method);
        w.WriteEndObject();
        w.WriteStartArray("clips");
        foreach (var (id, mask) in marks.Masks.Select(k => (k.Key, (int)k.Value)).Concat(marks.Unknown.Select(id => (id, -1))).OrderBy(k => k.Item1))
        {
            w.WriteStartArray();
            w.WriteNumberValue(id);
            w.WriteNumberValue(mask);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
    }
}
