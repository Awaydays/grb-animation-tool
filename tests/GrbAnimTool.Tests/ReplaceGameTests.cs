using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Tests;

/// <summary>Replace on the game's clips: an enemy's clip of another length on an emote, fitted each way, always the emote's exact length,
/// its events, timing data and non-bone tracks kept byte for byte; the loop's seam closed; the track map.</summary>
[Collection("install")]
public sealed class ReplaceGameTests(InstallFixture f)
{
    ClipEntry Emote => f.Install.FindClip("mca_emote_Hello")!;

    /// <summary>An enemy's clip that reads, longer than the emote, with a record of its own track list.</summary>
    ClipEntry Enemy(bool longer) => f.Install.ForgeClips.First(c => c.Name.StartsWith("nme_", StringComparison.OrdinalIgnoreCase) && c.Record is { WalkedToEnd: true, DurationValid: true }
        && (longer ? c.LastTick > Emote.LastTick + 60 : c.LastTick < Emote.LastTick - 20 && c.LastTick > 10));

    static string Arrays(ClipRecord r) => string.Join(";", ByteArrayTimes.Lists(r, out int unread).Select(l => string.Join(",", l.Values))) + $"|{unread}";

    [GameDataTheory]
    [InlineData(FitMode.Cut, true)]
    [InlineData(FitMode.Speed, true)]
    [InlineData(FitMode.Hold, true)]
    [InlineData(FitMode.Speed, false)]
    [InlineData(FitMode.Hold, false)]
    public void AnEnemysClipOnAnEmoteKeepsTheEmotesLengthEventsAndTiming(FitMode mode, bool longer)
    {
        var g = f.Install;
        var slot = Emote;
        var src = Enemy(longer);
        var r = ClipReplace.FromClip(g.Skeleton, g.SharedTable, slot, src, new FitOptions { Mode = mode, RootScale = 0 });
        var rec = r.Rebuilt.Record;
        Assert.Equal((slot.Id, slot.Name, slot.LastTick, slot.Record!.Duration), (rec.Id, rec.Name, rec.LastTick, rec.Duration));
        Assert.Equal(slot.Record.Tracks, rec.Tracks);
        // the events the emote ends and gives back control by, and its timing lists, as they were
        Assert.Equal(ClipMarks.Events(slot.Record), ClipMarks.Events(rec));
        Assert.Equal(Arrays(slot.Record), Arrays(rec));
        // every track that isn't a bone's rotation or position keeps the emote's bytes
        Assert.All(r.Fates.Where(x => x.Kind is not (TrackKind.Rotation or TrackKind.Position)), x => Assert.False(x.Written, x.ToString()));
        Assert.True(r.Fates.Count(x => x.Written) > 10);
        // the fit is said, with a warning for a speed change
        var fit = r.Report.Results.Single(x => x.Check == "fit");
        Assert.Equal(mode == FitMode.Speed ? CheckLevel.Warn : CheckLevel.Pass, fit.Level);
        // root motion 0: the emote's mover stays where it was
        Assert.Equal(src.LastTick, r.Unfitted!.LastTick);
        var map = ClipReplace.TrackMap(r, g.Skeleton);
        Assert.All(map.Filled, b => Assert.Contains(r.Fates, x => x.Name == b && x.Written));
    }

    [GameDataFact]
    public void ACutFromTheSuggestedStartWithTheLoopClosesItsSeam()
    {
        var g = f.Install;
        var slot = Emote;
        var src = Enemy(longer: true);
        var m = Motion.FromClip(g.Skeleton, src.Record!, g.SharedTable);
        int start = ClipFit.SuggestCut(m, slot.LastTick)!.Value;
        // the suggestion is at least as good as every other start
        double best = ClipFit.PoseDistance(m, start, start + slot.LastTick);
        for (int s = 0; s <= m.LastTick - slot.LastTick; s += 7) Assert.True(best <= ClipFit.PoseDistance(m, s, s + slot.LastTick) + 1e-4);
        var r = ClipReplace.FromClip(g.Skeleton, g.SharedTable, slot, src, new FitOptions { Mode = FitMode.Cut, CutStart = start, Loop = true, RootScale = 0 });
        var played = r.Imported;
        // as the game will play it: the last frame stands within the curves' rounding of the first, on every bone written
        double worst = 0;
        for (int b = 0; b < g.Skeleton.Bones.Count; b++)
            if (r.Fates.Any(x => x.Written && x.Hash == g.Skeleton.Bones[b].Hash))
                worst = Math.Max(worst, Core.Export.GltfAxes.AngleDeg(played.Local(0, b).Rotation, played.Local(played.LastTick, b).Rotation));
        Assert.True(worst < 0.5, $"the seam's worst bone is {worst:F3} deg");
        // the control: the same cut without the loop doesn't close it
        var open = ClipReplace.FromClip(g.Skeleton, g.SharedTable, slot, src, new FitOptions { Mode = FitMode.Cut, CutStart = start + 13, RootScale = 0 }).Imported;
        double gap = 0;
        for (int b = 0; b < g.Skeleton.Bones.Count; b++) gap = Math.Max(gap, Core.Export.GltfAxes.AngleDeg(open.Local(0, b).Rotation, open.Local(open.LastTick, b).Rotation));
        Assert.True(gap > worst, $"without the loop the seam is {gap:F3} deg");
    }

