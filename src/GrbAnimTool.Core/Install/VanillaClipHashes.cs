using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GrbAnimTool.Core.Install;

/// <summary>Where a clip of the user's install stands against the game's own files.</summary>
public enum ClipStatus
{
    /// <summary>No table of the game's clips is built into this copy of the tool.</summary>
    Unknown,
    /// <summary>The record's bytes are the game's own.</summary>
    Vanilla,
    /// <summary>The game has a clip with this ID, but the record's bytes differ: something replaced it.</summary>
    Replaced,
    /// <summary>The game has no clip with this ID: something added it.</summary>
    Added,
}

/// <summary>The SHA-256 of every clip record of the game's own animation datafile, by clip ID: hashes only, no game data. The hash is
/// over the whole record as the datafile holds it (type, length, name length, name and body). Built by the harness's hash-table command
/// from a vanilla forge and embedded in the core.
/// <para>Format: text, one line per clip sorted by ID: the ID as 16 hex digits, a space, the SHA-256 as 64 hex digits. Lines starting
/// with # are comments.</para></summary>
public sealed class VanillaClipHashes
{
    public const string ResourceName = "GrbAnimTool.Core.vanilla-clips.txt";
    const string Header = "# GRB Anim Tool: the game's own clip records, by clip ID (hex), with the SHA-256 of each whole record";

    readonly Dictionary<ulong, byte[]> _hashes;

    VanillaClipHashes(Dictionary<ulong, byte[]> h) => _hashes = h;

    public int Count => _hashes.Count;
    public IEnumerable<ulong> Ids => _hashes.Keys;

    /// <summary>The table built into the core, or null when this build has none.</summary>
    public static VanillaClipHashes? Embedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return s is null ? null : Read(s);
    }

    public static VanillaClipHashes Read(Stream s)
    {
        using var r = new StreamReader(s, Encoding.ASCII);
        var d = new Dictionary<ulong, byte[]>();
        int n = 0;
        while (r.ReadLine() is { } line)
        {
            n++;
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.Length != 81 || line[16] != ' ' || !ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id))
                throw new InvalidDataException($"clip hash table, line {n}: not an ID and a SHA-256");
            byte[] h;
            try { h = Convert.FromHexString(line.AsSpan(17)); }
            catch (FormatException) { throw new InvalidDataException($"clip hash table, line {n}: not a SHA-256"); }
            if (!d.TryAdd(id, h)) throw new InvalidDataException($"clip hash table, line {n}: ID {id:X16} twice");
        }
        return new VanillaClipHashes(d);
    }

    public static void Write(Stream s, IEnumerable<(ulong Id, byte[] Sha256)> entries)
    {
        var list = entries.OrderBy(x => x.Id).ToList();
        if (list.Select(x => x.Id).Distinct().Count() != list.Count) throw new ArgumentException("duplicate clip IDs");
        var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine(Header);
        foreach (var (id, sha) in list)
        {
            if (sha.Length != 32) throw new ArgumentException("a SHA-256 is 32 bytes");
            w.WriteLine($"{id:X16} {Convert.ToHexStringLower(sha)}");
        }
        w.Flush();
    }

    public bool Contains(ulong id) => _hashes.ContainsKey(id);

    public ClipStatus StatusOf(ulong id, ReadOnlySpan<byte> record)
    {
        if (!_hashes.TryGetValue(id, out var h)) return ClipStatus.Added;
        Span<byte> mine = stackalloc byte[32];
        SHA256.HashData(record, mine);
        return mine.SequenceEqual(h) ? ClipStatus.Vanilla : ClipStatus.Replaced;
    }
}
