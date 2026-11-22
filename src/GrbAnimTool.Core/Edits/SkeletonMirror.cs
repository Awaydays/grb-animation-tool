using System.Numerics;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Edits;

/// <summary>The mirror of a pose through the body's middle plane, on a skeleton, in the clip frame. The clip frame is the frame the mover and
/// the root bones' locals stand in: x is side to side (the right at +x), y forward and z up, as every travelling clip and the game's foot
/// plants show. The skeleton's rest pose stands in a frame of its own (on the game's skeleton it faces +x with the hands on y), so it is first
/// turned into the clip frame (<see cref="RestTurn"/>): its side-to-side axis is the model axis on which the hands' rest places stand furthest
/// apart, its up axis the one on which the hands stand furthest above the feet, and its facing the side the toes point to from the feet.
/// <para>Each body bone has a counterpart: by name (Left and Right exchanged), else itself when its rest place lies in the plane, else the bone
/// at its rest place mirrored (within 0.1 cm); a bone with none, or whose counterpart's parent isn't its parent's counterpart, and every bone
/// of the add-on skeleton, is not mirrored.</para>
/// <para>A model pose (in the clip frame) is mirrored by reflecting each bone's frame through the plane x = 0, setting it on the counterpart,
/// and carrying it onto the counterpart's rest: model'(c) = R(model(n)) k(n), with k(n) = R(rest(n))^-1 rest(c) and rest the rest pose turned
/// into the clip frame, so that pose mirrors to itself. Since R is an automorphism of the rotations, this reads per bone on its local transform
/// alone: local'(c) = k(p)^-1 R(local(n)) k(n) for the rotation and k(p)^-1 applied to M(local position of n) for the position, p the parent of
/// n (none for a root, whose frame is the mover's). So each mirrored track is its counterpart's curve, key by key.</para>
/// <para>A root bone's rest stands in the rest frame, not the clip frame, so a root bone with no track of a kind doesn't mirror to its
/// counterpart's rest (<see cref="RestMirrorsToRest"/>): a clip where neither holds that track can't be mirrored by its own tracks.</para></summary>
public sealed class SkeletonMirror
{
    public Skeleton Skeleton { get; }
    /// <summary>The plane's axis in the clip frame (0 x, 1 y, 2 z): always <see cref="ClipLateralAxis"/>.</summary>
    public int Axis { get; }
    /// <summary>Each mirrored bone's counterpart, by bone index; -1 for a bone not mirrored.</summary>
    public IReadOnlyList<int> Pair { get; }
    /// <summary>The bones not mirrored, by index, with why.</summary>
    public IReadOnlyDictionary<int, string> NotMirrored { get; }
    /// <summary>The worst distance of a mirrored bone's rest place, mirrored, from its counterpart's rest place (cm).</summary>
    public double RestWorstCm { get; }

    /// <summary>The clip frame's side-to-side axis (x), its forward axis (y) and its up axis (z).</summary>
    public const int ClipLateralAxis = 0, ClipForwardAxis = 1, ClipUpAxis = 2;
    /// <summary>The rest pose's model axes found from its bones: side to side (the hands), up (hands over feet), forward (the toes).</summary>
    public int RestLateralAxis { get; }
    public int RestUpAxis { get; }
    public int RestForwardAxis { get; }
    /// <summary>The turn that carries the rest pose's model frame into the clip frame (on the game's skeleton +90 deg about z).</summary>
    public Quaternion RestTurn { get; }
    /// <summary>Each bone's rest model transform turned into the clip frame.</summary>
    public IReadOnlyList<Transform> RestInClipFrame { get; }

    readonly Quaternion[] _k;
    readonly bool[] _restRot, _restPos;

    public const double RestToleranceCm = 0.1;
    /// <summary>How far a bone's rest local, mirrored, may stand from its counterpart's rest local and still count as that rest (deg, cm):
    /// the rest pose's own asymmetry, twice over (a local position is a difference of two model places).</summary>
    public const double RestLocalToleranceDeg = 0.1, RestLocalToleranceCm = 2 * RestToleranceCm;

