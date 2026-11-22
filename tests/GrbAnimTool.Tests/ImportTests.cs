using System.Numerics;
using System.Text;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>The import on a synthetic skeleton and slot, with files made by the tool's own glTF writer: the round trip, the refusals, the
/// keying rule, the warnings, and damaged files.</summary>
public sealed class ImportTests
{
    static readonly Skeleton S = Humanoid.Skeleton();

    /// <summary>The slot exported as the tool exports it, with <paramref name="edit"/> applied to the samples first.</summary>
    static byte[] Export(ClipEntry slot, Action<SampledClip>? edit = null, Skeleton? on = null, ExportFault fault = ExportFault.None)
    {
        var s = on ?? S;
        var sc = SampledClip.Of(s, slot.Record!, null);
        edit?.Invoke(sc);
        return GlbWriter.Clip(s, sc, slot.Name, fault);
    }

    static void TurnAll(SampledClip sc, string bone, Vector3 axis, Func<int, float> deg)
    {
        int b = Humanoid.Bone(S, bone);
        var r = sc.Rotations[b] ?? throw new InvalidOperationException($"{bone} has no rotation track");
        for (int t = 0; t < r.Length; t++) r[t] = Quaternion.Normalize(r[t] * Quaternion.CreateFromAxisAngle(axis, deg(t) * MathF.PI / 180));
    }

    static ImportResult Import(ClipEntry slot, byte[] glb, ClipGraphVerdict? v = null, bool graphRead = false) =>
        ClipImport.Run(S, null, slot, glb, "test.glb", v, graphRead);

    static CheckResult Line(ImportResult r, string check) => r.Report.Results.Single(x => x.Check == check);

