using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.IO;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.Core.Package;

/// <summary>A file a package build writes: its name and what it is.</summary>
public sealed record PackageFile(string Name, string What);

/// <summary>The game's own record of a clip a package replaces, for the package's <see cref="PackageBuild.OriginalsFolderName"/> folder:
/// the install's slot, the record (whose SHA-256 is the game's own by the table of the game's clips built into the tool) and where it was
/// taken from (<see cref="PackageBuild.FromInstall"/>, <see cref="PackageBuild.FromBackupForge"/> or <see cref="PackageBuild.FromBaseForge"/>);
/// or, with no record, why none was found (<see cref="Missing"/>): the package is then built without it, never refused. Neither: not looked
/// for yet in the forges (<see cref="Pending"/>).</summary>
public sealed record OriginalClip(ClipEntry Slot, byte[]? Record, string? From, string? Missing)
{
    /// <summary>The install's slot isn't the game's own and the base forge wasn't read yet.</summary>
    public bool Pending => Record is null && Missing is null;
}

/// <summary>What a package build will write: the folder (the package's name inside the save folder) and every file in it, or why it can't
/// be built (<see cref="Problem"/>).</summary>
public sealed record PackagePlan(string SaveFolder, string FolderName, IReadOnlyList<PackageFile> Files, string? Problem)
{
    /// <summary>The package's own folder: <see cref="FolderName"/> inside <see cref="SaveFolder"/>, or "" without a save folder.</summary>
    public string OutputFolder => SaveFolder.Length == 0 || FolderName.Length == 0 ? "" : Path.Combine(SaveFolder, FolderName);
    /// <summary>The package's name as given (its build marker records it).</summary>
    public string PackageName { get; init; } = "";

    /// <summary>The game's own record of each clip as the plan found it (from an install's plan: a forge used only when it was read before
    /// for that install, see <see cref="OriginalClip.Pending"/>); none for a plan without an install.</summary>
    public IReadOnlyList<OriginalClip> Originals { get; init; } = [];
}

/// <summary>A package built: its folder and every file written, with length and SHA-256 (a file of the originals folder named by its path
/// in the package's folder), and where each original record was taken from.</summary>
public sealed record PackageBuilt(string Folder, IReadOnlyList<WrittenRecordFile> Files)
{
    /// <summary>An earlier build moved aside for the new one that couldn't be deleted afterwards (its path), or null.</summary>
    public string? EarlierLeftAt { get; init; }

    /// <summary>The game's own record of each clip, as written into the originals folder.</summary>
    public IReadOnlyList<OriginalClip> Originals { get; init; } = [];
}

/// <summary>Plans and builds a package: one folder of the package's name inside a save folder the player picks, never in or under the
/// game's folders, holding the kept clips' record files (<c>index_-_name.Animation</c>), the folder <see cref="OriginalsFolderName"/> with
/// the game's own record of every replaced clip under the same file name (copied over the package's files, they uninstall it), and the
/// build's marker (<see cref="MarkerName"/>). The originals come from the user's own game files alone, each checked against the SHA-256 of
/// the game's own record built into the tool: the install's slot when it is the game's own, else the user's vanilla forge backup, else the
/// base forge's animation datafile (<see cref="Originals"/>); a clip whose original is found in none is built without it, named on the
/// screen (<see cref="MissingNote"/>). The build is whole or nothing: everything is
/// written into a fresh folder beside the package's (through <see cref="RecordFileWriter"/>, which reads each file back), and only then
/// is that folder given the package's name. An earlier build is replaced only when asked, and only when its folder carries this tool's
/// marker for this package and holds nothing but a package's files. No build deletes or moves anything through a link standing at its own
/// place.
/// <para>An item that is the game's own clip unchanged, or what the install already holds for its clip, replaces nothing and is never
/// packaged (<see cref="UnchangedProblem"/>), and no package is written inside a folder carrying this tool's marker, a package or a local
/// undo folder an earlier version wrote, nor is any other file of the tool's (<see cref="IO.FolderGuard.BuildFolderRefusal"/>).</para></summary>
public static class PackageBuild
{
    /// <summary>The marker every build writes into its folder: the tool's line, the package's name and the build time. Only a folder that
    /// carries it is ever replaced by a later build, and every writer of the tool refuses a folder carrying it.</summary>
    public const string MarkerName = ".grbanimtool-package";

    /// <summary>The marker's first line.</summary>
    public const string MarkerHead = "GRB Anim Tool package build";

    /// <summary>The package's subfolder holding the game's own record of every clip it replaces, under the same file names. It carries no
    /// marker: it lies inside the package, whose marker covers it.</summary>
    public const string OriginalsFolderName = "Original clips";

    /// <summary>Where an original record was taken from: the install's own slot.</summary>
    public const string FromInstall = "your game folder";

    /// <summary>Where an original record was taken from: the base forge's animation datafile, as the install's slot holds a modded clip.</summary>
    public const string FromBaseForge = "the game's base files";

    /// <summary>What the Package screen says of an original taken from the base forge.</summary>
    public const string FromBaseForgeWhy = "taken from the game's base files: your game folder holds a modded clip there";

    /// <summary>Where an original record was taken from: the vanilla backup of the patch forge the user names, as the install's slot holds
    /// a modded clip.</summary>
    public const string FromBackupForge = "your vanilla forge backup";

    /// <summary>What the Package screen says of an original taken from the user's vanilla forge backup.</summary>
    public const string FromBackupForgeWhy = "taken from your vanilla forge backup: your game folder holds a modded clip there";

    /// <summary>What the Package screen and the result say of a clip whose original wasn't found anywhere: the package is built without it.</summary>
    public static string NoOriginal(string clip) => $"no original included for {clip}: it can't be uninstalled with this package";

