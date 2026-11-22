using System.Text.Json.Nodes;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.Tests;

/// <summary>The package list kept between runs: back after a restart; back after the forge was repacked (an installed package, a patch);
/// another game folder's list kept untouched beside it, through a restore and a save; a clip whose slot the game files no longer have
/// kept in the file but not shown; a damaged, cut-short or partly empty file read as nothing with the problem said. On the vanilla
/// install, in a folder of the test's own.</summary>
[Collection("install")]
public sealed class PackageStoreTests(InstallFixture f) : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "package-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>A kept clip whose record is another than the game's own and still passes the rebuild check a restore holds it to: one
    /// byte changed, the nearest to the record's end that leaves it so.</summary>
    PackageItem Kept(ClipEntry c, ClipChange change)
    {
        var b = c.Bytes.ToArray();
        for (int k = 1; ; k++)
        {
            b[^k] ^= 0x01;
            try { if (Core.Writing.ClipRebuild.Rebuild(b, f.Install.SharedTable).Bytes.AsSpan().SequenceEqual(b)) break; }
            catch (Core.Writing.RebuildRefusedException) { }
            b[^k] ^= 0x01;
        }
        return new PackageItem(c, b, change, CheckLevel.Warn, $"Report of {c.Name}\nWARN something", new DateTime(2026, 11, 21, 10, 0, 0));
    }

    /// <summary>Changes the kept file as <paramref name="change"/> says.</summary>
    static void Change(PackageStore s, Action<JsonNode> change)
    {
        var n = JsonNode.Parse(File.ReadAllText(s.FilePath))!;
        change(n);
        File.WriteAllText(s.FilePath, n.ToJsonString());
    }

    /// <summary>Two running copies of the tool on one game folder: the list one saved since the other read the file is kept aside as it
    /// is before the other's save replaces it, and that save says so; the copy's later saves, of its own file, keep nothing aside.</summary>
    [GameDataFact]
    public void AListAnotherRunningCopySavedIsKeptAsideBeforeItIsReplaced()
    {
        var g = f.Install;
        PackageStore a = new(_dir), b = new(_dir);
        a.Load(g);
        b.Load(g);
        a.Save(g, [Kept(g.FindClip("mca_emote_Hello")!, ClipChange.Mirror())]);
        Assert.Null(a.ChangedElsewhere);
        var asSaved = File.ReadAllBytes(a.FilePath);
        b.Save(g, [Kept(g.FindClip("mcm_nwp_fm_std_V0_idle_03")!, ClipChange.Trim(3, 40))]);
        string aside = Assert.Single(Directory.GetFiles(_dir, PackageStore.ElsewhereName + "*.json"));
        Assert.Equal(asSaved, File.ReadAllBytes(aside));
        Assert.Equal(aside, b.ChangedElsewhere);
        b.Save(g, []);
        Assert.Null(b.ChangedElsewhere);
        Assert.Single(Directory.GetFiles(_dir, PackageStore.ElsewhereName + "*.json"));
        Assert.Empty(Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json"));
    }

    /// <summary>Another running copy saving its list of another game folder changes nothing of this game folder's list: this copy's save
    /// keeps nothing aside and says nothing. And the copies kept aside because another copy changed this game folder's list, however
    /// many, never push out the copy kept because the file couldn't be read whole.</summary>
    [GameDataFact]
    public void AnotherCopysListOfAnotherGameFolderIsNoChangeAndNeverPushesOutAFileNotReadWhole()
    {
        var g = f.Install;
        var hello = Kept(g.FindClip("mca_emote_Hello")!, ClipChange.Mirror());
        var idle = Kept(g.FindClip("mcm_nwp_fm_std_V0_idle_03")!, ClipChange.Trim(3, 40));
        var a = new PackageStore(_dir);
        a.Load(g);
        a.Save(g, [hello]);
        // another copy, on another game folder, saves its list in the same file
        Change(a, n =>
        {
            var other = JsonNode.Parse(n["Lists"]![0]!.ToJsonString())!;
            other["GameFolder"] = @"X:\Other install\Ghost Recon Breakpoint";
            n["Lists"]!.AsArray().Add(other);
        });
        a.Save(g, [hello, idle]);
        Assert.Null(a.ChangedElsewhere);
        Assert.Equal([a.FilePath], Directory.GetFiles(_dir));

        // a file not read whole, kept aside by the next save; then another copy on this game folder changes its list again and again
        File.WriteAllText(a.FilePath, "{\"Version\":4,\"Lists\":[]}");
        var unread = File.ReadAllBytes(a.FilePath);
        Assert.NotNull(a.Load(g).Problem);
        a.Save(g, [hello]);
        var b = new PackageStore(_dir);
        for (int k = 0; k < PackageStore.MostUnreadable + 1; k++)
        {
            b.Load(g);
            b.Save(g, k % 2 == 0 ? new[] { idle } : new[] { hello, idle });
            a.Save(g, [hello]);
            Assert.NotNull(a.ChangedElsewhere);
        }
        Assert.Contains(Directory.GetFiles(_dir), p => File.ReadAllBytes(p).AsSpan().SequenceEqual(unread));
        // the list, the copy not read whole, and the newest copies another copy's changes left
        Assert.Equal(PackageStore.MostUnreadable + 2, Directory.GetFiles(_dir).Length);
        Assert.Equal(PackageStore.MostUnreadable, Directory.GetFiles(_dir, PackageStore.ElsewhereName + "*.json").Length);
    }

    /// <summary>A kept record the rebuild check refuses (here its head holds one unit less of an arena than its curves need), in a file
    /// that is otherwise whole (its SHA-256 is the record's, its verdict PASS), isn't put back in the list: it is said with the clips that
    /// don't fit, with why, and stays kept in the file through a load and a save.</summary>
    [GameDataFact]
    public void AKeptRecordTheRebuildCheckRefusesIsntRestoredAndStaysKept()
    {
        var g = f.Install;
        var hello = g.FindClip("mca_emote_Hello")!;
        var store = new PackageStore(_dir);
        store.Save(g, [Kept(hello, ClipChange.Mirror())]);
        Assert.Equal([hello.Name], store.Load(g).Restored.Select(i => i.Slot.Name));   // sound as it was kept
        Change(store, n =>
        {
            var item = n["Lists"]![0]!["Items"]![0]!;
            var b = Convert.FromBase64String((string)item["Record"]!);
            var c = Core.Clips.ClipRecord.Parse(b);
            var need = new long[9];
            foreach (var cv in c.Curves) if (!cv.IsReference && Core.Writing.ClipRebuild.ArenaOf(cv.Tag, cv.ValueBytes) is var a and > 0) need[a] += Core.Writing.ClipRebuild.UnitsOf(cv);
            var (at, units) = new[] { (3, 1), (5, 2), (7, 4), (9, 8) }
                .Select(k => (At: Core.Writing.ClipRebuild.KindAt(b, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8)), c.HeadEntries, k.Item1), Need: need[k.Item2]))
                .First(k => k.At >= 0 && k.Need > 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)(units - 1));
            item["Record"] = Convert.ToBase64String(b);
            item["RecordSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
            item["Verdict"] = 0;
        });
        var before = File.ReadAllBytes(store.FilePath);
        var r = store.Load(g);
        Assert.Null(r.Problem);
        Assert.Empty(r.Restored);
        string said = Assert.Single(r.NotFitting);
        Assert.StartsWith(hello.Name + ":", said);
        Assert.Contains("arena kind", said);
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        store.Save(g, r.Restored);
        Assert.Single(JsonNode.Parse(File.ReadAllText(store.FilePath))!["Lists"]![0]!["Items"]!.AsArray());
        Assert.Single(store.Load(g).NotFitting);
        Assert.Empty(Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json"));
    }

    [GameDataFact]
    public void TheKeptListComesBackOnItsGameFilesAndOtherListsStayKept()
    {
        var g = f.Install;
        var hello = g.FindClip("mca_emote_Hello")!;
        var idle = g.FindClip("mcm_nwp_fm_std_V0_idle_03")!;
        var items = new[] { Kept(hello, ClipChange.Imported(Path.Combine("anims", "M72.glb"))), Kept(idle, ClipChange.Trim(3, 40)) };
        var store = new PackageStore(_dir);
        Assert.Same(PackageRestore.Nothing, store.Load(g));
        store.Save(g, items);
        Assert.Equal([store.FilePath], Directory.GetFiles(_dir));   // written whole, nothing beside it

        // a restart: a new store on the same folder, the same game files
        var r = new PackageStore(_dir).Load(g);
        Assert.Null(r.Problem);
        Assert.Empty(r.NotFitting);
        Assert.Empty(r.Elsewhere);
        Assert.Equal(2, r.Restored.Count);
        for (int i = 0; i < 2; i++)
        {
            Assert.Same(items[i].Slot, r.Restored[i].Slot);
            Assert.Equal(items[i].Record, r.Restored[i].Record);
            Assert.Equal((items[i].Change, items[i].Verdict, items[i].ReportText, items[i].KeptAt), (r.Restored[i].Change, r.Restored[i].Verdict, r.Restored[i].ReportText, r.Restored[i].KeptAt));
        }

        // the forge repacked (a package installed, a game patch: its file, size, time and record table changed): the list still comes
        // back, whatever the slots hold now
        Change(store, n => n["Lists"]![0]!["Forge"] = @"X:\elsewhere\DataPC_patch_01.forge");
        Assert.Equal(2, store.Load(g).Restored.Count);

        // another game folder's list (another install opened in between): not shown here, said, and kept untouched through a restore
        // and a save of this one
        Change(store, n => n["Lists"]![0]!["GameFolder"] = @"X:\Other install\Ghost Recon Breakpoint");
        var before = File.ReadAllBytes(store.FilePath);
        r = store.Load(g);
        Assert.Empty(r.Restored);
        Assert.Equal(@"2 clips kept for the game folder X:\Other install\Ghost Recon Breakpoint (X:\elsewhere\DataPC_patch_01.forge)", Assert.Single(r.Elsewhere));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));   // a load writes nothing
        store.Save(g, [items[0]]);
        var lists = JsonNode.Parse(File.ReadAllText(store.FilePath))!["Lists"]!.AsArray();
        Assert.Equal(2, lists.Count);
        Assert.Equal(2, lists.Single(l => (string)l!["GameFolder"]! == @"X:\Other install\Ghost Recon Breakpoint")!["Items"]!.AsArray().Count);
        Assert.Equal([hello.Name], store.Load(g).Restored.Select(i => i.Slot.Name));
        // this list emptied on the screen: the other list stays
        store.Save(g, []);
        Assert.Single(JsonNode.Parse(File.ReadAllText(store.FilePath))!["Lists"]!.AsArray());
        Assert.Single(store.Load(g).Elsewhere);

        // a clip whose slot these game files don't have as kept: not shown, said, and kept in the file through a save
        File.Delete(store.FilePath);
        store.Save(g, items);
        Change(store, n => n["Lists"]![0]!["Items"]![0]!["Name"] = "mca_emote_Goodbye");
        r = store.Load(g);
        Assert.Equal([idle.Name], r.Restored.Select(i => i.Slot.Name));
        Assert.StartsWith("mca_emote_Goodbye: these game files' clip of its ID is", Assert.Single(r.NotFitting));
        store.Save(g, r.Restored);
        Assert.Single(store.Load(g).NotFitting);
        // a record that isn't the one its SHA-256 names: a clip that can't be read, not shown, said by name with the file kept aside
        // before the next save, which leaves it out; the clip that doesn't fit stays
        Change(store, n => n["Lists"]![0]!["Items"]![0]!["RecordSha256"] = new string('0', 64));
        var damaged = store.Load(g);
        Assert.Empty(damaged.Restored);
        Assert.StartsWith("mca_emote_Goodbye:", Assert.Single(damaged.NotFitting));
        Assert.Contains($"1 kept clip ({idle.Name}) couldn't be read", damaged.Problem);
        Assert.Contains(PackageStore.UnreadableName + "<date>_<time>.json", damaged.Problem);
        var asItWas = File.ReadAllBytes(store.FilePath);
        store.Save(g, damaged.Restored);
        Assert.Equal(asItWas, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json"))));
        r = store.Load(g);
        Assert.Null(r.Problem);
        Assert.StartsWith("mca_emote_Goodbye:", Assert.Single(r.NotFitting));
        Assert.Empty(r.Restored);
        foreach (var aside in Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json")) File.Delete(aside);

        // a property given twice in a clip, or in its change: the clip can't be read, said, and the file kept aside before the next save
        File.Delete(store.FilePath);
        store.Save(g, items);
        File.WriteAllText(store.FilePath, File.ReadAllText(store.FilePath)
            .Replace("\"Name\":\"" + hello.Name + "\"", "\"Name\":\"" + hello.Name + "\",\"Name\":\"" + hello.Name + "\"", StringComparison.Ordinal)
            .Replace("\"Kind\":\"Trim\"", "\"Kind\":\"Trim\",\"Kind\":\"Trim\"", StringComparison.Ordinal));
        r = store.Load(g);
        Assert.Empty(r.Restored);
        // (the one giving its name twice isn't named: its name doesn't read)
        Assert.Contains($"2 kept clips ({idle.Name}) couldn't be read", r.Problem);
        store.Save(g, r.Restored);
        Assert.Single(Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json"));
        foreach (var aside in Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json")) File.Delete(aside);

        // the clips that don't fit forgotten on asking: the list on the screen and another game folder's list stay
        store.Save(g, items);
        Change(store, n =>
        {
            n["Lists"]![0]!["Items"]![0]!["Name"] = "mca_emote_Goodbye";
            var other = JsonNode.Parse(n["Lists"]![0]!.ToJsonString())!;
            other["GameFolder"] = @"X:\Other install\Ghost Recon Breakpoint";
            n["Lists"]!.AsArray().Add(other);
        });
        r = store.Load(g);
        Assert.Single(r.NotFitting);
        Assert.Equal(1, store.ForgetNotFitting(g, r.Restored));
        r = store.Load(g);
        Assert.Empty(r.NotFitting);
        Assert.Equal([idle.Name], r.Restored.Select(i => i.Slot.Name));
        Assert.Equal(@"2 clips kept for the game folder X:\Other install\Ghost Recon Breakpoint (" + g.PatchForgePath + ")", Assert.Single(r.Elsewhere));
        File.Delete(store.FilePath);

        // a file cut short (a write that didn't finish), of another version, or holding a missing clip: nothing back, the problem said
        store.Save(g, items);
        var whole = File.ReadAllBytes(store.FilePath);
        File.WriteAllBytes(store.FilePath, whole[..(whole.Length / 2)]);
        r = store.Load(g);
        Assert.Empty(r.Restored);
        Assert.Contains("couldn't be read", r.Problem);
        File.WriteAllText(store.FilePath, "{\"Version\":7}");
        Assert.Contains("couldn't be read", store.Load(g).Problem);
        File.WriteAllBytes(store.FilePath, whole);
        Change(store, n => n["Lists"]![0]!["Items"]![0] = null);
        Assert.Contains("couldn't be read", store.Load(g).Problem);
        Change(store, n => n["Lists"]![0] = null);
        Assert.Contains("couldn't be read", store.Load(g).Problem);
    }

    /// <summary>A file not read whole (a clip of a kind a later version writes, a clip damaged, a format this version can't read): what
    /// reads comes back, the rest is said; the file as it was is kept beside it before a save replaces it, under a name of its own, never
    /// written over, at most a few of them. The clips a build wrote leave their own game files' list only.</summary>
    [GameDataFact]
    public void AListNotReadWholeKeepsWhatReadsAndIsKeptAsideBeforeItIsReplaced()
    {
        var g = f.Install;
        var clips = new[] { "mca_emote_Hello", "mcm_nwp_fm_std_V0_idle_03", "mcm_nwp_fm_std_V1_cycle" }.Select(n => g.FindClip(n)!).ToArray();
        var items = new[] { Kept(clips[0], ClipChange.Imported(Path.Combine("anims", "M72.glb"))), Kept(clips[1], ClipChange.Trim(3, 40)), Kept(clips[2], ClipChange.Speed(1.25)) };
        var store = new PackageStore(_dir);
        store.Save(g, items);
        // a clip of a kind this version doesn't know, and one damaged: the third comes back, the two are said
        Change(store, n =>
        {
            n["Lists"]![0]!["Items"]![0]!["Change"]!["Kind"] = "Stretch";
            n["Lists"]![0]!["Items"]![1]!["Record"] = 12;
        });
        var asKept = File.ReadAllBytes(store.FilePath);
        var r = store.Load(g);
        Assert.Equal([clips[2].Name], r.Restored.Select(i => i.Slot.Name));
        Assert.Contains("2 kept clips (mca_emote_Hello, mcm_nwp_fm_std_V0_idle_03) couldn't be read", r.Problem);
        Assert.Contains(PackageStore.UnreadableName, r.Problem);
        Assert.Equal([store.FilePath], Directory.GetFiles(_dir));   // a load writes nothing
        // the next save keeps the file as it was beside it first
        store.Save(g, r.Restored);
        var aside = Assert.Single(Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json"));
        Assert.Equal(asKept, File.ReadAllBytes(aside));
        Assert.Null(store.Load(g).Problem);
        // the file saved reads whole: no other copy
        store.Save(g, r.Restored);
        Assert.Single(Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json"));

        // a file of a later format: nothing of it read, said; each such file kept before it is replaced, under a name of its own, the
        // oldest going past the most kept
        var copies = new List<byte[]>();
        for (int k = 0; k < PackageStore.MostUnreadable + 2; k++)
        {
            File.WriteAllText(store.FilePath, $"{{\"Version\":4,\"Lists\":[],\"Note\":{k}}}");
            copies.Add(File.ReadAllBytes(store.FilePath));
            var later = store.Load(g);
            Assert.Empty(later.Restored);
            Assert.Contains("a later version of the tool writes", later.Problem);
            store.Save(g, r.Restored);
        }
        var kept = Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json").Select(File.ReadAllBytes).ToList();
        Assert.Equal(PackageStore.MostUnreadable, kept.Count);
        foreach (var c in copies.TakeLast(PackageStore.MostUnreadable)) Assert.Contains(kept, x => x.SequenceEqual(c));
        // the same file again (a save after it failed, tried again): no second copy of it, and no older copy pushed out
        File.WriteAllBytes(store.FilePath, copies[^1]);
        store.Save(g, r.Restored);
        Assert.Equal(kept.Select(Convert.ToHexString).Order(), Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json").Select(File.ReadAllBytes).Select(Convert.ToHexString).Order());
        // the oldest copy held open by another program, so it can't go: the save goes on, the copy stays, and a later save takes it
        int Aside() => Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json").Length;
        string oldest = Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json").Select(p => new FileInfo(p))
            .OrderBy(i => i.LastWriteTimeUtc).ThenBy(i => i.Name, StringComparer.Ordinal).First().FullName;
        using (File.Open(oldest, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            File.WriteAllText(store.FilePath, "{\"Version\":4,\"Lists\":[],\"Note\":\"held\"}");
            Assert.Contains("a later version of the tool writes", store.Load(g).Problem);
            store.Save(g, r.Restored);
            Assert.Null(store.Load(g).Problem);
            Assert.Equal(PackageStore.MostUnreadable + 1, Aside());
        }
        File.WriteAllText(store.FilePath, "{\"Version\":4,\"Lists\":[],\"Note\":\"after\"}");
        store.Load(g);
        store.Save(g, r.Restored);
        Assert.Equal(PackageStore.MostUnreadable, Aside());
        Assert.False(File.Exists(oldest));

        // a list whose game folder isn't a path (empty, or with a character no path holds), or without its datafile's ID: left out, said,
        // the other list back; a load and a save never fail on it
        File.Delete(store.FilePath);
        store.Save(g, items);
        Change(store, n =>
        {
            var lists = n["Lists"]!.AsArray();
            foreach (var bad in new Action<JsonNode>[] { l => l["GameFolder"] = "", l => l["GameFolder"] = "X:\\a\0b", l => l.AsObject().Remove("DatafileId") })
            {
                var other = JsonNode.Parse(lists[0]!.ToJsonString())!;
                bad(other);
                lists.Add(other);
            }
        });
        var odd = store.Load(g);
        Assert.Equal(3, odd.Restored.Count);
        Assert.Contains("3 lists couldn't be read", odd.Problem);
        Assert.Empty(odd.Elsewhere);
        store.Save(g, odd.Restored);
        Assert.Null(store.Load(g).Problem);
        Assert.Equal(3, store.RemoveBuilt(g, odd.Restored) + store.Load(g).Restored.Count);

        // a load that couldn't read the file (another program held it), though the save after it reads it: the file is kept aside before
        // the save replaces it, so the clips the screen never got aren't lost
        File.Delete(store.FilePath);
        var once = new PackageStore(_dir);
        once.Save(g, items);
        var whole = File.ReadAllBytes(once.FilePath);
        PackageRestore held;
        using (File.Open(once.FilePath, FileMode.Open, FileAccess.Read, FileShare.None)) held = once.Load(g);
        Assert.Empty(held.Restored);
        Assert.Contains("couldn't be read", held.Problem);
        once.Save(g, []);
        Assert.Null(once.ChangedElsewhere);   // nothing of it was known at the load, so no other copy is said to have changed it
        Assert.Contains(Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json"), f => File.ReadAllBytes(f).AsSpan().SequenceEqual(whole));
        // once written, the file reads whole: the next save keeps nothing aside again
        int asideNow = Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json").Length;
        once.Save(g, [items[0]]);
        Assert.Equal(asideNow, Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json").Length);

        // the file held by another program while the list is saved, forgotten from or built: nothing is written over it (the other game
        // folder's list in it, and this one's, would be lost), each refused with why, and the file as it was; once it reads, saved again
        File.Delete(once.FilePath);
        once.Save(g, items);
        Change(once, n =>
        {
            var other = JsonNode.Parse(n["Lists"]![0]!.ToJsonString())!;
            other["GameFolder"] = @"X:\Other install\Ghost Recon Breakpoint";
            n["Lists"]!.AsArray().Add(other);
        });
        Assert.Null(once.Load(g).Problem);
        var beforeHeld = File.ReadAllBytes(once.FilePath);
        int asideBefore = Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json").Length;
        using (File.Open(once.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Contains("in use or can't be opened now", Assert.Throws<IOException>(() => once.Save(g, [items[0]])).Message);
            Assert.Throws<IOException>(() => once.ForgetNotFitting(g, [items[0]]));
            Assert.Throws<IOException>(() => once.RemoveBuilt(g, [items[2]]));
        }
        Assert.Equal(beforeHeld, File.ReadAllBytes(once.FilePath));
        Assert.Equal(asideBefore, Directory.GetFiles(_dir, PackageStore.UnreadableName + "*.json").Length);
        once.Save(g, [items[0]]);
        var heldLists = JsonNode.Parse(File.ReadAllText(once.FilePath))!["Lists"]!.AsArray();
        Assert.Equal(3, heldLists.Single(l => (string)l!["GameFolder"]! == @"X:\Other install\Ghost Recon Breakpoint")!["Items"]!.AsArray().Count);
        Assert.Equal([items[0].Slot.Name], once.Load(g).Restored.Select(i => i.Slot.Name));

        // a property given twice at the file's root: the file isn't read, said, and a save keeps it aside and replaces it
        File.WriteAllText(store.FilePath, "{\"Version\":3,\"Version\":3,\"Lists\":[]}");
        Assert.Contains("couldn't be read", store.Load(g).Problem);
        store.Save(g, [items[1]]);
        Assert.Equal([items[1].Slot.Name], store.Load(g).Restored.Select(i => i.Slot.Name));

        // a file saved with a UTF-8 byte order mark (as an editor may save it): read whole
        File.Delete(store.FilePath);
        store.Save(g, items);
        File.WriteAllBytes(store.FilePath, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(store.FilePath)]);
        var marked = store.Load(g);
        Assert.Null(marked.Problem);
        Assert.Equal(items.Select(i => i.Slot.Name), marked.Restored.Select(i => i.Slot.Name));

        // the clips built taken out of their own game files' list only, another game folder's list as it was
        File.Delete(store.FilePath);
        store.Save(g, items);
        Change(store, n =>
        {
            var other = JsonNode.Parse(n["Lists"]![0]!.ToJsonString())!;
            other["GameFolder"] = @"X:\Other install\Ghost Recon Breakpoint";
            n["Lists"]!.AsArray().Add(other);
        });
        Assert.Equal(1, store.RemoveBuilt(g, [items[2]]));
        Assert.Equal([clips[0].Name, clips[1].Name], store.Load(g).Restored.Select(i => i.Slot.Name));
        var lists = JsonNode.Parse(File.ReadAllText(store.FilePath))!["Lists"]!.AsArray();
        Assert.Equal(3, lists.Single(l => (string)l!["GameFolder"]! == @"X:\Other install\Ghost Recon Breakpoint")!["Items"]!.AsArray().Count);
        // a clip kept again since with another record stays
        Assert.Equal(0, store.RemoveBuilt(g, [Kept(clips[0], ClipChange.Mirror()) with { Record = [1, 2, 3] }]));
    }

    /// <summary>A list kept in the format before this one (format 2: each change as text, an edit's after "edit: ", an import's the .glb's
    /// path) comes back with each change read from its words, and a text that isn't exactly one of their wordings shown as it was kept,
    /// never guessed at; the other game files' list kept beside it as well. A load writes nothing; the next save writes format 3, each
    /// change in parts, which comes back the same. A change of format 3 that can't be read is a damaged file.</summary>
    [GameDataFact]
    public void AListKeptAsTextComesBackInPartsAndIsWrittenAnewOnTheNextSave()
    {
        var g = f.Install;
        (string Text, ClipChange Change, string Words)[] old =
        [
            ("edit: mirrored", ClipChange.Mirror(), "Mirrored"),
            ("edit: root motion scaled by 0.5", ClipChange.Scale(0.5), "Root-motion scale 0.5x"),
            ("edit: played at 1.25 times the speed", ClipChange.Speed(1.25), "Speed 1.25x"),
            ("edit: trimmed to its ticks 54 to 484", ClipChange.Trim(54, 484), "Trim 0.9 s to 8.07 s"),
            ("edit: mca_emote_Shotgun's motion swapped in", ClipChange.Swap("mca_emote_Shotgun"), "Swap: motion from mca_emote_Shotgun"),
            (@"X:\anims\walk.glb", ClipChange.Imported(@"X:\anims\walk.glb"), "Imported: walk.glb"),
            ("edit: trim, ticks 3 to 40", new ClipChange { Kind = ChangeKind.Text, Text = "edit: trim, ticks 3 to 40" }, "edit: trim, ticks 3 to 40"),
            ("edit: another clip's motion swapped in", new ClipChange { Kind = ChangeKind.Text, Text = "edit: another clip's motion swapped in" }, "edit: another clip's motion swapped in"),
            ("edit: played at 1,25 times the speed", new ClipChange { Kind = ChangeKind.Text, Text = "edit: played at 1,25 times the speed" }, "edit: played at 1,25 times the speed"),
        ];
        var clips = g.Clips.Where(c => c.Record is not null).Take(old.Length).ToList();
        var store = new PackageStore(_dir);
        store.Save(g, [.. clips.Select(c => Kept(c, ClipChange.Mirror()))]);
        // the file as format 2 kept it: each change as text, and another game folder's list beside it
        Change(store, n =>
        {
            n["Version"] = 2;
            var items = n["Lists"]![0]!["Items"]!.AsArray();
            for (int i = 0; i < old.Length; i++)
            {
                var item = items[i]!.AsObject();
                item.Remove("Change");
                item["Source"] = old[i].Text;
            }
            var other = JsonNode.Parse(n["Lists"]![0]!.ToJsonString())!;
            other["GameFolder"] = @"X:\Other install\Ghost Recon Breakpoint";
            n["Lists"]!.AsArray().Add(other);
        });
        var asKept = File.ReadAllBytes(store.FilePath);
        var r = store.Load(g);
        Assert.Null(r.Problem);
        Assert.Equal(old.Select(o => o.Change), r.Restored.Select(i => i.Change));
        Assert.Equal(old.Select(o => o.Words), r.Restored.Select(i => i.Change.Words));
        Assert.Single(r.Elsewhere);
        Assert.Equal(asKept, File.ReadAllBytes(store.FilePath));   // read, never written by a load

        // the next save writes format 3, the other list's changes in parts too, and they come back the same
        store.Save(g, r.Restored);
        var doc = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        Assert.Equal(PackageStore.Version, (int)doc["Version"]!);
        Assert.Equal(2, doc["Lists"]!.AsArray().Count);
        foreach (var item in doc["Lists"]!.AsArray().SelectMany(l => l!["Items"]!.AsArray()))
        {
            Assert.Null(item!["Source"]);
            Assert.NotNull(item["Change"]!["Kind"]);
        }
        var mine = doc["Lists"]!.AsArray().Single(l => (string)l!["GameFolder"]! == g.GameFolder)!["Items"]!;
        var trim = mine[3]!["Change"]!;
        Assert.Equal(("Trim", 54, 484, 60), ((string)trim["Kind"]!, (int)trim["From"]!, (int)trim["To"]!, (int)trim["TicksPerSecond"]!));
        Assert.Equal(("Swap", "mca_emote_Shotgun"), ((string)mine[4]!["Change"]!["Kind"]!, (string)mine[4]!["Change"]!["SourceClip"]!));
        Assert.Equal(("Import", @"X:\anims\walk.glb"), ((string)mine[5]!["Change"]!["Kind"]!, (string)mine[5]!["Change"]!["Glb"]!));
        Assert.Equal(("Text", "edit: trim, ticks 3 to 40"), ((string)mine[6]!["Change"]!["Kind"]!, (string)mine[6]!["Change"]!["Text"]!));
        var again = store.Load(g);
        Assert.Equal(old.Select(o => o.Change), again.Restored.Select(i => i.Change));
        Assert.Single(again.Elsewhere);

        // a round trip of format 3 from the items themselves: every kind, with factors and a later cut's ticks, back the same
        ClipChange[] made = [ClipChange.Mirror(), ClipChange.Scale(0.25), ClipChange.Speed(1.95), ClipChange.Trim(120, 437), ClipChange.Swap("mca_emote_Shotgun"),
            ClipChange.Imported(Path.Combine("anims", "walk.glb")), new ClipChange { Kind = ChangeKind.Text, Text = "edit: as kept" }];
        File.Delete(store.FilePath);
        store.Save(g, [.. clips.Take(made.Length).Select((c, i) => Kept(c, made[i]))]);
        Assert.Equal(made, store.Load(g).Restored.Select(i => i.Change));

        // a change of format 3 that can't be read: of an unknown kind, or missing a part its kind needs, the file is damaged; and a clip of
        // format 2 without its text
        var whole = File.ReadAllText(store.FilePath);
        Change(store, n => n["Lists"]![0]!["Items"]![0]!["Change"]!["Kind"] = "Stretch");
        Assert.Contains("couldn't be read", store.Load(g).Problem);
        File.WriteAllText(store.FilePath, whole);
        Change(store, n => n["Lists"]![0]!["Items"]![3]!["Change"]!.AsObject().Remove("TicksPerSecond"));
        Assert.Contains("couldn't be read", store.Load(g).Problem);
        File.WriteAllText(store.FilePath, whole);
        Change(store, n => n["Lists"]![0]!["Items"]![0]!.AsObject().Remove("Change"));
        Assert.Contains("couldn't be read", store.Load(g).Problem);
        File.WriteAllText(store.FilePath, whole);
        Change(store, n => n["Version"] = 2);
        Assert.Contains("couldn't be read", store.Load(g).Problem);
    }
}
