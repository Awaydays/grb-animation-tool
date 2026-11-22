using System.Buffers.Text;
using System.Globalization;
using System.Text;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Logs;

/// <summary>What a line of a clip log is: a note (<c>#</c>), the header, a clip start, or a line that reads as none of them.</summary>
public enum ClipLogKind { Note, Header, Start, Unreadable }

/// <summary>What started a clip, as the log says: an animation state, an animation layer, a motion-matching jump, or something else.</summary>
public enum ClipLogSource { Clip, Layer, Motion, Other }

/// <summary>Whether a start's clip ID was looked up in an install's clips, and what came of it.</summary>
public enum ClipLookup { NotLooked, Found, NotFound }

/// <summary>One line of a clip log: its number in the file (from 1), its kind, and for a start the time, the seconds since the log
/// started, the character (the player's or another), the character's address and ID, the clip's ID and what started it; for a note or a
/// line that doesn't read, its text (and why it doesn't read). <see cref="Clip"/> is the install's clip of that ID once looked up.</summary>
public sealed class ClipLogLine
{
    public required int Number { get; init; }
    public required ClipLogKind Kind { get; init; }

    /// <summary>A note's text (after the <c>#</c>), or the text of a line that doesn't read (cut to <see cref="ClipLog.MostText"/>
    /// characters); null for a start or the header.</summary>
    public string? Text { get; init; }

    /// <summary>Why a line doesn't read; null otherwise.</summary>
    public string? Problem { get; init; }

    public DateTime Time { get; init; }
    public double Seconds { get; init; }
    public bool Player { get; init; }
    public ulong Entity { get; init; }
    public ulong EntityId { get; init; }
    public ulong ClipId { get; init; }
    public ClipLogSource Source { get; init; }

    /// <summary>For a start of <see cref="ClipLogSource.Other"/>, where in the game's code it came from as the log gives it after
    /// "other " (as "GRB.exe+41C2D0"), or null.</summary>
    public string? SourceDetail { get; init; }

    /// <summary>A note that says the file reached the logger's size limit: the starts after it were counted, not written.</summary>
    public bool SizeLimit => Kind == ClipLogKind.Note && Text is { } t && t.StartsWith(ClipLog.SizeLimitNote, StringComparison.Ordinal);

    /// <summary>The install's clip of this start's ID (<see cref="ClipLog.Resolve"/>), or null: not looked up, or not one of its clips.</summary>
    public ClipEntry? Clip { get; set; }

    /// <summary>Whether the ID was looked up (<see cref="ClipLog.Resolve"/>).</summary>
    public ClipLookup Lookup { get; set; }

    /// <summary>Whether a click on the line can show its clip: a start whose ID is one of the install's clips.</summary>
    public bool Opens => Kind == ClipLogKind.Start && Clip is not null;

    // ---- the line's text in the Log screen's columns ----

    public string TimeText => Kind == ClipLogKind.Start ? Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "";

    /// <summary>The clip's name in the game's files; for an ID the install doesn't hold, "unknown clip" (the ID is beside it, never a
    /// guess); before an install is open, a word saying so; for a line that doesn't read, "unreadable line".</summary>
    public string ClipText => Kind switch
    {
        ClipLogKind.Unreadable => "unreadable line",
        ClipLogKind.Start => Lookup switch
        {
            ClipLookup.Found => Clip!.Name,
            ClipLookup.NotFound => UnknownText,
            _ => "(no game folder open)",
        },
        _ => "",
    };

    public const string UnknownText = "unknown clip";

    public string IdText => Kind == ClipLogKind.Start ? $"0x{ClipId:X16}" : "";

    public string CharacterText => Kind == ClipLogKind.Start ? Player ? "player" : "other" : "";

    public string SourceText => Kind == ClipLogKind.Start ? ClipLog.Word(Source) : "";

