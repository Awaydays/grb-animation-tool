namespace GrbAnimTool.Core.Index;

/// <summary>Who plays a clip, as the screens say it before the clip is replaced: a replaced
/// clip changes for every character that plays it. <see cref="Sure"/> are the who-categories the game's graph (a document of one layer
/// alone) or the tool's list gives; <see cref="Probably"/> those the clip's name alone gives, or only a machine several layers share, or
/// the graph's teammates where the name's family rules them out (<see cref="ClipIndexEntry.WhoProbably"/>, the same rule as the clip
/// info's labels). Both in the vocabulary's order. <see cref="Known"/> is false without an index entry; <see cref="Unknown"/> then says
/// why (the index not ready yet, not built, or not holding the clip).</summary>
public sealed record ClipUsers(IReadOnlyList<string> Sure, IReadOnlyList<string> Probably, bool Known, string? Unknown = null)
{
    /// <summary>Where a "probably" comes from: the three cases of <see cref="ClipIndexEntry.WhoProbably"/>.</summary>
    const string ProbablyFrom = "from the clip's name, from a machine several kinds of character share, or where the name's family says the graph's user is unlikely";

    /// <summary>What "probably" stands for, said once beside the users of several clips (the clip index's own lines).</summary>
    public const string ProbablyMeaning = "\"probably\": " + ProbablyFrom;

    /// <summary>The tooltip of every screen's "Played by" text (the clip info, Edit, Import and Package): what the users are, and what
    /// "probably" stands for, in the same words as <see cref="ProbablyMeaning"/>.</summary>
    public const string PlayedByTip = "Every kind of character that plays the clip: replacing it changes it for all of them. \"Probably\": " + ProbablyFrom + ".";

    /// <summary>What a clip says before the clip index is ready.</summary>
    public const string NotKnownText = "not known yet (the clip index isn't ready)";

    /// <summary>What a clip says when the clip index couldn't be built (the screens give the reason in the notice).</summary>
    public const string NotBuiltText = "not known (the clip index couldn't be built)";

    /// <summary>What a clip the index doesn't hold says.</summary>
    public const string NotHeldText = "not known (the clip index doesn't hold this clip)";

    /// <summary>What an uncategorised clip says.</summary>
    public const string UncategorisedText = "the tool can't tell who plays it";

    /// <summary>Why the clip index couldn't be built, when it couldn't (<see cref="NotBuiltText"/>); null otherwise.</summary>
    public string? IndexError { get; init; }

    /// <summary>The users of a clip of <paramref name="index"/>; not <see cref="Known"/> without an index (<paramref name="indexError"/>:
    /// why it couldn't be built, null while it isn't ready yet) or for a clip it doesn't hold.</summary>
    public static ClipUsers Of(ClipIndex? index, ulong clipId, string? indexError = null) =>
        index is null ? new([], [], false, indexError is null ? NotKnownText : NotBuiltText) { IndexError = indexError }
        : index.Find(clipId) is { } e ? Of(e)
        : new([], [], false, NotHeldText);

    public static ClipUsers Of(ClipIndexEntry e)
    {
        var probably = new HashSet<string>(e.WhoProbably, StringComparer.Ordinal);
        var names = e.Who.Select(c => c.Name).Distinct(StringComparer.Ordinal).ToList();
        return new(names.Where(n => !probably.Contains(n)).ToList(), names.Where(probably.Contains).ToList(), true);
    }

    /// <summary>How many kinds of character play it.</summary>
    public int Count => Sure.Count + Probably.Count;

    /// <summary>Whether more than one kind of character plays it: replacing it changes it for each.</summary>
    public bool Several => Count > 1;

    /// <summary>Whether one kind of character plays it, and only probably: the tool isn't sure nobody else does.</summary>
    public bool OneProbable => Count == 1 && Probably.Count == 1;

    /// <summary>Whether the index holds the clip but nothing places who plays it.</summary>
    public bool Uncategorised => Known && Count == 0;

    /// <summary>The users in words, the sure ones first and the probable ones in a clause of their own, so "probably" covers exactly
    /// them: "Player, Teammates and Enemy"; "Player; probably also Teammates, Enemy and NPC"; "probably Enemy and NPC";
    /// <see cref="UncategorisedText"/>; without an index entry, <see cref="Unknown"/>.</summary>
    public string Text =>
        !Known ? Unknown ?? NotKnownText
        : Count == 0 ? UncategorisedText
        : Probably.Count == 0 ? List(Sure)
        : Sure.Count == 0 ? "probably " + List(Probably)
        : List(Sure) + "; probably also " + List(Probably);

    /// <summary>The non-blocking notice the Edit, Import and Package screens show before a build: for a clip several kinds of character
    /// play, that replacing it changes it for all of them; for one only probably played by one kind, that others may play it too; for an
    /// uncategorised one, that the tool can't tell who else it changes; without an index entry, that who plays it isn't known (and why).
    /// Null for a clip one kind of character plays, sure.</summary>
    public string? Notice =>
        !Known ? $"Who plays this clip isn't known: {UnknownWhy}. A replaced clip changes for every kind of character that plays it."
        : Several ? $"More than one kind of character plays this clip: {Text}. Replacing it changes it for all of them."
        : OneProbable ? $"The tool isn't sure who plays this clip ({Text}): others may play it too, and replacing it changes it for every one of them."
        : Uncategorised ? "The tool can't tell who plays this clip, so it can't say who else a replacement changes."
        : null;

    /// <summary>Why who plays the clip isn't known, in words for a notice.</summary>
    public string UnknownWhy =>
        Unknown == NotBuiltText ? "the clip index couldn't be built" + (IndexError is { Length: > 0 } why ? $" ({why.TrimEnd('.')})" : "")
        : Unknown == NotHeldText ? "the clip index doesn't hold it"
        : "the clip index isn't ready yet";

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
