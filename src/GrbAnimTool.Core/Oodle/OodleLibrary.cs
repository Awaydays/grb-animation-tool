// GRB Anim Tool. Copyright (C) 2026 Awaydays. Licensed under GPL-3.0-or-later (see LICENSE), with an additional permission under section 7 (see LICENSE-EXCEPTION.md).
using System.Runtime.InteropServices;
using GrbAnimTool.Core.Hashing;

namespace GrbAnimTool.Core.Oodle;

/// <summary>The game's Oodle library is missing, unreadable, not a pinned one, does not load, or failed its control decode. An unknown
/// DLL is never loaded.</summary>
public sealed class OodleException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Oodle decompression through the game's own oo2core_7_win64.dll, loaded at run time from the user's game folder. The DLL is
/// never shipped or copied. It is loaded only when its SHA-256 is one of <see cref="PinnedSha256"/>, because loading a DLL runs its code.</summary>
public sealed unsafe class OodleLibrary : IDisposable
{
    public const string DllName = "oo2core_7_win64.dll";

    /// <summary>The SHA-256 of every oo2core_7_win64.dll this tool loads. One line per game version's DLL.</summary>
    public static readonly string[] PinnedSha256 =
    [
        "abc990aa59898fc6fa154239347250fe698f5b7a7857dd968796e4545db7bdef",   // the game's DLL since its update of 2026-09-29 (1,032,184 B)
        // the older game version's DLL (1,026,088 B, the 2023 files) goes here, once its SHA-256 has been taken from such an install
    ];

    const int ExtraBuffer = 64 * 1024;   // the output buffer is the chunk's size plus 64 KiB, room the decoder may write into

    readonly IntPtr _lib;
    readonly delegate* unmanaged<byte*, long, byte*, long, int, int, int, void*, long, void*, void*, void*, long, int, long> _decompress;
    // Decompress may run on several threads at once. Each call counts itself in under the gate; Dispose marks the library disposed, waits
    // until no call is in flight, and only then unloads the DLL, so a running call never loses the code it is executing.
    readonly object _gate = new();
    int _inFlight;
    bool _disposed;

    /// <summary>The DLL's full path and SHA-256.</summary>
    public string DllPath { get; }
    public string Sha256 { get; }

    OodleLibrary(string path, string sha)
    {
        DllPath = path;
        Sha256 = sha;
        _lib = NativeLibrary.Load(path);
        try
        {
            _decompress = (delegate* unmanaged<byte*, long, byte*, long, int, int, int, void*, long, void*, void*, void*, long, int, long>)
                NativeLibrary.GetExport(_lib, "OodleLZ_Decompress");
        }
        catch
        {
            NativeLibrary.Free(_lib);
            throw;
        }
    }

    static string Text(string gameFolder, string what) =>
        $"The game's Oodle library {DllName} {what} (game folder: {gameFolder}). The game files may be incomplete or from a version this tool "
        + "does not know. Verify the game files in Steam or Ubisoft Connect, then try again.";

    /// <summary>The game's own DLL from <paramref name="gameFolder"/>: never copied, never looked for elsewhere. It is hashed first and
    /// loaded only if it matches a pin. The file stays open with FileShare.Read from hashing until NativeLibrary.Load has mapped it, which
    /// on a local disk stops it being rewritten or replaced in between. The load opens the file by name a second time, so in a folder
    /// served by another computer the two opens aren't bound to one another.</summary>
    public static OodleLibrary Load(string gameFolder) => Load(gameFolder, PinnedSha256);

    internal static OodleLibrary Load(string gameFolder, IReadOnlyCollection<string> pins)
    {
        // A full path, so the file hashed and held below is the very file the loader maps: a relative path would be resolved against the
        // current directory here but by the system's own rules in LoadLibrary.
        string dll;
        try { dll = Path.GetFullPath(Path.Combine(gameFolder, DllName)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new OodleException(Text(gameFolder, $"cannot be found ({ex.Message})"), ex); }
        if (!File.Exists(dll)) throw new OodleException(Text(gameFolder, "is missing"));
        FileStream held;
        try { held = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new OodleException(Text(gameFolder, $"cannot be read ({ex.Message})"), ex); }
        using (held)
        {
            string actual;
            try { actual = Sha256Hex.Of(held); }
            catch (IOException ex) { throw new OodleException(Text(gameFolder, $"cannot be read ({ex.Message})"), ex); }
            if (!pins.Contains(actual, StringComparer.OrdinalIgnoreCase))
                throw new OodleException(Text(gameFolder, $"is not a version this tool supports (SHA-256 {actual}); it was not loaded"));
            try { return new OodleLibrary(dll, actual); }   // loaded while 'held' is still open
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            { throw new OodleException(Text(gameFolder, $"does not load ({ex.Message})"), ex); }
        }
    }

    /// <summary>One chunk: OodleLZ_Decompress(comp, compLen, raw, rawLen, fuzzSafe 1, checkCRC 0, verbosity 0, null, 0, null, null, null, 0,
    /// threadPhase 3). The fuzz-safe mode makes the decoder check its input; the output buffer is padded so the decoder never writes past it.
    /// Throws <see cref="InvalidDataException"/> unless exactly <paramref name="raw"/>'s length was decoded, and
    /// <see cref="ObjectDisposedException"/> once <see cref="Dispose"/> has begun. Safe to call from several threads at once.</summary>
    public void Decompress(ReadOnlySpan<byte> comp, Span<byte> raw)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (comp.Length == 0) throw new InvalidDataException("empty compressed chunk");
            _inFlight++;
        }
        byte[]? buf = null;
        try
        {
            buf = System.Buffers.ArrayPool<byte>.Shared.Rent(raw.Length + ExtraBuffer);
            long got;
            fixed (byte* c = comp)
            fixed (byte* r = buf)
                got = _decompress(c, comp.Length, r, raw.Length, 1, 0, 0, null, 0, null, null, null, 0, 3);
            if (got != raw.Length) throw new InvalidDataException($"OodleLZ_Decompress returned {got}, expected {raw.Length}");
            buf.AsSpan(0, raw.Length).CopyTo(raw);   // only the bytes this call decoded: a pooled buffer holds an earlier call's after them
        }
        finally
        {
            if (buf is not null) System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            lock (_gate) { if (--_inFlight == 0) Monitor.PulseAll(_gate); }
        }
    }

    /// <summary>Unloads the DLL once every <see cref="Decompress"/> in flight has returned; calls made after Dispose has begun throw
    /// <see cref="ObjectDisposedException"/>. Must not be called from inside a Decompress call.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_inFlight > 0) Monitor.Wait(_gate);
        }
        NativeLibrary.Free(_lib);
    }
}
