using GrbAnimTool.Core.Lzo;

namespace GrbAnimTool.Tests;

/// <summary>The LZO1X decoder on hand-made streams: each instruction form decodes to its bytes, and every damaged stream (cut short,
/// garbage, a run of zero bytes to the input's end, a copy before any output, a bad end) throws <see cref="InvalidDataException"/> within a
/// time limit, never another exception or a hang.</summary>
public sealed class LzoTests
{
    static readonly byte[] End = [0x11, 0, 0];
    static byte[] B(params byte[][] parts) => [.. parts.SelectMany(p => p)];
    static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    /// <summary>"abc", then a short copy of 6 bytes 3 back (lead 0xA8: length field 5, distance field 2), then the end marker.</summary>
    static readonly byte[] ShortCopy = B([0x14], Ascii("abc"), [0xA8, 0x00], End);
    /// <summary>"abcd", then a middle copy of 8 bytes 4 back (lead 0x26, distance word 0x000C).</summary>
    static readonly byte[] MiddleCopy = B([0x15], Ascii("abcd"), [0x26, 0x0C, 0x00], End);

    [Fact]
    public void TheInstructionFormsDecode()
    {
        Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(Lzo1x.Decompress(B([0x14], Ascii("abc"), End), 3)));
        Assert.Equal("abcabcabc", System.Text.Encoding.ASCII.GetString(Lzo1x.Decompress(ShortCopy, 9)));
        Assert.Equal("abcdabcdabcd", System.Text.Encoding.ASCII.GetString(Lzo1x.Decompress(MiddleCopy, 12)));
        // a literal run with one zero byte in its length extension: 18 + 255 + 27 = 300 bytes
        var data = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        Assert.Equal(data, Lzo1x.Decompress(B([0x00, 0x00, 27], data, End), 300));
        Assert.Equal(data, Lzo1x.DecompressAtMost(B([0x00, 0x00, 27], data, End), 1000));
        Assert.Equal("abcabcabc", System.Text.Encoding.ASCII.GetString(Lzo1x.DecompressAtMost(ShortCopy, 9)));
    }

    /// <summary>A literal run of <paramref name="data"/> at the stream's start, without the end marker.</summary>
    static byte[] Run(byte[] data) => GraphSynthetic.Literal(data)[..^3];

    static byte[] Data(int n) => [.. Enumerable.Range(0, n).Select(i => (byte)(i * 31 + i / 256))];

    /// <summary>The copies only the game's documents exercised before: the 2-byte near copy after a short literal tail, the 3-byte copy
    /// after a literal run of 4 or more (2049 back and more), and the far copy (16 KiB back and more, both of its high bits).</summary>
    [Fact]
    public void TheNearAndFarCopiesDecode()
    {
        // "abc" (a first run of 3: a short tail), then lead 0x04 and 0: a 2-byte copy 1 + 1 + 0 = 2 back
        Assert.Equal("abcbc", System.Text.Encoding.ASCII.GetString(Lzo1x.Decompress(B([0x14], Ascii("abc"), [0x04, 0x00], End), 5)));
        // the same after a short copy's tail of 2 literals: "abc", 6 bytes 3 back with 2 literals "xy" (lead 0xAA), then lead 0x0C and 1:
        // 2 bytes 1 + 3 + 4 = 8 back
        var near = B([0x14], Ascii("abc"), [0xAA, 0x00], Ascii("xy"), [0x0C, 0x01], End);
        Assert.Equal("abcabcabcxyab", System.Text.Encoding.ASCII.GetString(Lzo1x.Decompress(near, 13)));

        var big = Data(2100);   // a literal run of 2100 (a long tail), then lead 0x04 and 1: 3 bytes 2049 + 1 + 4 = 2054 back
        var want = B(big, big[(2100 - 2054)..(2100 - 2054 + 3)]);
        Assert.Equal(want, Lzo1x.Decompress(B(Run(big), [0x04, 0x01], End), want.Length));

        foreach (int high in new[] { 0, 1 })
        {
            int distance = 16384 + 16384 * high + 6;
            var far = Data(distance + 50);
            // lead 0x10 | high << 3 | (5 - 2): 5 bytes; the distance word d << 2 with no tail
            want = B(far, far[(far.Length - distance)..(far.Length - distance + 5)]);
            Assert.Equal(want, Lzo1x.Decompress(B(Run(far), [(byte)(0x10 | high << 3 | 3), 6 << 2, 0], End), want.Length));
            Assert.Equal(want, Lzo1x.DecompressAtMost(B(Run(far), [(byte)(0x10 | high << 3 | 3), 6 << 2, 0], End), 1 << 20));
        }
    }

    static string Class(Action a)
    {
        try { a(); return "decoded"; }
        catch (InvalidDataException ex) { return ex.Message.Split(':')[1].Trim(); }
    }

    [Fact(Timeout = 10_000)]
    public async Task DamagedStreamsThrowTheirClass()
    {
        await Task.Run(() =>
        {
            Assert.Equal("output-short", Class(() => Lzo1x.Decompress(ShortCopy, 10)));
            Assert.Equal("output-overrun", Class(() => Lzo1x.Decompress(ShortCopy, 8)));
            Assert.Equal("output-overrun", Class(() => Lzo1x.DecompressAtMost(ShortCopy, 8)));
            Assert.Equal("input-left", Class(() => Lzo1x.Decompress(B(ShortCopy, [0]), 9)));
            Assert.Equal("lookbehind", Class(() => Lzo1x.Decompress(B([0x14], Ascii("abc"), [0x50, 0x00], End), 6)));     // 5 back with 3 made
            Assert.Equal("lookbehind", Class(() => Lzo1x.Decompress(B([0x14], Ascii("abc"), [0xA8, 0x01], End), 9)));   // 11 back with 3 made
            Assert.Equal("bad-end", Class(() => Lzo1x.Decompress(B([0x14], Ascii("abc"), [0x11, 0x01, 0x00]), 3)));
            Assert.Equal("input-overrun", Class(() => Lzo1x.Decompress([], 0)));
        });
    }

    /// <summary>Every prefix of a valid stream is a stream cut short: each must throw.</summary>
    [Fact(Timeout = 10_000)]
    public async Task EveryCutThrows()
    {
        await Task.Run(() =>
        {
            foreach (var s in new[] { ShortCopy, MiddleCopy })
                for (int n = 0; n < s.Length; n++)
                {
                    Assert.Throws<InvalidDataException>(() => Lzo1x.Decompress(s.AsSpan(0, n), 12));
                    Assert.Throws<InvalidDataException>(() => Lzo1x.DecompressAtMost(s.AsSpan(0, n), 1 << 20));
                }
        });
    }

    /// <summary>A length extension's run of zero bytes that runs to the input's end: the loop must stop there (input-overrun) when the
    /// output has room for all it counts, and at the output's room (output-overrun) when it doesn't.</summary>
    [Fact(Timeout = 10_000)]
    public async Task ARunOfZerosStopsAtTheInputsEnd()
    {
        await Task.Run(() =>
        {
            var zeros = new byte[2_000_001];   // a literal run's lead 0, then two million zero bytes and no closing byte
            Assert.Equal("input-overrun", Class(() => Lzo1x.DecompressAtMost(zeros, int.MaxValue)));
            Assert.Equal("output-overrun", Class(() => Lzo1x.Decompress(zeros, 1 << 20)));
            // the same inside a middle copy's and a far copy's length
            Assert.Equal("input-overrun", Class(() => Lzo1x.DecompressAtMost(B([0x14], Ascii("abc"), [0x20], new byte[1_000_000]), int.MaxValue)));
            Assert.Equal("input-overrun", Class(() => Lzo1x.DecompressAtMost(B([0x14], Ascii("abc"), [0x10], new byte[1_000_000]), int.MaxValue)));
        });
    }

    /// <summary>Random bytes, many lengths: each must throw (a random stream essentially never ends exactly), never hang.</summary>
    [Fact(Timeout = 60_000)]
    public async Task GarbageThrows()
    {
        await Task.Run(() =>
        {
            var rnd = new Random(5);
            int decoded = 0;
            for (int i = 0; i < 4000; i++)
            {
                var b = new byte[rnd.Next(1, 3000)];
                rnd.NextBytes(b);
                if (i % 2 == 0) b[0] = (byte)rnd.Next(0, 18);   // half of them start with a lead byte below 18
                try { Lzo1x.Decompress(b, rnd.Next(0, 200_000)); decoded++; } catch (InvalidDataException) { }
                try { Lzo1x.DecompressAtMost(b, 1 << 22); decoded++; } catch (InvalidDataException) { }
            }
            Assert.Equal(0, decoded);
        });
    }
}
