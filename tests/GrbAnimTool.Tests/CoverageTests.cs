using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Forge;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>The second census, slow: every datafile of the four forges that hold clips, patch forges first so a clip ID counts once (a
/// patch entry overrides the base entry of the same ID). The patch forges are DataPC_patch_01.forge (GRBANIM_FORGE) and
/// DataPC_extra_patch_01.forge beside it; DataPC.forge and DataPC_extra.forge come from the game folder (GRBANIM_GAME). Run it alone with
/// --filter Category=Slow, or leave it out with --filter Category!=Slow.</summary>
[Collection("install")]
public sealed class CoverageTests(InstallFixture f, ITestOutputHelper output)
{
    sealed class SlowFactAttribute : FactAttribute
    {
        public SlowFactAttribute()
        {
            if (!GameData.Available) Skip = GameData.SkipText;
            else if (!File.Exists(ExtraPatch)) Skip = $"no DataPC_extra_patch_01.forge beside {GameData.Forge}";
        }
    }

    static string ExtraPatch => Path.Combine(Path.GetDirectoryName(GameData.Forge!) ?? "", "DataPC_extra_patch_01.forge");

    [SlowFact, Trait("Category", "Slow")]
    public void EveryClipOfTheFourForges()
    {
        var g = f.Install;
        var forges = new[]
        {
            GameData.Forge!, ExtraPatch,
            Path.Combine(GameData.Game!, "DataPC.forge"), Path.Combine(GameData.Game!, "DataPC_extra.forge"),
        };
        var seen = new HashSet<ulong>();
        long clipRecords = 0, walked = 0, full = 0, moverHas = 0, moverOk = 0;
        var notWalked = new List<string>();
        float longest = 0; string longestName = "";
        foreach (var path in forges)
        {
            using var fs = ForgeReader.OpenRead(path);
            var lay = ForgeReader.Read(fs);
            int entries = 0, unread = 0, withClips = 0;
            foreach (var e in lay.Entries)
            {
                entries++;
                List<DatafileRecord> recs;
                try
                {
                    var d = DatafileCodec.Decode(ForgeReader.ReadPayload(fs, e), g.Oodle, out int table, out _);
                    recs = DatafileCodec.Parse(d, table);
                }
                catch (InvalidDataException) { unread++; continue; }
                var cl = recs.Where(r => r.Type == ClipRecord.Type).ToList();
                if (cl.Count == 0) continue;
                withClips++;
                foreach (var r in cl)
                {
                    clipRecords++;
                    if (!seen.Add(r.Id)) continue;
                    ClipRecord c;
                    try { c = ClipRecord.Parse(r.Bytes.ToArray()); }
                    catch (InvalidDataException ex) { notWalked.Add($"{r.Name} 0x{r.Id:X16} in entry 0x{e.Id:X16}: {ex.Message}"); continue; }
                    if (c.Duration > longest) (longest, longestName) = (c.Duration, r.Name);
                    if (!c.WalkedToEnd) { notWalked.Add($"{r.Name} 0x{r.Id:X16} in entry 0x{e.Id:X16}: {c.Stop}"); continue; }
                    walked++;
                    bool ok = true, mHas = false, mOk = true;
                    for (int i = 0; i < c.Curves.Count && i < c.Tracks.Count; i++)
                    {
                        var u = SharedCurveTable.Resolve(c.Curves[i], g.SharedTable);
                        bool rot = u is not null && CurveTags.IsRotation(u.Tag), pos = u is not null && CurveTags.IsPosition(u.Tag);
                        bool dec = u is not null && (rot ? CurveDecoder.KeyRot(u) is not null : !pos || CurveDecoder.KeyPos(u) is not null);
                        if ((rot || pos || u is null) && !dec) ok = false;
                        if (c.Tracks[i] == 0) { mHas = true; if (!dec) mOk = false; }
                    }
                    if (ok) full++;
                    if (mHas) { moverHas++; if (mOk) moverOk++; }
                }
            }
            output.WriteLine($"{Path.GetFileName(path)}: {entries} entries, {withClips} holding clips, {unread} not read as datafiles");
        }
        output.WriteLine($"clip records {clipRecords}; unique IDs {seen.Count}; walked {walked}; every rotation and position curve decoded {full}; mover present {moverHas}, decoded {moverOk}");
        output.WriteLine($"longest clip: {longestName} {longest:F3} s (the parse refuses over {ClipRecord.MaxDuration} s)");
        foreach (var s in notWalked) output.WriteLine("not walked: " + s);
        Assert.Equal(21_626, seen.Count);
        Assert.Equal(21_625, walked);
        Assert.Equal(21_625, full);
        Assert.Equal(20_986, moverHas);
        Assert.Equal(moverHas, moverOk);
    }
}
