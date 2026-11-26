using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Library;

/// <summary>What a restore of the kept list gave: the clips restored (their slots the install's own entries); the clips kept for these
/// game files that don't fit them now, with why (they stay kept); a line for each other game files' list (it stays kept for them); and
/// why the file couldn't be read at all (null when it could, or when there was none).</summary>
public sealed record PackageRestore(IReadOnlyList<PackageItem> Restored, IReadOnlyList<string> NotFitting, IReadOnlyList<string> Elsewhere, string? Problem)
{
    public static readonly PackageRestore Nothing = new([], [], [], null);
}

/// <summary>The package list kept between runs of the tool, in one file (<see cref="FileName"/>) in a folder of the user's (by default
/// <see cref="DefaultFolder"/>): one list for each game folder and animation datafile the tool has kept clips on, each kept clip with
/// its slot (ID, index, name), its rebuilt record with its SHA-256, where it came from, and its checks' verdict and account (the screen's
/// data only). It is written whole or not at all (a file of its own moved over the old one), through the guard every output takes.
/// <para>A list belongs to its game folder and the animation datafile's ID, never to the forge's size, time or record table: installing a
/// package (or a game patch, or another mod) repacks the forge, and the list must still come back. A restore takes back each clip of the
/// open game files' list whose ID, index and name the install still has, whatever its slot holds now; a clip that doesn't fit stays in
/// the file, and so does every other game files' list: nothing is dropped by opening game files, only by changing the list on them.</para></summary>
public sealed class PackageStore(string folder)
{
    public const string FileName = "kept-clips.json";

    /// <summary>The format written: each clip's change in parts (<see cref="Change"/>). Format 2, the list format before this one, kept it as
    /// text (<see cref="ClipChange.FromKeptText"/>), read back and written as this format the next time the list is saved.</summary>
    public const int Version = 3;
    const int TextVersion = 2;

    /// <summary>The user's own folder for the list: <c>%APPDATA%\GrbAnimTool\package</c>.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrbAnimTool", "package");

    public string Folder { get; } = Path.GetFullPath(folder);
    public string FilePath => Path.Combine(Folder, FileName);

    /// <summary>A kept clip. <see cref="Change"/> in format 3; <see cref="Source"/>, the change as text, in format 2 only.</summary>
    sealed record Item(ulong Id, int Index, string Name, byte[] Record, string RecordSha256, Change? Change, CheckLevel Verdict, string ReportText, DateTime KeptAt,
        string? Source = null);
    sealed record KeptFor(string GameFolder, string Forge, ulong DatafileId, List<Item> Items);
    sealed record Kept(int Version, List<KeptFor> Lists);

    /// <summary>A clip's change as kept: its kind's name (<see cref="ChangeKind"/>), and the parts that kind has: the factor (scale and
    /// speed), the original clip's ticks and its ticks a second (trim), the source clip's name (swap), the .glb's path (import), the text
    /// as an earlier version kept it (text).</summary>
    sealed record Change(string Kind, double? Factor = null, int? From = null, int? To = null, int? TicksPerSecond = null, string? SourceClip = null,
        string? Glb = null, string? Text = null);

    static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    static Change Stored(ClipChange c) => c.Kind switch
    {
        ChangeKind.Scale or ChangeKind.Speed => new(c.Kind.ToString(), Factor: c.Factor),
        ChangeKind.Trim => new(c.Kind.ToString(), From: c.From, To: c.To, TicksPerSecond: c.TicksPerSecond),
        ChangeKind.Swap => new(c.Kind.ToString(), SourceClip: c.SourceClip),
        ChangeKind.Import => new(c.Kind.ToString(), Glb: c.GlbPath),
        ChangeKind.Text or ChangeKind.Replace => new(c.Kind.ToString(), Text: c.Text),
        _ => new(c.Kind.ToString()),
    };