    [Fact]
    public void An_unchanged_export_imports_back_to_the_same_record()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var r = Import(slot, Export(slot));
        Assert.Equal(slot.Bytes.ToArray(), r.Record);
        Assert.Equal(0, r.Written);
        Assert.Equal(slot.TrackCount, r.Kept);
        Assert.NotEqual(CheckLevel.Fail, r.Report.Verdict);
        Assert.Equal(CheckLevel.Pass, Line(r, "read-back").Level);
        Assert.Equal(CheckLevel.Pass, Line(r, "bone match").Level);
        Assert.Contains("VERDICT", r.ReportText());
    }

    /// <summary>An untouched track that jumps metres in one tick (the mover 14 m between ticks 150 and 151, as a motion-field clip's
    /// helpers do) keeps its bytes in a long slot: a key's time is stored as a float, so the file read at tick 150 takes a sliver of the
    /// jump (some 0.08 cm here), which isn't a change. A real change of the same track is still written.</summary>
    [Fact]
    public void An_untouched_track_that_jumps_metres_in_one_tick_keeps_its_bytes()
    {
        var slot = Humanoid.Slot(S, 300, armed: false, moverPath: ([0, 150, 151, 300], [Vector3.Zero, new(0, 0.5f, 0), new(14, 0.5f, 0), new(14, 1, 0)]));
        var r = Import(slot, Export(slot));
        Assert.Equal(0, r.Written);
        Assert.Equal(slot.Bytes.ToArray(), r.Record);
        Assert.Equal(CheckLevel.Pass, Line(r, "read-back").Level);
        // the file did lie past the fixed room beside the jump: the share of the step is what kept it
        Assert.Equal("(the mover)", r.Fates[0].Name);
        Assert.Contains("a hundredth of its own step", r.Fates[0].What);
        var moved = Import(slot, Export(slot, sc => { for (int t = 0; t < sc.MoverPosition!.Length; t++) sc.MoverPosition[t] += new Vector3(0.05f, 0, 0); }));
        Assert.True(moved.Fates[0].Written);
        Assert.Equal(1, moved.Written);
    }

    [Fact]
    public void A_changed_bone_is_written_and_everything_else_keeps_its_bytes()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var glb = Export(slot, sc => TurnAll(sc, "Head", Vector3.UnitX, t => t > 15 && t < 45 ? 20 : 0));
        var r = Import(slot, glb);
        var rec = r.Rebuilt.Record;
        var old = slot.Record!;
        Assert.Equal((old.Id, old.Name, old.Duration, old.Word1, old.Word2), (rec.Id, rec.Name, rec.Duration, rec.Word1, rec.Word2));
        Assert.Equal(old.Tracks, rec.Tracks);
        var written = r.Fates.Where(f => f.Written).Select(f => f.Name).ToList();
        Assert.Equal(["Head"], written);
        for (int i = 0; i < old.Tracks.Count; i++)
            if (!r.Fates[i].Written)
                Assert.Equal(old.Bytes.AsSpan(old.Curves[i].At, old.Curves[i].DiskLength).ToArray(), rec.Bytes.AsSpan(rec.Curves[i].At, rec.Curves[i].DiskLength).ToArray());
        // the written curve: the 6-byte form, a key every tick; the record decodes back to the file's motion within the writer's tolerance
        var head = rec.Curves[old.Tracks.ToList().IndexOf(Core.Hashing.Crc32.Of("Head"))];
        Assert.Equal((byte)0x0C, head.Tag);
        Assert.Equal(61u, head.Keys);
        Assert.Equal(CheckLevel.Pass, Line(r, "read-back").Level);
        int hb = Humanoid.Bone(S, "Head");
        for (int t = 0; t <= 60; t++) Assert.True(GltfAxes.AngleDeg(r.Imported.Local(t, hb).Rotation, r.FromFile.Local(t, hb).Rotation) < 0.01, $"tick {t}");
        Assert.True(GltfAxes.AngleDeg(r.Imported.Local(30, hb).Rotation, r.Game.Local(30, hb).Rotation) > 19);
    }

    /// <summary>A rotation is written in the 6-byte form wherever the head has its arena kind, a 12-byte track too; only a head without it
    /// gets the 12-byte form, with a warning.</summary>
    [Fact]
    public void A_12_byte_track_is_written_in_the_6_byte_form_where_the_head_allows()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var r = Import(slot, Export(slot, sc => TurnAll(sc, "LeftLeg", Vector3.UnitX, t => -t * 0.5f)));
        int li = slot.Record!.Tracks.ToList().IndexOf(Core.Hashing.Crc32.Of("LeftLeg"));
        Assert.Equal((byte)0x18, slot.Record!.Curves[li].Tag);
        Assert.Equal((byte)0x0C, r.Rebuilt.Record.Curves[li].Tag);
        Assert.Equal(CheckLevel.Pass, Line(r, "read-back").Level);
        Assert.DoesNotContain(r.Report.Results, x => x.Check == "curve form");

        var no5 = Humanoid.Slot(S, 60, armed: false, kind5: false);
        var r12 = Import(no5, Export(no5, sc => TurnAll(sc, "LeftLeg", Vector3.UnitX, t => -t * 0.5f)));
        Assert.Equal((byte)0x18, r12.Rebuilt.Record.Curves[li].Tag);
        Assert.NotEqual(CheckLevel.Fail, Line(r12, "read-back").Level);
        var form = Line(r12, "curve form");
        Assert.Equal(CheckLevel.Warn, form.Level);
        Assert.Contains("LeftLeg", form.Message);
        Assert.Contains("hasn't yet been seen playing in game", form.Message);
    }

    [Fact]
    public void Unturned_axes_are_refused()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var ex = Assert.Throws<ImportRefusedException>(() => Import(slot, Export(slot, fault: ExportFault.AxesNotTurned)));
        Assert.Equal(ImportRefusal.Axes, ex.Reason);
        Assert.Contains("glTF's own axes (y up)", ex.Message);
        Assert.DoesNotContain("Blender", ex.Message);
    }

    [Fact]
    public void A_wrong_length_is_refused()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var shorter = Humanoid.Slot(S, 59, armed: false);
        var ex = Assert.Throws<ImportRefusedException>(() => Import(slot, Export(shorter)));
        Assert.Equal(ImportRefusal.Length, ex.Reason);
        Assert.Contains("59", ex.Message);
        Assert.Contains("frame 60", ex.Message);
    }

    [Fact]
    public void Missing_bones_are_refused()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var small = Humanoid.Skeleton(leave: "RightToeBase");
        var ex = Assert.Throws<ImportRefusedException>(() => Import(slot, Export(slot, on: small)));
        Assert.Equal(ImportRefusal.BoneSet, ex.Reason);
        Assert.Contains("RightToeBase", ex.Message);
        var longer = Assert.Throws<ImportRefusedException>(() => Import(slot, Export(slot, on: Humanoid.Skeleton(longer: "LeftLeg"))));
        Assert.Equal(ImportRefusal.BoneSet, longer.Reason);
    }

    [Fact]
    public void A_long_slot_gets_the_keying_rule()
    {
        // the leg turned evenly: keys every 3 ticks join it exactly. The head's own sway is keyed every 10 ticks in the slot, so keys
        // every 3 ticks would cut its corners: its keys are placed where its motion needs them instead, at most 255
        var slot = Humanoid.Slot(S, 600, armed: false, headStep: 10);
        var r = Import(slot, Export(slot, sc =>
        {
            TurnAll(sc, "Head", Vector3.UnitX, t => 20 * MathF.Sin(t / 30f));
            TurnAll(sc, "LeftLeg", Vector3.UnitX, t => -t * 0.05f);
        }));
        Assert.Equal(3, r.KeyStep);
        Assert.Contains(r.Fates, f => f.Written && f.Name == "Head");
        foreach (var c in r.Rebuilt.Record.Curves.Where(c => !c.IsReference)) Assert.True(c.Keys <= 255, $"a curve of {c.Keys} keys");
        var tracks = slot.Record!.Tracks.ToList();
        var leg = r.Rebuilt.Record.Curves[tracks.IndexOf(Core.Hashing.Crc32.Of("LeftLeg"))];
        Assert.Equal(201u, leg.Keys);
        Assert.Equal(Enumerable.Range(0, 201).Select(k => 3 * k), CurveDecoder.KeyTicks(leg));
        var head = r.Rebuilt.Record.Curves[tracks.IndexOf(Core.Hashing.Crc32.Of("Head"))];
        Assert.InRange(head.Keys, 2u, 255u);
        Assert.Equal(0, CurveDecoder.KeyTicks(head)[0]);
        Assert.Equal(600, CurveDecoder.KeyTicks(head)[^1]);
        Assert.Equal(CheckLevel.Pass, Line(r, "read-back").Level);
        var keying = Line(r, "keying").Message;
        Assert.Contains("every 3 ticks", keying);
        Assert.Contains("Head", keying);
        Assert.Contains("where their motion needs it", keying);
        Assert.DoesNotContain("LeftLeg", keying);
    }

    [Fact]
    public void A_hand_moved_in_an_armed_slot_warns()
    {
        var armed = Humanoid.Slot(S, 60, armed: true);
        var glb = Export(armed, sc => TurnAll(sc, "LeftArm", Vector3.UnitY, _ => 25));
        var r = Import(armed, glb);
        Assert.Equal(CheckLevel.Warn, Line(r, "hands on a clip that aims them").Level);
        Assert.Contains("6C8CB68C", Line(r, "hands on a clip that aims them").Message);
        // the hand weights keep their bytes
        Assert.Contains(r.Fates, f => f.Hash == Humanoid.HandWeights && !f.Written);
        var plain = Humanoid.Slot(S, 60, armed: false);
        Assert.Equal(CheckLevel.Pass, Line(Import(plain, Export(plain, sc => TurnAll(sc, "LeftArm", Vector3.UnitY, _ => 25))), "hands on a clip that aims them").Level);
    }

    /// <summary>The graph's line uses the verdict as a whole: a plain clip node passes; a motion field, a clip only GR_Graph's list names,
    /// an unscanned clip, an incomplete graph and a blend's child (direct or a level further up) each warn.</summary>
    [Fact]
    public void The_graph_line_warns_on_every_state_the_verdict_warns_on()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var glb = Export(slot);
        CheckResult Graph(ClipGraphVerdict? v, bool read = true) => Line(Import(slot, glb, v, read), ClipImport.GraphCheckName);
        ClipGraphUse Node(uint parent = 0x12345678, int level = 0) =>
            new(slot.Id, GraphUseKind.ClipNode, GraphClasses.ClipNode, 1, "doc", 0, null, HoldsDuration: false, MotionField: false) { ParentClass = parent, ParentAt = 0, BlendLevel = level };
        var list = new ClipGraphUse(slot.Id, GraphUseKind.GraphClipList, GraphClasses.GraphClipListEntry, ClipGraph.GraphRecordId, "GR_Graph", 0, null, HoldsDuration: false, MotionField: false);
        var field = new ClipGraphUse(slot.Id, GraphUseKind.MotionFieldClip, 0, 1, "doc", 0, 0, HoldsDuration: false, MotionField: true);
        ClipGraphVerdict V(bool scanned = true, bool complete = true, params ClipGraphUse[] uses) => new() { ClipId = slot.Id, Uses = uses, GraphComplete = complete, Scanned = scanned };

        var plain = Graph(V(uses: [Node(), list]));
        Assert.Equal(CheckLevel.Pass, plain.Level);
        Assert.Contains("isn't a blend's child", plain.Message);
        var mf = Graph(V(uses: [field, Node(), list]));
        Assert.Equal(CheckLevel.Warn, mf.Level);
        Assert.Contains("motion field uses this clip", mf.Message);
        var listed = Graph(V(uses: [list]));
        Assert.Equal(CheckLevel.Warn, listed.Level);
        Assert.Contains(ClipGraphVerdict.ListedWithoutNodeUseReason, listed.Message);
        var unscanned = Graph(V(scanned: false, uses: [Node(), list]));
        Assert.Equal(CheckLevel.Warn, unscanned.Level);
        Assert.Contains("wasn't part of the graph read", unscanned.Message);
        Assert.Equal(CheckLevel.Warn, Graph(V(complete: false, uses: [Node(), list])).Level);
        var blend = Graph(V(uses: [Node(GraphClasses.BlendChild, 1), list]));
        Assert.Equal(CheckLevel.Warn, blend.Level);
        Assert.Equal(ClipGraphVerdict.ReplaceBlendWarning, blend.Message);
        Assert.Equal(CheckLevel.Warn, Graph(V(uses: [Node(level: 2), list])).Level);              // a blend's child above a pick
        Assert.Contains(ClipGraphVerdict.ReplaceBlendWarning, Graph(V(uses: [field, Node(GraphClasses.BlendChild, 1), list])).Message);
        Assert.Equal(CheckLevel.Warn, Graph(null, read: false).Level);                              // the graph not read: not known
        // the warnings reach the report's text and its warning lines (the Package screen's warnings)
        var r = Import(slot, glb, V(uses: [Node(GraphClasses.BlendChild, 1), list]), true);
        Assert.Contains(ClipGraphVerdict.ReplaceBlendWarning, r.ReportText());
        Assert.Contains(r.Report.Results, x => x.Level != CheckLevel.Pass && x.Line.Contains(ClipGraphVerdict.ReplaceBlendWarning));
    }

    [Fact]
    public void Motion_the_slot_cant_hold_warns()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        // RightArm has no track in the slot: its turn can't be written
        var r = Import(slot, Export(slot, sc =>
        {
            int b = Humanoid.Bone(S, "RightArm");
            sc.Rotations[b] = Enumerable.Range(0, sc.Frames).Select(_ => Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)).ToArray();
        }));
        var d = Line(r, "motion not written");
        Assert.Equal(CheckLevel.Warn, d.Level);
        Assert.Equal("RightArm", d.Bone);
    }

    static byte[] Rebuild(byte[] glb, Func<string, string> json)
    {
        var (j, bin) = Glb.Read(glb);
        return Glb.Write(Encoding.UTF8.GetBytes(json(j)), bin.Span);
    }

    [Fact]
    public void Damaged_files_are_refused_cleanly()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var good = Export(slot);
        var bad = new List<(string What, byte[] Bytes)>
        {
            ("empty", []),
            ("text", Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}}")),
            ("noise", Enumerable.Range(0, 5000).Select(i => (byte)(i * 7919 % 251)).ToArray()),
            ("deep json", Rebuild(good, _ => new string('[', 100_000) + new string(']', 100_000))),
            ("not json", Rebuild(good, j => j[..(j.Length / 2)])),
            ("a huge count", Rebuild(good, j => j.Replace("\"count\":61", "\"count\":2000000000"))),
            ("an accessor's view that isn't there", Rebuild(good, j => j.Replace("\"bufferView\":1,", "\"bufferView\":9999,"))),
            ("a buffer longer than the file", Rebuild(good, j => System.Text.RegularExpressions.Regex.Replace(j, "\"buffers\":\\[\\{\"byteLength\":\\d+", "\"buffers\":[{\"byteLength\":999999999"))),
            ("a node's own child", Rebuild(good, j => j.Replace("\"children\":[1]", "\"children\":[0]"))),
            ("a sampler that isn't there", Rebuild(good, j => j.Replace("\"sampler\":0", "\"sampler\":99999"))),
            ("CUBICSPLINE with too few values", Rebuild(good, j => j.Replace("\"LINEAR\"", "\"CUBICSPLINE\""))),
            ("a rotation of no length", Rebuild(good, j => j.Replace("\"rotation\":[", "\"rotation\":[0,0,0,0],\"x\":["))),
        };
        for (int cut = 1; cut < good.Length; cut += Math.Max(1, good.Length / 40)) bad.Add(($"cut at {cut}", good[..cut]));
        foreach (var (what, bytes) in bad)
        {
            var ex = Within.Seconds(10, () => Record.Exception(() => Import(slot, bytes)));
            Assert.True(ex is ImportRefusedException { Reason: ImportRefusal.File or ImportRefusal.BoneSet or ImportRefusal.Animation }, $"{what}: {ex?.GetType().Name}: {ex?.Message}");
        }
        // a file that reads but drives the bones with another interpolation keeps working: STEP everywhere is a step curve
        var step = Rebuild(good, j => j.Replace("\"LINEAR\"", "\"STEP\""));
        Assert.Equal(slot.Bytes.ToArray(), Import(slot, step).Record);
    }

    /// <summary>A .glb of <paramref name="channels"/> rotation channels on as many nodes, each on one CUBICSPLINE sampler of
    /// <paramref name="keys"/> keys; with <paramref name="ownAccessors"/>, each channel has its own values accessor over the same bytes.</summary>
    static byte[] ManyChannels(int channels, int keys, bool ownAccessors = false)
    {
        var bin = new byte[4 * keys + 4 * 12 * keys];
        for (int i = 0; i < keys; i++) BitConverter.TryWriteBytes(bin.AsSpan(4 * i), i / 60f);
        for (int i = 0; i < 3 * keys; i++) BitConverter.TryWriteBytes(bin.AsSpan(4 * keys + 16 * i + 12), 1f);   // every item (0, 0, 0, 1)
        var accs = new List<string> { $"{{\"bufferView\":0,\"componentType\":5126,\"count\":{keys},\"type\":\"SCALAR\"}}" };
        int nOut = ownAccessors ? channels : 1;
        for (int k = 0; k < nOut; k++) accs.Add($"{{\"bufferView\":1,\"componentType\":5126,\"count\":{3 * keys},\"type\":\"VEC4\"}}");
        var samplers = Enumerable.Range(0, nOut).Select(k => $"{{\"input\":0,\"output\":{k + 1},\"interpolation\":\"CUBICSPLINE\"}}");
        var chans = Enumerable.Range(0, channels).Select(i => $"{{\"sampler\":{(ownAccessors ? i : 0)},\"target\":{{\"node\":{i},\"path\":\"rotation\"}}}}");
        string json = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":" + bin.Length + "}],"
            + $"\"bufferViews\":[{{\"buffer\":0,\"byteLength\":{4 * keys}}},{{\"buffer\":0,\"byteOffset\":{4 * keys},\"byteLength\":{48 * keys}}}],"
            + $"\"accessors\":[{string.Join(",", accs)}],\"nodes\":[{string.Join(",", Enumerable.Range(0, channels).Select(i => $"{{\"name\":\"n{i}\"}}"))}],"
            + $"\"animations\":[{{\"samplers\":[{string.Join(",", samplers)}],\"channels\":[{string.Join(",", chans)}]}}]}}";
        return Glb.Write(Encoding.UTF8.GetBytes(json), bin);
    }

    /// <summary>The read's budget: an accessor many channels share is decoded once; one of more keys than a clip can hold, accessors that
    /// overlap far more than an exporter writes, and too many channels are refused plainly, quickly and without a large allocation.</summary>
    [Fact]
    public void A_file_that_would_decode_far_more_than_it_holds_is_refused()
    {
        long Allocated(Action a) { long before = GC.GetAllocatedBytesForCurrentThread(); a(); return GC.GetAllocatedBytesForCurrentThread() - before; }
        var slot = Humanoid.Slot(S, 60, armed: false);
        // a crafted file: 200 rotation channels on one CUBICSPLINE accessor of 144,004 keys
        var big = ManyChannels(200, 144_004);
        Exception? ex = null;
        long used = Allocated(() => ex = Within.Seconds(10, () => Record.Exception(() => Import(slot, big))));
        Assert.True(ex is ImportRefusedException { Reason: ImportRefusal.File }, $"{ex?.GetType().Name}: {ex?.Message}");
        Assert.Contains("keys", ex!.Message);
        Assert.True(used < 64L * 1024 * 1024, $"{used / 1048576} MB allocated");
        // the same at the most keys a channel may hold: read, the accessor decoded once for all 200 channels
        var shared = ManyChannels(200, GlbScene.MaxKeys);
        GlbScene? scene = null;
        used = Allocated(() => scene = GlbScene.Read(shared));
        var ch = scene!.Animations[0].Channels;
        Assert.Equal(200, ch.Count);
        Assert.All(ch, c => Assert.Same(ch[0].Values, c.Values));
        Assert.True(used < 3L * shared.Length + 32L * 1024 * 1024, $"{used / 1048576} MB allocated for a file of {shared.Length / 1048576} MB");
        // each channel with its own accessor over the same bytes: past the budget
        var overlap = Assert.Throws<InvalidDataException>(() => GlbScene.Read(ManyChannels(200, 1_000, ownAccessors: true)));
        Assert.Contains("overlap", overlap.Message);
        // too many channels
        Assert.Contains("channels", Assert.Throws<InvalidDataException>(() => GlbScene.Read(ManyChannels(GlbScene.MaxChannels + 1, 2))).Message);
    }

    /// <summary>The JSON's own budget: a JSON chunk past <see cref="GlbScene.MaxJsonBytes"/> (a long list of numbers under "extras", which
    /// nothing reads) is refused from its chunk's length, before its text is parsed, with little allocated; a list of more items than
    /// <see cref="GlbScene.MaxListItems"/> under the cap is refused before it is taken.</summary>
    [Fact]
    public void A_json_past_its_budget_is_refused_before_it_is_parsed()
    {
        long Allocated(Action a) { long before = GC.GetAllocatedBytesForCurrentThread(); a(); return GC.GetAllocatedBytesForCurrentThread() - before; }
        var sb = new StringBuilder(GlbScene.MaxJsonBytes + 1024);
        sb.Append("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{}],\"extras\":[0");
        while (sb.Length <= GlbScene.MaxJsonBytes) sb.Append(",0");
        var huge = Glb.Write(Encoding.UTF8.GetBytes(sb.Append("]}").ToString()), []);
        sb.Clear();
        InvalidDataException? ex = null;
        long used = Allocated(() => ex = Assert.Throws<InvalidDataException>(() => GlbScene.Read(huge)));
        Assert.Contains("JSON", ex!.Message);
        Assert.True(used < 1024 * 1024, $"{used} bytes allocated");
        var slot = Humanoid.Slot(S, 60, armed: false);
        Assert.Equal(ImportRefusal.File, Assert.Throws<ImportRefusedException>(() => Import(slot, huge)).Reason);
        // under the cap, but a list longer than any file needs
        string many = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{}],\"accessors\":[" + string.Join(",", Enumerable.Repeat("0", GlbScene.MaxListItems + 1)) + "]}";
        Assert.Contains("accessors", Assert.Throws<InvalidDataException>(() => GlbScene.Read(Glb.Write(Encoding.UTF8.GetBytes(many), []))).Message);
    }

    /// <summary>A long slot whose new motion is noise on every tick: no keying can follow it within 255 keys, so the curve gets exactly 255
    /// placed keys, the same bytes on a second import, and a read-back that honestly isn't a pass.</summary>
    [Fact]
    public void A_noisy_long_slot_reaches_the_255_key_cap_the_same_way_twice()
    {
        var slot = Humanoid.Slot(S, 3000, armed: false, headStep: 20);
        var rng = new Random(7);
        var noise = Enumerable.Range(0, 3001).Select(_ => (float)(rng.NextDouble() - 0.5) * 12).ToArray();
        var glb = Export(slot, sc => TurnAll(sc, "Head", Vector3.UnitZ, t => noise[t]));
        var r = Import(slot, glb);
        int head = slot.Record!.Tracks.ToList().IndexOf(Core.Hashing.Crc32.Of("Head"));
        Assert.True(r.Fates[head].Written);
        Assert.Equal(255u, r.Rebuilt.Record.Curves[head].Keys);
        var ticks = CurveDecoder.KeyTicks(r.Rebuilt.Record.Curves[head]);
        Assert.Equal((0, 3000), (ticks[0], ticks[^1]));
        var rb = Line(r, "read-back");
        Assert.NotEqual(CheckLevel.Pass, rb.Level);
        Assert.Contains("Head 255 keys", Line(r, "keying").Message);
        // the read-back line, warn or fail, says why: the long slot's keying of the tracks the file changed, named
        int hint = rb.Message.IndexOf("the clip is long", StringComparison.Ordinal);
        Assert.True(hint > 0, rb.Message);
        Assert.Contains("(Head)", rb.Message[hint..]);
        Assert.Contains("Head 255 keys", rb.Message[hint..]);
        var again = Import(slot, glb);
        Assert.Equal(r.Record, again.Record);
        Assert.Equal(r.ReportText(), again.ReportText());
    }

    [Fact]
    public void Channels_on_their_own_key_times_are_evaluated_at_every_tick()
    {
        // a linear channel of two keys, a step channel, and a cubic spline, each at its own times
        var lin = new GltfChannel(0, "translation", GltfInterpolation.Linear, [0f, 1f], [0, 0, 0, 2, 4, 6]);
        var half = lin.At(0.5);
        Assert.Equal((1.0, 2.0, 3.0), (Math.Round(half[0], 4), Math.Round(half[1], 4), Math.Round(half[2], 4)));
        Assert.Equal([2f, 4f, 6f], lin.At(5));
        var stp = new GltfChannel(0, "translation", GltfInterpolation.Step, [0f, 1f], [0, 0, 0, 2, 4, 6]);
        Assert.Equal([0f, 0f, 0f], stp.At(0.99));
        Assert.Equal([2f, 4f, 6f], stp.At(1));
        var q0 = Quaternion.Identity; var q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var rot = new GltfChannel(0, "rotation", GltfInterpolation.Linear, [0f, 2f], [q0.X, q0.Y, q0.Z, q0.W, q1.X, q1.Y, q1.Z, q1.W]);
        var h = rot.At(1);
        Assert.Equal(45, GltfAxes.AngleDeg(new Quaternion(h[0], h[1], h[2], h[3]), q0), 3);
        // a cubic spline with flat tangents passes its keys and eases between them
        var cub = new GltfChannel(0, "translation", GltfInterpolation.CubicSpline, [0f, 1f], [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0]);
        Assert.Equal(5, cub.At(0.5)[0], 3);
        Assert.Equal(10, cub.At(1)[0], 3);
    }

    /// <summary>Text a file holds (a node's name, a channel's path) reaches a refusal's message without its control characters.</summary>
    [Fact]
    public void A_refusal_shows_no_control_character_from_the_file()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        foreach (string json in new[]
        {
            """{"asset":{"version":"2.0"},"nodes":[{"name":"x\r\nwrote\u001b]0;T\u0007"}],"animations":[{"channels":[{"target":{"node":0,"path":"p"}}]}]}""",
            """{"asset":{"version":"2.0"},"nodes":[{"name":"x"}],"animations":[{"channels":[{"target":{"node":0,"path":"p\u007f\u0001"}}]}]}""",
            """{"asset":{"version":"2.0"},"extensionsRequired":["e\u001b[2J"],"nodes":[{"name":"x"}]}""",
            // a line and a paragraph separator, and bidirectional controls (an embedding, an override, an isolate and its end)
            """{"asset":{"version":"2.0"},"nodes":[{"name":"x""" + string.Concat(new[] { 0x2028, 0x2029, 0x202A, 0x202E, 0x2066, 0x2069 }.Select(c => (char)c))
                + """y"}],"animations":[{"channels":[{"target":{"node":0,"path":"p"}}]}]}""",
        })
        {
            var ex = Assert.Throws<ImportRefusedException>(() => Import(slot, Glb.Write(Encoding.UTF8.GetBytes(json), [])));
            Assert.Equal(ImportRefusal.File, ex.Reason);
            // no control character, line or paragraph separator, or bidirectional control
            static bool Unshown(char c) => c < 0x20 || c == 0x7F || (c >= 0x2028 && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069);
            Assert.True(!ex.Message.Any(Unshown), string.Concat(ex.Message.Select(c => Unshown(c) ? $"<{(int)c:X4}>" : c.ToString())));
        }
    }

    /// <summary>Keys so far apart in value that the join between them overflows give no finite place: refused, not written.</summary>
    [Fact]
    public void A_translation_that_isnt_finite_between_keys_is_refused()
    {
        var slot = Humanoid.Slot(S, 60, armed: false, moverPath: ([0, 60], [Vector3.Zero, new(0, 1, 0)]));
        var ex = Assert.Throws<ImportRefusedException>(() => Import(slot, Export(slot, sc =>
        {
            sc.MoverPosition![10] = new Vector3(-3e38f, 0, 0);
            sc.MoverPosition[11] = new Vector3(3e38f, 0, 0);
        })));
        Assert.Equal(ImportRefusal.File, ex.Reason);
        Assert.Contains("finite", ex.Message);
    }

    [Fact]
    public void The_package_list_holds_one_clip_a_slot()
    {
        var slot = Humanoid.Slot(S, 60, armed: false);
        var r = Import(slot, Export(slot));
        var list = new PackageList();
        int changes = 0;
        list.Changed += (_, _) => changes++;
        Assert.False(list.Keep(r));
        Assert.True(list.Keep(r));
        Assert.Single(list.Items);
        Assert.Equal(r.Record, list.Items[0].Record);
        Assert.True(list.Remove(slot.Id));
        Assert.Empty(list.Items);
        Assert.Equal(3, changes);
    }
}
