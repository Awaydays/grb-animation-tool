using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GrbAnimTool.Core.IO;

/// <summary>Whether a path is, or lies in, one of some protected folders, by where it really leads and not only by how it is spelt. Both
/// sides are compared as canonical places, case ignored as on Windows:
/// <list type="bullet">
/// <item>the spelling: the full path with any <c>\\?\</c> prefix taken off, and an administrative share of this computer
/// (<c>\\localhost\D$\...</c>, also by its name, any of its addresses or a 127.x.x.x address) turned back into its drive; as a share the
/// user made may be named like one, such a path is also checked as given, over the share;</item>
/// <item>the final path: the path and each existing folder above it is opened and asked for its final path, which follows every junction
/// and symbolic link and also a <c>subst</c> drive (its letter only names a folder of another volume, and the opened folder's final path
/// is on that volume) and an 8.3 short name; the part of the path that doesn't exist yet is added to it;</item>
/// <item>the identity: the path and each existing folder above it is compared with each protected folder by volume serial number and
/// file ID, which also holds through a network share that leads back to this computer. A folder opened over a share whose volume
/// serial number is a local volume's is also opened on that volume by its file ID, and its final path there and each folder above it
/// compared by spelling and identity, whatever name the share was reached by: a share (or a drive letter mapped to one) whose root lies
/// inside a protected folder never passes through the folder itself.</item>
/// </list>
/// It fails closed: an existing folder on the path that can't be opened, or whose final path can't be read, refuses the path, as does a
/// folder over a share whose place on this computer can't be had (the share gives no file IDs, say), and so do
/// forms a file to write never needs: a device path (<c>\\.\...</c>), an NT namespace path (<c>\??\</c>, <c>\\?\GLOBALROOT\</c>,
/// <c>\\?\Volume{...}\</c>), and a <c>:</c> after the drive, which names a stream of a file or folder (<c>folder:name</c>,
/// <c>folder::$INDEX_ALLOCATION</c>). Folders are opened by their <c>\\?\</c> path, so a path over 260 characters is checked too.</summary>
static partial class FolderGuard
{
    internal sealed record Place(string Given, List<string> Spellings, (ulong Volume, UInt128 File)? Id);

    /// <summary>Whether <paramref name="path"/> (a file or a folder, existing or not) is refused for <paramref name="folders"/>
    /// (see <see cref="Refusal"/>). Empty entries are skipped.</summary>
    public static bool Inside(string path, IEnumerable<string> folders) => Refusal(path, folders) is not null;

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies below it (see <see cref="Inside"/>).</summary>
    public static bool IsSameOrInside(string path, string folder) => Inside(path, [folder]);

