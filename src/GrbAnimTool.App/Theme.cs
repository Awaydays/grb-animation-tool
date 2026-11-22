using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Library;
using Microsoft.Win32;

namespace GrbAnimTool.App;

/// <summary>The window's theme: WPF's own Fluent theme through <see cref="Application.ThemeMode"/> (Light, Dark, or System, which
/// follows Windows' app setting and its changes), and the tool's own colours (<see cref="ThemePalette"/>) as brushes in the
/// application's resources, keyed "Grb." and the colour's name, which the screens take as dynamic resources so a change of theme
/// recolours them in place. Under Windows' high contrast the palette is Windows' own system colours.</summary>
static class Theme
{
    public static ThemeChoice Choice { get; private set; }

    /// <summary>Whether the theme shown is dark: the choice, or under <see cref="ThemeChoice.System"/> Windows' app setting.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Whether Windows' high contrast is on: the palette is then made of Windows' system colours.</summary>
    public static bool IsHighContrast { get; private set; }

    /// <summary>Sets the theme for every window and the palette that goes with it.</summary>
    public static void Apply(ThemeChoice choice)
    {
        Choice = choice;
        // ThemeMode is marked experimental (WPF0001) in .NET 10's WPF: it is the supported way to apply the built-in Fluent theme,
        // and the warning only says its shape may still change in a later release
#pragma warning disable WPF0001
        Application.Current.ThemeMode = choice switch
        {
            ThemeChoice.Light => ThemeMode.Light,
            ThemeChoice.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
        Refresh();
    }

    /// <summary>The tool's styles, built on the theme's, and the palette of the theme shown, put into the application's resources.
    /// Called again when Windows' light or dark setting, its high contrast or its colours change: the Fluent theme then puts in styles
    /// of its own again, which the tool's must be built on anew.</summary>
    public static void Refresh()
    {
        IsHighContrast = SystemParameters.HighContrast;
        Restyle();
        IsDark = Choice switch { ThemeChoice.Dark => true, ThemeChoice.Light => false, _ => WindowsAppsAreDark() };
        var palette = IsHighContrast
            ? ThemePalette.HighContrast(Argb(SystemColors.WindowColor), Argb(SystemColors.WindowTextColor), Argb(SystemColors.GrayTextColor),
                Argb(SystemColors.HighlightColor), Argb(SystemColors.HighlightTextColor))
            : ThemePalette.For(IsDark);
        var res = Application.Current.Resources;
        foreach (var (name, argb) in palette.Colours)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            res[Key(name)] = b;
        }
        // the theme's own brushes that the tool gives its palette's colours: put in the application's own resources, they come before
        // the theme's dictionaries, and the theme's templates take them as dynamic resources
        foreach (var (fluentKey, name) in FluentBrushes) res[fluentKey] = res[Key(PaletteName(fluentKey, name, IsHighContrast))];
        // the lists' rows as tight as before the theme (its rows are about twice as tall): no least height, no gap between rows, a
        // list box's rows with a small padding
        res["GridViewItemContainerMinHeight"] = 0.0;
        res["ListViewItemMinHeight"] = 0.0;
        res["ListViewItemMargin"] = new Thickness(0);
        res["ListBoxItemPadding"] = new Thickness(4, 1, 4, 1);
        res["GridViewColumnHeaderMinHeight"] = 0.0;
        // a check's badge is filled with the highlight colour under high contrast, as a selected row is: there an outline in the
        // highlight's text colour keeps it marked on a selected row; in light and dark it has none
        res[Key("BadgeOutline")] = res[Key("BadgeText")];
        res[Key("BadgeOutlineWidth")] = new Thickness(IsHighContrast ? 1 : 0);
    }

