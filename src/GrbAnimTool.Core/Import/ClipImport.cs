using System.Globalization;
using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Import;

/// <summary>Why an import was refused.</summary>
public enum ImportRefusal
{
    /// <summary>The file can't be read as a glTF binary.</summary>
    File,
    /// <summary>The slot's own record can't be rebuilt (it doesn't read to its end, or is shorter than a tick).</summary>
    Slot,
    /// <summary>No animation on the armature, or more than one.</summary>
    Animation,
    /// <summary>The file's bones aren't the game skeleton's: missing, under other parents, or at another rest pose.</summary>
    BoneSet,
    /// <summary>The axes aren't turned for glTF, or the armature is turned over.</summary>
    Axes,
    /// <summary>A bone or the armature is scaled.</summary>
    Scale,
    /// <summary>The animation isn't the slot's length.</summary>
    Length,
    /// <summary>The slot's head lacks an arena kind the new curves need (the rebuild's own refusal).</summary>
    Arena,
}

/// <summary>An import was refused; nothing was made. The message says why in plain words and what to do.</summary>
public sealed class ImportRefusedException(string message, ImportRefusal reason, Exception? inner = null) : Exception(message, inner)
{
    public ImportRefusal Reason { get; } = reason;
}

/// <summary>What became of one of the slot's tracks.</summary>
public sealed record TrackFate(int Index, uint Hash, string Name, TrackKind Kind, bool Written, string What)
{
    public override string ToString() => $"{Index,4} {Hash:X8} {Name,-24} {Kind,-9} {(Written ? "written" : "kept   ")}  {What}";
}

/// <summary>An import's result: the slot's record rebuilt around the new curves, kept in memory, with its check report, the game's clip and
/// the import as the record now plays it (decoded back from the bytes), and what became of every track.</summary>
public sealed class ImportResult
{
    public required ClipEntry Slot { get; init; }
    public required string Source { get; init; }
    public required string SourceSha256 { get; init; }
    public required RebuiltClip Rebuilt { get; init; }
    /// <summary>The whole rebuilt record, as a datafile holds it.</summary>
    public byte[] Record => Rebuilt.Bytes;
    public required CheckReport Report { get; init; }
    /// <summary>The slot's own clip, decoded.</summary>
    public required Motion Game { get; init; }
    /// <summary>The rebuilt record decoded back: what the game will play.</summary>
    public required Motion Imported { get; init; }
    /// <summary>The file's motion as it stands, every bone (including bones the slot has no track for).</summary>
    public required Motion FromFile { get; init; }
    public required IReadOnlyList<TrackFate> Fates { get; init; }
    /// <summary>The ticks between keys of a written curve: 1, or the keying rule's step for a long slot.</summary>
    public required int KeyStep { get; init; }

    public int Written => Fates.Count(f => f.Written);
    public int Kept => Fates.Count(f => !f.Written);

    /// <summary>The report as text: what was imported into which slot, the check lines, the verdict, then every track's fate.</summary>
    public string ReportText()
    {
        var c = Slot;
        string head = FormattableString.Invariant($"Import of {Path.GetFileName(Source)} (SHA-256 {SourceSha256}) into {c.Index}_-_{c.Name} 0x{c.Id:X16}, {c.Duration:F3} s, {c.LastTick} ticks, {c.TrackCount} tracks\n")
            + FormattableString.Invariant($"The rebuilt record: {Record.Length} B (the game's {c.Bytes.Length} B), SHA-256 {Sha256Hex.Of(Record)}; {Written} tracks written, {Kept} keep the game's bytes");
        var sb = new System.Text.StringBuilder(Report.ToText(head));
        sb.AppendLine("Tracks:");
        foreach (var f in Fates) sb.AppendLine("   " + f);
        return sb.ToString();
    }
}

