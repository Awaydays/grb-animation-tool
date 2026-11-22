using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Retiming;

namespace GrbAnimTool.Tests;

/// <summary>The time rules of a two-clip preview's reference (the Edit screen's original, the Import screen's game clip): in its own time
/// it plays at 1x and loops at its own length, a pause stops both, a scrub or a step puts it at the same time from its start as the shown
/// clip (modulo its own length), and Play runs both on from there; following the edit's progress it stands at the mapped tick. With no
/// game data: a clip of 120 ticks (2 s) and the shown clip as each edit makes it.</summary>
public sealed class ReferenceClockTests
{
    const int OriginalLast = 120;
    const float OriginalSeconds = OriginalLast / 60f;

    /// <summary>The result's clock and the original's, as the Edit screen sets them up for an edit with <paramref name="warp"/>: the result
    /// <paramref name="resultLast"/> ticks long, both at 0, playing.</summary>
    static (PlaybackClock Clock, ReferenceClock Original) Start(int resultLast, TimeWarp? warp)
    {
        var clock = new PlaybackClock();
        clock.Reset(resultLast / 60f, resultLast);
        var original = new ReferenceClock();
        original.Reset(OriginalSeconds, OriginalLast, warp);
        original.Toggle(clock);
        Assert.True(clock.Playing);
        return (clock, original);
    }

    /// <summary>Lets <paramref name="seconds"/> of real time go by in frames of <paramref name="frame"/> s, as the screen's frames do.</summary>
    static void Play(PlaybackClock clock, ReferenceClock original, double seconds, double frame = 0.1)
    {
        int n = (int)Math.Round(seconds / frame);
        for (int i = 0; i < n; i++) original.Advance(clock, frame);
    }

    [Fact]
    public void ASpeedUpEndsSoonerEachLoopWhileTheOriginalPlaysAt1x()
    {
        var warp = TimeWarp.Speed(OriginalLast, 1.25);
        Assert.Equal(96, warp.NewLast);
        var (clock, original) = Start(warp.NewLast, warp);
        Assert.False(original.FollowsEdit);
        Play(clock, original, 0.5);
        Assert.Equal(30, clock.Tick);
        // its own 1x tick, not the tick the result's maps to (30 x 1.25 = 37.5, rounded away from zero)
        Assert.Equal(30, original.TickAt(clock.Tick));
        Assert.Equal(38, ReferenceClock.MappedTick(warp, clock.Tick, OriginalLast));
        // past the result's end (1.6 s) the result loops; the original plays on to its own end
        Play(clock, original, 1.2);
        Assert.Equal(6, clock.Tick);
        Assert.Equal(102, original.TickAt(clock.Tick));
        Assert.Equal(1.7f, original.Time, 3);
        // past its own end (2 s) the original loops too, at its own length
        Play(clock, original, 0.4);
        Assert.Equal(30, clock.Tick);
        Assert.Equal(6, original.TickAt(clock.Tick));
        // following the edit's progress: the mapped tick, the original's own time kept
        original.FollowsEdit = true;
        Assert.Equal(38, original.TickAt(clock.Tick));
        Assert.Equal(6, original.OwnTick);
    }

    [Fact]
    public void ASlowDownLetsTheOriginalLoopTwiceInTheResultsOne()
    {
        var warp = TimeWarp.Speed(OriginalLast, 0.5);
        Assert.Equal(240, warp.NewLast);
        var (clock, original) = Start(warp.NewLast, warp);
        Play(clock, original, 2.5);
        Assert.Equal(150, clock.Tick);
        Assert.Equal(30, original.TickAt(clock.Tick));
        original.FollowsEdit = true;
        Assert.Equal(75, original.TickAt(clock.Tick));
    }

    [Fact]
    public void ATrimLoopsSoonerWhileTheOriginalPlaysItsWholeLength()
    {
        // from the start: the first second kept
        var start = TimeWarp.Trim(OriginalLast, 0, 60);
        var (clock, original) = Start(start.NewLast, start);
        Play(clock, original, 1.5);
        Assert.Equal(30, clock.Tick);
        Assert.Equal(90, original.TickAt(clock.Tick));
        original.FollowsEdit = true;
        Assert.Equal(30, original.TickAt(clock.Tick));

        // from a later tick: the cut's start plays at the result's tick 0; in its own time the original starts at its own tick 0
        var later = TimeWarp.Trim(OriginalLast, 30, 90);
        (clock, original) = Start(later.NewLast, later);
        Play(clock, original, 0.25, frame: 0.05);
        Assert.Equal(15, clock.Tick);
        Assert.Equal(15, original.TickAt(clock.Tick));
        Assert.Equal(45, ReferenceClock.MappedTick(later, clock.Tick, OriginalLast));
        Play(clock, original, 1.0, frame: 0.05);
        Assert.Equal(15, clock.Tick);
        Assert.Equal(75, original.TickAt(clock.Tick));
        original.FollowsEdit = true;
        Assert.Equal(45, original.TickAt(clock.Tick));
    }

