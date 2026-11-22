using GrbAnimTool.Core.Graph;

namespace GrbAnimTool.Core.Edits;

/// <summary>What the graph's verdict means for the edits: the refusals they share, and the warnings they carry into their results. Every
/// answer is the verdict's own (<see cref="ClipGraphVerdict.CanChangeMotion"/>, <see cref="ClipGraphVerdict.CanChangeLength"/>,
/// <see cref="ClipGraphVerdict.MotionWarning"/>, <see cref="ClipGraphVerdict.ReplaceWarning"/>), so a change to the graph's rules reaches
/// the edits unchanged.</summary>
public static class EditVerdicts
{
    /// <summary>The class of a blend's child (<see cref="GraphClasses.BlendChild"/>), kept for callers that name it here.</summary>
    public const uint BlendClass = GraphClasses.BlendChild;

    /// <summary>Whether the verdict counts the clip as a blend's child (<see cref="ClipGraphVerdict.UnderBlend"/>).</summary>
    public static bool UnderBlend(ClipGraphVerdict v) => v.UnderBlend;

    /// <summary>The verdict for an edit that changes the clip's motion (mirror, root-motion scale): refused wherever the verdict refuses a
    /// change of motion (<see cref="ClipGraphVerdict.CanChangeMotion"/>), with its reason. <paramref name="why"/> says what of the clip a
    /// motion field's selection reads that the edit changes.</summary>
    public static ClipGraphVerdict RequireMotionFree(string clip, ClipGraphVerdict? v, string why)
    {
        if (v is null) throw new EditRefusedException(EditRefusal.GraphIncomplete, $"{clip}: the animation graph wasn't read, so whether a motion field reads this clip's motion isn't known");
        if (v.CanChangeMotion) return v;
        var kind = !v.MotionFieldKnown ? EditRefusal.GraphIncomplete : v.ListedWithoutNodeUse ? EditRefusal.ListedWithoutNodeUse : EditRefusal.MotionField;
        string reason = v.MotionRefusal ?? "the animation graph's verdict refuses a change of this clip's motion";
        throw new EditRefusedException(kind, $"{clip}: {reason}" + (kind == EditRefusal.MotionField ? $"; its selection reads the clip's {why}" : ""));
    }

    /// <summary>The verdict for an edit that changes the clip's length (trim, speed): refused wherever the verdict refuses a change of length
    /// (<see cref="ClipGraphVerdict.CanChangeLength"/>), with its reason. <paramref name="edit"/> names the edit.</summary>
    public static ClipGraphVerdict RequireLengthFree(string clip, ClipGraphVerdict? v, string edit)
    {
        if (v is null) throw new EditRefusedException(EditRefusal.GraphIncomplete, $"{clip}: the animation graph wasn't read, so what holds this clip's length isn't known");
        if (v.CanChangeLength) return v;
        var kind = !v.Scanned || !v.GraphComplete ? EditRefusal.GraphIncomplete : v.ListedWithoutNodeUse ? EditRefusal.ListedWithoutNodeUse : EditRefusal.LengthHeld;
        string reason = v.LengthRefusal ?? "the animation graph's verdict refuses a change of this clip's length";
        throw new EditRefusedException(kind, $"{clip}: {reason}" + (kind == EditRefusal.LengthHeld ? $"; a {edit} would leave it describing the old length" : ""));
    }

    /// <summary>The warnings an edit carries from the verdict: on a replace (an equal swap) the verdict's
    /// <see cref="ClipGraphVerdict.ReplaceWarning"/>; on an edit that changes what a blend may read (<paramref name="changes"/>: the clip's
    /// speed, heading or both; null when the edit changes neither) the verdict's <see cref="ClipGraphVerdict.MotionWarning"/>, once. On a
    /// replace of a blend's child the replace warning already says the blend's case, so the motion warning isn't added again.</summary>
    public static List<string> Warnings(ClipGraphVerdict? v, string? changes, bool replace = false)
    {
        var w = new List<string>();
        if (v is null) { w.Add("the animation graph wasn't read: what plays this clip and what reads its motion aren't known"); return w; }
        if (replace && v.ReplaceWarning is { } r) w.Add(r);
        if (replace && v.UnderBlend) return w;
        if (changes is not null && v.MotionWarning is { } m && !w.Any(x => x.StartsWith(m, StringComparison.Ordinal)))
            w.Add($"{m} (this edit changes the clip's {changes})");
        return w;
    }
}
