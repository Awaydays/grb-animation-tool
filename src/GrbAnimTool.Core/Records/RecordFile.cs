using System.Buffers.Binary;
using System.Text;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Records;

/// <summary>A record file can't be made or written as asked; nothing was written, unless <see cref="FilesWritten"/>. The message says why.</summary>
public sealed class RecordFileException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The package's files were all written and read back, but an old file they replaced, moved aside, couldn't be deleted; the
    /// message names it. Not a refusal: the new files are in place.</summary>
    public bool FilesWritten { get; init; }
}

/// <summary>The record files players install by hand. A datafile unpacked into a folder holds one file per record; a file dropped into that
/// folder in place of the original of the same name replaces that record when the folder is packed again. The rules, as facts:
/// <list type="bullet">
/// <item>The folder is named <c>index_-_name.data</c> by the datafile's index in the forge and its name (the animation datafile is
/// <c>46_-_MIS_Y2E4_Katya_Maksimov.data</c> on today's files, <c>24_-_</c> on the 2023 ones). The files don't depend on it, so they ship
/// loose.</item>
/// <item>A file is named <c>index_-_name.Animation</c> for a clip: the record's index in the datafile and its name, both from the user's own
/// datafile. A file of another name sits beside the original instead of replacing it.</item>
/// <item>Its content is the record from the byte after its name: the head and the body, with no type, length or name.</item>
/// <item>The head rule: the first byte is 0 or 1. A 1 gives a head of 8 + 12k bytes, k the i32 at offset 4; a 0 gives a head of 1 byte.
/// The file is the head plus the record's length field, and the record's ID and type stand right after the head.</item>
/// <item>A name is given back unchanged only when it is not empty, holds no byte below 0x20 or above 0x7E nor any of <c>" * / : &lt; &gt; ? \ | } ~</c>,
/// no dot, no " (n)" for n below 50, and is not "enc", "unnamed" or the ID in 16 hex digits.</item>
/// </list></summary>
public static class RecordFile
{
    /// <summary>The extension of a clip's record file: the name of the type 0x0FA3067F, whose CRC-32 it is.</summary>
    public const string ClipExtension = ".Animation";

    /// <summary>The record file's content: <paramref name="record"/> (a whole record, as a datafile holds it) from the byte after its name.
    /// Refuses a record that breaks the head rule.</summary>
    public static byte[] Content(ReadOnlySpan<byte> record)
    {
        if (HeadRuleBroken(record) is { } why) throw new RecordFileException(why);
        int nl = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        return record[(12 + nl)..].ToArray();
    }

    /// <summary>Why <paramref name="record"/> breaks the head rule, or null when it holds.</summary>
    public static string? HeadRuleBroken(ReadOnlySpan<byte> record)
    {
        if (record.Length < 12) return $"a record of {record.Length} B has no type, length and name";
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(record);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        int nl = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        if (nl < 0 || nl > record.Length - 12) return $"the name's length {nl} doesn't fit a record of {record.Length} B";
        var file = record[(12 + nl)..];
        if (file.Length == 0 || file[0] > 1) return $"the byte after the name is {(file.Length == 0 ? "missing" : file[0].ToString())}, not 0 or 1";
        if (file[0] == 1 && file.Length < 8) return "the byte after the name is 1, but the head is cut short";
        long h = file[0] == 1 ? 8 + 12L * BinaryPrimitives.ReadInt32LittleEndian(file[4..]) : 1;
        if (h < 1 || h + 12 > file.Length) return $"a head of {h} B leaves no ID and type in the {file.Length} B after the name";
        if (file.Length != h + len) return $"the {file.Length} B after the name aren't the head's {h} B plus the length field's {len}";
        if (BinaryPrimitives.ReadUInt32LittleEndian(file[((int)h + 8)..]) != type) return "the type after the ID isn't the record's type";
        return null;
    }

    /// <summary>The ID a record file holds: the u64 right after its head; null when the head rule can't place it.</summary>
    public static ulong? IdOf(ReadOnlySpan<byte> file)
    {
        if (file.Length == 0 || file[0] > 1 || (file[0] == 1 && file.Length < 8)) return null;
        long h = file[0] == 1 ? 8 + 12L * BinaryPrimitives.ReadInt32LittleEndian(file[4..]) : 1;
        return h >= 1 && h + 8 <= file.Length ? BinaryPrimitives.ReadUInt64LittleEndian(file[(int)h..]) : null;
    }

