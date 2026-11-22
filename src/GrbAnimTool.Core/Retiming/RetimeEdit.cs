using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Retiming;

/// <summary>Trim and speed: a clip's length changed in its own record. Only where no graph place holds the clip's length. The new duration is
/// written by <see cref="DurationWriter"/>; every multi-key track is re-timed by the warp (rotations and positions re-sampled and written by
/// the writers, plants and every other track moved by key time, <see cref="CurveRetimer"/>); every event list is re-timed
/// (<see cref="EventRetimer"/>); the ground marker is written again from the new mover. A trim that starts after tick 0 moves the mover so
/// the new clip starts where the game's did: the cut's start becomes the clip's first place and turn. Refused where the clip's byte array
/// holds a list of times (<see cref="ByteArrayTimes"/>, read but not adjusted yet, so it can't be re-timed), where the head lacks an arena kind the new
/// curves need, or where a curve whose values aren't decoded would hold 256 keys or more.</summary>
public static class RetimeEdit
{
    public const double MinSpeed = 0.25, MaxSpeed = 4;

    /// <summary>The clip cut to its source ticks <paramref name="from"/> to <paramref name="to"/>: times are kept (shifted by the cut's
    /// start), keys past the cut dropped, a key at each cut taking the value there.</summary>
    public static EditedClip Trim(ClipRecord clip, EditContext ctx, int from, int to)
    {
        EditRebuild.Readable(clip);
        TimeWarp w;
        try { w = TimeWarp.Trim(clip.LastTick, from, to); }
        catch (ArgumentException ex) { throw new EditRefusedException(EditRefusal.BadParameter, $"{clip.Name}: {ex.Message}", ex); }
        return Apply(clip, ctx, w, "trim");
    }

    /// <summary>The clip played <paramref name="factor"/> times as fast: every time scaled.</summary>
    public static EditedClip Speed(ClipRecord clip, EditContext ctx, double factor)
    {
        EditRebuild.Readable(clip);
        if (!double.IsFinite(factor) || factor < MinSpeed || factor > MaxSpeed)
            throw new EditRefusedException(EditRefusal.BadParameter, $"{clip.Name}: a speed of {factor} is outside {MinSpeed} to {MaxSpeed}");
        TimeWarp w;
        try { w = TimeWarp.Speed(clip.LastTick, factor); }
        catch (ArgumentException ex) { throw new EditRefusedException(EditRefusal.BadParameter, $"{clip.Name}: {ex.Message}", ex); }
        return Apply(clip, ctx, w, "speed change");
    }

    /// <summary>The clip re-timed by any warp over its own ticks.</summary>
    public static EditedClip Apply(ClipRecord clip, EditContext ctx, TimeWarp w, string edit) =>
        EditRebuild.Guard(clip.Name, () => Run(clip, ctx, w, edit));

