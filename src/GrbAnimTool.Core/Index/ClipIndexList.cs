using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace GrbAnimTool.Core.Index;

/// <summary>One abbreviation of the clip names and what it means, from the tool's list; <paramref name="Search"/>: the entry's own
/// words for the clip search ("draw" for get, "left shoulder" for LHS), where its meaning's head is too long to search, or null. Several
/// such words are a phrase (<see cref="Library.SearchFieldKind.Phrase"/>), or a compound when they are compass words only ("north-east",
/// <see cref="Library.SearchFieldKind.Compound"/>).</summary>
public sealed record GlossaryEntry(string Abbreviation, string Meaning, string? Search = null);

/// <summary>One clip of the tool's list: its readable name and categories, each optional.</summary>
public sealed record ListedClip(ulong Id, string? Name, IReadOnlyList<string> Who, IReadOnlyList<string> What);

/// <summary>The list shipped with the tool (<c>clip-index.json</c>, built into the core): readable names and categories for clips, and a
/// glossary of the names' abbreviations, in the tool's own words. Only the entries marked sure are kept for use; one marked
/// <c>"sure": false</c> stays in the file for its maintainers but is left out of the categories, the names and the search.
/// <para>Format 1, UTF-8 JSON: <c>{"format": 1, "glossary": [{"abbr", "meaning", "search"?, "sure", "note"?}], "clips": [{"id": "0x" + 16 hex digits,
/// "name"?, "who"?: [...], "what"?: [...], "sure", "note"?}]}</c>, categories from the vocabulary (<see cref="ClipCategories"/>). A file
/// that isn't of that form is refused whole (<see cref="Error"/>); within one that is, an entry or a category that breaks the form is left
/// out and noted in <see cref="Problems"/>, and an entry without <c>sure</c> counts as unsure.</para></summary>
public sealed class ClipIndexList
{
    public const string ResourceName = "GrbAnimTool.Core.clip-index.json";
    public const int Format = 1;

    /// <summary>The sure glossary, by abbreviation, case ignored.</summary>
    public IReadOnlyDictionary<string, GlossaryEntry> Glossary { get; }
    /// <summary>The sure clips, by ID.</summary>
    public IReadOnlyDictionary<ulong, ListedClip> Clips { get; }
    /// <summary>The entries marked unsure (or not marked), left out.</summary>
    public int UnsureClips { get; }
    public int UnsureGlossary { get; }
    /// <summary>What was left out of a file of the right form, one line each.</summary>
    public IReadOnlyList<string> Problems { get; }
    /// <summary>Why the whole file was refused, or null when it was read.</summary>
    public string? Error { get; }

    ClipIndexList(Dictionary<string, GlossaryEntry> g, Dictionary<ulong, ListedClip> c, int uc, int ug, List<string> p, string? error)
    {
        Glossary = g; Clips = c; UnsureClips = uc; UnsureGlossary = ug; Problems = p; Error = error;
    }

    /// <summary>An empty list (no file), or one refused for <paramref name="error"/>.</summary>
    public static ClipIndexList Empty(string? error = null) => new(new(StringComparer.OrdinalIgnoreCase), [], 0, 0, [], error);

    /// <summary>The list built into the core, or null when this build holds none. A file that doesn't read comes back refused
    /// (<see cref="Error"/> says why), never as an exception.</summary>
    public static ClipIndexList? Embedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return s is null ? null : Read(s);
    }

    /// <summary>Reads a list; a file not of the form comes back refused, with the reason in <see cref="Error"/>.</summary>
    public static ClipIndexList Read(Stream s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            return Read(doc.RootElement);
        }
        catch (JsonException ex) { return Empty($"the tool's list isn't JSON: {ex.Message}"); }
        catch (InvalidDataException ex) { return Empty($"the tool's list isn't of format {Format}: {ex.Message}"); }
    }

    static ClipIndexList Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("it isn't a JSON object");
        if (!root.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.Number || !f.TryGetInt32(out int format) || format != Format)
            throw new InvalidDataException("its \"format\" isn't 1");
        var problems = new List<string>();
        var glossary = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        int unsureG = 0, unsureC = 0;
        foreach (var (e, n) in Entries(root, "glossary"))
        {
            string? abbr = Text(e, "abbr"), meaning = Text(e, "meaning");
            if (abbr is null || meaning is null || abbr.Any(char.IsWhiteSpace)) { problems.Add($"glossary entry {n}: needs an \"abbr\" of one word and a \"meaning\""); continue; }
            if (!Sure(e)) { unsureG++; continue; }
            // its own search words, optional: a "search" that isn't a text is left out (the meaning's head is searched instead)
            string? search = Text(e, "search");
            if (search is null && e.TryGetProperty("search", out _)) problems.Add($"glossary entry {n}: \"search\" isn't a text; left out");
            if (!glossary.TryAdd(abbr, new GlossaryEntry(abbr, meaning, search))) problems.Add($"glossary entry {n}: \"{abbr}\" stands twice; the first is kept");
        }
        var clips = new Dictionary<ulong, ListedClip>();
        foreach (var (e, n) in Entries(root, "clips"))
        {
            string? idText = Text(e, "id");
            if (idText is null || idText.Length != 18 || !idText.StartsWith("0x", StringComparison.Ordinal)
                || !ulong.TryParse(idText.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong id))
            {
                problems.Add($"clip entry {n}: \"id\" isn't 0x and 16 hex digits");
                continue;
            }
            if (!Sure(e)) { unsureC++; continue; }
            string? name = null;
            if (e.TryGetProperty("name", out var nm))
            {
                name = nm.ValueKind == JsonValueKind.String ? nm.GetString()!.Trim() : null;
                if (string.IsNullOrEmpty(name)) { name = null; problems.Add($"clip {idText}: \"name\" isn't a text; left out"); }
            }
            var who = Categories(e, "who", CategoryKind.Who, idText, problems);
            var what = Categories(e, "what", CategoryKind.What, idText, problems);
            if (!clips.TryAdd(id, new ListedClip(id, name, who, what))) problems.Add($"clip {idText} stands twice; the first is kept");
        }
        return new ClipIndexList(glossary, clips, unsureC, unsureG, problems, null);
    }

    static IEnumerable<(JsonElement, int)> Entries(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var a)) yield break;
        if (a.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"its \"{name}\" isn't a list");
        int n = 0;
        foreach (var e in a.EnumerateArray())
        {
            n++;
            if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"entry {n} of \"{name}\" isn't an object");
            yield return (e, n);
        }
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>Sure only when <c>"sure": true</c> stands; missing or anything else is unsure.</summary>
    static bool Sure(JsonElement e) => e.TryGetProperty("sure", out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A list of categories of one kind, each from the vocabulary (Uncategorised isn't a placement), once each.</summary>
    static List<string> Categories(JsonElement e, string name, CategoryKind kind, string id, List<string> problems)
    {
        var l = new List<string>();
        if (!e.TryGetProperty(name, out var a)) return l;
        if (a.ValueKind != JsonValueKind.Array) { problems.Add($"clip {id}: \"{name}\" isn't a list; left out"); return l; }
        foreach (var v in a.EnumerateArray())
        {
            string? c = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (c is null || !ClipCategories.IsIn(kind, c) || c == ClipCategories.Uncategorised)
                problems.Add($"clip {id}: \"{name}\" holds {(c is null ? "a value that isn't a text" : $"\"{c}\"")}, not a {kind.ToString().ToLowerInvariant()}-category; left out");
            else if (!l.Contains(c)) l.Add(c);
        }
        return l;
    }
}