    /// <summary>The whole record a record file stands for, as a datafile holds it: type, length, name length, name, then the file. The length is
    /// the file less its head, by the head rule. Refuses a file the head rule doesn't fit.</summary>
    public static byte[] ToRecord(ReadOnlySpan<byte> file, string name, uint type = ClipRecord.Type)
    {
        if (file.Length == 0 || file[0] > 1 || (file[0] == 1 && file.Length < 8)) throw new RecordFileException("the file doesn't start with a head the head rule reads");
        long h = file[0] == 1 ? 8 + 12L * BinaryPrimitives.ReadInt32LittleEndian(file[4..]) : 1;
        if (h < 1 || h + 12 > file.Length) throw new RecordFileException($"a head of {h} B leaves no ID and type in a file of {file.Length} B");
        var nb = Encoding.ASCII.GetBytes(name);
        var r = new byte[12 + nb.Length + file.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(r, type);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), (uint)(file.Length - h));
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(8), nb.Length);
        nb.CopyTo(r, 12);
        file.CopyTo(r.AsSpan(12 + nb.Length));
        return r;
    }

    static bool Illegal(byte x) => x < 0x20 || x >= 0x7F || "\"*/:<>?\\|}~".IndexOf((char)x) >= 0;

    /// <summary>Why a record of this name and ID would not get its own name back through a file, or null.</summary>
    public static string? NameRuleBroken(string name, ulong id)
    {
        if (name.Length == 0) return "the name is empty";
        var bytes = Encoding.UTF8.GetBytes(name);
        var bad = bytes.Where(Illegal).Distinct().ToArray();
        if (bad.Length > 0) return $"the name holds characters a file name loses: {string.Join(" ", bad.Select(b => $"0x{b:X2}"))}";
        if (name.Contains('.')) return "the name holds a dot";
        for (int i = 0; i < 50; i++) if (name.Contains($" ({i})", StringComparison.Ordinal)) return $"the name holds \" ({i})\"";
        if (name.Equals("enc", StringComparison.OrdinalIgnoreCase)) return "the name is \"enc\"";
        if (name.Equals("unnamed", StringComparison.OrdinalIgnoreCase)) return "the name is \"unnamed\"";
        if (name.Equals(id.ToString("X16"), StringComparison.OrdinalIgnoreCase)) return "the name is the record's ID in hex";
        return null;
    }

    /// <summary>A clip's record file name: <c>index_-_name.Animation</c>. Refuses a negative index or a name the rules would change.</summary>
    public static string ClipFileName(int index, string name, ulong id)
    {
        if (index < 0) throw new RecordFileException($"the index {index} is below 0");
        if (NameRuleBroken(name, id) is { } why) throw new RecordFileException($"{name}: {why}, so a record file wouldn't keep it");
        return $"{index}_-_{name}{ClipExtension}";
    }

    /// <summary>The folder a datafile's record files go into once the player has unpacked it: <c>index_-_name.data</c>.</summary>
    public static string DatafileFolderName(int datafileIndex, string datafileName) => $"{datafileIndex}_-_{datafileName}.data";

    /// <summary>The animation datafile's index in today's patch forge, and in the 2023 one.</summary>
    public const int AnimationDatafileIndexToday = 46, AnimationDatafileIndex2023 = 24;

    /// <summary>The folders a package named from <paramref name="namedFrom"/> may go into, each once: the folder under today's index and
    /// under the 2023 one, each saying which game files it is for; the folder named from is marked where it is one of them, and listed first
    /// where it is neither. When <paramref name="namedFrom"/> isn't of the form <c>index_-_name.data</c>, it is the only folder.</summary>
    public static List<(string Folder, string Where)> DatafileFolders(string namedFrom)
    {
        const string From = "the game folder these files were named from";
        if (DatafileNameOf(namedFrom) is not { } name) return [(namedFrom, From)];
        string today = DatafileFolderName(AnimationDatafileIndexToday, name), old = DatafileFolderName(AnimationDatafileIndex2023, name);
        var r = new List<(string, string)>();
        if (namedFrom != today && namedFrom != old) r.Add((namedFrom, From));
        r.Add((today, "today's game files" + (namedFrom == today ? $" ({From})" : "")));
        r.Add((old, "the 2023 game files" + (namedFrom == old ? $" ({From})" : "")));
        return r;
    }

    /// <summary>The datafile's name in a folder name <c>index_-_name.data</c>, or null when it isn't of that form.</summary>
    public static string? DatafileNameOf(string folderName)
    {
        int sep = folderName.IndexOf("_-_", StringComparison.Ordinal);
        if (sep <= 0 || !folderName.EndsWith(".data", StringComparison.Ordinal) || !folderName.AsSpan(0, sep).ToString().All(char.IsAsciiDigit)) return null;
        string name = folderName[(sep + 3)..^5];
        return name.Length > 0 ? name : null;
    }
}

