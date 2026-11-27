using System.Buffers.Binary;
using System.Diagnostics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Forge;
using GrbAnimTool.Core.Oodle;
using GrbAnimTool.Core.Records;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Install;

/// <summary>The install can't be read: a file is missing or can't be opened, an entry the tool needs isn't where it should be, or what it
/// holds is damaged. The message is meant for the user and names what failed.</summary>
public sealed class GameInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One clip of the animation datafile: its place in the datafile, ID, name, the record's bytes, the parsed record (decoded once and
/// kept), and how it stands against the game's own clip.</summary>
public sealed class ClipEntry
{
    /// <summary>The record's index within its datafile; a record file for this clip is named "index_-_name.Animation".</summary>
    public required int Index { get; init; }
    public required ulong Id { get; init; }
    public required string Name { get; init; }

    ReadOnlyMemory<byte> _bytes;
    ClipRecord? _record;
    string? _parseError;
    Lazy<(ReadOnlyMemory<byte> Bytes, ClipRecord? Record, string? Error)>? _load;

    /// <summary>The whole record as the datafile holds it (a view into the decoded datafile). A row of another datafile than the
    /// animation one reads it from the game's files the first time it is asked for (<see cref="Loaded"/>).</summary>
    public required ReadOnlyMemory<byte> Bytes { get => _load is null ? _bytes : _load.Value.Bytes; init => _bytes = value; }
    /// <summary>The parsed record, or null when its head can't be read (<see cref="ParseError"/> says why).</summary>
    public ClipRecord? Record { get => _load is null ? _record : _load.Value.Record; init => _record = value; }
    public string? ParseError { get => _load is null ? _parseError : _load.Value.Error; init => _parseError = value; }
    public ClipStatus Status { get; init; }

