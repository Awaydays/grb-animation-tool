using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.Core.Package;

/// <summary>Where a package item came from: an import of a .glb, or an edit of the game's clip.</summary>
public enum PackageItemKind { Import, Edit }

/// <summary>What the Package screen reads from the items kept in <see cref="PackageList"/>: what changed each clip
/// (<see cref="PackageItem.Change"/>, an edit or an import), and the warnings, the report's warning and failure lines.</summary>
public static class PackageEntries
{
    /// <summary>The item to keep for an edit's result: the clip's slot, the edited record, the edit, the checks' verdict and report.</summary>
    public static PackageItem ForEdit(EditOutcome o) =>
        new(o.Clip, o.Result.Bytes, o.Change, o.Report.Verdict, o.ReportText(), DateTime.Now);

    public static PackageItemKind Kind(PackageItem item) => item.Change.IsEdit ? PackageItemKind.Edit : PackageItemKind.Import;

    /// <summary>What was changed in the clip, in the tool's one wording (<see cref="ClipChange.Words"/>): "Speed 1.25x", "Imported: walk.glb".</summary>
    public static string What(PackageItem item) => item.Change.Words;

    /// <summary>Why a result is no change: its bytes are the game's own record of the clip.</summary>
    public const string GamesOwnUnchanged = "this is the game's own clip unchanged";

    /// <summary>Why a kept import is no change when packaged: its bytes are the record the open install already holds for the clip. That
    /// record needn't be the game's own: it may be this package's own clip, from an earlier build installed in the game, which the tool
    /// can't tell from what's installed; so the way out is game files that don't hold it, or the earlier package folder kept as it is.</summary>
    public const string InstallHoldsIt = "your game folder already holds this clip exactly as it is here (perhaps from an earlier build of this package installed in the game), "
        + "so the tool can't tell it from what's installed; build from game files that don't hold it (verify the game files or put your forge backup back first), "
        + "or keep the earlier package folder as it is";

    /// <summary>Why an import's result is no change: as <see cref="InstallHoldsIt"/>, with the import's own way out, an import on game
    /// files that don't hold the clip.</summary>
    public const string ImportInstallHoldsIt = "your game folder already holds this clip exactly as it is here (perhaps your own package's clip, installed in the game from an "
        + "earlier build), so the tool can't tell it from what's installed; import it on game files that don't hold it (verify the game files or put your forge backup back first)";

    /// <summary>Why an edit's result is no change: its bytes are the record the open install already holds for the clip. An edit is made
    /// from that record, so the edit changed nothing in it; other game files wouldn't change that.</summary>
    public const string EditChangesNothing = "this edit changes nothing: the result is the clip as your game folder already holds it";

    /// <summary>What <see cref="Unchanged(GameInstall, ClipEntry, ReadOnlySpan{byte}, PackageItemKind)"/> says before an import's
    /// install-held reason when the table of the game's clips knows the clip and says the result isn't its record (status Replaced), so
    /// it is never read as <see cref="GamesOwnUnchanged"/>. Not said for a clip the table doesn't know: there the tool can't tell.</summary>
    public const string NotTheGamesOwn = "this isn't the game's own clip unchanged, but ";

    /// <summary>Why <paramref name="record"/>, made for <paramref name="slot"/>'s clip by an import or an edit (<paramref name="kind"/>)
    /// just now, is no change, or null when it is one: its bytes are the game's own record of that clip (by the table of the game's clips
    /// built into the tool, so a modded install doesn't hide it, <see cref="GamesOwnUnchanged"/>), or the record <paramref name="g"/>
    /// already holds for it (for an import <see cref="ImportInstallHoldsIt"/>, after <see cref="NotTheGamesOwn"/> when the table knows
    /// the clip; for an edit <see cref="EditChangesNothing"/>). Such a result replaces nothing, and the game's own record is the game's
    /// file: it is never kept for a package, packaged or written as one.</summary>
    public static string? Unchanged(GameInstall g, ClipEntry slot, ReadOnlySpan<byte> record, PackageItemKind kind = PackageItemKind.Import) =>
        Unchanged(g, slot, record, kind, ImportInstallHoldsIt);

    /// <summary>As <see cref="Unchanged(GameInstall, ClipEntry, ReadOnlySpan{byte}, PackageItemKind)"/>, for an item kept for the package:
    /// an import's install-held reason is <see cref="InstallHoldsIt"/>, whose way out is a build from game files that don't hold it.</summary>
    public static string? Unchanged(GameInstall g, PackageItem item) => Unchanged(g, item.Slot, item.Record, Kind(item), InstallHoldsIt);

    static string? Unchanged(GameInstall g, ClipEntry slot, ReadOnlySpan<byte> record, PackageItemKind kind, string importHeld)
    {
        var status = g.Vanilla?.StatusOf(slot.Id, record);
        if (status == ClipStatus.Vanilla) return GamesOwnUnchanged;
        // the row it was made from (a modded file's, say) or the row the game loads for the clip
        if (!slot.Bytes.Span.SequenceEqual(record) && (g.FindClip(slot.Id) is not { } held || !held.Bytes.Span.SequenceEqual(record))) return null;
        if (kind == PackageItemKind.Edit) return EditChangesNothing;
        return status == ClipStatus.Replaced ? NotTheGamesOwn + importHeld : importHeld;
    }

    /// <summary>Every check of the kept report (<see cref="CheckResult.Line"/>'s lines read back: the level, the check's name, the message
    /// with where its worst fell, in the Edit screen's words), the failures first, then the warnings, then the passes, each in report order, as the Edit screen
    /// lists them.</summary>
    public static List<CheckResult> Checks(PackageItem item)
    {
        var l = new List<CheckResult>();
        foreach (var raw in item.ReportText.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            foreach (var level in new[] { CheckLevel.Pass, CheckLevel.Warn, CheckLevel.Fail })
            {
                string w = CheckResult.Word(level);
                if (!line.StartsWith(w + " ", StringComparison.Ordinal)) continue;
                string rest = line[w.Length..].TrimStart();
                // where the worst fell, worded as the Edit screen words it: "(tick 7, Foot_L)", not the report's "(worst at tick 7, Foot_L)"
                int worst = rest.LastIndexOf(" (worst at ", StringComparison.Ordinal);
                if (worst >= 0 && rest.EndsWith(')')) rest = rest[..worst] + " (" + rest[(worst + " (worst at ".Length)..];
                int colon = rest.IndexOf(": ", StringComparison.Ordinal);
                l.Add(colon < 0 ? new CheckResult("", level, rest) : new CheckResult(rest[..colon], level, rest[(colon + 2)..]));
            }
        }
        return [.. l.OrderByDescending(c => c.Level)];
    }

    /// <summary>The report's lines at warn or fail, each "WARN name: message" with its level word, in report order.</summary>
    public static List<string> Warnings(PackageItem item)
    {
        var l = new List<string>();
        foreach (var raw in item.ReportText.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            foreach (var level in new[] { CheckLevel.Warn, CheckLevel.Fail })
            {
                string w = CheckResult.Word(level);
                if (line.StartsWith(w + " ", StringComparison.Ordinal)) l.Add(w + " " + line[w.Length..].TrimStart());
            }
        }
        return l;
    }
}