    /// <summary>The Fluent theme's brushes the tool replaces by a colour of its palette. Greyed-out text: the theme's is 2.5:1 (light) or
    /// 3.2:1 (dark) on the window, under the 4.5:1 every text of the tool keeps; the palette's still reads as greyed beside plain text.
    /// A list's rows: their text, a hovered row, and a list box's selected row, which the theme fills with its accent colour (its text
    /// on that 4.3:1, a row's second line in the palette's secondary colour 1.6:1).</summary>
    public static readonly IReadOnlyList<(string FluentKey, string Palette)> FluentBrushes =
    [
        .. new[]
        {
            "TextFillColorDisabledBrush", "ButtonForegroundDisabled", "RadioButtonForegroundDisabled", "CheckBoxForegroundUncheckedDisabled",
            "CheckBoxCheckGlyphForegroundDisabled", "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled",
            "TextControlForegroundDisabled", "RepeatButtonForegroundDisabled", "ToggleButtonForegroundDisabled", "ExpanderHeaderDisabledForeground",
            "HyperlinkForegroundDisabled",
        }.Select(k => (k, "TextDisabled")),
        ("ListBoxItemForeground", "Text"), ("ListViewItemForeground", "Text"),
        ("ListBoxItemSelectedForegroundThemeBrush", "SelectedText"),
        ("ListBoxItemSelectedBackgroundThemeBrush", "Selected"), ("ListBoxItemSelectedBackgroundPointerOverThemeBrush", "Selected"),
        ("ListBoxItemSelectedBackgroundPressedThemeBrush", "Selected"), ("ListBoxItemBackgroundSelectedPressedThemeBrush", "Selected"),
        ("ListBoxItemUnselectedBackgroundPointerOverThemeBrush", "Raised"),
        ("ListViewItemBackgroundPointerOver", "Raised"), ("ListViewItemBackgroundSelectedPointerOver", "Raised"),
        // a greyed accent button (the screens' main action before it can be used): the theme's grey fill leaves its greyed text at 3.1:1
        // (light) or 4.1:1 (dark); the palette's raised fill and greyed text keep 4.5:1
        ("AccentButtonBackgroundDisabled", "Raised"), ("AccentButtonForegroundDisabled", "TextDisabled"),
    ];

    /// <summary>The palette colour a theme's brush of <see cref="FluentBrushes"/> takes. Under Windows' high contrast a list's hovered
    /// and selected row (one brush for its fill in both states) takes Windows' highlight colour, as the palette's Raised is then the
    /// window's own colour and would leave the row unmarked; its text then takes the highlight's text colour (the row style's triggers,
    /// see <see cref="Restyle()"/>).</summary>
    public static string PaletteName(string fluentKey, string name, bool highContrast) =>
        highContrast && fluentKey is "ListViewItemBackgroundPointerOver" or "ListViewItemBackgroundSelectedPointerOver" ? "Selected" : name;