    /// <summary>The line's whole account, for its tooltip.</summary>
    public string Tip => Kind switch
    {
        ClipLogKind.Start => FormattableString.Invariant($"Line {Number}: {Time:yyyy-MM-dd HH:mm:ss.fff}, {Seconds:F3} s after the log started; ")
            + $"{CharacterText}, entity {Entity:X}, entity ID {EntityId:X16}; clip ID 0x{ClipId:X16}, started by {SourceText}"
            + (SourceDetail is { } at ? $" (at {at} in the game's code)" : "") + ". "
            + Lookup switch
            {
                ClipLookup.Found => $"Clip {Clip!.Name} (record {Clip.Index} of the animation datafile). Click to show it in the Library.",
                ClipLookup.NotFound => "Not one of the clips of the game folder open: the tool doesn't name it.",
                _ => "Open the game folder in the Library to name the clip.",
            },
        ClipLogKind.Unreadable => $"Line {Number} doesn't read: {Problem}. The line: {Text}",
        ClipLogKind.Note => $"Line {Number}: {Text}",
        _ => $"Line {Number}: the header",
    };
}

/// <summary>The clip logger's CSV log: one file per session, notes on lines starting <c>#</c>, one header line, then one line per clip
/// start: <c>time,seconds,character,entity,entity_id,clip_id,source</c>, for example
/// <c>2026-11-18 19:10:18.490,11.111,player,1891C424E60,00000000F0010660,0x000001A6250011B7,clip</c>.</summary>
public static class ClipLog
{
    public const string Header = "time,seconds,character,entity,entity_id,clip_id,source";

    /// <summary>The start of the note the logger writes when the file reaches its size limit (32 MB).</summary>
    public const string SizeLimitNote = "the file reached its size limit";

    /// <summary>The largest file read as a log: twice the 32 MB past which the logger writes no more starts, so any file it wrote reads
    /// whole, and a larger file (one chosen by mistake) is refused instead of read into memory.</summary>
    public const long MostFile = 64L * 1024 * 1024;

    /// <summary>The most characters of a note or an unreadable line kept.</summary>
    public const int MostText = 400;

    /// <summary>The folder the logger writes its logs in: "GRB - Animation logging" in the Documents folder, as Windows reports it;
    /// null when Windows reports none.</summary>
    public static string? DefaultFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs ? Path.Combine(docs, FolderName) : null;

    public const string FolderName = "GRB - Animation logging";

    public static string Word(ClipLogSource s) => s switch
    {
        ClipLogSource.Clip => "clip",
        ClipLogSource.Layer => "layer",
        ClipLogSource.Motion => "motion",
        _ => "other",
    };

