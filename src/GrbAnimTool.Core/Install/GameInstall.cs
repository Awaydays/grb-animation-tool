using System.Buffers.Binary;
using System.Diagnostics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Forge;
using GrbAnimTool.Core.Oodle;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Install;

/// <summary>The install can't be read: a file is missing or can't be opened, an entry the tool needs isn't where it should be, or what it
/// holds is damaged. The message is meant for the user and names what failed.</summary>
public sealed class GameInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One clip of the animation datafile: its place in the datafile, ID, name, the record's bytes, the parsed record (decoded once and
/// kept), and how it stands against the game's own clip.</summary>
public sealed class ClipEntry
{
    /// <summary>The record's index within the animation datafile; a record file for this clip is named "index_-_name.Animation".</summary>
    public required int Index { get; init; }
    public required ulong Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The whole record as the datafile holds it (a view into the decoded datafile).</summary>
    public required ReadOnlyMemory<byte> Bytes { get; init; }
    /// <summary>The parsed record, or null when its head can't be read (<see cref="ParseError"/> says why).</summary>
    public ClipRecord? Record { get; init; }
    public string? ParseError { get; init; }
    public ClipStatus Status { get; init; }

    /// <summary>The clip's length in seconds, or 0 when the record states none in range (<see cref="ClipRecord.DurationValid"/>).</summary>
    public float Duration => Record is { DurationValid: true } r ? r.Duration : 0;
    /// <summary>The clip's length in ticks of 1/60 s (duration x 60, rounded); it has one more frame than this, tick 0 included.</summary>
    public int LastTick => Record?.LastTick ?? 0;
    public int TrackCount => Record?.Tracks.Count ?? 0;
    public bool WalkedToEnd => Record?.WalkedToEnd ?? false;
}

/// <summary>How long each step of opening the install took.</summary>
public sealed record OpenTimings(TimeSpan Oodle, TimeSpan ForgeIndex, TimeSpan AnimationDatafile, TimeSpan SharedTable, TimeSpan Skeletons, TimeSpan Clips, TimeSpan Total);

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
    public IReadOnlyList<ClipEntry> Clips { get; }
    public SharedCurveTable SharedTable { get; }
    public Skeleton BodySkeleton { get; }
    public Skeleton AddOnSkeleton { get; }
    /// <summary>The body skeleton with the add-on's bones appended: the skeleton clips are posed on.</summary>
    public Skeleton Skeleton { get; }
    public BoneNames Names { get; }
    /// <summary>The table of the game's own clips, or null when this build has none (every clip is then <see cref="ClipStatus.Unknown"/>).</summary>
    public VanillaClipHashes? Vanilla { get; }
    /// <summary>Clips of the game's own table that the user's datafile lacks.</summary>
    public int VanillaClipsMissing { get; }
    public OpenTimings Timings { get; }

    readonly Dictionary<ulong, ClipEntry> _byId = [];

    GameInstall(string game, string patch, string baseForge, OodleLibrary oodle, ForgeEntry animEntry, List<DatafileRecord> records, List<ClipEntry> clips,
        SharedCurveTable table, Skeleton body, Skeleton addOn, BoneNames names, VanillaClipHashes? vanilla, OpenTimings timings)
    {
        GameFolder = game; PatchForgePath = patch; BaseForgePath = baseForge; Oodle = oodle; AnimationEntry = animEntry; Records = records; Clips = clips;
        SharedTable = table; BodySkeleton = body; AddOnSkeleton = addOn; Skeleton = Skeleton.Merge(body, addOn); Names = names; Vanilla = vanilla; Timings = timings;
        foreach (var c in clips) _byId.TryAdd(c.Id, c);
        if (vanilla is not null) VanillaClipsMissing = vanilla.Ids.Count(id => !_byId.ContainsKey(id));
        // every writer's guard knows the install's own records, to tell the game's unpacked files from a folder of the player's own
        IO.FolderGuard.Remember(this);
    }

    public ClipEntry? FindClip(ulong id) => _byId.GetValueOrDefault(id);
    public ClipEntry? FindClip(string name) => Clips.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A pose sampler for one clip on the merged skeleton. Refuses a clip whose curves don't read to the record's end (or don't
    /// match its track list one to one): pairing curves and tracks by position would then give a wrong pose without a word.</summary>
    public PoseSampler CreateSampler(ClipEntry clip)
    {
        var record = clip.Record ?? throw new InvalidOperationException($"clip {clip.Name} can't be read: {clip.ParseError}");
        if (!record.WalkedToEnd) throw new InvalidOperationException($"clip {clip.Name} can't be played: {record.Stop}");
        return new(Skeleton, record, SharedTable);
    }

    /// <summary>Opens the install in <paramref name="gameFolder"/>: its Oodle DLL (checked against the pins), its patch forge (or
    /// <paramref name="patchForge"/> instead, for tests and diagnostics) and its base forge, all read-only. Every failure comes out as a
    /// <see cref="GameInstallException"/> naming what failed, or an <see cref="OodleException"/> for the library itself; nothing else.</summary>
    public static GameInstall Open(string gameFolder, string? patchForge = null)
    {
        var at = new OpenStep();
        try { return OpenSteps(gameFolder, patchForge, at); }
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

    static GameInstall OpenSteps(string gameFolder, string? patchForge, OpenStep at)
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
        }

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
            });
        }
        var tClips = sw.Elapsed;
        return new GameInstall(gameFolder, patch, baseForge, oodle, animEntry, records, clips, table, body, addOn, names, vanilla,
            new OpenTimings(tOodle, tIndex, tAnim, tTable, tSkel, tClips, total.Elapsed))
        {
            ControlDecodeOn = controlOn, BodySkeletonRecord = bodyRec, AddOnSkeletonRecord = addOnRec, PatchForgeStamp = patchStamp, BaseForgeStamp = baseStamp,
        };
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
        lock (_holdGate)
        {
            _disposeAsked = true;
            if (_holds > 0 || _disposed) return;
            _disposed = true;
        }
        Unload();
    }

    readonly object _holdGate = new();
    int _holds;
    bool _disposeAsked, _disposed;

    /// <summary>Whether the install was let go (<see cref="Dispose"/>, and no hold left): its Oodle library unloaded.</summary>
    public bool IsDisposed { get { lock (_holdGate) return _disposed; } }

    /// <summary>Keeps the install whole until the returned hold is disposed: a <see cref="Dispose"/> meanwhile (another install opened)
    /// returns at once, and the unloading is put off until the last hold is let go. Throws an <see cref="ObjectDisposedException"/> on an install already let go.</summary>
    public IDisposable Hold()
    {
        lock (_holdGate)
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
            lock (g._holdGate)
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
