using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrbAnimTool.Core.Library;

/// <summary>What the viewer remembers between runs: the last game folder opened, the theme chosen, the vanilla forge backup the
/// Package screen takes originals from, how the previews time the original and the folder the Log screen follows. Stored as JSON
/// (settings.json in the user's application-data folder, under GrbAnimTool).</summary>
public sealed record ViewerSettings
{
    [JsonConverter(typeof(TextOrNull))]
    public string? GameFolder { get; init; }

    /// <summary>The theme chosen, by its name (<see cref="ThemeChoices"/>); null, or a name the tool doesn't know, follows Windows.</summary>
    [JsonConverter(typeof(TextOrNull))]
    public string? Theme { get; init; }

    /// <summary>The vanilla backup of the patch forge the Package screen takes originals from (null: none).</summary>
    [JsonConverter(typeof(TextOrNull))]
    public string? BackupForge { get; init; }

    /// <summary>The Package screen's save folder chosen under Settings (null: none, the folder last built in).</summary>
    [JsonConverter(typeof(TextOrNull))]
    public string? PackageFolder { get; init; }

    /// <summary>The save folder the last Build wrote into, the Package screen's start without <see cref="PackageFolder"/>.</summary>
    [JsonConverter(typeof(TextOrNull))]
    public string? LastPackageFolder { get; init; }

    /// <summary>The Package screen's save folder at start: <see cref="PackageFolder"/>, else <see cref="LastPackageFolder"/>, else
    /// <paramref name="fallback"/>.</summary>
    public string StartPackageFolder(string fallback) =>
        PackageFolder is { Length: > 0 } p ? p : LastPackageFolder is { Length: > 0 } l ? l : fallback;

    /// <summary>The folder the Log screen follows the newest clip log in (null: the logger's own folder in the Documents folder).</summary>
    [JsonConverter(typeof(TextOrNull))]
    public string? LogFolder { get; init; }

    /// <summary>The Edit and Import previews' "Original follows the edit's progress" (null: never chosen, off).</summary>
    [JsonConverter(typeof(FlagOrNull))]
    public bool? OriginalFollowsEdit { get; init; }

    /// <summary>A yes-or-no setting read leniently: anything but true or false reads as null, so it loses only itself.</summary>
    sealed class FlagOrNull : JsonConverter<bool?>
    {
        public override bool HandleNull => true;

        public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteBooleanValue(value.Value);
        }
    }

    /// <summary>A setting's text read leniently: a value that isn't a string (a number, true, an object, ...) reads as null, so one
    /// setting written wrong loses only itself, not the other settings of the file.</summary>
    sealed class TextOrNull : JsonConverter<string?>
    {
        public override bool HandleNull => true;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value);
        }
    }

    /// <summary>The theme to use: <see cref="Theme"/>'s choice, else <see cref="ThemeChoice.System"/>.</summary>
    [JsonIgnore]
    public ThemeChoice ThemeChoice => ThemeChoices.Parse(Theme) ?? ThemeChoice.System;

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The settings in <paramref name="json"/>; empty settings when it is empty or not settings JSON.</summary>
    public static ViewerSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<ViewerSettings>(json, Options) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>The settings file's usual place: %APPDATA%\GrbAnimTool\settings.json (<see cref="ToolData"/>).</summary>
    public static string DefaultPath => Path.Combine(ToolData.Folder, "settings.json");

    /// <summary>The settings in <paramref name="path"/>; empty settings when the file is missing or can't be read.</summary>
    public static ViewerSettings Load(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>The settings in <paramref name="path"/> for a change to be saved over them: empty settings when there is no file; for a
    /// file that isn't settings JSON (a hand edit gone wrong), empty settings once the file is kept aside (<see cref="SetAsideIfUnreadable"/>),
    /// with what was done in <paramref name="why"/>; false, with why, when the file is there but can't be opened now (another program
    /// holds it), so a save doesn't write over the settings it holds.</summary>
    public static bool TryLoad(string path, out ViewerSettings settings, out string? why)
    {
        settings = new();
        why = null;
        try
        {
            if (!File.Exists(path)) return true;
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return true;
            settings = JsonSerializer.Deserialize<ViewerSettings>(json, Options) ?? new();
            return true;
        }
        catch (JsonException)
        {
            settings = new();
            why = SetAsideIfUnreadable(path);
            if (why is not null) return true;
            why = $"{path} isn't settings of this tool's form, and couldn't be kept aside";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            settings = new();
            why = $"{path} is in use or can't be opened now: {ex.Message}";
            return false;
        }
    }

    /// <summary>The start of the name a settings file that isn't settings JSON is kept under, beside it.</summary>
    public const string UnreadableName = "settings.unreadable-";

    /// <summary>A settings file at <paramref name="path"/> that isn't settings JSON is kept beside it (<see cref="UnreadableName"/>, the
    /// date and time, a number when that is taken; never written over; not again when the newest such copy holds the same bytes), so the
    /// tool can start with its default settings and save them. Returns what was done, naming both files; null when the file reads, isn't
    /// there, can't be opened now, or couldn't be kept aside.</summary>
    public static string? SetAsideIfUnreadable(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            try
            {
                // read as Load reads it: a byte order mark (UTF-8, UTF-16 or UTF-32) taken for the encoding it names, not as text
                string text;
                using (var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true)) text = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(text)) return null;
                JsonSerializer.Deserialize<ViewerSettings>(text, Options);
                return null;
            }
            catch (JsonException) { }
            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var newest = Directory.GetFiles(folder, UnreadableName + "*.json").Select(f => new FileInfo(f)).OrderBy(f => f.LastWriteTimeUtc).LastOrDefault();
            string kept;
            if (newest is not null && File.ReadAllBytes(newest.FullName).AsSpan().SequenceEqual(bytes)) kept = newest.FullName;
            else
            {
                string stem = Path.Combine(folder, UnreadableName + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture));
                kept = stem + ".json";
                for (int k = 2; File.Exists(kept); k++) kept = $"{stem}_{k}.json";
                Export.ClipExport.WriteFile(kept, [], s => s.Write(bytes), makeFolder: false);
            }
            return $"The settings file {path} couldn't be read (it isn't settings of this tool's form), so the tool goes on with its default "
                + $"settings and saves them there from now on; the file as it was is kept as {kept}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Export.ExportException) { return null; }
    }

    /// <summary>Writes the settings to <paramref name="path"/>, making its folder, refusing a path in the game folder the settings name
    /// (see <see cref="Save(string, IEnumerable{string})"/>). Throws an <see cref="IOException"/> on an IO error or a refusal.</summary>
    public void Save(string path) => Save(path, GameFolder is { Length: > 0 } g ? new[] { g } : Array.Empty<string>());

    /// <summary>Writes the settings to <paramref name="path"/> as every output of the tool is written (<see cref="Export.ClipExport.WriteFile"/>):
    /// guarded against <paramref name="protectedFolders"/> before its folder is made and again after, whole to a temporary file of its own
    /// beside it, then moved over the path, so a link standing at the name is replaced, never written through. Throws an
    /// <see cref="IOException"/> on an IO error or a refusal.</summary>
    public void Save(string path, IEnumerable<string> protectedFolders)
    {
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(ToJson());
        try { Export.ClipExport.WriteFile(path, protectedFolders, s => s.Write(bytes), makeFolder: true); }
        catch (Export.ExportException ex) { throw new IOException(ex.Message, ex); }
    }
}