    /// <summary>One line (its bytes, without the line end; a CR before it is taken off), numbered <paramref name="number"/>; null for an
    /// empty line. Never throws: a line that reads as no note, header or start is <see cref="ClipLogKind.Unreadable"/>, with why.</summary>
    public static ClipLogLine? Parse(ReadOnlySpan<byte> line, int number)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (line.IsEmpty) return null;
        if (line[0] == (byte)'#')
        {
            var rest = line[1..];
            if (rest.Length > 0 && rest[0] == (byte)' ') rest = rest[1..];
            return new ClipLogLine { Number = number, Kind = ClipLogKind.Note, Text = Text(rest) };
        }
        if (line.SequenceEqual(HeaderBytes)) return new ClipLogLine { Number = number, Kind = ClipLogKind.Header };
        string? problem = Start(line, out var start);
        if (problem is null) return new ClipLogLine
        {
            Number = number, Kind = ClipLogKind.Start, Time = start.Time, Seconds = start.Seconds, Player = start.Player, Entity = start.Entity,
            EntityId = start.EntityId, ClipId = start.ClipId, Source = start.Source, SourceDetail = start.SourceDetail,
        };
        return new ClipLogLine { Number = number, Kind = ClipLogKind.Unreadable, Text = Text(line), Problem = problem };
    }

    static readonly byte[] HeaderBytes = Encoding.ASCII.GetBytes(Header);

    static string Text(ReadOnlySpan<byte> b)
    {
        string s = Encoding.UTF8.GetString(b.Length > MostText * 4 ? b[..(MostText * 4)] : b);
        return s.Length > MostText ? s[..MostText] + " [cut]" : s;
    }

    readonly record struct StartFields(DateTime Time, double Seconds, bool Player, ulong Entity, ulong EntityId, ulong ClipId, ClipLogSource Source, string? SourceDetail);

    /// <summary>A start's seven fields, or why the line isn't one.</summary>
    static string? Start(ReadOnlySpan<byte> line, out StartFields f)
    {
        f = default;
        Span<Range> parts = stackalloc Range[7];
        int n = 0, from = 0;
        for (int i = 0; i <= line.Length && n <= 7; i++)
        {
            if (i < line.Length && line[i] != (byte)',') continue;
            if (n < 7) parts[n] = new Range(from, i);
            n++;
            from = i + 1;
        }
        if (n != 7) return $"{(n > 7 ? "more than 7" : n.ToString(CultureInfo.InvariantCulture))} fields, not the 7 of a start ({Header})";
        var time = line[parts[0]];
        if (!TryTime(time, out var t)) return "its time isn't of the form 2026-11-18 19:10:18.490";
        if (!Utf8Parser.TryParse(line[parts[1]], out double seconds, out int used) || used != line[parts[1]].Length || !double.IsFinite(seconds))
            return "its seconds aren't a number";
        var who = line[parts[2]];
        bool player;
        if (who.SequenceEqual("player"u8)) player = true;
        else if (who.SequenceEqual("other"u8)) player = false;
        else return "its character is neither player nor other";
        if (!Hex(line[parts[3]], out ulong entity)) return "its entity isn't a hex number";
        if (!Hex(line[parts[4]], out ulong entityId)) return "its entity_id isn't a hex number";
        var id = line[parts[5]];
        if (id.Length < 3 || id[0] != (byte)'0' || (id[1] | 0x20) != (byte)'x' || !Hex(id[2..], out ulong clipId)) return "its clip_id isn't a hex ID such as 0x000001A625068E05";
        var src = line[parts[6]];
        ClipLogSource source;
        string? detail = null;
        if (src.SequenceEqual("clip"u8)) source = ClipLogSource.Clip;
        else if (src.SequenceEqual("layer"u8)) source = ClipLogSource.Layer;
        else if (src.SequenceEqual("motion"u8)) source = ClipLogSource.Motion;
        else if (src.SequenceEqual("other"u8)) source = ClipLogSource.Other;
        // a start from a place in the game's code the logger doesn't name: "other " and where it is, as "other GRB.exe+41C2D0"
        else if (src.StartsWith("other "u8) && src.Length > 6) { source = ClipLogSource.Other; detail = Text(src[6..]); }
        else return "its source is none of clip, layer, motion and other";
        f = new StartFields(t, seconds, player, entity, entityId, clipId, source, detail);
        return null;
    }

    /// <summary>1 to 16 hex digits.</summary>
    static bool Hex(ReadOnlySpan<byte> s, out ulong v)
    {
        v = 0;
        if (s.Length is 0 or > 16) return false;
        foreach (byte b in s)
        {
            int lower = b | 0x20, d;
            if (b >= '0' && b <= '9') d = b - '0';
            else if (lower >= 'a' && lower <= 'f') d = lower - 'a' + 10;
            else return false;
            v = v << 4 | (uint)d;
        }
        return true;
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss.fff", read digit by digit.</summary>
    static bool TryTime(ReadOnlySpan<byte> s, out DateTime t)
    {
        t = default;
        if (s.Length != 23 || s[4] != '-' || s[7] != '-' || s[10] != ' ' || s[13] != ':' || s[16] != ':' || s[19] != '.') return false;
        int y = Digits(s, 0, 4), mo = Digits(s, 5, 2), d = Digits(s, 8, 2), h = Digits(s, 11, 2), mi = Digits(s, 14, 2), se = Digits(s, 17, 2), ms = Digits(s, 20, 3);
        if (y < 1 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h is < 0 or > 23 || mi is < 0 or > 59 || se is < 0 or > 59 || ms < 0)
            return false;
        t = new DateTime(y, mo, d, h, mi, se, ms, DateTimeKind.Local);
        return true;
    }

    /// <summary>The number of <paramref name="len"/> decimal digits at <paramref name="at"/>, or -1 when one isn't a digit.</summary>
    static int Digits(ReadOnlySpan<byte> s, int at, int len)
    {
        int v = 0;
        for (int i = at; i < at + len; i++)
        {
            if (s[i] < '0' || s[i] > '9') return -1;
            v = v * 10 + (s[i] - '0');
        }
        return v;
    }

    /// <summary>Looks each start's clip ID up in <paramref name="install"/>'s clips (null: no install, nothing looked up). Only the
    /// install's own clip list is used: an ID it doesn't hold stays unknown.</summary>
    public static void Resolve(IEnumerable<ClipLogLine> lines, GameInstall? install)
    {
        foreach (var l in lines)
        {
            if (l.Kind != ClipLogKind.Start) continue;
            if (install is null) { l.Clip = null; l.Lookup = ClipLookup.NotLooked; continue; }
            l.Clip = install.FindClip(l.ClipId);
            l.Lookup = l.Clip is null ? ClipLookup.NotFound : ClipLookup.Found;
        }
    }
}

