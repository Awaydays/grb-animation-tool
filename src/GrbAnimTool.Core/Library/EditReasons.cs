using GrbAnimTool.Core.Edits;

namespace GrbAnimTool.Core.Library;

/// <summary>Why an edit can't be made: the kind of refusal (null for a fault of the edit's own code) and the technical detail (tracks,
/// tags, arena kinds, the graph's places), which the screens show on hover and the command line and the reports print.</summary>
public sealed record EditBlock(EditRefusal? Kind, string Detail)
{
    /// <summary>For a fault of the edit's own code (no kind): the error itself, its place and stack, for the error log.</summary>
    public Exception? Error { get; init; }
}

/// <summary>A refusal in a modder's words: what would go wrong in the game if the edit were made anyway, or what the tool can't do yet,
/// with no term of the record's format. One sentence per edit and kind of refusal (<see cref="Plain"/>); the technical detail stays
/// apart (<see cref="EditBlock.Detail"/>).</summary>
public static class EditReasons
{
    /// <summary>The sentence's start for an edit: "Can't mirror", "Can't change the speed", ...</summary>
    public static string Lead(EditKind k) => k switch
    {
        EditKind.Mirror => "Can't mirror",
        EditKind.Scale => "Can't scale the root motion",
        EditKind.Speed => "Can't change the speed",
        EditKind.Trim => "Can't trim",
        _ => "Can't swap",
    };

    /// <summary>The edit as a noun: "a mirror", "a speed change", ...</summary>
    static string Noun(EditKind k) => k switch
    {
        EditKind.Mirror => "a mirror",
        EditKind.Scale => "a root-motion scale",
        EditKind.Speed => "a speed change",
        EditKind.Trim => "a trim",
        _ => "a swap",
    };

    /// <summary>What the edit changes, as the graph's refusals name it: its length (trim, speed) or its motion (mirror, scale, swap).</summary>
    static string Changes(EditKind k) => k is EditKind.Speed or EditKind.Trim ? "length" : "motion";

    /// <summary>The refusal of <paramref name="edit"/> for <paramref name="kind"/> (null: a fault of the edit's own code) in a modder's
    /// words: "Can't mirror: this clip aims the hands at the weapon, and a mirror would leave them reaching to the old side."</summary>
    public static string Plain(EditKind edit, EditRefusal? kind)
    {
        // a fault of the edit's own code on the clip: whether the edit can be made isn't known
        if (kind is null) return $"Can't tell whether this clip takes {Noun(edit)}: the tool failed on it (the detail says how).";
        string lead = kind == EditRefusal.TimesUnread && edit is EditKind.Speed or EditKind.Trim ? "Can't change the length yet" : Lead(edit);
        return $"{lead}: {Why(edit, kind)}.";
    }

    /// <summary>The part of <see cref="Plain(EditKind, EditRefusal?)"/> after its lead: what would go wrong, in a modder's words, with
    /// no full stop.</summary>
    public static string Why(EditKind edit, EditRefusal? kind)
    {
        string what = edit switch
        {
            EditKind.Mirror => "a mirrored clip",
            EditKind.Scale => "a clip with its travel scaled",
            EditKind.Speed => "a clip at another speed",
            EditKind.Trim => "a trimmed clip",
            _ => "a swapped clip",
        };
        return kind switch
        {
            EditRefusal.Unreadable => "part of this clip's data can't be read, so the tool can't rebuild it safely",
            EditRefusal.BadParameter => edit == EditKind.Speed
                ? "at this speed the clip would be too short or too long for the game"
                : "this setting is out of the range the edit takes",
            EditRefusal.GraphIncomplete => $"the game's animation graph couldn't be read whole, so the tool can't tell what in the game depends on this clip's {Changes(edit)}",
            EditRefusal.LengthHeld => "the game's animation graph keeps its own note of this clip's length, which would still say the old length, so in game the clip would be cut short or held at its end",
            EditRefusal.MotionField => $"the game picks this clip by the way it moves (a motion field), and {what} would no longer match what the game expects, so characters could play it at the wrong moment",
            EditRefusal.HandTracks => "this clip aims the hands at the weapon, and a mirror would leave them reaching to the old side",
            EditRefusal.TrackListsDiffer => "the two clips don't animate the same parts of the body, so one can't play in the other's place",
            EditRefusal.LengthsDiffer => "the two clips aren't the same length, so one can't play in the other's place",
            EditRefusal.NoSwapSource => "no other clip in these game files has the same length and animates the same parts of the body, which a swap needs",
            EditRefusal.NoRootMotion => "this clip doesn't travel, so there is no travel to scale",
            EditRefusal.Unpaired => "a part of the body this clip moves has no partner on the other side to take its mirrored motion",
            EditRefusal.ArenaKindMissing => $"{what} would need a kind of stored data this clip's file has no room for, and the tool can't add that room yet",
            EditRefusal.ArenaKindToZero => $"{what} would leave this clip's file with an empty kind of stored data, which no clip of the game has and the game may not read",
            EditRefusal.TooManyKeys or EditRefusal.LongCurve => "a part of this clip holds more keyframes than the tool rewrites safely yet",
            EditRefusal.CantMove => $"some of this clip's timed data can't be moved by the tool yet, so {what} would fall out of step",
            EditRefusal.Rebuild or EditRefusal.ReadBack => $"the tool couldn't write {what} that reads back as it should, so it makes nothing",
            EditRefusal.TimesUnread => "this clip has timing data the tool doesn't adjust yet, and it would fall out of step with the motion",
            EditRefusal.ListedWithoutNodeUse => $"the game uses this clip in a way the tool can't read, so it can't check what changing its {Changes(edit)} would break",
            EditRefusal.NoMotion => "this clip moves no bones (a face's or a dialogue's clip), so there is nothing to mirror",
            EditRefusal.TooShort => "this clip is too short to trim",
            _ => "the tool failed on this clip (the detail says how)",
        };
    }

    /// <summary>The block's sentence for <paramref name="edit"/> (<see cref="Plain"/>).</summary>
    public static string Plain(EditKind edit, EditBlock b) => Plain(edit, b.Kind);

    /// <summary>A refusal of the edit itself (an Apply, a trial run) as a block: its kind, and its message without the clip's name in
    /// front for the detail.</summary>
    public static EditBlock Of(string clip, EditRefusedException ex) => new(ex.Kind, EditJobs.Unnamed(clip, ex.Message));
}
