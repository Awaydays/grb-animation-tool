using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Diagnostics;

/// <summary>How far the core reads a curve's values.</summary>
public enum CurveRead
{
    /// <summary>A reader decodes the values: rotations and positions (checked at key 0), foot plants (form 22).</summary>
    Decoded,
    /// <summary>The walker sizes the curve and the values' layout is known (form 13, one byte a key; form 18, four bytes a key), but the
    /// core has no reader for the values yet.</summary>
    SizedNotRead,
    /// <summary>The walker sizes the curve, but what its values mean isn't known.</summary>
    NotRead,
}

/// <summary>A census of an install's clips: how many walk to their end, whether every rotation and position curve decodes at key 0, the
/// curve forms by tag, and the long clips. A check of the decoder's coverage on the user's own files, and a regression test on the game's.</summary>
public sealed class ClipCensus
{
    public int Records, ClipRecords, ParseFailures, Walked, NotWalked, UnresolvedReferences;
    /// <summary>Walked clips whose every rotation and position curve decodes to a value at key 0.</summary>
    public int FullyDecoded;
    /// <summary>Walked clips with no curve of <see cref="CurveRead.NotRead"/>: every curve is decoded or at least of a sized, known layout.</summary>
    public int AllKindsDecodedOrSized;
    public int ClipsOver255Ticks, ClipsWithCurvesOf256Keys, CurveUsesOf256Keys;
    public int MoverPresent, MoverDecoded;
    public int SharedTableCurves;
    public bool SharedTableAtEnd;
    /// <summary>The longest duration among the parsed clips (seconds), and that clip's name.</summary>
    public float LongestDuration;
    public string LongestName = "";
    /// <summary>Curve uses by tag (a shared-table curve used by several clips counts once per use), and clips holding each tag.</summary>
    public readonly SortedDictionary<byte, long> TagCurves = [];
    public readonly SortedDictionary<byte, int> TagClips = [];
    /// <summary>Curve uses by kind: decoded, sized but not read, not read.</summary>
    public readonly SortedDictionary<TrackKind, (long Decoded, long SizedNotRead, long NotRead)> ByKind = [];
    public readonly List<string> NotWalkedNames = [];
    public readonly List<string> PartlyDecodedNames = [];

    /// <summary>A curve's kind for the census, and how far the core reads it: rotations and positions by their decoders at key 0; plants
    /// (form 22) by their reader; byte (form 13) and float (form 18) tracks sized only, no reader yet; kind 4 and every other form not read.</summary>
    public static (TrackKind Kind, CurveRead Read) Classify(Curve u)
    {
        var kind = u.Kind;
        var read = kind switch
        {
            TrackKind.Rotation => CurveDecoder.KeyRot(u) is not null ? CurveRead.Decoded : CurveRead.NotRead,
            TrackKind.Position => CurveDecoder.KeyPos(u) is not null ? CurveRead.Decoded : CurveRead.NotRead,
            TrackKind.Plant when u.Form == 22 => CurveRead.Decoded,
            TrackKind.Byte when u.Form == 13 => CurveRead.SizedNotRead,
            TrackKind.Float when u.Form == 18 => CurveRead.SizedNotRead,
            _ => CurveRead.NotRead,
        };
        return (kind, read);
    }

    public static ClipCensus Run(GameInstall g) => Run(g.Records.Count, g.Clips, g.SharedTable);

