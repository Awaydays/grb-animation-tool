using System.Text;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.IO;

namespace GrbAnimTool.Core.Export;

/// <summary>An export can't be written: the clip can't be sampled, the place is refused, or the file system failed. The message is meant
/// for the user.</summary>
public sealed class ExportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What an export wrote, and the re-import control run on the written file.</summary>
public sealed record ExportResult(string GlbPath, string? SidecarPath, RoundTripResult Check);

/// <summary>Exports the install's skeleton or one of its clips: a .glb (<see cref="GlbWriter"/>) and, for a clip, its sidecar
/// (<see cref="ClipSidecar"/>) beside it. Both files are written whole to temporary names and moved into place only then. The .glb is then read
/// back from the disk and compared with the decoder (<see cref="RoundTrip"/>); the result says whether it passed. Nothing is ever written
/// in the game folder or the folder of the forge read.</summary>
public static class ClipExport
{
    /// <summary>A file name for a clip's .glb: its name with any character a file name can't hold replaced by '_'.</summary>
    public static string FileNameFor(string clipName)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(clipName.Length + 4);
        foreach (char ch in clipName) sb.Append(bad.Contains(ch) || ch < 0x20 ? '_' : ch);
        string s = sb.ToString().Trim().TrimEnd('.');
        return (s.Length == 0 ? "clip" : s) + ".glb";
    }

    public const string SkeletonFileName = "GRB_skeleton.glb";

    /// <summary>The sidecar's path for a .glb: the same folder and base name, <see cref="ClipSidecar.Extension"/>.</summary>
    public static string SidecarPathFor(string glbPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(glbPath)) ?? "";
        string name = Path.GetFileName(glbPath);
        if (name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return Path.Combine(dir, name + ClipSidecar.Extension);
    }

    public static ExportResult Skeleton(GameInstall g, string glbPath, ExportFault fault = ExportFault.None)
    {
        Guard(g, glbPath);
        Write(glbPath, GlbWriter.Skeleton(g.Skeleton, fault), null, null, ProtectedFolders(g));
        return new ExportResult(Path.GetFullPath(glbPath), null, Check(g, glbPath, null));
    }

    public static ExportResult Clip(GameInstall g, ClipEntry c, string glbPath, ExportFault fault = ExportFault.None)
    {
        var record = c.Record ?? throw new ExportException($"{c.Name} can't be exported: its record doesn't read ({c.ParseError}).");
        if (!record.WalkedToEnd) throw new ExportException($"{c.Name} can't be exported: {record.Stop}.");
        Guard(g, glbPath);
        string sidecar = SidecarPathFor(glbPath);
        Guard(g, sidecar);
        SampledClip sampled;
        try { sampled = SampledClip.Of(g.Skeleton, record, g.SharedTable); }
        catch (InvalidDataException ex) { throw new ExportException($"{c.Name} can't be exported: {ex.Message}", ex); }
        var side = ClipSidecar.Of(record, g.SharedTable, g.Skeleton, g.Names, sampled, Path.GetFileName(glbPath), c.Index, c.Status);
        Write(glbPath, GlbWriter.Clip(g.Skeleton, sampled, c.Name, fault), sidecar, new UTF8Encoding(false).GetBytes(side.ToJson()), ProtectedFolders(g));
        return new ExportResult(Path.GetFullPath(glbPath), sidecar, Check(g, glbPath, record));
    }

    static RoundTripResult Check(GameInstall g, string glbPath, Clips.ClipRecord? record)
    {
        GltfAnimation f;
        try { f = GltfAnimation.Read(glbPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new RoundTripResult(0, g.Skeleton.Bones.Count, double.NaN, double.NaN, "", "", [$"the written file doesn't read back: {ex.Message}"]);
        }
        return RoundTrip.Compare(g.Skeleton, f, record, g.SharedTable);
    }

    /// <summary>Refuses (with an <see cref="ExportException"/>) a path inside the install's protected folders
    /// (<see cref="ProtectedFolders(GameInstall)"/>).</summary>
    public static void Guard(GameInstall g, string path) => Guard(path, ProtectedFolders(g));

    /// <summary>The folders every writer of the tool refuses for an install: the game folder and the folder of each forge it read (the
    /// patch forge, which <c>--forge</c> may name elsewhere, and the base forge).</summary>
    public static IReadOnlyList<string> ProtectedFolders(GameInstall g) =>
        [g.GameFolder, FolderOf(g.PatchForgePath), FolderOf(g.BaseForgePath)];

    /// <summary>The same folders before an install is opened, from the game folder and the patch forge given with <c>--forge</c>
    /// (either may be null): the base forge is read from the game folder itself.</summary>
    public static IReadOnlyList<string> ProtectedFolders(string? gameFolder, string? patchForge) =>
        [gameFolder ?? "", patchForge is null ? "" : FolderOf(patchForge)];

    static string FolderOf(string file) => Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";

    /// <summary>Refuses (with an <see cref="ExportException"/>) a path inside one of <paramref name="folders"/>, of a form the guard
    /// doesn't accept, or on a folder that can't be checked (see <see cref="FolderGuard"/>), or inside a package or a local undo folder
    /// of this tool, by its spelling or by where it leads (<see cref="FolderGuard.BuildFolderRefusal"/>): for a command that writes a file
    /// with no install open.</summary>
    public static void Guard(string path, IEnumerable<string> folders)
    {
        if (FolderGuard.Refusal(path, folders) is { } why)
            throw new ExportException($"{path} is refused: {why}. The tool never writes in the game's folder or the folders of the forges it reads: choose a folder of your own.");
        // a file written into a datafile's unpacked folder would be packed into the game's datafile with its records
        if (FolderGuard.UnpackedDatafileRefusal(path, file: true) is { } unpacked)
            throw new ExportException($"{path} is refused: {unpacked}.");
        // a file written into a package would be shared with it, and one in a package's folder keeps the package from being built again
        if (FolderGuard.BuildFolderRefusal(path) is { } build)
            throw new ExportException($"{path} is refused: {build}, {FolderGuard.BuildFolderAdvice}");
    }

    /// <summary>Whether <paramref name="path"/> lies in one of <paramref name="folders"/> or below it, by where it really leads, or is
    /// otherwise refused by the guard (see <see cref="FolderGuard"/>), case ignored as on Windows.</summary>
    internal static bool Refused(string path, IEnumerable<string> folders) => FolderGuard.Inside(path, folders);

    /// <summary>Writes one file whole, guarded: the path is checked (<see cref="Guard(string, IEnumerable{string})"/>), its folder made
    /// when <paramref name="makeFolder"/> and checked again, <paramref name="write"/> fills a temporary file of its own name beside it,
    /// and the path is checked once more right before that file is moved over it. Moving a new file over the name, rather than writing
    /// into the old one, means a hard link or a file link standing at the path is replaced, never followed. On any failure the temporary
    /// file is deleted.</summary>
    public static void WriteFile(string path, IEnumerable<string> folders, Action<Stream> write, bool makeFolder = false)
    {
        var protect = folders.ToList();
        Guard(path, protect);
        string full = Path.GetFullPath(path);
        string tmp = Temporary(full);
        try
        {
            if (makeFolder)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                Guard(full, protect);
            }
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(fs);
            Guard(full, protect);
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            Quietly(() => { if (File.Exists(tmp)) File.Delete(tmp); });
            throw;
        }
    }

    /// <summary>Writes the .glb and, when given, its sidecar. Both are written whole to temporary names of their own first; only then
    /// is the sidecar moved into place and the .glb after it. Should the .glb's move fail, the sidecar that was there before is put
    /// back, so a .glb never stands beside a sidecar of another export. With <paramref name="protectedFolders"/>, both paths are checked
    /// again once their folder exists and right before the moves, so a folder turned into a link meanwhile is caught.</summary>
    internal static void Write(string glbPath, byte[] glb, string? sidecarPath, byte[]? sidecar, IReadOnlyList<string>? protectedFolders = null)
    {
        string full = Path.GetFullPath(glbPath);
        string? side = sidecarPath is null ? null : Path.GetFullPath(sidecarPath);
        string tmp = Temporary(full), tmpSide = side is null ? "" : Temporary(side), old = side is null ? "" : Temporary(side);
        bool oldMoved = false, sideMoved = false;
        string at = full;
        void Recheck()
        {
            if (protectedFolders is null) return;
            Guard(full, protectedFolders);
            if (side is not null) Guard(side, protectedFolders);
        }
        // checked before any folder is made, so a refused place gets nothing made in it; the folders made here are removed on failure
        Recheck();
        var made = new List<string>();   // outermost first
        for (string? d = Path.GetDirectoryName(full); d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) made.Insert(0, d);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            Recheck();
            File.WriteAllBytes(tmp, glb);
            if (side is not null)
            {
                at = side;
                File.WriteAllBytes(tmpSide, sidecar!);
            }
            Recheck();
            if (side is not null)
            {
                if (File.Exists(side)) { File.Move(side, old); oldMoved = true; }
                File.Move(tmpSide, side);
                sideMoved = true;
            }
            at = full;
            File.Move(tmp, full, overwrite: true);
        }
        catch (Exception ex)
        {
            if (sideMoved) Quietly(() => File.Delete(side!));
            bool restored = !oldMoved || Quietly(() => File.Move(old, side!));
            foreach (var t in new[] { tmp, tmpSide }) if (t.Length > 0) Quietly(() => { if (File.Exists(t)) File.Delete(t); });
            // the folders this write made, innermost first, each only while empty (a non-recursive delete never reaches through a link)
            foreach (var d in Enumerable.Reverse(made))
                Quietly(() => { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); });
            string kept = restored ? "" : $" The sidecar that was there couldn't be put back: it is kept as {old}; rename it to {Path.GetFileName(side)}.";
            if (ex is ExportException) throw new ExportException(ex.Message + kept, ex);
            throw new ExportException($"{at} couldn't be written: {ex.Message}{kept}", ex);
        }
        if (oldMoved) Quietly(() => File.Delete(old));
    }

    /// <summary>A name beside <paramref name="path"/> that no other file has: its name, a new GUID and ".tmp".</summary>
    static string Temporary(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    /// <summary>Runs a clean-up step; whether it succeeded.</summary>
    static bool Quietly(Action a)
    {
        try { a(); return true; }
        catch (Exception) { return false; }
    }
}
