using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Tests;

/// <summary>Where the game data for the tests is: GRBANIM_FORGE names a vanilla DataPC_patch_01.forge, GRBANIM_GAME the game folder (its
/// Oodle DLL and DataPC.forge). Tests that need them skip when they are not set.</summary>
public static class GameData
{
    public static string? Forge => Existing(Environment.GetEnvironmentVariable("GRBANIM_FORGE"), File.Exists);
    public static string? Game => Existing(Environment.GetEnvironmentVariable("GRBANIM_GAME"), Directory.Exists);
    public static bool Available => Forge is not null && Game is not null;
    public const string SkipText = "game data not configured: set GRBANIM_FORGE (a vanilla DataPC_patch_01.forge) and GRBANIM_GAME (the game folder)";

    static string? Existing(string? p, Func<string, bool> exists) => !string.IsNullOrWhiteSpace(p) && exists(p) ? p : null;
}

/// <summary>A test that needs the game data; it skips cleanly without it.</summary>
public sealed class GameDataFactAttribute : FactAttribute
{
    public GameDataFactAttribute() { if (!GameData.Available) Skip = GameData.SkipText; }
}

/// <summary>A test of several cases that needs the game data; it skips cleanly without it.</summary>
public sealed class GameDataTheoryAttribute : TheoryAttribute
{
    public GameDataTheoryAttribute() { if (!GameData.Available) Skip = GameData.SkipText; }
}

/// <summary>The vanilla install, opened once for every test that uses it, and its clip index (built from the graph with the shipped list,
/// no cache), built once on first use.</summary>
public sealed class InstallFixture : IDisposable
{
    readonly Lazy<GameInstall> _g = new(() => GameInstall.Open(GameData.Game!, GameData.Forge!));
    readonly Lazy<ClipIndex> _index;
    public InstallFixture() =>
        _index = new(() => ClipIndex.Build(Install, GraphFacts.Read(Install.Records, Install.Clips.Select(c => c.Id)), ClipIndexList.Embedded()));
    public GameInstall Install => _g.Value;
    public ClipIndex Index => _index.Value;
    public void Dispose() { if (_g.IsValueCreated) _g.Value.Dispose(); }
}

[CollectionDefinition("install")]
public sealed class InstallCollection : ICollectionFixture<InstallFixture>;

/// <summary>Runs work with a time limit, so a decoder that loops forever fails the test instead of hanging it.</summary>
public static class Within
{
    public static T Seconds<T>(double s, Func<T> f)
    {
        var t = Task.Run(f);
        if (!t.Wait(TimeSpan.FromSeconds(s))) throw new TimeoutException($"did not finish within {s} s");
        return t.GetAwaiter().GetResult();
    }
}