    /// <summary>Why <paramref name="path"/> (a file or a folder, existing or not) is refused: it is one of <paramref name="folders"/> or
    /// lies below one, it is of a form the guard doesn't accept, or a folder on it can't be checked. Null when it is allowed; when no
    /// folder is given (empty entries are skipped) only the form is checked. The reason is a plain clause for the user's message.</summary>
    public static string? Refusal(string path, IEnumerable<string> folders)
    {
        if (FormRefused(path) is { } form) return form;
        var guards = folders.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Protected).ToList();
        if (guards.Count == 0) return null;
        string full = Canonical(path);
        if (Hit(full, guards) is { } g) return In(g);
        if (!OperatingSystem.IsWindows()) return null;
        var volumes = new Lazy<List<(string Root, ulong Serial)>?>(LocalVolumes);
        // the path as given too: a share named like an administrative one (X$) may be one the user made, rooted anywhere
        string given = Canonical(path, adminShares: false);
        return Walk(full, guards, volumes) ?? (given.Equals(full, StringComparison.OrdinalIgnoreCase) ? null : Walk(given, guards, volumes));
    }

    /// <summary>The checks of <see cref="Refusal"/> on each existing folder of <paramref name="full"/>, from the deepest up.</summary>
    static string? Walk(string full, List<Place> guards, Lazy<List<(string Root, ulong Serial)>?> volumes)
    {
        for (string? d = full; d is not null; d = Path.GetDirectoryName(d))
        {
            // the folders that exist on the path, and the path itself when it is an existing file (a file link is followed by a write)
            if (!Directory.Exists(d) && !(d == full && File.Exists(d))) continue;
            using var h = Open(d, out int error);
            if (h is null) return $"{d} exists but couldn't be opened to check where it leads ({new Win32Exception(error).Message})";
            if (FinalPath(h) is not { } f) return $"{d} exists but where it really leads couldn't be read ({new Win32Exception(Marshal.GetLastPInvokeError()).Message})";
            string rest = Path.GetRelativePath(d, full);
            string there = Canonical(rest == "." ? f : Path.Join(f, rest));
            if (Hit(there, guards) is { } g2) return In(g2) + $" ({d} leads to {Canonical(f)})";
            var id = Identity(h);
            if (id is { } i && guards.FirstOrDefault(x => x.Id is { } gi && Same(gi, i)) is { } g3) return In(g3) + $" ({d} is that folder)";
            // a share's root may lie inside a protected folder, and the walk ends at that root without meeting the folder itself
            if (f.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                && ShareRefusal(d, id, guards, volumes.Value, root => Here(root, id!.Value.File)) is { } shared) return shared;
        }
        return null;
    }

    /// <summary>The host of <paramref name="p"/> when it is a share's path (<c>\\host\...</c> or <c>\\?\UNC\host\...</c>); else null.</summary>
    static string? UncHost(string p)
    {
        string? rest = p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? p[8..]
            : p.StartsWith(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\?\", StringComparison.Ordinal) && !p.StartsWith(@"\\.\", StringComparison.Ordinal) ? p[2..]
            : null;
        if (rest is null) return null;
        int end = rest.IndexOf('\\');
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>Why <paramref name="d"/>, a folder opened over a network share whose identity is <paramref name="id"/>, is refused, by
    /// where it stands on this computer, whatever name the share was reached by: for each local volume of <paramref name="volumes"/> with
    /// its volume serial number (the share leads back here), <paramref name="here"/> opens it on that volume's root by its file ID and
    /// gives its local final path and each folder above it, with their identities (null: it couldn't be opened or read). It is refused
    /// when one of them is, or lies in, a protected folder by spelling or by identity (a protected folder opened through a share of this
    /// computer has its local identity), and it fails closed when the answer can't be had: the share gives no file IDs, a protected folder
    /// on a share gives none, the local volumes couldn't be listed (<paramref name="volumes"/> null) or the place there couldn't be read.
    /// A share whose volume serial is no local volume's is another computer's (a NAS) and refuses nothing here, as the walk's own checks
    /// already matched it.</summary>
    internal static string? ShareRefusal(string d, (ulong Volume, UInt128 File)? id, List<Place> guards, List<(string Root, ulong Serial)>? volumes,
        Func<string, List<(string Path, (ulong Volume, UInt128 File)? Id)>?> here)
    {
        const string Way = "choose the folder by its drive path";
        if (id is not { } i)
            return $"{d} is on a network share that gives no file IDs, so whether it leads into a folder the tool only reads couldn't be checked: {Way}";
        // an existing protected folder (its final path read) on a share without file IDs can't be matched by identity
        if (guards.FirstOrDefault(g => g.Id is null && g.Spellings.Count > 1 && UncHost(g.Spellings[^1]) is not null) is { } blind)
            return $"{blind.Given}, a folder the tool only reads, is on a network share that gives no file IDs, so whether {d} leads into it couldn't be checked: {Way}, or give {blind.Given} by its drive path";
        if (volumes is null)
            return $"{d} is on a network share, and the volumes of this computer couldn't be listed to check where it stands: {Way}";
        foreach (var (root, _) in volumes.Where(v => SameVolume(v.Serial, i.Volume)))
        {
            if (here(root) is not { Count: > 0 } chain)
                return $"{d} is on a network share on a volume of this computer, and where it stands on this computer couldn't be read: {Way}";
            foreach (var (p, pid) in chain)
                if ((Hit(p, guards) ?? guards.FirstOrDefault(g => g.Id is { } gi && pid is { } x && Same(gi, x))) is { } g)
                    return In(g) + $" ({d} is {chain[0].Path} on this computer)";
        }
        return null;
    }

    /// <summary>Whether two identities are the same file: the same file ID on the same volume (<see cref="SameVolume"/>).</summary>
    static bool Same((ulong Volume, UInt128 File) a, (ulong Volume, UInt128 File) b) => a.File == b.File && SameVolume(a.Volume, b.Volume);

    /// <summary>Whether two volume serial numbers are one volume's: equal, or one of them only 32 bits (as
    /// GetFileInformationByHandle gives it) and equal to the other's low 32 bits.</summary>
    static bool SameVolume(ulong a, ulong b) => a == b || (a >> 32 == 0 || b >> 32 == 0) && (uint)a == (uint)b;

    /// <summary>The folder or file of <paramref name="file"/> on the local volume <paramref name="root"/> (<c>\\?\Volume{...}\</c>),
    /// opened by that ID: its canonical final path and each folder above it, with their identities; null when any of them couldn't be
    /// opened or the final path couldn't be read.</summary>
    internal static List<(string Path, (ulong Volume, UInt128 File)? Id)>? Here(string root, UInt128 file)
    {
        using var volume = Open(root, out _);
        using var h = volume is null ? null : OpenById(volume, file);
        if (h is null || FinalPath(h) is not { } f) return null;
        var chain = new List<(string, (ulong, UInt128)?)>();
        for (string? t = Canonical(f); t is not null; t = Path.GetDirectoryName(t))
        {
            using var th = Open(t, out _);
            if (th is null) return null;
            chain.Add((t, Identity(th)));
        }
        return chain;
    }

    /// <summary>Each local volume's root (<c>\\?\Volume{...}\</c>) and volume serial number; a volume that can't be opened (a drive
    /// without its medium) is left out. Null when the volumes can't be listed.</summary>
    internal static List<(string Root, ulong Serial)>? LocalVolumes()
    {
        var name = new char[300];
        nint find = FindFirstVolumeW(name, (uint)name.Length);
        if (find == -1) return null;
        var l = new List<(string, ulong)>();
        try
        {
            do
            {
                int n = Array.IndexOf(name, '\0');
                if (n <= 0) continue;
                string root = new(name, 0, n);
                using var h = Open(root, out _);
                if (h is not null && Identity(h) is { } id) l.Add((root, id.Volume));
            }
            while (FindNextVolumeW(find, name, (uint)name.Length));
        }
        finally { FindVolumeClose(find); }
        return l;
    }

    /// <summary>How many of the game's own clip files make a folder an unpacked datafile's (see
    /// <see cref="UnpackedDatafileRefusal(string, bool)"/>): the animation datafile's unpack holds 9,938, nearly all of them the game's
    /// own even after packages are installed; a folder of the player's own unchanged clips (written or imported unchanged, a few at a
    /// time) stays far below it, a package's files are edited clips, and a package's original clips lie in a folder right inside one
    /// carrying this tool's build marker (as an earlier version's local undo folder carries it).</summary>
    public const int UnpackedRecordFiles = 1000;

    /// <summary>The way out every writer gives for an unpacked datafile's folder.</summary>
    public const string UnpackedDatafileAdvice = "write to an empty folder and copy the files in yourself";

    /// <summary>As <see cref="UnpackedDatafileRefusal(string, bool)"/>, with <paramref name="path"/> taken as a folder unless it is an
    /// existing file.</summary>
    public static string? UnpackedDatafileRefusal(string path) => UnpackedDatafileRefusal(path, file: false);

    /// <summary>Why <paramref name="path"/> (a folder, or a file when <paramref name="file"/> or when it exists as one; existing or not)
    /// lies in what looks like a datafile unpacked from a forge, where any file written would be packed into the game's datafile with its
    /// records; null when it doesn't. Two rules:
    /// <list type="bullet">
    /// <item>the name: the path, any folder above it, or any folder on the whole path an existing folder of it really leads to (through a
    /// junction, a symbolic link or a <c>subst</c> drive) is named <c>digits_-_name.data</c>, case ignored (the form unpacking gives
    /// it). An existing folder whose real path can't be read refuses the path;</item>
    /// <item>the content: the folder the files go into (the path itself, or a file's own folder) already holds
    /// <see cref="UnpackedRecordFiles"/> or more of the game's own clip files, named <c>index_-_name.Animation</c> (see
    /// <see cref="IsGameClipFile"/>): the game's own record by the table of the game's clips built into the tool, which needs no install
    /// open, or byte for byte an open install's own record. A folder carrying this tool's build marker (a package or a local undo folder),
    /// and a package's folder of original clips right inside one, is never refused by it. The folders above it aren't counted, so a folder of the player's own inside a folder of record files is
    /// allowed.</item>
    /// </list></summary>
    public static string? UnpackedDatafileRefusal(string path, bool file)
    {
        string full;
        try { full = Canonical(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }   // the form guard says why
        for (string? d = full; d is not null; d = Path.GetDirectoryName(d))
        {
            if (UnpackName(Path.GetFileName(d)))
                return $"{d} is named as an unpacked datafile's folder (number_-_name.data); {UnpackedDatafileAdvice}";
            if (!OperatingSystem.IsWindows() || !Directory.Exists(d)) continue;
            // fail closed, with the reason alone: it isn't known to be an unpack
            using var h = Open(d, out int error);
            if (h is null) return $"{d} exists but couldn't be opened to check where it leads ({new Win32Exception(error).Message})";
            if (FinalPath(h) is not { } f) return $"{d} exists but where it really leads couldn't be read ({new Win32Exception(Marshal.GetLastPInvokeError()).Message})";
            // the whole path it leads to, not only its last name: a link to a folder inside an unpack leads into the unpack
            string there = Canonical(f);
            for (string? t = there; t is not null; t = Path.GetDirectoryName(t))
                if (UnpackName(Path.GetFileName(t)))
                    return $"{d} leads to {there}{(t == there ? "," : $", inside {t},")} named as an unpacked datafile's folder (number_-_name.data); {UnpackedDatafileAdvice}";
        }
        string? folder = file || File.Exists(full) ? Path.GetDirectoryName(full) : full;
        if (folder is not null && GameClipFilesIn(folder) is var n && n >= UnpackedRecordFiles)
            return $"the folder {folder} holds {n:N0} or more of the game's own clip files, as an unpacked animation datafile does; {UnpackedDatafileAdvice}";
        return null;
    }

    /// <summary>As <see cref="IsLink(string, out string?)"/>, without the reason an entry couldn't be read.</summary>
    public static bool IsLink(string path) => IsLink(path, out _);

    /// <summary>Whether <paramref name="path"/> is itself a link to another place (a junction or a symbolic link), whether what it leads
    /// to exists or not; false when nothing stands there, it is an ordinary folder or file, or the path can't name a file at all (a
    /// character Windows refuses in a name, say: a path error, which the plan says as such, and nothing can stand there). Its own entry is
    /// read, never followed. An entry that stands there but can't be read, or a reparse point whose target can't be read, counts as a link,
    /// so nothing is deleted or moved through it unseen; <paramref name="unread"/> then says why, so it is never called a link in words.</summary>
    public static bool IsLink(string path, out string? unread)
    {
        unread = null;
        FileAttributes a;
        try { a = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException || IsPathError(ex)) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { unread = ex.Message; return true; }
        if ((a & FileAttributes.ReparsePoint) == 0) return false;
        // other reparse points (a cloud file's, say) are the folder itself, not a link
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { unread = ex.Message; return true; }
    }

    /// <summary>Whether <paramref name="ex"/> says the path itself can't name a file (an invalid name or path syntax), not that the
    /// entry there can't be read.</summary>
    static bool IsPathError(Exception ex) =>
        ex is ArgumentException or NotSupportedException or PathTooLongException
        // ERROR_INVALID_NAME, ERROR_BAD_PATHNAME, ERROR_DIRECTORY (the directory name is invalid)
        || ex is IOException && (ex.HResult & 0xFFFF) is 123 or 161 or 267 && (ex.HResult >>> 16) == 0x8007;

    /// <summary>The characters Windows refuses in a folder's name (besides the separators, and the colon, which a path holds after its
    /// drive and the guard's form rule refuses elsewhere).</summary>
    static readonly char[] RefusedInNames = ['"', '*', '?', '<', '>', '|'];

    /// <summary>Why the path <paramref name="path"/> can't be used as a folder: past its root it holds a character Windows refuses in a
    /// folder name (<c>" * ? &lt; &gt; |</c> or a control character), said as a clause naming it; or null.</summary>
    public static string? RefusedNameChar(string path)
    {
        string root;
        try { root = Path.GetPathRoot(path) ?? ""; }
        catch (ArgumentException) { root = ""; }
        foreach (char ch in path.AsSpan(root.Length))
        {
            if (ch < 0x20) return $"it holds a control character (U+{(int)ch:X4}), which Windows refuses in a folder name";
            if (RefusedInNames.Contains(ch)) return $"it holds '{ch}', a character Windows refuses in a folder name";
        }
        return null;
    }

    /// <summary>What every writer says after <see cref="BuildFolderRefusal"/>'s reason.</summary>
    public const string BuildFolderAdvice = "and whatever is written in one would go with it. Pick a folder outside your packages.";

    /// <summary>Why <paramref name="path"/> (a file or a folder, existing or not) lies in a folder carrying this tool's build marker (a
    /// package or a local undo folder, see <see cref="Package.PackageBuild.MarkerName"/>), where whatever is written would be shared with
    /// that folder; null when it doesn't. Found by the path's spelling (the path or a folder above it carries the marker) and by where it
    /// really leads: the deepest existing folder on it is opened and asked for its final path, which follows junctions, symbolic links and
    /// <c>subst</c> drives, and that path and every folder above it are looked at too, so a link to a folder inside a package is caught.
    /// <paramref name="except"/>, when given, is a folder that doesn't count, by its spelling and by its final path: a build's own staging
    /// folder while the build writes into it. The reason is a clause for the user's message, before <see cref="BuildFolderAdvice"/>.</summary>
    public static string? BuildFolderRefusal(string path, string? except = null)
    {
        string full;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            full = Canonical(path);
            if (except is not null)
            {
                string e = Canonical(except);
                skip.Add(e);
                if (OperatingSystem.IsWindows() && Directory.Exists(e))
                {
                    using var eh = Open(e, out _);
                    if (eh is not null && FinalPath(eh) is { } ef) skip.Add(Canonical(ef));
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }   // the form guard says why
        const string What = "a package this tool built, or a local undo folder an earlier version of it wrote";
        bool Build(string d) => !skip.Contains(d) && Package.PackageBuild.IsEarlierBuild(d);
        string? deepest = null;
        for (string? d = full; d is not null; d = Path.GetDirectoryName(d))
        {
            if (!Directory.Exists(d)) continue;
            deepest ??= d;
            if (Build(d)) return d.Equals(full, StringComparison.OrdinalIgnoreCase) ? $"it is {What}" : $"it lies inside {d}, {What}";
        }
        if (deepest is null || !OperatingSystem.IsWindows()) return null;
        // where it really leads; a folder that can't be opened or read is refused by the guard's other rules, which fail closed
        using var h = Open(deepest, out _);
        if (h is null || FinalPath(h) is not { } f) return null;
        string there = Canonical(f);
        if (there.Equals(deepest, StringComparison.OrdinalIgnoreCase)) return null;
        for (string? t = there; t is not null; t = Path.GetDirectoryName(t))
            if (Build(t)) return $"{deepest} leads to {there}{(t.Equals(there, StringComparison.OrdinalIgnoreCase) ? "," : $", inside {t},")} {What}";
        return null;
    }

    /// <summary>Whether a folder name is of the form unpacking gives a datafile's folder, <c>digits_-_name.data</c>, case ignored as
    /// Windows ignores it.</summary>
    static bool UnpackName(string name) =>
        name.EndsWith(".data", StringComparison.OrdinalIgnoreCase) && Records.RecordFile.DatafileNameOf(name[..^5] + ".data") is not null;

    /// <summary>A file named <c>index_-_name.Animation</c> right in a folder: its index (leading zeros taken), its name as spelt, and
    /// what listing the folder gave of it.</summary>
    readonly record struct ClipFileName(FileInfo File, int Index, string Name);

    /// <summary>The files right in <paramref name="dir"/> that are the game's own clip files (see <see cref="IsGameClipFile"/>), counted
    /// up to <see cref="UnpackedRecordFiles"/>; a smaller number when it can't reach it, and none when the folder doesn't exist, carries
    /// this tool's build marker or can't be listed (a write there fails on its own). A folder holding fewer files of that form than the
    /// count is never read, so a folder of the player's own costs one listing; a larger one is read file by file until the count is
    /// reached or can't be any more, and the result is kept for the folder while the listing (each such file's name, length and time) and
    /// the open installs stay the same.</summary>
    static int GameClipFilesIn(string dir)
    {
        if (!Directory.Exists(dir) || Package.PackageBuild.IsEarlierBuild(dir) || IsOriginalsOfBuild(dir)) return 0;
        List<ClipFileName> files;
        try { files = ClipFilesIn(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return 0; }
        if (files.Count < UnpackedRecordFiles) return 0;
        var (installs, version) = InstallsAndVersion();
        byte[] listing = Listing(files);
        string key = Path.TrimEndingDirectorySeparator(dir);
        lock (Counted)
            if (Counted.TryGetValue(key, out var c) && c.Version == version && c.Listing.AsSpan().SequenceEqual(listing)) return c.Count;
        var table = Table.Value;
        int n = 0;
        for (int i = 0; i < files.Count && n < UnpackedRecordFiles && n + (files.Count - i) >= UnpackedRecordFiles; i++)
            if (IsGameClipFile(files[i], installs, table)) n++;
        lock (Counted)
        {
            if (Counted.Count >= 32) Counted.Clear();
            Counted[key] = (listing, version, n);
        }
        return n;
    }

    /// <summary>Whether <paramref name="dir"/> is a package's folder of original clips (<see cref="Package.PackageBuild.OriginalsFolderName"/>)
    /// right inside a folder carrying this tool's build marker: the game's own records of the clips the package replaces, never an unpack.</summary>
    static bool IsOriginalsOfBuild(string dir) =>
        string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), Package.PackageBuild.OriginalsFolderName, StringComparison.OrdinalIgnoreCase)
        && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir)) is { } parent && Package.PackageBuild.IsEarlierBuild(parent);

    /// <summary>The count of game clip files kept per folder (see <see cref="GameClipFilesIn"/>), with the listing and the installs'
    /// version it was made for.</summary>
    static readonly Dictionary<string, (byte[] Listing, int Version, int Count)> Counted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The files right in <paramref name="dir"/> named <c>index_-_name.Animation</c> (case ignored), sorted by name.</summary>
    static List<ClipFileName> ClipFilesIn(string dir)
    {
        var l = new List<ClipFileName>();
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*_-_*" + Records.RecordFile.ClipExtension))
        {
            string n = f.Name;
            if (!n.EndsWith(Records.RecordFile.ClipExtension, StringComparison.OrdinalIgnoreCase)) continue;
            string stem = n[..^Records.RecordFile.ClipExtension.Length];
            int sep = stem.IndexOf("_-_", StringComparison.Ordinal);
            if (sep <= 0 || sep + 3 >= stem.Length
                || !int.TryParse(stem.AsSpan(0, sep), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)) continue;
            l.Add(new ClipFileName(f, index, stem[(sep + 3)..]));
        }
        l.Sort((a, b) => string.CompareOrdinal(a.File.Name, b.File.Name));
        return l;
    }

    /// <summary>The SHA-256 of a folder's clip files as the listing gave them: each one's name, length and last write time.</summary>
    static byte[] Listing(List<ClipFileName> files)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> nums = stackalloc byte[16];
        foreach (var f in files)
        {
            h.AppendData(System.Text.Encoding.UTF8.GetBytes(f.File.Name + "\0"));
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(nums, f.File.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(nums[8..], f.File.LastWriteTimeUtc.Ticks);
            h.AppendData(nums);
        }
        return h.GetHashAndReset();
    }

    /// <summary>The table of the game's own clips built into the tool, read once; null when this build has none.</summary>
    static readonly Lazy<Install.VanillaClipHashes?> Table = new(() =>
    {
        try { return Install.VanillaClipHashes.Embedded(); }
        catch (InvalidDataException) { return null; }
    });

    /// <summary>No clip record comes near this length; a file longer than it isn't read.</summary>
    const long LongestClipFile = 64L << 20;

    /// <summary>Whether <paramref name="f"/> is one of the game's own clip files as the unpack writes it, by either of two checks:
    /// <list type="bullet">
    /// <item>by the table of the game's clips built into the tool (<paramref name="table"/>), which needs no install: the whole record
    /// rebuilt from the file and the name its file name gives (<see cref="Records.RecordFile.ToRecord"/>) hashes to the game's own record
    /// of the ID the file's head holds. The index isn't used, so the files of game files of another date count too;</item>
    /// <item>by an open install (<paramref name="installs"/>): the install's clip record of the file's index, of the file's name (case
    /// ignored), whose bytes from the byte after its name are the file's. This also counts a name spelt in another case, and a clip the
    /// table doesn't know (game files newer than the table, or a clip another package replaced in that install).</item>
    /// </list>
    /// A file that can't be read isn't one.</summary>
    static bool IsGameClipFile(ClipFileName f, List<Install.GameInstall> installs, Install.VanillaClipHashes? table)
    {
        long length = f.File.Length;
        if (length > LongestClipFile) return false;
        byte[]? bytes = null;
        try
        {
            foreach (var g in installs)
            {
                if (f.Index >= g.Records.Count) continue;
                var r = g.Records[f.Index];
                if (r.Index != f.Index || r.Type != Clips.ClipRecord.Type || !r.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var rec = r.Bytes.Span;
                if (rec.Length < 12) continue;
                int nl = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(rec[8..]);
                if (nl < 0 || nl > rec.Length - 12) continue;
                var own = rec[(12 + nl)..];
                if (length != own.Length) continue;
                bytes ??= File.ReadAllBytes(f.File.FullName);
                if (own.SequenceEqual(bytes)) return true;
            }
            if (table is null) return false;
            bytes ??= File.ReadAllBytes(f.File.FullName);
            if (Records.RecordFile.IdOf(bytes) is not { } id || !table.Contains(id)) return false;
            byte[] record;
            try { record = Records.RecordFile.ToRecord(bytes, f.Name); }
            catch (Records.RecordFileException) { return false; }
            return table.StatusOf(id, record) == Install.ClipStatus.Vanilla;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    static readonly List<WeakReference<Install.GameInstall>> OpenInstalls = [];

    /// <summary>Changes each time an install is noted as open or closed, so a count kept for a folder is made again.</summary>
    static int _installsVersion;

    /// <summary>Notes an install as open: its records are game files the content rule of
    /// <see cref="UnpackedDatafileRefusal(string, bool)"/> also looks for (see <see cref="IsGameClipFile"/>).</summary>
    internal static void Remember(Install.GameInstall g)
    {
        lock (OpenInstalls)
        {
            OpenInstalls.Add(new(g));
            _installsVersion++;
        }
    }

    /// <summary>Notes an install as closed.</summary>
    internal static void Forget(Install.GameInstall g)
    {
        lock (OpenInstalls)
        {
            OpenInstalls.RemoveAll(w => !w.TryGetTarget(out var x) || ReferenceEquals(x, g));
            _installsVersion++;
        }
    }

    /// <summary>For the tests, on their own thread only: the open installs aren't looked at, so the content rule runs on the table of
    /// the game's clips alone, as it does where no install is open.</summary>
    [ThreadStatic] internal static bool InstallsIgnoredForTest;

    /// <summary>The installs noted as open and still alive.</summary>
    internal static List<Install.GameInstall> Installs() => InstallsAndVersion().Installs;

    static (List<Install.GameInstall> Installs, int Version) InstallsAndVersion()
    {
        if (InstallsIgnoredForTest) return ([], -1);
        lock (OpenInstalls)
        {
            // an install collected without being closed changes the set too
            if (OpenInstalls.RemoveAll(w => !w.TryGetTarget(out _)) > 0) _installsVersion++;
            return (OpenInstalls.Select(w => w.TryGetTarget(out var x) ? x : null).OfType<Install.GameInstall>().ToList(), _installsVersion);
        }
    }

    static Place? Hit(string full, List<Place> guards) => guards.FirstOrDefault(g => g.Spellings.Any(s => Under(full, s)));

    static string In(Place g) => $"it is in {g.Given}, a folder the tool only reads";

    /// <summary>Why the path's form is refused before it is looked at, or null.</summary>
    static string? FormRefused(string path)
    {
        string p = path.Replace('/', '\\');
        if (p.Length == 0 || p.Contains('\0')) return "it is empty or holds a null character";
        if (p.StartsWith(@"\\.\", StringComparison.Ordinal)) return "it is a device path (\\\\.\\), not a folder of yours";
        if (p.StartsWith(@"\??\", StringComparison.Ordinal) || (p.StartsWith(@"\\?\", StringComparison.Ordinal) && !IsDriveOrUnc(p[4..])))
            return "it is an NT namespace path (\\??\\, \\\\?\\GLOBALROOT, \\\\?\\Volume{...} and the like): give it with its drive letter";
        // under \\?\ a name is taken as it is spelt, so a name ending in a space or a dot, or a . or .. part, isn't what it looks like
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)
            && p[4..].Split('\\').Skip(p[4..].StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ? 3 : 1).Any(n => n.EndsWith('.') || n.EndsWith(' ')))
            return "under \\\\?\\ it holds a name ending in a space or a dot, or a . or .. part: give it without them";
        string full, c;
        try
        {
            full = Path.GetFullPath(path);
            c = Canonical(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"it isn't a path Windows reads ({ex.Message})";
        }
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.StartsWith(@"\??\", StringComparison.Ordinal)
            || c.StartsWith(@"\\?\", StringComparison.Ordinal) || c.StartsWith(@"\\.\", StringComparison.Ordinal))
            return "it is a device or NT namespace path, not a folder of yours";
        // after the drive's colon, or after a share's host (an IPv6 host holds colons of its own)
        int from = c.StartsWith(@"\\", StringComparison.Ordinal) ? c.IndexOf('\\', 2) : 2;
        if (from >= 0 && c.AsSpan(from).Contains(':'))
            return "it holds a ':' after the drive, which names a stream of a file or folder rather than a file";
        return null;
    }

    static bool IsDriveOrUnc(string rest) =>
        rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)
        || rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && (rest.Length == 2 || rest[2] == '\\');

    static Place Protected(string dir)
    {
        var spellings = new List<string>();
        (ulong, UInt128)? id = null;
        try
        {
            string full = Canonical(dir);
            spellings.Add(full);
            if (OperatingSystem.IsWindows() && Directory.Exists(full))
            {
                using var h = Open(full, out _);
                if (h is not null)
                {
                    if (FinalPath(h) is { } f) spellings.Add(Canonical(f));
                    id = Identity(h);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { spellings.Add(dir); }
        return new Place(dir, spellings, id);
    }

    /// <summary>A path's full spelling without a <c>\\?\</c> or <c>\\.\</c> prefix before a drive, an administrative share of this
    /// computer as its drive (unless <paramref name="adminShares"/> is false), and no separator at the end.</summary>
    internal static string Canonical(string path, bool adminShares = true)
    {
        string p = Path.GetFullPath(path);
        for (int round = 0; round < 4; round++)
        {
            string q = p;
            if (q.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) q = @"\\" + q[8..];
            else if ((q.StartsWith(@"\\?\", StringComparison.Ordinal) || q.StartsWith(@"\\.\", StringComparison.Ordinal)) && q.Length >= 6 && q[5] == ':')
                q = q[4..];
            if (adminShares) q = AdminShare(q);
            if (q == p) break;
            p = Path.GetFullPath(q);
        }
        return Path.TrimEndingDirectorySeparator(p);
    }

    /// <summary>For the tests, on their own thread only: the administrative-share spelling is left as given, so a path through a share of
    /// this computer is caught by the identity check alone (as a share the spelling can't tell is this computer's would be).</summary>
    [ThreadStatic] internal static bool SharesUnmappedForTest;

    /// <summary>For the tests, on their own thread only: when set, an administrative-share spelling is read as this drive instead of its
    /// own, as a share the user made and named like one (<c>Q$</c>, rooted in some folder) would be misread.</summary>
    [ThreadStatic] internal static char AdminShareDriveForTest;

    /// <summary><c>\\host\X$\rest</c> as <c>X:\rest</c> when the host is this computer. Only a spelling: a share the user made may be
    /// named so too, so <see cref="Refusal"/> also checks the path as given.</summary>
    static string AdminShare(string p)
    {
        if (SharesUnmappedForTest) return p;
        if (!p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
            return p;
        int hostEnd = p.IndexOf('\\', 2);
        if (hostEnd < 0 || hostEnd + 3 > p.Length) return p;
        string host = p[2..hostEnd];
        char drive = p[hostEnd + 1];
        if (!IsThisComputer(host) || !char.IsAsciiLetter(drive) || p[hostEnd + 2] != '$') return p;
        string rest = hostEnd + 3 < p.Length ? p[(hostEnd + 3)..] : "";
        if (rest.Length > 0 && rest[0] != '\\') return p;
        return $"{char.ToUpperInvariant(AdminShareDriveForTest != '\0' ? AdminShareDriveForTest : drive)}:\\" + rest.TrimStart('\\');
    }

    /// <summary>Whether a share's host names this computer: localhost, its names, any loopback address (127.x.x.x, ::1, in any form
    /// .NET reads as an address), any address of its network interfaces, and those addresses as <c>*.ipv6-literal.net</c> names.</summary>
    internal static bool IsThisComputer(string host)
    {
        string h = host;
        int at = h.IndexOf('@');                                              // \\host@SSL@port\ forms
        if (at >= 0) h = h[..at];
        h = h.TrimEnd('.');
        if (h.EndsWith(".ipv6-literal.net", StringComparison.OrdinalIgnoreCase))
            h = h[..^".ipv6-literal.net".Length].Replace('-', ':').Replace('s', '%').Replace('S', '%');
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        if (h.Length == 0) return false;
        if (LocalNames.Value.Contains(h)) return true;
        if (!IPAddress.TryParse(h, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        return LocalAddresses.Value.Contains(new IPAddress(ip.GetAddressBytes()));       // without its scope ID
    }

    static readonly Lazy<HashSet<string>> LocalNames = new(() =>
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", Environment.MachineName };
        try
        {
            s.Add(Dns.GetHostName());
            var g = IPGlobalProperties.GetIPGlobalProperties();
            if (g.HostName.Length > 0) s.Add(g.HostName);
            if (g.HostName.Length > 0 && g.DomainName.Length > 0) s.Add($"{g.HostName}.{g.DomainName}");
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException or PlatformNotSupportedException) { }
        return s;
    });

    static readonly Lazy<HashSet<IPAddress>> LocalAddresses = new(() =>
    {
        var s = new HashSet<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = new IPAddress(u.Address.GetAddressBytes());              // without its scope ID
                    s.Add(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a);
                }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        return s;
    });

    static bool Under(string path, string dir) =>
        path.Equals(dir, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(dir.EndsWith('\\') ? dir : dir + '\\', StringComparison.OrdinalIgnoreCase);

    // ---- Windows ----

    const uint FileReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
    const int FileIdInfo = 0x12;

    /// <summary>A canonical path as Win32 opens it past 260 characters: <c>\\?\C:\...</c> or <c>\\?\UNC\host\share\...</c>, a share's
    /// root with its separator.</summary>
    internal static string Extended(string full)
    {
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (!full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\" + full;
        string unc = @"\\?\UNC\" + full[2..];
        return Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root).Equals(full, StringComparison.OrdinalIgnoreCase) ? unc + @"\" : unc;
    }

    /// <summary>A handle to an existing file or folder for its attributes only (backup semantics, as a folder needs), opened by its
    /// <c>\\?\</c> path; null with the Win32 error when it can't be opened.</summary>
    static SafeFileHandle? Open(string path, out int error)
    {
        var h = CreateFileW(Extended(path), FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        error = h.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    static string? FinalPath(SafeFileHandle h)
    {
        var buf = new char[1024];
        for (int round = 0; round < 3; round++)
        {
            uint n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Length, 0);
            if (n == 0) return null;
            if (n < buf.Length) return new string(buf, 0, (int)n);
            buf = new char[n + 1];
        }
        return null;
    }

    /// <summary>The identity of an existing file or folder (<see cref="Identity(SafeFileHandle)"/>); null when it can't be opened.</summary>
    internal static (ulong Volume, UInt128 File)? Identity(string path)
    {
        using var h = Open(path, out _);
        return h is null ? null : Identity(h);
    }

    /// <summary>The volume serial number and 128-bit file ID (FILE_ID_INFO); where the file system or the share gives none, the 32-bit
    /// serial and 64-bit file index of GetFileInformationByHandle; null where neither is given (a file ID of zero is none).</summary>
    static (ulong Volume, UInt128 File)? Identity(SafeFileHandle h)
    {
        var info = new byte[24];
        if (GetFileInformationByHandleEx(h, FileIdInfo, info, (uint)info.Length))
        {
            var file = new UInt128(BitConverter.ToUInt64(info, 16), BitConverter.ToUInt64(info, 8));
            if (file != 0) return (BitConverter.ToUInt64(info, 0), file);
        }
        // BY_HANDLE_FILE_INFORMATION: the serial at 28, the index's high and low halves at 44 and 48
        var b = new byte[52];
        if (!GetFileInformationByHandle(h, b)) return null;
        ulong index = (ulong)BitConverter.ToUInt32(b, 44) << 32 | BitConverter.ToUInt32(b, 48);
        return index == 0 ? null : (BitConverter.ToUInt32(b, 28), index);
    }

    /// <summary>A handle, for its attributes only, to the file or folder of a file ID on the volume <paramref name="volume"/> is open
    /// on; null when it can't be opened.</summary>
    static SafeFileHandle? OpenById(SafeFileHandle volume, UInt128 file)
    {
        // FILE_ID_DESCRIPTOR: its size, the ID's type (0 a 64-bit file ID, 2 a 128-bit one), the ID
        var id = new byte[24];
        BitConverter.TryWriteBytes(id, 24);
        BitConverter.TryWriteBytes(id.AsSpan(4), file >> 64 == 0 ? 0 : 2);
        BitConverter.TryWriteBytes(id.AsSpan(8), (ulong)file);
        BitConverter.TryWriteBytes(id.AsSpan(16), (ulong)(file >> 64));
        var h = OpenFileById(volume, id, FileReadAttributes, ShareAll, 0, BackupSemantics);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle OpenFileById(SafeFileHandle volume, byte[] id, uint access, uint share, nint security, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, [Out] byte[] info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle h, [Out] byte[] info);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindFirstVolumeW([Out] char[] name, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextVolumeW(nint find, [Out] char[] name, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindVolumeClose(nint find);
}
