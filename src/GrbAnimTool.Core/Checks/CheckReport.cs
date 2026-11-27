using System.Text;

namespace GrbAnimTool.Core.Checks;

/// <summary>How a check came out: it passed, it found something worth a look (the result is still usable), or it failed. For a look
/// check (<see cref="CheckKind.Look"/>) the same three steps are said "OK", "Note" and "Big difference", and none of them is a failure.</summary>
public enum CheckLevel { Pass, Warn, Fail }

/// <summary>What a check is about. <see cref="Game"/>: whether the game can play the clip correctly (its length, its structure, its tracks,
/// what the animation graph needs); a FAIL there stops it being added to a package. <see cref="Look"/>: how different it looks from the
/// game's own clip (floating, joint angles, where the hands and feet are, how fast it turns); the game still plays it, so a look check
/// never fails and never stops anything.</summary>
public enum CheckKind { Game, Look }

/// <summary>One check's outcome: its name, its level, a plain message, and where the worst of it fell (the tick and the bone), when that
/// means something for the check. Its kind follows from its name (<see cref="KindOf"/>).</summary>
public sealed record CheckResult(string Check, CheckLevel Level, string Message, int? Tick = null, string? Bone = null)
{
    /// <summary>The look checks by name: each compares the look with the game's own clip. Every other check is a game check.</summary>
    public static readonly IReadOnlySet<string> LookChecks = new HashSet<string>(StringComparer.Ordinal)
    {
        "feet in the floor", "feet off the floor", "joint limits", "speed", "the mover's turn", "twists", "pops", "limbs",
        "hands on a clip that aims them", "feet during the plants", Feet.SlideCheck,
    };

    public static CheckKind KindOf(string check) => LookChecks.Contains(check) ? CheckKind.Look : CheckKind.Game;

    public CheckKind Kind => KindOf(Check);

    /// <summary>A game check's word: PASS, WARN, FAIL.</summary>
    public static string Word(CheckLevel l) => l switch { CheckLevel.Pass => "PASS", CheckLevel.Warn => "WARN", _ => "FAIL" };

    /// <summary>A check's word by its kind: a game check's PASS, WARN, FAIL; a look check's OK, Note, Big difference.</summary>
    public static string Word(CheckLevel l, CheckKind k) => k == CheckKind.Game ? Word(l)
        : l switch { CheckLevel.Pass => "OK", CheckLevel.Warn => "Note", _ => "Big difference" };

    /// <summary>This result's word (<see cref="Word(CheckLevel, CheckKind)"/>).</summary>
    public string Said => Word(Level, Kind);

    /// <summary>Every word a report line starts with, the longest first, and the level it stands for.</summary>
    public static readonly IReadOnlyList<(string Word, CheckLevel Level)> Words =
        [("Big difference", CheckLevel.Fail), ("PASS", CheckLevel.Pass), ("WARN", CheckLevel.Warn), ("FAIL", CheckLevel.Fail), ("Note", CheckLevel.Warn), ("OK", CheckLevel.Pass)];

    /// <summary>The level as the screens colour it: a look check's big difference as a warning, its note and OK as a pass, since the
    /// game still plays it.</summary>
    public CheckLevel Shown => Kind == CheckKind.Game ? Level : Level == CheckLevel.Fail ? CheckLevel.Warn : CheckLevel.Pass;

    /// <summary>The order the screens list results in, the first first: a game FAIL, a game WARN, a big difference, a note, then the rest.</summary>
    public int Rank => (Kind, Level) switch
    {
        (CheckKind.Game, CheckLevel.Fail) => 4, (CheckKind.Game, CheckLevel.Warn) => 3, (CheckKind.Look, CheckLevel.Fail) => 2, (CheckKind.Look, CheckLevel.Warn) => 1, _ => 0,
    };

