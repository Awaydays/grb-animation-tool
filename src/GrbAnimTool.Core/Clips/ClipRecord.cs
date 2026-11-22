using System.Text;
using GrbAnimTool.Core.IO;

namespace GrbAnimTool.Core.Clips;

/// <summary>An animation clip record (type 0x0FA3067F, the CRC-32 of "Animation"), parsed as far as the read path needs: the head, the
/// duration and hash words, the track list, and the curves walked by the game's curve rule.
/// <para>The record, as a datafile holds it: u32 type, u32 length, u32 name length, the name; then a byte 1, a byte 2, the head's entry
/// count at +4 and its 12-byte entries from +8; then the ID (u64), the type (u32), a byte, the duration (f32 seconds) and two hash words.
/// The track list follows as objects (one of class 0x0181EFE8, then one of class 0x653CAA76 per track, each holding the track's hash). After
/// the last track stand two bytes of 0 and a counted byte array; then the curves' wrapper: a skipped u32, the length of what follows it,
/// the curve count, and the curves, which run to the record's end. Track i is animated by curve i.</para></summary>
public sealed class ClipRecord
{
    public const uint Type = 0x0FA3067F;

    /// <summary>The ticks per second of the game's clips: key times are in 1/60 s.</summary>
    public const int TicksPerSecond = 60;

    public byte[] Bytes { get; private init; } = [];
    public string Name { get; private init; } = "";
    public ulong Id { get; private init; }
    /// <summary>The ID's offset in <see cref="Bytes"/>; everything before it is the type, lengths, name and head.</summary>
    public int IdAt { get; private init; }
    public int HeadEntries { get; private init; }
    public float Duration { get; private init; }
    public uint Word1 { get; private init; }
    public uint Word2 { get; private init; }
    /// <summary>The word of the object heading the track list, and its count field.</summary>
    public uint TrackListWord { get; private init; }
    public int TrackListCount { get; private init; }
    /// <summary>The track hashes in order. A bone's track is the CRC-32 of its name; track 0 is the mover; a hash may stand twice (a
    /// rotation and a position track).</summary>
    public IReadOnlyList<uint> Tracks { get; private init; } = [];
    public IReadOnlyList<Curve> Curves { get; private init; } = [];
    public int FirstCurve { get; private init; }
    /// <summary>Why the walk stopped; <see cref="CurveWalker.AtEnd"/> when every curve was walked and the last ended at the record's end.</summary>
    public string Stop { get; private init; } = "";

    /// <summary>Every curve was walked, the last ended at the record's end, there is one curve per track, and the duration is in range.</summary>
    public bool WalkedToEnd => Stop == CurveWalker.AtEnd;

    /// <summary>The longest duration a clip may state, in seconds. The game's longest clip lasts 151.4 s in the animation datafile
    /// (fm_std_little_starts_stop_ale) and 261 s in all its clip forges (HomeSteader_BodyClips_Body_Streamed). A duration that isn't finite, is 0 or less, or is over this bound is refused, so no loop over a clip's ticks (at most
    /// 36,000) can run away.</summary>
    public const float MaxDuration = 600;

    /// <summary>Whether <see cref="Duration"/> is finite, above 0 and at most <see cref="MaxDuration"/>.</summary>
    public bool DurationValid => float.IsFinite(Duration) && Duration > 0 && Duration <= MaxDuration;

    /// <summary>The clip's length in ticks: its duration x 60, rounded (at most 36,000). The last key of a curve stands at most here. 0 when
    /// the duration is out of range.</summary>
    public int LastTick => DurationValid ? (int)MathF.Round(Duration * TicksPerSecond) : 0;