    static EditedClip Run(ClipRecord clip, EditContext ctx, TimeWarp w, string edit)
    {
        EditRebuild.Readable(clip);
        string name = clip.Name;
        var v = EditVerdicts.RequireLengthFree(name, ctx.Verdict, edit);
        // a speed change (or any warp that isn't a shift) changes the root speed a blend may read. A trim carries no blend warning: it plays
        // every tick it keeps at the game's own pace, so the speed at any moment is the game's. What a trim does change is the clip's totals:
        // the average speed over a shorter span, and, for a turning or travelling clip, its net turn and travel, which a blend on heading or
        // on the travel's end reads; the note gives the cut, so a user can see what was dropped
        var warnings = EditVerdicts.Warnings(v, IsShift(w) ? null : "root speed and pace");
        if (w.SourceLast != clip.LastTick) throw new EditRefusedException(EditRefusal.BadParameter, $"{name}: the warp is for a clip of {w.SourceLast} ticks, not {clip.LastTick}");
        int newLast = w.NewLast;
        if (newLast > ClipRecord.MaxDuration * ClipRecord.TicksPerSecond)
            throw new EditRefusedException(EditRefusal.BadParameter, $"{name}: {newLast} ticks is longer than a clip may be");
        var table = ctx.Table;
        var notes = new List<string> { $"{name}: {w.Describe}" };
        if (w.IsIdentity)
        {
            var same = EditRebuild.Rebuild(clip, table, new Dictionary<int, byte[]>());
            notes.Add("the warp leaves every tick where it was: the record is unchanged");
            return EditRebuild.Done(same.Bytes, same, table, notes, warnings);
        }
        // the byte array's times aren't adjusted yet, so they can't move with the others
        if (ByteArrayTimes.HoldsTimes(clip)) throw new EditRefusedException(EditRefusal.TimesUnread, $"{name}: {DurationWriter.UnreadTimesReason}");

        var marker = GroundMarker.Of(clip, table);
        if (marker is { Follows: false }) { notes.Add(marker.NotFollowing("re-timed with the other tracks")); marker = null; }
        // the mover: a trim from a later tick starts where the game's clip started (its first place and turn)
        var mover = GroundMarker.Mover(clip, table, clip.LastTick);
        var start = mover[0];
        var cutStart = MoverAt(clip, table, w.SourceOf(0));
        bool rebase = (cutStart.Position - start.Position).Length() > 1e-5f || CurveValues.Deg(cutStart.Rotation, start.Rotation) > 1e-4;
        var invCut = Inverse(cutStart);
        Transform Rebase(Transform x) => rebase ? start * (invCut * x) : x;
        Vector3 RebasePoint(Vector3 p) => rebase ? start.Apply(invCut.Apply(p)) : p;
        Quaternion RebaseRot(Quaternion q) => rebase ? Quaternion.Normalize(start.Rotation * invCut.Rotation * q) : q;
        // a plant's point in the mover's frame at the source time its new start plays, from the mover's frame at its own start: the rebase
        // moves the mover and the point's world place alike, so it cancels
        Vector3 PlantPoint(int sourceTick, int newTick, Vector3 p)
        {
            double at = w.SourceOf(newTick);
            if (Math.Abs(at - sourceTick) < 1e-9) return p;
            return Inverse(MoverAt(clip, table, at)).Apply(MoverAt(clip, table, sourceTick).Apply(p));
        }
        if (rebase) notes.Add($"the mover moved so the clip starts where the game's did: the trim's start ({cutStart.Position.X * 100:F1} {cutStart.Position.Y * 100:F1} {cutStart.Position.Z * 100:F1}) cm becomes its first place; the foot plants' points, which stand in the mover's frame, need no move for it");

        var curves = new Dictionary<int, byte[]>();
        int interp = 0, stepped = 0, plants = 0, rekeyed = 0;
        var forms = new SortedDictionary<int, int>();
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            uint h = clip.Tracks[i];
            var u = EditRebuild.Resolved(clip, table, i);
            byte[]? nb;
            if (u.Kind is TrackKind.Rotation or TrackKind.Position)
            {
                if (h == EditTracks.Marker && marker is not null) continue;
                bool isMover = h == 0 && rebase;
                nb = CurveRetimer.Interpolated(u, w, isMover && u.Kind == TrackKind.Rotation ? RebaseRot : null, isMover && u.Kind == TrackKind.Position ? RebasePoint : null, () => rekeyed++);
                if (nb is not null) interp++;
            }
            else if (u.Kind == TrackKind.Plant)
            {
                // a plant's point stands in the mover's frame at its start, not in the clip's world, so the rebase leaves it be; where the
                // new start plays another source time than the key's (a plant the cut starts), the point is read in the mover there
                nb = CurveRetimer.PlantsAt(u, w, name, i, PlantPoint);
                if (nb is not null) plants++;
            }
            else
            {
                nb = CurveRetimer.Stepped(u, w, name, i, notes);
                if (nb is not null) { stepped++; forms[u.Form] = forms.GetValueOrDefault(u.Form) + 1; }
            }
            if (nb is not null) curves[i] = nb;
        }
        if (marker is not null)
        {
            var nm = new Transform[newLast + 1];
            for (int t = 0; t <= newLast; t++) nm[t] = Rebase(MoverAt(clip, table, w.SourceOf(t)));
            // the end turn follows the new end; a marker whose E is no turn at all keeps it so, as the game's clip has it; any other
            // fixed turn moves with the mover
            var e = marker.EndTurn ? nm[newLast].Rotation : marker.NoTurn ? marker.E : RebaseRot(marker.E);
            if (rebase && marker.NoTurn && !marker.EndTurn) notes.Add($"the ground marker's fixed turn is no turn in the game's clip, and is kept so");
            marker.Rewrite(clip, table, nm, e, curves, notes);
        }
        notes.Add($"re-timed: {interp} rotation and position curves re-sampled, {plants} plant curves, {stepped} other curves moved by key time with their values as stored"
            + (forms.Count > 0 ? $" (by form: {string.Join(", ", forms.Select(f => $"{f.Key} x{f.Value}"))})" : ""));
        if (rekeyed > 0)
            notes.Add($"{rekeyed} rotation and position curve(s) would hold more than 255 keys: keyed every {KeyingRule.Step(newLast)} ticks instead by the keying rule, joined straight between keys");

