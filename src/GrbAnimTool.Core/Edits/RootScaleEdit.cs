using System.Globalization;
using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Edits;

/// <summary>The mover's travel scaled: track 0's positions moved toward or away from its first place on the ground by a factor, its height
/// and its turn kept. The feet the clip plants are held where they stood on the ground (a two-bone leg solve that blends in and out over 4
/// ticks), the foot plants' points (each in the mover's frame at its plant's start) are read again from the scaled mover to where their foot
/// stands, and the ground marker is written again from the new mover. Refused on motion-field clips and on clips whose mover doesn't
/// travel.</summary>
public static class RootScaleEdit
{
    public const float MinFactor = 0.25f, MaxFactor = 4f;

    /// <summary>Where a foot counts as planted for the lock: the toe below 8 cm and moving under 0.3 m/s in the game's clip's world, for 4
    /// ticks or more.</summary>
    public const float PlantHeight = 0.08f, PlantSpeed = 0.3f;
    public const int PlantMinTicks = 4;

    static readonly string[] Sides = ["Left", "Right"];

    public static ScaledClip Apply(ClipRecord clip, EditContext ctx, float factor, bool measure = true) =>
        EditRebuild.Guard(clip.Name, () => Run(clip, ctx, factor, measure));

    static ScaledClip Run(ClipRecord clip, EditContext ctx, float factor, bool measure)
    {
        EditRebuild.Readable(clip);
        string name = clip.Name;
        if (!float.IsFinite(factor) || factor < MinFactor || factor > MaxFactor)
            throw new EditRefusedException(EditRefusal.BadParameter, $"{name}: a travel factor of {EditPackage.FactorText(factor)} is outside {EditPackage.FactorText(MinFactor)} to {EditPackage.FactorText(MaxFactor)}");
        var v = EditVerdicts.RequireMotionFree(name, ctx.Verdict, "speed, which a scale changes");
        var s = ctx.Skeleton;
        var table = ctx.Table;
        int last = clip.LastTick;
        int mp = -1;
        for (int i = 0; i < clip.Tracks.Count; i++)
            if (clip.Tracks[i] == 0 && EditRebuild.Resolved(clip, table, i).Kind == TrackKind.Position) { mp = i; break; }
        if (mp < 0) throw new EditRefusedException(EditRefusal.NoRootMotion, $"{name}: it has no mover position track");
        var moverCurve = EditRebuild.Resolved(clip, table, mp);
        var before = GroundMarker.Mover(clip, table, last);
        var p0 = before[0].Position;
        double travel = before.Max(m => new Vector2(m.Position.X - p0.X, m.Position.Y - p0.Y).Length());
        if (moverCurve.Keys < 2 || travel < 0.01)
            throw new EditRefusedException(EditRefusal.NoRootMotion, $"{name}: its mover travels {travel * 100:F2} cm on the ground, nothing to scale");
        var notes = new List<string>();
        var marker = GroundMarker.Of(clip, table);
        if (marker is { Follows: false }) { notes.Add(marker.NotFollowing("kept as it is")); marker = null; }
        var curves = new Dictionary<int, byte[]>();
        Vector3 Scaled(Vector3 p) => new(p0.X + (p.X - p0.X) * factor, p0.Y + (p.Y - p0.Y) * factor, p.Z);

        // the mover, key by key (a linear map, so its blend between keys is the blend of the scaled keys)
        {
            var mt = CurveDecoder.KeyTicks(moverCurve);
            // a factor that moves no key leaves the curve as it is, however many keys it holds
            Vector3 Key(int k) => CurveDecoder.KeyPos(moverCurve, k)
                ?? throw new EditRefusedException(EditRefusal.Unreadable, $"{name}: the mover's position key {k} (tag 0x{moverCurve.Tag:X2}) doesn't decode");
            bool moves = Enumerable.Range(0, mt.Length).Any(k => (Scaled(Key(k)) - Key(k)).Length() > 1e-6f);
            var (ticks, own) = KeysOf(moverCurve, last);
            // its own keys each keep their own value, so two keys at one tick (a step) stay a step
            var p = own ? ticks.Select((_, k) => Scaled(Key(k))).ToList() : ticks.Select(t => Scaled(CurveValues.Pos(moverCurve, mt, t))).ToList();
            if (moves && !CurveValues.SamePos(moverCurve, ticks, p))
            {
                curves[mp] = CurveWriter.PosCurve(ticks, p);
                if (!own) notes.Add($"the mover's position curve ({mt.Length} keys) keyed again every {KeyingRule.Step(last)} ticks by the keying rule, joined straight between keys");
            }
        }
        var after = before.Select(m => new Transform(Scaled(m.Position), m.Rotation)).ToArray();

        // the feet: planted spans read on the game's clip, then each held at its place on the ground in the scaled clip
        var game = ClipMotion.Sample(clip, s, table);
        var scaled = game.Copy();
        for (int t = 0; t <= last; t++) scaled.Mover[t] = after[t];
        var spans = Plants(game);
        float unlocked = 0;
        var touched = new HashSet<int>();
        foreach (var (side, from, to) in spans)
        {
            var d0 = after[from].Position - before[from].Position;
            for (int t = from; t <= to; t++) unlocked = MathF.Max(unlocked, ((after[t].Position - before[t].Position) - d0).Length());
            foreach (int b in Lock(game, scaled, side, from, to, before, after)) touched.Add(b);
        }
        if (spans.Count > 0) notes.Add($"the foot lock held {spans.Count} planted spans ({string.Join(", ", spans.Select(x => $"{x.Side} {x.From}-{x.To}"))})");
        else notes.Add("no planted foot was found in the game's clip (a toe below 8 cm and still for 4 ticks), so no foot lock ran");

        // the legs the lock moved, keyed by the keying rule over the whole clip
        if (touched.Count > 0)
        {
            var ticks = KeyingRule.Ticks(last);
            foreach (int b in touched)
            {
                uint h = s.Bones[b].Hash;
                int ti = -1;
                for (int i = 0; i < clip.Tracks.Count; i++)
                    if (clip.Tracks[i] == h && EditRebuild.Resolved(clip, table, i).Kind == TrackKind.Rotation) { ti = i; break; }
                if (ti < 0) throw new EditRefusedException(EditRefusal.CantMove, $"{name}: the foot lock turns {s.Bones[b].Name}, which has no rotation track in this clip");
                var q = ticks.Select(t => scaled.Locals[t][b].Rotation).ToList();
                curves[ti] = CurveValues.WriteRot(EditRebuild.Resolved(clip, table, ti), ticks, q);
            }
            notes.Add($"the {touched.Count} leg curves the foot lock turned are keyed every {KeyingRule.Step(last)} ticks over the whole clip by the keying rule, joined straight between keys");
        }

        // the plants' points: each stands in the mover's frame at its plant's start tick and marks where its foot is planted. The game's point
        // in the world moves as the foot moved at the plant's middle tick (the lock holds a planted foot where the game had it, shifted by
        // the travel's change at its span's start), and is then read in the scaled mover's frame at the start tick. A point whose foot and
        // mover move alike over the plant's first half is unchanged. Where the lock turned the legs, the feet are read from the record as it
        // will be written (the legs keyed by the keying rule), not from the lock's own ticks, so the points follow the feet the game plays
        var feet = scaled;
        if (touched.Count > 0 && Enumerable.Range(0, clip.Tracks.Count).Any(i => EditRebuild.Resolved(clip, table, i).Kind == TrackKind.Plant))
        {
            var trial = EditRebuild.Rebuild(clip, table, curves);
            feet = ClipMotion.Sample(EditRebuild.Done(trial.Bytes, trial, table, []).Record, s, table);
        }
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            var u = EditRebuild.Resolved(clip, table, i);
            if (u.Kind != TrackKind.Plant) continue;
            if (u.Form != 22) throw new EditRefusedException(EditRefusal.CantMove, $"{name}: plant track {i} is in tag 0x{u.Tag:X2}, a form with no reader");
            int foot = EditTracks.IsPlantSlot(clip.Tracks[i]) ? s.IndexOf(Crc32.Of(Sides[EditTracks.PlantSide(clip.Tracks[i])] + "Foot")) : -1;
            var keys = new List<FootPlant>();
            for (int k = 0; k < u.Keys; k++)
            {
                var f = CurveDecoder.Plant(u, k)!.Value;
                bool zero = f.LengthTicks == 0 && f.Point == Vector3.Zero;
                if (zero) { keys.Add(f); continue; }
                int t = Math.Clamp(f.Tick, 0, last), mid = Math.Clamp(f.Tick + Math.Max(0, f.LengthTicks) / 2, 0, last);
                var d = after[t].Position - before[t].Position;
                var moved = foot >= 0 ? feet.World(mid)[foot].Position - game.World(mid)[foot].Position : d;
                var shift = Vector3.Transform(new Vector3(moved.X - d.X, moved.Y - d.Y, 0), Quaternion.Inverse(after[t].Rotation));
                keys.Add(f with { Point = f.Point + shift });
            }
            if (!MirrorEdit.SamePlants(u, keys)) curves[i] = MirrorEdit.WritePlants(keys, name, i);
        }
        if (curves.ContainsKey(mp)) marker?.Rewrite(clip, table, after, marker.E, curves, notes);