    [Fact]
    public void SwapAndMirrorKeepTheSameTickInBothWaysThroughLoopsAndScrubs()
    {
        // the same length and no warp: the original's own tick is the result's at every frame, however the frames fall
        var (clock, original) = Start(OriginalLast, null);
        var rnd = new Random(7);
        for (int i = 0; i < 600; i++)
        {
            original.Advance(clock, 0.004 + rnd.NextDouble() * 0.03);
            Assert.Equal(clock.Tick, original.TickAt(clock.Tick));
            Assert.Equal(clock.Time, original.Time);
        }
        original.FollowsEdit = true;
        Assert.Equal(clock.Tick, original.TickAt(clock.Tick));
        original.FollowsEdit = false;
        // a scrub to the end shows the original's end, not its start
        original.Seek(clock, OriginalSeconds);
        Assert.Equal(OriginalLast, clock.Tick);
        Assert.Equal(OriginalLast, original.TickAt(clock.Tick));
        original.Step(clock, -1);
        Assert.Equal(OriginalLast - 1, original.TickAt(clock.Tick));
    }

    [Fact]
    public void AScrubPutsBothAtTheSameTimeFromTheirStartsAndPlayRunsBothOn()
    {
        var warp = TimeWarp.Speed(OriginalLast, 0.5);
        var (clock, original) = Start(warp.NewLast, warp);
        Play(clock, original, 0.7);
        // a scrub pauses both at the result's time; the original's within its length as it is
        original.Seek(clock, 1.2f);
        Assert.False(clock.Playing);
        Assert.Equal(72, clock.Tick);
        Assert.Equal(72, original.TickAt(clock.Tick));
        // past the original's length: modulo it; at its end exactly: its end
        original.Seek(clock, 3.0f);
        Assert.Equal(180, clock.Tick);
        Assert.Equal(60, original.TickAt(clock.Tick));
        original.Seek(clock, 2.0f);
        Assert.Equal(OriginalLast, original.TickAt(clock.Tick));
        original.Seek(clock, 3.0f);
        // Play after the scrub: both run on from there
        original.Toggle(clock);
        Play(clock, original, 0.5);
        Assert.Equal(210, clock.Tick);
        Assert.Equal(90, original.TickAt(clock.Tick));
        // a step pauses and puts the original at the result's new time, modulo its length
        original.Step(clock, 1);
        Assert.False(clock.Playing);
        Assert.Equal(211, clock.Tick);
        Assert.Equal(91, original.TickAt(clock.Tick));
        // a scrub beyond the result's end is held to it (4 s), and the original follows that: two whole loops of its own, at its end, as
        // it rests when the result stops there with Loop off
        original.Seek(clock, 99f);
        Assert.Equal(240, clock.Tick);
        Assert.Equal(OriginalLast, original.TickAt(clock.Tick));
        // a step to the end the same
        original.Seek(clock, 3.9f);
        original.Step(clock, 10);
        Assert.Equal(240, clock.Tick);
        Assert.Equal(OriginalLast, original.TickAt(clock.Tick));
        // Loop off, the result played to its end: the original rests on its last tick too
        original.Seek(clock, 0f);
        clock.Loop = false;
        original.Toggle(clock);
        Play(clock, original, 4.5);
        Assert.False(clock.Playing);
        Assert.Equal(240, clock.Tick);
        Assert.Equal(OriginalLast, original.TickAt(clock.Tick));
    }

    [Fact]
    public void APauseStopsBothAndAResultThatDoesntLoopStopsTheOriginalAtItsEnd()
    {
        var warp = TimeWarp.Speed(OriginalLast, 1.25);
        var (clock, original) = Start(warp.NewLast, warp);
        Play(clock, original, 0.3);
        original.Toggle(clock);
        Assert.False(clock.Playing);
        float result = clock.Time, own = original.Time;
        Assert.False(original.Advance(clock, 0.1));
        Play(clock, original, 1.0);
        Assert.Equal(result, clock.Time);
        Assert.Equal(own, original.Time);
        // Play runs both on from where they stopped
        original.Toggle(clock);
        Play(clock, original, 0.2);
        Assert.Equal(30, clock.Tick);
        Assert.Equal(30, original.TickAt(clock.Tick));

        // Loop off: the result stops at its end, and the original where it stood then; Play starts both again at 0
        clock.Loop = false;
        Play(clock, original, 1.2);
        Assert.False(clock.Playing);
        Assert.Equal(96, clock.Tick);
        // the original moved only as far as the result did on the frame it stopped: 1.6 s in, its tick 96
        int stopped = original.OwnTick;
        Assert.Equal(96, stopped);
        Play(clock, original, 0.5);
        Assert.Equal(stopped, original.OwnTick);
        original.Toggle(clock);
        Assert.True(clock.Playing);
        Assert.Equal(0, clock.Tick);
        Assert.Equal(0, original.TickAt(clock.Tick));
    }

