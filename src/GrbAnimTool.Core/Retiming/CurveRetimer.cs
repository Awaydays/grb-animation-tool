using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Retiming;

/// <summary>One curve re-timed by a <see cref="TimeWarp"/>. Each returns the new curve's bytes, or null when the curve stays as it is (one
/// key, or a warp that leaves every key where it was). Every new curve is written in a one- or two-byte time form (a bitmap time form is
/// converted), its first key at tick 0, and no curve passes 255 keys: one that would is keyed by <see cref="KeyingRule"/>, or refused when
/// its values aren't decoded.</summary>
public static class CurveRetimer
{
    /// <summary>A rotation or position curve, re-sampled: keys at the new ticks of its own keys (rounded), at tick 0, and at the new last tick
    /// where the warp cuts keys off the end; each value the source curve's blend at the source time that plays there, then through
    /// <paramref name="rotMap"/> or <paramref name="posMap"/> when given. Two keys at one source tick (a step) stay two keys where the new
    /// tick plays that source tick exactly. <paramref name="rekeyed"/> is called when the curve written is keyed by the keying rule instead,
    /// its own keys being more than 255.</summary>
    public static byte[]? Interpolated(Curve u, TimeWarp w, Func<Quaternion, Quaternion>? rotMap = null, Func<Vector3, Vector3>? posMap = null, Action? rekeyed = null)
    {
        bool rot = u.Kind == TrackKind.Rotation;
        if (u.Kind is not (TrackKind.Rotation or TrackKind.Position)) throw new ArgumentException("not a rotation or position curve");
        if (rot ? CurveDecoder.KeyRot(u, 0) is null : CurveDecoder.KeyPos(u, 0) is null)
            throw new EditRefusedException(EditRefusal.CantMove, $"a {(rot ? "rotation" : "position")} curve in tag 0x{u.Tag:X2}, whose values aren't decoded, can't be re-timed");
        var t = CurveDecoder.KeyTicks(u);
        bool mapped = rotMap is not null || posMap is not null;
        if ((u.Keys == 1 || w.IsIdentity) && !mapped) return null;
        int last = w.NewLast;
        // the new keys: (tick, the source time whose value it takes, which key when that time is a key's own exactly)
        var keys = new List<(int Tick, double Src, int Key)>();
        if (u.Keys == 1) keys.Add((0, 0, 0));
        else
        {
            keys.Add((0, w.SourceOf(0), -1));
            for (int k = 0; k < t.Length; k++)
            {
                double x = w.Inverse(t[k]);
                if (x < -0.5 || x > last + 0.5) continue;
                int nt = Math.Clamp((int)Math.Round(x), 0, last);
                bool exact = Math.Abs(w.SourceOf(nt) - t[k]) < 1e-9;
                keys.Add((nt, w.SourceOf(nt), exact ? k : -1));
            }
            if (t[^1] > w.SourceOf(last) + 1e-6) keys.Add((last, w.SourceOf(last), -1));
        }
        // one key a tick, but both keys of a step that plays exactly
        var list = new List<(int Tick, double Src, int Key)>();
        foreach (var g in keys.GroupBy(k => k.Tick).OrderBy(g => g.Key))
        {
            var exact = g.Where(k => k.Key >= 0).Select(k => k.Key).Distinct().OrderBy(k => k).ToList();
            if (exact.Count >= 2 && exact[^1] - exact[0] == exact.Count - 1) { list.Add((g.Key, -1, exact[0])); list.Add((g.Key, -1, exact[^1])); }
            else list.Add((g.Key, g.First().Src, exact.Count == 1 ? exact[0] : -1));
        }
        bool byRule = list.Count > CurveWriter.MaxShortKeys;
        if (byRule) list = KeyingRule.Ticks(last).Select(x => (x, w.SourceOf(x), -1)).ToList();
        var ticks = list.Select(k => k.Tick).ToList();
        if (!mapped && ticks.SequenceEqual(t) && list.All(k => k.Key >= 0 || Math.Abs(k.Src - k.Tick) < 1e-9 && t.Contains(k.Tick))) return null;
        if (rot)
        {
            Quaternion KeyRot(int k) => CurveDecoder.KeyRot(u, k) ?? throw new EditRefusedException(EditRefusal.Unreadable, $"a rotation key (tag 0x{u.Tag:X2}, key {k}) doesn't decode");
            var q = list.Select(k => k.Key >= 0 ? KeyRot(k.Key) : CurveValues.Rot(u, t, k.Src)).Select(x => rotMap is null ? x : rotMap(x)).ToList();
            if (CurveValues.SameRot(u, ticks, q, 1e-5)) return null;
            if (byRule) rekeyed?.Invoke();
            return CurveValues.WriteRot(u, ticks, q);
        }
        Vector3 KeyPos(int k) => CurveDecoder.KeyPos(u, k) ?? throw new EditRefusedException(EditRefusal.Unreadable, $"a position key (tag 0x{u.Tag:X2}, key {k}) doesn't decode");
        var p = list.Select(k => k.Key >= 0 ? KeyPos(k.Key) : CurveValues.Pos(u, t, k.Src)).Select(x => posMap is null ? x : posMap(x)).ToList();
        if (CurveValues.SamePos(u, ticks, p, 0)) return null;
        if (byRule) rekeyed?.Invoke();
        return CurveWriter.PosCurve(ticks, p);
    }