    public static ClipCensus Run(int records, IEnumerable<ClipEntry> clips, SharedCurveTable table)
    {
        var x = new ClipCensus { Records = records, SharedTableCurves = table.Curves.Count, SharedTableAtEnd = table.WalkedToEnd };
        foreach (var e in clips)
        {
            x.ClipRecords++;
            var c = e.Record;
            if (c is null) { x.ParseFailures++; continue; }
            if (c.Duration > x.LongestDuration) { x.LongestDuration = c.Duration; x.LongestName = e.Name; }
            if (!c.WalkedToEnd)
            {
                x.NotWalked++;
                if (x.NotWalkedNames.Count < 20) x.NotWalkedNames.Add($"{e.Name} 0x{e.Id:X16}: {c.Stop}");
                continue;
            }
            x.Walked++;
            if (c.Duration * 60 > 255.5) x.ClipsOver255Ticks++;
            bool boneOk = true, allOk = true, over = false, mHas = false, mOk = true;
            var seen = new HashSet<byte>();
            for (int i = 0; i < c.Curves.Count; i++)
            {
                var u = SharedCurveTable.Resolve(c.Curves[i], table);
                bool mover = c.Tracks[i] == 0;
                if (mover) mHas = true;
                if (u is null) { x.UnresolvedReferences++; boneOk = allOk = false; if (mover) mOk = false; continue; }
                x.TagCurves[u.Tag] = x.TagCurves.GetValueOrDefault(u.Tag) + 1;
                seen.Add(u.Tag);
                if (u.Keys >= 256) { x.CurveUsesOf256Keys++; over = true; }
                var (kind, read) = Classify(u);
                var kd = x.ByKind.GetValueOrDefault(kind);
                x.ByKind[kind] = read switch
                {
                    CurveRead.Decoded => (kd.Decoded + 1, kd.SizedNotRead, kd.NotRead),
                    CurveRead.SizedNotRead => (kd.Decoded, kd.SizedNotRead + 1, kd.NotRead),
                    _ => (kd.Decoded, kd.SizedNotRead, kd.NotRead + 1),
                };
                if (read == CurveRead.NotRead)
                {
                    allOk = false;
                    if (kind is TrackKind.Rotation or TrackKind.Position) { boneOk = false; if (mover) mOk = false; }
                }
            }
            foreach (var t in seen) x.TagClips[t] = x.TagClips.GetValueOrDefault(t) + 1;
            if (over) x.ClipsWithCurvesOf256Keys++;
            if (boneOk) x.FullyDecoded++;
            else if (x.PartlyDecodedNames.Count < 25) x.PartlyDecodedNames.Add($"{e.Name} 0x{e.Id:X16}");
            if (allOk) x.AllKindsDecodedOrSized++;
            if (mHas) { x.MoverPresent++; if (mOk) x.MoverDecoded++; }
        }
        return x;
    }

    /// <summary>The census as lines of text.</summary>
    public IEnumerable<string> Lines()
    {
        yield return $"records {Records}, clip records {ClipRecords}; shared table: {SharedTableCurves} curves, to its end: {SharedTableAtEnd}";
        yield return $"parse failures {ParseFailures}; walked to the end {Walked}; not walked {NotWalked}; unresolved references {UnresolvedReferences}";
        yield return $"clips whose every rotation and position curve decodes: {FullyDecoded} of {Walked} walked";
        yield return $"clips whose every curve is decoded or sized with a known layout (byte and float values aren't read yet): {AllKindsDecodedOrSized} of {Walked}";
        yield return $"mover (track 0) present in {MoverPresent} clips, decoded in {MoverDecoded}";
        yield return $"longest clip: {LongestName} {LongestDuration:F3} s; clips longer than 255 ticks: {ClipsOver255Ticks}; clips with a curve of 256 keys or more: {ClipsWithCurvesOf256Keys} ({CurveUsesOf256Keys} curve uses)";
        yield return "curve uses by kind (decoded / sized, values not read / not read): " + string.Join(", ", ByKind.Select(k => $"{k.Key} {k.Value.Decoded}/{k.Value.SizedNotRead}/{k.Value.NotRead}"));
        yield return "curve uses by tag (form, time form, uses, clips): " + string.Join(", ", TagCurves.Select(t => $"0x{t.Key:X2} ({t.Key / 3},{t.Key % 3}) {t.Value} in {TagClips.GetValueOrDefault(t.Key)}"));
        foreach (var s in NotWalkedNames) yield return "not walked: " + s;
        foreach (var s in PartlyDecodedNames) yield return "a rotation or position curve not decoded: " + s;
    }
}