    /// <summary>Loop off with the two clips of one length (a mirror, a scale, a swap, an import): when the result stops at its end the
    /// original rests on its own last tick with it, never wrapped to its start, whatever the frames; a slow-down's original, shorter,
    /// rests where its own time took it, a whole frame never added past the result's stop.</summary>
    [Theory]
    [InlineData(0.1)]
    [InlineData(1 / 60.0)]
    [InlineData(0.037)]
    [InlineData(0.25)]
    public void LoopOffBothRestOnTheirLastTickWhenTheyAreOfOneLength(double frame)
    {
        var (clock, original) = Start(OriginalLast, null);
        clock.Loop = false;
        Play(clock, original, OriginalSeconds + 0.5, frame);
        Assert.False(clock.Playing);
        Assert.Equal(OriginalLast, clock.Tick);
        Assert.Equal(OriginalLast, original.OwnTick);
        Assert.Equal(OriginalLast, original.TickAt(clock.Tick));
        Assert.Equal(OriginalSeconds, original.Time);
        // following the edit's progress, the same
        original.FollowsEdit = true;
        Assert.Equal(OriginalLast, original.TickAt(clock.Tick));

        // a slow-down: the result 2.5 s, the original 2 s looping on its own, resting half a second into its second loop
        var warp = TimeWarp.Speed(OriginalLast, 0.8);
        Assert.Equal(150, warp.NewLast);
        (clock, original) = Start(warp.NewLast, warp);
        clock.Loop = false;
        Play(clock, original, 3.0, frame);
        Assert.False(clock.Playing);
        Assert.Equal(150, clock.Tick);
        Assert.Equal(30, original.OwnTick);
    }

    [Fact]
    public void ResetMappingAndClipsOfNoLength()
    {
        var o = new ReferenceClock();
        Assert.Equal(0, o.TickAt(5));
        o.Reset(float.NaN, 30);
        Assert.Equal(0, o.Duration);
        Assert.Equal(0, o.LastTick);
        var clock = new PlaybackClock();
        clock.Reset(1, 60);
        o.Toggle(clock);
        o.Advance(clock, 0.5);
        Assert.Equal(30, clock.Tick);
        Assert.Equal(0, o.TickAt(clock.Tick));
        o.Seek(0.5f);
        Assert.Equal(0, o.Time);
        // a new pair of clips starts at 0, the way chosen kept
        o.FollowsEdit = true;
        o.Reset(2, 120);
        Assert.True(o.FollowsEdit);
        Assert.Equal(0, o.Time);
        Assert.Equal(45, o.TickAt(45));
        Assert.Equal(120, o.TickAt(500));
        Assert.Equal(0, o.TickAt(-3));
        Assert.Equal(0, o.Wrap(double.NaN));
        Assert.Equal(0, o.Wrap(-1));
        Assert.Equal(0.5f, o.Wrap(4.5), 4);
        // the mapped tick of no warp is the same tick, clamped to the reference's
        Assert.Equal(7, ReferenceClock.MappedTick(null, 7, 120));
        Assert.Equal(120, ReferenceClock.MappedTick(null, 130, 120));
    }

    [Fact]
    public void TheChoiceIsRememberedLenientlyAndAPictureCanSetIt()
    {
        var s = new ViewerSettings { GameFolder = "g", OriginalFollowsEdit = true };
        Assert.Equal(s, ViewerSettings.FromJson(s.ToJson()));
        Assert.Null(new ViewerSettings().OriginalFollowsEdit);
        // a value that isn't true or false loses only itself
        var odd = ViewerSettings.FromJson("{ \"GameFolder\": \"g\", \"OriginalFollowsEdit\": \"yes\", \"Theme\": \"dark\" }");
        Assert.Null(odd.OriginalFollowsEdit);
        Assert.Equal("g", odd.GameFolder);
        Assert.Equal(ThemeChoice.Dark, odd.ThemeChoice);
        Assert.False(ViewerSettings.FromJson("{ \"OriginalFollowsEdit\": false }").OriginalFollowsEdit);

        var a = ViewerArgs.Parse(["--game", "g", "--shot", "a.png||0.5||edit|||||follows", "--shot", "b.png||0.5||edit||||dark|own", "--shot", "c.png"]);
        Assert.Equal(new ShotSpec("a.png", null, 0.5f, null, "edit", Follows: true), a.Shots[0]);
        Assert.Equal(new ShotSpec("b.png", null, 0.5f, null, "edit", Theme: ThemeChoice.Dark, Follows: false), a.Shots[1]);
        Assert.Null(a.Shots[2].Follows);
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", "g", "--shot", "a.png|||||||||maybe"]));
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", "g", "--shot", "a.png||||||||||"]));
    }
}
