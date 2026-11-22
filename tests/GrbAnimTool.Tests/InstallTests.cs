using System.Buffers.Binary;
using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Forge;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Tests;

/// <summary>Opening an install whose patch forge is built here from parts: entries copied as the game's patch forge stores them, and
/// entries made by hand. A damaged part must come out as a <see cref="GameInstallException"/> naming it, never another exception. The
/// forge is written to a folder of its own beside the test assembly and deleted afterwards; the game folder gives the Oodle DLL and the
/// base forge, read only.</summary>
public sealed class InstallTests : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "install-" + Guid.NewGuid().ToString("N"));

    public InstallTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>The payloads of these entries as the game's patch forge stores them (still compressed), for those it holds.</summary>
    static Dictionary<ulong, byte[]> Stored(params ulong[] ids)
    {
        using var fs = ForgeReader.OpenRead(GameData.Forge!);
        var lay = ForgeReader.Read(fs);
        var d = new Dictionary<ulong, byte[]>();
        foreach (var id in ids)
            if (lay.Find(id) is { } e) d[id] = ForgeReader.ReadPayload(fs, e);
        return d;
    }

    static byte[] Clip(string name, ulong id, float seconds = 1) =>
        Synthetic.Clip(name, id, seconds, [(Crc32.Of("Hips"), Synthetic.RotCurve([0, 30], Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f)))]);

    static byte[] AnimationDatafile() => Synthetic.StoredDatafile(
        (0xA1, Clip("clip_a", 0xA1)),
        (0xA2, Clip("clip_b", 0xBEEF)),            // its own ID isn't the table's
        (0xA3, Clip("clip_c", 0xA3, 1e9f)));       // a duration out of range

    /// <summary>A patch forge: the animation datafile and entry 0x800 as given, the skeletons as the game stores them unless given.</summary>
    string Forge(byte[] animation, byte[]? boot = null, byte[]? body = null, byte[]? addOn = null)
    {
        var stored = Stored(SharedCurveTable.EntryId, GameInstall.BodySkeletonId, GameInstall.AddOnSkeletonId);
        var entries = new List<(ulong, string, byte[])>
        {
            (GameInstall.AnimationDatafileId, "MIS_Y2E4_Katya_Maksimov", animation),
            (SharedCurveTable.EntryId, "Game Bootstrap Settings", boot ?? stored[SharedCurveTable.EntryId]),
        };
        if ((body ?? stored.GetValueOrDefault(GameInstall.BodySkeletonId)) is { } b) entries.Add((GameInstall.BodySkeletonId, "Regular_Male_Body_Skl", b));
        if ((addOn ?? stored.GetValueOrDefault(GameInstall.AddOnSkeletonId)) is { } a) entries.Add((GameInstall.AddOnSkeletonId, "Player_FakeGun_Addon", a));
        string path = Path.Combine(_dir, $"patch-{Guid.NewGuid():N}.forge");
        File.WriteAllBytes(path, Synthetic.Forge([.. entries]));
        return path;
    }

    static byte[] Table(uint declared) => Synthetic.StoredDatafile((SharedCurveTable.RecordId, Synthetic.SharedTable(declared, [])));

    /// <summary>A skeleton record a datafile accepts: the synthetic record with its length and body ID set.</summary>
    static byte[] SkeletonDatafile(ulong id, (uint, int, Vector3, Quaternion)[] bones)
    {
        var r = Synthetic.Skeleton(bones);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), (uint)(r.Length - 13 - 4));
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(17), id);
        return Synthetic.StoredDatafile((id, r));
    }

    [GameDataFact]
    public void ClipIdsAndDurationsAreChecked()
    {
        using var g = GameInstall.Open(GameData.Game!, Forge(AnimationDatafile()));
        Assert.Equal(3, g.Clips.Count);
        var a = g.FindClip(0xA1)!;
        Assert.True(a.WalkedToEnd);
        Assert.Equal(60, a.LastTick);
        var b = g.FindClip(0xA2)!;
        Assert.Null(b.Record);
        Assert.Contains("own ID 0x000000000000BEEF", b.ParseError);
        var c = g.FindClip(0xA3)!;
        Assert.False(c.WalkedToEnd);
        Assert.Contains("duration", c.Record!.Stop);
        Assert.Equal(0, c.LastTick);
        Assert.All(g.Clips, x => Assert.Equal(ClipStatus.Added, x.Status));
        // the animation datafile is stored without compression: the control decode ran on the next entry read
        Assert.Equal($"entry 0x{SharedCurveTable.EntryId:X}", g.ControlDecodeOn);
    }

    /// <summary>A clip the table of the game's clips doesn't know (status Added, as a clip newer than the table) and that the install
    /// holds exactly as a result is: no change, but never said not to be the game's own, as the tool can't know that; an import gets its
    /// own way out, an edit its own reason, a package's import the build's way out.</summary>
    [GameDataFact]
    public void AClipTheTableDoesntKnowIsNeverSaidNotToBeTheGamesOwn()
    {
        using var g = GameInstall.Open(GameData.Game!, Forge(AnimationDatafile()));
        Assert.NotNull(g.Vanilla);
        var a = g.FindClip(0xA1)!;
        Assert.Equal(ClipStatus.Added, a.Status);
        Assert.Equal(Core.Package.PackageEntries.ImportInstallHoldsIt, Core.Package.PackageEntries.Unchanged(g, a, a.Bytes.Span));
        Assert.Equal(Core.Package.PackageEntries.EditChangesNothing, Core.Package.PackageEntries.Unchanged(g, a, a.Bytes.Span, Core.Package.PackageItemKind.Edit));
        var item = new Core.Import.PackageItem(a, a.Bytes.ToArray(), Core.Library.ClipChange.Imported("x.glb"),Core.Checks.CheckLevel.Pass, "", DateTime.Now);
        Assert.Equal(Core.Package.PackageEntries.InstallHoldsIt, Core.Package.PackageEntries.Unchanged(g, item));
        // a record the install doesn't hold is a change
        var other = a.Bytes.ToArray();
        other[^1] ^= 1;
        Assert.Null(Core.Package.PackageEntries.Unchanged(g, a, other));
    }

    [GameDataFact]
    public void NoCompressedChunkIsSaidPlainly()
    {
        using var g = GameInstall.Open(GameData.Game!, Forge(AnimationDatafile(), boot: Table(0)));
        Assert.Null(g.ControlDecodeOn);
        Assert.Empty(g.SharedTable.Curves);
    }

    [GameDataFact]
    public void DamagedPartsAreReportedByName()
    {
        var anim = AnimationDatafile();
        void Refused(string path, string names)
        {
            var ex = Assert.Throws<GameInstallException>(() => GameInstall.Open(GameData.Game!, path).Dispose());
            Assert.Contains(names, ex.Message);
        }
        // a table record cut short, and one whose curves don't walk to its end
        var cut = Synthetic.SharedTable(0, [])[..20];
        Refused(Forge(anim, boot: Synthetic.StoredDatafile((SharedCurveTable.RecordId, cut))), "shared curve table");
        Refused(Forge(anim, boot: Table(3)), "shared curve table");
        // a body skeleton with no bones, an add-on whose rest pose holds a NaN
        Refused(Forge(anim, body: Synthetic.StoredDatafile((GameInstall.BodySkeletonId,
            Synthetic.Record(Skeleton.Type, "Regular_Male_Body_Skl", GameInstall.BodySkeletonId, new byte[16])))), "body skeleton");
        Refused(Forge(anim, addOn: SkeletonDatafile(GameInstall.AddOnSkeletonId,
            [(1, -1, Vector3.Zero, Quaternion.Identity), (2, 0, new Vector3(float.NaN, 0, 0), Quaternion.Identity)])), "add-on skeleton");
        // an animation datafile that isn't one
        Refused(Forge([.. Enumerable.Range(0, 100).Select(i => (byte)i)]), "animation datafile");
        // a forge cut short
        var whole = File.ReadAllBytes(Forge(anim));
        var shortForge = Path.Combine(_dir, "short.forge");
        File.WriteAllBytes(shortForge, whole[..(whole.Length - 1)]);
        Refused(shortForge, "short.forge");
    }
}
