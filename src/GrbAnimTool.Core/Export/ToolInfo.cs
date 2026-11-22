using System.Reflection;

namespace GrbAnimTool.Core.Export;

/// <summary>The tool's name and version as the exported files carry them.</summary>
public static class ToolInfo
{
    public const string Name = "GRB Anim Tool";

    /// <summary>The core library's informational version, without any source revision suffix.</summary>
    public static string Version { get; } = ReadVersion();

    static string ReadVersion()
    {
        var a = typeof(ToolInfo).Assembly;
        string? v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString();
        if (string.IsNullOrEmpty(v)) return "0";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }
}
