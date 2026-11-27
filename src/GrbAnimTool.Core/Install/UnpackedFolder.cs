using System.Globalization;
using System.Security.Cryptography;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.Core.Install;

/// <summary>The animation datafile's unpacked folder, where players install record files by hand: one file per record,
/// <c>index_-_name.Animation</c>, and their own files beside the game's. A repack sorts the files by the number before <c>_-_</c> and keeps
/// the first file of each ID, so a <c>1_-_</c> file is loaded in place of the game's own, which stays in the folder. The tool only reads it.
/// <para>Each file the forge's datafile doesn't hold byte for byte becomes a row of its own (<see cref="ClipEntry.FromFile"/>), read by its
/// own content; a file that holds what the forge holds is the forge's row (it gets the file's path and number).</para></summary>
public static class UnpackedFolder
{
    /// <summary>The most files read, and the largest file: a folder or file past them is refused, not read in part.</summary>
    public const int MaxFiles = 50_000;
    public const long MaxFileBytes = 64L << 20;

    /// <summary>Where the unpack of <paramref name="patchForge"/> puts the animation datafile's records: beside the forge,
    /// <c>Extracted\&lt;forge file&gt;\Extracted\&lt;index&gt;_-_&lt;name&gt;.data\</c>.</summary>
    public static string PathFor(string patchForge, string recordFolderName) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(patchForge)) ?? "", "Extracted", Path.GetFileName(patchForge), "Extracted", recordFolderName);

    /// <summary>One record file as read: its path, the number and name its file name gives (number null when there is none), its bytes,
    /// and the record it stands for or why it can't be read.</summary>
    sealed record FileRead(string Path, int? Number, string Name, byte[]? Content, ulong? Id, byte[]? Record, ClipRecord? Clip, string? Error);

    /// <summary>The rows of <paramref name="folder"/> beside <paramref name="forgeRows"/> (the forge's clips, by index): the forge's rows, each
    /// given its file when one holds its record, then a row for each file that holds something else, grouped by ID after the forge's row.
    /// A folder past <see cref="MaxFiles"/> is refused with an <see cref="InvalidDataException"/>.</summary>
    public static List<ClipEntry> Rows(string folder, IReadOnlyList<ClipEntry> forgeRows, VanillaClipHashes? vanilla)
    {
        var paths = new List<string>();
        foreach (var f in Directory.EnumerateFiles(folder, "*" + RecordFile.ClipExtension, SearchOption.TopDirectoryOnly))
        {
            if (paths.Count == MaxFiles) throw new InvalidDataException($"the unpacked folder {folder} holds more than {MaxFiles:N0} clip files");
            paths.Add(f);
        }
        var reads = new FileRead[paths.Count];
        Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => reads[i] = Read(paths[i]));

        var forgeById = new Dictionary<ulong, ClipEntry>();
        foreach (var c in forgeRows) forgeById.TryAdd(c.Id, c);
        var forgeByName = new Dictionary<string, ClipEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in forgeRows) forgeByName.TryAdd(c.Name, c);

        // an unreadable file is grouped by its ID when it has one, else by the forge's clip of its name
        ulong GroupOf(FileRead r) => r.Id ?? (forgeByName.TryGetValue(r.Name, out var c) ? c.Id : 0);
        var groups = reads.GroupBy(GroupOf).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Number ?? int.MinValue).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList());

        var attached = new Dictionary<ClipEntry, ClipEntry>(ReferenceEqualityComparer.Instance);
        var extra = new Dictionary<ulong, List<ClipEntry>>();
        foreach (var (id, files) in groups)
        {
            var forge = forgeById.GetValueOrDefault(id);
            var forgeContent = forge is null ? null : ContentOf(forge);
            var numbers = files.Where(f => f.Number is not null).Select(f => f.Number!.Value).ToList();
            int? lowest = numbers.Count > 0 ? numbers.Min() : null;
            bool orderUnknown = files.Count > 1 && (files.Any(f => f.Number is null) || numbers.Count(n => n == lowest) > 1);
            // a second file of the same bytes adds nothing: the first one stands for both, and keeps the others' numbers
            var kinds = new List<(FileRead First, List<int> Others)>();
            foreach (var f in files)
            {
                int k = f.Content is { } c ? kinds.FindIndex(x => x.First.Content is { } fc && fc.AsSpan().SequenceEqual(c)) : -1;
                if (k >= 0) { if (f.Number is int num) kinds[k].Others.Add(num); }
                else kinds.Add((f, []));
            }
            foreach (var (f, others) in kinds)
            {
                bool loadsFirst = files.Count > 1 && f.Number is not null && f.Number == lowest && !orderUnknown;
                // the game's own file in its own place (its number the record's index): loading first doesn't make it a mod (a small
                // datafile's clip at record 0 loads before any 1_-_ file)
                bool ownPlace = forge is not null && f.Number == forge.Index;
                // by its name or load order, or by its bytes: a file that isn't the game's own by the table of the game's clips (one
                // copied over the game's file under its number, say) is the player's mod too
                bool modded = f.Number == 1 && !ownPlace || loadsFirst && !ownPlace
                    || f.Record is { } fr && f.Id is { } frId && vanilla?.StatusOf(frId, fr) is ClipStatus.Replaced or ClipStatus.Added;
                string? note = Note(f, forge, files.Count, orderUnknown);
                // a 1_-_ file that a lower number loads before (a small datafile's record 0): said, the game doesn't use it
                if (f.Number == 1 && lowest is int lo && lo < 1 && !orderUnknown)
                    note = (note is null ? "" : note + "; ") + $"the game loads the file numbered {lo} first, so this one isn't used: give it the game's own file's name ({lo}_-_...) in its place";
                if (forge is not null && forgeContent is not null && f.Content is not null && f.Content.AsSpan().SequenceEqual(forgeContent) && !attached.ContainsKey(forge))
                {
                    attached[forge] = Copy(forge, f, modded, note, others);
                    continue;
                }
                // the folder's datafile, and the skeleton of the forge's clip of this ID (else the datafile's first clip's)
                var like = forge ?? (forgeRows.Count > 0 ? forgeRows[0] : null);
                var row = new ClipEntry
                {
                    Index = f.Number ?? -1, Id = f.Id ?? id, Name = f.Name, Bytes = f.Record ?? [], Record = f.Clip, ParseError = f.Error,
                    Status = f.Record is { } rec && f.Id is { } fid ? vanilla?.StatusOf(fid, rec) ?? ClipStatus.Unknown : ClipStatus.Unknown,
                    FilePath = f.Path, FileNumber = f.Number, FromFile = true, Modded = modded, Note = note, SameFileNumbers = others,
                    Key = RowKeyOf(id, f.Content ?? System.Text.Encoding.UTF8.GetBytes(f.Path)),
                    DatafileName = like?.DatafileName ?? "", DatafileIndex = like?.DatafileIndex ?? 0, DatafileId = like?.DatafileId ?? 0,
                    InPatchForge = like?.InPatchForge ?? true, OnSkeleton = like?.OnSkeleton ?? Skeletons.ClipSkeleton.Body, AlsoIn = forge?.AlsoIn ?? [],
                };
                (extra.TryGetValue(id, out var l) ? l : extra[id] = []).Add(row);
            }
        }

        var rows = new List<ClipEntry>(forgeRows.Count + extra.Values.Sum(l => l.Count));
        var done = new HashSet<ulong>();
        foreach (var c in forgeRows)
        {
            rows.Add(attached.GetValueOrDefault(c) ?? c);
            if (done.Add(c.Id) && extra.TryGetValue(c.Id, out var l)) rows.AddRange(l);
        }
        // files of an ID the forge doesn't hold, in the order of their numbers
        foreach (var (id, l) in extra.Where(k => !forgeById.ContainsKey(k.Key)).OrderBy(k => k.Value.Min(r => r.FileNumber ?? int.MinValue)))
            rows.AddRange(l);
        return rows;
    }

    /// <summary>What a row's list says of the file beyond its status: the order the repack can't settle, a file that names another clip
    /// than its record, or that the game doesn't load this file.</summary>
    static string? Note(FileRead f, ClipEntry? forge, int files, bool orderUnknown)
    {
        var l = new List<string>();
        if (f.Number is null) l.Add($"{System.IO.Path.GetFileName(f.Path)} has no index before _-_, so where a repack puts it isn't known");
        if (orderUnknown) l.Add("another file of this clip has the same index or none, so which of them the game loads isn't known");
        if (forge is not null && f.Id is not null && !string.Equals(forge.Name, f.Name, StringComparison.OrdinalIgnoreCase))
            l.Add($"the file is named {f.Name}, but its record is the clip 0x{f.Id:X16}, {forge.Name}: the game finds it by that ID");
        return l.Count > 0 ? string.Join("; ", l) : null;
    }

    static ClipEntry Copy(ClipEntry c, FileRead f, bool modded, string? note, List<int> others) => new()
    {
        Index = c.Index, Id = c.Id, Name = c.Name, Bytes = c.Bytes, Record = c.Record, ParseError = c.ParseError, Status = c.Status,
        FilePath = f.Path, FileNumber = f.Number, Modded = modded, Note = note, SameFileNumbers = others,
        DatafileName = c.DatafileName, DatafileIndex = c.DatafileIndex, DatafileId = c.DatafileId, InPatchForge = c.InPatchForge,
        OnSkeleton = c.OnSkeleton, AlsoIn = c.AlsoIn, Key = c.FromFile || c.Key == c.Id ? 0 : c.Key,
    };

    static byte[]? ContentOf(ClipEntry c)
    {
        try { return RecordFile.Content(c.Bytes.Span); }
        catch (RecordFileException) { return null; }
    }

    /// <summary>A file row's key: the top bit set, so it never meets a clip ID (those are far below 2^63), and 63 bits of the file's SHA-256,
    /// so the same bytes give the same key from run to run.</summary>
    /// <para>The clip's ID is hashed in too: the same bytes in two clips' groups (two broken files, say) are two rows; within one group
    /// equal bytes are one row already.</para>
    internal static ulong RowKeyOf(ulong id, ReadOnlySpan<byte> content)
    {
        var buf = new byte[8 + content.Length];
        BitConverter.TryWriteBytes(buf, id);
        content.CopyTo(buf.AsSpan(8));
        return (1UL << 63) | (BitConverter.ToUInt64(SHA256.HashData(buf), 0) & ~(1UL << 63));
    }

    /// <summary>The number and name a record file's name gives: <c>index_-_name.Animation</c>; no number when the part before the first
    /// <c>_-_</c> isn't one (or there is no <c>_-_</c>, and the whole stem is the name).</summary>
    internal static (int? Number, string Name) NameOf(string fileName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        int sep = stem.IndexOf("_-_", StringComparison.Ordinal);
        if (sep < 0) return (null, stem);
        return (int.TryParse(stem.AsSpan(0, sep), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null, stem[(sep + 3)..]);
    }

    static FileRead Read(string path)
    {
        var (number, name) = NameOf(path);
        byte[] content;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) return new(path, number, name, null, null, null, null, $"the file is {info.Length:N0} B, more than a clip file can be ({MaxFileBytes:N0} B)");
            content = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(path, number, name, null, null, null, null, $"the file can't be read: {ex.Message}");
        }
        var id = RecordFile.IdOf(content);
        if (id is null) return new(path, number, name, content, null, null, null, "the file doesn't start with a record head this tool reads");
        byte[] record;
        try { record = RecordFile.ToRecord(content, name); }
        catch (RecordFileException ex) { return new(path, number, name, content, id, null, null, $"the file isn't a record this tool reads: {ex.Message}"); }
        try
        {
            var clip = ClipRecord.Parse(record);
            if (clip.Id != id) return new(path, number, name, content, id, record, null, $"the clip's own ID 0x{clip.Id:X16} isn't the file's 0x{id:X16}");
            return new(path, number, name, content, id, record, clip, null);
        }
        // a player's file is read as untrusted: any failure of the parse refuses this file alone
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(path, number, name, content, id, record, null, $"the file's clip can't be read: {ex.Message}"); }
    }
}
