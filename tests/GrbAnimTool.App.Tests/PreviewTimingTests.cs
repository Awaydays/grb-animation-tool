using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls.Primitives;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>The previews' timing on the game's files: on the Edit screen, a 1.5x speed edit side by side, the original plays in its own
/// time (its own 1x tick after a fixed real time, not the tick the result's maps to) until "Original follows the edit's progress" is
/// ticked, which then shows the mapped tick and is remembered in the settings file the run names; a scrub and a pause follow the rules.
/// On the Import screen the game's clip, the import's length, keeps the import's tick either way.</summary>
[Collection("wpf")]
public sealed class PreviewTimingTests
{
    static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    static void Tick(ToggleButton box, bool on)
    {
        box.IsChecked = on;
        Click(box);
    }

    [GameDataFact]
    public void TheOriginalPlaysInItsOwnTimeUntilItFollowsTheEdit()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "preview-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string settings = Path.Combine(dir, "settings.json");
        try
        {
            Wpf.RunAsync(async () =>
            {
                // no settings file named: off
                PreviewChoices.SettingsPath = null;
                Assert.False(PreviewChoices.OriginalFollowsEdit);
                var edit = new EditView();
                edit.SetInstall(g, true, new LibraryView(), marks: false);
                var speed = ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--edit", "fm_std_HG_V0_idle_cool_06", "--edit-kind", "speed", "--factor", "1.5"]);
                Assert.Null(await edit.EditForAutomationAsync(speed));
                var o = edit.Outcome!;
                Assert.Equal(CompareLayout.SideBySide, edit.Layout);
                Assert.False(edit.FollowsEditBox.IsChecked);
                Assert.Contains("the original in its own time", edit.LegendText.Text);
                float originalSeconds = o.Original.LastTick / 60f, resultSeconds = o.Edited.LastTick / 60f;
                Assert.Equal(originalSeconds, edit.Reference.Duration);

                // Play, then a fixed real time in frames of 0.05 s: within the result's first loop, so the two ways differ
                Click(edit.PlayButton);
                Assert.True(edit.Clock.Playing);
                int frames = (int)Math.Min(20, Math.Floor(resultSeconds * 0.8 / 0.05));
                Assert.True(frames >= 4, $"the result lasts {resultSeconds:F2} s");
                for (int i = 0; i < frames; i++) edit.Frame(0.05);
                double played = frames * 0.05;
                int t = edit.Clock.Tick;
                Assert.Equal(PlaybackClock.TickOf((float)played), t);
                int own = PlaybackClock.TickOf((float)played), mapped = o.OriginalTick(t);
                Assert.NotEqual(own, mapped);
                Assert.Equal(own, edit.OriginalTickShown);
                Assert.Contains(FormattableString.Invariant($"the original in its own time: {edit.Reference.Time:F3} s  tick {own} / {o.Original.LastTick}"), edit.OverlayText.Text);

                // ticked (the run's settings file named): the mapped tick, today's way, and remembered
                PreviewChoices.SettingsPath = settings;
                Tick(edit.FollowsEditBox, true);
                Assert.True(PreviewChoices.OriginalFollowsEdit);
                Assert.Equal(mapped, edit.OriginalTickShown);
                Assert.Contains($"the original's tick {mapped}, following the edit's progress", edit.OverlayText.Text);
                Assert.Contains("at the same point of its move", edit.LegendText.Text);
                Assert.True(ViewerSettings.Load(settings).OriginalFollowsEdit);
                // read again from the file, a new screen starts with it ticked
                PreviewChoices.SettingsPath = settings;
                Assert.True(PreviewChoices.OriginalFollowsEdit);
                Assert.True(new EditView().FollowsEditBox.IsChecked);

                // frames go by while it is ticked: the result's tick drawn beside it mapped, the own time going on underneath
                for (int i = 0; i < 2; i++) edit.Frame(0.05);
                played += 0.1;
                t = edit.Clock.Tick;
                Assert.Equal(o.OriginalTick(t), edit.OriginalTickShown);
                own = PlaybackClock.TickOf((float)played);
                Assert.Equal(own, PlaybackClock.TickOf(edit.Reference.Time));
                Assert.NotEqual(PlaybackClock.TickOf((float)(played - 0.1)), own);

                // unticked: its own time again, which went on all along
                Tick(edit.FollowsEditBox, false);
                Assert.False(ViewerSettings.Load(settings).OriginalFollowsEdit);
                Assert.Equal(own, edit.OriginalTickShown);

                // past the result's end the result loops, the original plays on to its own length
                double more = resultSeconds - played + 0.25;
                for (double left = more; left > 1e-9; left -= 0.05) edit.Frame(Math.Min(0.05, left));
                played += more;
                Assert.True(played > resultSeconds && played < originalSeconds, $"{played:F2} s played: the result {resultSeconds:F2} s, the original {originalSeconds:F2} s");
                Assert.InRange(edit.Clock.Tick, 14, 16);
                Assert.InRange(edit.OriginalTickShown, PlaybackClock.TickOf((float)played) - 1, PlaybackClock.TickOf((float)played) + 1);

                // pause: both stop
                Click(edit.PlayButton);
                Assert.False(edit.Clock.Playing);
                int pausedResult = edit.Clock.Tick, pausedOriginal = edit.OriginalTickShown;
                for (int i = 0; i < 10; i++) edit.Frame(0.05);
                Assert.Equal(pausedResult, edit.Clock.Tick);
                Assert.Equal(pausedOriginal, edit.OriginalTickShown);

                // a scrub: the result's timeline, the original at the same time from its start
                edit.TimeSlider.Value = resultSeconds / 2;
                Assert.Equal(PlaybackClock.TickOf(resultSeconds / 2), edit.Clock.Tick);
                Assert.Equal(edit.Clock.Tick, edit.OriginalTickShown);
                // Play after it: both run on from there
                Click(edit.PlayButton);
                for (int i = 0; i < 6; i++) edit.Frame(0.05);
                Assert.Equal(edit.Clock.Tick, edit.OriginalTickShown);
                Click(edit.PlayButton);

                // a picture's shot puts the way for itself, without remembering it
                Assert.Null(edit.ShowForShot(new ShotSpec("a.png", null, 0.5f, null, Follows: true), ViewerArgs.Parse([])));
                Assert.Equal(o.OriginalTick(30), edit.OriginalTickShown);
                Assert.False(ViewerSettings.Load(settings).OriginalFollowsEdit);
                Assert.Null(edit.ShowForShot(new ShotSpec("b.png", null, 0.5f, null, Follows: false), ViewerArgs.Parse([])));
                Assert.Equal(30, edit.OriginalTickShown);
            });
        }
        finally
        {
            Wpf.Run(() => PreviewChoices.SettingsPath = null);
            Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g)).GetAwaiter().GetResult();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>On the game's files, Loop off on the Edit screen: a swap (the result and the original of one length) stops at its end
    /// with the original on its own last tick, never back at its start; a speed-up stops with the original at the time the result
    /// played, not a frame past it.</summary>
    [GameDataFact]
    public void WithLoopOffTheOriginalRestsWhereTheResultStopped()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        try
        {
            Wpf.RunAsync(async () =>
            {
                PreviewChoices.SettingsPath = null;
                var edit = new EditView();
                edit.SetInstall(g, true, new LibraryView(), marks: false);
                var swap = ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--edit", "mca_emote_Freeze", "--edit-kind", "swap", "--source", "mca_emote_Diamond_Wedge"]);
                Assert.Null(await edit.EditForAutomationAsync(swap));
                var o = edit.Outcome!;
                Assert.Equal(o.Original.LastTick, o.Edited.LastTick);
                Tick(edit.LoopBox, false);
                Click(edit.PlayButton);
                for (int i = 0; i < (int)(o.Edited.LastTick / 60.0 / 0.031) + 20; i++) edit.Frame(0.031);
                Assert.False(edit.Clock.Playing);
                Assert.Equal(o.Edited.LastTick, edit.Clock.Tick);
                Assert.Equal(o.Original.LastTick, edit.OriginalTickShown);
                Assert.Contains($"tick {o.Original.LastTick} / {o.Original.LastTick})", edit.OverlayText.Text);

                // a speed-up: the original, longer, rests at the time the result played (its tick at the result's length), not past it
                var speed = ViewerArgs.Parse(["--game", GameDataFactAttribute.Game!, "--edit", "fm_std_HG_V0_idle_cool_06", "--edit-kind", "speed", "--factor", "1.5"]);
                Assert.Null(await edit.EditForAutomationAsync(speed));
                o = edit.Outcome!;
                Assert.True(o.Original.LastTick > o.Edited.LastTick);
                Tick(edit.LoopBox, false);
                Click(edit.PlayButton);
                for (int i = 0; i < (int)(o.Edited.LastTick / 60.0 / 0.05) + 20; i++) edit.Frame(0.05);
                Assert.False(edit.Clock.Playing);
                Assert.Equal(o.Edited.LastTick, edit.Clock.Tick);
                Assert.Equal(PlaybackClock.TickOf(edit.Clock.Duration), edit.OriginalTickShown);
            });
        }
        finally
        {
            Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g)).GetAwaiter().GetResult();
        }
    }

    [GameDataFact]
    public void TheGameClipKeepsTheImportsTickEitherWay()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        string dir = Path.Combine(AppContext.BaseDirectory, "preview-timing-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Wpf.RunAsync(async () =>
            {
                PreviewChoices.SettingsPath = null;
                var idle = g.FindClip("mcm_nwp_fm_std_V0_idle_03")!;
                string glb = Path.Combine(dir, "idle_03_head.glb");
                File.WriteAllBytes(glb, HeadTurned(g, idle));
                var import = new ImportView();
                import.SetInstall(g, true);
                await import.ChooseGlbAsync(glb, pickNamed: false);
                Assert.Null(await import.ChooseSlotAsync(idle.Name));
                var r = import.Result!;
                Assert.False(import.FollowsEditBox.IsChecked);
                import.LayoutBox.SelectedIndex = (int)CompareLayout.SideBySide;
                Assert.Equal(r.Slot.Duration, import.Reference.Duration);

                Click(import.PlayButton);
                for (int i = 0; i < 20; i++) import.Frame(0.05);
                int t = import.Clock.Tick;
                Assert.Equal(PlaybackClock.TickOf((float)(1.0 % r.Slot.Duration)), t);
                Assert.Equal(t, import.GameTickShown);
                Assert.Contains($"the game's clip in its own time: {import.Reference.Time:F3} s  tick {t} / {r.Slot.LastTick}", import.OverlayText.Text);
                Tick(import.FollowsEditBox, true);
                Assert.Equal(t, import.GameTickShown);
                Assert.Contains($"the game's clip at tick {t}, following the import's progress", import.OverlayText.Text);
                // a loop past the end: still the same tick
                for (int i = 0; i < 200; i++) import.Frame(0.031);
                Assert.Equal(import.Clock.Tick, import.GameTickShown);
                Tick(import.FollowsEditBox, false);
                Assert.Equal(import.Clock.Tick, import.GameTickShown);
                Click(import.PlayButton);

                // Loop off: the import stops at its end and the game's clip, of the same length, rests on its last tick with it
                Tick(import.LoopBox, false);
                Click(import.PlayButton);
                Assert.True(import.Clock.Playing);
                for (int i = 0; i < (int)(r.Slot.Duration / 0.031) + 20; i++) import.Frame(0.031);
                Assert.False(import.Clock.Playing);
                Assert.Equal(r.Slot.LastTick, import.Clock.Tick);
                Assert.Equal(r.Slot.LastTick, import.GameTickShown);
                Assert.Contains($"tick {r.Slot.LastTick} / {r.Slot.LastTick}\n", import.OverlayText.Text);
                Assert.Contains($"the game's clip in its own time: {r.Slot.Duration:F3} s  tick {r.Slot.LastTick} / {r.Slot.LastTick}", import.OverlayText.Text);
            });
        }
        finally
        {
            Wpf.Run(() => PreviewChoices.SettingsPath = null);
            Task.WhenAll(InstallWork.Graph(g), InstallWork.Swaps(g), InstallWork.Mirror(g)).GetAwaiter().GetResult();
            Wpf.Run(Core.Import.PackageList.Current.Clear);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>The clip with its head turned a little over its length, as a .glb (an import that changes something).</summary>
    static byte[] HeadTurned(GameInstall g, ClipEntry c)
    {
        var sc = SampledClip.Of(g.Skeleton, c.Record!, g.SharedTable);
        var r = sc.Rotations[g.Skeleton.IndexOf(Crc32.Of("Head"))]!;
        for (int t = 0; t < r.Length; t++)
            r[t] = Quaternion.Normalize(r[t] * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 30 * MathF.Sin(MathF.PI * t / (r.Length - 1)) * MathF.PI / 180));
        return GlbWriter.Clip(g.Skeleton, sc, c.Name);
    }
}