    /// <summary>Whether a form's values are decoded by a reader (bytes, form 13, and floats, form 18); the others are moved by key time with
    /// their bytes as stored.</summary>
    public static bool Decoded(int form) => form is 13 or 18;

    /// <summary>A curve whose values are moved as stored, by key time, with no blend: a key at tick 0 and at the first new tick whose source
    /// time reaches each source key inside the new clip; each new key takes the value of the source's last key at or before the source time
    /// that plays there (the nearest earlier key, so keys that land on one tick keep the latest of them). Keys past the cut are dropped. A
    /// result of 256 keys or more is keyed by the keying rule for bytes and floats, and refused for a form whose values aren't decoded.</summary>
    public static byte[]? Stepped(Curve u, TimeWarp w, string clip, int track, List<string>? notes = null)
    {
        if (u.Keys == 1 || w.IsIdentity) return null;
        int e = CurveTags.ValueBytes(u.Tag);
        if (e <= 0) throw new EditRefusedException(EditRefusal.CantMove, $"{clip}: track {track}'s curve, tag 0x{u.Tag:X2}, is of a form whose value size isn't known");
        var t = CurveDecoder.KeyTicks(u);
        int last = w.NewLast;
        var set = new SortedSet<int> { 0 };
        for (int k = 0; k < t.Length; k++)
            if (t[k] > w.SourceOf(0) + 1e-6 && t[k] <= w.SourceOf(last) + 1e-6) set.Add(w.FirstReaching(t[k]));
        var ticks = set.ToList();
        if (ticks.Count > CurveWriter.MaxShortKeys)
        {
            if (!Decoded(u.Form))
                throw new EditRefusedException(EditRefusal.TooManyKeys, $"{clip}: track {track}'s curve (tag 0x{u.Tag:X2}, form {u.Form}, values not decoded) would hold {ticks.Count} keys after the re-timing; 255 is the most, and its values can't be re-keyed");
            ticks = [.. KeyingRule.Ticks(last)];
            notes?.Add($"track {track} (form {u.Form}): {set.Count} keys re-keyed every {KeyingRule.Step(last)} ticks by the keying rule");
        }
        var vals = ticks.Select(nt => ScalarDecoder.KeyRaw(u, CurveValues.KeyAtOrBefore(t, w.SourceOf(nt)))
            ?? throw new EditRefusedException(EditRefusal.Unreadable, $"{clip}: track {track}'s values don't fit its curve")).ToList();
        if (ticks.SequenceEqual(t) && Enumerable.Range(0, t.Length).All(k => vals[k].AsSpan().SequenceEqual(ScalarDecoder.KeyRaw(u, k)))) return null;
        return WriteRaw(u.Form, ticks, vals, clip, track);
    }