        EditRebuild.NoteWideRotations(curves, notes);
        var built = EditRebuild.Rebuild(clip, table, curves);
        var done = EditRebuild.Done(built.Bytes, built, table, notes);
        double travelAfter = travel * factor;
        double residual = measure ? Residual(game, ClipMotion.Sample(done.Record, s, table), spans) : double.NaN;
        notes.Add(string.Create(CultureInfo.InvariantCulture, $"the mover's travel {travel * 100:F1} cm scaled by {EditPackage.FactorText(factor)} to {travelAfter * 100:F1} cm; its turn kept"));
        if (measure) notes.Add($"planted feet: at most {residual * 100:F2} cm of slide beyond the game's clip, read back from the written record (with no lock {unlocked * 100:F2} cm)");
        return new ScaledClip(done.Bytes, done.Record, built, notes, spans, residual * 100, unlocked * 100, (travel * 100, travelAfter * 100))
        {
            Warnings = EditVerdicts.Warnings(v, "root speed"),
        };
    }

    static (IReadOnlyList<int> Ticks, bool Own) KeysOf(Curve c, int last)
    {
        var t = CurveDecoder.KeyTicks(c);
        return t.Length <= CurveWriter.MaxShortKeys && t[0] == 0 ? (t, true) : (KeyingRule.Ticks(last), false);
    }

    /// <summary>The planted spans of each foot in the game's clip: the toe below <see cref="PlantHeight"/> and moving under
    /// <see cref="PlantSpeed"/> in the clip's world, for <see cref="PlantMinTicks"/> ticks or more.</summary>
    public static List<(string Side, int From, int To)> Plants(ClipMotion m)
    {
        var o = new List<(string, int, int)>();
        var world = Enumerable.Range(0, m.Last + 1).Select(m.World).ToArray();
        foreach (var side in Sides)
        {
            int toe = m.Skeleton.IndexOf(Crc32.Of(side + "ToeBase"));
            if (toe < 0) continue;
            int start = -1;
            for (int t = 0; t <= m.Last + 1; t++)
            {
                bool down = false;
                if (t <= m.Last)
                {
                    var w = world[t][toe].Position;
                    var wp = world[Math.Max(0, t - 1)][toe].Position;
                    down = w.Z < PlantHeight && (w - wp).Length() * ClipRecord.TicksPerSecond < PlantSpeed;
                }
                if (down && start < 0) start = t;
                if (!down && start >= 0)
                {
                    if (t - start >= PlantMinTicks) o.Add((side, start, t - 1));
                    start = -1;
                }
            }
        }
        return o;
    }

    /// <summary>Holds one foot's ankle, over a planted span, where the game's clip has it on the ground: at each tick the game's ankle place
    /// moved by the travel's change at the span's start, blended in over the 4 ticks before and out over the 4 after. A two-bone solve turns
    /// the thigh and the shin, the knee kept in its own plane; the foot keeps its turn in the world. Returns the bones it turned.</summary>
    static IEnumerable<int> Lock(ClipMotion game, ClipMotion s, string side, int from, int to, Transform[] before, Transform[] after)
    {
        var sk = s.Skeleton;
        int up = sk.IndexOf(Crc32.Of(side + "UpLeg")), leg = sk.IndexOf(Crc32.Of(side + "Leg")), foot = sk.IndexOf(Crc32.Of(side + "Foot"));
        if (up < 0 || leg < 0 || foot < 0) yield break;
        var hold = after[from].Position - before[from].Position;
        Vector3? lastKnee = null;
        bool any = false;
        for (int t = Math.Max(0, from - 4); t <= Math.Min(s.Last, to + 4); t++)
        {
            float wgt = t < from ? 1 - (from - t) / 5f : t > to ? 1 - (t - to) / 5f : 1;
            var w = s.World(t);
            var g = game.World(t);
            var hp = w[up].Position; var k = w[leg].Position; var a = w[foot].Position;
            var target = Vector3.Lerp(a, g[foot].Position + hold, wgt);
            if ((target - a).Length() < 1e-4f) continue;
            var footQ = w[foot].Rotation;
            float l1 = (k - hp).Length(), l2 = (a - k).Length();
            var d = target - hp;
            float dl = MathF.Min(d.Length(), (l1 + l2) * 0.999f);
            if (dl < 1e-4f) continue;
            var dn = Vector3.Normalize(d);
            var kOff = (k - hp) - Vector3.Dot(k - hp, dn) * dn;
            // the knee's direction across the hip-ankle line: its own, plus a little of last tick's, so a nearly straight leg doesn't swing
            var kv = kOff;
            if (lastKnee is { } pk) kv += (pk - Vector3.Dot(pk, dn) * dn) * 0.06f;
            if (kv.LengthSquared() < 1e-10f) kv = Vector3.Cross(dn, Vector3.UnitX);
            var kn = Vector3.Normalize(kv);
            lastKnee = kn;
            float along = (l1 * l1 + dl * dl - l2 * l2) / (2 * dl);
            float hgt = MathF.Sqrt(MathF.Max(0, l1 * l1 - along * along));
            var k2 = hp + dn * along + kn * hgt;
            var a2 = hp + dn * dl;
            s.SetWorldRotation(t, up, Quaternion.Normalize(Between(k - hp, k2 - hp) * w[up].Rotation), w);
            var w2 = s.World(t);
            s.SetWorldRotation(t, leg, Quaternion.Normalize(Between(w2[foot].Position - w2[leg].Position, a2 - w2[leg].Position) * w2[leg].Rotation), w2);
            var w3 = s.World(t);
            s.SetWorldRotation(t, foot, footQ, w3);
            any = true;
        }
        if (any) { yield return up; yield return leg; yield return foot; }
    }

    /// <summary>The rotation that takes direction u onto direction v.</summary>
    static Quaternion Between(Vector3 u, Vector3 v)
    {
        u = Vector3.Normalize(u); v = Vector3.Normalize(v);
        float d = Math.Clamp(Vector3.Dot(u, v), -1, 1);
        var ax = Vector3.Cross(u, v);
        if (ax.LengthSquared() < 1e-12f)
            return d > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(u, MathF.Abs(u.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY)), MathF.PI);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), MathF.Acos(d));
    }

    /// <summary>The most a planted ankle slides in <paramref name="edited"/> beyond its slide in <paramref name="game"/> over the same span
    /// (metres): each tick's way from the span's first tick, against the game's.</summary>
    public static double Residual(ClipMotion game, ClipMotion edited, IReadOnlyList<(string Side, int From, int To)> spans)
    {
        double worst = 0;
        foreach (var (side, from, to) in spans)
        {
            int foot = game.Skeleton.IndexOf(Crc32.Of(side + "Foot"));
            if (foot < 0) continue;   // a toe without a foot bone: the lock held nothing on this side
            var g0 = game.World(from)[foot].Position; var e0 = edited.World(from)[foot].Position;
            for (int t = from; t <= to; t++)
            {
                var gd = game.World(t)[foot].Position - g0; var ed = edited.World(t)[foot].Position - e0;
                worst = Math.Max(worst, (ed - gd).Length());
            }
        }
        return worst;
    }
}
