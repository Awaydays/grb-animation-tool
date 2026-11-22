using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Edits;

/// <summary>A clip mirrored in place through the body's middle plane, the plane x = 0 of the mover's first place (x is the clip frame's side
/// to side axis): every mirrored bone's curve, <c>Reference</c> included, goes to its counterpart's track key by key
/// (<see cref="SkeletonMirror"/>); the mover (track 0) is mirrored, its sideways travel negated, its forward travel kept and its turn reversed;
/// the foot plants are mirrored by their slot's swap and their point's x sign (a point stands in the mover's frame at its plant's start); the
/// ground marker is written again from the new mover. Refused on armed clips (any hand track), where the graph's verdict refuses a change of
/// motion, on a clip with no bone or mover track, where a bone's mirrored motion has no track to go to, and where a curve the mirror rewrites
/// would hold 256 keys or more.
/// <para>Kept as they are, and listed in the result's notes: the step objects' foot events (types 6 and 8) and every other event object; the
/// add-on bones and bones without a counterpart; tracks naming no skeleton bone whose meaning isn't known; byte, float and other non-bone
/// tracks. Tracks shaped like an IK offset but not among the known hand tracks are named in a warning.</para></summary>
public static class MirrorEdit
{
    public static EditedClip Apply(ClipRecord clip, EditContext ctx, SkeletonMirror? mirror = null) =>
        EditRebuild.Guard(clip.Name, () => Run(clip, ctx, mirror));

