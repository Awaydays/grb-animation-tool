namespace GrbAnimTool.Core.Library;

/// <summary>The short help each screen's "?" opens, one text per heading, and the Help menu's Quick start made of two of them. The README's
/// Quick start uses the same words.</summary>
public static class HelpTexts
{
    public const string Export =
        "Clip .glb: a game animation with the game's skeleton. Start from this to change an existing animation.\n"
        + "Skeleton .glb: the bare rig with no animation. Start from this to animate from scratch.";

    public const string Modded =
        "Shows every file that isn't the game's own: Modded (a separate 1_-_ file) and Replaced (changed inside the game's files).";

    public const string Import =
        "Your animation must:\n"
        + "- use the game skeleton (start from a Clip or Skeleton .glb)\n"
        + "- be at 60 fps (set this in Blender BEFORE importing the .glb)\n"
        + "- match the clip's length, or use Fit\n"
        + "Scaling an animation to fit makes it play faster or slower.";

    public const string Replace =
        "Puts another game animation, or your .glb, on this clip. The clip keeps its own events, so emotes still end and give back control. "
        + "Works best for emotes, idles and poses. Moving, aiming and weapon animations may not line up with the game's own timing.";

    public const string Fit =
        "Cut: uses part of the animation (only when it's longer than the clip). The suggested start is where the loop closes best.\n"
        + "Speed: stretches or squeezes it to fit. It will play faster or slower.\n"
        + "Hold: plays it at normal speed, then keeps the last pose until the clip's own length is over. You get control back only then.";

    public const string SeamlessLoop =
        "Blends the end back into the start so a looping animation doesn't jump. Use it for idles, not for emotes or one-off moves.";

    public const string Swap =
        "Only for two clips with the same length and tracks. For anything else, use Replace.";

    public const string TimingRefused =
        "This clip has timing data the tool can't safely change. Replace and Import still work.";

    public const string Package =
        "Build makes a folder with your changed files (named 1_-_...) and a folder with the files they replace. Place the files like any other "
        + "animation mod. To undo, delete the 1_-_ files and put the originals back.\n"
        + "Clips from other datafiles go in that datafile's own folder; the package shows where.";

    public const string Checks =
        "Game checks: whether the game can play it. A FAIL here stops it being added.\n"
        + "Look checks: how different it looks from the game's own animation (floating, big joint angles, hands or feet in other places). "
        + "The game still plays it; check whether that's what you want.";

    public const string Set =
        "Clips the game switches between in one place, like the idles of one stance. Replace the whole set so they match.";

    public const string LineUpSet =
        "Blends the start and end of every clip toward the same pose, so the game's switches between them don't jump.";

    public const string ProbableSet =
        "Grouped by name; the tool couldn't read this set from the game's data. Check it in game.";

    public const string FeetSlide =
        "How far a planted foot moves during the tool's blends and at the switches between clips. A sliding foot shows in game.";

    public const string PieceStart =
        "Where this clip's piece starts in the source. Suggest spreads the pieces so they overlap as little as possible and the feet line up.";

    public const string SetFilter =
        "Clips the game switches between in one place. Blended: the game mixes them by a value such as aim angle.";

    public const string DatafileFilter =
        "The game keeps its animations in several files. Most player animations are in one of them; civilians and other characters have their own.";

    /// <summary>The warning under "One source for all" on a blended set, for the value the game blends it by.</summary>
    public static string OneSourceOnBlended(string parameter) =>
        $"Every clip gets the same pose, so the game's blend between them ({parameter}) does nothing.";

    /// <summary>The Help menu's Quick start: the Import text, then the Replace text.</summary>
    public const string QuickStart = Import + "\n\n" + Replace;
}
