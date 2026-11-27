using System.IO;
using System.Runtime.CompilerServices;
using GrbAnimTool.Core.Index;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App.Tests;

/// <summary>Every run of this assembly keeps the tool's own files in a folder of the run's own (<see cref="ToolData.Variable"/>), set
/// before any test runs, so no test can read or write the user's settings, caches, package list or error log.</summary>
static class TestToolData
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "tool-data-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Set()
    {
        Directory.CreateDirectory(Folder);
        Environment.SetEnvironmentVariable(ToolData.Variable, Folder);
    }
}

public class ToolDataTests
{
    /// <summary>The screens' places for the tool's files, the error log among them, are under the run's own folder, never the user's
    /// (the source search is in the core's tests).</summary>
    [Fact]
    public void TheScreensKeepTheirFilesInTheRunsOwnFolder()
    {
        string Under(string p) => Path.GetFullPath(p).TrimEnd('\\') + "\\";
        string users = ToolData.FolderFor(null);
        Assert.Equal(TestToolData.Folder, Environment.GetEnvironmentVariable(ToolData.Variable));
        foreach (var p in new[] { ErrorReport.DefaultLogPath, ViewerSettings.DefaultPath, ClipIndexCache.DefaultFolder, PackageStore.DefaultFolder })
        {
            Assert.StartsWith(Under(TestToolData.Folder), Under(p), StringComparison.OrdinalIgnoreCase);
            Assert.False(Under(p).StartsWith(Under(users), StringComparison.OrdinalIgnoreCase), p);
        }
    }
}