    /// <summary>Parses a whole clip record. A head that can't be read throws <see cref="InvalidDataException"/>; curves that don't walk to the
    /// end, a curve count other than the track count, or a duration out of range give a record whose <see cref="Stop"/> says why.</summary>
    public static ClipRecord Parse(byte[] b)
    {
        int nl = Bin.I32(b, 8, "clip name length");
        if (nl < 0) throw new InvalidDataException($"clip name length {nl}");
        Bin.Need(b, 12, nl, "clip name");
        string name = Encoding.ASCII.GetString(b, 12, nl);
        int o = 12 + nl;
        int n = Bin.U8(b, o + 4, "clip head entry count");
        long q = o + 8 + 12L * n;      // the ID
        ulong id = Bin.U64(b, q, "clip ID");
        float dur = Bin.F32(b, q + 13, "clip duration");
        uint w1 = Bin.U32(b, q + 17, "clip hash word 1");
        uint w2 = Bin.U32(b, q + 21, "clip hash word 2");
        var objs = RecordObjects.Objects(b);
        var trackObjs = objs.Where(x => x.Class == RecordObjects.TrackClass).ToList();
        var listObj = objs.LastOrDefault(x => x.Class == RecordObjects.TrackListClass);
        var rec = new ClipRecord { Bytes = b, Name = name, Id = id, IdAt = (int)q, HeadEntries = n, Duration = dur, Word1 = w1, Word2 = w2 };
        if (!rec.DurationValid) return rec.With(stop: $"the duration {dur} s is outside (0, {MaxDuration}] s");
        if (listObj.Class == 0) return rec.With(stop: "no track list object");
        uint listWord = Bin.U32(b, listObj.At + 12, "track list word");
        int listCount = Bin.I32(b, listObj.At + 16, "track list count");
        var tracks = new List<uint>(trackObjs.Count);
        foreach (var t in trackObjs) tracks.Add(Bin.U32(b, t.At + 12, "track hash"));
        int after = trackObjs.Count > 0 ? trackObjs[^1].At + 16 : listObj.At + 20;
        rec = rec.With(listWord: listWord, listCount: listCount, tracks: tracks);
        if (after + 6 > b.Length) return rec.With(stop: "no curve data after the track list");
        int blob = Bin.I32(b, after + 2, "byte array length");
        long p = after + 6L + blob;
        if (blob < 0 || p + 12 > b.Length) return rec.With(stop: "no curve data after the byte array");
        uint rest = Bin.U32(b, p + 4, "curve data length");
        uint count = Bin.U32(b, p + 8, "curve count");
        // the game's wrapper: the second word is the length of everything after it
        if (rest != b.Length - (p + 8)) return rec.With(stop: $"the curve data's length field {rest} doesn't match the {b.Length - (p + 8)} B after it");
        int cur = (int)p + 12;
        var curves = CurveWalker.Walk(b, cur, count > int.MaxValue ? -1 : (int)count, out _, out string stop);
        // track i is animated by curve i: with the counts apart, the pairing of curves and tracks isn't known
        if (stop == CurveWalker.AtEnd && curves.Count != tracks.Count) stop = $"{curves.Count} curves for {tracks.Count} tracks";
        return rec.With(curves: curves, firstCurve: cur, stop: stop);
    }

    ClipRecord With(uint? listWord = null, int? listCount = null, List<uint>? tracks = null, List<Curve>? curves = null, int? firstCurve = null, string? stop = null) => new()
    {
        Bytes = Bytes, Name = Name, Id = Id, IdAt = IdAt, HeadEntries = HeadEntries, Duration = Duration, Word1 = Word1, Word2 = Word2,
        TrackListWord = listWord ?? TrackListWord, TrackListCount = listCount ?? TrackListCount, Tracks = tracks ?? Tracks,
        Curves = curves ?? Curves, FirstCurve = firstCurve ?? FirstCurve, Stop = stop ?? Stop,
    };
}

/// <summary>The shared curve table: record 0x0BD9 of entry 0x800 ("Game Bootstrap Settings"). Clips refer to its curves by index (tag
/// 0xFE + u32, from 1). Its head is laid out as a clip's up to the ID; 13 bytes after the ID stand the length of the rest and the curve
/// count, then the curves to the record's end.</summary>
public sealed class SharedCurveTable
{
    public const ulong EntryId = 0x800;
    public const ulong RecordId = 0x0BD9;

    public byte[] Bytes { get; private init; } = [];
    public IReadOnlyList<Curve> Curves { get; private init; } = [];
    public uint DeclaredCount { get; private init; }
    public string Stop { get; private init; } = "";
    public bool WalkedToEnd => Stop == CurveWalker.AtEnd && Curves.Count == DeclaredCount;

    /// <summary>Parses the table record. A head that can't be read throws <see cref="InvalidDataException"/>; curves that don't walk to the
    /// end give a table whose <see cref="WalkedToEnd"/> is false.</summary>
    public static SharedCurveTable Parse(byte[] b)
    {
        int nl = Bin.I32(b, 8, "table name length");
        if (nl < 0) throw new InvalidDataException($"table name length {nl}");
        int o = 12 + nl;
        int n = Bin.U8(b, o + 4, "table head entry count");
        long q = o + 8 + 12L * n + 13;
        Bin.U32(b, q, "table length");
        uint count = Bin.U32(b, q + 4, "table curve count");
        var cs = CurveWalker.Walk(b, (int)q + 8, count > int.MaxValue ? -1 : (int)count, out _, out string stop);
        return new SharedCurveTable { Bytes = b, Curves = cs, DeclaredCount = count, Stop = stop };
    }

    /// <summary>What a curve is once a reference is followed; null when the reference can't be read. An inline curve is itself.</summary>
    public static Curve? Resolve(Curve c, SharedCurveTable? table)
    {
        if (!c.IsReference) return c;
        if (table is null || c.Ref == 0 || c.Ref > table.Curves.Count) return null;
        return table.Curves[(int)c.Ref - 1];
    }
}
