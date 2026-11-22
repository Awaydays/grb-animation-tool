using System.Buffers.Binary;
using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Diagnostics;
using GrbAnimTool.Core.Writing;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>The write path on the game's own clips: (a) every clip rebuilt with nothing replaced gives its record back byte for byte; (b)
/// every curve in a form with a writer, decoded and written again, keeps its size, key count and times byte for byte, its values within the
/// form's rounding; every clip rebuilt with all of them written again reads back; damaged copies are refused cleanly.</summary>
[Collection("install")]
public sealed class RebuildTests(InstallFixture f, ITestOutputHelper output)
{
    [GameDataFact]
    public void EveryClipRebuiltUnchangedIsItsRecord()
    {
        var g = f.Install;
        int same = 0;
        var bad = new List<string>();
        Within.Seconds(300, () =>
        {
            foreach (var e in g.Clips)
            {
                try
                {
                    var r = ClipRebuild.Rebuild(e.Record!, g.SharedTable);
                    if (r.Bytes.AsSpan().SequenceEqual(e.Bytes.Span) && r.Rewritten == 0) same++;
                    else bad.Add($"{e.Name}: not the same bytes");
                }
                catch (RebuildRefusedException ex) { bad.Add(ex.Message); }
            }
            return 0;
        });
        output.WriteLine($"rebuilt with nothing replaced: {same} of {g.Clips.Count} the same bytes");
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(20)));
        Assert.Equal(9938, same);
    }

    sealed class TagStats
    {
        public int Curves, MaxKeys, AllBytesSame, ValuesDiffer, Long; public double WorstDeg, WorstFloatDotDeg; public long Keys, KeysDiffer;
    }

    /// <summary>One curve decoded and written again: its layout (tag, size, key count, times) must be the game's byte for byte; positions,
    /// plants and bytes exactly; rotations within their form's rounding (the worst angle is kept).</summary>
    static void Check(Curve c, SortedDictionary<byte, TagStats> stats, List<string> bad, string where)
    {
        if (c.IsReference || !CurveReencoder.HasWriter(c.Tag)) return;
        var s = stats.TryGetValue(c.Tag, out var x) ? x : stats[c.Tag] = new TagStats();
        s.Curves++; s.Keys += c.Keys; s.MaxKeys = Math.Max(s.MaxKeys, (int)c.Keys);
        if (c.Keys >= 256) s.Long++;
        byte[] w;
        try { w = CurveReencoder.Reencode(c)!; }
        catch (ArgumentException ex) { bad.Add($"{where}: {ex.Message}"); return; }
        var game = c.Source.AsSpan(c.At, c.DiskLength);
        int layout = c.ValuesAt - c.At;   // tag, size, key count and times
        if (w.Length != game.Length || !w.AsSpan(0, layout).SequenceEqual(game[..layout])) { bad.Add($"{where}: tag 0x{c.Tag:X2}, {c.Keys} keys: the layout differs"); return; }
        if (w.AsSpan().SequenceEqual(game)) { s.AllBytesSame++; return; }
        if (!CurveTags.IsRotation(c.Tag)) { bad.Add($"{where}: tag 0x{c.Tag:X2}: the values differ"); return; }
        s.ValuesDiffer++;
        var again = CurveWalker.Walk(w, 0, 1, out _, out _)[0];
        for (int k = 0; k < c.Keys; k++)
        {
            if (w.AsSpan(again.ValuesAt + k * c.ValueBytes, c.ValueBytes).SequenceEqual(game.Slice(c.ValuesAt - c.At + k * c.ValueBytes, c.ValueBytes))) continue;
            s.KeysDiffer++;
            Quaternion a = CurveDecoder.KeyRot(c, k)!.Value, b = CurveDecoder.KeyRot(again, k)!.Value;
            s.WorstDeg = Math.Max(s.WorstDeg, Angles.Deg(a, b));
            s.WorstFloatDotDeg = Math.Max(s.WorstFloatDotDeg, Angles.FloatDotDeg(a, b));
        }
    }

    static string Table(SortedDictionary<byte, TagStats> stats) => string.Join("\n", stats.Select(kv =>
        $"tag 0x{kv.Key:X2}: {kv.Value.Curves} curves, {kv.Value.Keys} keys, up to {kv.Value.MaxKeys} keys ({kv.Value.Long} of 256 or more); every byte the same {kv.Value.AllBytesSame}; "
        + $"values differ in {kv.Value.ValuesDiffer} curves ({kv.Value.KeysDiffer} keys), worst {kv.Value.WorstDeg:F5} deg (by a float dot product {kv.Value.WorstFloatDotDeg:F4} deg)"));

    /// <summary>The tolerance of a rotation re-encoded from its decoded value, by the angle in double precision. These pin the vanilla
    /// datafile (worst 0.00346 deg in the 6-byte forms, 0.00003 deg in the 12-byte ones); they are not a bound for any input: one step of a
    /// 6-byte component is about 0.0049 deg, and more than one component may step.</summary>
    const double Tolerance6Deg = 0.005, Tolerance12Deg = 1e-4;

    [GameDataFact]
    public void EveryWritableCurveWrittenAgain()
    {
        var g = f.Install;
        var clips = new SortedDictionary<byte, TagStats>();
        var table = new SortedDictionary<byte, TagStats>();
        var bad = new List<string>();
        Within.Seconds(300, () =>
        {
            foreach (var e in g.Clips)
                for (int i = 0; i < e.Record!.Curves.Count; i++) Check(e.Record.Curves[i], clips, bad, $"{e.Name} track {i}");
            for (int i = 0; i < g.SharedTable.Curves.Count; i++) Check(g.SharedTable.Curves[i], table, bad, $"shared curve {i + 1}");
            return 0;
        });
        output.WriteLine("inline curves of the datafile's clips:\n" + Table(clips));
        output.WriteLine("the shared table's curves:\n" + Table(table));
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(20)));
        foreach (var st in new[] { clips, table })
            foreach (var (tag, s) in st)
                Assert.True(s.WorstDeg <= (tag is 0x18 or 0x19 ? Tolerance12Deg : Tolerance6Deg), $"tag 0x{tag:X2}: worst {s.WorstDeg} deg");
        // the counts on the vanilla datafile (2026-10-05 files): curves by tag, how many come back with every byte the same, and how many of
        // 256 keys or more; only rotations differ, in their values alone
        var want = new Dictionary<byte, (int Curves, int Same, int Long)>
        {
            [0x0C] = (102_479, 102_142, 0), [0x0D] = (47_319, 47_070, 3_892), [0x18] = (3_774, 3_027, 0), [0x19] = (429, 12, 7),
            [0x1B] = (49_869, 49_869, 0), [0x1C] = (10_280, 10_280, 1_063), [0x27] = (1_862, 1_862, 0), [0x28] = (1_569, 1_569, 365),
            [0x42] = (6_081, 6_081, 0), [0x43] = (1_589, 1_589, 0),
        };
        Assert.Equal(want.Keys.Order(), clips.Keys);
        foreach (var (tag, (n, same, lng)) in want)
            Assert.True(clips[tag].Curves == n && clips[tag].AllBytesSame == same && clips[tag].Long == lng && clips[tag].ValuesDiffer == n - same,
                $"tag 0x{tag:X2}: {clips[tag].Curves} curves, {clips[tag].AllBytesSame} the same, {clips[tag].Long} long");
        // the shared table: the same tags in the counts its census gives
        var wantTable = new Dictionary<byte, (int Curves, int Same)>
        {
            [0x0C] = (4_297, 4_283), [0x0D] = (266, 261), [0x18] = (291, 268), [0x19] = (2, 0), [0x1B] = (5_226, 5_226), [0x1C] = (21, 21),
            [0x27] = (414, 414), [0x28] = (35, 35), [0x42] = (143, 143), [0x43] = (2, 2),
        };
        Assert.Equal(wantTable.Keys.Order(), table.Keys);
        foreach (var (tag, (n, same)) in wantTable)
            Assert.True(table[tag].Curves == n && table[tag].AllBytesSame == same, $"shared tag 0x{tag:X2}: {table[tag].Curves} curves, {table[tag].AllBytesSame} the same");
    }

    [GameDataFact]
    public void EveryClipRebuiltWithItsCurvesWrittenAgain()
    {
        var r = Within.Seconds(600, () => RebuildPass.Run(f.Install));
        output.WriteLine($"clips {r.Clips}, skipped {r.Skipped}, same bytes {r.SameBytes}, curves re-encoded {r.CurvesReencoded}, sound {r.ReencodedSound}, keys {r.Keys}, digest {r.Digest}");
        Assert.True(r.Clean, string.Join("\n", r.Faults));
        Assert.Equal(9938, r.Clips);
        Assert.Equal(225_251, r.CurvesReencoded);   // every inline curve of a form with a writer, as counted by tag in the test above
    }

    /// <summary>Every clip holding events (steps, sounds and the rest), made 1.25 times as long. A clip whose byte array holds times is
    /// refused for that alone, and <see cref="DurationWriter.CanChange"/> says so beforehand. In every other one each event group's time
    /// list is found by the group reader and scaled, every step object lies in a scaled list, and no byte changes but the duration's four
    /// and the listed times: checked byte by byte against the game's record, not through the group reader.</summary>
    [GameDataFact]
    public void EveryClipWithEventsScalesItsTimesWithItsLength()
    {
        var g = f.Install;
        int withEvents = 0, withSteps = 0, scaled = 0, scaledSteps = 0, arrayRefused = 0, lists = 0, stepOnlyFinds = 0;
        var refused = new List<string>();
        Within.Seconds(300, () =>
        {
            foreach (var e in g.Clips)
            {
                var c = e.Record!;
                int steps = StepTimeList.StepWords(c.Bytes, c.FirstCurve - 12).Count;
                var want = Core.Export.EventObjects.Groups(c).Where(x => x.Events.Count > 0).ToList();
                if (steps == 0 && want.Count == 0) continue;
                withEvents++;
                if (steps > 0) withSteps++;
                if (StepTimeList.Find(c) is not null) stepOnlyFinds++;
                float s = c.Duration * 1.25f;
                bool can = DurationWriter.CanChange(c, out string why);
                if (ByteArrayTimes.HoldsTimes(c))
                {
                    Assert.False(can, c.Name);
                    Assert.Equal(DurationWriter.UnreadTimesReason, why);
                    var ex = Assert.Throws<RebuildRefusedException>(() => DurationWriter.Change(c, s));
                    Assert.Contains(DurationWriter.UnreadTimesReason, ex.Message);
                    arrayRefused++;
                    continue;
                }
                try
                {
                    var ch = DurationWriter.Change(c, s);
                    Assert.True(can, $"{c.Name}: {why}");
                    var y = ClipRecord.Parse(ch.Bytes);
                    Assert.Equal(want.Count, ch.Lists.Count);
                    Assert.Equal(steps, ch.StepObjects);
                    var back = Core.Export.EventObjects.Groups(y).Where(x => x.Events.Count > 0).ToList();
                    for (int i = 0; i < want.Count; i++)
                        Assert.Equal(want[i].Times.Select(t => (int)MathF.Round(t * s / c.Duration)), back[i].Times);
                    // byte by byte: only the duration and the listed times may differ
                    var allowed = new HashSet<int>(Enumerable.Range(c.IdAt + 13, 4));
                    foreach (var w in want) for (int i = 0; i < 2 * w.Times.Count; i++) allowed.Add(w.TimesAt + i);
                    Assert.Equal(c.Bytes.Length, ch.Bytes.Length);
                    for (int i = 0; i < c.Bytes.Length; i++)
                        if (c.Bytes[i] != ch.Bytes[i]) Assert.True(allowed.Contains(i), $"{c.Name}: byte +0x{i:X} changed, outside the duration and the listed times");
                    lists += ch.Lists.Count;
                    scaled++;
                    if (steps > 0) scaledSteps++;
                }
                catch (RebuildRefusedException ex) { refused.Add(ex.Message); }
            }
            return 0;
        });
        output.WriteLine($"clips with events {withEvents} ({withSteps} with step objects): refused for times in the byte array {arrayRefused}; every time scaled in {scaled} "
            + $"({scaledSteps} with step objects, {lists} lists), refused otherwise {refused.Count}; a list after the step objects alone found in {stepOnlyFinds}");
        foreach (var r in refused.Take(20)) output.WriteLine("   refused: " + r);
        Assert.Equal(6743, withEvents);
        Assert.Equal(5628, withSteps);
        Assert.Equal(3918, stepOnlyFinds);
        Assert.Equal(ArrayTimesCounts.EventClipsRefused, arrayRefused);
        Assert.Equal(6743 - ArrayTimesCounts.EventClipsRefused, scaled);
        Assert.Empty(refused);
    }

    /// <summary>The byte array's lists on the game's clips: every block reads by the rule, its payload fitting its form exactly (none
    /// unread); the two-byte lengths, the u16 lists, the u8 ones, and the clips holding a time in either, counted; the clips the u16 lists
    /// alone were first counted in (a list of rising u16 times after the block's form word, the last within the clip's length in 1/120 s,
    /// one above 0) are all among them.</summary>
    [GameDataFact]
    public void TheByteArrayTimesOnTheGamesClips()
    {
        var g = f.Install;
        int blocks = 0, u16 = 0, u8 = 0, unread = 0, holding = 0, holding16 = 0, firstRule = 0, endAtLength = 0, small16 = 0, twoByteLengths = 0;
        int counted = 0, countMatches = 0, headerOnly = 0, empty = 0;
        foreach (var e in g.Clips)
        {
            var c = e.Record!;
            var l = ByteArrayTimes.Lists(c, out int bad);
            // the header's block count against the blocks read, on every array that holds the count's marker
            var arr = ByteArrayTimes.ArrayOf(c)!.Value;
            if (ByteArrayTimes.HeaderCount(c) is { } count)
            {
                counted++;
                if (count == l.Count) countMatches++;
            }
            else if (arr.Length == ByteArrayTimes.HeaderOnlyLength && l.Count == 0) headerOnly++;
            else if (arr.Length == 0) empty++;
            unread += bad;
            blocks += l.Count;
            u16 += l.Count(x => x.Width == 2);
            u8 += l.Count(x => x.Width == 1);
            // the bytes between the head's FF FF FF and the form's own bytes are the length's varint
            twoByteLengths += l.Count(x => x.TimesAt - x.BlockAt - 10 - (x.Width == 1 ? 2 : 4) == 2);
            small16 += l.Count(x => x.Width == 2 && x.Values.All(v => v < 256));
            int t120 = (int)MathF.Round(c.Duration * 120);
            endAtLength += l.Count(x => x.Values.Count > 0 && x.Values[^1] == t120);
            bool h = ByteArrayTimes.HoldsTimes(c);
            Assert.Equal(bad > 0 || l.Any(x => x.HoldsTime), h);
            if (h) holding++;
            if (l.Any(x => x.Width == 2 && x.HoldsTime)) holding16++;
            if (FirstRule(c)) { firstRule++; Assert.True(h, c.Name); }
        }
        output.WriteLine($"blocks {blocks} (u16 lists {u16}, u8 lists {u8}; two-byte lengths {twoByteLengths}), unread {unread}; u16 lists with every value below 256 {small16}; "
            + $"lists ending at the clip's length {endAtLength}; clips holding a time {holding} (in a u16 list {holding16}); by the first rule {firstRule}");
        output.WriteLine($"arrays with the header's count {counted}, its count equal to the blocks read {countMatches}; header-only arrays {headerOnly}; empty {empty}");
        Assert.Equal(0, unread);   // every payload fits its form exactly, followed by 02 00 and a word or the array's end
        Assert.Equal(8_724, counted);
        Assert.Equal(8_724, countMatches);   // every array's header count is the blocks read
        Assert.Equal(153, headerOnly);        // the arrays without the count are all the header alone
        Assert.Equal(1_061, empty);
        Assert.Equal(g.Clips.Count, counted + headerOnly + empty);

        // a game clip of one block (FA_cigar_01, times 260 280) with one byte of its block word changed: the block is found by no search,
        // the header's count still says it is there, so the clip is refused for a length change
        var cigar = g.Clips.First(x => x.Name == "FA_cigar_01").Record!;
        var ca = ByteArrayTimes.ArrayOf(cigar)!.Value;
        var damaged = cigar.Bytes.ToArray();
        int w = damaged.AsSpan(ca.At, ca.Length).IndexOf(ByteArrayTimes.BlockWord) + ca.At;
        damaged[w + 5] ^= 0x01;
        var dc = ClipRecord.Parse(damaged);
        Assert.True(dc.WalkedToEnd, dc.Stop);
        Assert.Empty(ByteArrayTimes.Lists(dc, out int du));
        Assert.Equal(1, du);
        Assert.True(ByteArrayTimes.HoldsTimes(dc));
        Assert.False(DurationWriter.CanChange(dc, out _));
        // and with its header count set to 0 as well: 0 blocks found and a count of 0, but room for a block after the count, so unread
        int cm = damaged.AsSpan(ca.At, ca.Length).IndexOf(ByteArrayTimes.CountMarker) + ca.At + ByteArrayTimes.CountMarker.Length;
        damaged[cm] = 0;
        var zc = ClipRecord.Parse(damaged);
        Assert.Equal(0, ByteArrayTimes.HeaderCount(zc));
        ByteArrayTimes.Lists(zc, out int zu);
        Assert.True(zu > 0);
        Assert.True(ByteArrayTimes.HoldsTimes(zc));
        Assert.False(DurationWriter.CanChange(zc, out _));
        Assert.Equal(31_890, blocks);
        Assert.Equal(55, twoByteLengths);
        Assert.Equal(10_546, u16);
        Assert.Equal(21_344, u8);
        Assert.Equal(0, small16);   // a list is u8 where its times fit a byte: the u8 lists are times of the same kind
        Assert.Equal(ArrayTimesCounts.Clips, holding);
        Assert.Equal(3_699, firstRule);
    }

    /// <summary>The rule the byte array's times were first counted by, read on its own: from the last track object's end to the curves'
    /// wrapper, each block word followed by a byte and FF FF FF, then a byte length L, a u16 form, a u16 count n and n u16 values with
    /// 4 + 2n within L; a clip counts when such values rise, the last is at most its length in 1/120 s plus 1, and one is above 0.</summary>
    static bool FirstRule(ClipRecord c)
    {
        var b = c.Bytes;
        var all = RecordObjects.Objects(b).Where(o => o.At < c.FirstCurve).ToList();
        int lt = all.FindLastIndex(o => o.Class == RecordObjects.TrackClass);
        if (lt < 0) return false;
        int lo = all[lt].At + 16, hi = c.FirstCurve - 12, t120 = (int)MathF.Round(c.Duration * 120);
        var s = b.AsSpan(lo, hi - lo);
        for (int at = s.IndexOf(ByteArrayTimes.BlockWord); at >= 0;)
        {
            int p = at + 10;
            if (p + 5 <= s.Length && s[at + 7] == 0xFF && s[at + 8] == 0xFF && s[at + 9] == 0xFF)
            {
                int len = s[p], n = BinaryPrimitives.ReadUInt16LittleEndian(s[(p + 3)..]);
                if (n >= 1 && 4 + 2 * n <= len && p + 5 + 2 * n <= s.Length)
                {
                    var v = new int[n];
                    for (int i = 0; i < n; i++) v[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[(p + 5 + 2 * i)..]);
                    if (v.Zip(v.Skip(1)).All(x => x.First <= x.Second) && v[^1] <= t120 + 1 && v.Any(x => x > 0)) return true;
                }
            }
            int nx = s[(at + 1)..].IndexOf(ByteArrayTimes.BlockWord);
            at = nx < 0 ? -1 : at + 1 + nx;
        }
        return false;
    }

    static readonly Type[] Clean = [typeof(RebuildRefusedException), typeof(ArgumentException)];

    /// <summary>Runs both rebuilds on a damaged record: each is refused with a <see cref="RebuildRefusedException"/> (or a writer's
    /// <see cref="ArgumentException"/> for a key it can't write), or succeeds; a rebuild with nothing replaced that succeeds must give the
    /// damaged record back as it is. Any other exception fails the test.</summary>
    static (bool Refused, bool ReencodeRefused) Damaged(byte[] b, SharedCurveTable t)
    {
        bool refused = false, reRefused = false;
        try { Assert.Equal(b, ClipRebuild.Rebuild(b, t).Bytes); }
        catch (RebuildRefusedException) { refused = true; }
        try
        {
            var r = ClipRebuild.Rebuild(b, t, (_, _, u) => ReferenceEquals(u.Source, t.Bytes) ? null : CurveReencoder.Reencode(u));
            Assert.True(r.Record.WalkedToEnd);
        }
        catch (Exception ex) when (Clean.Contains(ex.GetType())) { reRefused = true; }
        return (refused, reRefused);
    }

    [GameDataFact]
    public void DamagedRecordsAreRefusedCleanly()
    {
        var g = f.Install;
        var t = g.SharedTable;
        var rnd = new Random(29);
        var all = g.Clips;
        var sample = new[] { g.FindClip("mca_emote_Hello")! }.Concat(Enumerable.Range(0, 60).Select(_ => all[rnd.Next(all.Count)])).ToList();
        int cuts = 0, cutsRefused = 0, flips = 0, headerRefused = 0;
        Within.Seconds(300, () =>
        {
            foreach (var e in sample)
            {
                var b = e.Bytes.ToArray();
                var c = e.Record!;
                // cut anywhere: always refused (the length fields no longer fit)
                var at = new List<int> { 0, 4, 8, 12, c.IdAt, c.IdAt + 13, c.FirstCurve - 12, c.FirstCurve, b.Length / 2, b.Length - 1 };
                for (int i = 0; i < 20; i++) at.Add(rnd.Next(b.Length));
                foreach (int n in at.Where(n => n >= 0 && n < b.Length))
                {
                    cuts++;
                    if (Damaged(b[..n], t).Refused) cutsRefused++;
                }
                // a cut inside the curves with the wrapper's length made to fit, so the walker itself meets the cut
                foreach (int n in new[] { c.FirstCurve + 3, (c.FirstCurve + b.Length) / 2, b.Length - 2 })
                {
                    if (n <= c.FirstCurve || n >= b.Length) continue;
                    var x = b[..n];
                    BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(c.FirstCurve - 8), (uint)(n - (c.FirstCurve - 4)));
                    BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(4), (uint)(n - c.IdAt));
                    cuts++;
                    if (Damaged(x, t).Refused) cutsRefused++;
                }
                // the record's length field and the curves' wrapper (its length and count): a change there must be refused
                foreach (int h in new[] { 4, 5, c.FirstCurve - 8, c.FirstCurve - 7, c.FirstCurve - 4 })
                {
                    var x = (byte[])b.Clone();
                    x[h] ^= 0x5A;
                    Assert.True(Damaged(x, t).Refused, $"{e.Name}: a change at +{h:X} wasn't refused");
                    headerRefused++;
                }
                // an arena entry holding one unit less than the curves need: refused as damaged
                var need = new long[9];
                foreach (var cv in c.Curves) if (!cv.IsReference && ClipRebuild.ArenaOf(cv.Tag, cv.ValueBytes) is var a and > 0) need[a] += ClipRebuild.UnitsOf(cv);
                foreach (var (kind, a) in new[] { (3, 1), (5, 2), (7, 4), (9, 8) })
                {
                    int ka = ClipRebuild.KindAt(b, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8)), c.HeadEntries, kind);
                    if (ka < 0 || need[a] == 0) continue;
                    var x = (byte[])b.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(ka), (uint)(need[a] - 1));
                    Assert.True(Damaged(x, t).Refused, $"{e.Name}: arena kind {kind} below its curves' need wasn't refused");
                    headerRefused++;
                }
                // the curve headers (tags, sizes, key counts): counted, most changes there stop the walk
                foreach (var cv in c.Curves.Take(25))
                    foreach (int h in cv.IsReference ? [cv.At] : new[] { cv.At, cv.At + 1, cv.At + 5 })
                    {
                        var x = (byte[])b.Clone();
                        x[h] ^= 0x5A;
                        if (Damaged(x, t).Refused) headerRefused++;
                    }
                // anywhere: a clean result, whatever byte changes
                for (int i = 0; i < 40; i++)
                {
                    var x = (byte[])b.Clone();
                    x[rnd.Next(x.Length)] ^= (byte)(1 << rnd.Next(8));
                    Damaged(x, t);
                    flips++;
                }
                {
                    var x = (byte[])b.Clone();
                    int s = rnd.Next(x.Length);
                    x.AsSpan(s, Math.Min(64, x.Length - s)).Fill(0xFF);
                    Damaged(x, t);
                }
            }
            return 0;
        });
        output.WriteLine($"cuts {cuts}, refused {cutsRefused}; header changes refused {headerRefused}; random flips {flips}");
        Assert.Equal(cuts, cutsRefused);
        Assert.True(headerRefused > 1000, $"only {headerRefused} header changes refused");
    }
}

/// <summary>The byte array's times on the vanilla datafile (2026-10-05 files): the clips holding a time there (a length change refuses
/// them), and of the clips with events, those refused for it.</summary>
static class ArrayTimesCounts
{
    public const int Clips = 7846, EventClipsRefused = 6116;
}
