using System.Text;
using GrbAnimTool.Core.Forge;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Logs;

namespace GrbAnimTool.Tests;

/// <summary>The clip logger's log as the Log screen reads it: its lines parsed (notes, the header, starts, and lines that don't read,
/// never a crash), a growing file read by what was added and only by whole lines, a file replaced or cut short read again, and the
/// newest log of a folder. Logs made here are synthetic, in the test's own temporary folder.</summary>
public sealed class ClipLogTests : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "cliplog-tests", Guid.NewGuid().ToString("N"));

    readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ClipLogTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    static ClipLogLine? P(string s, int n = 1) => ClipLog.Parse(Encoding.UTF8.GetBytes(s), n);

    /// <summary>A start line as the logger writes it.</summary>
    public static string Start(int i, bool player = true, ulong clip = 0x000001A625068E05, string source = "clip") =>
        FormattableString.Invariant($"2026-11-18 19:{10 + i / 60000 % 50:D2}:{i / 1000 % 60:D2}.{i % 1000:D3},{i * 0.001:F3},{(player ? "player" : "other")},1891C424E60,00000000F0010660,0x{clip:X16},{source}");

    [Fact]
    public void NotesTheHeaderAndStartsRead()
    {
        var note = P("# the player's character: entity 1891C424E60", 3)!;
        Assert.Equal((ClipLogKind.Note, "the player's character: entity 1891C424E60", 3), (note.Kind, note.Text, note.Number));
        Assert.False(note.SizeLimit);
        Assert.Equal("", P("#")!.Text);
        Assert.Equal("no space", P("#no space")!.Text);
        Assert.Equal(ClipLogKind.Header, P(ClipLog.Header)!.Kind);
        var s = P("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip", 6)!;
        Assert.Equal(ClipLogKind.Start, s.Kind);
        Assert.Equal(new DateTime(2026, 11, 18, 19, 10, 18, 490), s.Time);
        Assert.Equal(11.111, s.Seconds, 6);
        Assert.True(s.Player);
        Assert.Equal(0x1891C424E60UL, s.Entity);
        Assert.Equal(0x00000000F0010660UL, s.EntityId);
        Assert.Equal(0x000001A6250011B7UL, s.ClipId);
        Assert.Equal(ClipLogSource.Clip, s.Source);
        Assert.Equal(("19:10:18.490", "0x000001A6250011B7", "player", "clip"), (s.TimeText, s.IdText, s.CharacterText, s.SourceText));
        foreach (var (word, src) in new[] { ("layer", ClipLogSource.Layer), ("motion", ClipLogSource.Motion), ("other", ClipLogSource.Other) })
            Assert.Equal(src, P(Start(1, source: word))!.Source);
        Assert.False(P(Start(1, player: false))!.Player);
        // not looked up yet: no clip, and the line says no install is open, never a name
        Assert.Equal((ClipLookup.NotLooked, "(no game folder open)", false), (s.Lookup, s.ClipText, s.Opens));
    }

    /// <summary>A start from a place in the game's code the logger doesn't name, as it writes it ("other " and the module and offset):
    /// a start of source other, the place kept for its tooltip; read so by the parser and by the reader from a file.</summary>
    [Theory]
    [InlineData("other GRB.exe+41C2D0", "GRB.exe+41C2D0")]
    [InlineData("other GRB_vulkan.exe+3679DD", "GRB_vulkan.exe+3679DD")]
    public void AStartFromAnUnnamedPlaceInTheGamesCodeIsOfSourceOther(string source, string at)
    {
        var s = P("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A625068E05," + source)!;
        Assert.Equal((ClipLogKind.Start, ClipLogSource.Other, at, "other"), (s.Kind, s.Source, s.SourceDetail, s.SourceText));
        Assert.Contains($"started by other (at {at} in the game's code)", s.Tip);
        Assert.Null(P(Start(1, source: "other"))!.SourceDetail);
        Assert.Equal(ClipLogKind.Unreadable, P(Start(1, source: "other "))!.Kind);
        Assert.Equal(ClipLogKind.Unreadable, P(Start(1, source: "others GRB.exe+1"))!.Kind);
        string path = Path.Combine(_dir, "clips_DX11_2026-11-18_191002.csv");
        File.WriteAllText(path, ClipLog.Header + "\n" + Start(1) + "\n" + Start(2, player: false, source: source) + "\n");
        var r = new ClipLogReader(path).Read();
        var other = r.Lines[^1];
        Assert.Equal((ClipLogKind.Start, ClipLogSource.Other, at, 0x000001A625068E05UL), (other.Kind, other.Source, other.SourceDetail, other.ClipId));
        Assert.Equal(0, ClipLogCounts.Of(r.Lines).Unreadable);
    }

    [Fact]
    public void ALineEndOfCrLfAndAnEmptyLineRead()
    {
        var s = P(Start(5) + "\r")!;
        Assert.Equal(ClipLogKind.Start, s.Kind);
        Assert.Equal(ClipLogSource.Clip, s.Source);
        Assert.Equal(ClipLogKind.Header, P(ClipLog.Header + "\r")!.Kind);
        Assert.Equal("a note", P("# a note\r")!.Text);
        Assert.Null(P(""));
        Assert.Null(P("\r"));
    }

    [Fact]
    public void TheSizeLimitsNoteIsTold()
    {
        var n = P("# the file reached its size limit - from here on starts are counted, not written")!;
        Assert.True(n.SizeLimit);
        var counts = ClipLogCounts.Of([n, P(Start(1))!, P("# stopped (the entry was switched off): 9 starts read, 1 written")!]);
        Assert.True(counts.ReachedLimit);
        Assert.Equal((3, 1, 2), (counts.Lines, counts.Starts, counts.Notes));
    }

    [Theory]
    [InlineData("garbage", "fields")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7", "fields")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip,extra", "more than 7")]
    [InlineData("2026-11-18 19:10:18,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip", "time")]
    [InlineData("2026-13-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip", "time")]
    [InlineData("2026-02-30 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip", "time")]
    [InlineData("2026-11-18 25:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip", "time")]
    [InlineData("2026-11-18 19:10:18.490,eleven,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip", "seconds")]
    [InlineData("2026-11-18 19:10:18.490,11.111,Player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip", "character")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,1891G424E60,00000000F0010660,0x000001A6250011B7,clip", "entity")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,,00000000F0010660,0x000001A6250011B7,clip", "entity")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F00106601,0x000001A6250011B7,clip", "entity_id")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,000001A6250011B7,clip", "clip_id")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x,clip", "clip_id")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x10000001A6250011B7,clip", "clip_id")]
    [InlineData("2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7,Clip", "source")]
    [InlineData(",,,,,,", "time")]
    public void AMalformedLineIsUnreadableWithWhy(string line, string why)
    {
        var l = P(line, 9)!;
        Assert.Equal(ClipLogKind.Unreadable, l.Kind);
        Assert.Contains(why, l.Problem);
        Assert.Equal(line, l.Text);
        Assert.Equal(("unreadable line", false), (l.ClipText, l.Opens));
        Assert.Contains("Line 9 doesn't read", l.Tip);
    }

    [Fact]
    public void BytesThatArentTextAndAHugeLineNeverCrash()
    {
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var b = new byte[rnd.Next(0, 300)];
            rnd.NextBytes(b);
            if (b.Length > 0 && rnd.Next(3) == 0) b[0] = (byte)'#';
            var l = ClipLog.Parse(b, i + 1);
            if (l is not null) Assert.True(l.Kind is ClipLogKind.Note or ClipLogKind.Unreadable, $"{l.Kind} from random bytes");
        }
        var huge = P(new string('x', 100_000))!;
        Assert.Equal(ClipLogKind.Unreadable, huge.Kind);
        Assert.True(huge.Text!.Length <= ClipLog.MostText + 6);
        // a line of more than the most a line may be, with no line end: unreadable, then the lines after it read
        string path = Path.Combine(_dir, "long.csv");
        File.WriteAllText(path, ClipLog.Header + "\n" + new string('y', ClipLogReader.MostLine * 3) + "\n" + Start(1) + "\n");
        var r = new ClipLogReader(path).Read();
        Assert.Equal(new[] { ClipLogKind.Header, ClipLogKind.Unreadable, ClipLogKind.Start }, r.Lines.Select(l => l.Kind));
        Assert.Equal(new[] { 1, 2, 3 }, r.Lines.Select(l => l.Number));
    }

    /// <summary>A log written while it is read, as the logger writes it (its file kept open, shared): each read gives the whole lines
    /// added since the last, none twice, none before its line end is written, in order, whatever the chunks the file grows by.</summary>
    [Fact]
    public void AGrowingFileIsReadByWholeLinesOnceEachInOrder()
    {
        string path = Path.Combine(_dir, "clips_DX11_2026-11-18_191002.csv");
        var text = new StringBuilder();
        text.Append("# GRB Clip Logger, DX11 build (GRB.exe), started 2026-11-18 19:10:07.379\n").Append(ClipLog.Header).Append('\n');
        for (int i = 0; i < 400; i++) text.Append(i % 50 == 7 ? $"# mark {i}\n" : Start(i, clip: 0x000001A625000000UL + (ulong)i) + (i % 3 == 0 ? "\r\n" : "\n"));
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var reader = new ClipLogReader(path);
        var got = new List<ClipLogLine>();
        var rnd = new Random(3);
        int at = 0;
        while (at < bytes.Length)
        {
            int n = Math.Min(bytes.Length - at, rnd.Next(1, 90));
            writer.Write(bytes, at, n);
            writer.Flush();
            at += n;
            var r = reader.Read();
            Assert.False(r.Restarted);
            Assert.Null(r.Problem);
            got.AddRange(r.Lines);
            // never past the last line end written
            Assert.Equal(Array.LastIndexOf(bytes, (byte)'\n', at - 1) + 1, reader.Position);
        }
        Assert.Equal(402, got.Count);
        Assert.Equal(Enumerable.Range(1, 402), got.Select(l => l.Number));
        var starts = got.Where(l => l.Kind == ClipLogKind.Start).ToList();
        Assert.Equal(Enumerable.Range(0, 400).Where(i => i % 50 != 7).Select(i => 0x000001A625000000UL + (ulong)i), starts.Select(l => l.ClipId));
        Assert.Equal(8, got.Count(l => l.Kind == ClipLogKind.Note && l.Text!.StartsWith("mark", StringComparison.Ordinal)));
        Assert.DoesNotContain(got, l => l.Kind == ClipLogKind.Unreadable);
        // nothing new: nothing read
        Assert.Empty(reader.Read().Lines);
    }

    /// <summary>A log cut short, or replaced by another file (longer, or as long), is read again from its start, said so; a log deleted
    /// says it isn't there and is read again once one is back.</summary>
    [Fact]
    public void AFileCutShortOrReplacedIsReadAgainFromItsStart()
    {
        string path = Path.Combine(_dir, "clips_Vulkan_2026-11-18_192150.csv");
        string head = "# GRB Clip Logger, Vulkan build (GRB_vulkan.exe), started 2026-11-18 19:21:50.437\n" + ClipLog.Header + "\n";
        File.WriteAllText(path, head + string.Concat(Enumerable.Range(0, 10).Select(i => Start(i) + "\n")));
        var reader = new ClipLogReader(path);
        Assert.Equal(12, reader.Read().Lines.Count);
        // cut short
        File.WriteAllText(path, head + Start(100) + "\n");
        var cut = reader.Read();
        Assert.True(cut.Restarted);
        Assert.Equal(new[] { 1, 2, 3 }, cut.Lines.Select(l => l.Number));
        Assert.Equal(100 * 0.001, cut.Lines[2].Seconds, 6);
        // replaced by a longer file of another start
        string other = "# GRB Clip Logger, Vulkan build (GRB_vulkan.exe), started 2026-11-18 20:00:00.000\n" + ClipLog.Header + "\n";
        File.WriteAllText(path + ".new", other + string.Concat(Enumerable.Range(0, 30).Select(i => Start(i + 200) + "\n")));
        File.Move(path + ".new", path, overwrite: true);
        var replaced = reader.Read();
        Assert.True(replaced.Restarted);
        Assert.Equal(32, replaced.Lines.Count);
        Assert.StartsWith("GRB Clip Logger, Vulkan build (GRB_vulkan.exe), started 2026-11-18 20:00", replaced.Lines[0].Text);
        // replaced by one with the same start but other lines after it, as long or longer: the end of what was read differs
        File.WriteAllText(path, other + string.Concat(Enumerable.Range(0, 30).Select(i => Start(i + 900) + "\n")));
        var same = reader.Read();
        Assert.True(same.Restarted);
        Assert.Equal(0.9, same.Lines[2].Seconds, 6);
        // grown: only the new lines
        File.AppendAllText(path, Start(5000) + "\n");
        var grown = reader.Read();
        Assert.False(grown.Restarted);
        Assert.Equal(5.0, Assert.Single(grown.Lines).Seconds, 6);
        // deleted: said, nothing read, and read from its start when another is there
        File.Delete(path);
        var gone = reader.Read();
        Assert.Equal("the file isn't there", gone.Problem);
        Assert.Empty(gone.Lines);
        File.WriteAllText(path, head + Start(1) + "\n");
        var back = reader.Read();
        Assert.True(back.Restarted);
        Assert.Equal(3, back.Lines.Count);
    }

    /// <summary>A log saved again with a UTF-8 byte order mark reads its first note as a note; a file larger than a log can be isn't read,
    /// and says why.</summary>
    [Fact]
    public void AByteOrderMarkIsPassedOverAndAFileTooLargeIsntRead()
    {
        string path = Path.Combine(_dir, "bom.csv");
        File.WriteAllText(path, "# GRB Clip Logger, DX11 build (GRB.exe), started 2026-11-18 19:10:07.379\r\n" + ClipLog.Header + "\r\n" + Start(1) + "\r\n", new UTF8Encoding(true));
        Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);
        var r = new ClipLogReader(path).Read();
        Assert.Equal(new[] { ClipLogKind.Note, ClipLogKind.Header, ClipLogKind.Start }, r.Lines.Select(l => l.Kind));
        Assert.StartsWith("GRB Clip Logger", r.Lines[0].Text);
        string big = Path.Combine(_dir, "big.csv");
        using (var fs = new FileStream(big, FileMode.Create)) fs.SetLength(ClipLog.MostFile + 1);
        var b = new ClipLogReader(big).Read();
        Assert.Empty(b.Lines);
        Assert.Contains("more than a clip log can be", b.Problem);
        Assert.Equal(0, new ClipLogReader(big).Position);
    }

    /// <summary>The live log, held open by its writer and growing, its name older than an older session's (the clock set back): its
    /// last write, asked of the file itself, overrules the names, and it is the newest.</summary>
    [Fact]
    public void ALogHeldOpenAndWrittenIsJudgedByItsOwnLastWrite()
    {
        string old = Path.Combine(_dir, "clips_DX11_2026-11-21_160000.csv");
        File.WriteAllText(old, ClipLog.Header + "\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        string live = Path.Combine(_dir, "clips_DX11_2026-11-21_120000.csv");
        using (var w = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            w.Write(Encoding.UTF8.GetBytes(ClipLog.Header + "\n"));
            w.Flush();
            // its last write set back past the old log's (the folder's listing takes that), then written to again through the handle
            // still open, as the logger writes: the file itself says now, the listing may still say five hours back
            File.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddHours(-5));
            for (int i = 0; i < 20; i++)
            {
                var b = Encoding.UTF8.GetBytes(Start(i) + "\n");
                w.Write(b);
                w.Flush();
            }
            DateTime listed = new DirectoryInfo(_dir).GetFiles(Path.GetFileName(live)).Single().LastWriteTimeUtc;
            _output.WriteLine($"the live log: listed as written {listed:u}, the file itself says {File.GetLastWriteTimeUtc(live):u}");
            Assert.Equal(live, ClipLogFolder.Newest(_dir));
        }
    }

    [Fact]
    public void TheNewestLogIsTheLatestInItsNameThenItsNumber()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ClipLogFolder.Newest(Path.Combine(_dir, "not-there")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "not-there")));
        Assert.Null(ClipLogFolder.Newest(_dir));
        void Make(string name, DateTime written)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllText(p, "");
            File.SetLastWriteTimeUtc(p, written);
        }
        var at = new DateTime(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc);
        // the first session's file last written 40 minutes after the second's (an old log saved again soon after): the names decide
        Make("clips_DX11_2026-11-18_191002.csv", at.AddMinutes(50));
        Make("clips_Vulkan_2026-11-18_192150.csv", at.AddMinutes(10));
        Make("notes.csv", at.AddYears(3));
        Make("clips_DX11_2026-11-18_191002.txt", at.AddYears(3));
        Assert.Equal("clips_Vulkan_2026-11-18_192150.csv", Path.GetFileName(ClipLogFolder.Newest(_dir)));
        Make("clips_DX11_2026-11-18_192150_2.csv", at.AddMinutes(10));
        Assert.Equal("clips_DX11_2026-11-18_192150_2.csv", Path.GetFileName(ClipLogFolder.Newest(_dir)));
        Make("clips_DX11_2026-11-21_160529.csv", at.AddDays(3));
        Assert.Equal("clips_DX11_2026-11-21_160529.csv", Path.GetFileName(ClipLogFolder.Newest(_dir)));
        // the clock set back (a time zone changed): a newer session named earlier, written hours after the others: its writes decide
        Make("clips_DX11_2026-11-21_120000.csv", at.AddDays(3).AddHours(4));
        Assert.Equal("clips_DX11_2026-11-21_120000.csv", Path.GetFileName(ClipLogFolder.Newest(_dir)));
        // the hour repeated when daylight saving ends: a session named 01:20 started after one named 01:50 had stopped, and written
        // more than an hour after it
        var night = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("b", ClipLogFolder.Newest([("a", (night.AddMinutes(110), 1), night.AddMinutes(55)), ("b", (night.AddMinutes(80), 1), night.AddMinutes(130))]));
        Assert.Equal("b", ClipLogFolder.Newest([("b", (night.AddMinutes(80), 1), night.AddMinutes(130)), ("a", (night.AddMinutes(110), 1), night.AddMinutes(55))]));
        // within the hour the names decide
        Assert.Equal("a", ClipLogFolder.Newest([("a", (night.AddMinutes(110), 1), night.AddMinutes(55)), ("b", (night.AddMinutes(80), 1), night.AddMinutes(100))]));
        // a file of the pattern whose name holds no time is older than every log whose name holds one, whenever it was written
        Make("clips_saved_again.csv", at.AddYears(3));
        Assert.Equal("clips_DX11_2026-11-21_120000.csv", Path.GetFileName(ClipLogFolder.Newest(_dir)));
        Assert.Equal("y", ClipLogFolder.Newest([("x", null, night), ("y", null, night.AddMinutes(1))]));
        Assert.Equal((new DateTime(2026, 11, 18, 19, 21, 50, DateTimeKind.Utc), 2), ClipLogFolder.NameStamp("clips_DX11_2026-11-18_192150_2.csv"));
        Assert.Null(ClipLogFolder.NameStamp("clips_DX11.csv"));
    }

    /// <summary>A 300,000-line log (about the logger's 32 MB) reads in one go with its progress, and again only by what is added.</summary>
    [Fact]
    public void ALogAtTheLoggersLimitReadsWithItsProgress()
    {
        string path = Path.Combine(_dir, "clips_DX11_2026-11-18_200000.csv");
        WriteBig(path, 300_000);
        var progress = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new ClipLogReader(path).Read(new Inline(progress.Add));
        sw.Stop();
        Assert.Equal(300_003, r.Lines.Count);
        Assert.Equal(300_000, r.Lines.Count(l => l.Kind == ClipLogKind.Start));
        Assert.True(progress.Count > 5, $"{progress.Count} progress reports");
        Assert.Equal(1.0, progress[^1], 6);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"{sw.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"300,000 lines ({new FileInfo(path).Length:N0} bytes) read in {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>A read that runs short of memory once says so, and isn't kept as the file's answer: the next read of the same file reads it.</summary>
    [Fact]
    public void AReadShortOfMemoryIsTriedAgainAtTheNextRead()
    {
        string path = Path.Combine(_dir, "clips_DX11_2026-11-18_210000.csv");
        WriteBig(path, 40_000);   // past a megabyte, so the read reports its progress
        var reader = new ClipLogReader(path);
        bool thrown = false;
        var r = reader.Read(new Inline(_ => { if (!thrown) { thrown = true; throw new OutOfMemoryException("short of memory"); } }));
        Assert.True(thrown);
        Assert.StartsWith("OutOfMemoryException", r.Problem);
        Assert.Equal(40_000, reader.Read().Lines.Count(l => l.Kind == ClipLogKind.Start));
    }

    /// <summary>A file of more lines than a clip log can hold (a file of short lines that is no log) isn't read into memory: no line of it
    /// is kept, why is said, and the same file isn't read again until it changes.</summary>
    [Fact]
    public void AFileOfMoreLinesThanALogCanHoldIsntKeptNorReadAgainUntilItChanges()
    {
        string path = Path.Combine(_dir, "numbers.csv");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("1\n", 1_100_000)));
        var reader = new ClipLogReader(path);
        var progress = new List<double>();
        var r = reader.Read(new Inline(progress.Add));
        Assert.Empty(r.Lines);
        Assert.Contains("more than a clip log can hold", r.Problem);
        Assert.Equal(0, reader.Position);
        Assert.NotEmpty(progress);
        // the same file: the same answer, without reading it
        progress.Clear();
        var again = reader.Read(new Inline(progress.Add));
        Assert.Empty(again.Lines);
        Assert.Equal(r.Problem, again.Problem);
        Assert.Empty(progress);
        // the file changed: read again
        File.AppendAllText(path, "2\n");
        Assert.Equal(r.Problem, reader.Read(new Inline(progress.Add)).Problem);
        Assert.NotEmpty(progress);
    }

    /// <summary>A synthetic log of <paramref name="starts"/> starts (two in three the player's), its notes and its summary.</summary>
    internal static void WriteBig(string path, int starts, ulong[]? ids = null)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.NewLine = "\n";
        w.WriteLine("# GRB Clip Logger, DX11 build (GRB.exe), started 2026-11-18 20:00:00.000");
        w.WriteLine(ClipLog.Header);
        for (int i = 0; i < starts; i++) w.WriteLine(Start(i, player: i % 3 != 2, clip: ids is null ? 0x000001A625000000UL + (ulong)(i % 5000) : ids[i % ids.Length], source: i % 4 == 0 ? "motion" : "clip"));
        w.WriteLine($"# stopped (the entry was switched off): {starts} starts read, {starts} written");
    }

    sealed class Inline(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>The real logs GRBANIM_CLIPLOGS names (files, or folders of clips_*.csv, separated by ';'), read through the install of
/// GRBANIM_GAME and GRBANIM_FORGE: every line reads, every Greetings start (0x000001A625068E05) is mca_emote_Hello, and the counts and the
/// IDs that aren't the install's clips are written to the test's output, with where else in the game's files each is found.</summary>
[Collection("install")]
public sealed class ClipLogGameTests(InstallFixture f, Xunit.Abstractions.ITestOutputHelper output)
{
    public const ulong Greetings = 0x000001A625068E05;

    /// <summary>The logs named, or none.</summary>
    public static IReadOnlyList<string> Logs()
    {
        var list = new List<string>();
        foreach (var p in (Environment.GetEnvironmentVariable("GRBANIM_CLIPLOGS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Directory.Exists(p)) list.AddRange(Directory.GetFiles(p, ClipLogFolder.Pattern).Order(StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p)) list.Add(p);
        return list;
    }

    public sealed class ClipLogsFactAttribute : FactAttribute
    {
        public ClipLogsFactAttribute()
        {
            if (!GameData.Available) Skip = GameData.SkipText;
            else if (Logs().Count == 0) Skip = "no real clip logs named: set GRBANIM_CLIPLOGS to clip log files or folders (separated by ';')";
        }
    }

    [ClipLogsFact]
    public void EveryGreetingsStartOfTheRealLogsIsTheHelloEmote()
    {
        var g = f.Install;
        var hello = g.FindClip(Greetings);
        Assert.NotNull(hello);
        Assert.Equal(("mca_emote_Hello", 102), (hello!.Name, hello.Index));
        var unknown = new Dictionary<ulong, (int Starts, SortedSet<string> Logs, SortedSet<string> Sources, bool Player, bool Other)>();
        int greetings = 0;
        foreach (var path in Logs())
        {
            var read = new ClipLogReader(path).Read();
            ClipLog.Resolve(read.Lines, g);
            var c = ClipLogCounts.Of(read.Lines);
            var mine = read.Lines.Where(l => l.Kind == ClipLogKind.Start && l.ClipId == Greetings).ToList();
            greetings += mine.Count;
            Assert.All(mine, l => Assert.Equal(("mca_emote_Hello", true), (l.ClipText, l.Opens)));
            Assert.Equal(0, c.Unreadable);
            Assert.Null(read.Problem);
            string name = Path.GetFileName(path);
            output.WriteLine($"{name}: {c.Lines} lines ({c.Notes} notes, 1 header, {c.Starts} starts, {c.PlayerStarts} the player's); "
                + $"{c.Found} resolved, {c.NotFound} unknown ({c.UnknownIds.Count} distinct IDs); Greetings {mine.Count}");
            foreach (var l in read.Lines.Where(l => l.Lookup == ClipLookup.NotFound))
            {
                Assert.Equal((ClipLogLine.UnknownText, false), (l.ClipText, l.Opens));
                (int Starts, SortedSet<string> Logs, SortedSet<string> Sources, bool Player, bool Other) u =
                    unknown.TryGetValue(l.ClipId, out var x) ? x : (0, new SortedSet<string>(), new SortedSet<string>(), false, false);
                u.Logs.Add(name);
                u.Sources.Add(l.SourceText);
                unknown[l.ClipId] = (u.Starts + 1, u.Logs, u.Sources, u.Player || l.Player, u.Other || !l.Player);
            }
        }
        Assert.True(greetings > 0, "no Greetings start in the logs named");
        output.WriteLine($"Greetings starts in all: {greetings}, each mca_emote_Hello (record {hello.Index}), through GRBANIM_FORGE's patch forge");

        // the same through the game folder's own patch forge (a modded one among them): the ID names the same slot, whatever it holds
        using (var own = GameInstall.Open(GameData.Game!))
        {
            int n = 0;
            foreach (var path in Logs())
            {
                var read = new ClipLogReader(path).Read();
                ClipLog.Resolve(read.Lines, own);
                var mine = read.Lines.Where(l => l.Kind == ClipLogKind.Start && l.ClipId == Greetings).ToList();
                Assert.All(mine, l => Assert.Equal("mca_emote_Hello", l.ClipText));
                n += mine.Count;
                var c = ClipLogCounts.Of(read.Lines);
                output.WriteLine($"through {Path.GetFileName(own.PatchForgePath)} of the game folder ({own.Clips.Count} clips): {Path.GetFileName(path)}: {c.Found} resolved, {c.NotFound} unknown ({c.UnknownIds.Count} distinct IDs)");
            }
            Assert.Equal(greetings, n);
            output.WriteLine($"Greetings starts through the game folder's own patch forge: {n}, each mca_emote_Hello (status {own.FindClip(Greetings)!.Status})");
        }

        // where else the IDs the install's clips don't hold are: the animation datafile's other records, a record naming them, the
        // three forges' entries
        var forges = new[] { g.PatchForgePath, g.BaseForgePath, Path.Combine(g.GameFolder, "DataPC_extra.forge") }.Where(File.Exists)
            .Select(p => (Name: Path.GetFileName(p), Layout: ForgeReader.Read(p))).ToList();
        output.WriteLine($"{unknown.Count} distinct IDs not among the install's {g.Clips.Count} clips:");
        foreach (var (id, u) in unknown.OrderBy(k => k.Key))
        {
            var record = g.Records.FirstOrDefault(r => r.Id == id);
            var key = BitConverter.GetBytes(id);
            var naming = g.Records.Where(r => r.Bytes.Span.IndexOf(key) >= 0).Take(4).Select(r => $"{r.Name} (type {r.Type:X8})").ToList();
            var entries = forges.Select(fg => (fg.Name, Entry: fg.Layout.Find(id))).Where(x => x.Entry is not null).Select(x => $"{x.Name} entry {x.Entry!.Index} {x.Entry.Name}").ToList();
            output.WriteLine($"  0x{id:X16}: {u.Starts} start(s) ({string.Join("/", u.Sources)}; {(u.Player ? "player" : "")}{(u.Player && u.Other ? "+" : "")}{(u.Other ? "other" : "")}) in {string.Join(", ", u.Logs)}; "
                + $"datafile record: {(record is null ? "none" : $"{record.Name} type {record.Type:X8}")}; named in: {(naming.Count == 0 ? "no record of the datafile" : string.Join(", ", naming))}; "
                + $"forge entries: {(entries.Count == 0 ? "none" : string.Join(", ", entries))}");
        }
    }

    /// <summary>The IDs of the real logs that aren't the install's clips, looked for in every datafile of the three forges (read only):
    /// as a record of one (its name and type), else as eight bytes anywhere in one. Written to the test's output; nothing is asserted of
    /// where they are, as the screen shows only what the install's clip list holds.</summary>
    [ClipLogsFact, Trait("Category", "Slow")]
    public void TheUnknownIdsAreLookedForInEveryDatafileOfTheForges()
    {
        var g = f.Install;
        var ids = Logs().SelectMany(p => new ClipLogReader(p).Read().Lines).Where(l => l.Kind == ClipLogKind.Start && g.FindClip(l.ClipId) is null)
            .Select(l => l.ClipId).Distinct().Order().ToList();
        if (ids.Count == 0) { output.WriteLine("every ID of the logs is one of the install's clips"); return; }
        var keys = ids.ToDictionary(id => id, id => BitConverter.GetBytes(id));
        var asRecord = ids.ToDictionary(id => id, _ => new List<string>());
        var namedIn = ids.ToDictionary(id => id, _ => new List<string>());
        foreach (var path in new[] { g.PatchForgePath, Path.Combine(g.GameFolder, "DataPC_extra.forge"), g.BaseForgePath }.Where(File.Exists))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string forge = Path.GetFileName(path);
            var layout = ForgeReader.Read(path);
            int read = 0, failed = 0;
            long bytes = 0;
            using var fs = ForgeReader.OpenRead(path);
            foreach (var e in layout.Entries)
            {
                byte[] decoded;
                int table;
                try { decoded = Core.Datafiles.DatafileCodec.Decode(ForgeReader.ReadPayload(fs, e), g.Oodle, out table, out _); }
                catch (Exception) { failed++; continue; }
                read++;
                bytes += decoded.Length;
                List<Core.Datafiles.DatafileRecord>? records = null;
                try { records = Core.Datafiles.DatafileCodec.Parse(decoded, table); }
                catch (Exception) { }
                foreach (var id in ids)
                {
                    if (records?.FirstOrDefault(r => r.Id == id) is { } r) asRecord[id].Add($"{forge} entry {e.Index} {e.Name}: record {r.Name} (type {r.Type:X8})");
                    else if (decoded.AsSpan().IndexOf(keys[id]) >= 0) namedIn[id].Add($"{forge} entry {e.Index} {e.Name}");
                }
            }
            output.WriteLine(FormattableString.Invariant($"{forge}: {read} datafiles decoded ({bytes / 1e6:F0} MB), {failed} entries not datafiles this reads, in {sw.Elapsed.TotalSeconds:F0} s"));
        }
        foreach (var id in ids)
            output.WriteLine($"0x{id:X16}: {(asRecord[id].Count == 0 ? "a record of no datafile" : "a record in " + string.Join("; ", asRecord[id].Take(4)))}; "
                + $"named in {namedIn[id].Count} other datafile(s){(namedIn[id].Count == 0 ? "" : ": " + string.Join("; ", namedIn[id].Take(4)) + (namedIn[id].Count > 4 ? "; ..." : ""))}");
    }
}