    /// <summary>The change kept, or null when it isn't one this format holds (an unknown kind, or a part its kind needs missing or out of range).</summary>
    static ClipChange? Restored(Change k)
    {
        if (!Enum.TryParse<ChangeKind>(k.Kind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind) || !k.Kind.All(char.IsLetter)) return null;
        return kind switch
        {
            ChangeKind.Mirror => ClipChange.Mirror(),
            ChangeKind.Scale or ChangeKind.Speed => k.Factor is double f && double.IsFinite(f) && f > 0 ? new ClipChange { Kind = kind, Factor = f } : null,
            ChangeKind.Trim => k is { From: int a and >= 0, To: int b, TicksPerSecond: int r and > 0 } && b > a ? ClipChange.Trim(a, b) with { TicksPerSecond = r } : null,
            ChangeKind.Swap => ClipChange.Swap(k.SourceClip),
            ChangeKind.Import => k.Glb is { } glb ? ClipChange.Imported(glb) : null,
            ChangeKind.Replace => k.Text is { } r ? ClipChange.Replaced(r) : null,
            _ => k.Text is { } t ? new ClipChange { Kind = ChangeKind.Text, Text = t } : null,
        };
    }

    /// <summary>What a list belongs to: the game folder, spelt one way, and the animation datafile's ID.</summary>
    static (string Folder, ulong Datafile) KeyOf(GameInstall g) => (Spelt(g.GameFolder), g.AnimationEntry.Id);

