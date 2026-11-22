using GrbAnimTool.Core.Library;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>The light and dark palettes, the remembered theme and <c>--theme</c>.</summary>
public class ThemeTests(ITestOutputHelper output)
{
    public static TheoryData<string> Palettes => new() { "light", "dark", "hc-black", "hc-white", "hc-aquatic", "hc-desert" };

    /// <summary>The light and dark palettes, and the high-contrast palette built from the system colours (window, text, grey text,
    /// highlight, highlight text) of four of Windows' contrast themes: High Contrast Black and White, Aquatic and Desert.</summary>
    static ThemePalette Palette(string name) => name switch
    {
        "dark" => ThemePalette.Dark,
        "hc-black" => ThemePalette.HighContrast(0xFF000000, 0xFFFFFFFF, 0xFF3FF23F, 0xFF1AEBFF, 0xFF000000),
        "hc-white" => ThemePalette.HighContrast(0xFFFFFFFF, 0xFF000000, 0xFF600000, 0xFF37006E, 0xFFFFFFFF),
        "hc-aquatic" => ThemePalette.HighContrast(0xFF202020, 0xFFFFFFFF, 0xFFA6A6A6, 0xFF8EE3F0, 0xFF263B50),
        "hc-desert" => ThemePalette.HighContrast(0xFFFFFAEF, 0xFF3D3D3D, 0xFF676767, 0xFF903909, 0xFFFFF5E3),
        _ => ThemePalette.Light,
    };

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryTextColourHasContrastOfAtLeast4Point5OnEveryBackgroundItIsDrawnOn(string name)
    {
        var p = Palette(name);
        var low = new List<string>();
        double lowest = double.MaxValue;
        string lowestPair = "";
        foreach (var (fg, bg) in ThemePalette.TextPairs)
        {
            Assert.True(p.Colours.ContainsKey(fg), $"{name} has no colour {fg}");
            Assert.True(p.Colours.ContainsKey(bg), $"{name} has no colour {bg}");
            double r = ThemePalette.Contrast(p[fg], p[bg]);
            if (r < lowest) { lowest = r; lowestPair = $"{fg} on {bg}"; }
            if (r < 4.5) low.Add($"{fg} {p[fg]:X8} on {bg} {p[bg]:X8}: {r:F2}");
        }
        output.WriteLine($"{name}: lowest {lowest:F2} ({lowestPair}) over {ThemePalette.TextPairs.Count} pairs");
        Assert.True(low.Count == 0, string.Join("; ", low));
    }

    /// <summary>The light and dark palettes' greyed-out text (under high contrast Windows' own grey text decides).</summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void GreyedOutTextStillReadsAsGreyedBesidePlainText(string name)
    {
        var p = Palette(name);
        double apart = ThemePalette.Contrast(p["TextDisabled"], p["Text"]);
        output.WriteLine($"{name}: greyed-out text {p["TextDisabled"]:X8} against plain text {p["Text"]:X8}: {apart:F2}");
        Assert.True(apart >= ThemePalette.GreyedApart, $"{name}: {apart:F2}");
    }

    [Fact]
    public void BothPalettesNameTheSameOpaqueColours()
    {
        Assert.Equal(ThemePalette.Light.Colours.Keys.Order(), ThemePalette.Dark.Colours.Keys.Order());
        Assert.Equal(ThemePalette.Light.Colours.Keys.Order(), Palette("hc-black").Colours.Keys.Order());
        foreach (var p in new[] { ThemePalette.Light, ThemePalette.Dark, Palette("hc-white") })
            foreach (var (k, v) in p.Colours) Assert.True(v >> 24 == 0xFF, $"{p.Name} {k} isn't opaque");
        // a selected row's text in the light and dark themes is the plain and the secondary text, so it reads as the rest of the list
        foreach (var p in new[] { ThemePalette.Light, ThemePalette.Dark })
        {
            Assert.Equal(p["Text"], p["SelectedText"]);
            Assert.Equal(p["TextSecondary"], p["SelectedTextSecondary"]);
        }
        // the 3D view stays dark in both themes, so the skeletons read the same in each
        Assert.Equal(ThemePalette.Light["ViewBack"], ThemePalette.Dark["ViewBack"]);
        Assert.True(ThemePalette.Luminance(ThemePalette.Dark["ViewBack"]) < 0.05);
    }

