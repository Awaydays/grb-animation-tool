using System.Security.Cryptography;

namespace GrbAnimTool.Core.Hashing;

/// <summary>SHA-256 as lower-case hex, of a stream (from its current position to its end) or of bytes.</summary>
public static class Sha256Hex
{
    public static string Of(Stream s) => Convert.ToHexStringLower(SHA256.HashData(s));
    public static string Of(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(SHA256.HashData(b));

    public static string OfFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Of(fs);
    }
}

/// <summary>CRC-32 (the reflected polynomial 0xEDB88320, initial and final value 0xFFFFFFFF). The game names bones, tracks and record
/// types by the CRC-32 of their names: "Animation" gives 0x0FA3067F, "Skeleton" 0x24AECB7C.</summary>
public static class Crc32
{
    static readonly uint[] Table = MakeTable();

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320 & (uint)-(int)(c & 1));
            t[i] = c;
        }
        return t;
    }

    public static uint Of(ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in b) c = (c >> 8) ^ Table[(c ^ x) & 0xFF];
        return ~c;
    }

    /// <summary>The CRC-32 of a name, each character taken as one byte (names are ASCII).</summary>
    public static uint Of(string s)
    {
        uint c = 0xFFFFFFFF;
        foreach (char ch in s) c = (c >> 8) ^ Table[(c ^ (byte)ch) & 0xFF];
        return ~c;
    }
}