    [GameDataFact]
    public void AReplaceThatCantBeMadeIsRefused()
    {
        var g = f.Install;
        var shorter = Enemy(longer: false);
        // a cut can't fill a longer clip
        var e = Assert.Throws<ImportRefusedException>(() => ClipReplace.FromClip(g.Skeleton, g.SharedTable, Emote, shorter, new FitOptions { Mode = FitMode.Cut }));
        Assert.Equal(ImportRefusal.Length, e.Reason);
        Assert.Contains("Use Speed or Hold", e.Message);
        // a slot that doesn't read
        var broken = new ClipEntry { Index = 1, Id = Emote.Id, Name = Emote.Name, Bytes = Array.Empty<byte>(), ParseError = "damaged", FromFile = true };
        Assert.Equal(ImportRefusal.Slot, Assert.Throws<ImportRefusedException>(() => ClipReplace.FromClip(g.Skeleton, g.SharedTable, broken, shorter, new FitOptions { Mode = FitMode.Hold })).Reason);
    }

    [GameDataFact]
    public void AGlbOfAnotherLengthIsFittedAndOneOfTheClipsLengthTakesTheExactPath()
    {
        var g = f.Install;
        var dir = Path.Combine(Path.GetTempPath(), "grbanim-fitimport-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var src = Enemy(longer: true);
            var glb = Path.Combine(dir, "src.glb");
            Core.Export.ClipExport.Clip(g, src, glb);
            // refused as before without a fit, its message naming Fit
            var e = Assert.Throws<ImportRefusedException>(() => ClipImport.Run(g, Emote, glb));
            Assert.Equal(ImportRefusal.Length, e.Reason);
            Assert.Contains("use Fit", e.Message);
            foreach (var mode in Enum.GetValues<FitMode>())
            {
                var r = ClipImport.Run(g, Emote, glb, fit: new FitOptions { Mode = mode, CutStart = 5 });
                Assert.Equal(Emote.LastTick, r.Rebuilt.Record.LastTick);
                Assert.Equal(src.LastTick, r.Unfitted!.LastTick);
                Assert.Contains(r.Report.Results, x => x.Check == "fit");
                Assert.Equal(ClipMarks.Events(Emote.Record!), ClipMarks.Events(r.Rebuilt.Record));
            }
            // the exact length: no fit line, the length check as before
            var same = Path.Combine(dir, "same.glb");
            Core.Export.ClipExport.Clip(g, Emote, same);
            var exact = ClipImport.Run(g, Emote, same);
            Assert.Null(exact.Fit);
            Assert.DoesNotContain(exact.Report.Results, x => x.Check == "fit");
            Assert.DoesNotContain(exact.Fates, x => x.What.EndsWith(ClipImport.FilledNote, StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [GameDataFact]
    public void AReplaceOnAClipWithFootSyncMarkersWarns()
    {
        var g = f.Install;
        var cycles = g.ForgeClips.Where(c => c.Record is { WalkedToEnd: true, DurationValid: true } r && ClipImport.HoldsFootSync(r)).ToList();
        // the census's count of clips holding the track, for the reader of the record: most of them locomotion cycles, no emote
        Assert.InRange(cycles.Count, 1000, 2500);
        Assert.DoesNotContain(cycles, c => c.Name.StartsWith("mca_emote", StringComparison.Ordinal));
        var slot = cycles.First(c => c.LastTick > 30);
        var r = ClipReplace.FromClip(g.Skeleton, g.SharedTable, slot, Emote, new FitOptions { Mode = FitMode.Speed });
        Assert.Equal(CheckLevel.Warn, r.Report.Results.Single(x => x.Check == "foot sync").Level);
        // the control: an emote's replace has no such line
        var e = ClipReplace.FromClip(g.Skeleton, g.SharedTable, Emote, slot, new FitOptions { Mode = FitMode.Speed });
        Assert.DoesNotContain(e.Report.Results, x => x.Check == "foot sync");
    }

    [GameDataFact]
    public void AClipWhoseTimingRefusesTrimAndSpeedStillTakesAReplace()
    {
        var g = f.Install;
        // a clip whose byte array holds times the tool doesn't adjust: trim and speed refuse it, a replace keeps the times as they are
        var slot = g.ForgeClips.First(c => c.Record is { WalkedToEnd: true, DurationValid: true } rec && rec.LastTick > 60 && ByteArrayTimes.Lists(rec, out _).Any(l => l.HoldsTime));
        var r = ClipReplace.FromClip(g.Skeleton, g.SharedTable, slot, Emote, new FitOptions { Mode = FitMode.Speed });
        Assert.Equal(slot.LastTick, r.Rebuilt.Record.LastTick);
        Assert.Equal(Arrays(slot.Record!), Arrays(r.Rebuilt.Record));
    }
}
