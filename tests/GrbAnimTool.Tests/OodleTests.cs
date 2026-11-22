using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Forge;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Oodle;

namespace GrbAnimTool.Tests;

/// <summary>The Oodle pin: a DLL whose hash isn't pinned is refused before it is loaded. The test files are made in a folder of their own
/// beside the test assembly; the game's DLL is only ever read.</summary>
public sealed class OodleTests : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "oodle-pin-" + Guid.NewGuid().ToString("N"));

    public OodleTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    string FakeDll()
    {
        // not a DLL at all: a copy of this test assembly's dependency file, named as the game's DLL
        string src = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.deps.json").First();
        string dll = Path.Combine(_dir, OodleLibrary.DllName);
        File.Copy(src, dll);
        return dll;
    }

    [Fact]
    public void AMissingDllIsReported()
    {
        var ex = Assert.Throws<OodleException>(() => OodleLibrary.Load(_dir));
        Assert.Contains("is missing", ex.Message);
    }

    [Fact]
    public void AnUnpinnedFileIsRefusedBeforeLoading()
    {
        string dll = FakeDll();
        var ex = Assert.Throws<OodleException>(() => OodleLibrary.Load(_dir));
        Assert.Contains("is not a version this tool supports", ex.Message);
        Assert.Contains(Sha256Hex.OfFile(dll), ex.Message);
        Assert.Contains("it was not loaded", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void APinnedHashLetsTheFileThroughToTheLoader()
    {
        // the control of the test above: pinning this file's own hash passes the gate, and the loader then fails on a file that isn't a DLL
        string dll = FakeDll();
        var ex = Assert.Throws<OodleException>(() => OodleLibrary.Load(_dir, [Sha256Hex.OfFile(dll)]));
        Assert.Contains("does not load", ex.Message);
    }

    [Fact]
    public void ARelativeFolderIsResolvedToTheSameFile()
    {
        // the folder given relative to the current directory: the file hashed is the one in that folder, and the loader is handed its full path
        string dll = FakeDll();
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, _dir);
        var refused = Assert.Throws<OodleException>(() => OodleLibrary.Load(relative));
        Assert.Contains(Sha256Hex.OfFile(dll), refused.Message);
        var failed = Assert.Throws<OodleException>(() => OodleLibrary.Load(relative, [Sha256Hex.OfFile(dll)]));
        Assert.Contains("does not load", failed.Message);
    }

    [Fact]
    public void ThePinListHoldsTodaysDll() =>
        Assert.Contains("abc990aa59898fc6fa154239347250fe698f5b7a7857dd968796e4545db7bdef", OodleLibrary.PinnedSha256);

    [GameDataFact]
    public void TheGamesDllLoads()
    {
        using var o = OodleLibrary.Load(GameData.Game!);
        Assert.Contains(o.Sha256, OodleLibrary.PinnedSha256);
        Assert.Throws<InvalidDataException>(() => o.Decompress([1, 2, 3, 4, 5, 6, 7, 8], new byte[100]));
    }

    /// <summary>Dispose while eight threads decompress: Dispose waits for the calls in flight, then unloads; every call either decoded the
    /// chunk right or was refused with ObjectDisposedException, and every call after Dispose is refused.</summary>
    [GameDataFact]
    public void DisposeWaitsForDecodesInFlight()
    {
        byte[] comp, want;
        using (var fs = ForgeReader.OpenRead(GameData.Forge!))
        {
            var payload = ForgeReader.ReadPayload(fs, ForgeReader.Read(fs).Find(SharedCurveTable.EntryId)!);
            var chunk = CompressedFileData.ReadBlocks(payload, out _).SelectMany(b => b.Chunks).First(c => c.Compressed);
            comp = payload.AsSpan(chunk.DataOffset, (int)chunk.Stored).ToArray();
            want = new byte[chunk.Uncompressed];
        }
        var o = OodleLibrary.Load(GameData.Game!);
        o.Decompress(comp, want);
        int ok = 0, refused = 0;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            var raw = new byte[want.Length];
            go.Wait();
            while (true)
            {
                try
                {
                    o.Decompress(comp, raw);
                    if (!raw.AsSpan().SequenceEqual(want)) { errors.Enqueue("decoded differently"); return; }
                    Interlocked.Increment(ref ok);
                }
                catch (ObjectDisposedException) { Interlocked.Increment(ref refused); return; }
                catch (Exception ex) { errors.Enqueue(ex.ToString()); return; }
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        go.Set();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref ok) < 400 && clock.Elapsed < TimeSpan.FromSeconds(30)) Thread.Yield();
        o.Dispose();
        Assert.All(threads, t => Assert.True(t.Join(TimeSpan.FromSeconds(30)), "a decoding thread did not stop"));
        Assert.Empty(errors);
        Assert.True(ok >= 400, $"only {ok} decodes before Dispose");
        Assert.Equal(8, refused);
        Assert.Throws<ObjectDisposedException>(() => o.Decompress(comp, new byte[want.Length]));
        o.Dispose();   // a second Dispose does nothing
    }
}