    public SkeletonMirror(Skeleton s)
    {
        Skeleton = s;
        Axis = ClipLateralAxis;
        int n = s.Bones.Count;
        var model = new Transform[n];
        s.ComputeModel(s.RestLocals(), model);
        int Find(string name) => s.Bones.FirstOrDefault(b => b.Name == name && !b.FromAddOn)?.Index ?? -1;
        int lh = Find("LeftHand"), rh = Find("RightHand"), lf = Find("LeftFoot"), rf = Find("RightFoot"), lt = Find("LeftToeBase"), rt = Find("RightToeBase");
        if (lh < 0 || rh < 0) throw new EditRefusedException(EditRefusal.Unpaired, "the skeleton has no LeftHand and RightHand bones to find its middle plane by");
        if (lf < 0 || rf < 0 || lt < 0 || rt < 0) throw new EditRefusedException(EditRefusal.Unpaired, "the skeleton has no LeftFoot, RightFoot, LeftToeBase and RightToeBase bones to find its facing by");

        // the rest frame's axes: side to side by the hands, up by the hands over the feet, forward by the toes from the feet
        var hands = Vector3.Abs(model[lh].Position - model[rh].Position);
        int lat = Largest(hands, -1, -1);
        var up = (model[lh].Position + model[rh].Position - model[lf].Position - model[rf].Position) / 2;
        int upAxis = Largest(Vector3.Abs(up), lat, -1);
        int fwd = 3 - lat - upAxis;
        var toes = (model[lt].Position + model[rt].Position - model[lf].Position - model[rf].Position) / 2;
        float upSign = Get(up, upAxis), fwdSign = Get(toes, fwd);
        if (Math.Abs(upSign) < 0.1f) throw new EditRefusedException(EditRefusal.Unpaired, "the skeleton's rest hands don't stand above its feet, so its up axis can't be found");
        if (Math.Abs(fwdSign) < 0.01f) throw new EditRefusedException(EditRefusal.Unpaired, "the skeleton's rest toes don't point forward of its feet, so its facing can't be found");
        RestLateralAxis = lat; RestUpAxis = upAxis; RestForwardAxis = fwd;
        var u = Unit(upAxis, upSign); var f = Unit(fwd, fwdSign);
        var r = Vector3.Cross(f, u);   // the right side: forward x up, as in the clip frame (y x z = x)
        // the turn takes r to x, f to y, u to z: as a row-vector matrix, row i is the image of the model axis i
        var mtx = new Matrix4x4(r.X, f.X, u.X, 0, r.Y, f.Y, u.Y, 0, r.Z, f.Z, u.Z, 0, 0, 0, 0, 1);
        var turn = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(mtx));
        if ((Vector3.Transform(r, turn) - Vector3.UnitX).Length() > 1e-4f || (Vector3.Transform(u, turn) - Vector3.UnitZ).Length() > 1e-4f)
            throw new EditRefusedException(EditRefusal.Unpaired, "the skeleton's rest frame can't be turned into the clip frame");
        RestTurn = turn;
        var rest = new Transform[n];
        for (int i = 0; i < n; i++) rest[i] = new Transform(Vector3.Transform(model[i].Position, turn), Quaternion.Normalize(turn * model[i].Rotation));
        RestInClipFrame = rest;