    /// <summary>Reads the record when it is first asked for (another datafile's row): what it gives stands for <see cref="Bytes"/>,
    /// <see cref="Record"/> and <see cref="ParseError"/>; read once, whatever thread asks.</summary>
    internal Func<(ReadOnlyMemory<byte> Bytes, ClipRecord? Record, string? Error)> Load
    {
        init => _load = new(value, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Whether the record is in memory (always, but for another datafile's row not yet read).</summary>
    public bool Loaded => _load is null || _load.IsValueCreated;

    /// <summary>What the list shows of a row not read yet: its length, ticks, tracks and whether its curves read to its end, as the tool
    /// found them when it last read the datafile.</summary>
    internal (float Duration, int LastTick, int Tracks, bool Walked)? Facts { get; init; }

    /// <summary>The clip's length in seconds, or 0 when the record states none in range (<see cref="ClipRecord.DurationValid"/>).</summary>
    public float Duration => !Loaded && Facts is { } f ? f.Duration : Record is { DurationValid: true } r ? r.Duration : 0;
    /// <summary>The clip's length in ticks of 1/60 s (duration x 60, rounded); it has one more frame than this, tick 0 included.</summary>
    public int LastTick => !Loaded && Facts is { } f ? f.LastTick : Record?.LastTick ?? 0;
    public int TrackCount => !Loaded && Facts is { } f ? f.Tracks : Record?.Tracks.Count ?? 0;
    public bool WalkedToEnd => !Loaded && Facts is { } f ? f.Walked : Record?.WalkedToEnd ?? false;

    /// <summary>The skeleton the clip moves (<see cref="ClipSkeleton"/>): the human body the tool poses the player's clips on, another of
    /// the game's skeletons, or none the tool can name.</summary>
    public ClipSkeleton OnSkeleton { get; init; } = ClipSkeleton.Body;

    /// <summary>The other datafiles that hold this same clip (its ID), each a place a replacement goes too; empty for most clips.</summary>
    public IReadOnlyList<DatafilePlace> AlsoIn { get; init; } = [];

    /// <summary>The record file of the unpacked datafile folder this row stands for, or null (no unpacked folder, or no file holds it).</summary>
    public string? FilePath { get; init; }
    /// <summary>The number before <c>_-_</c> in <see cref="FilePath"/>'s name, or null when it has none (or there is no file).</summary>
    public int? FileNumber { get; init; }
    /// <summary>A row of a file whose record the forge's datafile doesn't hold: read from the file alone (<see cref="UnpackedFolder"/>).</summary>
    public bool FromFile { get; init; }
    /// <summary>The file replaces the game's own version: it is named <c>1_-_...</c>, or it is the first of two or more files of this ID.</summary>
    public bool Modded { get; init; }
    /// <summary>What else the list says of this row's file (its load order unknown, another clip's record), or null.</summary>
    public string? Note { get; init; }
    /// <summary>The numbers of the folder's other files holding exactly this row's bytes (the game's own file beside a 1_-_ copy of it).</summary>
    public IReadOnlyList<int> SameFileNumbers { get; init; } = [];

    /// <summary>The datafile the record is in: its forge entry's name, its index in the forge (the prefix of its unpacked folder's name,
    /// <c>index_-_name.data</c>) and its ID. Empty for a row made outside an install.</summary>
    public string DatafileName { get; init; } = "";
    public int DatafileIndex { get; init; }
    public ulong DatafileId { get; init; }
    /// <summary>Whether the record is in the animation datafile (MIS_Y2E4_Katya_Maksimov), where the player's clips are.</summary>
    public bool InAnimationDatafile => DatafileId == GameInstall.AnimationDatafileId;
    /// <summary>Whether the datafile is the patch forge's (else the base forge's: the patch doesn't hold it).</summary>
    public bool InPatchForge { get; init; } = true;

    readonly ulong _key;
    /// <summary>The row's own key, for anything worked out per row: the clip's ID for a forge row, a key of its own for a
    /// <see cref="FromFile"/> row (the top bit set, from the file's bytes), so two rows of one clip never share what was worked out.</summary>
    public ulong Key { get => FromFile || _key != 0 ? _key : Id; init => _key = value; }

    /// <summary>The Status column's text: the status, with "modded" for a file that replaces the game's version.</summary>
    public string StatusText => !Modded ? Status.ToString() : Status == ClipStatus.Vanilla ? "Modded (vanilla)" : "Modded";
}

/// <summary>How long each step of opening the install took.</summary>
public sealed record OpenTimings(TimeSpan Oodle, TimeSpan ForgeIndex, TimeSpan AnimationDatafile, TimeSpan SharedTable, TimeSpan Skeletons, TimeSpan Clips, TimeSpan Total)
{
    /// <summary>Reading the other datafiles' lists (through their cache when there is one).</summary>
    public TimeSpan OtherDatafiles { get; init; }
}

/// <summary>A game install, opened read-only from its game folder: the patch forge's animation datafile with every clip, the shared curve
/// table, and the body skeleton merged with the fake-gun add-on. Entries are found by ID, never by index or by a forge's hash. The forges
/// are read once and closed; the decoded data stays in memory. Dispose to unload the Oodle library.
/// <para>Nothing here changes after <see cref="Open"/> returns, so it may be read from several threads. The byte arrays it hands out
/// (records, clips, curves) are shared, not copies: callers must not write into them.</para></summary>
public sealed class GameInstall : IDisposable
{
    public const string PatchForgeName = "DataPC_patch_01.forge";
    public const string BaseForgeName = "DataPC.forge";

    /// <summary>The animation datafile (MIS_Y2E4_Katya_Maksimov): the player's and most characters' gameplay clips.</summary>
    public const ulong AnimationDatafileId = 0x000001DE8F05D119;
    /// <summary>The body skeleton Regular_Male_Body_Skl.</summary>
    public const ulong BodySkeletonId = 0x00000165C847E2F3;
    /// <summary>The fake-gun add-on skeleton Player_FakeGun_Addon, whose bones carry tracks in player clips.</summary>
    public const ulong AddOnSkeletonId = 0x0000014F43AA55D7;

    public string GameFolder { get; }
    public string PatchForgePath { get; }
    public string BaseForgePath { get; }
    public OodleLibrary Oodle { get; }

    /// <summary>The animation datafile's forge entry in the user's patch forge. Its index is the prefix of the folder name its record files
    /// are installed under (46 on today's files, 24 on the 2023 files).</summary>
    public ForgeEntry AnimationEntry { get; }
    public int AnimationDatafileIndex => AnimationEntry.Index;
    /// <summary>The folder name the animation datafile's record files are installed under: "index_-_name.data".</summary>
    public string RecordFolderName => $"{AnimationEntry.Index}_-_{AnimationEntry.Name}.data";

    public IReadOnlyList<DatafileRecord> Records { get; }
    /// <summary>Every row the lists show: the forge's clips, and after each one the files of its ID in the unpacked folder that hold
    /// something else (<see cref="ClipEntry.FromFile"/>), then files of IDs the forge lacks.</summary>
    public IReadOnlyList<ClipEntry> Clips { get; }
    /// <summary>The clips of the forge's datafile alone, in its order: what the caches keyed by the forge are made of.</summary>
    public IReadOnlyList<ClipEntry> ForgeClips { get; }
    /// <summary>The unpacked datafile folder read (<see cref="UnpackedFolder"/>), or null when there is none.</summary>
    public string? UnpackedFolderPath { get; private init; }
    /// <summary>Why the unpacked folder beside the forge couldn't be read (its rows are then left out), or null.</summary>
    public string? UnpackedFolderError { get; private init; }
    /// <summary>The names of clips the tool read in another datafile than the animation one (the shared curve table's entry, which holds
    /// some of the graph's clips), by ID: the Log screen names them, though they aren't rows of the lists.</summary>
    public IReadOnlyDictionary<ulong, string> OtherClipNames { get; private init; } = new Dictionary<ulong, string>();
    public SharedCurveTable SharedTable { get; }
    public Skeleton BodySkeleton { get; }
    public Skeleton AddOnSkeleton { get; }
    /// <summary>The body skeleton with the add-on's bones appended: the skeleton clips are posed on.</summary>
    public Skeleton Skeleton { get; }
    public BoneNames Names { get; }
    /// <summary>The table of the game's own clips, or null when this build has none (every clip is then <see cref="ClipStatus.Unknown"/>).</summary>
    public VanillaClipHashes? Vanilla { get; }
    /// <summary>Clips of the game's own table that the user's datafile lacks.</summary>
    public int VanillaClipsMissing => Vanilla is null ? 0 : Vanilla.Ids.Count(id => !_byId.ContainsKey(id));
    public OpenTimings Timings { get; }

    readonly Dictionary<ulong, ClipEntry> _byId = [];

    /// <summary>The clips of every other datafile of the forges that holds any (one row a clip, at the first datafile that holds it; the
    /// others in <see cref="ClipEntry.AlsoIn"/>), their records read when first needed; then the rows of their unpacked folders' files.</summary>
    public IReadOnlyList<ClipEntry> OtherClips { get; private set; } = [];
    /// <summary>Every row: the animation datafile's (<see cref="Clips"/>), then the other datafiles' (<see cref="OtherClips"/>).</summary>
    public IReadOnlyList<ClipEntry> AllClips { get; private set; } = [];
    /// <summary>What reading the other datafiles found: the datafiles, the entries looked at, those that didn't read, the cache's part.</summary>
    public OtherDatafiles? Others { get; private set; }
    /// <summary>Why an other datafile's unpacked folder couldn't be read (its rows then left out), one line each.</summary>
    public IReadOnlyList<string> OtherUnpackedErrors { get; private set; } = [];

    // the forges' entries by ID (where each payload stands), to read a skeleton record later; the patch's first
    Dictionary<ulong, ForgeEntry> _patchEntries = [], _baseEntries = [];

    GameInstall(string game, string patch, string baseForge, OodleLibrary oodle, ForgeEntry animEntry, List<DatafileRecord> records, List<ClipEntry> clips,
        SharedCurveTable table, Skeleton body, Skeleton addOn, BoneNames names, VanillaClipHashes? vanilla, OpenTimings timings)
    {
        GameFolder = game; PatchForgePath = patch; BaseForgePath = baseForge; Oodle = oodle; AnimationEntry = animEntry; Records = records; Clips = clips;
        ForgeClips = clips.Where(c => !c.FromFile).ToList();
        SharedTable = table; BodySkeleton = body; AddOnSkeleton = addOn; Skeleton = Skeleton.Merge(body, addOn); Names = names; Vanilla = vanilla; Timings = timings;
        foreach (var c in clips) _byId.TryAdd(c.Id, c);
        AllClips = clips;
        // every writer's guard knows the install's own records, to tell the game's unpacked files from a folder of the player's own
        IO.FolderGuard.Remember(this);
    }

    /// <summary>Takes the other datafiles' lists: a row each clip (by ID, at the first datafile holding it), its record read from its
    /// forge when first asked for; then each datafile's unpacked folder beside its forge, when there is one, as the animation datafile's.</summary>
    void TakeOthers(OtherDatafiles others, VanillaClipHashes? vanilla, string? unpackedRoot)
    {
        Others = others;
        var katya = Clips.Select(c => c.Id).ToHashSet();
        var places = new Dictionary<ulong, List<(OtherDatafile D, OtherClip C)>>();
        var order = new List<ulong>();
        foreach (var d in others.Datafiles.OrderBy(d => !d.InPatch).ThenBy(d => d.Index))
            foreach (var c in d.Clips)
            {
                if (!places.TryGetValue(c.Id, out var l)) { places[c.Id] = l = []; order.Add(c.Id); }
                l.Add((d, c));
            }
        // a clip as one datafile holds it: the row of its first datafile, or a stand-in for its copy in another (see below)
        ClipEntry RowAt(OtherDatafile d, OtherClip c, IReadOnlyList<(OtherDatafile D, OtherClip C)> l, ClipStatus status) => new()
        {
            Index = c.RecordIndex, Id = c.Id, Name = c.Name, Bytes = ReadOnlyMemory<byte>.Empty, Load = () => LoadOther(d, c),
            Facts = (c.Duration, c.LastTick, c.Tracks, c.Walked),
            Status = status,
            OnSkeleton = c.Skeleton, DatafileName = d.Name, DatafileIndex = d.Index, DatafileId = d.Id, InPatchForge = d.InPatch,
            AlsoIn = l.Where(x => x.D != d).Select(x => new DatafilePlace(x.D.InPatch ? PatchForgePath : BaseForgePath, x.D.Index, x.D.Id, x.D.Name, x.C.RecordIndex)).ToList(),
            // a clip of the animation datafile's ID in another datafile too: a row of its own, never sharing its key
            Key = katya.Contains(c.Id) ? UnpackedFolder.RowKeyOf(d.Id, BitConverter.GetBytes(c.Id)) : 0,
        };
        ClipStatus StatusOf(OtherClip c) => vanilla?.StatusOfSha256(c.Id, c.Sha256) ?? ClipStatus.Unknown;
        var rows = new List<ClipEntry>(order.Count);
        foreach (var id in order)
        {
            var l = places[id];
            // the clip is the game's own only when every datafile's copy is; else it shows as the first copy that isn't
            var status = l.Select(x => StatusOf(x.C)).FirstOrDefault(s => s != ClipStatus.Vanilla, ClipStatus.Vanilla);
            rows.Add(RowAt(l[0].D, l[0].C, l, status));
        }
        // each other datafile's unpacked folder, as the animation datafile's: its files' rows beside the forge's. The folder holds the
        // game's own file of every clip the datafile holds, those whose row stands at another datafile too: each of those is read against
        // a stand-in for the datafile's own copy, which is then left out of the list (the game's own file of it is no row; a file of the
        // player's beside it is)
        var errors = new List<string>();
        foreach (var forge in new[] { PatchForgePath, BaseForgePath })
        {
            // the unpack's own place beside each forge, or the one root given (for the patch forge's datafiles alone)
            string root = unpackedRoot is null ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(forge)) ?? "", "Extracted", Path.GetFileName(forge), "Extracted")
                : forge == PatchForgePath ? unpackedRoot : "";
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            bool inPatch = forge == PatchForgePath;
            foreach (var d in others.Datafiles.Where(d => d.InPatch == inPatch))
            {
                string folder = Path.Combine(root, RecordFile.DatafileFolderName(d.Index, d.Name));
                if (!Directory.Exists(folder)) continue;
                var mine = rows.Where(r => !r.FromFile && r.DatafileId == d.Id && r.InPatchForge == inPatch).ToList();
                var mineIds = mine.Select(r => r.Id).ToHashSet();
                var stand = d.Clips.Where(c => !mineIds.Contains(c.Id)).Select(c => RowAt(d, c, places[c.Id], StatusOf(c))).ToList();
                var standIds = stand.Select(r => r.Id).ToHashSet();
                try
                {
                    var got = UnpackedFolder.Rows(folder, [.. mine, .. stand], vanilla)
                        .Where(r => r.FromFile || !standIds.Contains(r.Id)).ToList();
                    int at = mine.Count > 0 ? rows.IndexOf(mine[0]) : rows.Count;
                    foreach (var r in mine) rows.Remove(r);
                    rows.InsertRange(Math.Min(at, rows.Count), got);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { errors.Add($"The unpacked folder {folder} couldn't be read, so its files aren't listed: {ex.Message}"); }
            }
        }
        OtherUnpackedErrors = errors;
        OtherClips = rows;
        AllClips = [.. Clips, .. rows];
        foreach (var r in rows) _byId.TryAdd(r.Id, r);
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<(bool, ulong), Lazy<List<DatafileRecord>>> _otherRecords = new();

    /// <summary>An other datafile's clip record, read from its forge now (the datafile decoded once, its clip records kept); a failure is
    /// said, not thrown.</summary>
    (ReadOnlyMemory<byte>, ClipRecord?, string?) LoadOther(OtherDatafile d, OtherClip c)
    {
        try
        {
            using var hold = Hold();
            var recs = _otherRecords.GetOrAdd((d.InPatch, d.Id), _ => new Lazy<List<DatafileRecord>>(() =>
                OtherDatafiles.ReadRecords(d.InPatch ? PatchForgePath : BaseForgePath, d, Oodle).Where(r => r.Type == ClipRecord.Type)
                    .Select(r => new DatafileRecord { Index = r.Index, Id = r.Id, Type = r.Type, Name = r.Name, Bytes = r.Bytes.ToArray(), Opaque = r.Opaque }).ToList())).Value;
            return OtherDatafiles.RecordOf(recs, c.RecordIndex, c.Id);
        }
        catch (ObjectDisposedException) { return (ReadOnlyMemory<byte>.Empty, null, "the game folder was closed before its record was read"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OodleException)
        {
            return (ReadOnlyMemory<byte>.Empty, null, $"its record can't be read from {d.Index}_-_{d.Name}: {ex.Message}");
        }
    }

    /// <summary>Every place an other datafile's clip is (its first datafile's, then those of <see cref="ClipEntry.AlsoIn"/>), each with
    /// the record's index in that datafile; empty for the animation datafile's clips. A row of an unpacked folder's file stands for the
    /// forge's clip of its ID.</summary>
    public IReadOnlyList<DatafilePlace> PlacesOf(ClipEntry c)
    {
        if (c.InAnimationDatafile || c.DatafileName.Length == 0) return [];
        // a file's row (in any of the clip's datafiles' folders) stands for the clip's row at its first datafile
        var row = c.FromFile ? OtherClips.FirstOrDefault(x => !x.FromFile && x.Id == c.Id) ?? c : c;
        if (row.FromFile) return [];
        return [new DatafilePlace(row.InPatchForge ? PatchForgePath : BaseForgePath, row.DatafileIndex, row.DatafileId, row.DatafileName, row.Index), .. row.AlsoIn];
    }

    /// <summary>The record an other datafile holds at <paramref name="p"/> for clip <paramref name="id"/>, read now (null when it can't be).</summary>
    public byte[]? RecordAt(DatafilePlace p, ulong id)
    {
        bool inPatch = p.ForgePath == PatchForgePath;
        var d = Others?.Datafiles.FirstOrDefault(x => x.InPatch == inPatch && x.Id == p.Id);
        var c = d?.Clips.FirstOrDefault(x => x.RecordIndex == p.RecordIndex && x.Id == id);
        if (d is null || c is null) return null;
        var (bytes, _, err) = LoadOther(d, c);
        return err is null || bytes.Length > 0 ? bytes.ToArray() : null;
    }

    /// <summary>The graph documents of the other datafiles that hold any (read now, each datafile decoded once more), and why a datafile or a
    /// document didn't read: the sets of the other datafiles' clips are read from these, where they read.</summary>
    public (List<Graph.GraphDocument> Documents, List<string> Failures) OtherGraphDocuments()
    {
        var docs = new List<Graph.GraphDocument>();
        var failed = new List<string>();
        foreach (var d in Others?.Datafiles.Where(d => d.HasGraph) ?? [])
        {
            try
            {
                using var hold = Hold();
                foreach (var r in OtherDatafiles.ReadRecords(d.InPatch ? PatchForgePath : BaseForgePath, d, Oodle).Where(r => Graph.GraphDocument.IsDocumentType(r.Type)))
                {
                    try { docs.Add(Graph.GraphDocument.Read(r)); }
                    catch (Graph.GraphDocumentException ex) { failed.Add($"{d.Index}_-_{d.Name}: {r.Name}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OodleException or ObjectDisposedException) { failed.Add($"{d.Index}_-_{d.Name}: {ex.Message}"); }
        }
        return (docs, failed);
    }

    public ClipEntry? FindClip(ulong id) => _byId.GetValueOrDefault(id);
    public ClipEntry? FindClip(string name) => AllClips.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Lazy<(Skeleton? Skeleton, string Name, string? Error)>> _own = new();

    /// <summary>The skeleton a clip is posed on: the merged body skeleton for a clip on the body, the game's own skeleton record for one on
    /// another (read from the forges once), or null when the clip's skeleton isn't known or can't be read (<see cref="SkeletonName"/> says
    /// why).</summary>
    public Skeleton? SkeletonOf(ClipEntry c) => c.OnSkeleton.Kind switch
    {
        ClipSkeletonKind.Body => Skeleton,
        ClipSkeletonKind.Own => Own(c.OnSkeleton).Skeleton,
        _ => null,
    };

    /// <summary>The skeleton's name in the screens' words: "the human body", the game's skeleton record's name, or why it isn't known.</summary>
    public string SkeletonName(ClipSkeleton k) => k.Kind switch
    {
        ClipSkeletonKind.Body => "the human body",
        ClipSkeletonKind.Own => Own(k) is { Skeleton: not null } o ? $"the {o.Name} skeleton" : $"skeleton 0x{k.SkeletonId:X16} ({Own(k).Error})",
        _ => "a skeleton the tool can't determine",
    };

    (Skeleton? Skeleton, string Name, string? Error) Own(ClipSkeleton k) => _own.GetOrAdd(k.SkeletonId, _ => new(() =>
    {
        try
        {
            using var hold = Hold();
            foreach (var (path, entries) in new[] { (PatchForgePath, _patchEntries), (BaseForgePath, _baseEntries) })
            {
                if (!entries.TryGetValue(k.EntryId, out var e)) continue;
                using var fs = OpenForge(path);
                var r = DecodeRecords(ForgeReader.ReadPayload(fs, e), Oodle, $"entry 0x{e.Id:X16} in {path}").FirstOrDefault(x => x.Id == k.SkeletonId && x.Type == Skeletons.Skeleton.Type);
                if (r is null) continue;
                var names = BoneNames.Default();
                names.AddFrom(r.Bytes.Span);
                return ((Skeleton?)Skeletons.Skeleton.Parse(r.Bytes.Span, names), r.Name, (string?)null);
            }
            return (null, "", "its record isn't in the game's files");
        }
        catch (Exception ex) when (ex is GameInstallException or InvalidDataException or IOException or ObjectDisposedException or OodleException)
        {
            return (null, "", ex.Message);
        }
    })).Value;

    /// <summary>Why <paramref name="source"/>'s motion (null: a .glb) can't be put on <paramref name="slot"/> by its skeleton, or null when it
    /// can: the slot's skeleton must be known and, for a clip source, the source's too; then the two must be the same skeleton, or the
    /// source's skeleton must hold at least <see cref="ClipSkeletons.MapShare"/> of the bones the slot keys (the track map's rule then
    /// takes each by name). A .glb is checked against the slot's skeleton by the import itself.</summary>
    public string? ReplaceProblem(ClipEntry slot, ClipEntry? source)
    {
        if (SkeletonOf(slot) is null) return $"{slot.Name} is on {SkeletonName(slot.OnSkeleton)}, so it can't be edited.";
        if (source is null) return null;
        if (SkeletonOf(source) is not { } ss) return $"{source.Name} is on {SkeletonName(source.OnSkeleton)}, so its motion can't be put on {slot.Name}.";
        var a = slot.OnSkeleton; var b = source.OnSkeleton;
        if (a.Kind == b.Kind && (a.Kind == ClipSkeletonKind.Body || a.SkeletonId == b.SkeletonId)) return null;
        if (slot.Record is not { } rec) return null;
        double share = ClipSkeletons.Shared(ClipSkeletons.BoneTracks(rec, SharedTable), ss);
        return share >= ClipSkeletons.MapShare ? null
            : FormattableString.Invariant($"{slot.Name} is on {SkeletonName(a)} and {source.Name} on {SkeletonName(b)}: only {share * 100:F0}% of the bones {slot.Name} keys are in {source.Name}'s skeleton, so its motion can't be put on it.");
    }

    /// <summary>For the harness's table command, on the game's own files: every clip of the other datafiles not on the body, with the
    /// smallest skeleton record of the two forges holding every bone it keys (of equals, the patch's, then the lowest ID) and the entry
    /// holding that record; clips no one record holds are left out. Decodes every entry of both forges (a minute or so).</summary>
    public List<(ulong Clip, ulong Skeleton, ulong Entry)> ClipSkeletonTable()
    {
        var skels = new List<(ulong Id, ulong Entry, bool InPatch, HashSet<uint> Bones)>();
        foreach (var (path, entries, inPatch) in new[] { (PatchForgePath, _patchEntries, true), (BaseForgePath, _baseEntries, false) })
        {
            using var fs = OpenForge(path);
            foreach (var e in entries.Values)
            {
                List<DatafileRecord> recs;
                try { recs = DatafileCodec.Parse(DatafileCodec.Decode(ForgeReader.ReadPayload(fs, e), Oodle, out int t, out _), t); }
                catch (InvalidDataException) { continue; }
                foreach (var r in recs.Where(r => r.Type == Skeletons.Skeleton.Type))
                {
                    try { skels.Add((r.Id, e.Id, inPatch, Skeletons.Skeleton.Parse(r.Bytes.Span).Bones.Select(b => b.Hash).ToHashSet())); }
                    catch (InvalidDataException) { }
                }
            }
        }
        var rows = new List<(ulong, ulong, ulong)>();
        foreach (var c in OtherClips.Where(c => !c.FromFile && c.OnSkeleton.Kind != ClipSkeletonKind.Body).DistinctBy(c => c.Id))
        {
            if (c.Record is not { } rec) continue;
            var bones = ClipSkeletons.BoneTracks(rec, SharedTable);
            if (bones.Count == 0) continue;
            var best = skels.Where(s => bones.All(s.Bones.Contains)).OrderBy(s => s.Bones.Count).ThenBy(s => !s.InPatch).ThenBy(s => s.Id).FirstOrDefault();
            if (best.Bones is not null) rows.Add((c.Id, best.Id, best.Entry));
        }
        return rows;
    }

    /// <summary>A pose sampler for one clip on its own skeleton (<see cref="SkeletonOf"/>). Refuses a clip whose skeleton isn't known, or
    /// whose curves don't read to the record's end (or don't match its track list one to one): pairing curves and tracks by position would
    /// then give a wrong pose without a word.</summary>
    public PoseSampler CreateSampler(ClipEntry clip)
    {
        var record = clip.Record ?? throw new InvalidOperationException($"clip {clip.Name} can't be read: {clip.ParseError}");
        if (!record.WalkedToEnd) throw new InvalidOperationException($"clip {clip.Name} can't be played: {record.Stop}");
        var s = SkeletonOf(clip) ?? throw new InvalidOperationException($"clip {clip.Name} can't be played: its skeleton is {SkeletonName(clip.OnSkeleton)}");
        return new(s, record, SharedTable);
    }

    /// <summary>Opens the install in <paramref name="gameFolder"/>: its Oodle DLL (checked against the pins), its patch forge (or
    /// <paramref name="patchForge"/> instead, for tests and diagnostics) and its base forge, all read-only. Every failure comes out as a
    /// <see cref="GameInstallException"/> naming what failed, or an <see cref="OodleException"/> for the library itself; nothing else.
    /// <para>The animation datafile's unpacked folder (<paramref name="unpackedFolder"/>; null: the one beside the patch forge, when there
    /// is one; "": none) adds its files' rows (<see cref="UnpackedFolder"/>). A folder that can't be read leaves them out and says why
    /// (<see cref="UnpackedFolderError"/>); it never fails the open.</para></summary>
    /// <para>The other datafiles that hold clips are read too (<see cref="OtherClips"/>), through their cache in
    /// <paramref name="datafileCache"/> when given (the tool's own folder; null: every entry decoded, nothing kept), with their unpacked
    /// folders from <paramref name="otherUnpackedRoot"/> (the folder holding <c>index_-_name.data</c> folders; null: the unpack's own beside
    /// each forge; "": none).</para>
    public static GameInstall Open(string gameFolder, string? patchForge = null, string? unpackedFolder = null, string? datafileCache = null, string? otherUnpackedRoot = null)
    {
        var at = new OpenStep();
        try { return OpenSteps(gameFolder, patchForge, unpackedFolder, datafileCache, otherUnpackedRoot, at); }
        catch (Exception ex)
        {
            at.Oodle?.Dispose();
            if (ex is GameInstallException or OodleException) throw;
            throw new GameInstallException($"{at.Step} failed: {ex.Message}", ex);
        }
    }

    /// <summary>What <see cref="Open"/> is doing, for the message when it fails, and the library to unload then.</summary>
    sealed class OpenStep
    {
        public string Step = "Checking the game folder";
        public OodleLibrary? Oodle;
    }

    static GameInstall OpenSteps(string gameFolder, string? patchForge, string? unpackedFolder, string? datafileCache, string? otherUnpackedRoot, OpenStep at)
    {
        var total = Stopwatch.StartNew();
        if (!Directory.Exists(gameFolder)) throw new GameInstallException($"The game folder {gameFolder} doesn't exist.");
        string patch = patchForge ?? Path.Combine(gameFolder, PatchForgeName);
        string baseForge = Path.Combine(gameFolder, BaseForgeName);
        if (!File.Exists(patch)) throw new GameInstallException($"{PatchForgeName} wasn't found at {patch}. Pick the folder that holds the game's GRB.exe.");
        if (!File.Exists(baseForge)) throw new GameInstallException($"{BaseForgeName} wasn't found in {gameFolder}. Pick the folder that holds the game's GRB.exe.");
        // each forge's length and last write time before it is read: what the caches are keyed by (a forge rewritten while the install is
        // open never gives its new stamp to what was read from the old one)
        var patchStamp = Stamp(patch);
        var baseStamp = Stamp(baseForge);

        var sw = Stopwatch.StartNew();
        at.Step = $"Loading the game's Oodle library from {gameFolder}";
        var oodle = at.Oodle = OodleLibrary.Load(gameFolder);
        var tOodle = sw.Elapsed;

        ForgeEntry animEntry; List<DatafileRecord> records; SharedCurveTable table; Skeleton body, addOn; byte[] bodyRec, addOnRec;
        string? controlOn;
        OtherDatafiles others;
        ForgeLayout patchLay, baseLayout;
        TimeSpan tOthers;
        var otherNames = new Dictionary<ulong, string>();
        TimeSpan tIndex, tAnim, tTable, tSkel;
        var names = BoneNames.Default();
        using (var fs = OpenForge(patch))
        {
            sw.Restart();
            at.Step = $"Reading the index of {patch}";
            var lay = ReadLayout(fs, patch);
            tIndex = sw.Elapsed;

            sw.Restart();
            at.Step = $"Reading the animation datafile 0x{AnimationDatafileId:X16} from {patch}";
            animEntry = lay.Find(AnimationDatafileId) ?? throw new GameInstallException($"The animation datafile 0x{AnimationDatafileId:X16} isn't in {patch}.");
            var animPayload = ForgeReader.ReadPayload(fs, animEntry);
            at.Step = "The control decode of the game's Oodle library";
            controlOn = ControlDecode(oodle, animPayload, "the animation datafile") ? "the animation datafile" : null;
            at.Step = $"Decoding the animation datafile from {patch}";
            records = DecodeRecords(animPayload, oodle, $"the animation datafile in {patch}");
            tAnim = sw.Elapsed;

            sw.Restart();
            at.Step = $"Reading entry 0x{SharedCurveTable.EntryId:X} from {patch}";
            var boot = lay.Find(SharedCurveTable.EntryId) ?? throw new GameInstallException($"Entry 0x{SharedCurveTable.EntryId:X} isn't in {patch}.");
            var bootPayload = ForgeReader.ReadPayload(fs, boot);
            // a datafile stored without compression (possible in a forge the game didn't write) gives the control decode nothing to check:
            // the next entry read is tried instead
            if (controlOn is null)
            {
                at.Step = "The control decode of the game's Oodle library";
                controlOn = ControlDecode(oodle, bootPayload, $"entry 0x{SharedCurveTable.EntryId:X}") ? $"entry 0x{SharedCurveTable.EntryId:X}" : null;
            }
            var bootRecs = DecodeRecords(bootPayload, oodle, $"entry 0x{SharedCurveTable.EntryId:X} in {patch}");
            foreach (var r in bootRecs) if (r.Type == ClipRecord.Type && r.Name.Length > 0) otherNames.TryAdd(r.Id, r.Name);
            var tableRec = bootRecs.FirstOrDefault(r => r.Id == SharedCurveTable.RecordId)
                ?? throw new GameInstallException($"The shared curve table 0x{SharedCurveTable.RecordId:X} isn't in entry 0x{SharedCurveTable.EntryId:X}.");
            at.Step = $"Reading the shared curve table 0x{SharedCurveTable.RecordId:X} of entry 0x{SharedCurveTable.EntryId:X} in {patch}";
            table = SharedCurveTable.Parse(tableRec.Bytes.ToArray());
            if (!table.WalkedToEnd)
                throw new GameInstallException($"The shared curve table 0x{SharedCurveTable.RecordId:X} in {patch} is damaged: {table.Curves.Count} of its {table.DeclaredCount} curves were read ({table.Stop}).");
            tTable = sw.Elapsed;

            sw.Restart();
            using var bfs = OpenForge(baseForge);
            at.Step = $"Reading the index of {baseForge}";
            var baseLay = ReadLayout(bfs, baseForge);
            string bodyStep = $"Reading the body skeleton 0x{BodySkeletonId:X16}", addOnStep = $"Reading the add-on skeleton 0x{AddOnSkeletonId:X16}";
            at.Step = bodyStep;
            bodyRec = SkeletonRecord(BodySkeletonId, (fs, lay, patch), (bfs, baseLay, baseForge), oodle);
            at.Step = addOnStep;
            addOnRec = SkeletonRecord(AddOnSkeletonId, (fs, lay, patch), (bfs, baseLay, baseForge), oodle);
            names.AddFrom(bodyRec);
            names.AddFrom(addOnRec);
            at.Step = bodyStep;
            body = Skeleton.Parse(bodyRec, names);
            at.Step = addOnStep;
            addOn = Skeleton.Parse(addOnRec, names, addOn: true);
            tSkel = sw.Elapsed;
            patchLay = lay; baseLayout = baseLay;
        }

        sw.Restart();
        at.Step = "Reading the other datafiles that hold clips";
        others = OtherDatafiles.Read(patch, patchLay, patchStamp, baseForge, baseLayout, baseStamp, oodle, table, body, datafileCache,
            Export.ClipExport.ProtectedFolders(gameFolder, patch));
        tOthers = sw.Elapsed;

        sw.Restart();
        at.Step = "Reading the clips of the animation datafile";
        var vanilla = VanillaClipHashes.Embedded();
        var clips = new List<ClipEntry>();
        // each record gets an array of its own (a clip shares it with its parsed record), so the decoded datafile isn't kept twice
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var own = r.Bytes.ToArray();
            records[i] = new DatafileRecord { Index = r.Index, Id = r.Id, Type = r.Type, Name = r.Name, Bytes = own, Opaque = r.Opaque };
            if (r.Type != ClipRecord.Type) continue;
            ClipRecord? c = null; string? err = null;
            try { c = ClipRecord.Parse(own); }
            catch (InvalidDataException ex) { err = ex.Message; }
            // a clip record is opaque to the datafile's strict parse, so its own ID is checked here, as that parse checks every other record's
            if (c is not null && c.Id != r.Id) { err = $"the clip's own ID 0x{c.Id:X16} isn't the datafile table's 0x{r.Id:X16}"; c = null; }
            clips.Add(new ClipEntry
            {
                Index = r.Index, Id = r.Id, Name = r.Name, Bytes = own, Record = c, ParseError = err,
                Status = vanilla?.StatusOf(r.Id, own) ?? ClipStatus.Unknown,
                DatafileName = animEntry.Name, DatafileIndex = animEntry.Index, DatafileId = animEntry.Id,
            });
        }
        at.Step = "Reading the unpacked animation datafile's folder";
        string? unpacked = unpackedFolder is null ? UnpackedFolder.PathFor(patch, $"{animEntry.Index}_-_{animEntry.Name}.data") : unpackedFolder.Length == 0 ? null : unpackedFolder;
        string? unpackedError = null;
        if (unpacked is not null && Directory.Exists(unpacked))
        {
            try { clips = UnpackedFolder.Rows(unpacked, clips, vanilla); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { unpackedError = $"The unpacked folder {unpacked} couldn't be read, so its files aren't listed: {ex.Message}"; }
        }
        else if (unpackedFolder is { Length: > 0 }) unpackedError = $"The unpacked folder {unpackedFolder} isn't there.";
        if (unpacked is not null && !Directory.Exists(unpacked)) unpacked = null;
        var tClips = sw.Elapsed;
        var g = new GameInstall(gameFolder, patch, baseForge, oodle, animEntry, records, clips, table, body, addOn, names, vanilla,
            new OpenTimings(tOodle, tIndex, tAnim, tTable, tSkel, tClips, total.Elapsed) { OtherDatafiles = tOthers })
        {
            UnpackedFolderPath = unpackedError is null ? unpacked : null, UnpackedFolderError = unpackedError, OtherClipNames = otherNames,
            ControlDecodeOn = controlOn, BodySkeletonRecord = bodyRec, AddOnSkeletonRecord = addOnRec, PatchForgeStamp = patchStamp, BaseForgeStamp = baseStamp,
        };
        foreach (var e in patchLay.Entries) g._patchEntries.TryAdd(e.Id, e);
        foreach (var e in baseLayout.Entries) g._baseEntries.TryAdd(e.Id, e);
        at.Step = "Reading the other datafiles' unpacked folders";
        g.TakeOthers(others, vanilla, otherUnpackedRoot);
        return g;
    }

    static (long Length, long WriteTimeUtcTicks) Stamp(string file)
    {
        var f = new FileInfo(file);
        return (f.Length, f.LastWriteTimeUtc.Ticks);
    }

    /// <summary>The patch forge's length and last write time as they stood when the install was opened, before it was read.</summary>
    public (long Length, long WriteTimeUtcTicks) PatchForgeStamp { get; private init; }

    /// <summary>The base forge's length and last write time as they stood when the install was opened, before it was read.</summary>
    public (long Length, long WriteTimeUtcTicks) BaseForgeStamp { get; private init; }

    /// <summary>Where the control decode ran ("the animation datafile", or the shared table's entry when the datafile holds no compressed
    /// chunk), or null when neither holds a compressed chunk, so the library was loaded on its hash alone and nothing decoded through it.</summary>
    public string? ControlDecodeOn { get; private init; }

    /// <summary>The skeleton records as read, for diagnostics and tests.</summary>
    internal byte[] BodySkeletonRecord { get; private init; } = [];
    internal byte[] AddOnSkeletonRecord { get; private init; } = [];

    /// <summary>Decodes the animation datafile again from the patch forge with the library already loaded (diagnostics: the decode path
    /// through Oodle, run on demand). A failure comes out as a <see cref="GameInstallException"/>.</summary>
    public List<DatafileRecord> DecodeAnimationDatafileAgain() => ReadAnimationDatafile(PatchForgePath, Oodle).Records;

    /// <summary>A forge's animation datafile records alone, without skeletons or clips parsed (used to build the table of the game's clips).
    /// A failure comes out as a <see cref="GameInstallException"/> (or an <see cref="OodleException"/>).</summary>
    public static (ForgeEntry Entry, List<DatafileRecord> Records) ReadAnimationDatafile(string forge, OodleLibrary oodle)
    {
        try
        {
            using var fs = OpenForge(forge);
            var lay = ReadLayout(fs, forge);
            var e = lay.Find(AnimationDatafileId) ?? throw new GameInstallException($"The animation datafile isn't in {forge}.");
            return (e, DecodeRecords(ForgeReader.ReadPayload(fs, e), oodle, "the animation datafile"));
        }
        catch (Exception ex) when (ex is not (GameInstallException or OodleException))
        {
            throw new GameInstallException($"Reading the animation datafile from {forge} failed: {ex.Message}", ex);
        }
    }

    static FileStream OpenForge(string path)
    {
        try { return ForgeReader.OpenRead(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new GameInstallException($"{path} can't be opened for reading ({ex.Message}). If the game is running, close it and try again.", ex); }
    }

    static ForgeLayout ReadLayout(Stream s, string path)
    {
        try { return ForgeReader.Read(s); }
        catch (InvalidDataException ex) { throw new GameInstallException($"{path} isn't a forge this tool can read: {ex.Message}", ex); }
    }

    static List<DatafileRecord> DecodeRecords(byte[] payload, OodleLibrary oodle, string what)
    {
        try
        {
            var d = DatafileCodec.Decode(payload, oodle, out int table, out _);
            return DatafileCodec.Parse(d, table);
        }
        catch (InvalidDataException ex) { throw new GameInstallException($"{what} can't be decoded: {ex.Message}", ex); }
    }

    /// <summary>A skeleton record by its ID: from the forge entry of that ID, the patch forge first (a patch entry overrides the base entry of
    /// the same ID), then the base forge.</summary>
    static byte[] SkeletonRecord(ulong id, (Stream S, ForgeLayout L, string Path) patch, (Stream S, ForgeLayout L, string Path) basef, OodleLibrary oodle)
    {
        foreach (var (s, l, p) in new[] { patch, basef })
        {
            var e = l.Find(id);
            if (e is null) continue;
            var recs = DecodeRecords(ForgeReader.ReadPayload(s, e), oodle, $"entry 0x{id:X16} in {p}");
            var r = recs.FirstOrDefault(x => x.Id == id) ?? throw new GameInstallException($"Entry 0x{id:X16} in {p} doesn't hold its own record.");
            if (r.Type != Skeleton.Type) throw new GameInstallException($"Record 0x{id:X16} in {p} isn't a skeleton (type 0x{r.Type:X8}).");
            return r.Bytes.ToArray();
        }
        throw new GameInstallException($"The skeleton 0x{id:X16} is in neither {patch.Path} nor {basef.Path}.");
    }

    /// <summary>A check after the Oodle library has loaded (never instead of its hash): the first compressed chunk of a datafile's payload
    /// must decode to exactly the length it claims, and when it is the table's first chunk, the table's count must fit block 1's length.
    /// Returns false when the payload holds no compressed chunk, so nothing was checked.</summary>
    internal static bool ControlDecode(OodleLibrary oodle, byte[] payload, string what)
    {
        List<CfdBlock> blocks;
        try { blocks = CompressedFileData.ReadBlocks(payload, out _); }
        catch (InvalidDataException ex) { throw new GameInstallException($"The payload of {what} can't be read: {ex.Message}", ex); }
        for (int k = 0; k < blocks.Count; k++)
            for (int i = 0; i < blocks[k].Chunks.Count; i++)
            {
                if (!blocks[k].Chunks[i].Compressed) continue;
                byte[] d;
                try { d = CompressedFileData.DecodeChunk(payload, blocks[k], i, oodle); }
                catch (InvalidDataException ex) { throw new OodleException($"The game's Oodle library failed its control decode: {ex.Message}", ex); }
                if (k == 0 && i == 0 && d.Length >= 2)
                {
                    long table = 2 + 14L * BinaryPrimitives.ReadUInt16LittleEndian(d);
                    if (table > blocks[0].UncompressedLength) throw new OodleException($"The game's Oodle library failed its control decode: the table claims {table} B, block 1 holds {blocks[0].UncompressedLength} B.");
                }
                return true;
            }
        return false;
    }

    /// <summary>Unloads the Oodle library, once any decode in flight on another thread has finished; while the install is held
    /// (<see cref="Hold"/>: a package build reading its forges, say), only once the last hold is let go.</summary>
    public void Dispose()
    {
        // let go under the same lock a hold is taken under, so no hold is taken between the check and the unloading
        lock (_holdSync)
        {
            _disposeAsked = true;
            if (_holds > 0 || _disposed) return;
            _disposed = true;
        }
        Unload();
    }

    readonly object _holdSync = new();
    int _holds;
    bool _disposeAsked, _disposed;

    /// <summary>Whether the install was let go (<see cref="Dispose"/>, and no hold left): its Oodle library unloaded.</summary>
    public bool IsDisposed { get { lock (_holdSync) return _disposed; } }

    /// <summary>Keeps the install whole until the returned hold is disposed: a <see cref="Dispose"/> meanwhile (another install opened)
    /// returns at once, and the unloading is put off until the last hold is let go. Throws an <see cref="ObjectDisposedException"/> on an install already let go.</summary>
    public IDisposable Hold()
    {
        lock (_holdSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _holds++;
        }
        return new HoldOn(this);
    }

    sealed class HoldOn(GameInstall g) : IDisposable
    {
        int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            bool now;
            lock (g._holdSync)
            {
                now = --g._holds == 0 && g._disposeAsked && !g._disposed;
                if (now) g._disposed = true;
            }
            if (now) g.Unload();
        }
    }

    /// <summary>Unloads, once: called only by the one who set <see cref="_disposed"/>.</summary>
    void Unload()
    {
        IO.FolderGuard.Forget(this);
        Oodle.Dispose();
    }
}