    /// <summary>The tool's implicit styles, each built on the theme's own (an implicit style alone would replace the theme's, leaving
    /// the control unthemed).</summary>
    static void Restyle()
    {
        // the tooltips' width limit; the theme's check boxes and radio buttons are at least 120 wide, which the timeline's row and the
        // edits' panel can't spare
        Restyle(typeof(ToolTip), new Setter(FrameworkElement.MaxWidthProperty, 420.0));
        Restyle(typeof(CheckBox), new Setter(FrameworkElement.MinWidthProperty, 0.0));
        Restyle(typeof(RadioButton), new Setter(FrameworkElement.MinWidthProperty, 0.0));
        // a text of a part that can't be used (the value beside a greyed slider, a label of a greyed box) greyed as the controls are; in
        // the application's resources the style also reaches the texts inside the controls' templates
        Restyle(typeof(TextBlock), new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(Key("TextDisabled"))) },
        });
        // the theme's slider looks the same whether it can be used or not: one that can't is drawn in the palette's greyed colours
        var greyed = new MultiTrigger { Setters = { new Setter(Control.TemplateProperty, Application.Current.FindResource("Grb.DisabledSlider")) } };
        greyed.Conditions.Add(new Condition(UIElement.IsEnabledProperty, false));
        greyed.Conditions.Add(new Condition(Slider.OrientationProperty, Orientation.Horizontal));
        Restyle(typeof(Slider), greyed);
        // a combo box's text closer to its left edge (the theme's 12 is the narrow boxes' room for their longest item), the theme's
        // own top, right and bottom kept
        var pad = Application.Current.TryFindResource("ComboBoxPadding") is Thickness t ? t : new Thickness(12, 5, 0, 7);
        Restyle(typeof(ComboBox), new Setter(Control.PaddingProperty, new Thickness(ComboBoxLeftPadding, pad.Top, pad.Right, pad.Bottom)));
        // a list's hovered or selected row has its text in the selected row's colour: the same as plain text in light and dark, and
        // under high contrast the highlight's text colour on the highlight the row is then filled with (PaletteName); and the row has no
        // padding of its own, so its cells' text lines up with its column headers' (CompactHeaders)
        Setter RowText() => new(Control.ForegroundProperty, new DynamicResourceExtension(Key("SelectedText")));
        RestyleKeyed(GridView.GridViewItemContainerStyleKey, typeof(ListViewItem),
            new Setter(Control.PaddingProperty, new Thickness(0)),
            new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true, Setters = { RowText() } },
            new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { RowText() } });
        // a list box's hovered row that isn't selected: under high contrast the theme fills it with the window's own colour, which
        // leaves it unmarked, so it takes a border in the highlight colour (a fill of it would hide the row's text, which stays the
        // window's text colour); in light and dark the theme's own style stays, its fill marking the row
        if (IsHighContrast)
        {
            var hover = new MultiTrigger { Setters = { new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(Key("Selected"))) } };
            hover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
            hover.Conditions.Add(new Condition(ListBoxItem.IsSelectedProperty, false));
            Restyle(typeof(ListBoxItem), hover);
        }
        else Application.Current.Resources.Remove(typeof(ListBoxItem));
    }

    /// <summary>A combo box's text's distance from its left edge (the theme's is 12).</summary>
    public const double ComboBoxLeftPadding = 8;

    /// <summary>An implicit style of the tool's own for <paramref name="type"/> built on the theme's current one, with these setters and
    /// triggers.</summary>
    static void Restyle(Type type, params object[] parts) => RestyleKeyed(type, type, parts);

    /// <summary>The style under <paramref name="key"/> (a type's implicit style, or a style the theme keys, such as a list's rows
    /// in a grid view) replaced by the tool's own for <paramref name="type"/>, built on the theme's current one, with these setters and
    /// triggers.</summary>
    static void RestyleKeyed(object key, Type type, params object[] parts)
    {
        var res = Application.Current.Resources;
        res.Remove(key);
        var s = new Style(type, Application.Current.TryFindResource(key) as Style);
        foreach (var p in parts)
            if (p is SetterBase setter) s.Setters.Add(setter);
            else s.Triggers.Add((TriggerBase)p);
        res[key] = s;
    }

    /// <summary>A list's column headers with a small padding (the theme's 12 on each side cuts short words such as "Status" in the
    /// narrow columns the tool's lists had before the theme), each header made a <see cref="GridViewColumnHeader"/> to hold it. Its left
    /// padding is where its column's cells start their text: the row's 1 border and the 6 WPF leaves before each cell.</summary>
    public static void CompactHeaders(ListView list)
    {
        foreach (var column in ((GridView)list.View).Columns)
        {
            if (column.Header is not GridViewColumnHeader h) column.Header = h = new GridViewColumnHeader { Content = column.Header };
            h.Padding = new Thickness(7, 0, 4, 0);
        }
    }

    /// <summary>The resource key of a palette colour.</summary>
    public static string Key(string name) => "Grb." + name;

    /// <summary>The resource key of a check level's text colour: green for pass, amber for warn, red for fail.</summary>
    public static string TextKeyOf(CheckLevel level) => Key(level switch { CheckLevel.Pass => "Success", CheckLevel.Warn => "Warning", _ => "Error" });

    static uint Argb(Color c) => (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;

    /// <summary>Windows' app setting, as the Fluent theme's System mode reads it (no value: light).</summary>
    static bool WindowsAppsAreDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }
}
