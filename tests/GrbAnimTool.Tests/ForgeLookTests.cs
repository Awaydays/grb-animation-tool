using System.Buffers.Binary;
using System.Diagnostics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Forge;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Package;

namespace GrbAnimTool.Tests;

/// <summary>The Package screen's plan never waits for a forge being read for the original clips: while the read runs in the background,
/// the look the screen makes on the window's thread (the originals without reading, and the last read's failure) answers at once, the
/// clip still to be looked for; once the read ends, the original is found there.</summary>
[Collection("install")]
public sealed class ForgeLookTests(InstallFixture f)
{
    [GameDataFact]
    public void ALookWhileAForgeIsReadNeverWaitsForIt()
    {
        // the game folder's own install holds modded clips: one of them, whose original a vanilla backup holds (the vanilla patch forge
        // stands in for it: a whole animation datafile to decode)
        using var installed = GameInstall.Open(GameData.Game!);
        var c = installed.Clips.FirstOrDefault(x => x.Status == ClipStatus.Replaced && f.Install.FindClip(x.Id) is not null);
        Assert.True(c is not null, "the game folder's patch forge holds no clip of the game's that isn't the game's own");
        var record = c!.Bytes.ToArray();
        record[^1] ^= 1;
        var items = new List<PackageItem> { new(c, record, Core.Library.ClipChange.Imported("x.glb"), CheckLevel.Pass, "", DateTime.Now) };
        string backup = GameData.Forge!;
        var reading = Stopwatch.StartNew();
        var read = Task.Run(() => PackageBuild.Originals(installed, items, readForges: true, backupForge: backup));
        while (!PackageBuild.ReadInProgress(installed, backup) && !read.IsCompleted) Thread.Sleep(1);
        Assert.False(read.IsCompleted, "the read ended before it could be looked at");
        // the window thread's look: at once, the clip still to be looked for, no failure said
        var look = Stopwatch.StartNew();
        var meanwhile = PackageBuild.Originals(installed, items, readForges: false, backupForge: backup);
        var failure = PackageBuild.ForgeReadFailure(installed, backup);
        look.Stop();
        bool stillReading = PackageBuild.ReadInProgress(installed, backup);
        Assert.True(look.Elapsed < TimeSpan.FromMilliseconds(250), $"the look took {look.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(stillReading, "the read ended during the look: the look waited for it");
        Assert.True(meanwhile[0].Pending);
        Assert.Null(failure);
        // a second read of the same forge waits for the first, and both find the original there
        var second = PackageBuild.Originals(installed, items, readForges: true, backupForge: backup);
        var found = read.GetAwaiter().GetResult();
        reading.Stop();
        Assert.True(reading.Elapsed > look.Elapsed * 4, $"the read took {reading.Elapsed.TotalMilliseconds:F0} ms, the look {look.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(PackageBuild.FromBackupForge, found[0].From);
        Assert.Equal(PackageBuild.FromBackupForge, second[0].From);
        Assert.False(PackageBuild.ReadInProgress(installed, backup));
        Assert.Equal(PackageBuild.FromBackupForge, PackageBuild.Originals(installed, items, readForges: false, backupForge: backup)[0].From);
    }
}

/// <summary>The forge reader on forges made here, of directories alone: a small one reads, and one whose directories hold more entries
/// together than any forge does, or number an entry past what an index holds, is refused. No game data.</summary>
public sealed class ForgeReaderTests
{
    /// <summary>A forge of its header, its data header, the directories in a chain, and <paramref name="tableBytes"/> zeros that stand as
    /// every directory's index and name table (each entry: offset 0, size 0, no name).</summary>
    static MemoryStream Forge(int tableBytes, params (uint Count, uint IdxStart, uint IdxEnd)[] dirs)
    {
        const int dataHeader = 0x40, first = dataHeader + 0x2C;
        int tables = first + 0x30 * dirs.Length;
        var b = new byte[tables + tableBytes];
        "scimitar\0"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), 27);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(0x0D), dataHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(dataHeader + 0x20), (uint)dirs.Length);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(dataHeader + 0x24), first);
        for (int i = 0; i < dirs.Length; i++)
        {
            var d = b.AsSpan(first + 0x30 * i, 0x30);
            BinaryPrimitives.WriteUInt32LittleEndian(d, dirs[i].Count);
            BinaryPrimitives.WriteInt64LittleEndian(d[0x08..], tables);
            BinaryPrimitives.WriteInt64LittleEndian(d[0x10..], i + 1 < dirs.Length ? first + 0x30 * (i + 1) : -1);
            BinaryPrimitives.WriteUInt32LittleEndian(d[0x18..], dirs[i].IdxStart);
            BinaryPrimitives.WriteUInt32LittleEndian(d[0x1C..], dirs[i].IdxEnd);
            BinaryPrimitives.WriteInt64LittleEndian(d[0x20..], tables);
        }
        return new MemoryStream(b);
    }

    [Fact]
    public void ASmallForgeReads()
    {
        var f = ForgeReader.Read(Forge(192 * 3, (3, 10, 12), (2, 13, 14)));
        Assert.Equal(2, f.Fdts.Count);
        Assert.Equal([10, 11, 12, 13, 14], f.Entries.Select(e => e.Index));
    }

    /// <summary>Eleven directories of 100,000 entries each, all on the same tables: more entries together than the reader takes.</summary>
    [Fact]
    public void MoreEntriesThanAForgeHoldsAreRefused()
    {
        var dirs = Enumerable.Range(0, 11).Select(i => (100_000u, (uint)i * 100_000, (uint)i * 100_000 + 99_999)).ToArray();
        Assert.Throws<InvalidDataException>(() => ForgeReader.Read(Forge(192 * 100_000, dirs)));
    }

    [Fact]
    public void AnEntryNumberedPastAnIndexIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => ForgeReader.Read(Forge(192, (1, 0x80000000, 0x80000000))));
    }
}
