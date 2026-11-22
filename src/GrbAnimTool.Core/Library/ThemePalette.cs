namespace GrbAnimTool.Core.Library;

/// <summary>The window's theme: Windows' own app setting (light or dark, followed when it changes while the program runs), or always
/// light, or always dark.</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>The theme choices by the names the settings file and <c>--theme</c> use: system, light, dark.</summary>
public static class ThemeChoices
{
    public static string Name(ThemeChoice c) => c switch { ThemeChoice.Light => "light", ThemeChoice.Dark => "dark", _ => "system" };

    /// <summary>The choice a name gives, in any case; null for anything else.</summary>
    public static ThemeChoice? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "system" => ThemeChoice.System,
        "light" => ThemeChoice.Light,
        "dark" => ThemeChoice.Dark,
        _ => null,
    };
}

/// <summary>The colours the window draws with beside the Fluent theme's own controls, one palette per theme, as 0xAARRGGBB, every one
/// opaque. The viewer turns each into a brush keyed "Grb." and its name, and gives the Fluent theme's own brushes for greyed-out text and
/// for a list's hovered and selected rows the palette's colours too. The backgrounds include the Fluent theme's own surfaces that the
/// palette's text is drawn on (its window, control and hover fills, made opaque over the window), so <see cref="TextPairs"/> covers each
/// text colour on everything under it. The 3D view's background is dark in both themes, so the skeletons read the same in each.</summary>
public sealed class ThemePalette
{
    public string Name { get; }
    public IReadOnlyDictionary<string, uint> Colours { get; }

    ThemePalette(string name, Dictionary<string, uint> colours) { Name = name; Colours = colours; }

    public uint this[string key] => Colours[key];

    public static ThemePalette For(bool dark) => dark ? Dark : Light;

    public static readonly ThemePalette Light = new("light", new()
    {
        // backgrounds: the window, a control or list (the Fluent control fill over the window), a hovered row, a list's selected row,
        // the error strip, the timeline's strips, the 3D view, the check badges
        ["Surface"] = 0xFFF3F3F3, ["Field"] = 0xFFFBFBFB, ["Raised"] = 0xFFE6E6E6, ["Selected"] = 0xFFCCE4F7, ["ErrorBack"] = 0xFFFDE7E9,
        ["Strip"] = 0xFFE6E6E6, ["ViewBack"] = 0xFF2B2D31, ["PassBadge"] = 0xFF1E7A34, ["WarnBadge"] = 0xFF8A5A00, ["FailBadge"] = 0xFFB42318,
        // text: plain, labels and notes, a control that can't be used (greyed, yet readable), the red reasons and errors, the amber
        // warnings, the green results, the two lines of a selected row, a badge's word, the 3D view's readout and hint
        ["Text"] = 0xFF1A1A1A, ["TextSecondary"] = 0xFF595959, ["TextDisabled"] = 0xFF666666, ["Error"] = 0xFFA4262C, ["Warning"] = 0xFF7A4F00,
        ["Success"] = 0xFF2E5E2E, ["SelectedText"] = 0xFF1A1A1A, ["SelectedTextSecondary"] = 0xFF595959,
        ["BadgeText"] = 0xFFFFFFFF, ["ViewText"] = 0xFFE8E8E8, ["ViewHint"] = 0xFFB8B8B8,
        // the marks on the timeline's strips: events, the left and the right foot's plants, the play head, the trim's kept span
        ["StripEvent"] = 0xFFA04E00, ["StripLeft"] = 0xFF006A80, ["StripRight"] = 0xFF1F4E8C, ["StripHead"] = 0xFF202020, ["TrimKept"] = 0xFF276B35,
        // lines only: borders and splitters, and a slider that can't be used (its track, and its filled part and thumb)
        ["ErrorBorder"] = 0xFFD9534F, ["StripBorder"] = 0xFFC8C8C8, ["Splitter"] = 0xFFDADADA, ["ViewBorder"] = 0xFFB0B0B0, ["ListBorder"] = 0xFFC8C8C8,
        ["DisabledTrack"] = 0xFFD2D2D2, ["DisabledMark"] = 0xFFA3A3A3,
    });