/// <summary>Imports a clip made in a glTF tool into a slot: a glTF binary on the game's skeleton (the tool's own export, edited, then
/// exported as glTF) read channel by channel and evaluated at every tick of the slot (60 a second), then written into
/// the slot's own record.
/// <para>What is kept: the slot's ID, name, duration, hash words, track list and every object; every track that isn't a bone's rotation or
/// position keeps its bytes (hand weights, the IK offsets, helpers, byte and float tracks, forms 14 and 15, foot plants, the events), and so
/// do the hand tracks (<see cref="HandTracks"/>) and any bone track the file leaves as the game had it. What is written: every other bone
/// rotation (6-byte form, 0x0C/0x0D, or 12-byte, 0x18/0x19, only where the slot's head lacks the 6-byte form's arena kind, with a warning) and position (0x1B/0x1C) the slot holds,
/// and the mover (track 0) from the armature's motion, one key where the value holds still, else a key every tick, or every
/// <see cref="KeyingRule.Step"/> ticks in a slot of 255 ticks or more, so no curve passes 255 keys (or, where keys that far apart would miss
/// a curve's motion, up to 255 keys placed where it needs them).</para>
/// <para>Refused, plainly: a file that can't be read, the bones not the game's (missing, other parents, another rest), the axes not turned,
/// a scale, an animation other than one, a length other than the slot's, and a head lacking an arena kind the new curves need. Warned: a hand
/// that moves in an armed slot, a slot the graph warns on replacing (a motion field's, a blend's child, one only GR_Graph's list names, or one
/// whose answer isn't known), motion the slot can't hold, and the motion checks.</para></summary>
public static class ClipImport
{
    /// <summary>A track is left as the game had it when the file's values match it at every tick within these, plus
    /// <see cref="SameStepShare"/> of the track's own step from that tick to its neighbours.</summary>
    public const double SameDeg = 0.01, SameCm = 0.01;
    /// <summary>The share of a track's own step to the neighbouring ticks (the larger of the two) added to <see cref="SameDeg"/> and
    /// <see cref="SameCm"/> at each tick. A key's time is stored as a float, so a file's key for tick t stands a hair before or after t
    /// (up to some thousandths of a tick late in a long clip), and the file read at t takes that part of the step to the next key. Beside a
    /// track that jumps metres in one tick (the mover, <c>Reference</c> or a helper in a long motion-field clip) that part is up to a few
    /// tenths of a centimetre: without this room such a track, untouched, would be written again and, in a slot of 255 ticks or more,
    /// could not be held in 255 keys.</summary>
    public const double SameStepShare = 0.01;
    /// <summary>A curve is written as one key when its values hold still within these.</summary>
    const double StillDeg = 0.002, StillCm = 0.001;
    /// <summary>A scale off 1 by more than this is refused.</summary>
    public const double ScaleTolerance = 1e-3;
    /// <summary>The name of the report's line on the graph's word about the slot: motion fields and blends.</summary>
    public const string GraphCheckName = "motion field and blends";
    /// <summary>The largest file taken: 64 MB, some six times the largest the tool's own export writes (about 10 MB, for a clip of 8,940
    /// ticks; a clip of 2,866 ticks exports to 3.2 MB).</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    static string F(double v, string f = "F3") => v.ToString(f, CultureInfo.InvariantCulture);

    /// <summary>Imports <paramref name="glbPath"/> into <paramref name="slot"/> of <paramref name="g"/>, with the motion-field verdict of
    /// <paramref name="graph"/> when given.</summary>
    public static ImportResult Run(GameInstall g, ClipEntry slot, string glbPath, ClipGraph? graph = null)
    {
        byte[] bytes;
        try
        {
            var fi = new FileInfo(glbPath);
            if (!fi.Exists) throw new ImportRefusedException($"{glbPath} doesn't exist.", ImportRefusal.File);
            if (fi.Length > MaxFileBytes) throw new ImportRefusedException($"{glbPath} is {fi.Length / 1048576} MB, too large for a clip.", ImportRefusal.File);
            bytes = File.ReadAllBytes(glbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ImportRefusedException($"{glbPath} can't be read: {ex.Message}", ImportRefusal.File, ex); }
        return Run(g.Skeleton, g.SharedTable, slot, bytes, Path.GetFullPath(glbPath), graph?.Verdict(slot.Id), graph is not null);
    }

    /// <summary>Imports a .glb's bytes; a file that can't be read as a glTF binary is refused (<see cref="ImportRefusal.File"/>).</summary>
    public static ImportResult Run(Skeleton s, SharedCurveTable? table, ClipEntry slot, byte[] glb, string source, ClipGraphVerdict? verdict = null, bool graphRead = false)
    {
        GlbScene scene;
        try { scene = GlbScene.Read(glb); }
        catch (InvalidDataException ex) { throw new ImportRefusedException($"{Path.GetFileName(source)} can't be read as a glTF binary: {ex.Message}.", ImportRefusal.File, ex); }
        return Run(s, table, slot, scene, source, Sha256Hex.Of(glb), verdict, graphRead);
    }

    /// <summary>The import itself, on any skeleton and shared table (the tests' synthetic ones too).</summary>
    public static ImportResult Run(Skeleton s, SharedCurveTable? table, ClipEntry slot, GlbScene scene, string source, string sourceSha256,
        ClipGraphVerdict? verdict = null, bool graphRead = false)
    {
        var rec = slot.Record ?? throw new ImportRefusedException($"{slot.Name}'s record can't be read ({slot.ParseError}), so nothing can be imported into it.", ImportRefusal.Slot);
        if (!rec.WalkedToEnd) throw new ImportRefusedException($"{slot.Name}'s curves don't read to the record's end ({rec.Stop}), so it can't be rebuilt.", ImportRefusal.Slot);
        if (rec.LastTick < 1) throw new ImportRefusedException($"{slot.Name} is shorter than one tick.", ImportRefusal.Slot);
        int last = rec.LastTick;
        int n = s.Bones.Count;
        var report = new CheckReport();

        // ---- the file's bones and armature
        var nodes = scene.Nodes;
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
            if (s.IndexOf(BoneHash(nodes[i].Name)) is >= 0 && s.Bones.Any(b => b.Name == nodes[i].Name) && !byName.TryAdd(nodes[i].Name, i))
                throw new ImportRefusedException($"two nodes in the file are named {nodes[i].Name}.", ImportRefusal.BoneSet);
        var boneNode = new int[n];
        for (int b = 0; b < n; b++) boneNode[b] = byName.GetValueOrDefault(s.Bones[b].Name, -1);
        var boneNodes = boneNode.Where(x => x >= 0).ToHashSet();
        if (boneNodes.Count == 0)
            throw new ImportRefusedException("none of the game skeleton's bones are in the file. Start from the tool's own export of the clip (or of the skeleton), animate that armature, and export it.", ImportRefusal.BoneSet);
        var armParents = boneNodes.Where(x => !boneNodes.Contains(nodes[x].Parent)).Select(x => nodes[x].Parent).Distinct().ToList();
        if (armParents.Count != 1)
            throw new ImportRefusedException("the skeleton's bones don't hang from one armature in the file. Keep every bone under the one armature of the tool's export, with no bones split off or added between them.", ImportRefusal.BoneSet);
        int armature = armParents[0];
        var chain = new List<int>();   // the armature and the nodes above it, armature first
        for (int p = armature; p >= 0; p = nodes[p].Parent) chain.Add(p);

        var fileBones = new Dictionary<string, FileBone>(StringComparer.Ordinal);
        var under = UnderArmature(nodes, armature);
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!under[i]) continue;
            int p = nodes[i].Parent;
            fileBones.TryAdd(nodes[i].Name, new FileBone(p == armature ? null : nodes[p].Name, GltfAxes.FromGltf(nodes[i].Translation), GltfAxes.FromGltf(nodes[i].Rotation)));
        }
        var bm = BoneMatch.Check(s, fileBones);
        report.Add(bm.Check);
        if (bm.Fault != BoneMatchFault.None)
            throw new ImportRefusedException(bm.Check.Message + ".", bm.Fault == BoneMatchFault.Axes ? ImportRefusal.Axes : ImportRefusal.BoneSet);

