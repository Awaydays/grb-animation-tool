namespace GrbAnimTool.Core.Library;

/// <summary>The folder the tool keeps its own files in between runs: the settings, the clip index cache, the package list and the
/// error log. It is <c>%APPDATA%\GrbAnimTool</c>, or the folder named by the environment variable <see cref="Variable"/> when that is set.
/// The variable is for tests: every test run sets it, so no test ever reads or writes the user's own folder. This is the one place the
/// tool asks Windows for the user's application data folder.</summary>
public static class ToolData
{
    public const string Variable = "GRBANIM_APPDATA";

    /// <summary>The folder for this run: <see cref="FolderFor"/> of the variable as it stands now.</summary>
    public static string Folder => FolderFor(Environment.GetEnvironmentVariable(Variable));

    /// <summary>The folder the variable's value <paramref name="value"/> gives: that folder in full when it is set, else the user's own.</summary>
    public static string FolderFor(string? value) => string.IsNullOrWhiteSpace(value)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrbAnimTool")
        : Path.GetFullPath(value);
}