    public static readonly ThemePalette Dark = new("dark", new()
    {
        ["Surface"] = 0xFF202020, ["Field"] = 0xFF2D2D2D, ["Raised"] = 0xFF3A3A3A, ["Selected"] = 0xFF1F3A55, ["ErrorBack"] = 0xFF442726,
        ["Strip"] = 0xFF2B2B2B, ["ViewBack"] = 0xFF2B2D31, ["PassBadge"] = 0xFF6CCB7F, ["WarnBadge"] = 0xFFFFC857, ["FailBadge"] = 0xFFFF8A8A,
        ["Text"] = 0xFFFFFFFF, ["TextSecondary"] = 0xFFC8C8C8, ["TextDisabled"] = 0xFFA6A6A6, ["Error"] = 0xFFFF99A4, ["Warning"] = 0xFFFFC857,
        ["Success"] = 0xFF7FD18B, ["SelectedText"] = 0xFFFFFFFF, ["SelectedTextSecondary"] = 0xFFC8C8C8,
        ["BadgeText"] = 0xFF1A1A1A, ["ViewText"] = 0xFFE8E8E8, ["ViewHint"] = 0xFFB8B8B8,
        ["StripEvent"] = 0xFFF0902A, ["StripLeft"] = 0xFF3CC8DC, ["StripRight"] = 0xFF5A9BE0, ["StripHead"] = 0xFFF0F0F0, ["TrimKept"] = 0xFF5BB86C,
        ["ErrorBorder"] = 0xFFC0504D, ["StripBorder"] = 0xFF454545, ["Splitter"] = 0xFF3A3A3A, ["ViewBorder"] = 0xFF505050, ["ListBorder"] = 0xFF454545,
        ["DisabledTrack"] = 0xFF464646, ["DisabledMark"] = 0xFF767676,
    });

    /// <summary>The palette under Windows' high contrast: the window's surfaces in its window colour, every text in its text colour (a
    /// greyed-out one in its grey text colour), a selected row and the check badges in its highlight colours, the strips' marks in its
    /// highlight and text colours; the 3D view keeps the palette of its brightness (light or dark), being drawn by the tool, not by
    /// Windows. The arguments are Windows' system colours as 0xAARRGGBB.</summary>
    public static ThemePalette HighContrast(uint window, uint windowText, uint grayText, uint highlight, uint highlightText)
    {
        window |= 0xFF000000; windowText |= 0xFF000000; grayText |= 0xFF000000; highlight |= 0xFF000000; highlightText |= 0xFF000000;
        var c = new Dictionary<string, uint>((Luminance(window) < 0.2 ? Dark : Light).Colours);
        foreach (var k in new[] { "Surface", "Field", "Raised", "ErrorBack", "Strip" }) c[k] = window;
        foreach (var k in new[] { "Text", "TextSecondary", "Error", "Warning", "Success", "StripLeft", "StripRight", "StripHead", "ErrorBorder",
                     "StripBorder", "Splitter", "ViewBorder", "ListBorder", "DisabledTrack" }) c[k] = windowText;
        foreach (var k in new[] { "Selected", "PassBadge", "WarnBadge", "FailBadge", "StripEvent", "TrimKept" }) c[k] = highlight;
        foreach (var k in new[] { "SelectedText", "SelectedTextSecondary", "BadgeText" }) c[k] = highlightText;
        c["TextDisabled"] = grayText;
        c["DisabledMark"] = grayText;
        return new ThemePalette("high contrast", c);
    }

    /// <summary>Every foreground the window draws on a background of the palette, as the names of the two: each text colour, the
    /// greyed-out one included, on the window, a control and a hovered row; both lines of a list's selected row on its fill (selected
    /// and hovered alike); the errors on the error strip; a badge's word on each badge; the 3D view's text on it; the strip's marks on
    /// the strip.</summary>
    public static readonly IReadOnlyList<(string Foreground, string Background)> TextPairs =
    [
        .. from f in new[] { "Text", "TextSecondary", "TextDisabled", "Error", "Warning", "Success" }
           from b in new[] { "Surface", "Field", "Raised" }
           select (f, b),
        ("SelectedText", "Selected"), ("SelectedTextSecondary", "Selected"),
        ("Error", "ErrorBack"),
        ("BadgeText", "PassBadge"), ("BadgeText", "WarnBadge"), ("BadgeText", "FailBadge"),
        ("ViewText", "ViewBack"), ("ViewHint", "ViewBack"),
        .. from f in new[] { "StripEvent", "StripLeft", "StripRight", "StripHead", "TrimKept" } select (f, "Strip"),
    ];

    /// <summary>The least contrast ratio between greyed-out text and plain text, so a control that can't be used still reads as greyed
    /// beside one that can (while its text keeps 4.5:1 on its background).</summary>
    public const double GreyedApart = 2.0;

    /// <summary>A colour's relative luminance by the WCAG formula: each sRGB channel linearised, then 0.2126 R + 0.7152 G + 0.0722 B.</summary>
    public static double Luminance(uint argb)
    {
        static double Channel(uint v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel((argb >> 16) & 0xFF) + 0.7152 * Channel((argb >> 8) & 0xFF) + 0.0722 * Channel(argb & 0xFF);
    }

    /// <summary>The WCAG contrast ratio of two opaque colours: (the lighter's luminance + 0.05) / (the darker's + 0.05), 1 to 21.</summary>
    public static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