        // ---- the animation on them
        var relevant = boneNodes.Concat(chain).ToHashSet();
        var anims = scene.Animations.Where(a => a.Channels.Any(c => relevant.Contains(c.Node))).ToList();
        if (anims.Count == 0) throw new ImportRefusedException("the file holds no animation on the armature. Export the animation with it.", ImportRefusal.Animation);
        if (anims.Count > 1)
            throw new ImportRefusedException($"the file holds {anims.Count} animations on the armature ({string.Join(", ", anims.Select(a => a.Name ?? "unnamed"))}): export the clip's "
                + "one animation only, then import again.", ImportRefusal.Animation);
        var chans = anims[0].Channels.Where(c => relevant.Contains(c.Node)).ToList();

        // ---- scale: none on the bones or the armature
        double scaleOff = 0; string scaleAt = "";
        foreach (int i in relevant)
        {
            var sc = nodes[i].Scale;
            double d = Math.Max(Math.Abs(sc.X - 1), Math.Max(Math.Abs(sc.Y - 1), Math.Abs(sc.Z - 1)));
            if (d > scaleOff) { scaleOff = d; scaleAt = nodes[i].Name; }
        }
        foreach (var c in chans.Where(c => c.Path == "scale"))
        {
            // a cubic spline holds an in-tangent, the value and an out-tangent a key: only the values are scales
            bool cubic = c.Interpolation == GltfInterpolation.CubicSpline;
            for (int k = 0; k < c.Values.Length; k++)
            {
                if (cubic && k / 3 % 3 != 1) continue;
                float v = c.Values[k];
                if (Math.Abs(v - 1) > scaleOff) { scaleOff = Math.Abs(v - 1); scaleAt = nodes[c.Node].Name; }
            }
        }
        if (scaleOff > ScaleTolerance)
            throw new ImportRefusedException($"{scaleAt} is scaled (by {F(1 + scaleOff, "F4")} or so): the game's bones have no scale. Undo the scaling (on the bone, or on the armature) and export again.", ImportRefusal.Scale);

        // ---- the length, from the animation's first and last key
        double start = chans.Min(c => c.Times[0]), end = chans.Max(c => c.Times[^1]);
        double ticks = (end - start) * ClipRecord.TicksPerSecond;
        int got = (int)Math.Round(ticks);
        string rate = FrameRateHint(chans);
        if (got != last || Math.Abs(ticks - got) > 0.05)
            throw new ImportRefusedException(FormattableString.Invariant($"the animation lasts {end - start:F3} s ({ticks:F2} ticks at 60 a second); {slot.Name} lasts {rec.Duration:F3} s ({last} ticks). ")
                + $"An import must last exactly as long as the clip it replaces: at 60 frames a second, from frame 0 to frame {last}.{rate}", ImportRefusal.Length);
        report.Add(MotionChecks.Length(got, last));
        bool offGrid = chans.Any(c => c.Times.Any(t => Math.Abs((t - start) * 60 - Math.Round((t - start) * 60)) > 0.01));
        if (start > 1e-4 || offGrid)
            report.Add(new CheckResult("key times", CheckLevel.Warn, (start > 1e-4 ? FormattableString.Invariant($"the animation starts at {start:F3} s, taken as the clip's tick 0; ") : "")
                + (offGrid ? "some keys fall between ticks of 1/60 s: the motion was sampled at every tick." + rate : "every key on a tick")));

