using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.IO;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.Tests;

/// <summary>The folder guard against a fake protected folder made here (never a real game folder): every spelling of a place inside it
/// is refused, through the export's guard and the record-file writer alike, and places beside it are not. Also the export's paired
/// write of a .glb and its sidecar. Nothing is written in the fake protected folder.</summary>
public sealed class GuardTests : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "guard-" + Guid.NewGuid().ToString("N"));
    readonly string _game, _out;

    public GuardTests()
    {
        _game = Path.Combine(_dir, "Fake Game");
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(_game, "data"));
        Directory.CreateDirectory(_out);
    }

    /// <summary>The links made below the top of the test's folder, removed (the links themselves) before anything else.</summary>
    readonly List<string> _links = [];

    public void Dispose()
    {
        // links first, so the recursive delete never reaches through one
        foreach (var l in Enumerable.Reverse(_links))
            try { if (new DirectoryInfo(l).LinkTarget is not null) Directory.Delete(l); else if (new FileInfo(l).Exists) File.Delete(l); }
            catch (IOException) { }
        foreach (var d in Directory.GetDirectories(_dir))
            if (new DirectoryInfo(d).LinkTarget is not null) Directory.Delete(d);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>The fake folder's drive as an administrative share of this computer, e.g. \\localhost\C$\...</summary>
    string Share(string host, string path) => $@"\\{host}\{path[0]}$\{path[3..]}";

    IEnumerable<string> Inside(string file)
    {
        string p = Path.Combine(_game, file);
        yield return p;
        yield return p.ToUpperInvariant();
        yield return p.Replace('\\', '/');
        yield return Path.Combine(_game, "data", "..", file);
        yield return Path.Combine(_game, "new", "deeper", file);
        yield return @"\\?\" + p;
        yield return @"\\?\" + Path.Combine(_game, "new", file);
        yield return @"\\.\" + p;
        yield return @"\\.\" + _out + @"\x.glb";              // a device path is refused wherever it leads
        yield return Share("localhost", p);
        yield return Share("127.0.0.1", p);
        yield return Share(Environment.MachineName, p);
        yield return @"\\?\UNC\" + Share("localhost", p)[2..];
    }

    static bool RecordWriterRefuses(string folder, string protectedFolder)
    {
        var ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(folder, "anim", [protectedFolder], []));
        return ex.Message.Contains("never written into the game's folders", StringComparison.Ordinal);
    }

    [Fact]
    public void EverySpellingInsideIsRefused()
    {
        foreach (var p in Inside("x.glb"))
        {
            Assert.True(ClipExport.Refused(p, [_game]), p);
            Assert.Throws<ExportException>(() => ClipExport.Guard(p, [_game]));
            // the protected folder given in another spelling too
            Assert.True(ClipExport.Refused(p, [@"\\?\" + _game + @"\"]), p);
            Assert.True(ClipExport.Refused(p, [_game.ToLowerInvariant() + "/"]), p);
        }
        foreach (var p in Inside("sub"))
        {
            Assert.True(RecordWriterRefuses(p, _game), p);
            Assert.True(RecordWriterRefuses(p, @"\\?\" + _game), p);
        }
        // the protected folder itself
        Assert.True(RecordWriterRefuses(_game, _game));
        Assert.True(RecordWriterRefuses(@"\\?\" + _game, _game));
        Assert.True(RecordWriterRefuses(Share("localhost", _game) + @"\", _game));
        // a relative spelling
        string rel = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_game, "data"));
        Assert.True(ClipExport.Refused(Path.Combine(rel, "x.glb"), [_game]));
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));
    }

    /// <summary>The writer's plain-file write (an import's report folder) and its folder check: every spelling inside the fake protected
    /// folder and a junction into it refused with nothing written; a folder of the user's written whole; a file already there refused
    /// without overwrite and left as it was; names that aren't plain file names refused.</summary>
    [Fact]
    public void ThePlainFileWriteIsGuardedAndWhole()
    {
        byte[] text = Encoding.UTF8.GetBytes("a report");
        foreach (var p in Inside("reports"))
        {
            Assert.NotNull(RecordFileWriter.OutputRefusal(p, [_game]));
            Assert.Throws<RecordFileException>(() => RecordFileWriter.WriteFiles(p, [_game], [("a.checks.txt", text)]));
        }
        string j = Path.Combine(_dir, "junction-reports");
        Junction(j, _game);
        Assert.NotNull(RecordFileWriter.OutputRefusal(j, [_game]));
        Assert.Throws<RecordFileException>(() => RecordFileWriter.WriteFiles(Path.Combine(j, "new"), [_game], [("a.checks.txt", text)]));
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));

        string mine = Path.Combine(_out, "reports");
        Assert.Null(RecordFileWriter.OutputRefusal(mine, [_game]));
        var w = RecordFileWriter.WriteFiles(mine, [_game], [("a.checks.txt", text), ("b.checks.txt", text)]);
        Assert.Equal(2, w.Count);
        Assert.Equal(["a.checks.txt", "b.checks.txt"], Directory.GetFiles(mine).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        File.WriteAllText(Path.Combine(mine, "b.checks.txt"), "kept");
        Assert.Throws<RecordFileException>(() => RecordFileWriter.WriteFiles(mine, [_game], [("a.checks.txt", text), ("b.checks.txt", text)]));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(mine, "b.checks.txt")));
        RecordFileWriter.WriteFiles(mine, [_game], [("b.checks.txt", text)], overwrite: true);
        Assert.Equal(text, File.ReadAllBytes(Path.Combine(mine, "b.checks.txt")));
        foreach (var bad in new[] { @"..\x.txt", "sub/x.txt", "x.txt.", "x:y", "" })
            Assert.Throws<RecordFileException>(() => RecordFileWriter.WriteFiles(mine, [_game], [(bad, text)]));
        Assert.Equal(2, Directory.GetFiles(mine).Length);
    }

    [Fact]
    public void PlacesBesideAreAllowed()
    {
        string[] beside = [Path.Combine(_out, "x.glb"), Path.Combine(_dir, "Fake Game2", "x.glb"), Path.Combine(_dir, "Fake Gam", "x.glb"),
            @"\\?\" + Path.Combine(_out, "x.glb"), Share("localhost", Path.Combine(_out, "x.glb"))];
        foreach (var p in beside)
        {
            Assert.False(ClipExport.Refused(p, [_game, ""]), p);
            ClipExport.Guard(p, [_game]);
        }
        Assert.False(ClipExport.Refused(Path.Combine(_game, "x.glb"), [""]));
        // the record writer gets past the guard to its next refusal
        Assert.False(RecordWriterRefuses(_out, _game));
        Assert.False(RecordWriterRefuses(Path.Combine(_out, "new"), _game));
    }

    /// <summary>A junction (no privilege needed) and, where this account may make one, a symbolic link, each leading into the protected
    /// folder or below it: every path through them is refused, existing or not.</summary>
    [Fact]
    public void LinksIntoTheFolderAreRefused()
    {
        string j = Path.Combine(_dir, "junction"), jd = Path.Combine(_dir, "junction-data");
        Junction(j, _game);
        Junction(jd, Path.Combine(_game, "data"));
        Assert.True(Directory.Exists(Path.Combine(j, "data")), "the junction wasn't made");
        foreach (var p in new[] { Path.Combine(j, "x.glb"), Path.Combine(j, "new", "deeper", "x.glb"), Path.Combine(jd, "x.glb"), Path.Combine(jd, "new", "x.glb"),
                     @"\\?\" + Path.Combine(jd, "x.glb"), Share("localhost", Path.Combine(jd, "x.glb")) })
        {
            Assert.True(ClipExport.Refused(p, [_game]), p);
            Assert.True(RecordWriterRefuses(Path.GetDirectoryName(p)!, _game), p);
        }
        Assert.True(RecordWriterRefuses(j, _game));
        // the protected folder named through the junction
        Assert.True(ClipExport.Refused(Path.Combine(_game, "x.glb"), [j]));
        Assert.False(ClipExport.Refused(Path.Combine(_out, "x.glb"), [j, jd]));

        string s = Path.Combine(_dir, "symlink");
        try { Directory.CreateSymbolicLink(s, Path.Combine(_game, "data")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // no privilege for symbolic links here
        Assert.True(ClipExport.Refused(Path.Combine(s, "x.glb"), [_game]));
        Assert.True(ClipExport.Refused(Path.Combine(s, "new", "x.glb"), [_game]));
    }

    /// <summary>A folder carrying this tool's marker (a package, or a local undo folder an earlier version wrote), any folder below it (a
    /// package's "Original clips" among them), a junction leading into either, and their other spellings are refused by every writer, with
    /// the same way out: the record writer (with overwrite too), its plain files and its folder check, the export's guard, its single file
    /// and its .glb with its sidecar, and the package's plan as a save folder. Nothing is written in them. A folder beside them, and an
    /// ordinary folder whose name only looks like one, are fine.</summary>
    [Fact]
    public void APackageOrALocalUndoFolderIsRefusedByEveryWriter()
    {
        string pkg = Path.Combine(_out, "Pack"), undo = Path.Combine(_out, "Pack - local undo (do not share)"), undo2 = Path.Combine(_out, "Pack - local undo");
        Directory.CreateDirectory(Path.Combine(pkg, "sub"));
        Directory.CreateDirectory(Path.Combine(pkg, "Original clips"));
        Directory.CreateDirectory(undo);
        Directory.CreateDirectory(undo2);
        File.WriteAllText(Path.Combine(pkg, PackageBuild.MarkerName), PackageBuild.MarkerText("Pack", DateTime.UtcNow, restore: false));
        File.WriteAllText(Path.Combine(undo, PackageBuild.MarkerName), PackageBuild.MarkerText("Pack - local undo (do not share)", DateTime.UtcNow, restore: true));
        File.WriteAllText(Path.Combine(undo2, PackageBuild.MarkerName), PackageBuild.MarkerText("Pack - local undo", DateTime.UtcNow));
        File.WriteAllText(Path.Combine(undo, "README.txt"), "the local undo note");
        string toSub = Path.Combine(_dir, "to-sub"), toUndo = Path.Combine(_dir, "to-undo");
        Junction(toSub, Path.Combine(pkg, "sub"));
        Junction(toUndo, undo);
        string Tree() => string.Join("\n", Directory.GetFileSystemEntries(_out, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => p + (File.Exists(p) ? " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : "")));
        string before = Tree();
        const string Advice = "Pick a folder outside your packages";
        (int, string, ulong)[] clips = [(102, "walk", 0x0000012300000001)];
        string[] refused = [pkg, pkg + @"\", Path.Combine(pkg, "sub"), Path.Combine(pkg, "Original clips"), Path.Combine(pkg, "new", "deeper"), undo, undo2, Path.Combine(undo2, "sub"),
            toSub, Path.Combine(toSub, "new"), toUndo,
            @"\\?\" + Path.Combine(pkg, "sub"), Share("localhost", Path.Combine(pkg, "sub")), pkg.ToUpperInvariant()];
        foreach (var folder in refused)
        {
            Assert.Contains(Advice, Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(folder, "anim", [_game], [])).Message);
            Assert.Contains(Advice, Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(folder, "anim", [_game], [], overwrite: true)).Message);
            Assert.Contains(Advice, Assert.Throws<RecordFileException>(() => RecordFileWriter.WriteFiles(folder, [_game], [("a.checks.txt", [1])], overwrite: true)).Message);
            Assert.Contains(Advice, RecordFileWriter.OutputRefusal(folder, [_game]));
            string file = Path.Combine(folder, "x.glb");
            Assert.Contains(Advice, Assert.Throws<ExportException>(() => ClipExport.Guard(file, [_game])).Message);
            Assert.Contains(Advice, Assert.Throws<ExportException>(() => ClipExport.Guard(file, [])).Message);
            Assert.Contains(Advice, Assert.Throws<ExportException>(() => ClipExport.WriteFile(file, [_game], s => s.WriteByte(1))).Message);
            Assert.Throws<ExportException>(() => ClipExport.Write(file, [1], ClipExport.SidecarPathFor(file), [2], [_game]));
            var plan = PackageBuild.Plan(clips, "Inner", folder, [_game]);
            Assert.Contains(Advice, plan.Problem);
            // the build refuses it again, whatever the plan said
            Assert.Contains(Advice, Assert.Throws<RecordFileException>(() =>
                PackageBuild.Build(plan with { Problem = null }, _ => throw new InvalidOperationException("must not write"), _ => throw new InvalidOperationException("must not write"), false, [_game])).Message);
        }
        // the junction is said as where it leads
        Assert.Contains($"{toSub} leads to {Path.Combine(pkg, "sub")}, inside {pkg}, a package this tool built, or a local undo folder a pre-release build of it wrote",
            RecordFileWriter.OutputRefusal(Path.Combine(toSub, "new"), [_game]));
        Assert.Equal(before, Tree());
        Assert.Equal("the local undo note", File.ReadAllText(Path.Combine(undo, "README.txt")));
        // beside them, and a folder named like a local undo folder without the marker, is allowed
        string plain = Path.Combine(_out, "Other - local undo (do not share)");
        Directory.CreateDirectory(plain);
        foreach (var folder in new[] { _out, Path.Combine(_out, "beside"), plain })
        {
            Assert.Null(RecordFileWriter.OutputRefusal(folder, [_game]));
            ClipExport.Guard(Path.Combine(folder, "x.glb"), [_game]);
            Assert.DoesNotContain(Advice, Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(folder, "anim", [_game], [])).Message);
            Assert.Null(PackageBuild.Plan(clips, "Inner", folder, [_game]).Problem);
        }
    }

    internal static void Junction(string link, string target) => Mklink("/J", link, target);

    /// <summary>A junction (/J) or a hard link (/H), neither needing a privilege, made by the system's own mklink.</summary>
    static void Mklink(string kind, string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c", "mklink", kind, link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000));
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>A stream name (a ':' after the drive) on the fake protected folder itself, on a folder or a file inside it, or anywhere
    /// else, is refused by every writer before anything is written; nothing appears on or in the fake protected folder.</summary>
    [Fact]
    public void StreamNamesAreRefused()
    {
        string data = Path.Combine(_game, "data");
        string[] streams = [_game + ":s", _game + ":x.glb", _game + @"::$INDEX_ALLOCATION\x.glb", _game + @":$I30:$INDEX_ALLOCATION\x.glb",
            data + ":s", Path.Combine(_game, "x.glb") + ":s", Path.Combine(data, "x.glb") + "::$DATA", @"\\?\" + _game + ":s",
            Share("localhost", _game) + ":s", Path.Combine(_out, "x.glb") + ":s", _out + ":s", _out + "::$INDEX_ALLOCATION"];
        foreach (var p in streams)
        {
            Assert.True(ClipExport.Refused(p, [_game]), p);
            var ex = Assert.Throws<ExportException>(() => ClipExport.WriteFile(p, [_game], s => s.WriteByte(1)));
            Assert.Contains("stream", ex.Message);
            Assert.True(RecordWriterRefuses(p, _game), p);
        }
        Assert.False(File.Exists(_game + ":s"));
        Assert.False(File.Exists(_game + ":x.glb"));
        Assert.Equal([data], Directory.EnumerateFileSystemEntries(_game));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_out));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    /// <summary>NT namespace spellings (<c>\??\</c>, <c>\\?\GLOBALROOT\</c>, <c>\\?\Volume{...}\</c>) and, under <c>\\?\</c>, names
    /// ending in a dot or a space and . or .. parts are refused, wherever they lead.</summary>
    [Fact]
    public void NtNamespaceFormsAreRefused()
    {
        string p = Path.Combine(_game, "x.glb"), o = Path.Combine(_out, "x.glb");
        var forms = new List<string> { @"\??\" + p, @"\??\" + o, @"\\?\GLOBALROOT\??\" + p, @"\\?\GLOBALROOT\??\" + o, @"\\.\GLOBALROOT\??\" + p,
            "//?/GLOBALROOT/??/" + p, @"\\?\" + _game + @".\x.glb", @"\\?\" + _game + @" \x.glb", @"\\?\" + Path.Combine(_out, "..", "Fake Game", "x.glb"),
            @"\\?\" + _game + @"\.\x.glb" };
        var vol = new StringBuilder(64);
        if (GetVolumeNameForVolumeMountPointW(Path.GetPathRoot(p)!, vol, vol.Capacity))     // \\?\Volume{...}\ of the folder's drive
        {
            forms.Add(vol + p[3..]);
            forms.Add(vol + o[3..]);
            Assert.True(Directory.Exists(vol + _game[3..]), "the volume spelling doesn't reach the folder");
        }
        foreach (var f in forms)
        {
            Assert.True(ClipExport.Refused(f, [_game]), f);
            Assert.Throws<ExportException>(() => ClipExport.WriteFile(f, [_game], s => s.WriteByte(1)));
            Assert.True(RecordWriterRefuses(f, _game), f);
        }
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_out));
    }

    /// <summary>Paths past 260 characters are checked like short ones (every folder is opened by its <c>\\?\</c> path): a junction
    /// into the fake protected folder at a deep place, and a deep fake protected folder reached through a junction, are refused. A
    /// folder on the path that exists but can't be opened (a junction whose target is gone) refuses the path: the guard fails closed.</summary>
    [Fact]
    public void LongPathsAndFoldersThatCantBeCheckedAreRefused()
    {
        string first = Path.Combine(_dir, new string('d', 40)), deep = first;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);

        // a junction into the protected folder, made at a short place and moved to the deep one
        string shortJ = Path.Combine(_dir, "jmove"), deepJ = Path.Combine(deep, "junction");
        Junction(shortJ, _game);
        Directory.Move(shortJ, deepJ);
        _links.Add(deepJ);
        Assert.True(deepJ.Length > 260);
        Assert.True(Directory.Exists(Path.Combine(deepJ, "data")), "the junction didn't move");
        foreach (var p in new[] { Path.Combine(deepJ, "x.glb"), Path.Combine(deepJ, "new", "x.glb"), @"\\?\" + Path.Combine(deepJ, "x.glb"), Path.Combine(deepJ, "data", "x.glb") })
        {
            Assert.True(ClipExport.Refused(p, [_game]), p);
            Assert.Throws<ExportException>(() => ClipExport.WriteFile(p, [_game], s => s.WriteByte(1)));
            Assert.True(RecordWriterRefuses(Path.GetDirectoryName(p)!, _game), p);
        }

        // a fake protected folder whose own path is past 260 characters, reached through a junction to the chain's first folder
        string deepGame = Path.Combine(deep, "Fake Deep Game");
        Directory.CreateDirectory(deepGame);
        string toDeep = Path.Combine(_dir, "jdeep");
        Junction(toDeep, first);
        string viaJ = Path.Combine(toDeep, Path.GetRelativePath(first, deepGame));
        Assert.True(Directory.Exists(viaJ));
        Assert.True(ClipExport.Refused(Path.Combine(deepGame, "x.glb"), [deepGame]));
        Assert.True(ClipExport.Refused(Path.Combine(viaJ, "x.glb"), [deepGame]));
        Assert.True(ClipExport.Refused(Path.Combine(viaJ, "new", "x.glb"), [deepGame]));
        Assert.True(ClipExport.Refused(Path.Combine(deepGame, "x.glb"), [viaJ]));      // the protected folder named through the junction
        Assert.True(RecordWriterRefuses(viaJ, deepGame));
        Assert.False(ClipExport.Refused(Path.Combine(deep, "x.glb"), [deepGame, _game]));
        ClipExport.WriteFile(Path.Combine(deep, "ok.bin"), [_game, deepGame], s => s.WriteByte(7));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(deep, "ok.bin")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(deepGame));

        // fail closed: a junction whose target is gone is a folder that can't be opened
        string gone = Path.Combine(_dir, "gone"), dangling = Path.Combine(_dir, "dangling");
        Directory.CreateDirectory(gone);
        Junction(dangling, gone);
        Directory.Delete(gone);
        var why = FolderGuard.Refusal(Path.Combine(dangling, "x.glb"), [_game]);
        Assert.NotNull(why);
        Assert.Contains("couldn't be opened", why);
        Assert.Throws<ExportException>(() => ClipExport.WriteFile(Path.Combine(dangling, "x.glb"), [_game], s => s.WriteByte(1)));
        // the unpack rule fails closed on it too, with no protected folder given, saying why without calling it an unpack
        var unpackWhy = FolderGuard.UnpackedDatafileRefusal(Path.Combine(dangling, "new"));
        Assert.NotNull(unpackWhy);
        Assert.Contains("couldn't be opened", unpackWhy);
        Assert.DoesNotContain(FolderGuard.UnpackedDatafileAdvice, unpackWhy);
        Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(Path.Combine(dangling, "new"), "anim", [], [new RecordFileItem(94, "mca_emote_Clap", 0x1234, [1])]));
        Assert.Throws<ExportException>(() => ClipExport.WriteFile(Path.Combine(dangling, "x.glb"), [], s => s.WriteByte(1)));
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));
    }

    /// <summary>A fact skipped, and reported as skipped, where the test's volume keeps no 8.3 short names.</summary>
    public sealed class ShortNamesFactAttribute : FactAttribute
    {
        public ShortNamesFactAttribute()
        {
            string probe = Path.Combine(AppContext.BaseDirectory, "GrbAnimTool.Tests.dll");
            var sb = new StringBuilder(1024);
            uint n = GetShortPathNameW(probe, sb, (uint)sb.Capacity);
            if (n == 0 || n >= sb.Capacity || Path.GetFileName(sb.ToString()).Equals(Path.GetFileName(probe), StringComparison.OrdinalIgnoreCase))
                Skip = $"the volume of {AppContext.BaseDirectory} keeps no 8.3 short names, so a short spelling can't be made here";
        }
    }

    /// <summary>A fact skipped, and reported as skipped, where this computer's administrative share of the test's drive
    /// (<c>\\localhost\X$</c>) can't be reached as it is set up (nothing is changed to reach it).</summary>
    public sealed class LocalShareFactAttribute : FactAttribute
    {
        public LocalShareFactAttribute()
        {
            string b = AppContext.BaseDirectory;
            bool reached;
            try { reached = b.Length > 3 && b[1] == ':' && Directory.Exists($@"\\localhost\{b[0]}$\{b[3..]}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reached = false; }
            if (!reached) Skip = $"\\\\localhost\\{b[0]}$ isn't reachable on this computer as it is set up, so the identity over a share can't be checked here";
        }
    }

    /// <summary>An 8.3 short name of the fake protected folder leads into it, given either side; skipped (and reported so) where the
    /// volume keeps no short names.</summary>
    [ShortNamesFact]
    public void ShortNamesAreFollowed()
    {
        var sb = new StringBuilder(1024);
        uint n = GetShortPathNameW(_game, sb, (uint)sb.Capacity);
        string shortGame = n > 0 && n < sb.Capacity ? sb.ToString() : _game;
        Assert.False(Path.GetFileName(shortGame).Equals(Path.GetFileName(_game), StringComparison.OrdinalIgnoreCase), "the volume gave no short name");
        foreach (var p in new[] { Path.Combine(shortGame, "x.glb"), Path.Combine(shortGame, "new", "x.glb"), Path.Combine(shortGame, "data", "x.glb") })
        {
            Assert.True(ClipExport.Refused(p, [_game]), p);
            Assert.True(RecordWriterRefuses(Path.GetDirectoryName(p)!, _game), p);
        }
        Assert.True(ClipExport.Refused(Path.Combine(_game, "x.glb"), [shortGame]));
        Assert.False(ClipExport.Refused(Path.Combine(_out, "x.glb"), [shortGame]));
    }

    /// <summary>The identity check over a share: with the share's spelling left as given (an internal hook, this thread only), a path into
    /// the fake protected folder through <c>\\localhost\X$</c> is still refused, as the folder opened over the share has the protected
    /// folder's volume serial and file ID; a folder beside it isn't. Skipped (and reported so) where the share can't be reached.</summary>
    [LocalShareFact]
    public void TheIdentityHoldsOverAShare()
    {
        FolderGuard.SharesUnmappedForTest = true;
        try
        {
            string viaShare = Share("localhost", _game), besideShare = Share("localhost", _out);
            Assert.StartsWith(@"\\localhost\", FolderGuard.Canonical(viaShare), StringComparison.OrdinalIgnoreCase);   // the spelling alone doesn't tell
            foreach (var p in new[] { viaShare, Path.Combine(viaShare, "x.glb"), Path.Combine(viaShare, "new", "x.glb") })
            {
                var why = FolderGuard.Refusal(p, [_game]);
                Assert.NotNull(why);
                Assert.Contains("is that folder", why);
            }
            Assert.Null(FolderGuard.Refusal(Path.Combine(besideShare, "x.glb"), [_game]));
        }
        finally { FolderGuard.SharesUnmappedForTest = false; }
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));
    }

    /// <summary>A share whose root lies inside the protected folder: the walk up such a path ends at the share's root and never meets the
    /// protected folder itself. The administrative share stands in for it, its spelling left as given: a path into a folder below the
    /// fake protected folder is refused at that folder already, the first existing one the walk meets, by where it stands on this
    /// computer; a folder beside the protected one isn't. Skipped (and reported so) where the share can't be reached.</summary>
    [LocalShareFact]
    public void AFolderBelowOverAShareIsRefusedByWhereItStands()
    {
        FolderGuard.SharesUnmappedForTest = true;
        try
        {
            string local = Path.Combine(_game, "data"), data = Share("localhost", local);
            foreach (var p in new[] { data, Path.Combine(data, "x.glb"), Path.Combine(data, "new", "x.glb") })
            {
                var why = FolderGuard.Refusal(p, [_game]);
                Assert.NotNull(why);
                Assert.Contains($"({data} is {local} on this computer)", why, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Null(FolderGuard.Refusal(Path.Combine(Share("localhost", _out), "new", "x.glb"), [_game]));
        }
        finally { FolderGuard.SharesUnmappedForTest = false; }
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));
    }

    /// <summary>The decision for a folder over a share on a protected folder's volume, without a share: with the protected folder on this
    /// computer, a folder that stands inside it is refused, one that stands outside it isn't, and one whose place can't be read is refused;
    /// with the protected folder itself on a share (a NAS), nothing is refused for it and nothing is opened.</summary>
    [Fact]
    public void AShareOnAProtectedFoldersVolumeIsRefusedOnlyWhenThatFolderIsOnThisComputer()
    {
        var local = new FolderGuard.Place(@"C:\Games\GRB", [@"C:\Games\GRB"], (7, 9));
        var nas = new FolderGuard.Place(@"\\nas\games\GRB", [@"\\nas\games\GRB"], (7, 9));
        string d = @"\\nas\mods\out";
        Assert.Contains(@"is C:\Games\GRB\data on this computer", FolderGuard.ShareRefusal(d, local, [local], () => @"\\?\C:\Games\GRB\data"));
        Assert.Null(FolderGuard.ShareRefusal(d, local, [local], () => @"\\?\C:\Mods\out"));
        Assert.Contains("couldn't be read", FolderGuard.ShareRefusal(d, local, [local], () => null));
        bool opened = false;
        Assert.Null(FolderGuard.ShareRefusal(d, nas, [nas], () => { opened = true; return null; }));
        Assert.False(opened);
        // the game folder itself opened through a share of this computer is looked for as a local one: inside refused, unreadable refused
        var here = new FolderGuard.Place(@"\\127.0.0.1\games\GRB", [@"\\127.0.0.1\games\GRB"], (7, 9));
        Assert.Contains(@"is C:\Games\GRB\data on this computer", FolderGuard.ShareRefusal(d, here, [here, local], () => @"\\?\C:\Games\GRB\data"));
        Assert.Contains("couldn't be read", FolderGuard.ShareRefusal(d, here, [here], () => null));
    }

    /// <summary>The forms a file to write never needs are refused with no protected folder given too.</summary>
    [Fact]
    public void TheFormIsCheckedWithNoProtectedFolder()
    {
        foreach (var p in new[] { @"\\.\" + _out + @"\x.glb", @"\\?\GLOBALROOT\Device\x", Path.Combine(_out, "x.glb:stream") })
        {
            Assert.NotNull(FolderGuard.Refusal(p, []));
            Assert.NotNull(FolderGuard.Refusal(p, ["", " "]));
        }
        Assert.Null(FolderGuard.Refusal(Path.Combine(_out, "x.glb"), []));
    }

    /// <summary>Only the start of a build marker is read: a line past its first 4 KiB isn't looked for.</summary>
    [Fact]
    public void OnlyTheStartOfAMarkerIsRead()
    {
        string d = Path.Combine(_out, "marked"), marker = Path.Combine(d, PackageBuild.MarkerName);
        Directory.CreateDirectory(d);
        File.WriteAllText(marker, PackageBuild.MarkerHead + "\n" + new string(' ', 5000) + "\nname: far\n");
        Assert.True(PackageBuild.IsEarlierBuild(d));
        Assert.False(PackageBuild.IsEarlierBuild(d, "far", restore: false));
        File.WriteAllText(marker, PackageBuild.MarkerText("near", DateTime.UtcNow, restore: false));
        Assert.True(PackageBuild.IsEarlierBuild(d, "near", restore: false));
    }

    /// <summary>A build whose writer faults with an error that isn't a file error leaves no staging folder, and no folder made for it.</summary>
    [Fact]
    public void ABuildThatFaultsLeavesNothingBehind()
    {
        string save = Path.Combine(_out, "packs");
        var plan = PackageBuild.Plan([(94, "mca_emote_Clap", 0x1234UL)], "Mine", save, [_game]);
        Assert.Null(plan.Problem);
        Assert.Throws<InvalidOperationException>(() => PackageBuild.Build(plan, _ => throw new InvalidOperationException("a fault"), null, replace: false, [_game]));
        Assert.False(Directory.Exists(save));
    }

    /// <summary>A folder that looks like a datafile unpacked from a forge by its name (a fake <c>46_-_X.data</c> made here, any case, a
    /// subfolder of it, a junction to it or to a subfolder of it, a junction to such a junction, with or without a new folder below, and
    /// a junction to a folder above it with the unpack's name after it) is refused by every writer with the plain advice, and nothing is
    /// written in it; a name of another form is allowed, and so is a folder of record files that aren't the game's own (their bytes aren't
    /// any clip of the game's), and a folder inside it.</summary>
    [Fact]
    public void AnUnpackedDatafileFolderIsRefusedByEveryWriter()
    {
        string extracted = Path.Combine(_dir, "Extracted");
        string unpack = Path.Combine(extracted, "46_-_X.data");
        string sub = Path.Combine(unpack, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(unpack, "94_-_mca_emote_Clap.Animation"), "original");
        string many = Path.Combine(_dir, "my exports");
        Directory.CreateDirectory(many);
        for (int i = 0; i < 1200; i++) File.WriteAllBytes(Path.Combine(many, $"{i}_-_clip{i}.Animation"), [1]);
        string j = Path.Combine(_dir, "to-unpack"), jSub = Path.Combine(_dir, "to-sub"), jUp = Path.Combine(_dir, "to-extracted");
        string jj = Path.Combine(_dir, "to-to-unpack"), jjSub = Path.Combine(_dir, "to-to-sub");
        Junction(j, unpack);
        Junction(jSub, sub);
        Junction(jUp, extracted);
        Junction(jj, j);
        Junction(jjSub, jSub);
        _links.AddRange([j, jSub, jUp, jj, jjSub]);
        string forges = Path.Combine(_dir, "Fake Forges");
        Directory.CreateDirectory(forges);
        var protect = ClipExport.ProtectedFolders(_game, Path.Combine(forges, "DataPC_patch_01.forge"));
        var item = new RecordFileItem(94, "mca_emote_Clap", 0x1234, [0x7F, 0x06, 0xA3, 0x0F]);
        foreach (var dir in new[] { unpack, sub, Path.Combine(sub, "new"), j, Path.Combine(j, "new"), jSub, Path.Combine(jSub, "new"), Path.Combine(jSub, "new", "deeper"),
                     Path.Combine(jUp, "46_-_X.data"), Path.Combine(jUp, "46_-_X.data", "sub"), unpack.ToUpperInvariant(), Path.Combine(extracted, "46_-_x.DATA"),
                     @"\\?\" + unpack, Share("localhost", unpack), @"\\?\" + jSub, Share("localhost", jSub), jj, Path.Combine(jj, "new"), jjSub, Path.Combine(jjSub, "new") })
        {
            Assert.NotNull(FolderGuard.UnpackedDatafileRefusal(dir));
            var ex = Assert.Throws<RecordFileException>(() => RecordFileWriter.Write(dir, "46_-_X.data", protect, [item], overwrite: true));
            Assert.Contains(FolderGuard.UnpackedDatafileAdvice, ex.Message);
            Assert.Contains(FolderGuard.UnpackedDatafileAdvice, RecordFileWriter.OutputRefusal(dir, protect));
            Assert.Throws<RecordFileException>(() => RecordFileWriter.WriteFiles(dir, protect, [("a.checks.txt", [1])], overwrite: true));
            var e2 = Assert.Throws<ExportException>(() => ClipExport.WriteFile(Path.Combine(dir, "x.png"), protect, s => s.WriteByte(1), makeFolder: true));
            Assert.Contains(FolderGuard.UnpackedDatafileAdvice, e2.Message);
            Assert.Throws<ExportException>(() => ClipExport.Write(Path.Combine(dir, "c.glb"), [1], Path.Combine(dir, "c.grbanim.json"), [2], protect));
            Assert.Throws<IOException>(() => new ViewerSettings { GameFolder = _game }.Save(Path.Combine(dir, "settings.json"), protect));
            Assert.Contains(FolderGuard.UnpackedDatafileAdvice, PackageBuild.Plan([(94, "mca_emote_Clap", 0x1234UL)], "pack", dir, protect).Problem);
        }
        // a package named like an unpack, inside a save folder of the user's own
        Assert.Contains(FolderGuard.UnpackedDatafileAdvice, PackageBuild.Plan([(94, "mca_emote_Clap", 0x1234UL)], "24_-_Y.data", _out, protect).Problem);
        // nothing was written in it, the original untouched, its subfolder empty
        Assert.Equal(new[] { Path.Combine(unpack, "94_-_mca_emote_Clap.Animation"), sub }.Order(StringComparer.Ordinal), Directory.EnumerateFileSystemEntries(unpack).Order(StringComparer.Ordinal));
        Assert.Equal("original", File.ReadAllText(Path.Combine(unpack, "94_-_mca_emote_Clap.Animation")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sub));
        // a folder of the player's own record files, more of them than the content rule's count but none the game's own, an ordinary
        // empty folder inside it, a new one, and names of other forms are allowed
        Assert.Null(FolderGuard.UnpackedDatafileRefusal(many));
        string empty = Path.Combine(many, "new pack");
        Directory.CreateDirectory(empty);
        foreach (var ok in new[] { many, empty, Path.Combine(many, "not made yet"), Path.Combine(_dir, "46_-_X.data2"), Path.Combine(_dir, "a_-_X.data"), Path.Combine(_dir, "46_-_.data"), _out })
        {
            Assert.Null(FolderGuard.UnpackedDatafileRefusal(ok));
            Assert.Null(RecordFileWriter.OutputRefusal(ok, protect));
        }
        RecordFileWriter.WriteFiles(empty, protect, [("a.checks.txt", [1])]);
        ClipExport.WriteFile(Path.Combine(many, "ok.bin"), protect, s => s.WriteByte(1));
        Assert.True(File.Exists(Path.Combine(many, "ok.bin")));
        Assert.True(File.Exists(Path.Combine(empty, "a.checks.txt")));
    }

    /// <summary>The export's paired write checks before it makes any folder: through a junction into the fake protected folder, with new
    /// folders below it, it is refused and nothing at all is made inside the fake folder.</summary>
    [Fact]
    public void TheExportMakesNoFolderBeforeItsCheck()
    {
        string j = Path.Combine(_dir, "junction-made");
        Junction(j, _game);
        foreach (var dir in new[] { Path.Combine(j, "madehere"), Path.Combine(j, "madehere", "g"), Path.Combine(j, "data", "a", "b") })
        {
            Assert.Throws<ExportException>(() => ClipExport.Write(Path.Combine(dir, "c.glb"), [1], Path.Combine(dir, "c.grbanim.json"), [2], [_game]));
            Assert.Throws<ExportException>(() => ClipExport.Write(Path.Combine(dir, "c.glb"), [1], null, null, [_game]));
        }
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_game, "data")));
    }

    /// <summary>This computer's other names: every address of its network interfaces (its LAN addresses), any 127.x.x.x address, the
    /// IPv6 loopback as an ipv6-literal.net name, an IPv4-mapped loopback and its host name. Each, as an administrative share, is turned
    /// back into the drive: refused inside the fake protected folder, allowed beside it. Nothing goes over the network.</summary>
    [Fact]
    public void OtherNamesOfThisComputerAreItsDrives()
    {
        string p = Path.Combine(_game, "x.glb"), o = Path.Combine(_out, "x.glb");
        var hosts = new List<string> { "127.0.0.2", "127.1", "127.255.255.254", "localhost.", "[::ffff:127.0.0.1]", "0--1.ipv6-literal.net", "[::1]", Dns.GetHostName() };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
            {
                var a = u.Address;
                if (IPAddress.IsLoopback(a)) continue;
                if (a.AddressFamily == AddressFamily.InterNetwork) hosts.Add(a.ToString());
                else if (a.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    hosts.Add(new IPAddress(a.GetAddressBytes()).ToString().Replace(':', '-') + ".ipv6-literal.net");
                    hosts.Add(a.ToString().Replace(':', '-').Replace('%', 's') + ".ipv6-literal.net");
                }
            }
        foreach (var h in hosts)
        {
            Assert.True(FolderGuard.IsThisComputer(h), h);
            Assert.True(ClipExport.Refused(Share(h, p), [_game]), h);
            Assert.True(RecordWriterRefuses(Share(h, _game), _game), h);
            Assert.Equal(o, FolderGuard.Canonical(Share(h, o)), ignoreCase: true);
            Assert.False(ClipExport.Refused(Share(h, o), [_game]), h);
        }
        Assert.False(FolderGuard.IsThisComputer("192.0.2.1"));         // a documentation address: no computer's
        Assert.False(FolderGuard.IsThisComputer("server"));
    }

    /// <summary>The picture mode's PNG paths (<c>--screenshot</c>, <c>--shot</c>) are refused when the command line is read, inside the
    /// game folder or the folder of the forge given, in any spelling; the picture's write is guarded again (folder made only once
    /// allowed). One protected set for every writer: the game folder and the forge's folder.</summary>
    [Fact]
    public void PicturePathsAreGuarded()
    {
        string forges = Path.Combine(_dir, "Fake Forges");
        Directory.CreateDirectory(forges);
        string forge = Path.Combine(forges, "DataPC_patch_01.forge");
        Assert.Equal(new[] { _game, forges }, ClipExport.ProtectedFolders(_game, forge));
        Assert.Equal(new[] { _game, "" }, ClipExport.ProtectedFolders(_game, null));
        string[] refused = [Path.Combine(_game, "x.png"), Path.Combine(_game, "new", "x.png"), Path.Combine(forges, "x.png"), _game + ":x.png",
            Share("localhost", Path.Combine(_game, "x.png")), @"\\?\" + Path.Combine(forges, "x.png"), Path.Combine(_out, "x.png") + ":s"];
        foreach (var png in refused)
        {
            Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", _game, "--forge", forge, "--screenshot", png]));
            Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", _game, "--forge", forge, "--shot", png + "|mca_emote_Hello|1|on"]));
            Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", _game, "--forge", forge, "--shot", Path.Combine(_out, "a.png"), "--shot", png]));
            Assert.Throws<ExportException>(() => ClipExport.WriteFile(png, ClipExport.ProtectedFolders(_game, forge), s => s.WriteByte(1), makeFolder: true));
        }
        var ex = Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", _game, "--screenshot", Path.Combine(_game, "x.png")]));
        Assert.Contains("never writes in the game's folder", ex.Message);
        // the clip index's cache folder: not the game's folder, nor one in it or in the forge's
        foreach (var cache in new[] { _game, Path.Combine(_game, "index"), forges })
            Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", _game, "--forge", forge, "--index-cache", cache]));
        Assert.Equal(Path.Combine(_out, "index"), ViewerArgs.Parse(["--game", _game, "--forge", forge, "--index-cache", Path.Combine(_out, "index")]).IndexCache);
        var ok = ViewerArgs.Parse(["--game", _game, "--forge", forge, "--screenshot", Path.Combine(_out, "x.png"), "--shot", Path.Combine(_out, "pics", "y.png") + "|c|1"]);
        Assert.Equal(2, ok.Shots.Count);
        ClipExport.WriteFile(Path.Combine(_out, "pics", "y.png"), ClipExport.ProtectedFolders(_game, forge), s => s.Write([1, 2]), makeFolder: true);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(_out, "pics", "y.png")));
        Assert.Single(Directory.GetFiles(Path.Combine(_out, "pics")));
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));
        Assert.Empty(Directory.EnumerateFileSystemEntries(forges));
    }

    /// <summary>An output file is written whole to a name of its own and moved over the path, so a hard link standing there is replaced,
    /// not written through; a failed write leaves the old file and no temporary file.</summary>
    [Fact]
    public void AnOutputFileIsReplacedNotWrittenThrough()
    {
        string outside = Path.Combine(_dir, "kept.bin"), link = Path.Combine(_out, "linked.bin");
        File.WriteAllBytes(outside, [1, 2, 3]);
        Mklink("/H", link, outside);
        _links.Add(link);
        ClipExport.WriteFile(link, [_game], s => s.WriteByte(9));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(outside));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(link));
        Assert.Throws<InvalidOperationException>(() => ClipExport.WriteFile(link, [_game], _ => throw new InvalidOperationException("stop")));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(link));
        Assert.Equal(new[] { "linked.bin" }, Directory.GetFiles(_out).Select(f => Path.GetFileName(f)));
    }

    /// <summary>The export checks its paths again once their folder exists and right before the moves: a folder that leads into the
    /// fake protected folder by then is refused and nothing is written there.</summary>
    [Fact]
    public void TheExportChecksAgainBeforeItWrites()
    {
        string j = Path.Combine(_dir, "junction-late");
        Junction(j, _game);
        Assert.Throws<ExportException>(() => ClipExport.Write(Path.Combine(j, "c.glb"), [1], Path.Combine(j, "c.grbanim.json"), [2], [_game]));
        Assert.Throws<ExportException>(() => ClipExport.Write(Path.Combine(j, "c.glb"), [1], null, null, [_game]));
        Assert.Equal([Path.Combine(_game, "data")], Directory.EnumerateFileSystemEntries(_game));
    }

    [Fact]
    public void FolderGuardCanonicalSpelling()
    {
        Assert.Equal(_game, FolderGuard.Canonical(@"\\?\" + _game + @"\"));
        Assert.Equal(_game, FolderGuard.Canonical(@"\\.\" + _game));
        Assert.Equal(_game, FolderGuard.Canonical(Share("localhost", _game)), ignoreCase: true);
        Assert.Equal(_game, FolderGuard.Canonical(@"\\?\UNC\" + Share("127.0.0.1", _game)[2..]), ignoreCase: true);
        Assert.Equal(@"\\server\D$\x", FolderGuard.Canonical(@"\\server\D$\x"));    // another computer's share stays as it is
    }

    /// <summary>The .glb and its sidecar are written whole under names of their own, then moved into place; when the .glb can't be
    /// replaced, the sidecar that was there before is put back and no temporary file is left.</summary>
    [Fact]
    public void GlbAndSidecarMoveTogether()
    {
        string glb = Path.Combine(_out, "c.glb"), side = ClipExport.SidecarPathFor(glb);
        ClipExport.Write(glb, [1, 2, 3], side, [4]);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(glb));
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(side));
        File.WriteAllBytes(Path.Combine(_out, "c.glb.tmp"), [9]);              // the old fixed temporary name is no longer touched
        ClipExport.Write(glb, [5], side, [6]);
        Assert.Equal(new byte[] { 5 }, File.ReadAllBytes(glb));
        Assert.Equal(new byte[] { 6 }, File.ReadAllBytes(side));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(_out, "c.glb.tmp")));
        File.Delete(Path.Combine(_out, "c.glb.tmp"));

        using (new FileStream(glb, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<ExportException>(() => ClipExport.Write(glb, [7], side, [8]));
        Assert.Equal(new byte[] { 5 }, File.ReadAllBytes(glb));
        Assert.Equal(new byte[] { 6 }, File.ReadAllBytes(side));
        Assert.Equal(new[] { "c.glb", "c.grbanim.json" }, Directory.GetFiles(_out).Select(f => Path.GetFileName(f)).Order());

        ClipExport.Write(glb, [10], null, null);                                  // the skeleton: a .glb alone
        Assert.Equal(new byte[] { 10 }, File.ReadAllBytes(glb));
        Assert.Equal(2, Directory.GetFiles(_out).Length);
    }
}

/// <summary>The package plan on an install: an item whose slot name a record file's name can't keep is the plan's problem, and the plan
/// is still given.</summary>
[Collection("install")]
public sealed class PackagePlanNameTests(InstallFixture f)
{
    [GameDataFact]
    public void ASlotNameTheRulesRefuseIsThePlansProblem()
    {
        var slot = new Core.Install.ClipEntry { Index = 3, Id = 0x1122334455667788, Name = "bad.name", Bytes = new byte[16] };
        var items = new List<Core.Import.PackageItem> { new(slot, new byte[16], ClipChange.Imported("x.glb"), Core.Checks.CheckLevel.Pass, "", DateTime.Now) };
        var plan = PackageBuild.Plan(f.Install, items, "Mine", Path.Combine(AppContext.BaseDirectory, "plan-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith("bad.name:", plan.Problem);
        Assert.Contains("so a record file wouldn't keep it", plan.Problem);   // the name rule's own words, not another problem of the clip
    }
}

/// <summary>The picture check on pixels made here: an empty 3D view and a view without its skeleton are caught.</summary>
public sealed class PictureCheckTests
{
    const int W = 40, H = 30;
    static readonly PixelRect View = new(10, 5, 20, 20);

    static int[] Plain(int colour) => Enumerable.Repeat(colour, W * H).ToArray();

    static int[] WithFloor()
    {
        var px = Plain(0x2B2D31);
        for (int x = 10; x < 30; x += 3) px[22 * W + x] = 0x787C84;          // a grid line's pixels in the view's lower quarter
        px[2 * W + 2] = 0xFFFFFF;                                              // text outside the view
        return px;
    }

    [Fact]
    public void AnEmptyViewIsCaught()
    {
        var blank = Plain(0x2B2D31);
        for (int x = 11; x < 20; x++) blank[7 * W + x] = 0xE8E8E8;            // the overlay's text at the view's top
        Assert.Contains("empty", PictureCheck.ViewProblem(blank, [], W, H, View));
        Assert.Contains("no size", PictureCheck.ViewProblem(blank, [], W, H, new PixelRect(10, 5, 0, 20)));
        Assert.Null(PictureCheck.ViewProblem(WithFloor(), [], W, H, View));
    }

    [Fact]
    public void AViewWithoutItsSkeletonIsCaught()
    {
        var bare = WithFloor();
        var shot = (int[])bare.Clone();
        Assert.Contains("no skeleton", PictureCheck.ViewProblem(shot, bare, W, H, View));
        shot[12 * W + 20] = 0x4F8FD8;                                          // one joint's pixel
        Assert.Null(PictureCheck.ViewProblem(shot, bare, W, H, View));
        var outside = (int[])bare.Clone();
        outside[12 * W + 35] = 0x4F8FD8;                                       // a difference outside the view doesn't count
        Assert.Contains("no skeleton", PictureCheck.ViewProblem(outside, bare, W, H, View));
        Assert.Equal(1, PictureCheck.Differences(shot, bare, W, View));
        Assert.True(PictureCheck.OneColour(Plain(5), W, View));
        Assert.True(PictureCheck.OneColour(Plain(5), W, View.Within(W, H)));
        Assert.Equal(new PixelRect(30, 25, 10, 5), new PixelRect(30, 25, 50, 50).Within(W, H));
    }
}