        EditRebuild.NoteWideRotations(curves, notes);
        var built = EditRebuild.Rebuild(clip, table, curves);
        var bytes = (byte[])built.Bytes.Clone();
        EventRetimer.Apply(clip, bytes, w, notes);
        float seconds = newLast == clip.LastTick ? clip.Duration : newLast / (float)ClipRecord.TicksPerSecond;
        ClipRecord mid;
        try { mid = ClipRecord.Parse(bytes); }
        catch (InvalidDataException ex) { throw new EditRefusedException(EditRefusal.ReadBack, $"{name}: the re-timed record can't be read: {ex.Message}", ex); }
        byte[] final;
        try { final = DurationWriter.WithDuration(mid, seconds, scaleSteps: false); }
        catch (RebuildRefusedException ex) { throw new EditRefusedException(EditRefusal.Rebuild, ex.Message, ex); }
        notes.Add($"duration {clip.Duration:F4} s ({clip.LastTick} ticks) to {seconds:F4} s ({newLast} ticks)");
        return EditRebuild.Done(final, built, table, notes, warnings);
    }

    /// <summary>Whether every new tick plays one source tick after the last (a trim: the pace is the game's).</summary>
    static bool IsShift(TimeWarp w)
    {
        for (int t = 1; t <= w.NewLast; t++) if (Math.Abs(w.SourceOf(t) - w.SourceOf(t - 1) - 1) > 1e-9) return false;
        return true;
    }

    static Transform Inverse(Transform t)
    {
        var iq = Quaternion.Inverse(t.Rotation);
        return new Transform(Vector3.Transform(-t.Position, iq), iq);
    }

    /// <summary>The mover at a source time (fractional ticks), identity where the clip has no mover track.</summary>
    static Transform MoverAt(ClipRecord clip, SharedCurveTable table, double tick)
    {
        Vector3 p = Vector3.Zero; Quaternion q = Quaternion.Identity;
        bool hp = false, hr = false;
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            if (clip.Tracks[i] != 0) continue;
            var u = EditRebuild.Resolved(clip, table, i);
            if (u.Kind == TrackKind.Position && !hp) { p = CurveValues.Pos(u, CurveDecoder.KeyTicks(u), tick); hp = true; }
            else if (u.Kind == TrackKind.Rotation && !hr) { q = CurveValues.Rot(u, CurveDecoder.KeyTicks(u), tick); hr = true; }
        }
        return new Transform(p, q);
    }
}
