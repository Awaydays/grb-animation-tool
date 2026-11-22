using System.Text;
using GrbAnimTool.Core.Edits;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.Tests;

/// <summary>The Edit screen's marks of which edits each clip allows, and their cache: built when there is none, reused while the
/// install's files and the edits' code are the same, built again when either changed or the file is damaged.</summary>
[Collection("install")]
public sealed class EditMarksTests(InstallFixture f, Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>The values a progress heard, from any thread.</summary>
    public sealed class Reports : IProgress<double>
    {
        readonly List<double> _values = [];
        readonly HashSet<(string? Name, ThreadPriority Priority, bool Pool)> _threads = [];
        public List<double> Values { get { lock (_values) return [.. _values]; } }

        /// <summary>The threads the reports came from: their name, priority and whether they are the thread pool's.</summary>
        public List<(string? Name, ThreadPriority Priority, bool Pool)> Threads { get { lock (_values) return [.. _threads]; } }

        public void Report(double value)
        {
            var t = Thread.CurrentThread;
            lock (_values)
            {
                _values.Add(value);
                _threads.Add((t.Name, t.Priority, t.IsThreadPoolThread));
            }
        }
    }

    static EditMarksFingerprint Fp => new(new IndexFingerprint(123, 456, 0x000001DE8F05D119, 789, "ab"), 1000, 2000, "cd", EditMarksFingerprint.ThisMethod);

    static readonly Dictionary<ulong, byte> Masks = new() { [0xA] = 0, [0xB] = EditMarks.AllEdits, [0x12] = (byte)(EditMarks.Bit(EditKind.Speed) | EditMarks.Bit(EditKind.Trim)) };

    [Fact]
    public void TheMarksSayWhichEditsEachClipAllows()
    {
        var m = EditMarks.From(Masks);
        ClipEntry Clip(ulong id) => new() { Index = 1, Id = id, Name = "c", Bytes = ReadOnlyMemory<byte>.Empty };
        Assert.Equal("· · S T ·", m.Marks(Clip(0x12)));
        Assert.Equal("Allows: Speed, Trim", m.MarksText(Clip(0x12)));
        Assert.Equal("M R S T W", m.Marks(Clip(0xB)));
        Assert.Equal("· · · · ·", m.Marks(Clip(0xA)));
        Assert.Equal("Allows no edit", m.MarksText(Clip(0xA)));
        Assert.False(m.Any(Clip(0xA)));
        Assert.True(m.Any(Clip(0x12)));
        // a clip not worked out: no marks, and not known whether it allows any
        Assert.Equal("", m.Marks(Clip(0x99)));
        Assert.Null(m.Any(Clip(0x99)));
        Assert.Equal(1, m.NoEdit);
        // the same marks as a clip's choices give them
        var choices = new ClipEditChoices(
        [
            EditOption.Of(EditKind.Mirror, new EditBlock(EditRefusal.HandTracks, "hand tracks"), null), EditOption.Of(EditKind.Scale, new EditBlock(EditRefusal.NoRootMotion, "no travel"), null),
            EditOption.Of(EditKind.Speed, null, null), EditOption.Of(EditKind.Trim, null, null), EditOption.Of(EditKind.Swap, null, null),
        ], FactorChoices.None(EditKind.Speed), FactorChoices.None(EditKind.Scale), TrimChoices.None);
        Assert.Equal((byte)28, EditMarks.MaskOf(choices));
        Assert.Equal(("· · S T W", "Allows: Speed, Trim, Swap"), (choices.Marks, choices.MarksText));
        Assert.Equal((choices.Marks, choices.MarksText), (EditMarks.Text(28), EditMarks.Meaning(28)));
        // this build's way of working them out: the format and the core library's own build
        Assert.Equal($"{EditMarksFingerprint.FormatVersion}/{typeof(EditJobs).Assembly.ManifestModule.ModuleVersionId:N}", EditMarksFingerprint.ThisMethod);
        Assert.NotEqual(Guid.Empty, typeof(EditJobs).Assembly.ManifestModule.ModuleVersionId);
    }

    /// <summary>The errors that say nothing of the clip (the machine short of memory or stack) are passing ones: they leave the marks
    /// incomplete, for Retry, never a clip kept as not told; every other error is the clip's (or the code's), a disposed object's included
    /// (the install closed under the work is a cancellation, the work cancelled first).</summary>
    [Fact]
    public void OnlyErrorsOfTheMomentArePassing()
    {
        foreach (var ex in new Exception[] { new OutOfMemoryException(), new InsufficientExecutionStackException(), new AggregateException(new OutOfMemoryException()) })
            Assert.True(EditJobs.Transient(ex), ex.GetType().Name);
        foreach (var ex in new Exception[] { new ObjectDisposedException("forge"), new IndexOutOfRangeException(), new ArgumentException("x"), new InvalidOperationException(), new NullReferenceException(),
                     new InvalidDataException(), new IOException(), new AggregateException(new OutOfMemoryException(), new OutOfMemoryException()),
                     new AggregateException(new IndexOutOfRangeException()) })
            Assert.False(EditJobs.Transient(ex), ex.GetType().Name);
    }

    /// <summary>Without the game, on an install of six made-up clips whose edits' work is given here: a clip the edits fail on is kept as
    /// not told, its error kept for the log; a clip met while memory ran short is left out, the marks incomplete with that error; the
    /// rest are told. The work runs on the marks' own threads, as the program's does.</summary>
    [Fact]
    public void AClipsFaultIsNotToldAndAnErrorOfTheMomentLeavesTheMarksIncomplete()
    {
        var body = EditSynthetic.Body();
        var clips = Enumerable.Range(1, 6).Select(i => new ClipEntry { Index = i, Id = (ulong)i, Name = $"synthetic_{i}", Bytes = ReadOnlyMemory<byte>.Empty }).ToList();
        var ctor = typeof(GameInstall).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
        var g = (GameInstall)ctor.Invoke(["game", "patch.forge", "base.forge", null, new Core.Forge.ForgeEntry(), new List<Core.Datafiles.DatafileRecord>(), clips,
            EditSynthetic.Table(), body, body, null, null, new OpenTimings(default, default, default, default, default, default, default)]);
        var allowed = new ClipEditChoices([.. Enum.GetValues<EditKind>().Select(k => EditOption.Of(k, null, null))], FactorChoices.None(EditKind.Speed),
            FactorChoices.None(EditKind.Scale), TrimChoices.None);
        InstallEditChoices.ChoicesForTests = (install, c) => !ReferenceEquals(install, g) ? null
            : c.Id == 2 ? throw new IndexOutOfRangeException("a curve past its end")
            : c.Id == 4 ? throw new OutOfMemoryException("short of memory for a curve")
            : allowed;
        try
        {
            var marks = Within.Seconds(60, () => EditMarks.BuildAsync(g, Core.Graph.ClipGraph.Read([], clips.Select(c => c.Id)), SwapSources.Build([], null),
                new SkeletonMirror(body), null, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(new ulong[] { 1, 3, 5, 6 }, marks.Masks.Keys.Order());
            Assert.All(marks.Masks.Values, m => Assert.Equal(EditMarks.AllEdits, m));
            Assert.Equal(new[] { 2ul }, marks.Unknown.ToArray());
            var (clip, error) = Assert.Single(marks.Faults);
            Assert.Equal(("synthetic_2", "a curve past its end"), (clip, error.Message));
            Assert.IsType<IndexOutOfRangeException>(error);
            Assert.IsType<OutOfMemoryException>(marks.Transient);
            Assert.Equal("not every clip was worked out (OutOfMemoryException: short of memory for a curve)", marks.Incomplete);
        }
        finally
        {
            InstallEditChoices.ChoicesForTests = null;
            Core.IO.FolderGuard.Forget(g);
        }
    }

    /// <summary>Without the game: a worker thread that can't be had (the first, or the third of three) fails the work, at once or once
    /// the workers already started have ended, never leaving it waiting; with every thread had, the same work ends with every clip told.</summary>
    [Fact]
    public void AWorkerThreadThatCantBeStartedFailsTheWorkInsteadOfLeavingItWaiting()
    {
        var body = EditSynthetic.Body();
        var clips = Enumerable.Range(1, 40).Select(i => new ClipEntry { Index = i, Id = (ulong)i, Name = $"synthetic_{i}", Bytes = ReadOnlyMemory<byte>.Empty }).ToList();
        var ctor = typeof(GameInstall).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
        var g = (GameInstall)ctor.Invoke(["game", "patch.forge", "base.forge", null, new Core.Forge.ForgeEntry(), new List<Core.Datafiles.DatafileRecord>(), clips,
            EditSynthetic.Table(), body, body, null, null, new OpenTimings(default, default, default, default, default, default, default)]);
        var allowed = new ClipEditChoices([.. Enum.GetValues<EditKind>().Select(k => EditOption.Of(k, null, null))], FactorChoices.None(EditKind.Speed),
            FactorChoices.None(EditKind.Scale), TrimChoices.None);
        InstallEditChoices.ChoicesForTests = (install, c) => ReferenceEquals(install, g) ? allowed : null;
        try
        {
            var graph = Core.Graph.ClipGraph.Read([], clips.Select(c => c.Id));
            var swaps = SwapSources.Build([], null);
            var mirror = new SkeletonMirror(body);
            foreach (int failAt in new[] { 0, 2 })
            {
                int made = 0;
                InstallEditChoices.NewThreadForTests.Value = start => Interlocked.Increment(ref made) - 1 == failAt
                    ? throw new OutOfMemoryException("no thread to be had") : new Thread(start);
                var task = InstallEditChoices.OfInBackground(g, graph, swaps, mirror, CancellationToken.None, parallelism: 3);
                var ex = Assert.Throws<AggregateException>(() => Within.Seconds(30, () => task.Wait(TimeSpan.FromSeconds(25)) ? 0 : throw new TimeoutException("the work was left waiting")));
                var inner = Assert.IsType<InvalidOperationException>(Assert.Single(ex.Flatten().InnerExceptions));
                Assert.IsType<OutOfMemoryException>(inner.InnerException);
                Assert.Equal(failAt + 1, made);
            }
            InstallEditChoices.NewThreadForTests.Value = null;
            var whole = Within.Seconds(30, () => InstallEditChoices.OfInBackground(g, graph, swaps, mirror, CancellationToken.None, parallelism: 3).GetAwaiter().GetResult());
            Assert.Equal(40, whole.Clips.Count);
        }
        finally
        {
            InstallEditChoices.NewThreadForTests.Value = null;
            InstallEditChoices.ChoicesForTests = null;
            Core.IO.FolderGuard.Forget(g);
        }
    }

    [Fact]
    public void TheCacheFileReadsBackAndRefusesAnotherInstallsAnotherBuildsOrADamagedOne()
    {
        var fp = Fp;
        string dir = Path.Combine(AppContext.BaseDirectory, "edit-marks-unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "edits.json");
            using (var fs = File.Create(file)) EditMarksCache.Write(fs, fp, EditMarks.From(Masks));
            var ids = Masks.Keys.ToHashSet();
            Assert.Equal(EditMarksCache.ReadResult.Ok, EditMarksCache.TryRead(file, fp, ids, out var back));
            Assert.Equal(Masks.OrderBy(k => k.Key), back!.Masks.OrderBy(k => k.Key));
            Assert.Empty(back.Unknown);
            // a clip the tool failed on kept as not told: [ID, -1], read back as not told (no marks, not known whether it allows an edit)
            string withUnknown = Path.Combine(dir, "unknown.json");
            using (var fs = File.Create(withUnknown)) EditMarksCache.Write(fs, fp, EditMarks.From(Masks.Where(k => k.Key != 0xB).ToDictionary(), unknown: [0xB]));
            Assert.Contains("[11,-1]", File.ReadAllText(withUnknown));
            Assert.Equal(EditMarksCache.ReadResult.Ok, EditMarksCache.TryRead(withUnknown, fp, ids, out var told));
            Assert.Equal(new[] { 0xBul }, told!.Unknown.ToArray());
            Assert.Equal(2, told.Count);
            File.WriteAllText(withUnknown, File.ReadAllText(withUnknown).Replace("[11,-1]", "[11,-2]"));
            Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(withUnknown, fp, ids, out _));
            // another install's files: the patch forge, its datafile's table, the base forge, the skeletons
            foreach (var other in new[]
                     {
                         fp with { Install = fp.Install with { ForgeLength = 124 } }, fp with { Install = fp.Install with { ForgeWriteTimeUtcTicks = 457 } },
                         fp with { Install = fp.Install with { DatafileTableSha256 = "ac" } }, fp with { BaseForgeLength = 1001 },
                         fp with { BaseForgeWriteTimeUtcTicks = 2001 }, fp with { SkeletonSha256 = "ce" },
                     })
                Assert.Equal(EditMarksCache.ReadResult.OtherFingerprint, EditMarksCache.TryRead(file, other, ids, out _));
            // the marks worked out another way: another format, or another build of the edits
            Assert.Equal(EditMarksCache.ReadResult.OtherFingerprint, EditMarksCache.TryRead(file, fp with { Method = $"{EditMarksFingerprint.FormatVersion + 1}/x" }, ids, out _));
            Assert.Equal(EditMarksCache.ReadResult.OtherFingerprint, EditMarksCache.TryRead(file, fp with { Method = $"{EditMarksFingerprint.FormatVersion}/{Guid.NewGuid():N}" }, ids, out _));
            // a clip the install doesn't hold, or one it holds missing
            Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(file, fp, ids.Where(i => i != 0xA).ToHashSet(), out _));
            Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(file, fp, ids.Append(0x99ul).ToHashSet(), out _));
            // cut short, every way; garbage; JSON of another kind; a mark out of range; a clip twice
            var bytes = File.ReadAllBytes(file);
            foreach (int cut in new[] { 0, 1, 10, bytes.Length / 3, bytes.Length / 2, bytes.Length - 2 })
            {
                File.WriteAllBytes(file, bytes[..cut]);
                Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(file, fp, ids, out _));
            }
            File.WriteAllBytes(file, [0xFF, 0xFE, 0, 1, 2]);
            Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(file, fp, ids, out _));
            File.WriteAllText(file, "{\"magic\": \"something else\", \"format\": 1}");
            Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(file, fp, ids, out _));
            // this cache's file in another format (an older build's, which held no clip not told): another build's, worked out again
            foreach (int other in new[] { 1, EditMarksCache.FileFormat + 1 })
            {
                File.WriteAllText(file, $"{{\"magic\": \"{EditMarksCache.Magic}\", \"format\": {other}, \"anything\": [1, 2]}}");
                Assert.Equal(EditMarksCache.ReadResult.OtherFingerprint, EditMarksCache.TryRead(file, fp, ids, out _));
            }
            string text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("[18,12]", text);
            File.WriteAllText(file, text.Replace("[18,12]", "[18,64]"));
            Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(file, fp, ids, out _));
            File.WriteAllText(file, text.Replace("[18,12]", "[10,12]"));
            Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(file, fp, ids, out _));
            File.WriteAllText(file, text.Replace("[18,12]", "[18,12,1]"));
            Assert.Equal(EditMarksCache.ReadResult.Unreadable, EditMarksCache.TryRead(file, fp, ids, out _));
            File.WriteAllText(file, text);
            Assert.Equal(EditMarksCache.ReadResult.Ok, EditMarksCache.TryRead(file, fp, ids, out _));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>On the game's files, through the cache's folder: built (the work given the progress) and kept, reused with no work, built
    /// again for other files or another build of the edits, and for a damaged file; one file per patch forge, beside the clip index's.</summary>
    [GameDataFact]
    public void OnTheGameTheCacheIsBuiltReusedAndBuiltAgain()
    {
        var g = f.Install;
        string dir = Path.Combine(AppContext.BaseDirectory, "edit-marks-game", Guid.NewGuid().ToString("N"));
        var cache = new EditMarksCache(dir);
        Assert.StartsWith(AppContext.BaseDirectory, cache.FileFor(g), StringComparison.OrdinalIgnoreCase);
        // the same name as the index's file of the same forge, but edits- for graph-
        Assert.Equal(Path.GetFileName(new ClipIndexCache(dir).FileFor(g)).Replace("graph-", "edits-"), Path.GetFileName(cache.FileFor(g)));
        int runs = 0;
        // three clips the tool failed on, kept as not told; the rest told
        var unknown = g.Clips.Take(3).Select(c => c.Id).ToList();
        var masks = g.Clips.Skip(3).ToDictionary(c => c.Id, c => (byte)(c.Index % 3 == 0 ? 0 : EditMarks.AllEdits));
        EditMarks Work()
        {
            runs++;
            return EditMarks.From(masks, unknown: unknown);
        }
        // as the program has them: read from the cache, else worked out and saved
        CachedEditMarks Open(Func<EditMarks> work) => cache.Read(g, out var outcome) ?? cache.Save(g, EditMarksFingerprint.Of(g), work(), outcome, TimeSpan.Zero);
        try
        {
            var built = Open(Work);
            Assert.Equal((CacheOutcome.Built, 1), (built.Outcome, runs));
            Assert.Null(built.SaveError);
            Assert.True(File.Exists(cache.FileFor(g)));
            Assert.True(new FileInfo(cache.FileFor(g)).Length < 400_000, $"{new FileInfo(cache.FileFor(g)).Length} B");
            // reused: no work, the same marks, the clips not told the same
            var reused = Open(Work);
            Assert.Equal(unknown.Order(), reused.Marks.Unknown.Order());
            Assert.All(g.Clips.Take(3), c => Assert.Null(reused.Marks.Any(c)));
            Assert.Equal((CacheOutcome.Reused, 1), (reused.Outcome, runs));
            Assert.Equal(masks.OrderBy(k => k.Key), reused.Marks.Masks.OrderBy(k => k.Key));
            Assert.True(reused.Time < TimeSpan.FromSeconds(2), $"read in {reused.Time.TotalSeconds:F2} s");
            // other files (the patch forge's length), or another build of the edits: built again, then reused
            var fp = EditMarksFingerprint.Of(g);
            // the forges as they stood when the install was opened, before it read them (a forge rewritten since keeps its old stamp here)
            Assert.Equal((new FileInfo(g.PatchForgePath).Length, new FileInfo(g.PatchForgePath).LastWriteTimeUtc.Ticks), g.PatchForgeStamp);
            Assert.Equal((new FileInfo(g.BaseForgePath).Length, new FileInfo(g.BaseForgePath).LastWriteTimeUtc.Ticks), g.BaseForgeStamp);
            Assert.Equal((g.PatchForgeStamp.Length, g.PatchForgeStamp.WriteTimeUtcTicks, g.BaseForgeStamp.Length, g.BaseForgeStamp.WriteTimeUtcTicks),
                (fp.Install.ForgeLength, fp.Install.ForgeWriteTimeUtcTicks, fp.BaseForgeLength, fp.BaseForgeWriteTimeUtcTicks));
            Assert.Equal(EditMarksFingerprint.ThisMethod, fp.Method);
            foreach (var other in new[] { fp with { Install = fp.Install with { ForgeLength = fp.Install.ForgeLength + 1 } }, fp with { Method = "1/another build" } })
            {
                using (var fs = File.Create(cache.FileFor(g))) EditMarksCache.Write(fs, other, reused.Marks);
                Assert.Equal(CacheOutcome.RebuiltChanged, Open(Work).Outcome);
                Assert.Equal(CacheOutcome.Reused, Open(Work).Outcome);
            }
            Assert.Equal(3, runs);
            // damaged: built again
            var bytes = File.ReadAllBytes(cache.FileFor(g));
            File.WriteAllBytes(cache.FileFor(g), bytes[..(bytes.Length / 2)]);
            Assert.Equal(CacheOutcome.RebuiltUnreadable, Open(Work).Outcome);
            Assert.Equal(CacheOutcome.Reused, Open(Work).Outcome);
            Assert.Equal(4, runs);
            Assert.Single(Directory.GetFiles(dir));
            // marks that may not hold next time (the graph didn't read), or that miss a clip neither told nor known not told, are shown but
            // never kept: the cache stays as it was, and the save error says why
            var before = File.ReadAllBytes(cache.FileFor(g));
            var fp2 = EditMarksFingerprint.Of(g);
            var partial = cache.Save(g, fp2, EditMarks.From(masks, "the game's animation graph couldn't be read", unknown), CacheOutcome.RebuiltChanged, TimeSpan.Zero);
            Assert.Equal("they may not hold next time: the game's animation graph couldn't be read", partial.SaveError);
            var missing = cache.Save(g, fp2, EditMarks.From(masks.Skip(1).ToDictionary(), unknown: unknown), CacheOutcome.RebuiltChanged, TimeSpan.Zero);
            Assert.Equal("they don't cover every clip in the game files", missing.SaveError);
            Assert.Equal(before, File.ReadAllBytes(cache.FileFor(g)));
            File.Delete(cache.FileFor(g));
            Assert.NotNull(Open(() => EditMarks.From(masks, "the swap sources couldn't be worked out", unknown)).SaveError);
            Assert.False(File.Exists(cache.FileFor(g)));
            Assert.Null(cache.Read(g, out var none));
            Assert.Equal(CacheOutcome.Built, none);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Two records of one clip ID (a modded datafile may hold them; the install takes the first): one clip to the marks, worked
    /// out once, the marks whole and kept. On the game's files, a few clips with one of them twice; the real forges' own records sharing an
    /// ID are said in the output.</summary>
    [GameDataFact]
    public void TwoRecordsOfOneIdAreOneClip()
    {
        var real = f.Install;
        using (var own = GameInstall.Open(GameData.Game!))
            foreach (var (which, g) in new[] { ("the install's own forge", own), ("the vanilla forge", real) })
                output.WriteLine($"{which}: {g.Clips.Count} clip records, {g.Clips.Count - g.Clips.Select(c => c.Id).Distinct().Count()} sharing an ID with an earlier one, "
                    + $"{ClipQuery.Families(g.Clips).Count} families in the list's family box");
        var clips = real.Clips.Where(c => c.Record is not null).Take(40).ToList();
        clips.Add(clips[0]);
        var ctor = typeof(GameInstall).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
        var twice = (GameInstall)ctor.Invoke([real.GameFolder, real.PatchForgePath, real.BaseForgePath, real.Oodle, real.AnimationEntry, real.Records.ToList(), clips,
            real.SharedTable, real.BodySkeleton, real.AddOnSkeleton, real.Names, real.Vanilla, real.Timings]);
        string dir = Path.Combine(AppContext.BaseDirectory, "edit-marks-twice", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(40, InstallEditChoices.ClipsOf(twice).Count);
            var marks = Within.Seconds(300, () => EditMarks.BuildAsync(twice, Core.Graph.ClipGraph.Read(real), SwapSources.Build(real.Clips, real.SharedTable),
                new Core.Edits.SkeletonMirror(real.Skeleton), null, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Null(marks.Incomplete);
            Assert.Equal(40, marks.Count + marks.Unknown.Count);
            var cache = new EditMarksCache(dir);
            Assert.Null(cache.Save(twice, EditMarksFingerprint.Of(twice), marks, CacheOutcome.Built, TimeSpan.Zero).SaveError);
            Assert.Equal(CacheOutcome.Reused, cache.Read(twice, out _)!.Outcome);
        }
        finally
        {
            // (never disposed: its library is the real install's)
            Core.IO.FolderGuard.Forget(twice);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>On the game's files: a build cancelled while it runs (another install opened) ends soon with a cancellation, its threads
    /// gone, and keeps nothing; one whose graph didn't read says its marks are incomplete, so they aren't kept.</summary>
    [GameDataFact]
    public void OnTheGameACancelledBuildEndsAndKeepsNothing()
    {
        var g = f.Install;
        string dir = Path.Combine(AppContext.BaseDirectory, "edit-marks-cancel", Guid.NewGuid().ToString("N"));
        var cache = new EditMarksCache(dir);
        try
        {
            var swaps = SwapSources.Build(g.Clips, g.SharedTable);
            var mirror = new Core.Edits.SkeletonMirror(g.Skeleton);
            var graph = Core.Graph.ClipGraph.Read(g);
            var reports = new Reports();
            using var cts = new CancellationTokenSource();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var build = Task.Run(() => EditMarks.BuildAsync(g, graph, swaps, mirror, reports, cts.Token));
            // once it has started on the clips
            while (reports.Values.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(20);
            Assert.NotEmpty(reports.Values);
            cts.Cancel();
            var stopped = System.Diagnostics.Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => build.GetAwaiter().GetResult());
            Assert.True(stopped.Elapsed < TimeSpan.FromSeconds(5), $"the build took {stopped.Elapsed.TotalSeconds:F1} s to stop");
            Assert.True(reports.Values.Max() < 1, "the build ran to its end");
            // (the build's task ends only when its last worker thread has left its loop: the workers are gone)
            Assert.False(File.Exists(cache.FileFor(g)));
            // without the graph: every clip worked out, but incomplete, so not kept (as the program saves them)
            var noGraph = Within.Seconds(600, () => EditMarks.BuildAsync(g, null, swaps, mirror, null, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal("the game's animation graph couldn't be read", noGraph.Incomplete);
            Assert.Equal(g.Clips.Count, noGraph.Count);
            Assert.NotNull(cache.Save(g, EditMarksFingerprint.Of(g), noGraph, CacheOutcome.Built, TimeSpan.Zero).SaveError);
            Assert.False(File.Exists(cache.FileFor(g)));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
