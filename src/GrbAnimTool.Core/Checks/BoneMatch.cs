using System.Globalization;
using System.Numerics;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Checks;

/// <summary>What a bone match found wrong, if anything.</summary>
public enum BoneMatchFault { None, Missing, Parent, Hash, Rest, Axes }

/// <summary>A bone as a file holds it: its parent's name (null at the armature) and its rest, local to its parent, in the game's axes.</summary>
public sealed record FileBone(string? Parent, Vector3 RestPosition, Quaternion RestRotation);

/// <summary>A file's bones against the game's skeleton: every skeleton bone present by name (and its name's CRC-32 its hash, for a named
/// bone), under the same parent, at the same rest within a tolerance (lengths and turns), in the game's axes turned for glTF. A file whose
/// rest reads right only without the turn has its axes unturned (exported with z up), the fault that lays the character on its side.</summary>
public static class BoneMatch
{
    /// <summary>Rest within 0.1 cm and 0.1 degrees passes, within 0.5 warns, further fails.</summary>
    public const float PassCm = 0.1f, PassDeg = 0.1f, FailCm = 0.5f, FailDeg = 0.5f;

    public sealed record Result(CheckResult Check, BoneMatchFault Fault);

    static string F(double v, string f = "F4") => v.ToString(f, CultureInfo.InvariantCulture);

    static bool IsHexName(string n) => n.Length == 8 && n.All(Uri.IsHexDigit);

    public static Result Check(Skeleton s, IReadOnlyDictionary<string, FileBone> file)
    {
        const string Name = "bone match";
        var missing = s.Bones.Where(b => !file.ContainsKey(b.Name)).Select(b => b.Name).ToList();
        if (missing.Count > 0)
            return new(new CheckResult(Name, CheckLevel.Fail, $"{missing.Count} of the skeleton's {s.Bones.Count} bones aren't in the file: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", ..." : "")}. "
                + "Start from the tool's own export of the skeleton or a clip, and keep every bone"), BoneMatchFault.Missing);
        foreach (var b in s.Bones)
        {
            // a named bone's name gives its hash; an unnamed one is named by its hash
            uint h = IsHexName(b.Name) ? uint.Parse(b.Name, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : Crc32.Of(b.Name);
            if (h != b.Hash) return new(new CheckResult(Name, CheckLevel.Fail, $"the bone {b.Name} doesn't hash to the skeleton's {b.Hash:X8}"), BoneMatchFault.Hash);
            string? want = b.ParentIndex >= 0 ? s.Bones[b.ParentIndex].Name : null;
            string? got = file[b.Name].Parent;
            if (got != want)
                return new(new CheckResult(Name, CheckLevel.Fail, $"the bone {b.Name} hangs from {got ?? "the armature"} in the file, from {want ?? "the armature"} in the game's skeleton"), BoneMatchFault.Parent);
        }
        var (cm, deg, bc, bd) = Worst(s, file, turned: true);
        if (cm <= FailCm && deg <= FailDeg)
        {
            var level = cm <= PassCm && deg <= PassDeg ? CheckLevel.Pass : CheckLevel.Warn;
            int extra = file.Count - s.Bones.Count;
            return new(new CheckResult(Name, level, $"all {s.Bones.Count} bones by name and hash, under the same parents; the rest pose within {F(cm)} cm{(cm > 0 ? $" ({bc})" : "")} and {F(deg)} deg{(deg > 0 ? $" ({bd})" : "")} of the game's"
                + $" (pass within {PassCm} cm and {PassDeg} deg, fail past {FailCm} and {FailDeg}); the axes turned for glTF (y up){(extra > 0 ? $"; {extra} other bone(s) in the file, not used" : "")}",
                Bone: level == CheckLevel.Pass ? null : (cm / PassCm >= deg / PassDeg ? bc : bd)), BoneMatchFault.None);
        }
        var (ucm, udeg, _, _) = Worst(s, file, turned: false);
        if (ucm <= FailCm && udeg <= FailDeg)
            return new(new CheckResult(Name, CheckLevel.Fail, "the file's axes aren't turned for glTF: it was written with z up, so the character would lie on its side. "
                + "Export with glTF's own axes (y up)"), BoneMatchFault.Axes);
        // the worst bone's length against the skeleton's, when the turns agree: a bone made longer or shorter
        var lengths = s.Bones.Where(b => b.ParentIndex >= 0 && b.LocalRest.Position.Length() > 1e-3f)
            .Select(b => (b.Name, Ratio: file[b.Name].RestPosition.Length() / b.LocalRest.Position.Length())).OrderByDescending(x => Math.Abs(x.Ratio - 1)).FirstOrDefault();
        string len = lengths.Name is null ? "" : $"; the bone furthest from its length is {lengths.Name}, {F((lengths.Ratio - 1) * 100, "F2")}% off";
        return new(new CheckResult(Name, CheckLevel.Fail, $"the file's rest pose isn't the game's skeleton: {F(cm, "F2")} cm off ({bc}), {F(deg, "F2")} deg off ({bd}){len} (fail past {FailCm} cm or {FailDeg} deg). "
            + "Start from the tool's own export of the skeleton or the clip, and animate the bones without changing their rest pose", Bone: cm / FailCm >= deg / FailDeg ? bc : bd), BoneMatchFault.Rest);
    }

    /// <summary>The worst rest offset over the bones; with <paramref name="turned"/> false the file's values are first turned back to glTF's
    /// axes, which is what a file written without the turn would read as.</summary>
    static (double Cm, double Deg, string AtCm, string AtDeg) Worst(Skeleton s, IReadOnlyDictionary<string, FileBone> file, bool turned)
    {
        double cm = 0, deg = 0; string bc = "", bd = "";
        foreach (var b in s.Bones)
        {
            var f = file[b.Name];
            var p = turned ? f.RestPosition : GltfAxes.ToGltf(f.RestPosition);
            var q = turned ? f.RestRotation : GltfAxes.ToGltf(f.RestRotation);
            double d = Vector3.Distance(p, b.LocalRest.Position) * 100, a = GltfAxes.AngleDeg(q, b.LocalRest.Rotation);
            if (!(d <= cm)) { cm = double.IsNaN(d) ? double.PositiveInfinity : d; bc = b.Name; }
            if (!(a <= deg)) { deg = double.IsNaN(a) ? double.PositiveInfinity : a; bd = b.Name; }
        }
        return (cm, deg, bc, bd);
    }
}
