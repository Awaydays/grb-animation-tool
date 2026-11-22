using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Graph;

namespace GrbAnimTool.Core.Edits;

/// <summary>An equal swap: the source clip's motion played in the target's slot. Every track of the target takes the source's curve bytes as
/// they are (inline curves copied, shared-table references kept), so nothing is re-encoded; only when the two track lists are identical,
/// every hash and kind in order, and the two clips are of one length. The target keeps its ID, name, duration, hash words, track list and
/// every object before its tracks, its events among them. After the copy every arena kind is checked: a kind dropping to 0 or a kind the copy
/// needs and the head lacks is refused.</summary>
public static class SwapEdit
{
    /// <summary>A track as the swap compares it: its hash and its curve's kind.</summary>
    public static IReadOnlyList<(uint Hash, TrackKind Kind)> Signature(ClipRecord c, SharedCurveTable table) =>
        Enumerable.Range(0, c.Tracks.Count).Select(i => (c.Tracks[i], EditRebuild.Resolved(c, table, i).Kind)).ToList();

    /// <summary>Why <paramref name="source"/> can't be swapped into <paramref name="target"/>'s slot, or null when it can.</summary>
    public static (EditRefusal Kind, string Why)? Mismatch(ClipRecord target, ClipRecord source, SharedCurveTable table)
    {
        // one length: the same duration to the bit, not only the same rounded tick (the target keeps its own duration)
        if (target.LastTick != source.LastTick || BitConverter.SingleToInt32Bits(target.Duration) != BitConverter.SingleToInt32Bits(source.Duration))
            return (EditRefusal.LengthsDiffer, $"{source.Name} lasts {source.LastTick} ticks ({source.Duration:R} s) and {target.Name} {target.LastTick} ({target.Duration:R} s): a swap needs one length, the same duration exactly");
        var a = Signature(target, table); var b = Signature(source, table);
        var diff = new List<string>();
        if (a.Count != b.Count) diff.Add($"{target.Name} has {a.Count} tracks and {source.Name} {b.Count}");
        for (int i = 0; i < Math.Min(a.Count, b.Count) && diff.Count < 12; i++)
            if (a[i] != b[i]) diff.Add($"track {i}: {a[i].Hash:X8} {a[i].Kind} against {b[i].Hash:X8} {b[i].Kind}");
        if (diff.Count > 0) return (EditRefusal.TrackListsDiffer, $"the track lists differ: {string.Join("; ", diff)}");
        return null;
    }

    public static EditedClip Apply(ClipRecord target, ClipRecord source, EditContext ctx) =>
        EditRebuild.Guard(target.Name, () => Run(target, source, ctx));

    static EditedClip Run(ClipRecord target, ClipRecord source, EditContext ctx)
    {
        EditRebuild.Readable(target);
        EditRebuild.Readable(source);
        var table = ctx.Table;
        if (Mismatch(target, source, table) is { } m) throw new EditRefusedException(m.Kind, $"{source.Name} into {target.Name}: {m.Why}");
        var notes = new List<string>();
        ClipGraphVerdict? v = ctx.Verdict;
        var warnings = EditVerdicts.Warnings(v, "motion (its speed and heading become the source's)", replace: true);
        var curves = new Dictionary<int, byte[]>();
        for (int i = 0; i < source.Curves.Count; i++)
        {
            var sc = source.Curves[i];
            var bytes = source.Bytes.AsSpan(sc.At, sc.DiskLength).ToArray();
            var tc = target.Curves[i];
            if (!bytes.AsSpan().SequenceEqual(target.Bytes.AsSpan(tc.At, tc.DiskLength))) curves[i] = bytes;
        }
        var built = EditRebuild.Rebuild(target, table, curves);
        foreach (var a in built.Arenas)
        {
            if (a.Present && a.Before != 0 && a.After == 0)
                throw new EditRefusedException(EditRefusal.ArenaKindToZero, $"{source.Name} into {target.Name}: arena kind {a.Kind} would drop from {a.Before} to 0, an entry no game clip holds");
            if (!a.Present && a.CurvesNeedAfter > 0)
                throw new EditRefusedException(EditRefusal.ArenaKindMissing, $"{source.Name} into {target.Name}: the copied curves need arena kind {a.Kind}, which the target's head lacks");
        }
        notes.Add($"every track takes {source.Name}'s curve as stored ({curves.Count} of {source.Curves.Count} differ from {target.Name}'s)");
        notes.Add($"{target.Name}'s events, step objects and event times are kept: {source.Name}'s motion plays against them");
        return EditRebuild.Done(built.Bytes, built, table, notes, warnings);
    }
}