    /// <summary>The names of the folders of the game's own files earlier versions of this tool wrote beside a package (the local undo
    /// folder, and the earliest's "restore" folder); the tool writes none now. Such a folder carries the marker, so every writer refuses it, and
    /// no build replaces it as a package.</summary>
    static readonly string[] EarlierBesideSuffixes = [" - local undo (do not share)", " - local undo", " - restore"];

    /// <summary>Whether <paramref name="folder"/>'s name is one earlier versions gave the folder of the game's own files beside a package
    /// (<see cref="EarlierBesideSuffixes"/>), so the screen can say what it is before Build refuses it.</summary>
    public static bool IsEarlierBesideName(string folder) =>
        EarlierBesideSuffixes.Any(s => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>The texts earlier versions of this tool wrote into a package, recognised as a package's own files when such a build is
    /// replaced.</summary>
    static readonly string[] EarlierTextFiles = ["install-note.txt", "README.txt"];

    /// <summary>The marker's text for a build of <paramref name="packageName"/> at <paramref name="built"/> (UTC).</summary>
    public static string MarkerText(string packageName, DateTime built) =>
        $"{MarkerHead}\nname: {packageName.Trim()}\nbuilt: {built.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}\n";

    /// <summary>As <see cref="MarkerText(string, DateTime)"/>, with the build's kind: <c>kind: package</c>, or <c>kind: restore</c> as
    /// earlier versions wrote it for the folders of the game's own files beside a package.</summary>
    public static string MarkerText(string packageName, DateTime built, bool restore) =>
        MarkerText(packageName, built) + $"kind: {(restore ? "restore" : "package")}\n";

    /// <summary>Whether <paramref name="folder"/> carries this tool's build marker (<see cref="MarkerName"/>, starting <see cref="MarkerHead"/>).</summary>
    public static bool IsEarlierBuild(string folder) => MarkerLines(folder) is { } l && l.FirstOrDefault() == MarkerHead;

    /// <summary>Whether <paramref name="folder"/> carries this tool's build marker for a build of <paramref name="packageName"/> of the same
    /// kind (a package, or with <paramref name="restore"/> a folder of the game's own files an earlier version wrote beside one): the name
    /// the marker records must be the name given, and its kind the kind asked (a marker without a kind line is of the second kind when the
    /// name it records ends as such a folder's did).</summary>
    public static bool IsEarlierBuild(string folder, string packageName, bool restore)
    {
        if (MarkerLines(folder) is not { } l || l.FirstOrDefault() != MarkerHead) return false;
        string name = packageName.Trim();
        string? recorded = l.FirstOrDefault(x => x.StartsWith("name: ", StringComparison.Ordinal))?[6..];
        string? kind = l.FirstOrDefault(x => x.StartsWith("kind: ", StringComparison.Ordinal))?[6..];
        bool wasRestore = kind is null
            ? recorded is not null && EarlierBesideSuffixes.Any(x => recorded.EndsWith(x, StringComparison.OrdinalIgnoreCase))
            : kind == "restore";
        // names that differ only in case name the same folder on Windows, so they are the same build
        return string.Equals(recorded, name, StringComparison.OrdinalIgnoreCase) && wasRestore == restore;
    }

    /// <summary>The marker's lines in <paramref name="folder"/>, or null when there is none or it can't be read.</summary>
    static List<string>? MarkerLines(string folder)
    {
        string m = Path.Combine(folder, MarkerName);
        try
        {
            if (!File.Exists(m)) return null;
            // the first 4 KiB alone: a marker is three or four short lines, and a file of that name may be of any size
            var b = new byte[4096];
            int n;
            using (var fs = File.OpenRead(m)) n = fs.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
            using var r = new StreamReader(new MemoryStream(b, 0, n));
            var lines = new List<string>();
            while (lines.Count < 8 && r.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The folder name a package name gives: the name with the characters a folder name can't hold made '_', and no space or dot
    /// at its ends; "" when nothing is left.</summary>
    public static string FolderNameFor(string packageName)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char ch in packageName.Trim()) sb.Append(ch < 0x20 || bad.Contains(ch) ? '_' : ch);
        string s = sb.ToString().Trim(' ', '.');
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(s.Split('.')[0], StringComparer.OrdinalIgnoreCase)) s = "_" + s;
        return s;
    }

    /// <summary>The folders an install reads, which a package is never written into: the game folder and both forges' folders, the one
    /// set every writer of the tool refuses (<see cref="Export.ClipExport.ProtectedFolders(GameInstall)"/>).</summary>
    public static string[] Protected(GameInstall g) => [.. Export.ClipExport.ProtectedFolders(g)];

    /// <summary>The plan for <paramref name="clips"/> (each clip's index, name and ID): the record files in clip order, the originals
    /// folder with the same file names, and the marker, and the first problem found (no clip, no name, no save folder, a save folder holding
    /// a character Windows refuses in a folder name, a save folder in or under a protected folder or in or under a folder carrying this
    /// tool's marker, a record file's name the rules refuse, two clips of one file name).</summary>
    public static PackagePlan Plan(IReadOnlyList<(int Index, string Name, ulong Id)> clips, string packageName, string saveFolder, IEnumerable<string> protectedFolders)
    {
        string folder = FolderNameFor(packageName ?? "");
        string save = (saveFolder ?? "").Trim();
        var files = new List<PackageFile>();
        var originals = new List<PackageFile>();
        string? problem = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in clips)
        {
            string fname;
            try { fname = RecordFile.ClipFileName(c.Index, c.Name, c.Id); }
            catch (RecordFileException ex) { problem ??= ex.Message; continue; }
            if (!names.Add(fname)) problem ??= $"{fname} is in the package twice";
            files.Add(new PackageFile(fname, $"replaces {c.Name}"));
            originals.Add(new PackageFile(Path.Combine(OriginalsFolderName, fname), $"the game's own {c.Name}"));
        }
        if (originals.Count > 0)
            files.Add(new PackageFile(OriginalsFolderName + Path.DirectorySeparatorChar,
                "the game's own record of each clip this package replaces: copied over its files in the unpacked datafile, they uninstall it"));
        files.AddRange(originals);
        files.Add(new PackageFile(MarkerName, "this tool's marker file (the package's name and the build time): only a folder carrying it is replaced by a later build"));
        if (clips.Count == 0) problem = "The package holds no clip yet: add an import or an edit first.";
        else if (folder.Length == 0) problem = "Give the package a name: it names the package's folder.";
        else if (save.Length == 0) problem = "Choose a save folder of your own.";
        else
        {
            string? full = null;
            try { full = Path.GetFullPath(save); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { problem = $"The save folder {save} isn't a folder path: {ex.Message}"; }
            if (full is not null && GuardRefusal(full, folder, protectedFolders) is { } why) problem = why;
        }
        return new PackagePlan(save, folder, files, problem) { PackageName = (packageName ?? "").Trim() };
    }

    /// <summary>Why a package may not be written in <paramref name="save"/> (the save folder, full) as <paramref name="folder"/>: the save
    /// folder or its own folder is refused by the guard for one of <paramref name="protectedFolders"/>, with the guard's reason; or null. A
    /// save folder that is, or lies inside, a folder carrying this tool's marker, by its spelling or by where it really leads
    /// (<see cref="FolderGuard.BuildFolderRefusal"/>), is refused too: what is written there would be shared with that package.</summary>
    static string? GuardRefusal(string save, string folder, IEnumerable<string> protectedFolders)
    {
        var protect = protectedFolders.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        // a name Windows refuses is said as such: nothing can stand there, a link no more than a folder
        if (FolderGuard.RefusedNameChar(save) is { } bad)
            return $"The package can't be written in {save}: this folder name can't be used: {bad}. Choose another save folder.";
        foreach (var path in folder.Length == 0 ? new[] { save } : new[] { save, Path.Combine(save, folder) })
        {
            if (FolderGuard.Refusal(path, protect) is { } why)
                return $"The package can't be written in {path}: {why}. Packages are never written into the game's folders: pick a folder of your own.";
            // by its spelling and by where it really leads: a link to a folder inside a package leads into the package
            if (path == save && FolderGuard.BuildFolderRefusal(save) is { } build)
                return $"The package can't be written in {save}: {build}, {FolderGuard.BuildFolderAdvice}";
            if (FolderGuard.UnpackedDatafileRefusal(path) is { } unpacked)
                return $"The package can't be written in {path}: {unpacked}.";
        }
        return null;
    }

    /// <summary>The plan for the items kept, on <paramref name="g"/>: the files, and the original of each clip as found without reading a
    /// forge anew (<see cref="Originals"/>), from the install, the user's vanilla forge backup (<paramref name="backupForge"/>, when given)
    /// and the base forge. An item that is no change (<see cref="UnchangedProblem"/>) is its problem. A clip whose original isn't found
    /// anywhere is no problem: the package is built without that original, and the screen names it (<see cref="MissingNote"/>).</summary>
    public static PackagePlan Plan(GameInstall g, IReadOnlyList<PackageItem> items, string packageName, string saveFolder, string? backupForge = null)
    {
        var plan = Plan(items.Select(i => (i.Slot.Index, i.Slot.Name, i.Slot.Id)).ToList(), packageName, saveFolder, Protected(g));
        var originals = Originals(g, items, readForges: false, backupForge);
        OriginalClip? Of(PackageFile f) => originals.FirstOrDefault(o => FileOf(o) is { } name && f.Name == Path.Combine(OriginalsFolderName, name));
        plan = plan with
        {
            Originals = originals,
            // each original as it stands: from a forge, not found (the package is built without it), or still to be looked for
            Files = plan.Files.Where(f => Of(f) is not { Missing: not null })
                .Select(f => Of(f) is not { } o ? f
                : o.From == FromBaseForge ? f with { What = $"{f.What}, {FromBaseForgeWhy}" }
                : o.From == FromBackupForge ? f with { What = $"{f.What}, {FromBackupForgeWhy}" }
                : o.Pending ? f with { What = $"{f.What}, still to be looked for: your game folder holds a modded clip there" }
                : f).ToList(),
        };
        // the originals folder isn't written when no original was found at all
        if (originals.Count > 0 && originals.All(o => o.Missing is not null))
            plan = plan with { Files = plan.Files.Where(f => f.Name != OriginalsFolderName + Path.DirectorySeparatorChar).ToList() };
        return UnchangedProblem(g, items) is { } same ? plan with { Problem = same } : plan;
    }

    /// <summary>The file name of an original's slot, or null when the name rules refuse it: no file of the plan is its file, and the
    /// plan says the name as its problem.</summary>
    static string? FileOf(OriginalClip o)
    {
        try { return RecordFile.ClipFileName(o.Slot.Index, o.Slot.Name, o.Slot.Id); }
        catch (RecordFileException) { return null; }
    }

    /// <summary>The game's own record of each item's clip, for the originals folder, from the user's own game files only: the install's
    /// record when its SHA-256 is the game's own by the table of the game's clips built into the tool; else a record of the same ID in the
    /// animation datafile of the user's vanilla forge backup (<paramref name="backupForge"/>, when named), then of the base forge
    /// (<see cref="GameInstall.BaseForgeName"/>), each read as the install reads its forges, when its SHA-256 is the game's own. Otherwise
    /// none, and why (the package is built without it). Without <paramref name="readForges"/> a forge is used only when it was read before
    /// for this install (what it holds of the game's own records is kept; a failed read stands until the next read); a clip still to look
    /// for is <see cref="OriginalClip.Pending"/>.</summary>
    public static IReadOnlyList<OriginalClip> Originals(GameInstall g, IReadOnlyList<PackageItem> items, bool readForges, string? backupForge = null)
    {
        var l = new List<OriginalClip>();
        string? backup = string.IsNullOrWhiteSpace(backupForge) ? null : backupForge.Trim();
        (Dictionary<ulong, byte[]>? Records, string? Error)? fromBase = null, fromBackup = null;
        foreach (var i in items)
        {
            var slot = g.FindClip(i.Slot.Id) ?? i.Slot;
            if (g.Vanilla is not { } table)
            {
                l.Add(new(slot, null, null, "this copy of the tool holds no table of the game's own clips, so it can't tell the game's own record"));
                continue;
            }
            if (!table.Contains(slot.Id))
            {
                l.Add(new(slot, null, null, "the game has no clip of this ID"));
                continue;
            }
            if (g.FindClip(slot.Id) is { } held && table.StatusOf(held.Id, held.Bytes.Span) == ClipStatus.Vanilla)
            {
                l.Add(new(held, held.Bytes.ToArray(), FromInstall, null));
                continue;
            }
            // the user's vanilla forge backup first (when named), then the base forge; a forge not read yet leaves the clip pending
            if (backup is not null)
            {
                fromBackup ??= ForgeOriginals(g, backup, readForges);
                if (fromBackup is not { } k) { l.Add(new(slot, null, null, null)); continue; }
                if (k.Records?.GetValueOrDefault(slot.Id) is { } rk) { l.Add(new(slot, rk, FromBackupForge, null)); continue; }
            }
            fromBase ??= ForgeOriginals(g, BaseForgeInTests.Value ?? g.BaseForgePath, readForges);
            if (fromBase is not { } b) l.Add(new(slot, null, null, null));
            else if (b.Records?.GetValueOrDefault(slot.Id) is { } rec) l.Add(new(slot, rec, FromBaseForge, null));
            else
            {
                var why = new List<string> { "your game folder holds a modded clip there" };
                if (backup is null) why.Add("no vanilla forge backup is named");
                else why.Add(!File.Exists(backup) ? $"your vanilla forge backup isn't there any more: {backup}"
                    : fromBackup?.Error is { } be ? $"your vanilla forge backup couldn't be read: {Plain(be)}" : "your vanilla forge backup doesn't hold the game's own record of it");
                why.Add(b.Error is { } err ? $"{GameInstall.BaseForgeName} couldn't be read: {Plain(err)}" : $"{GameInstall.BaseForgeName} doesn't hold it either");
                l.Add(new(slot, null, null, string.Join(", ", why)));
            }
        }
        return l;
    }

    /// <summary>The game's own clip records a forge's animation datafile holds (the base forge, or the user's vanilla backup), by ID (only
    /// those whose SHA-256 is the game's own), kept per install and path once read; or why it couldn't be read, which stands until the
    /// next read tries again (never kept as the result); null when it isn't read yet, or is being read, and <paramref name="read"/> is
    /// false. The read itself runs outside the lock: a look without reading (the screen's plan, on the window's thread) never waits for a
    /// read in progress, it finds the forge still to be looked in; a second read of the same forge waits for the first.</summary>
    static (Dictionary<ulong, byte[]>? Records, string? Error)? ForgeOriginals(GameInstall g, string forge, bool read)
    {
        TaskCompletionSource<(Dictionary<ulong, byte[]>?, string?)>? mine = null;
        Task<(Dictionary<ulong, byte[]>?, string?)> reading;
        lock (BaseRead)
        {
            var kept = BaseRead.GetOrCreateValue(g);
            if (kept.TryGetValue(forge, out var was)) return was;
            var inProgress = Reading.GetOrCreateValue(g);
            // not read now: a forge being read is still to be looked in; one that failed last time stands as failed (said, the clip without
            // its original) until the next read tries again, never as not read yet, which would leave the screen looking for ever
            if (!read)
                return inProgress.ContainsKey(forge) ? null
                    : BaseReadFailed.TryGetValue(g, out var failed) && failed.TryGetValue(forge, out var why) ? (null, why) : null;
            if (inProgress.TryGetValue(forge, out var other)) reading = other;
            else
            {
                mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
                inProgress[forge] = reading = mine.Task;
            }
        }
        if (mine is null) return reading.GetAwaiter().GetResult();
        (Dictionary<ulong, byte[]>? Records, string? Error) result;
        try
        {
            var (_, records) = GameInstall.ReadAnimationDatafile(forge, g.Oodle);
            var d = new Dictionary<ulong, byte[]>();
            foreach (var r in records)
                if (r.Type == Clips.ClipRecord.Type && g.Vanilla?.StatusOf(r.Id, r.Bytes.Span) == ClipStatus.Vanilla) d.TryAdd(r.Id, r.Bytes.ToArray());
            result = (d, null);
        }
        // a base forge that doesn't hold the animation datafile, or can't be read, finds no original. Not kept: the cause may pass (the
        // game holding the forge), so the next look, or Build, reads it again; the failure is kept apart, for the screen to say
        catch (Exception ex) when (ex is GameInstallException or Oodle.OodleException or IOException or UnauthorizedAccessException)
        {
            result = (null, ex.Message);
        }
        catch (Exception ex)
        {
            // any other error (a fault): rethrown for the reader to report, and kept as the failure too, so the plan says it with its
            // reason until the next read
            lock (BaseRead)
            {
                BaseReadFailed.GetOrCreateValue(g)[forge] = $"{ex.GetType().Name}: {ex.Message}";
                Reading.GetOrCreateValue(g).Remove(forge);
            }
            mine.SetException(ex);
            // observed here: the reader itself reports it (rethrown below), a waiter too; never again when the task is collected
            _ = mine.Task.Exception;
            throw;
        }
        lock (BaseRead)
        {
            if (result.Error is { } error) BaseReadFailed.GetOrCreateValue(g)[forge] = error;
            else BaseRead.GetOrCreateValue(g)[forge] = result;
            Reading.GetOrCreateValue(g).Remove(forge);
        }
        mine.SetResult(result);
        return result;
    }

    /// <summary>The forge reads in progress for each install, by the forge's path: what a second read of the same forge waits for.</summary>
    static readonly ConditionalWeakTable<GameInstall, Dictionary<string, Task<(Dictionary<ulong, byte[]>?, string?)>>> Reading = new();

    /// <summary>Whether a forge is being read for the originals of <paramref name="g"/> now (for tests).</summary>
    internal static bool ReadInProgress(GameInstall g, string forge)
    {
        lock (BaseRead) return Reading.TryGetValue(g, out var r) && r.ContainsKey(forge);
    }

    /// <summary>Why the last read of a forge for the originals failed (the base forge, or the user's vanilla backup when
    /// <paramref name="forge"/> names it), or null when it didn't or hasn't been read. A failure isn't kept as the result: the next read
    /// tries again.</summary>
    public static string? ForgeReadFailure(GameInstall g, string? forge = null)
    {
        lock (BaseRead)
        {
            forge ??= BaseForgeInTests.Value ?? g.BaseForgePath;
            if (BaseRead.TryGetValue(g, out var kept) && kept.ContainsKey(forge)) return null;
            return BaseReadFailed.TryGetValue(g, out var why) && why.TryGetValue(forge, out var w) ? w : null;
        }
    }

    /// <summary>The last failure of each install's forge reads for the originals, by the forge's path, apart from the results.</summary>
    static readonly ConditionalWeakTable<GameInstall, Dictionary<string, string>> BaseReadFailed = new();

    /// <summary>The base forge's records read for each install, by the forge's path.</summary>
    static readonly ConditionalWeakTable<GameInstall, Dictionary<string, (Dictionary<ulong, byte[]>? Records, string? Error)>> BaseRead = new();

    /// <summary>A forge that stands for the base forge on this flow of work, for tests; null otherwise.</summary>
    internal static readonly AsyncLocal<string?> BaseForgeInTests = new();

    /// <summary>The warning for each clip whose original wasn't found anywhere (<see cref="OriginalClip.Missing"/>): the package is built
    /// without it, so that clip can't be uninstalled with the package (<see cref="NoOriginal"/>), with why; or null. Never a refusal.</summary>
    public static string? MissingNote(IReadOnlyList<OriginalClip> originals)
    {
        var missing = originals.Where(o => o.Missing is not null).ToList();
        if (missing.Count == 0) return null;
        bool one = missing.Count == 1;
        return string.Join(" ", missing.Select(o => $"{Capital(NoOriginal(o.Slot.Name))}; {o.Missing}."))
            + $" To include {(one ? "it" : "them")}, name a vanilla backup of DataPC_patch_01.forge that holds {(one ? "it" : "them")}, or build on game files "
            + $"where {(one ? "that clip is" : "those clips are")} the game's own.";
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A forge read's failure in a sentence of its own: its own brackets dropped where they would nest, and its full stop.</summary>
    static string Plain(string error) => error.TrimEnd('.', ' ');

    /// <summary>What the Package screen says of the originals before Build: each clip whose original is taken from a forge, as the install
    /// holds a modded clip there; each still to be looked for; each not found (<see cref="MissingNote"/>). Null when every original is the
    /// install's own.</summary>
    public static string? OriginalsNote(IReadOnlyList<OriginalClip> originals)
    {
        var s = new List<string>();
        foreach (var o in originals.Where(o => o.From == FromBaseForge)) s.Add($"\"{OriginalsFolderName}\": {o.Slot.Name} is {FromBaseForgeWhy}.");
        foreach (var o in originals.Where(o => o.From == FromBackupForge)) s.Add($"\"{OriginalsFolderName}\": {o.Slot.Name} is {FromBackupForgeWhy}.");
        var pending = originals.Where(o => o.Pending).ToList();
        if (pending.Count > 0)
            s.Add($"Your game folder holds a modded clip in place of {string.Join(", ", pending.Select(o => o.Slot.Name))}: looking for the game's own record "
                + $"for \"{OriginalsFolderName}\"...");
        if (MissingNote(originals) is { } m) s.Add(m);
        return s.Count == 0 ? null : string.Join(" ", s);
    }

    /// <summary>Why the items can't be packaged as they stand because one is no change (<see cref="PackageEntries.Unchanged(GameInstall,
    /// PackageItem)"/>): the game's own clip unchanged, an edit that changes nothing, or an import of what the install already holds for
    /// its clip; or null. Such an item would replace nothing, so it is never packaged: the game's own clip unchanged and an edit that
    /// changed nothing are to be removed, and the install's record kept by an import (perhaps this package's own clip, installed from an
    /// earlier build) is to be built from game files that don't hold it.</summary>
    public static string? UnchangedProblem(GameInstall g, IReadOnlyList<PackageItem> items)
    {
        // the game's own clip unchanged is to be removed; what the install holds may be this package's own clip, installed from an earlier
        // build, which isn't to be dropped from the package: its own reason says the way out
        foreach (var i in items)
            if (PackageEntries.Unchanged(g, i) is { } why)
                return why == PackageEntries.GamesOwnUnchanged
                    ? $"{i.Slot.Name}: {why}, so it replaces nothing. Remove it from the package."
                    : why == PackageEntries.EditChangesNothing ? $"{i.Slot.Name}: {why}. Remove it from the package."
                    : $"{i.Slot.Name}: {why}.";
        return null;
    }

    /// <summary>Builds the items kept on <paramref name="g"/> as <paramref name="plan"/> says: their record files, the originals folder
    /// (the vanilla backup and the base forge read when the install's slot holds a modded clip; a clip whose original none holds is built
    /// without it) and the marker. Refused, with nothing written, while an item is no change (<see cref="UnchangedProblem"/>), whatever the
    /// plan says.</summary>
    public static PackageBuilt Build(GameInstall g, PackagePlan plan, IReadOnlyList<PackageItem> items, bool replace, string? backupForge = null)
    {
        if (UnchangedProblem(g, items) is { } same) throw new RecordFileException(same);
        if (plan.Problem is { } p) throw new RecordFileException(p);
        var originals = Originals(g, items, readForges: true, backupForge);
        // a clip whose original wasn't found is built without it (said on the screen and in the result); no original at all: no folder
        var found = originals.Where(o => o.Record is not null).ToList();
        var built = Build(plan, folder => RecordFileWriter.Write(g, folder, items.Select(i => (i.Slot, i.Record))),
            found.Count == 0 ? null : folder => RecordFileWriter.Write(g, folder, found.Select(o => (o.Slot, o.Record!))), replace, Protected(g));
        return built with { Originals = originals };
    }

    /// <summary>Builds a package: <paramref name="writeRecords"/> writes the record files into the folder it is given and
    /// <paramref name="writeOriginals"/> the originals into the originals folder inside it (each whole or nothing, refusing a game folder),
    /// the build's marker goes beside them, everything is read back, and the folder then takes the package's name. Refused with a
    /// <see cref="RecordFileException"/> when the plan has a problem, or the package's folder exists and isn't empty and
    /// <paramref name="replace"/> isn't set, or it doesn't carry this tool's marker for this package (<see cref="MarkerName"/>), or it holds
    /// anything but a package's files. Nothing is left behind on a refusal or a failure, the folders made for it included.</summary>
    public static PackageBuilt Build(PackagePlan plan, Func<string, RecordPackage> writeRecords, Func<string, RecordPackage>? writeOriginals, bool replace,
        IEnumerable<string> protectedFolders)
    {
        var protect = protectedFolders.ToList();
        var s = Stage(plan, writeRecords, writeOriginals, replace, protect);
        try { Place(s, protect); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RecordFileException) { throw Failed(s, ex, Undo(s)); }
        // any other error is undone the same way and rethrown as it is
        catch { Undo(s); throw; }
        return Finish(s);
    }

    /// <summary>A build written into its staging folder beside its own folder and read back: what is moved into place, and what was
    /// done, so it can be undone.</summary>
    sealed class Staged
    {
        public required PackagePlan Plan { get; init; }
        public required string Save { get; init; }
        public required string Final { get; init; }
        public required string Staging { get; init; }
        public required string Aside { get; init; }
        /// <summary>The folders made for the staging folder above it, outermost first.</summary>
        public required List<string> Made { get; init; }
        public RecordPackage? Records { get; set; }
        public RecordPackage? Originals { get; set; }
        public byte[] Marker { get; set; } = [];
        /// <summary>The earlier build was moved aside to <see cref="Aside"/>.</summary>
        public bool MovedAside { get; set; }
        /// <summary>The staging folder took the build's own name.</summary>
        public bool Placed { get; set; }
    }

    /// <summary>Writes a build into a fresh staging folder beside its own folder and reads it back; nothing in place is touched. Refused
    /// as <see cref="Build(PackagePlan, Func{string, RecordPackage}, Func{string, RecordPackage}, bool, IEnumerable{string})"/> refuses; on
    /// a refusal or a failure the staging folder (its originals folder included) and the folders made for it are removed again.</summary>
    static Staged Stage(PackagePlan plan, Func<string, RecordPackage> writeRecords, Func<string, RecordPackage>? writeOriginals, bool replace, List<string> protect)
    {
        if (plan.Problem is { } p) throw new RecordFileException(p);
        string save = Path.GetFullPath(plan.SaveFolder);
        if (GuardRefusal(save, plan.FolderName, protect) is { } refused) throw new RecordFileException(refused);
        string final = Path.Combine(save, plan.FolderName);
        if (EarlierRefusal(final, plan, replace) is { } taken) throw new RecordFileException(taken);
        string tag = Guid.NewGuid().ToString("N")[..10];
        var made = new List<string>();
        for (string? d = save; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) made.Insert(0, d);
        var s = new Staged
        {
            Plan = plan, Save = save, Final = final, Made = made,
            Staging = Path.Combine(save, $"{plan.FolderName}.{tag}.building"), Aside = Path.Combine(save, $"{plan.FolderName}.{tag}.earlier"),
        };
        string staging = Path.GetFileName(s.Staging);
        try
        {
            // the staging folder is made and given the build's marker before any record goes in, so the guard knows it as this tool's
            // build (its originals folder holds the game's own records, as an unpack does)
            if (GuardRefusal(save, staging, protect) is { } early) throw new RecordFileException(early);
            Directory.CreateDirectory(s.Staging);
            if (GuardRefusal(save, staging, protect) is { } madeThere) throw new RecordFileException(madeThere);
            s.Marker = WriteNew(Path.Combine(s.Staging, MarkerName), MarkerText(MarkedName(plan), DateTime.UtcNow, restore: false));
            // the record writer writes whole or nothing and says so itself when it refuses or fails; it refuses every folder carrying
            // this tool's marker, and every folder inside one, but this build's own staging folder, named to it for the write alone (the
            // originals folder inside it carries no marker of its own)
            string? was = RecordFileWriter.BuildingIn.Value;
            RecordFileWriter.BuildingIn.Value = s.Staging;
            try
            {
                s.Records = writeRecords(s.Staging);
                // no original found for any clip: no originals folder (the screen and the result name each clip)
                s.Originals = writeOriginals?.Invoke(Path.Combine(s.Staging, OriginalsFolderName));
            }
            finally { RecordFileWriter.BuildingIn.Value = was; }
            // the record writer guarded the staging folder; checked again once everything is in it
            if (GuardRefusal(save, staging, protect) is { } late) throw new RecordFileException(late);
            if (!File.ReadAllBytes(Path.Combine(s.Staging, MarkerName)).AsSpan().SequenceEqual(s.Marker))
                throw new RecordFileException($"{Path.Combine(s.Staging, MarkerName)} doesn't read back as written");
            return s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RecordFileException) { throw Failed(s, ex, Undo(s)); }
        // any other error is undone the same way and rethrown as it is
        catch { Undo(s); throw; }
    }

    /// <summary>Moves a staged build into place, its own folder checked right before: an earlier build there is moved aside (kept until
    /// <see cref="Finish"/>), and the staging folder takes the build's name.</summary>
    static void Place(Staged s, List<string> protect)
    {
        if (GuardRefusal(s.Save, s.Plan.FolderName, protect) is { } before) throw new RecordFileException(before);
        // looked at again right before the move: a link put there since the build was staged is never taken for the earlier build
        if (FolderGuard.IsLink(s.Final, out string? unread)) throw new RecordFileException(LinkRefusal(s.Final, unread));
        if (Directory.Exists(s.Final))
        {
            Directory.Move(s.Final, s.Aside);
            s.MovedAside = true;
        }
        Directory.Move(s.Staging, s.Final);
        s.Placed = true;
    }

    /// <summary>A build in place: the earlier build moved aside is deleted, and every file written is listed with its length and SHA-256.
    /// A link standing where the earlier build was moved is removed alone, never listed through: the folder it leads to is left as it is.</summary>
    static PackageBuilt Finish(Staged s)
    {
        string? left = null;
        if (s.MovedAside)
        {
            try { DeleteBuildFolder(s.Aside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (Directory.Exists(s.Aside)) left = s.Aside;
        }
        var written = s.Records!.Files.Select(f => f with { Path = Path.Combine(s.Final, f.FileName) }).ToList();
        if (s.Originals is not null) written.AddRange(s.Originals.Files.Select(f => f with { FileName = Path.Combine(OriginalsFolderName, f.FileName), Path = Path.Combine(s.Final, OriginalsFolderName, f.FileName) }));
        written.Add(new WrittenRecordFile(MarkerName, Path.Combine(s.Final, MarkerName), s.Marker.Length, Sha256Hex.Of(s.Marker)));
        return new PackageBuilt(s.Final, written) { EarlierLeftAt = left };
    }

    /// <summary>Deletes a build's folder: its files, and its originals folder's files and that folder, then the folder itself, each folder
    /// only once empty. Not recursive: a link (the folder itself, or one standing as its originals folder) is removed as itself, never
    /// emptied through. What can't be deleted is left; the caller looks whether the folder is gone.</summary>
    static void DeleteBuildFolder(string folder)
    {
        if (!FolderGuard.IsLink(folder))
        {
            foreach (var f in Directory.EnumerateFiles(folder)) TryDelete(f);
            string sub = Path.Combine(folder, OriginalsFolderName);
            if (Directory.Exists(sub))
            {
                if (!FolderGuard.IsLink(sub))
                    foreach (var f in Directory.EnumerateFiles(sub)) TryDelete(f);
                try { Directory.Delete(sub); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        Directory.Delete(folder);
    }

    const string NothingWritten = "Nothing was written.";

    /// <summary>Undoes a staged build, placed or not: the build in place goes back to its staging folder, the staging folder's files (its
    /// originals folder's included) and the folder are deleted, an earlier build moved aside goes back, and the folders made for it are
    /// removed, innermost first and each only while empty (a non-recursive delete never reaches through a link). Says what was left, for
    /// the message.</summary>
    static string Undo(Staged s)
    {
        if (s.Placed)
        {
            try
            {
                Directory.Move(s.Final, s.Staging);
                s.Placed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"The new build couldn't be taken out again: it is in {s.Final}." + (s.MovedAside ? $" The earlier build is in {s.Aside}." : "");
            }
        }
        try { if (Directory.Exists(s.Staging)) DeleteBuildFolder(s.Staging); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string state = Directory.Exists(s.Staging) ? $"The unfinished build couldn't be removed: it is in {s.Staging}." : NothingWritten;
        if (s.MovedAside && !Directory.Exists(s.Final))
        {
            try
            {
                Directory.Move(s.Aside, s.Final);
                s.MovedAside = false;
                state += " The earlier build is in place as it was.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { state += $" The earlier build couldn't be put back: it is in {s.Aside}."; }
        }
        foreach (var d in Enumerable.Reverse(s.Made))
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return state;
    }

    /// <summary>The refusal of a build that failed: why, without the writer's own "Nothing was written.", then what was left
    /// (<paramref name="state"/>, from <see cref="Undo"/>).</summary>
    static RecordFileException Failed(Staged s, Exception ex, string state)
    {
        string why = ex.Message.EndsWith(NothingWritten, StringComparison.Ordinal) ? ex.Message[..^NothingWritten.Length].TrimEnd() : ex.Message;
        return new RecordFileException($"The package couldn't be built in {s.Final}: {why} {state}", ex);
    }

    /// <summary>Why the build's folder <paramref name="final"/> (full) isn't built into, or null: it is a file, or it exists and isn't
    /// empty and <paramref name="replace"/> isn't set, or it carries no marker of this tool, or the marker names another build or a build
    /// of the other kind, or it holds anything but a package's files (its originals folder of record files counts as one).</summary>
    static string? EarlierRefusal(string final, PackagePlan plan, bool replace)
    {
        // a link's own entry is looked at first: its target's marker and files aren't this place's, and nothing is deleted through it
        if (FolderGuard.IsLink(final, out string? unread)) return LinkRefusal(final, unread);
        if (File.Exists(final)) return $"{final} is a file, so the package's folder can't be made there. Nothing was written.";
        if (!Directory.Exists(final) || !Directory.EnumerateFileSystemEntries(final).Any()) return null;
        if (!replace) return $"{final} exists already and isn't empty. Tick \"Replace an earlier build of this package\" to replace it, or give the package another name. Nothing was written.";
        if (!IsEarlierBuild(final))
            return $"{final} isn't a package this tool built (it has no {MarkerName} marker file), so it isn't replaced: give the package another name or pick another save folder. Nothing was written.";
        if (!IsEarlierBuild(final, MarkedName(plan), restore: false))
            return $"{final} is a package this tool built, but its marker file names another package than \"{MarkedName(plan)}\" (or it is a folder of the game's own files a "
                + "pre-release build of this tool wrote beside a package), so it isn't replaced: give the package another name or pick another save folder. Nothing was written.";
        var foreign = Directory.EnumerateFileSystemEntries(final).Where(e => !IsPackageFile(e) && !IsOriginalsFolder(e)).Take(3).ToList();
        if (foreign.Count > 0)
            return $"{final} holds things a package doesn't ({string.Join(", ", foreign.Select(Path.GetFileName))}), so it isn't replaced: give the package another name. Nothing was written.";
        return null;
    }

    /// <summary>Whether <paramref name="path"/> is a package's originals folder as a build writes it: a folder of that name (not a link)
    /// holding record files alone.</summary>
    static bool IsOriginalsFolder(string path) =>
        string.Equals(Path.GetFileName(path), OriginalsFolderName, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path) && !FolderGuard.IsLink(path)
        && Directory.EnumerateFileSystemEntries(path).All(e => File.Exists(e) && e.EndsWith(RecordFile.ClipExtension, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="folder"/> is itself a link to another place (a junction or a symbolic link), so no build replaces
    /// it (see <see cref="FolderGuard.IsLink(string, out string?)"/>): what the Package screen says of a folder standing at the package's
    /// place. An entry there that can't be read counts too, and <paramref name="unread"/> then says why.</summary>
    public static bool IsLink(string folder, out string? unread) => FolderGuard.IsLink(folder, out unread);

    /// <summary>As <see cref="IsLink(string, out string?)"/>, without the reason.</summary>
    public static bool IsLink(string folder) => FolderGuard.IsLink(folder);

    /// <summary>Why a build's own place <paramref name="final"/>, a link to another folder (or an entry that can't be read,
    /// <paramref name="unread"/> saying why), is never built into: what it leads to isn't this build's, and a later build would delete or
    /// move that folder's files through it.</summary>
    static string LinkRefusal(string final, string? unread) => unread is null
        ? $"{final} is a link (a junction or a symbolic link) to another folder, so it isn't replaced and nothing is deleted or moved through it: "
            + "remove the link, or give the package another name. Nothing was written."
        : $"{final} can't be read ({unread}), so it isn't replaced and nothing is deleted or moved through it: give the package another name or pick another save folder. Nothing was written.";

    /// <summary>The name a build's marker records: the package's name as given, else its folder's.</summary>
    static string MarkedName(PackagePlan plan) => plan.PackageName.Length > 0 ? plan.PackageName : plan.FolderName;

    /// <summary>Writes <paramref name="text"/> (UTF-8) to a new file and reads it back; returns the bytes written.</summary>
    static byte[] WriteNew(string path, string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.Write(bytes);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new RecordFileException($"{path} doesn't read back as written");
        return bytes;
    }

    /// <summary>Folders a build of this package left beside it when it didn't finish (the program ended between its steps):
    /// <c>name.tag.building</c> (a build not yet in place) and <c>name.tag.earlier</c> (an earlier build moved aside), full paths. Build
    /// leaves them as they are and the Package screen names them (<see cref="BesideNotes"/>).</summary>
    public static IReadOnlyList<string> LeftoverFolders(PackagePlan plan)
    {
        if (plan.SaveFolder.Length == 0 || plan.FolderName.Length == 0) return [];
        try
        {
            string save = Path.GetFullPath(plan.SaveFolder);
            if (!Directory.Exists(save)) return [];
            // the tag is the first 10 hex digits of a new GUID (see Stage)
            var left = new Regex($@"^{Regex.Escape(plan.FolderName)}\.[0-9a-f]{{10}}\.(?:building|earlier)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Directory.EnumerateDirectories(save).Where(d => left.IsMatch(Path.GetFileName(d))).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return []; }
    }

    /// <summary>What the Package screen says about folders a build of this package that didn't finish left beside it
    /// (<see cref="LeftoverFolders"/>), which Build leaves as they are; none when there are none.</summary>
    public static IReadOnlyList<string> BesideNotes(PackagePlan plan) =>
        LeftoverFolders(plan) is { Count: > 0 } left
            ? [$"A build of this package that didn't finish left {(left.Count == 1 ? "this folder" : "these folders")} beside it: {string.Join(", ", left)}. "
                + "They aren't part of the package: delete them once you've checked you don't need them."]
            : [];

    /// <summary>Whether a folder entry is a file a package build writes: a record file or the marker, or a text an earlier version of this
    /// tool wrote beside them (<see cref="EarlierTextFiles"/>).</summary>
    static bool IsPackageFile(string path) =>
        File.Exists(path) && (path.EndsWith(RecordFile.ClipExtension, StringComparison.Ordinal) || Path.GetFileName(path) == MarkerName
            || EarlierTextFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal));

    static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