    static string Spelt(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant();

    static bool Owns(KeptFor l, (string Folder, ulong Datafile) key) => Spelt(l.GameFolder) == key.Folder && l.DatafileId == key.Datafile;

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    /// <summary>Why a kept clip doesn't fit the install, or null when the install still has its slot (its ID, index and name) and its
    /// kept record passes the rebuild check every record the tool writes goes through, on these game files' shared curves: rebuilt with
    /// nothing replaced it comes back byte for byte, as the slot's clip. The file's own word (its SHA-256, its verdict) isn't enough: the
    /// list may be older than the tool or the game files, or not this tool's work. (A clip whose kept record isn't the one its SHA-256
    /// names is not read at all: <see cref="ItemsOf"/>.)</summary>
    static string? Misfit(GameInstall g, Item i, out ClipEntry? slot)
    {
        // the row it was kept from: a modded file's row has the file's number for its index
        // after a repack that file's bytes are the forge's row, which keeps the file's number beside its own index
        static bool At(ClipEntry x, Item i) => x.Index == i.Index || x.FileNumber == i.Index;
        slot = g.Clips.FirstOrDefault(x => x.Id == i.Id && x.Index == i.Index && x.Name == i.Name)
            ?? g.Clips.FirstOrDefault(x => x.Id == i.Id && At(x, i) && x.Name == i.Name) ?? g.Clips.FirstOrDefault(x => x.Id == i.Id);
        if (slot is null) return "these game files have no clip of its ID";
        if (!At(slot, i) || slot.Name != i.Name) return $"these game files' clip of its ID is {slot.Index}_-_{slot.Name}, not {i.Index}_-_{i.Name}";
        try
        {
            var rebuilt = Writing.ClipRebuild.Rebuild(i.Record, g.SharedTable);
            return !rebuilt.Bytes.AsSpan().SequenceEqual(i.Record) ? "its kept record doesn't come back the same from a rebuild, so it isn't one the tool writes"
                : rebuilt.Record.Id != slot.Id || rebuilt.Record.Name != slot.Name ? $"its kept record is of the clip {rebuilt.Record.Name}, not this one"
                : null;
        }
        catch (Writing.RebuildRefusedException ex) { return $"its kept record doesn't pass the rebuild check on these game files ({ex.Message})"; }
    }

    /// <summary>The file as it stands, in this format, read list by list and clip by clip: null with no file; what could be read, and a
    /// problem saying what couldn't, when not all of it reads (a list or a clip damaged, of a kind this version doesn't know, or written by
    /// a version this one can't read, the whole file then). A file of format 2 is read with each clip's change converted from its text
    /// (<see cref="ClipChange.FromKeptText"/>). Nothing is written here; a file not read whole is kept aside before a save replaces it
    /// (<see cref="KeepUnreadable"/>). A file there that can't be opened or read now (held by another program, say) gives
    /// <c>InUse</c>: nothing of it is known, so nothing may be written over it.</summary>
    (Kept? Kept, string? Problem, bool InUse) Read()
    {
        if (!File.Exists(FilePath)) return (null, null, false);
        string Whole(string why) => $"The package list kept between runs couldn't be read ({why}), so the list starts empty; the file as it is "
            + $"is kept beside it as {UnreadableName}<date>_<time>.json before the list is saved again. File: {FilePath}";
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"The package list kept between runs couldn't be read (it is in use or can't be opened now: {ex.Message}), so the list "
                + $"starts empty; nothing is saved over it while it can't be read, and once it reads it is kept beside it as "
                + $"{UnreadableName}<date>_<time>.json before the list is saved again. File: {FilePath}", true);
        }
        JsonNode? root;
        // (a UTF-8 byte order mark, as an editor may save the file with, skipped)
        try { root = JsonNode.Parse(bytes.AsSpan(bytes.AsSpan().StartsWith("﻿"u8) ? 3 : 0)); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return (null, Whole($"damaged or cut short: {ex.Message}"), false);
        }
        JsonArray lists;
        int version;
        try
        {
            if (root is not JsonObject o || o["Version"] is not JsonValue v || !v.TryGetValue<int>(out version))
                return (null, Whole("not a list of kept clips"), false);
            if (version is not (Version or TextVersion))
                return (null, Whole($"its format {version} is one a later version of the tool writes, which this one can't read"), false);
            if (o["Lists"] is not JsonArray a) return (null, Whole("it holds no lists"), false);
            lists = a;
        }
        // (a property given twice is only found when the file's properties are looked up)
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException) { return (null, Whole($"damaged: {ex.Message}"), false); }
        var read = new List<KeptFor>();
        int listsLeft = 0, clipsLeft = 0;
        var clipsLeftNamed = new List<string>();
        foreach (var ln in lists)
        {
            KeptFor? list = null;
            try
            {
                // a list needs a game folder that is a usable path (it is spelt as one to tell lists apart) and its datafile's ID
                if (ln is JsonObject lo && lo["GameFolder"]?.GetValue<string>() is { Length: > 0 } folder && Spelt(folder) is { Length: > 0 }
                    && lo["DatafileId"]?.GetValue<ulong>() is ulong datafile && lo["Items"] is JsonArray items)
                    list = new KeptFor(folder, lo["Forge"]?.GetValue<string>() ?? "", datafile, [.. ItemsOf(items, version, clipsLeftNamed, ref clipsLeft)]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or ArgumentException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException) { list = null; }
            if (list is null) listsLeft++;
            else read.Add(list);
        }
        string? problem = listsLeft + clipsLeft == 0 ? null
            : $"Not all of the package list kept between runs could be read: {(listsLeft > 0 ? $"{listsLeft} list{(listsLeft == 1 ? "" : "s")}" : "")}"
              + (listsLeft > 0 && clipsLeft > 0 ? " and " : "")
              + (clipsLeft > 0 ? $"{clipsLeft} kept clip{(clipsLeft == 1 ? "" : "s")}{(clipsLeftNamed.Count > 0 ? $" ({string.Join(", ", clipsLeftNamed.Take(5))}{(clipsLeftNamed.Count > 5 ? ", ..." : "")})" : "")}" : "")
              + $" couldn't be read (damaged, or of a kind this version doesn't know), so {(listsLeft + clipsLeft == 1 ? "it is" : "they are")} left out; the rest is "
              + $"back. The file as it is is kept beside it as {UnreadableName}<date>_<time>.json before the list is saved again. File: {FilePath}";
        return (new Kept(Version, read), problem, false);
    }

    /// <summary>A list's clips that read whole, each in this format (format 2's text converted); each other one (a property given twice in
    /// it, a part missing or not read, a record that isn't the one its SHA-256 names) counted, and named when its name reads.</summary>
    static List<Item> ItemsOf(JsonArray items, int version, List<string> leftNamed, ref int left)
    {
        // every object in the clip looked into, so a property given twice in any of them is found (reading it into a clip would keep
        // only one of them)
        static void LookIn(JsonNode? n)
        {
            if (n is JsonObject o) foreach (var p in o) LookIn(p.Value);
            else if (n is JsonArray a) foreach (var x in a) LookIn(x);
        }
        var read = new List<Item>();
        foreach (var n in items)
        {
            Item? i = null;
            try { LookIn(n); i = n?.Deserialize<Item>(Options); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException) { i = null; }
            if (i is null || i.Name is null || i.Record is null || i.RecordSha256 is null || i.ReportText is null || Sha(i.Record) != i.RecordSha256
                || (version == TextVersion ? i.Source is null : i.Change is null || Restored(i.Change) is null))
            {
                left++;
                try { if (n is JsonObject no && no["Name"] is JsonValue nv && nv.TryGetValue<string>(out var name)) leftNamed.Add(name); }
                catch (ArgumentException) { }
                continue;
            }
            read.Add(version == TextVersion ? i with { Change = Stored(ClipChange.FromKeptText(i.Source!)), Source = null } : i);
        }
        return read;
    }

    /// <summary>The start of the name a file not read whole is kept under beside the list, before a save replaces it.</summary>
    public const string UnreadableName = "kept-clips.unreadable-";

    /// <summary>The start of the name a file another running copy of the tool wrote is kept under beside the list, before a save replaces
    /// it: kept and pruned apart from the files not read whole, so however many there are, they never push one of those out.</summary>
    public const string ElsewhereName = "kept-clips.elsewhere-";

    /// <summary>How many files not read whole are kept beside the list at most, and how many files another copy wrote: a new one takes
    /// the place of the oldest of its kind.</summary>
    public const int MostUnreadable = 5;

    /// <summary>Keeps the file as it stands beside it, under a name of its own (<paramref name="name"/>, <see cref="UnreadableName"/> or
    /// <see cref="ElsewhereName"/>, the date and time, and a number when that is taken; never written over), guarded as every output is,
    /// unless the newest such file holds the same bytes (a save that failed after it, tried again). Returns that copy, which
    /// <see cref="PruneUnreadable"/> keeps once the list is written; null when there is no file.</summary>
    string? KeepUnreadable(GameInstall g, string name)
    {
        if (!File.Exists(FilePath)) return null;
        var bytes = File.ReadAllBytes(FilePath);
        var copies = Copies(name);
        if (copies.Count > 0 && Sha(File.ReadAllBytes(copies[^1].FullName)) == Sha(bytes)) return copies[^1].FullName;
        string stem = Path.Combine(Folder, name + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        string path = stem + ".json";
        for (int k = 2; File.Exists(path); k++) path = $"{stem}_{k}.json";
        ClipExport.WriteFile(path, ClipExport.ProtectedFolders(g), s => s.Write(bytes), makeFolder: true);
        return Path.GetFullPath(path);
    }

    List<FileInfo> Copies(string name) => [.. Directory.GetFiles(Folder, name + "*.json").Select(f => new FileInfo(f))
        .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)];

    /// <summary>After the list is written: the oldest files kept aside under <paramref name="name"/> beyond <see cref="MostUnreadable"/>
    /// go, never <paramref name="keep"/> (its time may sort it first after a clock set back). As far as it can: a copy that can't go now
    /// (held open, read-only) stays until a later save, and never stops this one.</summary>
    void PruneUnreadable(GameInstall g, string name, string keep)
    {
        try
        {
            var copies = Copies(name).Where(f => !string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var f in copies.Take(Math.Max(0, copies.Count - (MostUnreadable - 1))))
            {
                try
                {
                    ClipExport.Guard(f.FullName, ClipExport.ProtectedFolders(g));
                    f.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExportException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Writes <paramref name="items"/> as the list of <paramref name="g"/>'s game files, whole or not at all, never into the
    /// install's folders: the other game files' lists stay as they were, and so do this list's clips that don't fit these game files.</summary>
    public void Save(GameInstall g, IReadOnlyList<PackageItem> items) => Write(g, items, keepMisfits: true);

    /// <summary>Forgets the clips kept for <paramref name="g"/>'s game files that don't fit them (each said on the Package screen), keeping
    /// <paramref name="items"/>, the list on the screen, and every other game files' list. Returns how many were forgotten.</summary>
    public int ForgetNotFitting(GameInstall g, IReadOnlyList<PackageItem> items) => Write(g, items, keepMisfits: false);

    /// <summary>Writes the list; returns how many of this list's clips kept before were dropped.</summary>
    int Write(GameInstall g, IReadOnlyList<PackageItem> items, bool keepMisfits)
    {
        var key = KeyOf(g);
        ChangedElsewhere = null;
        var (old, problem, inUse) = Read();
        // a file there that can't be read now is never written over: the other game files' lists in it would be lost
        if (inUse) throw new IOException(problem);
        // a file not read whole, now or when it was loaded (a read that failed then, though it reads now), is kept as it is before it is
        // replaced: what wasn't read, and so isn't on the screen, is never lost by this save; so is a file in which another copy of the
        // tool changed this game folder's list
        var (aside, kind) = KeepAside(g, key, old, problem);
        var lists = old?.Lists.Where(l => !Owns(l, key)).ToList() ?? [];
        var keptHere = items.Select(i => new Item(i.Slot.Id, i.Slot.Index, i.Slot.Name, i.Record, Sha(i.Record), Stored(i.Change), i.Verdict, i.ReportText, i.KeptAt)).ToList();
        // the clips kept for these game files that don't fit them now (none of them on the screen, so none changed there) stay kept (a
        // clip whose kept record is damaged isn't read, so it is in the file kept aside above, not in the list)
        var ids = items.Select(i => i.Slot.Id).ToHashSet();
        var others = old?.Lists.Where(l => Owns(l, key)).SelectMany(l => l.Items).Where(i => !ids.Contains(i.Id)).ToList() ?? [];
        var stay = keepMisfits ? others.Where(i => Misfit(g, i, out _) is not null).ToList() : [];
        keptHere.AddRange(stay);
        if (keptHere.Count > 0) lists.Add(new KeptFor(g.GameFolder, g.PatchForgePath, key.Datafile, keptHere));
        Put(g, new Kept(Version, lists));
        if (aside is not null) PruneUnreadable(g, kind, aside);
        return others.Count - stay.Count;
    }

    /// <summary>Before a write: the file kept aside as it is, when it wasn't read whole (now or at the load; <see cref="UnreadableName"/>)
    /// or another running copy changed this game folder's list in it (<see cref="ElsewhereName"/>, said by
    /// <see cref="ChangedElsewhere"/>). Returns the copy and its kind, or null.</summary>
    (string? Aside, string Kind) KeepAside(GameInstall g, (string, ulong) key, Kept? read, string? problem)
    {
        bool elsewhere = _known.TryGetValue(key, out var known) && known != Own(read, key);
        string kind = problem is not null || _notWhole ? UnreadableName : ElsewhereName;
        string? aside = problem is not null || _notWhole || elsewhere ? KeepUnreadable(g, kind) : null;
        ChangedElsewhere = elsewhere ? aside : null;
        return (aside, kind);
    }

    /// <summary>Writes the file, and takes its list for <paramref name="g"/>'s game files as the one this store knows.</summary>
    void Put(GameInstall g, Kept kept)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(kept, Options);
        ClipExport.WriteFile(FilePath, ClipExport.ProtectedFolders(g), s => s.Write(bytes), makeFolder: true);
        _known[KeyOf(g)] = Own(kept, KeyOf(g));
        _notWhole = false;
    }

    /// <summary>Whether the last load found the file not readable whole: it stays so until the next write, which keeps the file aside
    /// first.</summary>
    bool _notWhole;

    /// <summary>Each game files' own list as this store last loaded or wrote it (<see cref="Own"/>); none for game files not loaded yet, or
    /// whose load found the file in use (nothing of it was known then). Another copy's list of other game files is no change of this one.</summary>
    readonly Dictionary<(string, ulong), string> _known = [];

    /// <summary>The list of the game files <paramref name="key"/> names in <paramref name="kept"/>, as each clip's ID and its record's
    /// SHA-256: empty with no file, or no list of them.</summary>
    static string Own(Kept? kept, (string Folder, ulong Datafile) key) =>
        string.Join(",", (kept?.Lists ?? []).Where(l => Owns(l, key)).SelectMany(l => l.Items).Select(i => $"{i.Id:X16}:{i.RecordSha256}"));

    /// <summary>After a write: the copy of the file kept aside because another running copy of the tool had changed this game folder's
    /// list in it since this one last loaded or wrote it (the clips it changed aren't in the list just written); null when it hadn't.</summary>
    public string? ChangedElsewhere { get; private set; }

    /// <summary>The kept clips of <paramref name="g"/>'s game files that fit it: none (and nothing said) when there is no file; none, with
    /// the problem, when the file can't be read whole; else each clip of the list kept for its game folder and animation datafile whose ID,
    /// index and name the install still has (whatever its slot holds now: a package installed in the game, say), each other one said with
    /// why, and each other game files' list said. Nothing is written.</summary>
    public PackageRestore Load(GameInstall g)
    {
        // (a file that couldn't be read at all is kept aside too before a later save replaces it: none of its clips came to the screen)
        var (kept, problem, inUse) = Read();
        _notWhole = problem is not null;
        var key = KeyOf(g);
        if (inUse) _known.Remove(key);
        else _known[key] = Own(kept, key);
        if (kept is null) return problem is null ? PackageRestore.Nothing : new PackageRestore([], [], [], problem);
        var restored = new List<PackageItem>();
        var notFitting = new List<string>();
        foreach (var i in kept.Lists.Where(l => Owns(l, key)).SelectMany(l => l.Items))
        {
            if (Misfit(g, i, out var slot) is { } why) notFitting.Add($"{i.Name}: {why}");
            else restored.Add(new PackageItem(slot!, i.Record, Restored(i.Change!)!, i.Verdict, i.ReportText, i.KeptAt));
        }
        var elsewhere = kept.Lists.Where(l => !Owns(l, key) && l.Items.Count > 0)
            .Select(l => $"{l.Items.Count} clip{(l.Items.Count == 1 ? "" : "s")} kept for the game folder {l.GameFolder} ({l.Forge})").ToList();
        return new PackageRestore(restored, notFitting, elsewhere, problem);
    }

    /// <summary>Whether two installs' kept lists are one: the same game folder and animation datafile.</summary>
    public static bool SameList(GameInstall a, GameInstall b) => KeyOf(a) == KeyOf(b);

    /// <summary>Takes <paramref name="built"/> (the clips a build wrote, kept on <paramref name="g"/>) out of the list kept for
    /// <paramref name="g"/>'s game files, each by its ID and its record's SHA-256 (a clip kept again since, with another record, stays);
    /// every other list stays as it was. Returns how many were taken out.</summary>
    public int RemoveBuilt(GameInstall g, IReadOnlyList<PackageItem> built)
    {
        var key = KeyOf(g);
        ChangedElsewhere = null;
        var (old, problem, inUse) = Read();
        if (old is null && problem is null) return 0;
        if (inUse) throw new IOException(problem);
        var (aside, kind) = KeepAside(g, key, old, problem);
        var done = built.Select(b => (b.Slot.Id, Sha(b.Record))).ToHashSet();
        int removed = 0;
        var lists = new List<KeptFor>();
        foreach (var l in old?.Lists ?? [])
        {
            if (!Owns(l, key)) { lists.Add(l); continue; }
            var stay = l.Items.Where(i => !done.Contains((i.Id, i.RecordSha256))).ToList();
            removed += l.Items.Count - stay.Count;
            if (stay.Count > 0) lists.Add(l with { Items = stay });
        }
        Put(g, new Kept(Version, lists));
        if (aside is not null) PruneUnreadable(g, kind, aside);
        return removed;
    }
}
