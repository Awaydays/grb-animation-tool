using System.Globalization;
using System.Text.RegularExpressions;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Library;

/// <summary>What changed a clip: one of the five edits, an import of a .glb, or (<see cref="Text"/>) an edit an earlier version kept in
/// words this one can't read back.</summary>
public enum ChangeKind { Mirror, Scale, Speed, Trim, Swap, Import, Text, Replace }

/// <summary>What was changed in a clip, and its one wording (<see cref="Words"/>): the kind; the factor (root-motion scale and speed); the
/// cut (trim: the original clip's ticks, and its ticks a second); the source clip's name (swap); the .glb's path (import); or the text an
/// earlier version kept that can't be read back (<see cref="ChangeKind.Text"/>), shown as it was kept.</summary>
public sealed record ClipChange
{
    public required ChangeKind Kind { get; init; }
    public double Factor { get; init; } = 1;
    public int From { get; init; }
    public int To { get; init; }
    public int TicksPerSecond { get; init; } = ClipRecord.TicksPerSecond;
    public string? SourceClip { get; init; }
    public string? GlbPath { get; init; }
    public string? Text { get; init; }

    /// <summary>Whether an edit made the clip (an edit kept in words included), not an import.</summary>
    public bool IsEdit => Kind != ChangeKind.Import;

    public static ClipChange Mirror() => new() { Kind = ChangeKind.Mirror };
    public static ClipChange Scale(double factor) => new() { Kind = ChangeKind.Scale, Factor = factor };
    public static ClipChange Speed(double factor) => new() { Kind = ChangeKind.Speed, Factor = factor };
    public static ClipChange Trim(int from, int to) => new() { Kind = ChangeKind.Trim, From = from, To = to };
    public static ClipChange Swap(string? sourceClip) => new() { Kind = ChangeKind.Swap, SourceClip = sourceClip };
    public static ClipChange Imported(string glbPath) => new() { Kind = ChangeKind.Import, GlbPath = glbPath };
    /// <summary>A replace: <paramref name="what"/> says the source and the fit ("motion from mca_emote_Hello, cut from 1.2 s").</summary>
    public static ClipChange Replaced(string what) => new() { Kind = ChangeKind.Replace, Text = what };

    /// <summary>The change an edit request makes.</summary>
    public static ClipChange Of(EditRequest r) => r.Kind switch
    {
        EditKind.Mirror => Mirror(),
        EditKind.Scale => Scale(r.Factor),
        EditKind.Speed => Speed(r.Factor),
        EditKind.Trim => Trim(r.From, r.To),
        _ => Swap(r.Source?.Name),
    };

    /// <summary>The change in the tool's one wording, short and parallel: <c>Mirrored</c>, <c>Root-motion scale 0.5x</c>, <c>Speed 1.25x</c>,
    /// <c>Trim 0.9 s to 7.2 s</c> (the cut in seconds of the original clip), <c>Swap: motion from mca_emote_Shotgun</c>,
    /// <c>Imported: walk.glb</c> (the file's name only); a kept text as it was kept.</summary>
    public string Words => Kind switch
    {
        ChangeKind.Mirror => "Mirrored",
        ChangeKind.Scale => $"Root-motion scale {FactorText(Factor)}",
        ChangeKind.Speed => $"Speed {FactorText(Factor)}",
        ChangeKind.Trim => $"Trim {SecondsText(From, TicksPerSecond)} s to {SecondsText(To, TicksPerSecond)} s",
        ChangeKind.Swap => $"Swap: motion from {SourceClip ?? "another clip"}",
        ChangeKind.Import => $"Imported: {Path.GetFileName(GlbPath ?? "")}",
        ChangeKind.Replace => $"Replaced: {Text}",
        _ => Text ?? "",
    };

    /// <summary>The words with what they leave out, for a tooltip: a trim's exact ticks of the original clip.</summary>
    public string Detail => Kind == ChangeKind.Trim
        ? string.Create(CultureInfo.InvariantCulture, $"{Words}, ticks {From} to {To} of the original clip")
        : Words;

    /// <summary>A factor as the wording gives it: invariant culture, up to three decimals, no trailing zeros, then "x" (1.25x, 0.5x, 2x).</summary>
    public static string FactorText(double factor) => FactorNumber(factor) + "x";

    /// <summary>A factor's number, as every place that gives a factor writes it: invariant culture, up to three decimals, no trailing
    /// zeros (a float widened, 0.800000011920929, reads 0.8).</summary>
    public static string FactorNumber(double factor) => factor.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A factor as an edit takes it, on the screen and the command line alike: to three decimals, a half away from zero.</summary>
    public static double RoundFactor(double factor) => Math.Round(factor, 3, MidpointRounding.AwayFromZero);

    /// <summary>A tick in seconds at <paramref name="ticksPerSecond"/>: invariant culture, up to two decimals, no trailing zeros.</summary>
    public static string SecondsText(int tick, int ticksPerSecond = ClipRecord.TicksPerSecond) =>
        (tick / (double)ticksPerSecond).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The prefix the kept list's format 2 put before an edit's words; any other kept text is an import's .glb path.</summary>
    public const string KeptEditPrefix = "edit: ";

    /// <summary>The change a kept list of format 2 holds as text: an edit's words after <see cref="KeptEditPrefix"/>
    /// (<c>mirrored</c>, <c>root motion scaled by 0.5</c>, <c>played at 1.25 times the speed</c>, <c>trimmed to its ticks 54 to 484</c>,
    /// <c>mca_emote_Shotgun's motion swapped in</c>), read back exactly; else an import's .glb path. An edit's words that aren't exactly one
    /// of those are kept as they are (<see cref="ChangeKind.Text"/>), never guessed at.</summary>
    public static ClipChange FromKeptText(string text)
    {
        if (!text.StartsWith(KeptEditPrefix, StringComparison.Ordinal)) return Imported(text);
        string w = text[KeptEditPrefix.Length..];
        if (w == "mirrored") return Mirror();
        if (Regex.Match(w, @"^root motion scaled by ([0-9]+(?:\.[0-9]+)?)$") is { Success: true } s && Positive(s.Groups[1].Value) is { } sf) return Scale(sf);
        if (Regex.Match(w, @"^played at ([0-9]+(?:\.[0-9]+)?) times the speed$") is { Success: true } p && Positive(p.Groups[1].Value) is { } pf) return Speed(pf);
        if (Regex.Match(w, @"^trimmed to its ticks ([0-9]{1,9}) to ([0-9]{1,9})$") is { Success: true } t
            && int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) is var from && int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture) is var to && from < to)
            return Trim(from, to);
        // a clip's name holds no space or quote: "another clip's motion swapped in" (no source known) stays as kept
        if (Regex.Match(w, @"^([^\s']+)'s motion swapped in$") is { Success: true } sw) return Swap(sw.Groups[1].Value);
        return new() { Kind = ChangeKind.Text, Text = text };
    }

    static double? Positive(string s) => double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double f) && f > 0 && double.IsFinite(f) ? f : null;
}
