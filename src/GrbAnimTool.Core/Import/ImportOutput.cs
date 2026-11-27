using System.Text;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.Core.Import;

/// <summary>What <see cref="ImportOutput.Write"/> wrote: the record files and each check report.</summary>
public sealed record ImportFiles(RecordPackage Package, IReadOnlyList<string> Reports);

/// <summary>Writes imports' rebuilt records as record files through <see cref="RecordFileWriter"/>'s package: the record files and each
/// import's check report, guarded against every folder of the game's and written whole or not at all.</summary>
public static class ImportOutput
{
    /// <summary>The report's file name for a record file: <c>index_-_name.checks.txt</c>.</summary>
    public static string ReportFileName(ClipEntry c) => $"{RecordFileWriter.FileIndexOf(c)}_-_{c.Name}.checks.txt";

    const string NothingWritten = "Nothing was written.";

    /// <summary>Writes <paramref name="results"/> into <paramref name="folder"/>: the record files and each report, as one package. When
    /// <paramref name="reportFolder"/> names another folder, the reports go there instead: that folder is checked by the same guard, and
    /// each report's place in it (a folder of its name, or a file already there without <paramref name="overwrite"/>), before anything is
    /// written; the reports are then written whole or not at all after the record files. A file already there is refused unless
    /// <paramref name="overwrite"/>, reports included. Should the reports still fail after the record files were written, or an old copy a
    /// written file replaced not be deleted, the <see cref="RecordFileException"/> says which and carries
    /// <see cref="RecordFileException.FilesWritten"/>.</summary>
    public static ImportFiles Write(GameInstall g, string folder, IReadOnlyList<ImportResult> results, bool overwrite = false, string? reportFolder = null) =>
        Write(g, folder, results, overwrite, reportFolder, []);

    /// <summary>As <see cref="Write(GameInstall, string, IReadOnlyList{ImportResult}, bool, string?)"/>, with <paramref name="alsoProtected"/>
    /// guarded as well as the install's own folders: no file is written into any of them, nor into a folder inside one.</summary>
    public static ImportFiles Write(GameInstall g, string folder, IReadOnlyList<ImportResult> results, bool overwrite, string? reportFolder, IEnumerable<string> alsoProtected)
    {
        var protect = Export.ClipExport.ProtectedFolders(g).Concat(alsoProtected).ToList();
        var reports = results.Select(r => (ReportFileName(r.Slot), new UTF8Encoding(false).GetBytes(r.ReportText()))).ToList();
        var items = new List<RecordFileItem>();
        foreach (var r in results) items.Add(RecordFileWriter.ItemOf(g, r.Slot, r.Record));
        bool apart = reportFolder is not null && !SameFolder(reportFolder, folder);
        if (apart)
        {
            if (RecordFileWriter.OutputRefusal(reportFolder!, protect) is { } why)
                throw new RecordFileException($"Report folder: {why}" + (why.EndsWith(NothingWritten, StringComparison.Ordinal) ? "" : " " + NothingWritten));
            if (ReportTargetRefusal(reportFolder!, reports.Select(x => x.Item1), overwrite) is { } taken)
                throw new RecordFileException($"Report folder: {taken} {NothingWritten}");
        }
        // the folder the files go into: the one datafile's they are all of (the animation datafile's for most)
        var folders = results.Select(r => RecordFileWriter.FolderOf(g, r.Slot)).Distinct().ToList();
        var pkg = RecordFileWriter.Write(folder, folders.Count == 1 ? folders[0] : string.Join(", ", folders), protect, items, apart ? [] : reports, overwrite);
        if (!apart) return new ImportFiles(pkg, pkg.ExtraFiles.Select(f => f.Path).ToList());
        try
        {
            var written = RecordFileWriter.WriteFiles(reportFolder!, protect, reports, overwrite);
            return new ImportFiles(pkg, written.Select(f => f.Path).ToList());
        }
        catch (RecordFileException ex) when (ex.FilesWritten)
        {
            // the reports were written too, but an old copy they replaced is stuck beside them: said as it is
            throw new RecordFileException($"The record files were written into {pkg.OutputFolder}, and the reports: {ex.Message}", ex) { FilesWritten = true };
        }
        catch (RecordFileException ex)
        {
            string inner = ex.Message.EndsWith(" " + NothingWritten, StringComparison.Ordinal) ? ex.Message[..^(NothingWritten.Length + 1)] : ex.Message;
            throw new RecordFileException($"The record files were written into {pkg.OutputFolder}, but the reports weren't: {inner}", ex) { FilesWritten = true };
        }
    }

    /// <summary>Why a report can't go into <paramref name="folder"/> (the folder, or a folder above it, is a file; a folder of the report's
    /// name there, or a file of its name without <paramref name="overwrite"/>), or null: checked before the package is written, so a refusal
    /// leaves nothing behind.</summary>
    static string? ReportTargetRefusal(string folder, IEnumerable<string> names, bool overwrite)
    {
        try
        {
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            // a file where the folder or a folder above it would stand: no folder can be made there
            for (string? d = dir; d is not null; d = Path.GetDirectoryName(d))
                if (File.Exists(d))
                    return d.Equals(dir, StringComparison.OrdinalIgnoreCase) ? $"{dir} is a file, not a folder, so the reports can't be written in it."
                        : $"{d} is a file, so the folder {dir} can't be made below it for the reports.";
            foreach (var n in names)
            {
                string target = Path.Combine(dir, n);
                if (Directory.Exists(target)) return $"{target} is a folder, so {n} can't be written there.";
                if (!overwrite && File.Exists(target)) return $"{target} exists already.";
            }
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return $"{folder} can't be checked ({ex.Message}).";
        }
    }

    static bool SameFolder(string a, string b)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
