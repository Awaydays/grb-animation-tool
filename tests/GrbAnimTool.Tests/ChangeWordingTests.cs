using System.Globalization;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.Tests;

/// <summary>The one wording of what changed a clip (<see cref="ClipChange.Words"/>), as every screen, the command line and the package list
/// give it: each kind, factors at their edges, a trim from a later tick in seconds of the original clip with its ticks in the detail, the
/// invariant culture whatever the machine's, and the texts the kept list's format 2 holds read back exactly or kept as they are.</summary>
public sealed class ChangeWordingTests
{
    [Fact]
    public void EachKindHasItsShortWords()
    {
        Assert.Equal("Mirrored", ClipChange.Mirror().Words);
        Assert.Equal("Root-motion scale 0.5x", ClipChange.Scale(0.5).Words);
        Assert.Equal("Speed 1.25x", ClipChange.Speed(1.25).Words);
        Assert.Equal("Trim 0.9 s to 7.2 s", ClipChange.Trim(54, 432).Words);
        Assert.Equal("Swap: motion from mca_emote_Shotgun", ClipChange.Swap("mca_emote_Shotgun").Words);
        Assert.Equal("Imported: walk.glb", ClipChange.Imported(Path.GetFullPath(Path.Combine("anims", "walk.glb"))).Words);
        Assert.Equal("Imported: walk.glb", ClipChange.Imported("walk.glb").Words);
        // the same words from the Edit screen's request
        var source = new Core.Install.ClipEntry { Id = 1, Index = 2, Name = "mca_emote_Shotgun", Bytes = Array.Empty<byte>() };
        Assert.Equal("Swap: motion from mca_emote_Shotgun", new EditRequest(EditKind.Swap, Source: source).Describe());
        Assert.Equal("Trim 0.9 s to 7.2 s", new EditRequest(EditKind.Trim, 1, 54, 432).Describe());
        Assert.Equal("Mirrored", new EditRequest(EditKind.Mirror).Describe());
        Assert.Equal(ClipChange.Scale(0.5), new EditRequest(EditKind.Scale, 0.5).Change);
        // only an import isn't an edit
        Assert.All(Enum.GetValues<ChangeKind>(), k => Assert.Equal(k != ChangeKind.Import, new ClipChange { Kind = k }.IsEdit));
    }

    [Fact]
    public void FactorsHaveUpToThreeDecimalsAndNoTrailingZeros()
    {
        Assert.Equal("Speed 2x", ClipChange.Speed(2).Words);
        Assert.Equal("Speed 0.5x", ClipChange.Speed(0.5).Words);
        Assert.Equal("Speed 1.95x", ClipChange.Speed(1.95).Words);
        Assert.Equal("Root-motion scale 0.25x", ClipChange.Scale(0.25).Words);
        Assert.Equal("Root-motion scale 1x", ClipChange.Scale(1).Words);
        Assert.Equal("Speed 0.333x", ClipChange.Speed(1 / 3.0).Words);
        Assert.Equal("Speed 1.235x", ClipChange.Speed(1.2345678).Words);
        Assert.Equal("Speed 2x", ClipChange.Speed(1.9999).Words);
        Assert.Equal("Speed 0.001x", ClipChange.Speed(0.0012).Words);
        // a factor read as a float and widened reads as it was typed
        Assert.Equal("Root-motion scale 0.8x", ClipChange.Scale(0.8f).Words);
        Assert.Equal("Speed 1.1x", ClipChange.Speed(1.1f).Words);
        Assert.Equal("1.25x", ClipChange.FactorText(1.25));
    }

    [Fact]
    public void ATrimIsInSecondsOfTheOriginalClipWithItsTicksInTheDetail()
    {
        // from the clip's start
        Assert.Equal("Trim 0 s to 1.5 s", ClipChange.Trim(0, 90).Words);
        // from a later tick: the cut's own start and end, never shifted to zero; up to two decimals, no trailing zeros
        var later = ClipChange.Trim(54, 484);
        Assert.Equal("Trim 0.9 s to 8.07 s", later.Words);
        Assert.Equal("Trim 0.9 s to 8.07 s, ticks 54 to 484 of the original clip", later.Detail);
        Assert.Equal("Trim 0.02 s to 0.05 s", ClipChange.Trim(1, 3).Words);
        Assert.Equal("Trim 1 s to 2.5 s", ClipChange.Trim(60, 150).Words);
        Assert.Equal("Trim 0.5 s to 1 s", (ClipChange.Trim(15, 30) with { TicksPerSecond = 30 }).Words);
        Assert.Equal("1.33", ClipChange.SecondsText(80));
        // the detail is the words for every other kind
        Assert.Equal("Speed 1.25x", ClipChange.Speed(1.25).Detail);
    }

    [Fact]
    public void TheWordsAreTheSameInAnyCulture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            // a culture writing a decimal comma (built from the invariant one: the tests run without the machine's cultures)
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("1,25", 1.25.ToString(CultureInfo.CurrentCulture));
            Assert.Equal("Speed 1.25x", ClipChange.Speed(1.25).Words);
            Assert.Equal("Trim 0.9 s to 8.07 s", ClipChange.Trim(54, 484).Words);
            Assert.Equal("Root-motion scale 0.5x", new EditRequest(EditKind.Scale, 0.5).Describe());
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void TheTextsOfEarlierVersionsAreReadBackExactlyOrKeptAsTheyAre()
    {
        Assert.Equal(ClipChange.Mirror(), ClipChange.FromKeptText("edit: mirrored"));
        Assert.Equal(ClipChange.Scale(0.5), ClipChange.FromKeptText("edit: root motion scaled by 0.5"));
        Assert.Equal(ClipChange.Scale(2), ClipChange.FromKeptText("edit: root motion scaled by 2"));
        Assert.Equal(ClipChange.Speed(1.25), ClipChange.FromKeptText("edit: played at 1.25 times the speed"));
        Assert.Equal(ClipChange.Speed(0.333), ClipChange.FromKeptText("edit: played at 0.333 times the speed"));
        Assert.Equal(ClipChange.Trim(54, 484), ClipChange.FromKeptText("edit: trimmed to its ticks 54 to 484"));
        Assert.Equal(ClipChange.Swap("mca_emote_Diamond_Wedge"), ClipChange.FromKeptText("edit: mca_emote_Diamond_Wedge's motion swapped in"));
        Assert.Equal(ClipChange.Imported(Path.Combine("anims", "walk.glb")), ClipChange.FromKeptText(Path.Combine("anims", "walk.glb")));
        // anything else after "edit: " is kept as it was, never guessed at
        foreach (var text in new[]
        {
            "edit: trim, ticks 3 to 40", "edit: another clip's motion swapped in", "edit: played at 1,25 times the speed", "edit: Mirrored",
            "edit: root motion scaled by 0", "edit: trimmed to its ticks 40 to 3", "edit: root motion scaled by -1", "edit: ", "edit: mirrored ",
        })
        {
            var c = ClipChange.FromKeptText(text);
            Assert.Equal(ChangeKind.Text, c.Kind);
            Assert.Equal(text, c.Words);
            Assert.True(c.IsEdit);
        }
    }
}