    static EditedClip Run(ClipRecord clip, EditContext ctx, SkeletonMirror? mirror)
    {
        EditRebuild.Readable(clip);
        string name = clip.Name;
        var v = EditVerdicts.RequireMotionFree(name, ctx.Verdict, "heading, which a mirror turns the other way");
        var hand = EditTracks.HandTracksOf(clip);
        if (hand.Count > 0)
            throw new EditRefusedException(EditRefusal.HandTracks, $"{name}: it holds hand tracks ({string.Join(", ", hand.Select(h => h.ToString("X8")))}), which a mirror in place would leave aiming the hands at the old side");
        var m = mirror ?? new SkeletonMirror(ctx.Skeleton);
        var s = ctx.Skeleton;
        var table = ctx.Table;
        int last = clip.LastTick;
        var notes = new List<string>();
        var curves = new Dictionary<int, byte[]>();
        var resolved = Enumerable.Range(0, clip.Tracks.Count).Select(i => EditRebuild.Resolved(clip, table, i)).ToArray();
        // a clip with no bone and no mover track (a face's or a dialogue's, of byte and form-14 channels only) has no motion to mirror
        if (!Enumerable.Range(0, clip.Tracks.Count).Any(i => resolved[i].Kind is TrackKind.Rotation or TrackKind.Position && (clip.Tracks[i] == 0 || s.IndexOf(clip.Tracks[i]) >= 0)))
            throw new EditRefusedException(EditRefusal.NoMotion, $"{name}: it holds no bone or mover track, so there is no motion to mirror");
        // the curves written, by track, with their key count: none may hold more than 255 keys
        var keyCounts = new Dictionary<int, int>();

        // the tracks by hash and kind
        var byKey = new Dictionary<(uint, bool), int>();
        for (int i = 0; i < clip.Tracks.Count; i++)
            if (resolved[i].Kind is TrackKind.Rotation or TrackKind.Position) byKey.TryAdd((clip.Tracks[i], resolved[i].Kind == TrackKind.Rotation), i);

        var marker = GroundMarker.Of(clip, table);
        if (marker is { Follows: false }) { notes.Add(marker.NotFollowing("kept unmirrored")); marker = null; }
        var moverBefore = GroundMarker.Mover(clip, table, last);
        var frame = moverBefore[0];

        var keptBones = new List<string>(); var keptOther = new List<string>();
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            uint h = clip.Tracks[i];
            var u = resolved[i];
            bool rot = u.Kind == TrackKind.Rotation;
            if (u.Kind is not (TrackKind.Rotation or TrackKind.Position))
            {
                if (u.Kind != TrackKind.Plant) keptOther.Add($"{h:X8} ({u.Kind}, tag 0x{u.Tag:X2})");
                continue;
            }
            if (h == EditTracks.Marker && marker is not null) continue;
            if (h == 0)
            {
                // the mover: mirrored in its first place's frame, key by key
                var (ticks, src) = Keys(u);
                if (rot)
                {
                    var q = ticks.Select((_, k) => m.InFrame(frame, new Transform(frame.Position, CurveValues.KeyRot(src, k))).Rotation).ToList();
                    if (!CurveValues.SameRot(u, ticks, q)) { curves[i] = CurveValues.WriteRot(u, ticks, q); keyCounts[i] = ticks.Count; }
                }
                else
                {
                    var p = ticks.Select((_, k) => m.PointInFrame(frame, CurveValues.KeyPos(src, k))).ToList();
                    if (!CurveValues.SamePos(u, ticks, p)) { curves[i] = CurveWriter.PosCurve(ticks, p); keyCounts[i] = ticks.Count; }
                }
                continue;
            }
            int b = s.IndexOf(h);
            if (b < 0) { keptOther.Add($"{h:X8} ({(rot ? "rotation" : "position")}, in no skeleton)"); continue; }
            if (m.Pair[b] < 0) { keptBones.Add($"{s.Bones[b].Name} ({m.NotMirrored[b]})"); continue; }
            // this track takes its counterpart's curve mirrored; where the counterpart has no track of this kind, its rest
            int c = m.Pair[b];
            var cb = s.Bones[c];
            if (byKey.TryGetValue((cb.Hash, rot), out int j))
            {
                var (ticks, src) = Keys(resolved[j]);
                if (rot)
                {
                    var q = ticks.Select((_, k) => m.LocalRot(c, CurveValues.KeyRot(src, k))).ToList();
                    if (!CurveValues.SameRot(u, ticks, q)) { curves[i] = CurveValues.WriteRot(u, ticks, q); keyCounts[i] = ticks.Count; }
                }
                else
                {
                    var p = ticks.Select((_, k) => m.LocalPos(c, CurveValues.KeyPos(src, k))).ToList();
                    if (!CurveValues.SamePos(u, ticks, p)) { curves[i] = CurveWriter.PosCurve(ticks, p); keyCounts[i] = ticks.Count; }
                }
            }
            else
            {
                // the counterpart stands at its rest: this bone takes the rest mirrored, one key
                if (rot)
                {
                    var q = m.LocalRot(c, cb.LocalRest.Rotation);
                    if (!CurveValues.SameRot(u, [0], [q], 0.01)) curves[i] = CurveValues.WriteRot(u, [0], [q]);
                }
                else
                {
                    var p = m.LocalPos(c, cb.LocalRest.Position);
                    if (!CurveValues.SamePos(u, [0], [p], 1e-4)) curves[i] = CurveWriter.PosCurve([0], [p]);
                }
            }
        }
        // a bone whose counterpart has no track to take its mirrored motion: refused unless that motion is the counterpart's rest
        foreach (var ((h, rot), i) in byKey)
        {
            int b = s.IndexOf(h);
            if (b < 0 || h == 0 || m.Pair[b] < 0) continue;
            int c = m.Pair[b];
            if (byKey.ContainsKey((s.Bones[c].Hash, rot))) continue;
            var u = resolved[i];
            var tk = CurveDecoder.KeyTicks(u);
            var rest = s.Bones[c].LocalRest;
            for (int k = 0; k < u.Keys; k++)
            {
                double off;
                if (rot)
                {
                    var q = CurveDecoder.KeyRot(u, k) ?? throw new EditRefusedException(EditRefusal.Unreadable,
                        $"{name}: {s.Bones[b].Name}'s rotation key {k} (tag 0x{u.Tag:X2}) doesn't decode, and {s.Bones[c].Name}, its counterpart, has no rotation track: whether the mirror may leave it at its rest can't be told");
                    off = CurveValues.Deg(m.LocalRot(b, q), rest.Rotation);
                }
                else
                {
                    var p = CurveDecoder.KeyPos(u, k) ?? throw new EditRefusedException(EditRefusal.Unreadable,
                        $"{name}: {s.Bones[b].Name}'s position key {k} (tag 0x{u.Tag:X2}) doesn't decode, and {s.Bones[c].Name}, its counterpart, has no position track: whether the mirror may leave it at its rest can't be told");
                    off = (m.LocalPos(b, p) - rest.Position).Length() * 100;
                }
                if (off > 0.05)
                    throw new EditRefusedException(EditRefusal.Unpaired,
                        $"{name}: {s.Bones[b].Name} has a {(rot ? "rotation" : "position")} track and {s.Bones[c].Name}, its counterpart, has none to take its mirrored motion (key {k} at tick {tk[k]} stands {off:F2} {(rot ? "deg" : "cm")} off its rest)");
            }
        }
        // a bone where neither it nor its counterpart has a track of a kind stays at its rest: refused where that rest doesn't mirror to the
        // counterpart's (a root bone's turn, whose rest stands in the skeleton's frame rather than the clip's)
        foreach (var bone in s.Bones)
        {
            int b = bone.Index, c = m.Pair[b];
            if (c < 0 || c < b) continue;
            foreach (bool rot in new[] { true, false })
            {
                if (byKey.ContainsKey((bone.Hash, rot)) || byKey.ContainsKey((s.Bones[c].Hash, rot)) || m.RestMirrorsToRest(b, rot)) continue;
                string kind = rot ? "rotation" : "position", rest = rot ? "turn" : "place";
                throw new EditRefusedException(EditRefusal.Unpaired, c == b
                    ? $"{name}: {bone.Name} has no {kind} track, and its rest {rest} doesn't mirror to itself in the clip's frame, so the mirror would need a track the clip doesn't hold"
                    : $"{name}: neither {bone.Name} nor {s.Bones[c].Name} has a {kind} track, and their rest {rest}s don't mirror to each other in the clip's frame, so the mirror would need a track the clip doesn't hold");
            }
        }