/// <summary>One clip to write as a record file: its index and name in the user's datafile, its ID, and its whole record.</summary>
public sealed record RecordFileItem(int Index, string Name, ulong Id, byte[] Record);

/// <summary>A record file written: its name, full path, length and SHA-256.</summary>
public sealed record WrittenRecordFile(string FileName, string Path, long Length, string Sha256);

/// <summary>What was written: loose record files in <see cref="OutputFolder"/>, and the folder name of the datafile they go into.</summary>
public sealed record RecordPackage(string OutputFolder, string DatafileFolderName, IReadOnlyList<WrittenRecordFile> Files)
{
    /// <summary>The extra files written in the same package (check reports, say), in the order given.</summary>
    public IReadOnlyList<WrittenRecordFile> ExtraFiles { get; init; } = [];
}

/// <summary>Writes clips as record files into an output folder the caller gives. It never writes into the game folder or a folder holding the
/// forges read: such an output folder is refused before anything is written. Every file is checked first, then the files are written whole
/// or not at all: each file goes to a temporary name of its own in the folder, and only once all are written are they moved into place; any
/// refusal or failure removes what was written and puts back what it would have replaced, so the folder is left as it was.</summary>
public static class RecordFileWriter
{
    /// <summary>Writes <paramref name="clips"/> as record files named from <paramref name="install"/>'s own animation datafile. Each clip's
    /// index, name and ID must be the datafile's (a replacement keeps its place). Refuses an output folder inside the install's game folder or
    /// the folder of either forge it read.</summary>
    public static RecordPackage Write(GameInstall install, string outputFolder, IEnumerable<(ClipEntry Clip, byte[] Record)> clips, bool overwrite = false) =>
        Write(install, outputFolder, clips, [], overwrite);

    /// <summary>Writes <paramref name="items"/> into <paramref name="outputFolder"/> (made when missing), whole or not at all, refusing an
    /// output folder that is, or lies inside, any of <paramref name="protectedFolders"/>, or whose path holds a folder name ending in a space
    /// or a dot. A file already there is refused unless <paramref name="overwrite"/>; a folder of a file's name always is.</summary>
    public static RecordPackage Write(string outputFolder, string datafileFolderName, IEnumerable<string> protectedFolders, IReadOnlyList<RecordFileItem> items, bool overwrite = false) =>
        Write(outputFolder, datafileFolderName, protectedFolders, items, [], overwrite);

    /// <summary>As <see cref="Write(GameInstall, string, IEnumerable{ValueTuple{ClipEntry, byte[]}}, bool)"/>, with
    /// <paramref name="extraFiles"/> (a check report, say) written in the same package: guarded, and whole or not at all with the record
    /// files.</summary>
    public static RecordPackage Write(GameInstall install, string outputFolder, IEnumerable<(ClipEntry Clip, byte[] Record)> clips,
        IReadOnlyList<(string Name, byte[] Content)> extraFiles, bool overwrite = false)
    {
        var items = new List<RecordFileItem>();
        foreach (var (c, rec) in clips) items.Add(ItemOf(install, c, rec));
        return Write(outputFolder, install.RecordFolderName, Export.ClipExport.ProtectedFolders(install), items, extraFiles, overwrite);
    }

    /// <summary>The number a row's record file is named with: a forge row's index in the datafile; a row of the unpacked folder's own
    /// file the number its file has (1, as the players' 1_-_ files, when it has none).</summary>
    public static int FileIndexOf(ClipEntry c) => !c.FromFile ? c.Index : c.FileNumber is int n and >= 0 ? n : 1;

    /// <summary>The record file to write for <paramref name="record"/> made for <paramref name="c"/>: a forge row's must be the install's
    /// own record of its index, name and ID (a replacement keeps its place); a file row's is named as its file is.</summary>
    public static RecordFileItem ItemOf(GameInstall install, ClipEntry c, byte[] record)
    {
        if (!c.FromFile && (c.Index < 0 || c.Index >= install.Records.Count || install.Records[c.Index].Index != c.Index || install.Records[c.Index].Id != c.Id || install.Records[c.Index].Name != c.Name))
            throw new RecordFileException($"{c.Name} 0x{c.Id:X16} isn't record {c.Index} of the game folder's animation datafile");
        return new RecordFileItem(FileIndexOf(c), c.Name, c.Id, record);
    }

