using System.IO;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.App;

/// <summary>The Edit and Import previews' "Original follows the edit's progress", one choice for both screens: off unless the settings
/// file says on, and remembered there when a person changes it. A person's run uses the user's settings file; an automated run only the
/// one <c>--settings</c> names (none without it: nothing is read or written); a host that doesn't start the program (the tests) none
/// until it says one.</summary>
static class PreviewChoices
{
    static bool? _follows;

    /// <summary>The settings file the choice is read from and remembered in (null: none). Setting it reads the choice again from it.</summary>
    public static string? SettingsPath
    {
        get => _settingsPath;
        set { _settingsPath = value; _follows = null; }
    }

    static string? _settingsPath;

    /// <summary>Whether the original follows the edit's progress (read from the settings once; off when none say it).</summary>
    public static bool OriginalFollowsEdit => _follows ??= _settingsPath is { } p && ViewerSettings.Load(p).OriginalFollowsEdit == true;

    /// <summary>The choice changed (on either screen, or for a picture): each screen shows it.</summary>
    public static event Action? Changed;

    /// <summary>Sets the choice for both screens; with <paramref name="remember"/>, writes it to the settings file, guarded as every
    /// settings save is (never in <paramref name="protect"/> nor the game folder the settings name). Returns the save's error, or null.</summary>
    public static string? Set(bool on, bool remember, IEnumerable<string> protect)
    {
        bool changed = OriginalFollowsEdit != on;
        _follows = on;
        string? error = null;
        if (remember && _settingsPath is { } p)
        {
            // a settings file that can't be read now isn't written over (the other settings in it would be lost)
            if (!ViewerSettings.TryLoad(p, out var kept, out var why)) error = $"The preview's choice couldn't be remembered: the settings file couldn't be read ({why}).";
            else
            {
                var s = kept with { OriginalFollowsEdit = on };
                var guard = new List<string>(protect);
                if (s.GameFolder is { Length: > 0 } f) guard.Add(f);
                try { s.Save(p, guard); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    error = $"The preview's choice couldn't be remembered: {ex.Message}" + (why is null ? "" : $" {why}");
                }
                // a settings file that wasn't settings, kept aside before this save: said
                error ??= why;
            }
        }
        if (changed) Changed?.Invoke();
        return error;
    }
}