/// <summary>What a read of a log gave: the lines completed since the last read (in order), whether the file was found replaced or cut
/// short (its lines then read again from its start, and the earlier ones no longer stand), the file's length and how far it is read
/// (the end of its last complete line), or why it couldn't be read now (null: it could); and, for a read that failed in a way the reader
/// doesn't foresee, the error, given by the read that met it only.</summary>
public sealed record ClipLogRead(IReadOnlyList<ClipLogLine> Lines, bool Restarted, long Length, long Position, string? Problem, Exception? Error = null);

/// <summary>Reads a clip log as it grows: each <see cref="Read"/> takes the lines completed since the last, never a line before its end
/// is written (the part after the last line end waits for the next read), and never the whole file again. The file is opened for each
/// read, shared for reading, writing and deleting, so the logger goes on writing it, and closed again. A file found shorter than what was
/// read, or whose start or last line read are no longer the same bytes (replaced by another file), is read again from its start, said by
/// <see cref="ClipLogRead.Restarted"/>. One read at a time: an instance isn't for several threads at once.</summary>
public sealed class ClipLogReader(string path)
{
    public string Path { get; } = path;

    /// <summary>How far the file is read: the end of its last complete line.</summary>
    public long Position { get; private set; }

    /// <summary>The lines read so far (empty lines among them).</summary>
    public int LineCount { get; private set; }

    /// <summary>The longest line read: a line longer (no line end within it) is unreadable, and skipped to its end.</summary>
    public const int MostLine = 1 << 16;

    /// <summary>The most lines read of one file: more than any log the logger writes holds (it stops at 32 MB, and a start is 45 bytes
    /// or more), so a file of more lines (short lines, no log) is refused instead of read into memory line by line.</summary>
    public const int MostLines = 1_000_000;

    /// <summary>The file's length and last write at the last read that was refused or failed in the reading, and what it said: the same
    /// file isn't read again until it changes.</summary>
    (long Length, DateTime Written)? _failedAt;
    string? _failure;

    /// <summary>How many bytes of the file's start, and of the end of what was read, are compared at each read to see the file is the same.</summary>
    const int Compared = 256;

    byte[] _head = [];
    byte[] _tail = [];
    bool _skipping;