    /// <summary>Writes plain files (no record file) into <paramref name="folder"/> (made when missing) under the same
    /// guard and the same whole-or-nothing write as a package: a check report kept in a folder apart from the record files, say.</summary>
    public static IReadOnlyList<WrittenRecordFile> WriteFiles(string folder, IEnumerable<string> protectedFolders, IReadOnlyList<(string Name, byte[] Content)> files, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new RecordFileException("no output folder was given");
        var protect = protectedFolders.ToList();
        GuardOutput(folder, protect);
        if (FolderNameRefused(folder) is { } why) throw new RecordFileException(why);
        if (files.Count == 0) throw new RecordFileException("no file to write");
        string outDir = Real(folder);
        var list = CheckedExtras(files, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var (fname, _) in list)
        {
            string target = Path.Combine(outDir, fname);
            if (Directory.Exists(target)) throw new RecordFileException($"{target} is a folder, so {fname} can't be written there. Nothing was written.");
            if (!overwrite && File.Exists(target)) throw new RecordFileException($"{target} exists already. Nothing was written.");
        }
        return Commit(outDir, list, overwrite, protect, "files");
    }

    /// <summary>Why <paramref name="folder"/> is refused as an output folder for these protected folders (as <see cref="Write(string, string,
    /// IEnumerable{string}, IReadOnlyList{RecordFileItem}, bool)"/> would refuse it), or null: for a caller to check a second folder before
    /// it writes anything.</summary>
    public static string? OutputRefusal(string folder, IEnumerable<string> protectedFolders)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "no output folder was given";
        if (IO.FolderGuard.Refusal(folder, protectedFolders) is { } why)
            return $"The output folder {folder} is refused: {why}. Files are never written into the game's folders: pick a folder of your own.";
        return UnpackedRefusal(folder) ?? BuildRefusal(folder) ?? FolderNameRefused(folder);
    }

    /// <summary>Extra files' names checked: a plain file name (no folder part, no character Windows refuses, not ending in a space or a
    /// dot), not given twice, and not a name the package writes itself.</summary>
    static List<(string Name, byte[] Content)> CheckedExtras(IReadOnlyList<(string Name, byte[] Content)> extras, HashSet<string> names)
    {
        var bad = Path.GetInvalidFileNameChars();
        foreach (var (n, _) in extras)
        {
            if (string.IsNullOrWhiteSpace(n) || n.IndexOfAny(bad) >= 0 || n.IndexOfAny(['/', '\\', ':']) >= 0 || n.EndsWith(' ') || n.EndsWith('.') || n is "." or "..")
                throw new RecordFileException($"\"{n}\" isn't a plain file name. Nothing was written.");
            if (!names.Add(n)) throw new RecordFileException($"{n} is given twice. Nothing was written.");
        }
        return extras.Select(e => (e.Name, e.Content)).ToList();
    }

    /// <summary>As <see cref="Write(string, string, IEnumerable{string}, IReadOnlyList{RecordFileItem}, bool)"/>, with
    /// <paramref name="extraFiles"/> written in the same package, whole or not at all with the record files. A read-only file replaced
    /// with <paramref name="overwrite"/> is replaced like any other; its old copy is never left behind.</summary>
    public static RecordPackage Write(string outputFolder, string datafileFolderName, IEnumerable<string> protectedFolders, IReadOnlyList<RecordFileItem> items,
        IReadOnlyList<(string Name, byte[] Content)> extraFiles, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(outputFolder)) throw new RecordFileException("no output folder was given");
        var protect = protectedFolders.ToList();
        GuardOutput(outputFolder, protect);
        string outDir = Real(outputFolder);
        if (items.Count == 0) throw new RecordFileException("no record to write");
        if (FolderNameRefused(outputFolder) is { } why) throw new RecordFileException(why);
        var files = new List<(string Name, byte[] Content)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            string fname = RecordFile.ClipFileName(it.Index, it.Name, it.Id);
            if (!names.Add(fname)) throw new RecordFileException($"{fname} is given twice");
            if (it.Record.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(it.Record) != ClipRecord.Type)
                throw new RecordFileException($"{fname}: the record isn't a clip record");
            int nl = BinaryPrimitives.ReadInt32LittleEndian(it.Record.AsSpan(8));
            if (nl < 0 || nl > it.Record.Length - 12 || Encoding.ASCII.GetString(it.Record, 12, nl) != it.Name)
                throw new RecordFileException($"{fname}: the record's own name isn't {it.Name}");
            var content = RecordFile.Content(it.Record);
            if (RecordFile.IdOf(content) != it.Id) throw new RecordFileException($"{fname}: the record's ID isn't 0x{it.Id:X16}");
            files.Add((fname, content));
        }
        files.AddRange(CheckedExtras(extraFiles, names));
        foreach (var (fname, _) in files)
        {
            string target = Path.Combine(outDir, fname);
            if (Directory.Exists(target)) throw new RecordFileException($"{target} is a folder, so {fname} can't be written there. Nothing was written.");
            if (!overwrite && File.Exists(target)) throw new RecordFileException($"{target} exists already. Nothing was written.");
        }
        var written = Commit(outDir, files, overwrite, protect);
        return new RecordPackage(outDir, datafileFolderName, written.Take(items.Count).ToList()) { ExtraFiles = written.Skip(items.Count).ToList() };
    }

    /// <summary>Why <paramref name="folder"/> is refused for a folder name Windows keeps but handles badly (one ending in a space or a dot,
    /// which Explorer can't open or delete), or null. The path is read as given, before any normalising drops such an ending.</summary>
    static string? FolderNameRefused(string folder)
    {
        foreach (var part in folder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            if (part is not "." and not ".." && (part.EndsWith(' ') || part.EndsWith('.')))
                return $"The output folder {folder} holds a folder name ending in a space or a dot (\"{part}\"), which Windows handles badly: pick another name. Nothing was written.";
        return null;
    }

    /// <summary>Writes the package whole or not at all: every file to a temporary name of its own in <paramref name="outDir"/> (read back),
    /// then each moved into place, a file it replaces moved aside first. Only when all are in place and read back are the files moved aside
    /// deleted. On any failure, what was moved into place is removed, what was moved aside goes back, the temporary files are deleted and the
    /// folders made here are removed, and a <see cref="RecordFileException"/> says what happened, naming the files as <paramref name="what"/>
    /// ("record files" for a package, "files" for plain files).</summary>
    static List<WrittenRecordFile> Commit(string outDir, List<(string Name, byte[] Content)> files, bool overwrite, List<string> protect, string what = "record files")
    {
        string tag = Guid.NewGuid().ToString("N")[..12];
        var made = new List<string>();   // the folders made here, outermost first
        for (string? d = outDir; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) made.Insert(0, d);
        var temps = files.Select(f => Path.Combine(outDir, $"{f.Name}.{Guid.NewGuid():N}.part")).ToList();
        var aside = new List<(string Target, string Aside)>();
        var placed = new List<string>();
        var readOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // the old files that were read-only, so a rollback restores it
        bool committed = false;   // every new file in place and read back: from here nothing is undone
        try
        {
            Directory.CreateDirectory(outDir);
            GuardOutput(outDir, protect);        // again, now that the folder exists: where it really leads
            for (int i = 0; i < files.Count; i++)
            {
                using (var fs = new FileStream(temps[i], FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.Write(files[i].Content);
                if (!File.ReadAllBytes(temps[i]).AsSpan().SequenceEqual(files[i].Content)) throw new RecordFileException($"{temps[i]} doesn't read back as written");
            }
            GuardOutput(outDir, protect);        // and right before the files are moved into place
            for (int i = 0; i < files.Count; i++)
            {
                string target = Path.Combine(outDir, files[i].Name);
                if (Directory.Exists(target)) throw new RecordFileException($"{target} is a folder, so {files[i].Name} can't be written there");
                if (File.Exists(target))
                {
                    if (!overwrite) throw new RecordFileException($"{target} exists already");
                    string old = $"{target}.{tag}.old";
                    var attrs = File.GetAttributes(target);
                    File.Move(target, old);
                    aside.Add((target, old));
                    // a read-only old file can be moved but not deleted: its moved-aside copy is made writable, so it can be removed
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        readOnly.Add(target);
                        File.SetAttributes(old, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                File.Move(temps[i], target);
                placed.Add(target);
            }
            var written = new List<WrittenRecordFile>();
            for (int i = 0; i < files.Count; i++)
            {
                var back = File.ReadAllBytes(placed[i]);
                if (!back.AsSpan().SequenceEqual(files[i].Content)) throw new RecordFileException($"{placed[i]} doesn't read back as written");
                written.Add(new WrittenRecordFile(files[i].Name, placed[i], back.Length, Sha256Hex.Of(back)));
            }
            committed = true;
            var stuck = new List<string>();
            foreach (var (_, old) in aside)
            {
                try
                {
                    var at = File.GetAttributes(old);
                    if ((at & FileAttributes.ReadOnly) != 0) File.SetAttributes(old, at & ~FileAttributes.ReadOnly);
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stuck.Add(old); }
            }
            // never a success with an old copy left beside the new files, where it would ship with the folder
            if (stuck.Count > 0)
                throw new RecordFileException($"The {what} were written into {outDir} and read back, but the old files they replaced, moved aside as "
                    + $"{string.Join(", ", stuck)}, couldn't be deleted. Delete them before you share the folder.") { FilesWritten = true };
            return written;
        }
        catch (Exception ex) when (!committed)
        {
            var left = new List<string>();
            void Try(Action a, string what)
            {
                try { a(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(what); }
            }
            foreach (var p in Enumerable.Reverse(placed)) Try(() => File.Delete(p), p);
            foreach (var (target, old) in Enumerable.Reverse(aside))
                Try(() =>
                {
                    File.Move(old, target);
                    if (readOnly.Contains(target)) File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
                }, $"{target} (kept as {old})");
            foreach (var t in temps) Try(() => { if (File.Exists(t)) File.Delete(t); }, t);
            foreach (var d in Enumerable.Reverse(made)) Try(() => { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }, d);
            // an error that isn't a file error is undone the same way and rethrown as it is
            if (ex is not (IOException or UnauthorizedAccessException or RecordFileException)) throw;
            string why = ex is RecordFileException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            string state = left.Count == 0 ? "Nothing was left there: any file it would have replaced is as it was."
                : $"These couldn't be put back as they were: {string.Join("; ", left)}.";
            throw new RecordFileException($"The {what} couldn't be written into {outDir} ({why}). {state}", ex);
        }
    }

    /// <summary>A folder's full path as given. Where it really leads (links, <c>subst</c> drives, shares of this computer) is the
    /// guard's to find (<see cref="GuardOutput"/>): following links here by their stored targets goes wrong through a share.</summary>
    static string Real(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>Refuses an output folder that is, or lies inside, one of <paramref name="protect"/>, is of a form the guard doesn't
    /// accept, or holds a folder that can't be checked (see <see cref="IO.FolderGuard.Refusal"/>), looks like an unpacked datafile, or is,
    /// or lies inside, a package or a local undo folder of this tool (<see cref="BuildRefusal"/>).</summary>
    static void GuardOutput(string outputFolder, List<string> protect)
    {
        if (IO.FolderGuard.Refusal(outputFolder, protect) is { } why)
            throw new RecordFileException($"The output folder {outputFolder} is refused: {why}. Record files are never written into the game's folders: pick a folder of your own.");
        if (UnpackedRefusal(outputFolder) is { } unpacked) throw new RecordFileException(unpacked);
        if (BuildRefusal(outputFolder) is { } build) throw new RecordFileException(build);
    }

    /// <summary>The staging folder a package build is writing its records into on this flow of work (set by the build around its record
    /// writer, null otherwise): the one folder carrying this tool's marker the writer writes into.</summary>
    internal static readonly AsyncLocal<string?> BuildingIn = new();

    /// <summary>The refusal of an output folder that is, or lies inside, a package or a local undo folder of this tool, by its spelling or
    /// by where it leads (see <see cref="IO.FolderGuard.BuildFolderRefusal"/>), or null: a file written there would be shared with that
    /// folder, and a record file written into a local undo folder would be taken for one it holds. The build's own staging folder
    /// (<see cref="BuildingIn"/>) is allowed.</summary>
    static string? BuildRefusal(string folder) =>
        IO.FolderGuard.BuildFolderRefusal(folder, BuildingIn.Value) is { } why
            ? $"The output folder {folder} is refused: {why}, {IO.FolderGuard.BuildFolderAdvice} Nothing was written." : null;

    /// <summary>The refusal of an output folder that looks like a datafile unpacked from a forge (see
    /// <see cref="IO.FolderGuard.UnpackedDatafileRefusal"/>), or null: a file written there would be packed with the game's records, and
    /// a record file of an original's name would replace it with no copy kept.</summary>
    static string? UnpackedRefusal(string folder) =>
        IO.FolderGuard.UnpackedDatafileRefusal(folder) is { } why ? $"The output folder {folder} is refused: {why}. Nothing was written." : null;
}