    /// <summary>The alignment a curve's header is padded to before its values, by value size: as the game's own curves of these forms have it.</summary>
    public static int Alignment(int valueBytes) => valueBytes switch
    {
        1 => 1,
        2 or 6 => 2,
        4 or 8 or 12 => 4,
        _ => 0,
    };

    /// <summary>A curve of any form with a known value size, its values as stored, in the one-byte time form (two-byte past tick 255).</summary>
    public static byte[] WriteRaw(int form, IReadOnlyList<int> ticks, IReadOnlyList<byte[]> values, string clip = "", int track = -1)
    {
        byte baseTag = (byte)(form * 3);
        int e = CurveTags.ValueBytes(baseTag), a = Alignment(e);
        if (e <= 0 || a == 0) throw new EditRefusedException(EditRefusal.CantMove, $"{clip}: track {track}'s form {form} has no known layout to write");
        try { return CurveWriter.CurveN(baseTag, a, ticks, values); }
        catch (ArgumentException ex) { throw new EditRefusedException(EditRefusal.CantMove, $"{clip}: track {track} can't be written: {ex.Message}", ex); }
    }

    /// <summary>Foot plants re-timed: each key's start and end (its tick plus its length) taken to the first new tick whose source time
    /// reaches them; a plant the warp cuts at its start starts at tick 0, one wholly before the new clip or after it is dropped; the length
    /// kept within the new clip. Keys that land on one tick keep the first. A first key at tick 0 is added (all zeros, the foot in the air)
    /// where none lands there. Each point through <paramref name="pointMap"/> when given; a key of zeros stays zeros.</summary>
    public static byte[]? Plants(Curve u, TimeWarp w, string clip, int track, Func<Vector3, Vector3>? pointMap = null) =>
        PlantsAt(u, w, clip, track, pointMap is null ? null : (_, _, p) => pointMap(p));

    /// <summary>As <see cref="Plants"/>, each point through <paramref name="pointAt"/> (the key's source start tick, its new start tick, the
    /// point) when given: a point stands in the mover's frame at its plant's start, so where the new start plays another source time than
    /// the key's own (a plant the cut starts, or a start tick rounded by the warp) it is read again in the mover's frame there.</summary>
    public static byte[]? PlantsAt(Curve u, TimeWarp w, string clip, int track, Func<int, int, Vector3, Vector3>? pointAt)
    {
        if (u.Form != 22) throw new EditRefusedException(EditRefusal.CantMove, $"{clip}: plant track {track} is in tag 0x{u.Tag:X2}, a form with no reader");
        if (w.IsIdentity && pointAt is null) return null;
        int last = w.NewLast;
        double s0 = w.SourceOf(0), s1 = w.SourceOf(last);
        var keys = new List<FootPlant>();
        for (int k = 0; k < u.Keys; k++)
        {
            var f = CurveDecoder.Plant(u, k) ?? throw new EditRefusedException(EditRefusal.Unreadable, $"{clip}: plant track {track}'s key {k} doesn't decode");
            // a key of zeros says the foot is in the air from there; it is added again below where the new clip starts in the air
            if (f.LengthTicks == 0 && f.Point == Vector3.Zero) continue;
            int len0 = Math.Max(0, f.LengthTicks), end = f.Tick + len0;
            if (end < s0 - 1e-6 || f.Tick > s1 + 1e-6) continue;
            int nt = f.Tick <= s0 + 1e-6 ? 0 : w.FirstReaching(f.Tick);
            int ne = len0 == 0 ? nt : Math.Max(nt + 1, w.FirstReaching(end));
            if (keys.Count > 0 && nt <= keys[^1].Tick) continue;
            int len = len0 == 0 ? 0 : Math.Min(ne - nt, last - nt);
            keys.Add(new FootPlant(nt, Math.Max(0, len), pointAt is null ? f.Point : pointAt(f.Tick, nt, f.Point)));
        }
        if (keys.Count == 0 || keys[0].Tick != 0) keys.Insert(0, new FootPlant(0, 0, Vector3.Zero));
        if (MirrorEdit.SamePlants(u, keys)) return null;
        return MirrorEdit.WritePlants(keys, clip, track);
    }
}
