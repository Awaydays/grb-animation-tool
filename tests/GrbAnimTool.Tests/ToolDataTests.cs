using System.Runtime.CompilerServices;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.Tests;

/// <summary>Every run of this assembly keeps the tool's own files in a folder of the run's own (<see cref="ToolData.Variable"/>), set
/// before any test runs, so no test can read or write the user's settings, caches or package list.</summary>
static class TestToolData
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "tool-data-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Set()
    {
        Directory.CreateDirectory(Folder);
        Environment.SetEnvironmentVariable(ToolData.Variable, Folder);
    }

    /// <summary>The repository's root (the folder holding the solution), from the test's own folder up; null when the tests run outside it.</summary>
    public static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GrbAnimTool.slnx"))) return d.FullName;
        return null;
    }

    /// <summary>The source files that ask Windows for the user's application data folder, or read the APPDATA variable, by path under
    /// <c>src</c>.</summary>
    public static List<string> AppDataReaders(string root) =>
        Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f) is var t && (t.Contains("SpecialFolder.ApplicationData") || t.Contains("SpecialFolder.LocalApplicationData")
                || t.Contains("\"APPDATA\"")))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
}

public class ToolDataTests
{
    /// <summary>The proof that a test run never reaches the user's own folder: the run's override is set; every place the tool keeps its
    /// files is under it and not under the user's folder; and in the whole source, the one file that asks Windows for that folder is
    /// <see cref="ToolData"/>, which gives it only when the override is unset.</summary>
    [Fact]
    public void ATestRunNeverReachesTheUsersOwnFolder()
    {
        string users = ToolData.FolderFor(null);
        string Under(string p) => Path.GetFullPath(p).TrimEnd('\\') + "\\";
        // the control: without the override, the folder is the user's (a path only, nothing read)
        Assert.StartsWith(Under(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)), Under(users), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TestToolData.Folder, Environment.GetEnvironmentVariable(ToolData.Variable));
        Assert.Equal(Path.GetFullPath(TestToolData.Folder), ToolData.Folder);
        foreach (var p in new[] { ViewerSettings.DefaultPath, ClipIndexCache.DefaultFolder, PackageStore.DefaultFolder })
        {
            Assert.StartsWith(Under(TestToolData.Folder), Under(p), StringComparison.OrdinalIgnoreCase);
            Assert.False(Under(p).StartsWith(Under(users), StringComparison.OrdinalIgnoreCase), p);
        }
        string root = TestToolData.RepoRoot() ?? throw new InvalidOperationException("the tests run outside the repository: the source can't be searched");
        // ToolData's own line is the search's positive control: a search that finds nothing is broken, not a pass
        Assert.Equal(["src/GrbAnimTool.Core/Library/ToolData.cs"], TestToolData.AppDataReaders(root));
    }

    [Fact]
    public void TheOverrideIsTakenInFull()
    {
        Assert.Equal(Path.GetFullPath("rel-folder"), ToolData.FolderFor("rel-folder"));
        Assert.Equal(ToolData.FolderFor(null), ToolData.FolderFor("  "));
    }
}
