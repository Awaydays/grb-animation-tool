using System.Buffers.Binary;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Edits;

/// <summary>The last steps every edit takes: its new curves checked against the head's arena kinds (so a refusal says which kind and which
/// tracks), the record rebuilt around them, and the result read back whole: it must walk to its end, every reference resolve, every key
/// decode and no key stand past the clip's last tick.</summary>
public static class EditRebuild
{
    static readonly (int Kind, int Align)[] Kinds = [(3, 1), (5, 2), (7, 4), (9, 8)];

    /// <summary>Runs an edit, turning a writer's refusal of a value (a rotation with no direction, a time that falls, a plant past a 16-bit
    /// value: what a damaged record's keys give) into a refusal of the edit, so an edit ends in a result or a refusal and nothing else.</summary>
    public static T Guard<T>(string clip, Func<T> edit)
    {
        try { return edit(); }
        catch (ArgumentException ex) { throw new EditRefusedException(EditRefusal.CantMove, $"{clip}: a new curve can't be written: {ex.Message}", ex); }
        catch (InvalidDataException ex) { throw new EditRefusedException(EditRefusal.Unreadable, $"{clip}: the record can't be read: {ex.Message}", ex); }
    }

    /// <summary>A record ready for an edit: parsed, walked to its end, one curve a track, its duration in range.</summary>
    public static ClipRecord Readable(ClipRecord clip)
    {
        if (!clip.DurationValid) throw new EditRefusedException(EditRefusal.Unreadable, $"{clip.Name}: its duration {clip.Duration} s is out of range");
        if (!clip.WalkedToEnd) throw new EditRefusedException(EditRefusal.Unreadable, $"{clip.Name}: its curves don't read to the record's end ({clip.Stop})");
        return clip;
    }

    /// <summary>Parses a whole record for an edit, refusing one that can't be read.</summary>
    public static ClipRecord Parse(byte[] record)
    {
        try { return Readable(ClipRecord.Parse(record)); }
        catch (InvalidDataException ex) { throw new EditRefusedException(EditRefusal.Unreadable, $"the record can't be read: {ex.Message}", ex); }
    }

    /// <summary>The curve of track <paramref name="i"/>, a reference followed; refused when it can't be.</summary>
    public static Curve Resolved(ClipRecord clip, SharedCurveTable table, int i) =>
        SharedCurveTable.Resolve(clip.Curves[i], table)
        ?? throw new EditRefusedException(EditRefusal.Unreadable, $"{clip.Name}: track {i} refers to shared curve {clip.Curves[i].Ref}, which the shared table doesn't hold");

    /// <summary>The head's arena kinds against the curves <paramref name="curves"/> would give, by the rebuild's own rule: refused when a
    /// kind the new curves need more of is absent, or a kind would drop to 0.</summary>
    public static void CheckArenas(ClipRecord clip, IReadOnlyDictionary<int, byte[]> curves)
    {
        var needOld = new long[9];
        var needNew = new long[9];
        var newBy = new Dictionary<int, List<int>>();
        for (int i = 0; i < clip.Curves.Count; i++)
        {
            Add(needOld, clip.Curves[i], i, false);
            if (curves.TryGetValue(i, out var nb))
            {
                var one = CurveWalker.Walk(nb, 0, 1, out _, out string stop);
                if (one.Count != 1 || stop != CurveWalker.AtEnd)
                    throw new EditRefusedException(EditRefusal.Rebuild, $"{clip.Name}: the new curve for track {i} isn't one whole curve ({stop})");
                int a = Add(needNew, one[0], i, true);
                if (a > 0) { if (!newBy.TryGetValue(a, out var l)) newBy[a] = l = []; l.Add(i); }
            }
            else Add(needNew, clip.Curves[i], i, false);
        }
        foreach (var (kind, align) in Kinds)
        {
            uint? before = ClipRebuild.ArenaValue(clip, kind);
            string tracks = newBy.TryGetValue(align, out var l) ? string.Join(", ", l.Take(8)) + (l.Count > 8 ? $" and {l.Count - 8} more" : "") : "";
            if (before is null && needNew[align] > needOld[align])
                throw new EditRefusedException(EditRefusal.ArenaKindMissing,
                    $"{clip.Name}: the new curves (tracks {tracks}) need {needNew[align]} units of arena kind {kind} (alignment {align}), and the record's head holds no entry of that kind");
            if (before is { } b && b != 0 && needNew[align] + Math.Max(0, b - needOld[align]) == 0)
                throw new EditRefusedException(EditRefusal.ArenaKindToZero,
                    $"{clip.Name}: arena kind {kind} (alignment {align}) would drop from {b} to 0, an entry no game clip holds");
        }

        int Add(long[] need, Curve c, int track, bool fresh)
        {
            if (c.IsReference) return 0;
            int a = ClipRebuild.ArenaOf(c.Tag, c.ValueBytes);
            if (a == 0)
            {
                if (!fresh) return 0;
                throw new EditRefusedException(EditRefusal.CantMove, $"{clip.Name}: track {track}'s new curve, tag 0x{c.Tag:X2}, has no known arena, so the head can't be sized for it");
            }
            need[a] += (c.Size + (long)a - 1) / a;
            return a;
        }
    }