    [Fact]
    public void TheContrastFormulaGivesWcagsKnownValues()
    {
        Assert.Equal(21, ThemePalette.Contrast(0xFF000000, 0xFFFFFFFF), 6);
        Assert.Equal(1, ThemePalette.Contrast(0xFF777777, 0xFF777777), 6);
        // #767676 on white is the usual 4.54:1 grey
        Assert.Equal(4.54, ThemePalette.Contrast(0xFF767676, 0xFFFFFFFF), 2);
    }

    [Fact]
    public void TheThemeIsRememberedAndAnUnknownOneFollowsWindows()
    {
        Assert.Equal(ThemeChoice.System, new ViewerSettings().ThemeChoice);
        foreach (var c in Enum.GetValues<ThemeChoice>())
        {
            var s = new ViewerSettings { GameFolder = "g", Theme = ThemeChoices.Name(c) };
            var back = ViewerSettings.FromJson(s.ToJson());
            Assert.Equal(s, back);
            Assert.Equal(c, back.ThemeChoice);
            Assert.DoesNotContain("ThemeChoice", s.ToJson());
        }
        // a name the tool doesn't know keeps the game folder and follows Windows
        var odd = ViewerSettings.FromJson("{ \"GameFolder\": \"g\", \"Theme\": \"purple\" }");
        Assert.Equal("g", odd.GameFolder);
        Assert.Equal(ThemeChoice.System, odd.ThemeChoice);
        Assert.Equal(ThemeChoice.Dark, ViewerSettings.FromJson("{ \"Theme\": \"Dark\" }").ThemeChoice);
        // a settings file from before the theme setting
        Assert.Equal(ThemeChoice.System, ViewerSettings.FromJson("{ \"GameFolder\": \"g\" }").ThemeChoice);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{ \"name\": \"dark\" }")]
    [InlineData("[ \"dark\" ]")]
    [InlineData("null")]
    public void ASettingThatIsntTextLosesOnlyItself(string value)
    {
        var s = ViewerSettings.FromJson($"{{ \"GameFolder\": \"g\", \"Theme\": {value} }}");
        Assert.Equal("g", s.GameFolder);
        Assert.Null(s.Theme);
        Assert.Equal(ThemeChoice.System, s.ThemeChoice);
        var t = ViewerSettings.FromJson($"{{ \"GameFolder\": {value}, \"Theme\": \"dark\" }}");
        Assert.Null(t.GameFolder);
        Assert.Equal(ThemeChoice.Dark, t.ThemeChoice);
    }

    [Fact]
    public void TheThemeIsSavedBesideTheGameFolder()
    {
        // a folder of the test's own beside the test assembly, deleted afterwards
        string dir = Path.Combine(AppContext.BaseDirectory, "theme-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "GrbAnimTool", "settings.json");
            // the theme saved over a remembered game folder, as the Settings menu saves it
            new ViewerSettings { GameFolder = "g" }.Save(path);
            (ViewerSettings.Load(path) with { Theme = ThemeChoices.Name(ThemeChoice.Dark) }).Save(path);
            var s = ViewerSettings.Load(path);
            Assert.Equal("g", s.GameFolder);
            Assert.Equal(ThemeChoice.Dark, s.ThemeChoice);
            // and a game folder saved over a remembered theme, as the Library screen saves it when a folder is opened
            new ViewerSettings { Theme = ThemeChoices.Name(ThemeChoice.Light) }.Save(path);
            (ViewerSettings.Load(path) with { GameFolder = "h" }).Save(path);
            var t = ViewerSettings.Load(path);
            Assert.Equal("h", t.GameFolder);
            Assert.Equal(ThemeChoice.Light, t.ThemeChoice);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("dark", ThemeChoice.Dark)]
    [InlineData("Light", ThemeChoice.Light)]
    [InlineData("system", ThemeChoice.System)]
    public void TheCommandLineTakesATheme(string value, ThemeChoice expected)
    {
        var a = ViewerArgs.Parse(["--theme", value]);
        Assert.Equal(expected, a.Theme);
        Assert.True(a.Automated);   // a run with --theme neither reads nor writes the remembered settings
        Assert.Null(ViewerArgs.Parse([]).Theme);
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--theme", "purple"]));
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--theme"]));
    }

    [Fact]
    public void APictureMaySwitchTheTheme()
    {
        var a = ViewerArgs.Parse(["--game", "g", "--theme", "light", "--shot", "a.png||||library", "--shot", "b.png||||library||||DARK",
            "--shot", "c.png||||edit||||"]);
        Assert.Equal(ThemeChoice.Light, a.Theme);
        Assert.Equal(new ThemeChoice?[] { null, ThemeChoice.Dark, null }, a.Shots.Select(s => s.Theme));
        Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(["--game", "g", "--shot", "a.png||||library||||dim"]));
    }
}