    /// <summary>Reads the lines completed since the last read; reports its progress (0 to 1) on a read of more than a megabyte. A file
    /// that can't be opened now (not there, or held), or that fails part-way, gives no lines and why, and the next read tries again where
    /// the last whole read stood: what a read found counts only once it has ended. A UTF-8 byte order mark at the file's start is passed
    /// over. A file larger than <see cref="ClipLog.MostFile"/>, or of more than <see cref="MostLines"/> lines, isn't read. A file that
    /// was refused so, or whose read failed in another way than the file system's (<see cref="ClipLogRead.Error"/>, given once), isn't
    /// read again until its length or last write changes: each read until then gives the same answer. A read short of memory is the
    /// moment's, not the file's: the next read tries again. Only a cancelled read throws.</summary>
    public ClipLogRead Read(IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        FileStream fs;
        try { fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClipLogRead([], false, 0, Position, ex is FileNotFoundException or DirectoryNotFoundException ? "the file isn't there" : ex.Message);
        }
        using (fs)
        {
            (long Length, DateTime Written) state = default;
            // a read that fails part-way (a share that drops, say) says why and counts for nothing, as one that can't open
            try
            {
                state = (fs.Length, File.GetLastWriteTimeUtc(fs.SafeFileHandle));
                if (state == _failedAt) return new ClipLogRead([], false, state.Length, Position, _failure);
                var read = ReadOpen(fs, progress, cancel);
                if (read.Problem is not null) (_failedAt, _failure) = (state, read.Problem);
                return read;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new ClipLogRead([], false, 0, Position, ex.Message); }
            // any other failure would fail the same way at each read of the same file; memory short is the moment's, tried again next time
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string failure = $"{ex.GetType().Name}: {ex.Message}";
                if (ex is not OutOfMemoryException) (_failedAt, _failure) = (state, failure);
                return new ClipLogRead([], false, 0, Position, failure, ex);
            }
        }
    }

    ClipLogRead ReadOpen(FileStream fs, IProgress<double>? progress, CancellationToken cancel)
    {
        {
            long length = fs.Length;
            if (length > ClipLog.MostFile)
                return new ClipLogRead([], false, length, Position, FormattableString.Invariant(
                    $"it is {length / 1048576.0:F0} MB, more than a clip log can be (the logger stops at 32 MB), so it isn't read"));
            bool restarted = Position > 0 && (length < Position || !Same(fs, 0, _head) || !Same(fs, Position - _tail.Length, _tail));
            // the read's own place, line count and skip: kept only once the read has ended (a read that fails counts for nothing)
            long position = restarted ? 0 : Position;
            int lineCount = restarted ? 0 : LineCount;
            bool skipping = !restarted && _skipping;
            if (position == 0 && Same(fs, 0, Bom)) position = Bom.Length;
            var lines = new List<ClipLogLine>();
            long start = position, toRead = length - start;
            bool report = progress is not null && toRead > 1 << 20;
            fs.Position = position;
            var buf = new byte[Math.Clamp(toRead + 1, 4096, 1 << 20) + MostLine];
            int have = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                int got = fs.Read(buf, have, buf.Length - have);
                if (got <= 0) break;
                have += got;
                int at = 0;
                while (at < have)
                {
                    int nl = buf.AsSpan(at, have - at).IndexOf((byte)'\n');
                    if (nl < 0) break;
                    if (skipping) skipping = false;
                    else if (ClipLog.Parse(buf.AsSpan(at, nl), ++lineCount) is { } line) lines.Add(line);
                    at += nl + 1;
                    if (lineCount > MostLines)
                        return new ClipLogRead([], false, length, Position, FormattableString.Invariant(
                            $"it holds more than {MostLines:N0} lines, more than a clip log can hold (the logger stops at 32 MB), so it isn't read"));
                }
                position += at;
                if (at > 0) Buffer.BlockCopy(buf, at, buf, 0, have - at);
                have -= at;
                // a line longer than the most: unreadable, the rest of it skipped up to its end
                if (have >= MostLine)
                {
                    if (!skipping) lines.Add(new ClipLogLine
                    {
                        Number = ++lineCount, Kind = ClipLogKind.Unreadable, Text = Encoding.UTF8.GetString(buf, 0, ClipLog.MostText) + " [cut]",
                        Problem = FormattableString.Invariant($"longer than {MostLine:N0} bytes with no line end"),
                    });
                    skipping = true;
                    position += have;
                    have = 0;
                }
                if (report) progress!.Report(Math.Min(1, (double)(position - start) / Math.Max(1, toRead)));
            }
            var head = Bytes(fs, 0, (int)Math.Min(Compared, position));
            int t = (int)Math.Min(Compared, position);
            var tail = Bytes(fs, position - t, t);
            (Position, LineCount, _skipping, _head, _tail) = (position, lineCount, skipping, head, tail);
            return new ClipLogRead(lines, restarted, length, Position, null);
        }
    }

    static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    static byte[] Bytes(FileStream fs, long at, int n)
    {
        var b = new byte[n];
        fs.Position = at;
        int got = 0;
        while (got < n)
        {
            int r = fs.Read(b, got, n - got);
            if (r <= 0) return b[..got];
            got += r;
        }
        return b;
    }

    static bool Same(FileStream fs, long at, byte[] expected) =>
        expected.Length == 0 || at >= 0 && at + expected.Length <= fs.Length && Bytes(fs, at, expected.Length).AsSpan().SequenceEqual(expected);
}