        // ---- the file's motion at every tick
        var byNodePath = chans.ToDictionary(c => (c.Node, c.Path));
        (Vector3 T, Quaternion R) NodeAt(int node, double sec)
        {
            var nd = nodes[node];
            var t = byNodePath.TryGetValue((node, "translation"), out var ct) ? V3(ct.At(sec)) : nd.Translation;
            // keys or spline tangents too large to join overflow between keys: no place to write, and a NaN would pass for "unchanged"
            if (!(float.IsFinite(t.X) && float.IsFinite(t.Y) && float.IsFinite(t.Z)))
                throw new ImportRefusedException($"{nd.Name}'s translation isn't a finite number at {F(sec)} s: its keys or tangents there are too large to join. Check the animation's curves and export again.", ImportRefusal.File);
            var r = byNodePath.TryGetValue((node, "rotation"), out var cr) ? Q4(cr.At(sec)) : nd.Rotation;
            return (t, r);
        }
        var fileLocals = new Transform[last + 1][];
        var fileMover = new Transform[last + 1];
        var rk = new bool[n];
        var pk = new bool[n];
        for (int b = 0; b < n; b++)
        {
            rk[b] = byNodePath.ContainsKey((boneNode[b], "rotation"));
            pk[b] = byNodePath.ContainsKey((boneNode[b], "translation"));
        }
        for (int t = 0; t <= last; t++)
        {
            double sec = start + t / (double)ClipRecord.TicksPerSecond;
            var l = new Transform[n];
            for (int b = 0; b < n; b++)
            {
                var (tr, ro) = NodeAt(boneNode[b], sec);
                l[b] = new Transform(GltfAxes.FromGltf(tr), GltfAxes.FromGltf(ro));
            }
            fileLocals[t] = l;
            // the armature and anything above it, composed in glTF's axes (the turn commutes with composing) and turned back
            var w = Transform.Identity;
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                var (tr, ro) = NodeAt(chain[k], sec);
                w *= new Transform(tr, ro);
            }
            fileMover[t] = new Transform(GltfAxes.FromGltf(w.Position), GltfAxes.FromGltf(w.Rotation));
        }
        var fromFile = new Motion(s, fileLocals, fileMover, rk, pk, hasMover: true);

        Motion game;
        try { game = Motion.FromClip(s, rec, table); }
        catch (InvalidDataException ex) { throw new ImportRefusedException($"{slot.Name} can't be decoded: {ex.Message}", ImportRefusal.Slot, ex); }

        // the armature turned over: the character's up against the game clip's at the first tick
        {
            var up = Vector3.Transform(Vector3.UnitZ, fileMover[0].Rotation);
            var gup = Vector3.Transform(Vector3.UnitZ, game.Mover(0).Rotation);
            double tilt = Math.Acos(Math.Clamp(Vector3.Dot(up, gup), -1, 1)) * 180 / Math.PI;
            if (tilt > 45)
                throw new ImportRefusedException($"the armature is turned {F(tilt, "F0")} deg off upright against the game clip's start, so the character would lie on its side or upside down. "
                    + "Keep the armature upright (turning it about the vertical is fine, as the clip's own root does), and export with glTF's own axes (y up).", ImportRefusal.Axes);
        }

        // ---- the slot's own values per track at every tick, to leave unchanged tracks as they are: sampled as the tool's export samples
        // them (SampledClip), so a track the file leaves as exported matches tick for tick, but for the float rounding of the file's key
        // times beside a large step (SameStepShare)
        var own = new ClipSampler(rec, table);
        // how far the file's value at tick t may lie from the game's and still be the game's: the fixed room plus a share of the track's own
        // step to its neighbouring ticks (see SameStepShare)
        static double Room(Func<int, int, double> step, int t, int last, double same) =>
            same + SameStepShare * Math.Max(t > 0 ? step(t - 1, t) : 0, t < last ? step(t, t + 1) : 0);
        var ownRot = new Dictionary<int, Quaternion[]>();
        var ownPos = new Dictionary<int, Vector3[]>();
        foreach (var ch in own.Channels) { if (ch.Rotation) ownRot[ch.TrackIndex] = new Quaternion[last + 1]; else ownPos[ch.TrackIndex] = new Vector3[last + 1]; }
        for (int t = 0; t <= last; t++)
        {
            own.At(t / (float)ClipRecord.TicksPerSecond);
            for (int i = 0; i < own.Channels.Count; i++)
            {
                var ch = own.Channels[i];
                if (ch.Rotation) ownRot[ch.TrackIndex][t] = own.Rotations[i]; else ownPos[ch.TrackIndex][t] = own.Positions[i];
            }
        }

        // ---- the curves
        bool has5 = ClipRebuild.ArenaValue(rec, 5) is not null, has7 = ClipRebuild.ArenaValue(rec, 7) is not null;
        int[] keyTicks = last < 255 ? Enumerable.Range(0, last + 1).ToArray() : KeyingRule.Ticks(last);
        int step = last < 255 ? 1 : KeyingRule.Step(last);
        var curves = new Dictionary<int, byte[]>();
        var fates = new List<TrackFate>();
        var writtenRot = new bool[n];
        var writtenPos = new bool[n];
        bool moverRotWritten = false, moverPosWritten = false;
        var twelveMulti = new List<string>();
        var placed = new List<(string Name, int Keys, double Uniform, double Got, string Unit)>();
        for (int i = 0; i < rec.Tracks.Count; i++)
        {
            uint h = rec.Tracks[i];
            var u = SharedCurveTable.Resolve(rec.Curves[i], table);
            int b = h == 0 ? -1 : s.IndexOf(h);
            string name = h == 0 ? "(the mover)" : b >= 0 ? s.Bones[b].Name : h.ToString("X8");
            var kind = u?.Kind ?? TrackKind.Unknown;
            void Keep(string why) => fates.Add(new TrackFate(i, h, name, kind, false, why));
            if (u is null) { Keep("its shared curve can't be found: kept"); continue; }
            if (kind is not (TrackKind.Rotation or TrackKind.Position)) { Keep($"tag 0x{u.Tag:X2}: not a bone's rotation or position, kept byte for byte"); continue; }
            if (h != 0 && HandTracks.IsHandTrack(h)) { Keep("a hand track: kept byte for byte"); continue; }
            if (h != 0 && b < 0) { Keep("names no skeleton bone (a helper, offset or marker): kept byte for byte"); continue; }
            bool rot = kind == TrackKind.Rotation;
            if ((rot ? !ownRot.ContainsKey(i) : !ownPos.ContainsKey(i))) { Keep($"tag 0x{u.Tag:X2} isn't decoded: kept byte for byte"); continue; }
            if (rot)
            {
                var v = new Quaternion[last + 1];
                for (int t = 0; t <= last; t++) v[t] = h == 0 ? fileMover[t].Rotation : fileLocals[t][b].Rotation;
                var o = ownRot[i];
                double diff = 0; bool same = true;
                for (int t = 0; t <= last; t++)
                {
                    double d = GltfAxes.AngleDeg(v[t], o[t]);
                    diff = Math.Max(diff, d);
                    if (same && d > SameDeg && d > Room((a, z) => GltfAxes.AngleDeg(o[a], o[z]), t, last, SameDeg)) same = false;
                }
                if (same) { Keep(FormattableString.Invariant($"the file leaves it as the game had it (within {diff:F4} deg{(diff > SameDeg ? ", a hundredth of its own step a tick there" : "")}): kept byte for byte")); continue; }
                bool still = v.All(q => GltfAxes.AngleDeg(q, v[0]) <= StillDeg);
                int[] kt = still ? [0] : step == 1 ? keyTicks
                    : Placed(name, keyTicks, (a, z, t) => GltfAxes.AngleDeg(Quaternion.Slerp(v[a], v[z], (t - a) / (float)(z - a)), v[t]), "deg", placed);
                var kv = kt.Select(t => v[t]).ToList();
                // the 6-byte form wherever the head has its arena kind (5): it is the form seen playing in game; the 12-byte form only
                // where the head lacks kind 5
                bool twelve = !has5 && has7;
                if (twelve && kt.Length > 1) twelveMulti.Add(name);
                curves[i] = twelve ? CurveWriter.RotCurve12(kt, kv) : CurveWriter.RotCurve(kt, kv);
                fates.Add(new TrackFate(i, h, name, kind, true, FormattableString.Invariant($"written, {kt.Length} key(s), {(twelve ? "12" : "6")}-byte form (the game's tag 0x{u.Tag:X2}, {u.Keys} keys); up to {diff:F2} deg from the game's")));
                if (h == 0) moverRotWritten = true; else writtenRot[b] = true;
            }
            else
            {
                var v = new Vector3[last + 1];
                for (int t = 0; t <= last; t++) v[t] = h == 0 ? fileMover[t].Position : fileLocals[t][b].Position;
                var o = ownPos[i];
                double diff = 0; bool same = true;
                for (int t = 0; t <= last; t++)
                {
                    double d = Vector3.Distance(v[t], o[t]) * 100;
                    diff = Math.Max(diff, d);
                    if (same && d > SameCm && d > Room((a, z) => Vector3.Distance(o[a], o[z]) * 100, t, last, SameCm)) same = false;
                }
                if (same) { Keep(FormattableString.Invariant($"the file leaves it as the game had it (within {diff:F4} cm{(diff > SameCm ? ", a hundredth of its own step a tick there" : "")}): kept byte for byte")); continue; }
                bool still = v.All(p => Vector3.Distance(p, v[0]) * 100 <= StillCm);
                int[] kt = still ? [0] : step == 1 ? keyTicks
                    : Placed(name, keyTicks, (a, z, t) => Vector3.Distance(Vector3.Lerp(v[a], v[z], (t - a) / (float)(z - a)), v[t]) * 100, "cm", placed);
                curves[i] = CurveWriter.PosCurve(kt, kt.Select(t => v[t]).ToList());
                fates.Add(new TrackFate(i, h, name, kind, true, FormattableString.Invariant($"written, {kt.Length} key(s) (the game's tag 0x{u.Tag:X2}, {u.Keys} keys); up to {diff:F2} cm from the game's")));
                if (h == 0) moverPosWritten = true; else writtenPos[b] = true;
            }
        }

        RebuiltClip built;
        try { built = ClipRebuild.Rebuild(rec, table, curves); }
        catch (RebuildRefusedException ex)
        {
            throw new ImportRefusedException($"{slot.Name} can't take the new curves: {ex.Message}. This clip's record has no room of the kind the new curves need; pick another clip to replace.", ImportRefusal.Arena, ex);
        }
        Motion written;
        try { written = Motion.FromClip(s, built.Record, table); }
        catch (InvalidDataException ex) { throw new ImportRefusedException($"the rebuilt record doesn't decode: {ex.Message}", ImportRefusal.Slot, ex); }

        // what the record should play: the file's values where a track was written, the game's elsewhere
        var expLocals = new Transform[last + 1][];
        var expMover = new Transform[last + 1];
        for (int t = 0; t <= last; t++)
        {
            var l = new Transform[n];
            for (int b = 0; b < n; b++)
            {
                var gl = game.Local(t, b);
                l[b] = new Transform(writtenPos[b] ? fileLocals[t][b].Position : gl.Position, writtenRot[b] ? fileLocals[t][b].Rotation : gl.Rotation);
            }
            expLocals[t] = l;
            var gm = game.Mover(t);
            expMover[t] = new Transform(moverPosWritten ? fileMover[t].Position : gm.Position, moverRotWritten ? fileMover[t].Rotation : gm.Rotation);
        }
        var expected = new Motion(s, expLocals, expMover, game.RotationKeyed, game.PositionKeyed, game.HasMover);

        // ---- the report
        int keptNonBone = fates.Count(f => !f.Written && f.Kind is not (TrackKind.Rotation or TrackKind.Position));
        int keptHand = fates.Count(f => !f.Written && HandTracks.IsHandTrack(f.Hash));
        report.Add(new CheckResult("tracks", CheckLevel.Pass, $"{fates.Count(f => f.Written)} written, {fates.Count(f => !f.Written)} keep the game's bytes ({keptNonBone} that aren't bone rotations or positions, "
            + $"{keptHand} hand tracks, the rest unchanged by the file); the clip's ID, duration, hash words, track list and events stay as they were"));
        report.Add(new CheckResult("keying", CheckLevel.Pass, step == 1
            ? $"a key every tick where a track moves ({last + 1} keys at most), one key where it holds still"
            : $"the clip lasts {last} ticks, so moving tracks are keyed every {step} ticks with the last tick always keyed ({keyTicks.Length} keys, at most 255 a curve)"
                + (placed.Count == 0 ? "" : $"; {placed.Count} track(s) keyed where their motion needs it instead, since keys every {step} ticks would miss it: "
                    + string.Join("; ", placed.Take(6).Select(p => FormattableString.Invariant($"{p.Name} {p.Keys} keys, within {p.Got:F3} {p.Unit} (every {step} ticks: {p.Uniform:F2})")))
                    + (placed.Count > 6 ? "; ..." : ""))));
        if (twelveMulti.Count > 0)
            report.Add(new CheckResult("curve form", CheckLevel.Warn, $"the clip's head has no room of the 6-byte rotations' kind, so {string.Join(", ", twelveMulti.Take(6))}{(twelveMulti.Count > 6 ? ", ..." : "")} "
                + "were written as 12-byte curves of several keys: a form that reads back here but hasn't yet been seen playing in game", null, twelveMulti[0]));
        var readBack = MotionChecks.ReadBack(expected, written);
        if (step > 1 && readBack.Level != CheckLevel.Pass)
            readBack = readBack with { Message = readBack.Message + KeyingHint(step, fates, placed) };
        report.Add(readBack);
        report.AddRange(MotionChecks.Body(written, game));
        report.Add(MotionChecks.HandMoves(written, game, rec.Tracks.ToList()));
        report.Add(MotionChecks.Plants(written, game, Plants(rec, table)));
        report.Add(Dropped(fromFile, expected, s, rec, table));
        report.Add(MotionField(verdict, graphRead));

        return new ImportResult
        {
            Slot = slot, Source = source, SourceSha256 = sourceSha256, Rebuilt = built, Report = report, Game = game, Imported = written, FromFile = fromFile,
            Fates = fates, KeyStep = step,
        };
    }

    /// <summary>What a read-back short of a pass on a long slot comes from, added to its line: the tracks written (the file changed them;
    /// every track it leaves as the game had it keeps its bytes) hold at most <see cref="MaxCurveKeys"/> keys, joined straight between, and
    /// those keyed where their motion needs it are named with how close they came.</summary>
    static string KeyingHint(int step, IReadOnlyList<TrackFate> fates, IReadOnlyList<(string Name, int Keys, double Uniform, double Got, string Unit)> placed)
    {
        var written = fates.Where(f => f.Written).Select(f => f.Name).Distinct().ToList();
        string tracks = written.Count == 0 ? "" : $" ({string.Join(", ", written.Take(6))}{(written.Count > 6 ? ", ..." : "")})";
        string hint = $"; the clip is long, so each of the {written.Count} track(s) the file changed{tracks} is written with at most {MaxCurveKeys} keys, "
            + $"every {step} ticks or placed where its motion needs them, joined straight between keys: a fast or busy change can't be held closer "
            + "(tracks the file leaves as the game had them keep their bytes)";
        if (placed.Count > 0)
            hint += "; keyed where the motion needs it: " + string.Join("; ", placed.Take(6).Select(p => FormattableString.Invariant($"{p.Name} {p.Keys} keys, within {p.Got:F3} {p.Unit}")))
                + (placed.Count > 6 ? "; ..." : "");
        return hint;
    }

    /// <summary>A long slot's curve is keyed every <see cref="KeyingRule.Step"/> ticks when that misses its motion, joined straight between
    /// keys, by no more than this (degrees, or centimetres for a position); else its keys are placed where the motion needs them.</summary>
    public const double UniformKeyingTolerance = 0.05;
    /// <summary>The most keys a written curve holds.</summary>
    public const int MaxCurveKeys = 255;

    /// <summary>The key ticks of one moving curve of a long slot: <paramref name="uniform"/> (the keying rule's) when, joined straight, it
    /// misses the motion by no more than <see cref="UniformKeyingTolerance"/>; else keys placed where the motion needs them, at most
    /// <see cref="MaxCurveKeys"/> with the first and last tick always keyed: starting from those two, the tick missed most is keyed next,
    /// until every tick is within a hundredth or the keys run out. The placed keys are used only where they miss less; each such curve is
    /// listed in <paramref name="placed"/>. <paramref name="miss"/> gives how far tick t lies from the straight join of keys a and z.</summary>
    static int[] Placed(string name, int[] uniform, Func<int, int, int, double> miss, string unit,
        List<(string Name, int Keys, double Uniform, double Got, string Unit)> placed)
    {
        (double Miss, int At) Worst(int a, int z)
        {
            double w = 0; int at = -1;
            for (int t = a + 1; t < z; t++) { double m = miss(a, z, t); if (m > w) { w = m; at = t; } }
            return (w, at);
        }
        double MaxMiss(IReadOnlyList<int> keys)
        {
            double w = 0;
            for (int k = 1; k < keys.Count; k++) w = Math.Max(w, Worst(keys[k - 1], keys[k]).Miss);
            return w;
        }
        double u = MaxMiss(uniform);
        if (u <= UniformKeyingTolerance) return uniform;
        int last = uniform[^1];
        var keys = new SortedSet<int> { 0, last };
        var segs = new List<(int A, int Z, double Miss, int At)>();
        var (m0, at0) = Worst(0, last);
        segs.Add((0, last, m0, at0));
        while (keys.Count < MaxCurveKeys)
        {
            int i = 0;
            for (int k = 1; k < segs.Count; k++) if (segs[k].Miss > segs[i].Miss) i = k;
            var s = segs[i];
            if (s.At < 0 || s.Miss <= 0.01) break;
            keys.Add(s.At);
            var (ma, aa) = Worst(s.A, s.At);
            var (mz, az) = Worst(s.At, s.Z);
            segs[i] = (s.A, s.At, ma, aa);
            segs.Add((s.At, s.Z, mz, az));
        }
        var got = keys.ToArray();
        double g = segs.Max(x => x.Miss);
        if (g >= u) return uniform;
        placed.Add((name, got.Length, u, g, unit));
        return got;
    }

    /// <summary>How long a file's animations last, in ticks of 1/60 s, from the first key of any channel to the last; null without one.
    /// The Import screen uses it to offer the slots of the file's length.</summary>
    public static double? AnimationTicks(GlbScene scene)
    {
        var ch = scene.Animations.SelectMany(a => a.Channels).ToList();
        if (ch.Count == 0) return null;
        return (ch.Max(c => c.Times[^1]) - ch.Min(c => c.Times[0])) * ClipRecord.TicksPerSecond;
    }

    static uint BoneHash(string name) => name.Length == 8 && name.All(Uri.IsHexDigit)
        ? uint.Parse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : Crc32.Of(name);

    /// <summary>By node: whether it lies below <paramref name="armature"/> (the armature itself not), found in one pass down the tree
    /// (the scene's parents hold no loop: <see cref="GlbScene"/> refuses one).</summary>
    static bool[] UnderArmature(IReadOnlyList<GltfNode> nodes, int armature)
    {
        var under = new bool[nodes.Count];
        var children = new List<int>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].Parent >= 0) (children[nodes[i].Parent] ??= []).Add(i);
        var stack = new Stack<int>();
        if (armature >= 0) stack.Push(armature);
        else for (int i = 0; i < nodes.Count; i++) if (nodes[i].Parent < 0) { under[i] = true; stack.Push(i); }
        while (stack.Count > 0)
            foreach (int c in children[stack.Pop()] ?? [])
                if (!under[c]) { under[c] = true; stack.Push(c); }
        return under;
    }

    static Vector3 V3(float[] v) => new(v[0], v[1], v[2]);
    static Quaternion Q4(float[] v) => Quaternion.Normalize(new Quaternion(v[0], v[1], v[2], v[3]));

    /// <summary>A hint at the frame rate the file's keys were made at, when it isn't 60 a second.</summary>
    static string FrameRateHint(IReadOnlyList<GltfChannel> chans)
    {
        var c = chans.MaxBy(x => x.Times.Length);
        if (c is null || c.Times.Length < 3) return "";
        double dt = c.Times[1] - c.Times[0];
        if (dt <= 0) return "";
        double fps = 1 / dt;
        foreach (int r in new[] { 24, 25, 30, 50, 120 })
            if (Math.Abs(fps - r) < 0.05) return $" The file's keys are {r} a second: it was made at {r} frames a second, not 60.";
        return "";
    }

    /// <summary>The slot's foot plants: each key's slot (0 left, 1 right), tick and length.</summary>
    static List<(int Slot, int Tick, int Length)> Plants(ClipRecord rec, SharedCurveTable? table)
    {
        var l = new List<(int, int, int)>();
        for (int i = 0; i < rec.Tracks.Count; i++)
        {
            uint h = rec.Tracks[i];
            if (h is not (0xFFFFFFE4 or 0xFFFFFFE5)) continue;
            var u = SharedCurveTable.Resolve(rec.Curves[i], table);
            if (u is null || u.Kind != TrackKind.Plant) continue;
            for (int k = 0; k < u.Keys; k++)
                if (CurveDecoder.Plant(u, k) is { } p && p.LengthTicks > 0) l.Add(((int)(h - 0xFFFFFFE4), p.Tick, p.LengthTicks));
        }
        return l;
    }

    /// <summary>Motion in the file that the record can't hold: bones that move in the file but the slot has no track for (or holds as a hand
    /// track), and the armature's motion where the slot has no mover.</summary>
    static CheckResult Dropped(Motion file, Motion expected, Skeleton s, ClipRecord rec, SharedCurveTable? table)
    {
        const string Name = "motion not written";
        var hand = rec.Tracks.Where(HandTracks.IsHandTrack).ToHashSet();
        var off = new List<(string Name, double Deg, double Cm, int Tick)>();
        for (int b = 0; b < s.Bones.Count; b++)
        {
            // the worst tick by the measure the lines are ranked by: degrees plus centimetres
            double d = 0, c = 0, most = -1; int at = 0;
            for (int t = 0; t <= file.LastTick; t++)
            {
                var a = file.Local(t, b); var e = expected.Local(t, b);
                double dd = GltfAxes.AngleDeg(a.Rotation, e.Rotation), cc = Vector3.Distance(a.Position, e.Position) * 100;
                if (dd + cc > most) { most = dd + cc; at = t; }
                d = Math.Max(d, dd); c = Math.Max(c, cc);
            }
            if (d > 0.5 || c > 0.5) off.Add((s.Bones[b].Name + (hand.Contains(s.Bones[b].Hash) ? " (a hand track, kept)" : ""), d, c, at));
        }
        double md = 0, mc = 0, mw = -1; int mt = 0;
        for (int t = 0; t <= file.LastTick; t++)
        {
            double dd = GltfAxes.AngleDeg(file.Mover(t).Rotation, expected.Mover(t).Rotation), cc = Vector3.Distance(file.Mover(t).Position, expected.Mover(t).Position) * 100;
            if (dd + cc > mw) { mw = dd + cc; mt = t; }
            md = Math.Max(md, dd); mc = Math.Max(mc, cc);
        }
        if (md > 0.5 || mc > 0.5) off.Add(("the armature (the clip has no mover track of that kind)", md, mc, mt));
        if (off.Count == 0) return new CheckResult(Name, CheckLevel.Pass, "everything the file moves is in the record");
        var worst = off.MaxBy(x => x.Deg + x.Cm);
        return new CheckResult(Name, CheckLevel.Warn, $"the file moves {off.Count} bone(s) the clip has no track for, so the game won't show it: "
            + string.Join("; ", off.Take(6).Select(x => FormattableString.Invariant($"{x.Name} up to {x.Deg:F1} deg, {x.Cm:F1} cm"))) + (off.Count > 6 ? "; ..." : ""), worst.Tick, worst.Name);
    }

    /// <summary>The graph's word on replacing the slot, from the verdict as a whole (<see cref="ClipGraphVerdict.ReplaceWarning"/>): a warning
    /// when it isn't known (the graph not read, or not whole, or the slot not looked for), when only GR_Graph's list names the slot, when a
    /// motion field uses it, and when it is a blend's child.</summary>
    static CheckResult MotionField(ClipGraphVerdict? v, bool graphRead)
    {
        const string Name = GraphCheckName;
        if (v is null) return new CheckResult(Name, CheckLevel.Warn, graphRead ? "this clip isn't in the graph's read" : "the animation graph wasn't read, so whether a motion field or a blend uses this clip isn't known");
        if (v.ReplaceWarning is not { } w) return new CheckResult(Name, CheckLevel.Pass, "no motion field uses this clip and it isn't a blend's child");
        if (v.MotionFieldKnown && v.IsMotionField)
            w = "a motion field uses this clip: its rows still describe the game's clip, so the game may pick the clip badly if the import changes its heading or speed "
                + "(replacements of a walk and a run by a walk and a run played in their place)" + (v.UnderBlend ? $"; also, {ClipGraphVerdict.ReplaceBlendWarning}" : "");
        return new CheckResult(Name, CheckLevel.Warn, w);
    }
}
