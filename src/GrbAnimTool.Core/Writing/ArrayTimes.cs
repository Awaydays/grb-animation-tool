using System.Buffers.Binary;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Writing;

/// <summary>One list found in a clip's byte array (see <see cref="ByteArrayTimes"/>).</summary>
/// <param name="BlockAt">The offset in the record of the block's word.</param>
/// <param name="TimesAt">The offset in the record of the list's first value.</param>
/// <param name="Width">The bytes of each value: 1 or 2.</param>
/// <param name="Values">The values, in file order (in the game's clips, rising times in 1/120 s).</param>
public sealed record ArrayTimeList(int BlockAt, int TimesAt, int Width, IReadOnlyList<int> Values)
{
    /// <summary>Whether a value is above 0, so a change of the clip's length would leave it out of step.</summary>
    public bool HoldsTime => Values.Any(v => v > 0);
}

/// <summary>What a clip's byte array holds, read as far as its lists of times. The array is the counted one after the last track object
/// before the curves (two bytes of 0, a u32 length, then the bytes), ending right at the curves' wrapper. It starts with a header that
/// gives the block count (the byte after <c>30 05 A7 D0</c>; an array of the header alone, 66 bytes, holds neither). The header's other
/// bytes aren't read as times: two idles of 538 and 544 ticks hold byte-identical arrays, header included, so it doesn't follow the length.
/// After the header the array holds blocks, each:
/// <list type="bullet">
/// <item>the word <c>6C 98 41 86 03 60</c>, one byte, <c>FF FF FF</c>, then the payload's length <c>L</c> as a LEB128 varint (7 bits a
/// byte, low first, the high bit set on every byte but the last; at most 5 bytes, none overlong) and <c>L</c> bytes of payload;</item>
/// <item>the payload: a form byte, then a list. Forms 0 and 2: a u8 count n and n u8 values. Forms 1 and 3: a 0 byte, a u16 count n and n
/// u16 values. After the list, per value: forms 0 and 1 hold 1 byte a value; forms 2 and 3 pad to 4 bytes (from the payload's start), then
/// hold 4 bytes a value. The payload ends exactly there;</item>
/// <item>after the payload, <c>02 00</c> and the next block's word, or the array's end.</item>
/// </list>
/// On the game's clips every block reads by this rule, and the values are times in 1/120 s: they rise, most end at or before the clip's
/// length and many at the length itself. A list is written in u8 where every time fits a byte: no u16 list in the game's clips holds only
/// times below 256. What the game does with them isn't known, so they aren't scaled; a length change refuses a clip holding any (see
/// <see cref="DurationWriter.CanChange"/>). A block that doesn't fit this rule exactly counts as unread, and refuses the clip too; so does
/// a header count that differs from the block words found (a block whose word isn't exact), and an array without the count that isn't the
/// header alone.</summary>
public static class ByteArrayTimes
{
    /// <summary>The word that starts each block.</summary>
    public static ReadOnlySpan<byte> BlockWord => [0x6C, 0x98, 0x41, 0x86, 0x03, 0x60];

