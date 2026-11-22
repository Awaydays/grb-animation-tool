namespace GrbAnimTool.Core.Lzo;

/// <summary>The LZO1X decoder for the game's graph documents. Written from a description of the stream format, not from any reference
/// implementation. Every read is checked against the end of its input and every write against the end of its output, so a damaged or
/// truncated stream throws instead of running on: a length extension's run of zero bytes stops at the input's end and at the output's room.</summary>
public static class Lzo1x
{
    /// <summary>What the previous instruction left behind: it decides what a lead byte 0-15 means.</summary>
    enum After { Start, Plain, ShortTail, LongTail }

    /// <summary>Decodes one stream to exactly <paramref name="outLen"/> bytes. A malformed stream throws <see cref="InvalidDataException"/>
    /// whose message names its class (input-overrun, output-overrun, lookbehind, bad-end, input-left, output-short). The output grows as
    /// the stream decodes, up to the stated length, so a damaged stated length costs no large buffer before the stream fails.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> src, int outLen)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outLen);
        var r = new Unpacker(src, Initial(src, outLen), outLen, exact: true);
        r.Run();
        return r.Result();
    }

    /// <summary>One stream whose decoded length isn't stated: decoded to its end marker, at most <paramref name="maxLen"/> bytes (more is
    /// output-overrun). The output grows as the stream decodes, so a short stream costs no large buffer.</summary>
    public static byte[] DecompressAtMost(ReadOnlySpan<byte> src, int maxLen)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLen);
        var r = new Unpacker(src, Initial(src, maxLen), maxLen, exact: false);
        r.Run();
        return r.Result();
    }

    /// <summary>The output's first size: four times the stream (the documents decode to about 2.4 times), within the cap.</summary>
    static int Initial(ReadOnlySpan<byte> src, int cap) => (int)Math.Min(cap, Math.Max(256L, src.Length * 4L));

    static InvalidDataException Bad(string cls, string what) => new($"LZO1X stream: {cls}: {what}");

    ref struct Unpacker(ReadOnlySpan<byte> src, int initial, int cap, bool exact)
    {
        readonly ReadOnlySpan<byte> s = src;
        byte[] Out = new byte[initial];
        int at;      // the next stream byte
        int made;    // output bytes produced so far

        /// <summary>How many more bytes the output may take.</summary>
        readonly int Room => cap - made;

        /// <summary>Makes the output array hold <paramref name="n"/> more bytes (already checked against <see cref="Room"/>).</summary>
        void Grow(long n)
        {
            if (made + n <= Out.Length) return;
            long size = Math.Min(cap, Math.Max(made + n, Out.Length * 2L));
            Array.Resize(ref Out, (int)size);
        }

        public readonly byte[] Result() => made == Out.Length ? Out : Out.AsSpan(0, made).ToArray();

        byte Next(string what)
        {
            if (at >= s.Length) throw Bad("input-overrun", $"{what} needed at stream byte {at} of {s.Length}");
            return s[at++];
        }

        /// <summary>A length extension: zero bytes worth 255 each, closed by a non-zero byte that is added to the base. Each zero byte is
        /// read through <see cref="Next"/>, so the run stops at the input's end, and the count is checked against the output's room.</summary>
        long Extended(int start)
        {
            long zeros = 0;
            while (true)
            {
                byte b = Next("a length extension byte");
                if (b != 0) return start + 255 * zeros + b;
                zeros++;
                if (start + 255 * zeros > Room) throw Bad("output-overrun", $"a length extension already counts {start + 255 * zeros} bytes with {Room} left at output {made}");
            }
        }

        void Literals(long n, string what)
        {
            if (n > Room) throw Bad("output-overrun", $"{what} of {n} byte(s) with {Room} left at output {made}");
            if (n > s.Length - at) throw Bad("input-overrun", $"{what} of {n} byte(s) with {s.Length - at} stream byte(s) left");
            Grow(n);
            s.Slice(at, (int)n).CopyTo(Out.AsSpan(made));
            at += (int)n; made += (int)n;
        }

        void Copy(long length, int distance)
        {
            if (distance > made) throw Bad("lookbehind", $"a copy {distance} back with {made} byte(s) produced");
            if (length > Room) throw Bad("output-overrun", $"a copy of {length} byte(s) with {Room} left at output {made}");
            Grow(length);
            for (int k = 0; k < length; k++, made++) Out[made] = Out[made - distance];   // one byte at a time, so an overlapping copy repeats its bytes
        }

        /// <summary>T literals after a copy; the context that follows.</summary>
        After Tail(int t)
        {
            Literals(t, "trailing literals");
            return t == 0 ? After.Plain : After.ShortTail;
        }

        public void Run()
        {
            var ctx = After.Start;
            while (true)
            {
                int lead = Next("a lead byte");
                if (ctx == After.Start && lead >= 18)   // a first literal run
                {
                    int n = lead - 17;
                    Literals(n, "a first literal run");
                    ctx = n <= 3 ? After.ShortTail : After.LongTail;
                }
                else if (lead < 16)
                {
                    if (ctx is After.Start or After.Plain)   // a literal run
                    {
                        long n = lead == 0 ? Extended(18) : lead + 3;
                        Literals(n, "a literal run");
                        ctx = After.LongTail;
                    }
                    else   // the 2-byte near copy (after a short tail), the 3-byte copy (after a long one)
                    {
                        int b = Next("a distance byte");
                        bool near = ctx == After.ShortTail;
                        Copy(near ? 2 : 3, (near ? 1 : 2049) + (lead >> 2) + 4 * b);
                        ctx = Tail(lead & 3);
                    }
                }
                else if (lead >= 64)   // the short copy
                {
                    int b = Next("a distance byte");
                    Copy((lead >> 5) + 1, 1 + ((lead >> 2) & 7) + 8 * b);
                    ctx = Tail(lead & 3);
                }
                else if (lead >= 32)   // the middle copy
                {
                    int field = lead & 31;
                    long n = field == 0 ? Extended(33) : field + 2;
                    int v = Next("a distance byte"); v |= Next("a distance byte") << 8;
                    Copy(n, (v >> 2) + 1);
                    ctx = Tail(v & 3);
                }
                else   // the far copy, or the end marker
                {
                    int high = (lead >> 3) & 1, field = lead & 7;
                    long n = field == 0 ? Extended(9) : field + 2;
                    int v = Next("a distance byte"); v |= Next("a distance byte") << 8;
                    int d = v >> 2;
                    if (high == 0 && d == 0)
                    {
                        if (lead != 0x11 || v != 0) throw Bad("bad-end", $"an end marker that is not exactly 11 00 00 (lead {lead}, value {v}) at stream byte {at - 3}");
                        if (at != s.Length) throw Bad("input-left", $"{s.Length - at} byte(s) after the end marker");
                        if (exact && made != cap) throw Bad("output-short", $"the end marker after {made} of {cap} byte(s)");
                        return;
                    }
                    Copy(n, 16384 + 16384 * high + d);
                    ctx = Tail(v & 3);
                }
            }
        }
    }
}
