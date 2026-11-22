using System.Globalization;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.Core.Edits;

/// <summary>An edited clip written as a record file named from the install's own datafile, by <see cref="RecordFileWriter"/>, which refuses
/// the game's folders, and the edit's account for the console: what was done, the lengths before and after, the graph's warnings, what
/// the edit kept and measured, and the arena kinds.</summary>
public static class EditPackage
{
    /// <summary>The edits by name, as the command line takes them.</summary>
    public static readonly string[] Edits = ["mirror", "scale", "speed", "trim", "swap"];

    /// <summary>Runs the edit named <paramref name="edit"/> on <paramref name="clip"/>: scale and speed take <paramref name="factor"/>, trim
    /// <paramref name="from"/> and <paramref name="to"/> (the clip's ticks), swap <paramref name="source"/>. Gives the result and a few words
    /// for the account; refuses with an <see cref="EditRefusedException"/>, and throws an <see cref="ArgumentException"/> for an unknown edit or
    /// a missing parameter. A factor is taken to three decimals, as the screen's own boxes take it, so the edit made is the one its words
    /// name.</summary>
    public static EditedClip Run(ClipRecord clip, EditContext ctx, string edit, out string description, double? factor = null, int? from = null, int? to = null, ClipRecord? source = null)
    {
        double F() => ClipChange.RoundFactor(factor ?? throw new ArgumentException($"the {edit} edit needs a factor"));
        switch (edit)
        {
            case "mirror": description = ClipChange.Mirror().Words; return MirrorEdit.Apply(clip, ctx);
            case "scale": description = ClipChange.Scale(F()).Words; return RootScaleEdit.Apply(clip, ctx, (float)F());
            case "speed": description = ClipChange.Speed(F()).Words; return Retiming.RetimeEdit.Speed(clip, ctx, F());
            case "trim":
                {
                    int a = from ?? throw new ArgumentException("the trim edit needs a first tick"), b = to ?? throw new ArgumentException("the trim edit needs a last tick");
                    description = ClipChange.Trim(a, b).Detail;
                    return Retiming.RetimeEdit.Trim(clip, ctx, a, b);
                }
            case "swap":
                {
                    var s = source ?? throw new ArgumentException("the swap edit needs a source clip");
                    description = ClipChange.Swap(s.Name).Words;
                    return SwapEdit.Apply(clip, s, ctx);
                }
            default: throw new ArgumentException($"the edit is one of {string.Join(", ", Edits)}, not '{edit}'");
        }
    }

    /// <summary>A scale or speed factor as the notes and accounts show it: the one factor wording (<see cref="ClipChange.FactorNumber"/>),
    /// so a factor read as a float and widened (0.8 is 0.800000011920929 as a double) reads as it was typed, "0.8".</summary>
    public static string FactorText(double factor) => ClipChange.FactorNumber(factor);

    /// <summary>The edit's account, as the command line prints it, each line marked by what it is (the edit, a warning, a note, an arena
    /// kind).</summary>
    public static List<string> AccountLines(ClipEntry clip, EditedClip result, string description, string forgeName)
    {
        var before = clip.Record;
        var l = new List<string>
        {
            $"edit: {clip.Name} ({description})",
            "edit: " + string.Create(CultureInfo.InvariantCulture,
                $"{(before is null ? "?" : before.Duration.ToString("F4", CultureInfo.InvariantCulture))} s ({before?.LastTick} ticks) before, {result.Record.Duration:F4} s ({result.Record.LastTick} ticks) after"),
            $"edit: built from {(clip.Status == ClipStatus.Vanilla ? "the game's own clip" : $"the game folder's clip (status {clip.Status})")} in {forgeName}",
        };
        if (result.Warnings.Count == 0) l.Add("warning: none from the animation graph or the edit");
        l.AddRange(result.Warnings.Select(w => "warning: " + w));
        l.AddRange(result.Notes.Select(n => "done or kept: " + n));
        l.AddRange(result.Rebuilt.Arenas.Select(a => $"arena kind {a.Kind} (a head entry the game sizes curve memory by): {(a.Present ? $"{a.Before} -> {a.After}" : "not in the head")}"));
        // a scale's slide is in its own notes when it was measured; only one that wasn't measured is said here
        if (result is ScaledClip sc && double.IsNaN(sc.ResidualSlideCm))
            l.Add("planted feet: the slide wasn't measured on this record");
        return l;
    }

    /// <summary>Writes <paramref name="result"/>, the edit of <paramref name="clip"/>, into <paramref name="outputFolder"/> as its record
    /// file, guarded and whole or not at all.</summary>
    public static RecordPackage Write(GameInstall install, string outputFolder, ClipEntry clip, EditedClip result, bool overwrite = false) =>
        RecordFileWriter.Write(install, outputFolder, [(clip, result.Bytes)], overwrite);
}
