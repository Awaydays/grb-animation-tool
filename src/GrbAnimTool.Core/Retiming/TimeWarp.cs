namespace GrbAnimTool.Core.Retiming;

/// <summary>A re-timing of a clip: for every tick of the new clip, the source clip's time (in ticks, fractional) that plays there, rising
/// and never falling. Its inverse takes a source time to the new clip's: by the first new tick whose source time reaches it, and as a
/// fractional time between the two new ticks around it. A trim is a shift (the cut's start plays at tick 0), a speed change a scale.</summary>
public sealed class TimeWarp
{
    readonly double[] _src;

    /// <summary>The source clip's last tick, and the new clip's.</summary>
    public int SourceLast { get; }
    public int NewLast => _src.Length - 1;

    /// <summary>Whether every new tick plays the source's same tick (nothing changes).</summary>
    public bool IsIdentity { get; }

    /// <summary>What the warp is, in words.</summary>
    public string Describe { get; }

    TimeWarp(double[] src, int sourceLast, string describe)
    {
        for (int i = 1; i < src.Length; i++)
            if (!(src[i] >= src[i - 1])) throw new ArgumentException($"a time warp must not fall: tick {i} plays {src[i]} after {src[i - 1]}");
        _src = src; SourceLast = sourceLast; Describe = describe;
        IsIdentity = src.Length == sourceLast + 1 && src.Select((x, i) => x == i).All(b => b);
    }

    /// <summary>A warp from a list: the source time of each new tick (at least one tick, rising).</summary>
    public static TimeWarp FromList(IReadOnlyList<double> sourceTimes, int sourceLast, string describe = "a time warp")
    {
        if (sourceTimes.Count < 2) throw new ArgumentException("a time warp needs at least two ticks");
        return new TimeWarp([.. sourceTimes], sourceLast, describe);
    }

    /// <summary>A trim: the source's ticks <paramref name="from"/> to <paramref name="to"/>, played from tick 0.</summary>
    public static TimeWarp Trim(int sourceLast, int from, int to)
    {
        if (from < 0 || to > sourceLast || to - from < 1) throw new ArgumentException($"a trim from tick {from} to {to} doesn't fit a clip of {sourceLast} ticks with at least one tick left");
        var s = new double[to - from + 1];
        for (int i = 0; i < s.Length; i++) s[i] = from + i;
        // the edit's account (shown on the Edit screen and the command line) in the tool's one wording, with the exact ticks
        return new TimeWarp(s, sourceLast, Library.ClipChange.Trim(from, to).Detail);
    }

    /// <summary>A speed change: the clip played <paramref name="factor"/> times as fast, its last tick round(last / factor), the source's
    /// last tick at the new last tick.</summary>
    public static TimeWarp Speed(int sourceLast, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentException($"a speed factor of {factor}");
        int n = (int)Math.Round(sourceLast / factor);
        if (n < 1) throw new ArgumentException($"{sourceLast} ticks at {factor} times the speed leave no tick");
        var s = new double[n + 1];
        for (int i = 0; i <= n; i++) s[i] = i == n ? sourceLast : (double)i * sourceLast / n;
        return new TimeWarp(s, sourceLast, $"{Library.ClipChange.Speed(factor).Words} ({sourceLast} ticks to {n})");
    }

    /// <summary>The source time that plays at new tick <paramref name="t"/> (clamped to the new clip).</summary>
    public double SourceOf(int t) => _src[Math.Clamp(t, 0, _src.Length - 1)];

    /// <summary>The first new tick whose source time reaches <paramref name="x"/>; -1 when <paramref name="x"/> is before the new clip's
    /// first source time by more than a hair and <paramref name="clampBefore"/> is false; the last tick when no tick reaches it.</summary>
    public int FirstReaching(double x, bool clampBefore = true)
    {
        if (x < _src[0] - 1e-6 && !clampBefore) return -1;
        int lo = 0, hi = _src.Length - 1, r = _src.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_src[mid] >= x - 1e-6) { r = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return r;
    }

    /// <summary>The inverse: the new clip's time (fractional ticks) at which the source time <paramref name="x"/> plays, by straight lines
    /// between the new ticks around it. Before the first source time it is below 0 and past the last above <see cref="NewLast"/>, by the
    /// warp's first and last steps, so a caller can see what a trim cuts off.</summary>
    public double Inverse(double x)
    {
        int n = _src.Length - 1;
        if (x <= _src[0]) return _src[1] > _src[0] ? (x - _src[0]) / (_src[1] - _src[0]) : 0;
        if (x >= _src[n]) return _src[n] > _src[n - 1] ? n + (x - _src[n]) / (_src[n] - _src[n - 1]) : n;
        int k = FirstReaching(x);   // _src[k] >= x > _src[k - 1]
        if (k == 0) return 0;
        double a = _src[k - 1], b = _src[k];
        return b > a ? k - 1 + (x - a) / (b - a) : k;
    }

    /// <summary>Whether the source time <paramref name="x"/> plays inside the new clip.</summary>
    public bool Covers(double x) => x >= _src[0] - 1e-6 && x <= _src[^1] + 1e-6;
}
