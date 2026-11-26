using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Import;

/// <summary>Replace: another game clip's motion (or a .glb's, through <see cref="ClipImport.Run(Skeleton, SharedCurveTable?, ClipEntry, GlbScene, string, string, ClipGraphVerdict?, bool, FitOptions?)"/>)
/// put on a clip, fitted to its exact length. The clip keeps its own record: its ID, length, events, hash words and timing data, every
/// track that isn't a bone's rotation or position (byte tracks, the hand IK offset and the like) and its hand tracks. Each of its bone
/// tracks takes the source's motion; one the source has no track for takes the skeleton's rest pose; the source's tracks the clip has no
/// track for are dropped (the report's "motion not written" line names them).</summary>
public static class ClipReplace
{
    /// <summary>Puts <paramref name="source"/>'s motion on <paramref name="slot"/>. Refused (an <see cref="ImportRefusedException"/>) when
    /// either record can't be read, or the fit can't be made.</summary>
    public static ImportResult FromClip(Skeleton s, SharedCurveTable? table, ClipEntry slot, ClipEntry source, FitOptions fit,
        ClipGraphVerdict? verdict = null, bool graphRead = false)
    {
        var rec = slot.Record ?? throw new ImportRefusedException($"{slot.Name}'s record can't be read ({slot.ParseError}), so nothing can be put on it.", ImportRefusal.Slot);
        if (!rec.WalkedToEnd) throw new ImportRefusedException($"{slot.Name}'s curves don't read to the record's end ({rec.Stop}), so it can't be rebuilt.", ImportRefusal.Slot);
        if (rec.LastTick < 1) throw new ImportRefusedException($"{slot.Name} is shorter than one tick.", ImportRefusal.Slot);
        var src = source.Record ?? throw new ImportRefusedException($"{source.Name}'s record can't be read ({source.ParseError}).", ImportRefusal.File);
        Motion m;
        try { m = Motion.FromClip(s, src, table); }
        catch (InvalidDataException ex) { throw new ImportRefusedException($"{source.Name} can't be decoded: {ex.Message}", ImportRefusal.File, ex); }
        FitResult f;
        try { f = ClipFit.Apply(m, rec.LastTick, fit); }
        catch (ArgumentException ex) { throw new ImportRefusedException(ex.Message, ImportRefusal.Length, ex); }
        var report = new CheckReport();
        report.Add(ClipImport.FitCheck(f, src.LastTick, rec.LastTick));
        string where = source.FilePath is { } p ? $" ({Path.GetFileName(p)})" : "";
        return ClipImport.Write(s, table, slot, report, f.Motion, $"clip {source.Name}{where}", Hashing.Sha256Hex.Of(source.Bytes.ToArray()), verdict, graphRead, f, m);
    }

    /// <summary>The track map a replace made, for the screen before the player keeps it: the clip's tracks filled with the rest pose (the
    /// source has none), the source's bones the clip has no track for (dropped), and the clip's tracks kept as they were.</summary>
    public static (IReadOnlyList<string> Filled, IReadOnlyList<string> Dropped, IReadOnlyList<string> Kept) TrackMap(ImportResult r, Skeleton s)
    {
        var filled = r.Fates.Where(x => x.Written && x.What.EndsWith(ClipImport.FilledNote, StringComparison.Ordinal)).Select(x => x.Name).Distinct().ToList();
        var slotBones = new HashSet<int>();
        bool slotMover = false;
        foreach (var x in r.Fates)
        {
            if (x.Hash == 0) slotMover = true;
            else if (s.IndexOf(x.Hash) is >= 0 and var b) slotBones.Add(b);
        }
        var dropped = new List<string>();
        for (int b = 0; b < s.Bones.Count; b++)
            if ((r.FromFile.RotationKeyed[b] || r.FromFile.PositionKeyed[b]) && !slotBones.Contains(b)) dropped.Add(s.Bones[b].Name);
        if (r.FromFile.HasMover && !slotMover) dropped.Add("(the mover)");
        var kept = r.Fates.Where(x => !x.Written && x.Kind is not (TrackKind.Rotation or TrackKind.Position)).Select(x => x.Name).Distinct().ToList();
        return (filled, dropped, kept);
    }
}
