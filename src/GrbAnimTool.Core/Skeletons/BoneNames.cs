using System.Text;
using GrbAnimTool.Core.Hashing;

namespace GrbAnimTool.Core.Skeletons;

/// <summary>Names for bone and track hashes. The game stores only the CRC-32 of a bone's name, so names come from trial: each candidate's
/// CRC-32 is compared, and a name is known only because its hash matches. Unknown hashes print as eight hex digits.</summary>
public sealed class BoneNames
{
    /// <summary>The body skeleton's named bones.</summary>
    static readonly string[] Body =
    [
        "Reference", "Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
        "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "RightShoulder", "RightArm", "RightForeArm", "RightHand",
        "LeftHandThumb1", "LeftHandThumb2", "LeftHandThumb3", "LeftHandIndex1", "LeftHandIndex2", "LeftHandIndex3",
        "LeftHandMiddle1", "LeftHandMiddle2", "LeftHandMiddle3", "LeftHandRing1", "LeftHandRing2", "LeftHandRing3",
        "LeftHandPinky1", "LeftHandPinky2", "LeftHandPinky3",
        "RightHandThumb1", "RightHandThumb2", "RightHandThumb3", "RightHandIndex1", "RightHandIndex2", "RightHandIndex3",
        "RightHandMiddle1", "RightHandMiddle2", "RightHandMiddle3", "RightHandRing1", "RightHandRing2", "RightHandRing3",
        "RightHandPinky1", "RightHandPinky2", "RightHandPinky3",
        "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase", "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase",
        "FakeGunRoot_Gameplay", "Neck1",
    ];

    /// <summary>Usual bone words and prefixes tried in every combination.</summary>
    static readonly string[] Words =
    [
        "Reference", "Root", "Hips", "Pelvis", "Spine", "Spine1", "Spine2", "Spine3", "Chest", "Neck", "Neck1", "Head", "Shoulder", "Arm", "ForeArm",
        "Hand", "UpLeg", "Leg", "Foot", "ToeBase", "Toe", "Thumb1", "Thumb2", "Thumb3", "Index1", "Index2", "Index3", "Middle1", "Middle2", "Middle3",
        "Ring1", "Ring2", "Ring3", "Pinky1", "Pinky2", "Pinky3", "HandThumb4", "HandIndex4", "HandMiddle4", "HandRing4", "HandPinky4", "ToeBase_End",
        "ArmRoll", "ForeArmRoll", "UpLegRoll", "LegRoll", "Weapon", "Prop", "Gun", "FakeGun", "FakeGunRoot", "Holster", "Sling", "Back", "Stow",
        "Attach", "IK", "Trajectory", "Camera", "Eye",
    ];

    static readonly string[] Prefixes =
        ["", "A_", "H_", "B_", "C_", "M_", "Left", "Right", "L_", "R_", "A_Left", "A_Right", "A_L_", "A_R_", "A_M_", "H_Left", "H_Right", "Bip_", "Skl_", "Bn_", "Bone_"];

    readonly Dictionary<uint, string> _names = [];

    BoneNames() { }

    /// <summary>The built-in names: the body's bones and the trial of usual words.</summary>
    public static BoneNames Default()
    {
        var d = new BoneNames();
        foreach (var n in Body) d.Add(n);
        foreach (var w in Words) foreach (var p in Prefixes) d.Add(p + w);
        return d;
    }

    /// <summary>Adds a candidate name; the first name met for a hash is kept. Only while the table is built (<see cref="Default"/>, and an
    /// install's open): once handed out the table is read-only, so it is safe to read from several threads.</summary>
    internal void Add(string name) => _names.TryAdd(Crc32.Of(name), name);

    /// <summary>Every printable ASCII run of 2 to 96 characters in <paramref name="b"/> is tried as a name, whole and split at
    /// separators. A trailing _decimal that is the rest's own CRC-32 (how some lists carry a bone's hash) names the rest.</summary>
    internal void AddFrom(ReadOnlySpan<byte> b)
    {
        int i = 0;
        while (i < b.Length)
        {
            if (b[i] < 0x20 || b[i] >= 0x7F) { i++; continue; }
            int j = i;
            while (j < b.Length && b[j] >= 0x20 && b[j] < 0x7F && j - i < 96) j++;
            if (j - i >= 2)
            {
                string s = Encoding.ASCII.GetString(b[i..j]);
                Add(s);
                foreach (var t in s.Split(';', ' ', ',', '.'))
                {
                    if (t.Length < 2) continue;
                    Add(t);
                    int u = t.LastIndexOf('_');
                    if (u > 0 && uint.TryParse(t[(u + 1)..], out uint h) && Crc32.Of(t[..u]) == h) _names.TryAdd(h, t[..u]);
                }
            }
            i = j;
        }
    }

    public bool TryGet(uint hash, out string name) => _names.TryGetValue(hash, out name!);

    /// <summary>The name, "(mover)" for track 0, or null when unknown.</summary>
    public string? NameOf(uint hash) => hash == 0 ? "(mover)" : _names.GetValueOrDefault(hash);

    /// <summary>The name, or the hash as eight hex digits.</summary>
    public string Display(uint hash) => NameOf(hash) ?? Hex(hash);

    public static string Hex(uint hash) => hash.ToString("X8");
}