    /// <summary>The result as one line: "PASS  name: message (worst at tick t, bone b)", a look check with its own word ("Note  name: ...").</summary>
    public string Line
    {
        get
        {
            string where = Tick is null && Bone is null ? ""
                : $" (worst at {(Tick is int t ? $"tick {t}" : "")}{(Tick is not null && Bone is not null ? ", " : "")}{(Bone is not null ? Bone : "")})";
            return $"{Said,-4}  {Check}: {Message}{where}";
        }
    }

    public override string ToString() => Line;
}

/// <summary>The checks run on one motion, in order, and their overall verdict: the worst level among the game checks. The look checks are
/// counted apart (<see cref="Headline"/>) and never make the verdict.</summary>
public sealed class CheckReport
{
    readonly List<CheckResult> _results = [];

    public IReadOnlyList<CheckResult> Results => _results;

    public CheckReport Add(CheckResult r) { _results.Add(r); return this; }

    public CheckReport AddRange(IEnumerable<CheckResult> rs) { _results.AddRange(rs); return this; }

    /// <summary>Puts <paramref name="r"/> in place of the result of the same check, or adds it when there is none.</summary>
    public CheckReport Set(CheckResult r)
    {
        int i = _results.FindIndex(x => x.Check == r.Check);
        if (i >= 0) _results[i] = r; else _results.Add(r);
        return this;
    }

    /// <summary>The worst level among the game checks; <see cref="CheckLevel.Pass"/> for none. A look check never counts here.</summary>
    public CheckLevel Verdict => VerdictOf(_results);

    /// <summary>The worst level among <paramref name="results"/>' game checks.</summary>
    public static CheckLevel VerdictOf(IEnumerable<CheckResult> results) =>
        results.Where(r => r.Kind == CheckKind.Game).Select(r => r.Level).DefaultIfEmpty(CheckLevel.Pass).Max();

    /// <summary>How many game checks came out at <paramref name="l"/>.</summary>
    public int Count(CheckLevel l) => Count(l, CheckKind.Game);

    public int Count(CheckLevel l, CheckKind k) => _results.Count(r => r.Level == l && r.Kind == k);

    /// <summary>The two kinds counted apart, as the screens head the checks: "Game: 12 pass, 0 warn, 0 fail. Look: 2 big differences, 2 notes."</summary>
    public string Headline => HeadlineOf(_results);

    public static string HeadlineOf(IEnumerable<CheckResult> results)
    {
        var l = results.ToList();
        int N(CheckKind k, CheckLevel v) => l.Count(r => r.Kind == k && r.Level == v);
        int big = N(CheckKind.Look, CheckLevel.Fail), notes = N(CheckKind.Look, CheckLevel.Warn);
        return $"Game: {N(CheckKind.Game, CheckLevel.Pass)} pass, {N(CheckKind.Game, CheckLevel.Warn)} warn, {N(CheckKind.Game, CheckLevel.Fail)} fail. "
            + $"Look: {big} big difference{(big == 1 ? "" : "s")}, {notes} note{(notes == 1 ? "" : "s")}.";
    }

    public IEnumerable<string> Lines() => _results.Select(r => r.Line);

    /// <summary>The report as text: an optional heading, the game checks' lines, the look checks' lines, then the verdict with the
    /// headline's counts.</summary>
    public string ToText(string? heading = null)
    {
        var sb = new StringBuilder();
        if (heading is not null) sb.AppendLine(heading);
        foreach (var k in new[] { CheckKind.Game, CheckKind.Look })
        {
            var rs = _results.Where(r => r.Kind == k).ToList();
            if (rs.Count == 0) continue;
            sb.AppendLine(k == CheckKind.Game ? "Game checks (whether the game can play it):" : "Look checks (how different it looks from the game's own clip; the game still plays it):");
            foreach (var r in rs) sb.AppendLine(r.Line);
        }
        sb.AppendLine($"VERDICT: {CheckResult.Word(Verdict)} ({Headline})");
        return sb.ToString();
    }
}
