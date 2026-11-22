using System.Buffers.Binary;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Writing;

/// <summary>A rebuild was refused: the record can't be rebuilt as asked, and nothing was made. <see cref="Exception.Message"/> says why in
/// plain words, naming the clip.</summary>
public sealed class RebuildRefusedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One of the head's arena kinds before and after a rebuild.</summary>
/// <param name="Kind">The head entry's kind: 3, 5, 7 or 9.</param>
/// <param name="Alignment">The arena's alignment: 1, 2, 4 or 8.</param>
/// <param name="Present">Whether the head holds an entry of this kind.</param>
/// <param name="Before">The head's value before (0 when absent).</param>
/// <param name="After">The value written (0 when absent).</param>
/// <param name="CurvesNeedBefore">The units the record's inline curves needed before.</param>
/// <param name="CurvesNeedAfter">The units its inline curves need now.</param>
public sealed record ArenaKind(int Kind, int Alignment, bool Present, long Before, long After, long CurvesNeedBefore, long CurvesNeedAfter);

/// <summary>A rebuilt clip record and what changed in it.</summary>
/// <param name="Bytes">The whole record (type, length, name, head, body), as a datafile holds it.</param>
/// <param name="Record">The rebuilt record, parsed again.</param>
/// <param name="Rewritten">Tracks whose curve was replaced.</param>
/// <param name="Kept">Tracks whose curve kept its bytes.</param>
/// <param name="Arenas">The head's arena kinds 3, 5, 7 and 9.</param>
public sealed record RebuiltClip(byte[] Bytes, ClipRecord Record, int Rewritten, int Kept, IReadOnlyList<ArenaKind> Arenas);

/// <summary>Rebuilds a clip record from its parsed parts with any curves replaced. The record keeps its ID, name, duration, hash words, track
/// list and every object; a track whose curve isn't replaced keeps its bytes. Only what follows from the curves' bytes is written again: the
/// record's length (from the ID to its end), the head's arena kinds and the length field of the curves' wrapper. So a rebuild with nothing
/// replaced gives the record back byte for byte.
/// <para>The head's kinds 3, 5, 7 and 9 size the arenas of alignment 1, 2, 4 and 8 that the game's curve readers write into with no check,
/// so a head holding less than the curves need would let the game write past its arena. Each kind is written as what the new curves need
/// plus whatever the head held beyond what the old curves needed (that belongs to the record's other objects). A head with no entry of a
/// kind the new curves need is refused, and so is a kind that would drop to 0 where it wasn't 0: no game clip holds such an entry.</para></summary>
public static class ClipRebuild
{
    /// <summary>The head kinds of the arenas and their alignments.</summary>
    static readonly (int Kind, int Align)[] ArenaKinds = [(3, 1), (5, 2), (7, 4), (9, 8)];

    /// <summary>Parses <paramref name="record"/> and rebuilds it; a record whose head can't be read is refused like any other.</summary>
    public static RebuiltClip Rebuild(byte[] record, SharedCurveTable? table, Func<int, uint, Curve, byte[]?>? replace = null)
    {
        ClipRecord c;
        try { c = ClipRecord.Parse(record); }
        catch (InvalidDataException ex) { throw new RebuildRefusedException($"the record can't be read: {ex.Message}", ex); }
        return Rebuild(c, table, replace);
    }

    /// <summary>Rebuilds with the curves of the tracks in <paramref name="curves"/> (track index to the new curve's bytes) replaced.</summary>
    public static RebuiltClip Rebuild(ClipRecord clip, SharedCurveTable? table, IReadOnlyDictionary<int, byte[]> curves)
    {
        foreach (int i in curves.Keys)
            if (i < 0 || i >= clip.Tracks.Count) throw new RebuildRefusedException($"{clip.Name}: there is no track {i} (the clip has {clip.Tracks.Count})");
        return Rebuild(clip, table, (i, _, _) => curves.GetValueOrDefault(i));
    }