    /// <summary>The record rebuilt with <paramref name="curves"/> (track index to new curve bytes) after the arena check; a rebuild refusal
    /// comes out as an <see cref="EditRefusedException"/>.</summary>
    public static RebuiltClip Rebuild(ClipRecord clip, SharedCurveTable table, IReadOnlyDictionary<int, byte[]> curves)
    {
        CheckArenas(clip, curves);
        try { return ClipRebuild.Rebuild(clip, table, curves); }
        catch (RebuildRefusedException ex) { throw new EditRefusedException(EditRefusal.Rebuild, ex.Message, ex); }
    }

    /// <summary>Adds a note when any of <paramref name="curves"/> is a rotation of several keys in the 12-byte form (tags 0x18 to 0x1A): the
    /// writers keep that form where the track's own curve was in it, but a written curve of that form with more than one key hasn't been
    /// seen playing in game.</summary>
    public static void NoteWideRotations(IReadOnlyDictionary<int, byte[]> curves, List<string> notes)
    {
        var tracks = new List<int>();
        foreach (var (i, b) in curves.OrderBy(x => x.Key))
        {
            var one = CurveWalker.Walk(b, 0, 1, out _, out _);
            if (one.Count == 1 && !one[0].IsReference && one[0].Tag is >= 0x18 and <= 0x1A && one[0].Keys > 1) tracks.Add(i);
        }
        if (tracks.Count > 0)
            notes.Add($"{tracks.Count} rotation curve{(tracks.Count == 1 ? "" : "s")} written with several keys in the 12-byte form (tags 0x18 to 0x1A), the form of the track's own curve (tracks {string.Join(", ", tracks.Take(12))}{(tracks.Count > 12 ? ", ..." : "")}): a written curve of that form with more than one key hasn't been seen playing in game");
    }

    /// <summary>The edit's result from a finished record: read back by <see cref="Check"/> and refused if that fails.</summary>
    public static EditedClip Done(byte[] record, RebuiltClip rebuilt, SharedCurveTable table, List<string> notes, IReadOnlyList<string>? warnings = null)
    {
        var (rec, why) = Check(record, table);
        if (why is not null) throw new EditRefusedException(EditRefusal.ReadBack, why);
        return new EditedClip(record, rec!, rebuilt, notes) { Warnings = warnings ?? [] };
    }

    /// <summary>The read-back of an edited record: it parses, walks to its end with one curve a track, every reference resolves, every
    /// rotation, position and plant key decodes to finite values, every other curve's values fit it, and no key stands past the last tick.
    /// Returns the parsed record and null, or a reason.</summary>
    public static (ClipRecord? Record, string? Why) Check(byte[] record, SharedCurveTable table)
    {
        ClipRecord c;
        try { c = ClipRecord.Parse(record); }
        catch (InvalidDataException ex) { return (null, $"the edited record can't be read: {ex.Message}"); }
        if (!c.WalkedToEnd) return (c, $"{c.Name}: the edited record's curves don't read to its end ({c.Stop})");
        if (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4)) != record.Length - c.IdAt) return (c, $"{c.Name}: the edited record's length field is wrong");
        int last = c.LastTick;
        for (int i = 0; i < c.Curves.Count; i++)
        {
            var u = SharedCurveTable.Resolve(c.Curves[i], table);
            if (u is null) return (c, $"{c.Name}: track {i}'s reference to shared curve {c.Curves[i].Ref} doesn't resolve");
            var ticks = CurveDecoder.KeyTicks(u);
            for (int k = 1; k < ticks.Length; k++)
                if (ticks[k] < ticks[k - 1]) return (c, $"{c.Name}: track {i}'s key times fall at key {k}");
            // a shared curve is keyed for whichever clips use it, so only the record's own curves are held to its length
            if (!c.Curves[i].IsReference && ticks[^1] > last && u.Keys > 1) return (c, $"{c.Name}: track {i}'s last key stands at tick {ticks[^1]}, past the clip's last tick {last}");
            for (int k = 0; k < u.Keys; k++)
            {
                bool ok = u.Kind switch
                {
                    TrackKind.Rotation => CurveDecoder.TryKeyRot(u, k, out var q, out _) && float.IsFinite(q.X + q.Y + q.Z + q.W) || CurveTags.ValueBytes(u.Tag) == 3,
                    TrackKind.Position => CurveDecoder.KeyPos(u, k) is { } p && float.IsFinite(p.X + p.Y + p.Z),
                    TrackKind.Plant => u.Form != 22 || CurveDecoder.Plant(u, k) is not null,
                    _ => ScalarDecoder.KeyRaw(u, k) is not null,
                };
                if (!ok) return (c, $"{c.Name}: track {i}'s key {k} (tag 0x{u.Tag:X2}) doesn't decode");
            }
        }
        return (c, null);
    }
}
