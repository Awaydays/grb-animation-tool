using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Diagnostics;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Tests;

/// <summary>The census of the vanilla animation datafile as a regression test: every number here was read from the game's own files.</summary>
[Collection("install")]
public sealed class CensusTests(InstallFixture f)
{
    [GameDataFact]
    public void TheDatafileAndTheCounts()
    {
        var g = f.Install;
        Assert.Equal(46, g.AnimationDatafileIndex);
        Assert.Equal("46_-_MIS_Y2E4_Katya_Maksimov.data", g.RecordFolderName);
        var c = ClipCensus.Run(g);
        Assert.Equal(10_588, c.Records);
        Assert.Equal(9_938, c.ClipRecords);
        Assert.Equal(0, c.ParseFailures);
        Assert.Equal(9_938, c.Walked);
        Assert.Equal(0, c.NotWalked);                     // so every clip has one curve per track and a duration in range
        Assert.Equal(0, c.UnresolvedReferences);
        Assert.Equal(9_938, c.FullyDecoded);
        Assert.Equal(9_658, c.AllKindsDecodedOrSized);   // 280 clips hold forms 14 and 15, whose values' meaning isn't known
        Assert.Equal("fm_std_little_starts_stop_ale", c.LongestName);
        Assert.Equal(151.4, Math.Round(c.LongestDuration, 2));   // the parse refuses over ClipRecord.MaxDuration (600 s)
        Assert.Equal(34_549, c.SharedTableCurves);
        Assert.True(c.SharedTableAtEnd);
        Assert.Equal(2_559, c.ClipsOver255Ticks);
        Assert.Equal(822, c.ClipsWithCurvesOf256Keys);
        Assert.Equal(5_810, c.CurveUsesOf256Keys);
        Assert.Equal(9_733, c.MoverPresent);
        Assert.Equal(9_733, c.MoverDecoded);
    }

    /// <summary>Curve uses and the clips holding them, per tag.</summary>
    static readonly (byte Tag, long Uses, int Clips)[] Tags =
    [
        (0x09, 186_711, 9_292), (0x0A, 32_301, 2_521), (0x0B, 6_363, 1_963),
        (0x0C, 180_701, 9_722), (0x0D, 47_943, 2_525), (0x0E, 25_462, 2_037),
        (0x0F, 583, 224), (0x10, 152, 52), (0x11, 37, 27),
        (0x15, 136_148, 9_614), (0x16, 10_186, 1_715), (0x17, 3_092, 1_093),
        (0x18, 5_799, 765), (0x19, 433, 123), (0x1A, 200, 85),
        (0x1B, 88_797, 9_576), (0x1C, 10_325, 2_517), (0x1D, 4_705, 2_107),
        (0x27, 40_035, 7_003), (0x28, 1_646, 142), (0x29, 928, 244),
        (0x2A, 410, 199), (0x2B, 34, 13), (0x2C, 35, 10),
        (0x2D, 50, 50), (0x2E, 11, 11),
        (0x36, 11_949, 4_472), (0x37, 3_797, 1_591),
        (0x42, 6_407, 3_309), (0x43, 1_593, 871),
    ];

    [GameDataFact]
    public void FormsByTag()
    {
        var c = ClipCensus.Run(f.Install);
        Assert.Equal(Tags.Select(t => t.Tag), c.TagCurves.Keys);
        foreach (var (tag, uses, clips) in Tags)
        {
            Assert.True(uses == c.TagCurves[tag], $"tag 0x{tag:X2}: {c.TagCurves[tag]} uses, expected {uses}");
            Assert.True(clips == c.TagClips[tag], $"tag 0x{tag:X2}: in {c.TagClips[tag]} clips, expected {clips}");
        }
        long Sum(byte a, byte b) => c.TagCurves.Where(t => t.Key >= a && t.Key <= b).Sum(t => t.Value);
        Assert.Equal(254_106, Sum(0x0C, 0x0E));   // 6-byte rotations
        Assert.Equal(149_426, Sum(0x15, 0x17));   // 2-byte, about Z
        Assert.Equal(225_375, Sum(0x09, 0x0B));   // 4-byte
        Assert.Equal(6_432, Sum(0x18, 0x1A));     // 12-byte
        Assert.Equal(772, Sum(0x0F, 0x11));       // 8-byte
        Assert.Equal(103_827, Sum(0x1B, 0x1D));   // positions
        Assert.Equal(8_000, Sum(0x42, 0x43));     // plants
        // decoded / sized, values not read / not read
        Assert.Equal((636_111L, 0L, 0L), c.ByKind[TrackKind.Rotation]);
        Assert.Equal((103_827L, 0L, 0L), c.ByKind[TrackKind.Position]);
        Assert.Equal((8_000L, 0L, 0L), c.ByKind[TrackKind.Plant]);
        Assert.Equal((0L, 15_746L, 0L), c.ByKind[TrackKind.Float]);   // form 18: no reader yet
        Assert.Equal((0L, 42_609L, 479L), c.ByKind[TrackKind.Byte]);  // form 13: no reader yet; form 14 not known
        Assert.Equal((0L, 0L, 61L), c.ByKind[TrackKind.Kind4]);
    }

    [GameDataFact]
    public void EveryClipIsTheGamesOwn()
    {
        var g = f.Install;
        Assert.NotNull(g.Vanilla);
        Assert.Equal(21_377, g.Vanilla!.Count);
        Assert.All(g.Clips, c => Assert.Equal(ClipStatus.Vanilla, c.Status));
        // the other datafiles' clips too, every one of them read (their records read only now)
        Assert.All(g.OtherClips, c => Assert.Equal(ClipStatus.Vanilla, c.Status));
        Assert.Equal(11_439, g.OtherClips.Count);
        Assert.Equal(0, g.VanillaClipsMissing);
    }
}
