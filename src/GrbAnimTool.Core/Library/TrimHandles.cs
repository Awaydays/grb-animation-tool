namespace GrbAnimTool.Core.Library;

/// <summary>The trim's two handles on a strip that spans the source clip, tick 0 at the left edge and its last tick at the right: where a
/// handle stands for a tick, which tick a place on the strip is, and a drag that keeps the cut a trim can make (the start before the end by
/// at least one tick, both within the clip).</summary>
public readonly record struct TrimHandles(int LastTick, int From, int To)
{
    /// <summary>The handles at the clip's ends: nothing cut.</summary>
    public static TrimHandles Whole(int lastTick) => new(Math.Max(lastTick, 1), 0, Math.Max(lastTick, 1));

    /// <summary>The handles at the given ticks, each clamped into the clip and kept a tick apart (the end moves when they meet).</summary>
    public static TrimHandles At(int lastTick, int from, int to)
    {
        int last = Math.Max(lastTick, 1);
        int f = Math.Clamp(from, 0, last - 1);
        int t = Math.Clamp(to, f + 1, last);
        return new TrimHandles(last, f, t);
    }

    /// <summary>Whether anything is cut.</summary>
    public bool Cuts => From > 0 || To < LastTick;

    /// <summary>The ticks the trim keeps, its new last tick.</summary>
    public int Kept => To - From;

    /// <summary>The tick a place <paramref name="x"/> on a strip <paramref name="width"/> wide stands for, rounded to the nearest and
    /// clamped to the clip.</summary>
    public static int TickAt(double x, double width, int lastTick)
    {
        if (!(width > 0) || !double.IsFinite(x)) return 0;
        return (int)Math.Clamp(Math.Round(x / width * lastTick, MidpointRounding.AwayFromZero), 0, lastTick);
    }

    /// <summary>Where tick <paramref name="tick"/> stands on a strip <paramref name="width"/> wide.</summary>
    public static double XOf(int tick, double width, int lastTick) => lastTick > 0 && width > 0 ? Math.Clamp((double)tick / lastTick, 0, 1) * width : 0;

    /// <summary>The start handle dragged to place <paramref name="x"/>: it stays at least a tick before the end.</summary>
    public TrimHandles DragFrom(double x, double width) => this with { From = Math.Clamp(TickAt(x, width, LastTick), 0, To - 1) };

    /// <summary>The end handle dragged to place <paramref name="x"/>: it stays at least a tick after the start.</summary>
    public TrimHandles DragTo(double x, double width) => this with { To = Math.Clamp(TickAt(x, width, LastTick), From + 1, LastTick) };

    /// <summary>The cut in words: the ticks and seconds kept.</summary>
    public string Describe() => FormattableString.Invariant(
        $"ticks {From} to {To} ({From / 60.0:F3} s to {To / 60.0:F3} s): {Kept} ticks ({Kept / 60.0:F3} s) kept of {LastTick}");
}
