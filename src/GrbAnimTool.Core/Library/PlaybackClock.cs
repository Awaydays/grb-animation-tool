using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Library;

/// <summary>The play head of a clip viewer: the time in seconds within [0, duration], play and pause, loop, speed, and stepping by ticks of
/// 1/60 s. It holds no clock of its own: the viewer passes the real time that went by to <see cref="Advance"/>.</summary>
public sealed class PlaybackClock
{
    /// <summary>The speeds a viewer offers.</summary>
    public static readonly IReadOnlyList<float> Speeds = [0.25f, 0.5f, 1f, 2f];

    float _speed = 1;

    public float Duration { get; private set; }
    /// <summary>The clip's last tick (duration x 60, rounded): the play head's ticks run from 0 to it.</summary>
    public int LastTick { get; private set; }
    public float Time { get; private set; }
    public bool Playing { get; private set; }
    public bool Loop { get; set; } = true;

    /// <summary>The play speed, a factor on real time; it must be above zero.</summary>
    public float Speed
    {
        get => _speed;
        set => _speed = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), "the speed must be above zero");
    }

    /// <summary>The play head's tick: the nearest tick to <see cref="Time"/>.</summary>
    public int Tick => TickOf(Time);

    public static int TickOf(float seconds) => (int)MathF.Round(seconds * ClipRecord.TicksPerSecond);
    public static float SecondsOf(int tick) => (float)tick / ClipRecord.TicksPerSecond;

    /// <summary>Starts on a new clip: paused at 0. A duration a clip record may not state (not finite, 0 or less, over
    /// <see cref="ClipRecord.MaxDuration"/>) gives a clip of no length, which never plays.</summary>
    public void Reset(float duration, int lastTick)
    {
        bool ok = float.IsFinite(duration) && duration > 0 && duration <= ClipRecord.MaxDuration;
        Duration = ok ? duration : 0;
        LastTick = ok ? Math.Clamp(lastTick, 0, TickOf(Duration)) : 0;
        Time = 0;
        Playing = false;
    }

    /// <summary>Puts the play head at <paramref name="seconds"/>, clamped to the clip.</summary>
    public void Seek(float seconds) => Time = float.IsFinite(seconds) ? Math.Clamp(seconds, 0, Duration) : 0;

    /// <summary>Plays from the play head; at the end of a clip that doesn't loop, from the start again.</summary>
    public void Play()
    {
        if (Duration <= 0) return;
        if (!Loop && Time >= Duration) Time = 0;
        Playing = true;
    }

    public void Pause() => Playing = false;

    public void Toggle()
    {
        if (Playing) Pause(); else Play();
    }

    /// <summary>Moves the play head by <paramref name="ticks"/> from its nearest tick and pauses. The ticks stay within 0 and the last
    /// tick, and the time within the duration.</summary>
    public void Step(int ticks)
    {
        Playing = false;
        int t = Math.Clamp(Tick + ticks, 0, LastTick);
        Time = Math.Clamp(SecondsOf(t), 0, Duration);
    }

    /// <summary>Lets <paramref name="realSeconds"/> of real time go by. While playing, the play head moves by it times the speed; past
    /// the end it wraps when looping, else it stops at the end. Returns whether the time changed.</summary>
    public bool Advance(double realSeconds)
    {
        if (!Playing || Duration <= 0 || !(realSeconds > 0)) return false;
        float before = Time;
        double t = Time + realSeconds * _speed;
        if (t >= Duration)
        {
            if (Loop) t %= Duration;
            else { t = Duration; Playing = false; }
        }
        Time = (float)t;
        return Time != before;
    }
}