    /// <summary>Rebuilds <paramref name="clip"/>. <paramref name="replace"/> gets each track's index, hash and curve (a reference resolved
    /// through <paramref name="table"/>) and returns the new curve's bytes, or null to keep the track's bytes; null keeps every track. A new
    /// curve may be inline or a reference (tag 0xFE + u32 index into the shared table) and must keep the track's kind. Any reason the result
    /// wouldn't be a sound record is a <see cref="RebuildRefusedException"/>.</summary>
    public static RebuiltClip Rebuild(ClipRecord clip, SharedCurveTable? table, Func<int, uint, Curve, byte[]?>? replace = null)
    {
        string name = clip.Name;
        byte[] old = clip.Bytes;
        if (!clip.WalkedToEnd) throw new RebuildRefusedException($"{name}: its curves don't read to the record's end ({clip.Stop})");
        if (clip.Curves.Count != clip.Tracks.Count) throw new RebuildRefusedException($"{name}: {clip.Curves.Count} curves for {clip.Tracks.Count} tracks");
        uint lenField = BinaryPrimitives.ReadUInt32LittleEndian(old.AsSpan(4));
        if (lenField != old.Length - clip.IdAt) throw new RebuildRefusedException($"{name}: its length field {lenField} isn't the {old.Length - clip.IdAt} B from its ID to its end");
        int first = clip.FirstCurve, lenAt = first - 8;
        if (lenAt <= clip.IdAt || first > old.Length) throw new RebuildRefusedException($"{name}: the curves' wrapper isn't where it was read");

        // the new curves, each checked on its own before it is spliced in
        var parts = new byte[clip.Curves.Count][];
        var changed = new bool[clip.Curves.Count];
        int rewritten = 0;
        for (int i = 0; i < clip.Curves.Count; i++)
        {
            var c = clip.Curves[i];
            var u = Resolve(c, table, name, i);
            byte[]? fresh = replace is null ? null : replace(i, clip.Tracks[i], u);
            if (fresh is null) { parts[i] = old.AsSpan(c.At, c.DiskLength).ToArray(); continue; }
            var one = CurveWalker.Walk(fresh, 0, 1, out _, out string stop);
            if (stop != CurveWalker.AtEnd || one.Count != 1)
                throw new RebuildRefusedException($"{name}: the new curve for track {i} isn't one whole curve ({stop})");
            var nu = Resolve(one[0], table, name, i);
            if (nu.Kind != u.Kind) throw new RebuildRefusedException($"{name}: track {i} would change its kind from {u.Kind} (tag 0x{u.Tag:X2}) to {nu.Kind} (tag 0x{nu.Tag:X2})");
            parts[i] = fresh;
            changed[i] = !fresh.AsSpan().SequenceEqual(old.AsSpan(c.At, c.DiskLength));
            rewritten++;
        }

        long bodyLen = 0;
        foreach (var p in parts) bodyLen += p.Length;
        if (first + bodyLen > int.MaxValue / 2) throw new RebuildRefusedException($"{name}: the rebuilt record would be {first + bodyLen} B");
        var nr = new byte[first + (int)bodyLen];
        old.AsSpan(0, first).CopyTo(nr);
        int at = first;
        foreach (var p in parts) { p.CopyTo(nr, at); at += p.Length; }
        BinaryPrimitives.WriteUInt32LittleEndian(nr.AsSpan(4), (uint)(nr.Length - clip.IdAt));
        BinaryPrimitives.WriteUInt32LittleEndian(nr.AsSpan(lenAt), (uint)(nr.Length - (lenAt + 4)));

        ClipRecord walked;
        try { walked = ClipRecord.Parse(nr); }
        catch (InvalidDataException ex) { throw new RebuildRefusedException($"{name}: the rebuilt record can't be read: {ex.Message}", ex); }
        if (!walked.WalkedToEnd || walked.Curves.Count != clip.Curves.Count)
            throw new RebuildRefusedException($"{name}: the rebuilt record's curves don't read to its end ({walked.Stop})");

        // the arenas: what the inline curves need, before and after. A curve of a form whose arena isn't known may stay as it is (it needs
        // the same on both sides) but can't be written new.
        var arenas = new List<ArenaKind>(4);
        var needOld = new long[9];
        var needNew = new long[9];
        for (int i = 0; i < clip.Curves.Count; i++)
        {
            AddNeed(needOld, clip.Curves[i], name, i, changed[i]);
            AddNeed(needNew, walked.Curves[i], name, i, changed[i]);
        }
        int nameLen = BinaryPrimitives.ReadInt32LittleEndian(old.AsSpan(8));
        foreach (var (kind, align) in ArenaKinds)
        {
            int ka = KindAt(old, nameLen, clip.HeadEntries, kind);
            long before = ka < 0 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(old.AsSpan(ka));
            // no game clip's head holds less than its curves need; one that does would let the game write past the arena
            if (needOld[align] > before)
                throw new RebuildRefusedException($"{name}: the head holds {(ka < 0 ? "no" : before.ToString())} units of arena kind {kind} (alignment {align}), and its curves need {needOld[align]}: the record is damaged");
            // what the head holds beyond its curves' need belongs to the record's other objects, which stay as they are
            long value = needNew[align] + Math.Max(0, before - needOld[align]);
            if (ka < 0)
            {
                if (needNew[align] > needOld[align])
                    throw new RebuildRefusedException($"{name}: the head has no arena kind {kind} (alignment {align}), and the new curves need {needNew[align]} units of it");
            }
            else
            {
                if (value > uint.MaxValue) throw new RebuildRefusedException($"{name}: arena kind {kind} would need {value} units");
                if (value == 0 && before != 0) throw new RebuildRefusedException($"{name}: arena kind {kind} (alignment {align}) would drop from {before} to 0, an entry no game clip holds");
                BinaryPrimitives.WriteUInt32LittleEndian(nr.AsSpan(ka), (uint)value);
            }
            arenas.Add(new ArenaKind(kind, align, ka >= 0, before, ka < 0 ? 0 : value, needOld[align], needNew[align]));
        }

        ClipRecord nw;
        try { nw = ClipRecord.Parse(nr); }
        catch (InvalidDataException ex) { throw new RebuildRefusedException($"{name}: the rebuilt record can't be read: {ex.Message}", ex); }
        if (!nw.WalkedToEnd) throw new RebuildRefusedException($"{name}: the rebuilt record's curves don't read to its end ({nw.Stop})");
        if (nw.Id != clip.Id || nw.Name != clip.Name || nw.IdAt != clip.IdAt || BitConverter.SingleToInt32Bits(nw.Duration) != BitConverter.SingleToInt32Bits(clip.Duration)
            || nw.Word1 != clip.Word1 || nw.Word2 != clip.Word2 || nw.TrackListWord != clip.TrackListWord || nw.TrackListCount != clip.TrackListCount
            || !nw.Tracks.SequenceEqual(clip.Tracks) || nw.FirstCurve != clip.FirstCurve)
            throw new RebuildRefusedException($"{name}: the rebuilt record's ID, name, duration, hash words or track list read back differently");
        for (int i = 0; i < nw.Curves.Count; i++)
            if (!changed[i] && !nr.AsSpan(nw.Curves[i].At, nw.Curves[i].DiskLength).SequenceEqual(old.AsSpan(clip.Curves[i].At, clip.Curves[i].DiskLength)))
                throw new RebuildRefusedException($"{name}: track {i} didn't keep its bytes");
        return new RebuiltClip(nr, nw, rewritten, clip.Curves.Count - rewritten, arenas);
    }