/// <summary>The logs in a folder: the logger's files are named clips_&lt;build&gt;_&lt;date&gt;_&lt;time&gt;.csv (with _2, _3, ... after
/// the time when a name was taken); a file of another name is not one of them.
/// <para>Which is the newest: the time in a name is when its session started by the PC's clock as it then stood, so it is wrong across a
/// change of that clock (the hour repeated when daylight saving ends, a time zone changed); the time a file was last written is kept by
/// Windows in UTC, so a clock change doesn't touch it, but it also moves when an old log is saved again (in a spreadsheet, say). So two
/// logs are ordered by the time in their names, then by their numbers, unless their last writes say the other way round by more than an
/// hour: a newer session writes after an older one has stopped, so a name that says older while the file was written over an hour later
/// comes from a clock change, and the last writes decide. Within the hour, the names decide.</para></summary>
public static class ClipLogFolder
{
    public const string Pattern = "clips_*.csv";

    /// <summary>How far two logs' last writes must say the other way round from their names before they decide which is the newer.</summary>
    public static readonly TimeSpan WrittenOverrules = TimeSpan.FromHours(1);

    /// <summary>The newest log in <paramref name="folder"/>, or null when it holds none. Throws when the folder can't be listed.</summary>
    public static string? Newest(string folder)
    {
        var listed = new DirectoryInfo(folder).EnumerateFiles(Pattern).Where(f => f.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Info: f, Name: NameStamp(f.Name))).ToList();
        // the file itself asked for its last write only for the few its name calls newest, and any the listing says was written after
        // the oldest of those: a folder of months of logs is not opened file by file at each read
        var newestByName = listed.Where(f => f.Name is not null).OrderByDescending(f => f.Name!.Value).Take(MostAsked).Select(f => f.Info).ToHashSet();
        DateTime since = newestByName.Count > 0 ? newestByName.Min(f => f.LastWriteTimeUtc) : DateTime.MinValue;
        return Newest([.. listed.Select(f => (f.Info.FullName, f.Name,
            newestByName.Contains(f.Info) || f.Info.LastWriteTimeUtc > since ? WrittenUtc(f.Info) : f.Info.LastWriteTimeUtc))]);
    }

    /// <summary>How many logs, newest by name, are asked for their last write at each read (with any written after them).</summary>
    public const int MostAsked = 3;

    /// <summary>When a file was last written, asked of the file itself (a folder's listing may hold an older time for a file another
    /// program keeps open and writes to, as the logger does its log); the listing's time when the file can't be opened now.</summary>
    static DateTime WrittenUtc(FileInfo f)
    {
        try
        {
            using var h = File.OpenHandle(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return File.GetLastWriteTimeUtc(h);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return f.LastWriteTimeUtc; }
    }

    /// <summary>The newest of these logs (<see cref="ClipLogFolder"/>: by their names unless their last writes overrule them), or null.</summary>
    public static string? Newest(IReadOnlyList<(string Path, (DateTime Stamp, int Number)? Name, DateTime Written)> files)
    {
        if (files.Count == 0) return null;
        var best = files[0];
        foreach (var f in files.Skip(1)) if (Newer(f, best)) best = f;
        return best.Path;
    }

    /// <summary>Whether log <paramref name="a"/> is newer than <paramref name="b"/>.</summary>
    static bool Newer((string Path, (DateTime Stamp, int Number)? Name, DateTime Written) a, (string Path, (DateTime Stamp, int Number)? Name, DateTime Written) b)
    {
        // a file of this pattern whose name holds no time (not one the logger wrote) is older than every log whose name holds one: its
        // last write, in UTC, can't be set beside a name's local time; two such files go by their last writes
        if ((a.Name is null) != (b.Name is null)) return a.Name is not null;
        if (a.Name is null) return a.Written != b.Written ? a.Written > b.Written : string.CompareOrdinal(a.Path, b.Path) > 0;
        var an = a.Name.Value;
        var bn = b.Name!.Value;
        int byName = an.CompareTo(bn);
        if (byName < 0 && a.Written - b.Written > WrittenOverrules) return true;
        if (byName > 0 && b.Written - a.Written > WrittenOverrules) return false;
        if (byName != 0) return byName > 0;
        int byWritten = a.Written.CompareTo(b.Written);
        return byWritten != 0 ? byWritten > 0 : string.CompareOrdinal(a.Path, b.Path) > 0;
    }

    /// <summary>The time in a log's name (taken as UTC for ordering only) and its number (1 without one), or null for a name of another form.</summary>
    public static (DateTime Stamp, int Number)? NameStamp(string name)
    {
        // clips_<build>_<yyyy-MM-dd>_<HHmmss>[_<n>].csv
        if (!name.StartsWith("clips_", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = name[6..^4].Split('_');
        if (parts.Length < 3) return null;
        int number = 1, last = parts.Length - 1;
        if (parts.Length >= 4 && int.TryParse(parts[last], NumberStyles.None, CultureInfo.InvariantCulture, out int k) && parts[last].Length < 6) { number = k; last--; }
        if (last < 2) return null;
        if (!DateTime.TryParseExact(parts[last - 1] + "_" + parts[last], "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)) return null;
        return (stamp, number);
    }
}

/// <summary>A log's counts: its lines, starts (the player's among them), the starts whose ID is one of the install's clips and those
/// whose isn't (with the distinct unknown IDs), notes and unreadable lines, and whether it reached the logger's size limit.</summary>
public sealed class ClipLogCounts
{
    public int Lines, Starts, PlayerStarts, Found, NotFound, Notes, Unreadable;
    public bool ReachedLimit;
    public readonly HashSet<ulong> UnknownIds = [];

    public void Add(ClipLogLine l) => Add(l, l.Lookup);

    /// <summary>Counts <paramref name="l"/> as looked up with <paramref name="lookup"/> (a lookup worked out but not yet set on the line).</summary>
    public void Add(ClipLogLine l, ClipLookup lookup)
    {
        Lines++;
        switch (l.Kind)
        {
            case ClipLogKind.Start:
                Starts++;
                if (l.Player) PlayerStarts++;
                if (lookup == ClipLookup.Found) Found++;
                else if (lookup == ClipLookup.NotFound) { NotFound++; UnknownIds.Add(l.ClipId); }
                break;
            case ClipLogKind.Note:
                Notes++;
                if (l.SizeLimit) ReachedLimit = true;
                break;
            case ClipLogKind.Unreadable: Unreadable++; break;
        }
    }

    public static ClipLogCounts Of(IEnumerable<ClipLogLine> lines)
    {
        var c = new ClipLogCounts();
        foreach (var l in lines) c.Add(l);
        return c;
    }
}