    /// <summary>Where the array's bytes stand in the record (after its u32 length), or null when the record doesn't place them (no track
    /// list, or the length runs past the record's end). Only objects before the curves count: the last track object among them, else the
    /// track list.</summary>
    public static (int At, int Length)? ArrayOf(ClipRecord clip)
    {
        var b = clip.Bytes;
        int limit = clip.FirstCurve > 0 ? clip.FirstCurve : b.Length;
        var objs = RecordObjects.Objects(b).Where(o => o.At < limit).ToList();
        int after = -1;
        for (int i = objs.Count - 1; i >= 0 && after < 0; i--) if (objs[i].Class == RecordObjects.TrackClass) after = objs[i].At + 16;
        if (after < 0)
        {
            var list = objs.LastOrDefault(x => x.Class == RecordObjects.TrackListClass);
            if (list.Class == 0) return null;
            after = list.At + 20;
        }
        if (after + 6 > b.Length) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(after + 2));
        if (len < 0 || after + 6L + len > b.Length) return null;
        return (after + 6, len);
    }

    /// <summary>The bytes that stand in the array's header right before its block count: the count is the byte after them.</summary>
    public static ReadOnlySpan<byte> CountMarker => [0x30, 0x05, 0xA7, 0xD0];

    /// <summary>The length of the one array form that holds no block and no count: the header alone, without <see cref="CountMarker"/>.</summary>
    public const int HeaderOnlyLength = 66;

    /// <summary>The block count the array's header gives: the byte after <see cref="CountMarker"/>, the marker searched for in the bytes
    /// before the first block word (the whole array when it holds none). Null when the array can't be placed, is empty, or holds no marker
    /// there; -1 when the count byte is missing or has its high bit set (a count this rule doesn't read).</summary>
    public static int? HeaderCount(ClipRecord clip)
    {
        if (ArrayOf(clip) is not { } arr || arr.Length == 0) return null;
        var s = clip.Bytes.AsSpan(arr.At, arr.Length);
        int first = s.IndexOf(BlockWord);
        var head = first < 0 ? s : s[..first];
        int m = head.IndexOf(CountMarker);
        if (m < 0) return null;
        int at = m + CountMarker.Length;
        return at < head.Length && head[at] < 0x80 ? head[at] : -1;
    }

    /// <summary>Every list in the clip's byte array, in file order. <paramref name="unread"/> counts the blocks that don't read by the rule
    /// (a length that isn't a sound varint, a payload that runs past the array, doesn't fit its form exactly or isn't followed by <c>02 00</c>
    /// and a block word or the array's end) and the block words with no block's form after them (none of either in the game's clips); it is
    /// 1 when the array itself can't be placed, or doesn't end at the curves' wrapper. The header's block count (<see cref="HeaderCount"/>)
    /// must equal the block words found: a block whose word isn't exact is found by no search, so a count that differs adds 1, and so
    /// does a count of 0 with room for a block after the count (no game clip holds a count of 0). An array
    /// without the count's marker is read only in its header-only form (<see cref="HeaderOnlyLength"/> bytes, no block word, no
    /// <c>FF FF FF</c>); any other such array is unread (1). On the game's clips every count equals the blocks found, and every array
    /// without the marker is of the header-only form.</summary>
    public static List<ArrayTimeList> Lists(ClipRecord clip, out int unread)
    {
        unread = 0;
        var r = new List<ArrayTimeList>();
        if (ArrayOf(clip) is not { } arr || (clip.FirstCurve > 0 && arr.At + arr.Length != clip.FirstCurve - 12)) { unread = 1; return r; }
        if (arr.Length == 0) return r;
        int lo = arr.At;
        var s = clip.Bytes.AsSpan(lo, arr.Length);
        int? count = HeaderCount(clip);
        if (count is null)
        {
            // no count to check against: only the header-only form, which holds no block, reads
            ReadOnlySpan<byte> blockHead = [0xFF, 0xFF, 0xFF];
            if (s.Length != HeaderOnlyLength || s.IndexOf(BlockWord) >= 0 || s.IndexOf(blockHead) >= 0) unread = 1;
            return r;
        }
        for (int at = s.IndexOf(BlockWord); at >= 0;)
        {
            int next = at + 1;
            if (at + 10 < s.Length && s[at + 7] == 0xFF && s[at + 8] == 0xFF && s[at + 9] == 0xFF
                && Varint(s[(at + 10)..], out int nb) is { } l && l > 0 && at + 10L + nb + l <= s.Length
                && Read(s.Slice(at + 10 + nb, l)) is { } form && Followed(s, at + 10 + nb + l))
            {
                int p = at + 10 + nb;
                var (off, width, n) = form;
                var v = new int[n];
                var pl = s.Slice(p, l);
                for (int i = 0; i < n; i++) v[i] = width == 1 ? pl[off + i] : BinaryPrimitives.ReadUInt16LittleEndian(pl[(off + 2 * i)..]);
                r.Add(new ArrayTimeList(lo + at, lo + p + off, width, v));
                next = p + l;   // the payload is the block's own: a word inside it isn't a block
            }
            else unread++;   // the word without a block of this form after it: what it holds can't be told
            int nx = next < s.Length ? s[next..].IndexOf(BlockWord) : -1;
            at = nx < 0 ? -1 : next + nx;
        }
        // a block whose word was changed is found by no search: the header's count is what still tells it is there
        if (count != r.Count + unread) unread++;
        // a count of 0 (which no game clip holds) with room after the count for a block whose word was changed can't be told from such a
        // block: it is read only when the bytes after the count are too few to hold one
        else if (count == 0)
        {
            int first = s.IndexOf(BlockWord);
            int countAt = (first < 0 ? s : s[..first]).IndexOf(CountMarker) + CountMarker.Length;
            if (s.Length - (countAt + 1) >= SmallestBlock) unread++;
        }
        return r;
    }

    /// <summary>The fewest bytes a block takes: the word (6), its byte, <c>FF FF FF</c>, a one-byte length and a payload of a form byte
    /// and an empty u8 list (2).</summary>
    const int SmallestBlock = 6 + 1 + 3 + 1 + 2;

    /// <summary>A LEB128 varint at the start of <paramref name="s"/> and the bytes it takes; null when it runs past <paramref name="s"/>,
    /// takes more than 5 bytes, is overlong (a last byte of 0 after the first) or is above <see cref="int.MaxValue"/>.</summary>
    static int? Varint(ReadOnlySpan<byte> s, out int bytes)
    {
        long v = 0;
        for (int i = 0; i < 5 && i < s.Length; i++)
        {
            byte x = s[i];
            v |= (long)(x & 0x7F) << (7 * i);
            if ((x & 0x80) != 0) continue;
            bytes = i + 1;
            return (i > 0 && x == 0) || v > int.MaxValue ? null : (int)v;
        }
        bytes = 0;
        return null;
    }

    /// <summary>Whether the payload ending at <paramref name="end"/> is followed by <c>02 00</c> and a block word, or by the array's end.</summary>
    static bool Followed(ReadOnlySpan<byte> s, int end) =>
        end == s.Length || (end + 8 <= s.Length && s[end] == 2 && s[end + 1] == 0 && s.Slice(end + 2, 6).SequenceEqual(BlockWord));

    /// <summary>A payload's list: the offset of its first value, its width and count; null when the payload doesn't fit its form exactly.
    /// Every read is checked against the payload's end.</summary>
    static (int Off, int Width, int Count)? Read(ReadOnlySpan<byte> pl)
    {
        if (pl.Length < 2) return null;
        int form = pl[0], off, width, n;
        if (form is 0 or 2) { n = pl[1]; off = 2; width = 1; }
        else if (form is 1 or 3)
        {
            if (pl.Length < 4 || pl[1] != 0) return null;
            n = BinaryPrimitives.ReadUInt16LittleEndian(pl[2..]);
            off = 4; width = 2;
        }
        else return null;
        long end = off + (long)width * n;
        long size = form is 0 or 1 ? end + n : ((end + 3) & ~3L) + 4L * n;
        return size == pl.Length ? (off, width, n) : null;
    }

    /// <summary>Whether the clip's byte array holds a list with a time above 0, or a block (or the array) that can't be read: a change of
    /// the clip's length would leave such times out of step.</summary>
    public static bool HoldsTimes(ClipRecord clip)
    {
        var l = Lists(clip, out int unread);
        return unread > 0 || l.Any(x => x.HoldsTime);
    }
}