        // the plants: slots swapped (low bit), points mirrored, key by key. A point stands in the mover's frame at its plant's start, and the
        // mirrored mover there is the mover reflected on both sides, so the point is mirrored in its own frame: its x negated
        var plants = new Dictionary<uint, int>();
        for (int i = 0; i < clip.Tracks.Count; i++)
            if (resolved[i].Kind == TrackKind.Plant && EditTracks.IsPlantSlot(clip.Tracks[i])) plants[clip.Tracks[i]] = i;
        foreach (var (h, i) in plants)
        {
            uint other = EditTracks.FirstPlantSlot + ((h - EditTracks.FirstPlantSlot) ^ 1);
            if (!plants.TryGetValue(other, out int j))
                throw new EditRefusedException(EditRefusal.Unpaired, $"{name}: it holds the plant slot {h - EditTracks.FirstPlantSlot} and not its counterpart {other - EditTracks.FirstPlantSlot}, so the mirrored plants have no track to go to");
            var src = resolved[j];
            if (src.Form != 22) throw new EditRefusedException(EditRefusal.CantMove, $"{name}: plant track {j} is in tag 0x{src.Tag:X2}, a form with no reader");
            var keys = new List<FootPlant>();
            for (int k = 0; k < src.Keys; k++)
            {
                var f = CurveDecoder.Plant(src, k)!.Value;
                bool zero = f.LengthTicks == 0 && f.Point == Vector3.Zero;
                keys.Add(zero ? f : f with { Point = m.M(f.Point) });
            }
            if (!SamePlants(resolved[i], keys)) { curves[i] = WritePlants(keys, name, i); keyCounts[i] = keys.Count; }
        }
        if (plants.Count > 0) notes.Add($"the foot plants mirrored: slots swapped, each point's lateral place mirrored ({plants.Count} tracks)");
        // the tool writes no curve of 256 keys or more until one is seen playing in game; the mirror keeps each curve's own key times, so a
        // clip whose mirror rewrites such a curve is refused rather than re-keyed
        if (keyCounts.Where(x => x.Value > CurveWriter.MaxShortKeys).ToList() is { Count: > 0 } longOnes)
            throw new EditRefusedException(EditRefusal.LongCurve,
                $"{name}: this clip holds curves of 256 keys or more; the tool doesn't write such curves until one is seen playing ({longOnes.Count} the mirror would rewrite, the longest {longOnes.Max(x => x.Value)} keys)");

