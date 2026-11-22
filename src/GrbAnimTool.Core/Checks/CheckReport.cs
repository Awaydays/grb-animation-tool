using System.Text;

namespace GrbAnimTool.Core.Checks;

/// <summary>How a check came out: it passed, it found something worth a look (the result is still usable), or it failed.</summary>
public enum CheckLevel { Pass, Warn, Fail }

/// <summary>One check's outcome: its name, its level, a plain message, and where the worst of it fell (the tick and the bone), when that
/// means something for the check.</summary>
public sealed record CheckResult(string Check, CheckLevel Level, string Message, int? Tick = null, string? Bone = null)
{
    public static string Word(CheckLevel l) => l switch { CheckLevel.Pass => "PASS", CheckLevel.Warn => "WARN", _ => "FAIL" };

    /// <summary>The result as one line: "PASS  name: message (worst at tick t, bone b)".</summary>
    public string Line
    {
        get
        {
            string where = Tick is null && Bone is null ? ""
                : $" (worst at {(Tick is int t ? $"tick {t}" : "")}{(Tick is not null && Bone is not null ? ", " : "")}{(Bone is not null ? Bone : "")})";
            return $"{Word(Level),-4}  {Check}: {Message}{where}";
        }
    }

    public override string ToString() => Line;
}

/// <summary>The checks run on one motion, in order, and their overall verdict: the worst level among them.</summary>
public sealed class CheckReport
{
    readonly List<CheckResult> _results = [];

    public IReadOnlyList<CheckResult> Results => _results;

    public CheckReport Add(CheckResult r) { _results.Add(r); return this; }

    public CheckReport AddRange(IEnumerable<CheckResult> rs) { _results.AddRange(rs); return this; }

    /// <summary>The worst level among the results; <see cref="CheckLevel.Pass"/> for none.</summary>
    public CheckLevel Verdict => _results.Count == 0 ? CheckLevel.Pass : _results.Max(r => r.Level);

    public int Count(CheckLevel l) => _results.Count(r => r.Level == l);

    public IEnumerable<string> Lines() => _results.Select(r => r.Line);

    /// <summary>The report as text: an optional heading, every line, then the verdict with the counts.</summary>
    public string ToText(string? heading = null)
    {
        var sb = new StringBuilder();
        if (heading is not null) sb.AppendLine(heading);
        foreach (var l in Lines()) sb.AppendLine(l);
        sb.AppendLine($"VERDICT: {CheckResult.Word(Verdict)} ({Count(CheckLevel.Pass)} pass, {Count(CheckLevel.Warn)} warn, {Count(CheckLevel.Fail)} fail)");
        return sb.ToString();
    }
}