        var byName = new Dictionary<string, int>();
        foreach (var b in s.Bones) byName.TryAdd(b.Name, b.Index);
        var pair = Enumerable.Repeat(-1, n).ToArray();
        var why = new Dictionary<int, string>();
        const float Tol = (float)(RestToleranceCm / 100);
        foreach (var b in s.Bones)
        {
            if (b.FromAddOn) { why[b.Index] = "an add-on bone"; continue; }
            int c = -1;
            if (b.Name.StartsWith("Left", StringComparison.Ordinal) && byName.TryGetValue("Right" + b.Name[4..], out var rb)) c = rb;
            else if (b.Name.StartsWith("Right", StringComparison.Ordinal) && byName.TryGetValue("Left" + b.Name[5..], out var lb)) c = lb;
            else
            {
                var mp = M(rest[b.Index].Position);
                if ((mp - rest[b.Index].Position).Length() <= Tol) c = b.Index;
                else
                {
                    var best = s.Bones.Where(o => !o.FromAddOn && !o.Name.StartsWith("Left", StringComparison.Ordinal) && !o.Name.StartsWith("Right", StringComparison.Ordinal))
                        .OrderBy(o => (rest[o.Index].Position - mp).Length()).First();
                    if ((rest[best.Index].Position - mp).Length() <= Tol && best.Index != b.Index) c = best.Index;
                }
            }
            if (c < 0 || s.Bones[c].FromAddOn) { why[b.Index] = "no counterpart: its rest place isn't in the middle plane, and no bone stands at its mirror"; continue; }
            pair[b.Index] = c;
        }
        // a counterpart must be mutual, and its parent must be the parent's counterpart, or the per-bone rule doesn't hold
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < n; i++)
            {
                if (pair[i] < 0) continue;
                int c = pair[i], p = s.Bones[i].ParentIndex, pc = s.Bones[c].ParentIndex;
                string? bad = pair[c] != i ? "its counterpart isn't mutual"
                    : p < 0 != pc < 0 ? "it and its counterpart aren't both roots"
                    : p >= 0 && pair[p] != pc ? "its counterpart's parent isn't its parent's counterpart" : null;
                if (bad is null) continue;
                why[i] = bad; pair[i] = -1; changed = true;
            }
        }
        double worst = 0;
        for (int i = 0; i < n; i++)
            if (pair[i] >= 0) worst = Math.Max(worst, (M(rest[i].Position) - rest[pair[i]].Position).Length() * 100);
        _k = new Quaternion[n];
        for (int i = 0; i < n; i++)
            _k[i] = pair[i] < 0 ? Quaternion.Identity : Quaternion.Normalize(Quaternion.Inverse(R(rest[i].Rotation)) * rest[pair[i]].Rotation);
        Pair = pair; NotMirrored = why; RestWorstCm = worst;
        if (worst > RestToleranceCm) throw new EditRefusedException(EditRefusal.Unpaired, $"the skeleton's rest pose isn't symmetric: a bone's mirrored rest place stands {worst:F3} cm from its counterpart's");
        // whether each bone's rest local mirrors to its counterpart's rest local: true for every bone below a root, by the symmetry; a root's
        // rest stands in the rest frame, so its turn doesn't
        _restRot = new bool[n]; _restPos = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (pair[i] < 0) continue;
            var c = s.Bones[pair[i]].LocalRest;
            _restRot[i] = CurveValues.Deg(LocalRot(i, s.Bones[i].LocalRest.Rotation), c.Rotation) <= RestLocalToleranceDeg;
            _restPos[i] = (LocalPos(i, s.Bones[i].LocalRest.Position) - c.Position).Length() * 100 <= RestLocalToleranceCm;
        }
    }

    static int Largest(Vector3 v, int not1, int not2)
    {
        int best = -1; float bv = -1;
        for (int a = 0; a < 3; a++)
            if (a != not1 && a != not2 && Get(v, a) > bv) { best = a; bv = Get(v, a); }
        return best;
    }

    static float Get(Vector3 v, int a) => a switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    static Vector3 Unit(int a, float sign) => (a switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ }) * MathF.Sign(sign);

    /// <summary>Whether bone <paramref name="bone"/>'s rest local (its rotation, or its position), mirrored, is its counterpart's rest
    /// local: where neither holds a track of that kind, the mirror is then the clip's own pose. False for a bone not mirrored.</summary>
    public bool RestMirrorsToRest(int bone, bool rotation) => rotation ? _restRot[bone] : _restPos[bone];

    /// <summary>A vector reflected through the plane.</summary>
    public Vector3 M(Vector3 v) => Axis switch { 0 => new(-v.X, v.Y, v.Z), 1 => new(v.X, -v.Y, v.Z), _ => new(v.X, v.Y, -v.Z) };

    /// <summary>A rotation reflected through the plane: the same turn about the mirrored axis the other way.</summary>
    public Quaternion R(Quaternion q) => Axis switch { 0 => new(q.X, -q.Y, -q.Z, q.W), 1 => new(-q.X, q.Y, -q.Z, q.W), _ => new(-q.X, -q.Y, q.Z, q.W) };

    /// <summary>A transform reflected through the plane on both sides (a proper rigid transform again).</summary>
    public Transform T(Transform t) => new(M(t.Position), R(t.Rotation));

    Quaternion KInvOfParent(int bone) => Skeleton.Bones[bone].ParentIndex is var p and >= 0 ? Quaternion.Inverse(_k[p]) : Quaternion.Identity;

    /// <summary>Bone <paramref name="bone"/>'s local rotation mirrored: what its counterpart's local rotation becomes.</summary>
    public Quaternion LocalRot(int bone, Quaternion local) => Quaternion.Normalize(KInvOfParent(bone) * R(local) * _k[bone]);

    /// <summary>Bone <paramref name="bone"/>'s local position mirrored: what its counterpart's local position becomes.</summary>
    public Vector3 LocalPos(int bone, Vector3 local) => Vector3.Transform(M(local), KInvOfParent(bone));

    /// <summary>A world transform mirrored through the plane of <paramref name="frame"/> (the mover's first place): frame M frame^-1 x M.</summary>
    public Transform InFrame(Transform frame, Transform x)
    {
        var inv = new Transform(Vector3.Transform(-frame.Position, Quaternion.Inverse(frame.Rotation)), Quaternion.Inverse(frame.Rotation));
        return frame * T(inv * x);
    }

    /// <summary>A world point mirrored through the plane of <paramref name="frame"/>.</summary>
    public Vector3 PointInFrame(Transform frame, Vector3 p) =>
        frame.Position + Vector3.Transform(M(Vector3.Transform(p - frame.Position, Quaternion.Inverse(frame.Rotation))), frame.Rotation);
}