        if (marker is not null)
        {
            var moverAfter = moverBefore.Select(x => m.InFrame(frame, x)).ToArray();
            var e = m.InFrame(frame, new Transform(Vector3.Zero, marker.E)).Rotation;
            marker.Rewrite(clip, table, moverAfter, e, curves, notes);
        }
        Notes(clip, notes, keptBones, keptOther);
        var warnings = EditVerdicts.Warnings(v, "heading and sideways travel");
        if (IkShaped(clip, resolved, s) is { Count: > 0 } ik)
            warnings.Add($"it holds tracks shaped like an IK offset (a byte, a position and a rotation track on one hash in no skeleton) that aren't among the known hand tracks: {string.Join(", ", ik.Select(x => x.ToString("X8")))}. "
                + "They are kept unmirrored, so whatever they aim (likely the hands) still aims at the old side");
        EditRebuild.NoteWideRotations(curves, notes);
        var built = EditRebuild.Rebuild(clip, table, curves);
        return EditRebuild.Done(built.Bytes, built, table, notes, warnings);
    }

    /// <summary>The hashes in no skeleton, other than the mover, the ground marker, the plants and the known hand tracks, that hold a byte, a
    /// position and a rotation track: the IK offset's own signature.</summary>
    internal static List<uint> IkShaped(ClipRecord clip, Curve[] resolved, Skeleton s)
    {
        var kinds = new Dictionary<uint, HashSet<TrackKind>>();
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            uint h = clip.Tracks[i];
            if (h == 0 || h == EditTracks.Marker || EditTracks.IsPlantSlot(h) || EditTracks.Hand.Contains(h) || s.IndexOf(h) >= 0) continue;
            if (!kinds.TryGetValue(h, out var k)) kinds[h] = k = [];
            k.Add(resolved[i].Kind);
        }
        return kinds.Where(x => x.Value.Contains(TrackKind.Byte) && x.Value.Contains(TrackKind.Position) && x.Value.Contains(TrackKind.Rotation))
            .Select(x => x.Key).OrderBy(x => x).ToList();
    }

    /// <summary>The keys a mirrored track takes: the source curve's own times, each key's own value (two keys at one tick, a step, stay two
    /// values), so the mirror is exact key by key and never re-sampled. A clip
    /// where a curve so written would hold 256 keys or more is refused (<see cref="EditRefusal.LongCurve"/>).</summary>
    static (IReadOnlyList<int> Ticks, Curve Src) Keys(Curve src) => (CurveDecoder.KeyTicks(src), src);

    internal static bool SamePlants(Curve c, IReadOnlyList<FootPlant> keys)
    {
        if (c.Form != 22 || c.Keys != keys.Count) return false;
        for (int k = 0; k < keys.Count; k++)
        {
            var f = CurveDecoder.Plant(c, k)!.Value;
            if (f.Tick != keys[k].Tick || f.LengthTicks != keys[k].LengthTicks || (f.Point - keys[k].Point).Length() > 0.0005f) return false;
        }
        return true;
    }

    internal static byte[] WritePlants(IReadOnlyList<FootPlant> keys, string name, int track)
    {
        try { return CurveWriter.PlantCurve(keys); }
        catch (ArgumentException ex) { throw new EditRefusedException(EditRefusal.CantMove, $"{name}: plant track {track} can't be written: {ex.Message}", ex); }
    }

    static void Notes(ClipRecord clip, List<string> notes, List<string> keptBones, List<string> keptOther)
    {
        var groups = EventObjects.Groups(clip);
        int feet = groups.SelectMany(g => g.Events).Count(e => e.Class == EventObjects.StepEvent && e.Type is 6 or 8);
        int events = groups.Sum(g => g.Events.Count);
        if (feet > 0) notes.Add($"kept unmirrored: {feet} step objects' foot events (types 6 and 8), whose side, if they name one, isn't read");
        if (events > feet) notes.Add($"kept as they are: {events - feet} other event objects and their times");
        if (keptBones.Count > 0) notes.Add("kept unmirrored, bones without a counterpart: " + string.Join(", ", keptBones.Distinct()));
        if (keptOther.Count > 0) notes.Add("kept as they are, tracks whose meaning isn't known: " + string.Join(", ", keptOther.Distinct()));
    }
}
