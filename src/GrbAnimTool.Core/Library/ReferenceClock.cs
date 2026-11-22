using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Retiming;

namespace GrbAnimTool.Core.Library;

/// <summary>Where the reference clip of a two-clip preview stands (the Edit screen's original beside or behind the result, the Import
/// screen's game clip beside or behind the import) while the shown clip plays on its <see cref="PlaybackClock"/>, whose timeline is the
/// shown clip's. Two ways, by <see cref="FollowsEdit"/>:
/// <list type="bullet">
/// <item>Its own time (the default): it plays at its own speed, 1x in real time, and loops at its own length, whatever the shown clip's
/// length; with the shown clip's Loop off it stops where that clip stopped (on its own last tick when both end together). A speed-up then visibly ends sooner each loop than the original, a trim loops sooner while the original plays its
/// whole length. Pause stops both (it moves only while the shown clip's clock plays). A scrub or a step of the timeline, a picture's time
/// or a new clip puts it at the same time from its start as the shown clip: the shown clip's time, taken modulo its own length when it is
/// past it (a time within its length, its end included, stays as it is). Play then runs both on from there.</item>
/// <item>Following the edit's progress: it stands at the tick the shown clip's tick maps to (<see cref="MappedTick"/>: the edit's time
/// warp for trim and speed, else the same tick), so both are at the same point of the move.</item>
/// </list>
/// It holds no clock of its own (the screen passes the real time that went by) and allocates nothing as it plays.</summary>
public sealed class ReferenceClock
{
    TimeWarp? _warp;

    /// <summary>On: the reference shows the tick the shown clip's tick maps to. Off: its own time.</summary>
    public bool FollowsEdit { get; set; }

    /// <summary>The reference clip's own length (seconds) and last tick.</summary>
    public float Duration { get; private set; }
    public int LastTick { get; private set; }

    /// <summary>The reference clip's own time, within [0, <see cref="Duration"/>].</summary>
    public float Time { get; private set; }

    /// <summary>The tick of its own time, within its ticks.</summary>
    public int OwnTick => Math.Clamp(PlaybackClock.TickOf(Time), 0, LastTick);

    /// <summary>Starts on a new pair of clips at time 0: the reference's length and last tick, and the warp taking the shown clip's ticks
    /// to its own (null: the same tick). A length a clip record may not state gives a reference of no length, which stays at tick 0.</summary>
    public void Reset(float duration, int lastTick, TimeWarp? warp = null)
    {
        bool ok = float.IsFinite(duration) && duration > 0 && duration <= ClipRecord.MaxDuration;
        Duration = ok ? duration : 0;
        LastTick = ok ? Math.Clamp(lastTick, 0, PlaybackClock.TickOf(Duration)) : 0;
        _warp = warp;
        Time = 0;
    }

    /// <summary>The reference's tick at the shown clip's tick <paramref name="shownTick"/>: the mapped one when following, else its own.</summary>
    public int TickAt(int shownTick) => FollowsEdit ? MappedTick(_warp, shownTick, LastTick) : OwnTick;

    /// <summary>The reference's tick that plays at the shown clip's tick <paramref name="shownTick"/> under <paramref name="warp"/>
    /// (null: the same tick), rounded half away from zero, within 0 and <paramref name="lastTick"/>.</summary>
    public static int MappedTick(TimeWarp? warp, int shownTick, int lastTick) =>
        Math.Clamp(warp is null ? shownTick : (int)Math.Round(warp.SourceOf(shownTick), MidpointRounding.AwayFromZero), 0, Math.Max(0, lastTick));

    /// <summary>Puts the reference at the same time from its start as the shown clip's <paramref name="shownSeconds"/>: that time, modulo
    /// its own length when past it.</summary>
    public void Seek(float shownSeconds) => Time = Wrap(shownSeconds);

    /// <summary>The time within the reference's length at <paramref name="seconds"/> from its start: as it is up to its length (its end
    /// included), else modulo the length, a whole number of lengths giving its end (so a scrub or step to the end of a result some
    /// lengths of the original long shows the original's last tick, as the result stopping there with Loop off does); 0 for no length or
    /// a time that isn't a number of 0 or more.</summary>
    public float Wrap(double seconds)
    {
        if (Duration <= 0 || !double.IsFinite(seconds) || seconds <= 0) return 0;
        if (seconds <= Duration) return (float)seconds;
        double t = seconds % Duration;
        return (float)(t < EndTolerance || Duration - t < EndTolerance ? Duration : t);
    }

    /// <summary>Lets <paramref name="realSeconds"/> of real time go by on <paramref name="clock"/> and on the reference: the reference moves
    /// only while the clock plays, by the same time (times the clock's speed), wrapping at its own length (as the clock wraps at the shown
    /// clip's, with the same arithmetic, so two clips of one length stay on the same tick). Returns whether either time changed.</summary>
    public bool Advance(PlaybackClock clock, double realSeconds)
    {
        bool playing = clock.Playing;
        float clockBefore = clock.Time;
        bool moved = clock.Advance(realSeconds);
        if (!playing || Duration <= 0 || !(realSeconds > 0)) return moved;
        float before = Time;
        double t;
        if (clock.Playing)
        {
            t = Time + realSeconds * clock.Speed;
            if (t >= Duration) t %= Duration;
        }
        else
        {
            // the shown clip stopped at its end (Loop off): the reference moves only as far as the clock did, and one that reaches its own
            // end with it (two clips of one length) rests there, on its last tick, never wrapped to its start
            // (at its start within a float's rounding is its end too: a whole number of its lengths played, the last wrap a frame early)
            t = Time + ((double)clock.Time - clockBefore);
            if (t > Duration) t %= Duration;
            if (Duration - t < EndTolerance || t < EndTolerance) t = Duration;
        }
        Time = (float)t;
        return moved || Time != before;
    }

    /// <summary>How near its end (seconds) a reference stopping with the clock counts as at it: a float's rounding, far under a tick.</summary>
    const double EndTolerance = 1e-4;

    /// <summary>Plays or pauses <paramref name="clock"/>; a play from the end of a clip that doesn't loop starts the clock again at 0, and
    /// the reference with it.</summary>
    public void Toggle(PlaybackClock clock)
    {
        float before = clock.Time;
        clock.Toggle();
        if (clock.Time != before) Seek(clock.Time);
    }

    /// <summary>A scrub of the shown clip's timeline: the clock paused at <paramref name="seconds"/>, the reference at the same time from
    /// its start (<see cref="Seek(float)"/>).</summary>
    public void Seek(PlaybackClock clock, float seconds)
    {
        clock.Pause();
        clock.Seek(seconds);
        Seek(clock.Time);
    }

    /// <summary>A step of the shown clip's timeline by <paramref name="ticks"/> (paused), the reference following it as a scrub does.</summary>
    public void Step(PlaybackClock clock, int ticks)
    {
        clock.Step(ticks);
        Seek(clock.Time);
    }
}