    static Curve Resolve(Curve c, SharedCurveTable? table, string name, int track)
    {
        if (!c.IsReference) return c;
        if (table is null) throw new RebuildRefusedException($"{name}: track {track} refers to shared curve {c.Ref}, and no shared table was given");
        return SharedCurveTable.Resolve(c, table) ?? throw new RebuildRefusedException($"{name}: track {track} refers to shared curve {c.Ref}, which the shared table ({table.Curves.Count} curves) doesn't hold");
    }

    /// <summary>The arena alignment of a curve: 8 for foot plants (tags 0x42 to 0x44), 2 for values of 2 and 6 bytes, 4 for 4, 8 and 12; for
    /// one-byte values 2 with two-byte times (tag % 3 = 1), else 1; 0 for a form whose arena isn't known (3- and 16-byte values). Over the
    /// animation datafile's 9,938 clips no head holds less than its curves need by this rule; with every one-byte form at alignment 1, the
    /// 112 clips holding tag 0x28 or 0x2E would.</summary>
    public static int ArenaOf(byte tag, int valueBytes) => tag is 0x42 or 0x43 or 0x44 ? 8 : valueBytes switch
    {
        2 or 6 => 2,
        4 or 8 or 12 => 4,
        1 => tag % 3 == 1 ? 2 : 1,
        _ => 0,
    };

    /// <summary>The arena units a curve needs: its size in memory in units of its alignment, rounded up. A reference needs none here (it
    /// lives in the shared table).</summary>
    public static long UnitsOf(Curve c) => c.IsReference ? 0 : ArenaOf(c.Tag, c.ValueBytes) is var a and > 0 ? (c.Size + (long)a - 1) / a : -1;

    static void AddNeed(long[] need, Curve c, string name, int track, bool changed)
    {
        if (c.IsReference) return;
        int a = ArenaOf(c.Tag, c.ValueBytes);
        if (a == 0)
        {
            if (!changed) return;
            throw new RebuildRefusedException($"{name}: track {track}'s curve, tag 0x{c.Tag:X2}, has no known arena, so the head can't be sized for it");
        }
        need[a] += (c.Size + (long)a - 1) / a;
    }

    /// <summary>The offset of the value of the head's class-0 entry of <paramref name="kind"/>, or -1. The entries are 12 bytes from 8 bytes
    /// after the name: a u32 class, a u16, a u16 kind, a u32 value.</summary>
    internal static int KindAt(byte[] b, int nameLen, int entries, int kind)
    {
        int o = 12 + nameLen;
        for (int i = 0, q = o + 8; i < entries && q + 12 <= b.Length; i++, q += 12)
            if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(q)) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(q + 6)) == kind) return q + 8;
        return -1;
    }

    /// <summary>The value of the head's arena kind <paramref name="kind"/> (3, 5, 7 or 9) in a parsed record, or null when absent.</summary>
    public static uint? ArenaValue(ClipRecord clip, int kind)
    {
        int at = KindAt(clip.Bytes, BinaryPrimitives.ReadInt32LittleEndian(clip.Bytes.AsSpan(8)), clip.HeadEntries, kind);
        return at < 0 ? null : BinaryPrimitives.ReadUInt32LittleEndian(clip.Bytes.AsSpan(at));
    }
}
