using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Library;

/// <summary>How a <see cref="SearchField"/> is matched.</summary>
public enum SearchFieldKind
{
    /// <summary>Text split into words (see <see cref="ClipSearch.Words"/>): each query word is matched whole, at a word's start, inside a
    /// word, across words or with a typo.</summary>
    Words,
    /// <summary>A clip ID as 16 hex digits: matched only as a run of hex digits, by a query that starts with 0x or is eight or more hex
    /// digits, never by words or with typos.</summary>
    HexId,
    /// <summary>Text written in words apart, such as a readable name or a category: matched as <see cref="Words"/>, but never across
    /// words at random. A name runs its words together (ImWatchingYou), so a query may too; in "Soldier death, electrocuted" the words
    /// stand apart, and "hel" across "death electrocuted" is no match. Two of its neighbouring words typed together as one query word of
    /// <see cref="ClipSearch.RunTogetherLength"/> letters or more do match ("deaththreat" for "Death threat"), and an English word's
    /// inflection matches its plain form ("walking" for walk, <see cref="ClipSearch.Inflected"/>).</summary>
    Prose,
    /// <summary><see cref="Prose"/> whose words stand together, such as "left shoulder": its last word counts alone, the words before it
    /// only together with it. The field counts for a query (of one token or several) only when a token matches its last word, in any
    /// way prose matches (whole, a start, inside, a typo, an inflection, or across two of its words typed together: "leftshoulder");
    /// then every word of it counts, as prose. So "shoulder", "shoulder aim" and "left shoulder" find it, "left" and "aim left" never
    /// through it. Phrases with the same last word stand against each other by the words before it: where the query holds such a word
    /// of another one as a whole word ("right" of "right shoulder") and matches none of this one's, this one doesn't count, so "right
    /// shoulder" brings in no left-shoulder clip through a name that says right; a word typed only so far ("shoulder ri") stands
    /// against nothing yet.</summary>
    Phrase,
    /// <summary><see cref="Prose"/> whose words each count alone, such as the compass's "north-east", where either word is a direction
    /// of its own: the field counts for a query when a token matches any of its words, in any way a phrase's last word is matched, so
    /// "north", "walk north", "east" and "north east" all find it. Compounds that share a word stand against each other by their other
    /// words, as phrases do: where the query holds a word of another one as a whole word ("east" of "north-east") that this one doesn't
    /// hold, this one doesn't count, so "north east" brings in no north-west clip and "south east" no north-east clip.</summary>
    Compound,
}

/// <summary>One text of a clip that the search looks in, with its weight: a match's score is multiplied by it, so a clip's name (1) can
/// count more than, say, a category word (less than 1). The weight must be above 0. <paramref name="Bonus"/>: what a match of a query
/// word here adds when another field holds that word's best match (0 to <see cref="ClipSearch.MostBonus"/>; see the search's notes).</summary>
public sealed record SearchField(string Text, double Weight = 1, SearchFieldKind Kind = SearchFieldKind.Words, double Bonus = ClipSearch.FurtherField);

/// <summary>A clip the search found, with its score (higher is a better match).</summary>
public readonly record struct SearchHit(ClipEntry Clip, double Score);

/// <summary>The search box's matcher over a list of clips. Each clip's fields (by default its name and its hex ID, see
/// <see cref="NameAndId"/>) are split into words once, when the search is built. A query word is then scored once against the list's
/// distinct words (about a thousand on an install) and each clip looks up its own words' scores, so a keystroke costs a few
/// milliseconds on an install's 9,938 clips.
/// <para><b>Words.</b> Underscores, dashes, spaces and every other character that is neither a letter nor a digit break words, and so do
/// a lower-case letter followed by a capital (IsExhausted: is, exhausted), the last capital of a run followed by a lower-case letter
/// (STDInj: std, inj) and a change between letters and digits (V0: v, 0). Case is ignored, and so are accents and other combining
/// marks; compatibility forms such as full-width letters or ligatures read as their plain letters (NFKC). The query is split the same
/// way, and every query word must match in the clip (the words in any order).</para>
/// <para><b>Words typed together.</b> A token of the query (the text between separators) that splits into several words, such as V0
/// or STDInj, matches as one: its words on neighbouring words of the clip, in the same order, each one whole but the last, which may
/// also be a word's start (V1 in civ_std_V1_N, not in civ_std_V0_N_01; STDInj in Mch_STDInj_V2, not in Mch_Std_CvrInjFM), or its
/// letters run together as one word of the clip (IdleBreak in idlebreak). Such a token scores as its weakest word. A single letter or
/// digit matches only a whole word or a word's start, never inside a word or across words.</para>
/// <para><b>The score.</b> A clip's score is the sum, over the query's tokens, of the token's best match in any of the clip's fields
/// times that field's weight:
/// <list type="table">
/// <item><term><see cref="WholeWord"/> (100)</term><description>the query word is a whole word of the field: "hello" in mca_emote_Hello</description></item>
/// <item><term><see cref="WordStart"/> (70)</term><description>it starts a word: "emo" in emote</description></item>
/// <item><term><see cref="InsideWord"/> (40)</term><description>it is inside a word: "move" in remove</description></item>
/// <item><term><see cref="AcrossWords"/> (30)</term><description>it runs across a word break, the field's letters and digits taken
/// together: "imwatching" in ImWatchingYou; in a <see cref="SearchFieldKind.Prose"/> field only two neighbouring words typed together,
/// the first whole and the second whole or its start, by a query word of <see cref="RunTogetherLength"/> letters or more</description></item>
/// <item><term>typo, whole word: 20 (one edit), 12 (two)</term><description>the query word is a word of the field but for a typo</description></item>
/// <item><term><see cref="Inflected"/> (20)</term><description>in a prose field, the query word is an English inflection of a word
/// of it: walks, walked, walking for walk; moving for move; running for run; carries for carry</description></item>
/// <item><term>typo, word start: 15 (one edit), 9 (two)</term><description>it is the start of a word but for a typo: "greting" for greetings</description></item>
/// <item><term><see cref="AcrossUnheld"/> (6)</term><description>a run across a name's words of a query word of under
/// <see cref="RunTogetherLength"/> letters that takes typos (no clip holds it as a whole word): such a short run is mostly chance
/// ("deth" in grena-de_th-row), so the words it was meant for come first; a longer one ("verylowhealth", "imwatchingyou") is a name's
/// words typed together and keeps 30</description></item>
/// </list>
/// Every match as written outranks every typo, but for that short chance run. A query word that matches in more than one field of a clip
/// adds, over its best field's score, each further field's <see cref="SearchField.Bonus"/> (<see cref="FurtherField"/> unless the field
/// says otherwise; a category's is larger), at most <see cref="MostBonus"/> in all. The kinds of match above lie at least 3 apart (the
/// typos' 15, 12 and 9, and the 6), so within one field of weight w they lie at least 3w apart, and the bonus (2 at most) never lifts a
/// weaker match over a better one in a field of weight above 2/3 (a name or a readable name, at 1; a glossary meaning, at 0.7). Between
/// fields of other weights the steps can be smaller than the bonus (a category's whole word, 100 x 0.5 = 50, against a glossary
/// meaning's word start, 70 x 0.7 = 49), and there the bonus may tie or reorder two clips' matches. "death" thus puts a clip whose name
/// and category say death before one whose name and readable name do, and both before one whose name alone does. Clips of equal score
/// come shorter names first, then by name (case ignored), then by index.</para>
/// <para><b>Typos.</b> An edit is a letter put in, taken out, changed, or two neighbours swapped (Damerau-Levenshtein, optimal string
/// alignment). A query word of under 4 letters takes no typo, of 4 to 7 letters one, of 8 or more two (<see cref="MaxTypos"/>). A query
/// word that is a whole word of some clip of the list is taken as meant and matched only as written, so "head" never brings "heal" or
/// "dead" in. Any other word is matched with typos as well, below every match as written: "deth" finds death, "trun" turn, and "rigt"
/// right (after the game's own "rigth", which it starts). When no clip matches the query (after the filters, see
/// <see cref="Search(string, Func{ClipEntry, bool})"/>), it is searched again with typos for every word, so a typo that is itself a
/// word ("heal back" for head back) still finds something.</para>
/// <para><b>Hex IDs.</b> A query starting with 0x matches only IDs: the hex digits after it (spaces left out) anywhere in the 16 digits.
/// A query of eight or more hex digits matches IDs too, besides the words. The whole 16 digits score as a whole word, the ID's start as
/// a word's start, anything else as inside a word.</para>
/// <para><b>No text.</b> Text with no word and no hex digit (empty, only separators, or 0x alone) is no query: every clip matches. Only
/// the first <see cref="MaxQueryLength"/> characters and <see cref="MaxQueryWords"/> words of a query are read, so a long paste costs
/// about what a typed query does.</para></summary>
public sealed class ClipSearch
{
    public const double WholeWord = 100, WordStart = 70, InsideWord = 40, AcrossWords = 30, Inflected = 20, AcrossUnheld = 6;

    /// <summary>What a further field holding a query word adds by default (<see cref="SearchField.Bonus"/>), and the most all further
    /// fields add for one query word.</summary>
    public const double FurtherField = 0.5, MostBonus = 2;

    /// <summary>The fewest letters of a query word matched across two neighbouring words of a prose field: shorter runs are mostly
    /// chance.</summary>
    public const int RunTogetherLength = 8;

    /// <summary>Query words longer than this take no typos (their edit table would be long for nothing: a name's words are short).</summary>
    public const int MaxTypoLength = 32;

    /// <summary>A query is read up to this many words; the rest of a long paste is left out.</summary>
    public const int MaxQueryWords = 32;

    /// <summary>A query is read up to this many characters.</summary>
    public const int MaxQueryLength = 1024;

    // a typo's score: to a whole word, to a word's start, at one edit and at two
    const double TypoWhole1 = 20, TypoWhole2 = 12, TypoStart1 = 15, TypoStart2 = 9;

    /// <summary>The fields searched when none are given: the clip's name (weight 1) and its ID as 16 hex digits.</summary>
    public static IEnumerable<SearchField> NameAndId(ClipEntry clip) =>
        [new SearchField(clip.Name), new SearchField(clip.Id.ToString("X16", CultureInfo.InvariantCulture), 1, SearchFieldKind.HexId)];

    /// <summary>One field of one clip, ready to match: its words as indexes into the list's vocabulary, its letters and digits run
    /// together (lower case; empty for prose), or its hex digits (upper case); for prose, each pair of neighbouring words as an index
    /// into the list's pairs (null otherwise); and for a phrase or compound of several words (<see cref="SearchFieldKind.Phrase"/>,
    /// <see cref="SearchFieldKind.Compound"/>) its place in the list's distinct phrases, else -1.</summary>
    readonly record struct Field(int[] Words, string Joined, string? Hex, double Weight, double Bonus, int[]? Pairs, int Phrase = -1);

    readonly ClipEntry[] _clips;
    readonly Field[][] _fields;
    /// <summary>Per distinct phrase or compound of several words of the list: its words that count alone (a phrase's last word, every
    /// word of a compound), its words that count only with one of those (a phrase's words before the last), and its rivals: the words
    /// it doesn't hold itself of the others that share a word counting alone with it. And per word of a phrase, the phrase pairs it is
    /// in.</summary>
    readonly int[][] _anchors;
    readonly int[][] _leads;
    readonly int[][] _rivals;
    readonly Dictionary<int, int[]> _phrasePairsOf;
    readonly string[] _vocabulary;
    readonly Dictionary<string, int> _wordIds;
    /// <summary>The prose fields' distinct pairs of neighbouring words: the two run together, and the first one's length.</summary>
    readonly string[] _pairs;
    readonly int[] _pairFirstLength;

    /// <summary>The clips searched, in the order given.</summary>
    public IReadOnlyList<ClipEntry> Clips => _clips;

    /// <summary>Where each clip's fields came from.</summary>
    public Func<ClipEntry, IEnumerable<SearchField>> Fields { get; }

    /// <summary>Distinct words over every clip's fields.</summary>
    public int VocabularySize => _vocabulary.Length;

    /// <summary>Builds the search over <paramref name="clips"/>, reading each clip's fields from <paramref name="fields"/> once (by
    /// default <see cref="NameAndId"/>). A further source of words, such as a readable name or category words, is one more field with
    /// its own weight. A field source may give a clip no field, or a field with no text (null reads as empty).</summary>
    public ClipSearch(IEnumerable<ClipEntry> clips, Func<ClipEntry, IEnumerable<SearchField>>? fields = null)
    {
        Fields = fields ?? NameAndId;
        _clips = clips.ToArray();
        _fields = new Field[_clips.Length][];
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var vocabulary = new List<string>();
        var pairIds = new Dictionary<(int, int), int>();
        var pairs = new List<string>();
        var pairFirst = new List<int>();
        var phraseIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var phrases = new List<(int[] Words, int[] Pairs, bool Compound)>();
        for (int i = 0; i < _clips.Length; i++)
        {
            var list = new List<Field>();
            foreach (var f in Fields(_clips[i]) ?? [])
            {
                if (f is null) continue;
                if (!(f.Weight > 0) || !double.IsFinite(f.Weight))
                    throw new ArgumentException($"a search field's weight must be above 0 (clip {_clips[i].Name}: {f.Weight})", nameof(fields));
                if (!(f.Bonus >= 0 && f.Bonus <= MostBonus))
                    throw new ArgumentException($"a search field's bonus must be 0 to {MostBonus} (clip {_clips[i].Name}: {f.Bonus})", nameof(fields));
                string text = f.Text ?? "";
                if (f.Kind == SearchFieldKind.HexId)
                {
                    list.Add(new Field([], "", text.Trim().ToUpperInvariant(), f.Weight, f.Bonus, null));
                    continue;
                }
                var words = Words(text);
                var wordIds = new int[words.Count];
                for (int j = 0; j < words.Count; j++)
                {
                    if (!ids.TryGetValue(words[j], out int id))
                    {
                        id = vocabulary.Count;
                        ids.Add(words[j], id);
                        vocabulary.Add(words[j]);
                    }
                    wordIds[j] = id;
                }
                if (f.Kind is not (SearchFieldKind.Prose or SearchFieldKind.Phrase or SearchFieldKind.Compound))
                {
                    list.Add(new Field(wordIds, string.Concat(words), null, f.Weight, f.Bonus, null));
                    continue;
                }
                // prose keeps no run of its letters, only its pairs of neighbouring words: nothing else matches across its words
                var pairOf = new int[Math.Max(0, wordIds.Length - 1)];
                for (int j = 0; j < pairOf.Length; j++)
                {
                    if (!pairIds.TryGetValue((wordIds[j], wordIds[j + 1]), out int p))
                    {
                        p = pairs.Count;
                        pairIds.Add((wordIds[j], wordIds[j + 1]), p);
                        pairs.Add(words[j] + words[j + 1]);
                        pairFirst.Add(words[j].Length);
                    }
                    pairOf[j] = p;
                }
                int phrase = -1;
                bool compound = f.Kind == SearchFieldKind.Compound;
                if ((compound || f.Kind == SearchFieldKind.Phrase) && wordIds.Length > 1)
                {
                    string key = (compound ? "+" : "") + string.Join(",", wordIds);
                    if (!phraseIds.TryGetValue(key, out phrase))
                    {
                        phrase = phrases.Count;
                        phraseIds.Add(key, phrase);
                        phrases.Add((wordIds, pairOf, compound));
                    }
                }
                list.Add(new Field(wordIds, "", null, f.Weight, f.Bonus, pairOf, phrase));
            }
            _fields[i] = [.. list];
        }
        _vocabulary = [.. vocabulary];
        _wordIds = ids;
        _pairs = [.. pairs];
        _pairFirstLength = [.. pairFirst];
        int[][] anchors = [.. phrases.Select(p => p.Compound ? p.Words.Distinct().ToArray() : [p.Words[^1]])];
        _anchors = anchors;
        _leads = [.. phrases.Select((p, i) => p.Words.Except(anchors[i]).ToArray())];
        _rivals = [.. phrases.Select((p, i) => phrases.Where((_, j) => anchors[j].Any(w => anchors[i].Contains(w))).SelectMany(o => o.Words)
            .Except(p.Words).ToArray())];
        _phrasePairsOf = phrases.SelectMany(p => p.Pairs.SelectMany((pair, j) => new[] { (Word: p.Words[j], Pair: pair), (Word: p.Words[j + 1], Pair: pair) }))
            .GroupBy(x => x.Word).ToDictionary(g => g.Key, g => g.Select(x => x.Pair).Distinct().ToArray());
    }

    /// <summary>The searches built over one list, the most recently used first: one per field source, at most
    /// <see cref="SourcesKept"/>.</summary>
    static readonly ConditionalWeakTable<IReadOnlyList<ClipEntry>, List<ClipSearch>> Built = new();
    const int SourcesKept = 4;

    /// <summary>The search over <paramref name="clips"/> with <paramref name="fields"/> (by default <see cref="NameAndId"/>), built on
    /// first use and kept for as long as the list lives: an install's clip list has its words split once. Searches with a few different
    /// field sources are kept side by side (sources are told apart by delegate equality, so pass the same delegate each time: a new
    /// lambda per query builds the search again). It is built again when the list's clips are no longer the ones it was built from.
    /// Fields are read when it is built. A build runs outside the lock, so building one source in the background (the clip index's
    /// fields, say) never holds up a search with another; two threads building the same source at once each build it, and the last
    /// one built is kept.</summary>
    public static ClipSearch For(IReadOnlyList<ClipEntry> clips, Func<ClipEntry, IEnumerable<SearchField>>? fields = null)
    {
        fields ??= NameAndId;
        var kept = Built.GetValue(clips, _ => []);
        lock (kept)
        {
            int i = kept.FindIndex(s => s.Fields.Equals(fields));
            if (i >= 0 && kept[i].Holds(clips))
            {
                var found = kept[i];
                kept.RemoveAt(i);
                kept.Insert(0, found);
                return found;
            }
        }
        var search = new ClipSearch(clips, fields);
        lock (kept)
        {
            kept.RemoveAll(s => s.Fields.Equals(fields));
            kept.Insert(0, search);
            if (kept.Count > SourcesKept) kept.RemoveAt(kept.Count - 1);
        }
        return search;
    }

    bool Holds(IReadOnlyList<ClipEntry> clips)
    {
        if (clips.Count != _clips.Length) return false;
        for (int i = 0; i < _clips.Length; i++)
            if (!ReferenceEquals(clips[i], _clips[i])) return false;
        return true;
    }

    /// <summary>The words of <paramref name="text"/>, lower case, in order (see the class's notes for where words break).</summary>
    public static List<string> Words(string text) => [.. Tokens(Fold(text ?? ""), int.MaxValue).SelectMany(t => t)];

    /// <summary>The tokens of <paramref name="text"/> (its runs of letters and digits), each as its words, lower case; at most
    /// <paramref name="maxWords"/> words in all, the token reached then cut short.</summary>
    static List<string[]> Tokens(string text, int maxWords)
    {
        var tokens = new List<string[]>();
        var token = new List<string>();
        int start = -1, count = 0;
        for (int i = 0; i <= text.Length && count < maxWords; i++)
        {
            bool part = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (start >= 0 && (!part || Breaks(text, i)))
            {
                token.Add(text[start..i].ToLowerInvariant());
                count++;
                start = -1;
            }
            if (!part && token.Count > 0)
            {
                tokens.Add([.. token]);
                token.Clear();
            }
            if (part && start < 0) start = i;
        }
        if (token.Count > 0) tokens.Add([.. token]);
        return tokens;
    }

    /// <summary>Whether a word breaks between text[i - 1] and text[i], both letters or digits.</summary>
    static bool Breaks(string s, int i)
    {
        char a = s[i - 1], b = s[i];
        if (char.IsDigit(a) != char.IsDigit(b)) return true;
        if (char.IsLower(a) && char.IsUpper(b)) return true;
        return char.IsUpper(a) && char.IsUpper(b) && i + 1 < s.Length && char.IsLower(s[i + 1]);
    }

    /// <summary>Text as the search compares it: compatibility forms read as their plain letters (full-width ＨＥＬＬＯ, the ligature ﬁ),
    /// accents and other combining marks taken out (é is e), the rest in NFKC; a lone surrogate (no character) reads as a space. Plain
    /// ASCII is returned as it is.</summary>
    internal static string Fold(string text)
    {
        if (Ascii.IsValid(text)) return text;
        var clean = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogatePair(text, i)) clean.Append(text, i++, 2);
            else clean.Append(char.IsSurrogate(text[i]) ? ' ' : text[i]);
        }
        string s = clean.ToString();
        string split = SystemNormalization.Normalize(s, SystemNormalization.FormKD) ?? s;
        var kept = new StringBuilder(split.Length);
        for (int i = 0; i < split.Length; i++)
        {
            int n = char.IsSurrogatePair(split, i) ? 2 : 1;
            if (CharUnicodeInfo.GetUnicodeCategory(split, i) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                kept.Append(split, i, n);
            i += n - 1;
        }
        string bare = kept.ToString();
        return SystemNormalization.Normalize(bare, SystemNormalization.FormKC) ?? bare;
    }

    /// <summary>How many edits a query word of <paramref name="length"/> letters may differ by: none under 4 (or over
    /// <see cref="MaxTypoLength"/>), one for 4 to 7, two for 8 or more.</summary>
    public static int MaxTypos(int length) => length is < 4 or > MaxTypoLength ? 0 : length <= 7 ? 1 : 2;

    /// <summary>A query as read: its tokens, each as its words; the hex digits it gives, or null; and whether it can match no clip
    /// (0x followed by something other than hex digits).</summary>
    sealed record Query(List<string[]> Tokens, string? Hex, bool MatchesNone);

    static Query Read(string? text)
    {
        string t = text ?? "";
        if (t.Length > MaxQueryLength) t = t[..MaxQueryLength];
        t = Fold(t).Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = string.Concat(t[2..].Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
            if (!hex.All(char.IsAsciiHexDigit)) return new([], null, true);
            return new([], hex.Length > 0 ? hex : null, false);
        }
        string? digits = t.Length >= 8 && t.All(char.IsAsciiHexDigit) ? t.ToUpperInvariant() : null;
        return new([.. Tokens(t, MaxQueryWords).DistinctBy(tk => string.Join(" ", tk))], digits, false);
    }

    /// <summary>Whether <paramref name="text"/> is a query: it holds a word, or follows 0x with anything. Text that isn't (empty, only
    /// separators, 0x alone) matches every clip.</summary>
    public static bool HasQuery(string? text) => Read(text) is var q && (q.MatchesNone || q.Hex is not null || q.Tokens.Count > 0);

    /// <summary>The clips that match <paramref name="text"/>, best first (see the class's notes). Text that is no query (see
    /// <see cref="HasQuery"/>) matches every clip, with score 0, in the list's order.</summary>
    public List<SearchHit> Search(string? text) => Search(text, null);

    /// <summary>The clips that match <paramref name="text"/> and that <paramref name="keep"/> (the list's filters: status, family,
    /// length, ...) lets through, best first. When none of them matches, the query is searched again with typos for every word (see the
    /// class's notes).</summary>
    public List<SearchHit> Search(string? text, Func<ClipEntry, bool>? keep)
    {
        var q = Read(text);
        if (q.MatchesNone) return [];
        if (q.Hex is null && q.Tokens.Count == 0) return [.. _clips.Where(c => keep is null || keep(c)).Select(c => new SearchHit(c, 0))];
        var hits = Scan(q, typosForAll: false, keep, out bool held);
        if (hits.Count == 0 && held) hits = Scan(q, typosForAll: true, keep, out _);
        hits.Sort(Better);
        return hits;
    }

    /// <summary>Best first: the higher score, then the shorter name, then the name (case ignored), then the index.</summary>
    static int Better(SearchHit a, SearchHit b)
    {
        int r = b.Score.CompareTo(a.Score);
        if (r == 0) r = a.Clip.Name.Length.CompareTo(b.Clip.Name.Length);
        if (r == 0) r = string.Compare(a.Clip.Name, b.Clip.Name, StringComparison.OrdinalIgnoreCase);
        if (r == 0) r = a.Clip.Index.CompareTo(b.Clip.Index);
        return r;
    }

    /// <summary>A query word's scores against each word of the vocabulary: as that whole word, as the whole word or its start, and
    /// anywhere in it. <paramref name="Held"/>: the word could take typos but didn't, being a whole word of some clip.
    /// <paramref name="Typos"/>: it was matched with typos. <paramref name="Stems"/>: the vocabulary's words it inflects (walk for
    /// walking). <paramref name="Pairs"/>: the prose pairs it runs across (null for a word too short to).</summary>
    sealed record WordScores(double[] Whole, double[] Start, double[] Any, bool Held, bool Typos, int[] Stems, bool[]? Pairs);

    /// <summary>A token's word scores, and for a token of several words the scores of its letters run together.</summary>
    sealed record TokenScores(WordScores[] Words, WordScores? Joined);

    List<SearchHit> Scan(Query q, bool typosForAll, Func<ClipEntry, bool>? keep, out bool held)
    {
        held = false;
        var tables = new TokenScores[q.Tokens.Count];
        var scored = new Dictionary<string, WordScores>(StringComparer.Ordinal);
        WordScores Scores(string w) => scored.TryGetValue(w, out var s) ? s : scored[w] = Score(w, typosForAll);
        for (int t = 0; t < tables.Length; t++)
        {
            var words = q.Tokens[t];
            var scores = words.Select(Scores).ToArray();
            var joined = words.Length > 1 ? Scores(string.Concat(words)) : null;
            tables[t] = new TokenScores(scores, joined);
            held |= scores.Any(s => s.Held) || joined is { Held: true };
        }
        var counted = _anchors.Length > 0 && tables.Length > 0 ? PhrasesCounted(tables) : null;
        var hits = new List<SearchHit>();
        for (int i = 0; i < _clips.Length; i++)
        {
            if (keep is not null && !keep(_clips[i])) continue;
            double s = WordsScore(i, q.Tokens, tables, counted);
            if (q.Hex is not null) s = Math.Max(s, HexScore(i, q.Hex));
            if (s > 0) hits.Add(new SearchHit(_clips[i], s));
        }
        return hits;
    }

    /// <summary>The scores of query word <paramref name="q"/> against every word of the vocabulary: as written, and with typos when it
    /// is long enough and either no clip holds it as a whole word or <paramref name="typosForAll"/>.</summary>
    WordScores Score(string q, bool typosForAll)
    {
        int n = _vocabulary.Length;
        double[] whole = new double[n], start = new double[n], any = new double[n];
        bool isWord = false;
        for (int w = 0; w < n; w++)
        {
            string v = _vocabulary[w];
            int at = v.IndexOf(q, StringComparison.Ordinal);
            if (at < 0) continue;
            if (at > 0)
            {
                // a single letter or digit is in most words: it matches only a whole word or a word's start
                if (q.Length > 1) any[w] = InsideWord;
            }
            else if (v.Length == q.Length)
            {
                whole[w] = start[w] = any[w] = WholeWord;
                isWord = true;
            }
            else start[w] = any[w] = WordStart;
        }
        int[] stems = [.. Stems(q).Select(s => _wordIds.TryGetValue(s, out int id) ? id : -1).Where(id => id >= 0).Distinct()];
        bool[]? pairs = null;
        if (q.Length >= RunTogetherLength)
        {
            // across exactly two neighbouring words: from the first one's start, past its end, and no further than the second one's end
            pairs = new bool[_pairs.Length];
            for (int p = 0; p < pairs.Length; p++)
                pairs[p] = q.Length > _pairFirstLength[p] && _pairs[p].StartsWith(q, StringComparison.Ordinal);
        }
        int k = MaxTypos(q.Length);
        if (k == 0 || (isWord && !typosForAll)) return new WordScores(whole, start, any, Held: k > 0, Typos: false, stems, pairs);
        for (int w = 0; w < n; w++)
        {
            var (toWhole, toStart) = Edits(q, _vocabulary[w], k);
            double typoWhole = toWhole > k ? 0 : toWhole == 0 ? WholeWord : toWhole == 1 ? TypoWhole1 : TypoWhole2;
            double typoStart = toStart > k ? 0 : toStart == 0 ? WordStart : toStart == 1 ? TypoStart1 : TypoStart2;
            whole[w] = Math.Max(whole[w], typoWhole);
            start[w] = Math.Max(start[w], Math.Max(typoWhole, typoStart));
            any[w] = Math.Max(any[w], start[w]);
        }
        return new WordScores(whole, start, any, Held: false, Typos: true, stems, pairs);
    }

    /// <summary>The plain forms an English word may be an inflection of, by a light rule on its ending: -s, -es, -ies (carries: carry),
    /// -ed, -ied, -ing, with a dropped e (moving: move) or a doubled last letter (running: run). Each form has three letters or more,
    /// and a dropped-e form four or more, so that "thing" isn't read as "the" (nor "shed" as "she"); a word ending in ss is no plural.</summary>
    internal static IEnumerable<string> Stems(string q)
    {
        const int Least = 3, LeastWithE = 4;
        var forms = new List<string>();
        void Add(string s) { if (s.Length >= Least) forms.Add(s); }
        void AddE(string s) { if (s.Length >= LeastWithE) forms.Add(s); }
        void Doubled(string s) { if (s.Length > Least && s[^1] == s[^2] && !"aeiouls".Contains(s[^1])) forms.Add(s[..^1]); }
        if (q.EndsWith("ies", StringComparison.Ordinal)) Add(q[..^3] + "y");
        else if (q.EndsWith("es", StringComparison.Ordinal)) { Add(q[..^2]); Add(q[..^1]); }
        else if (q.EndsWith('s') && !q.EndsWith("ss", StringComparison.Ordinal)) Add(q[..^1]);
        if (q.EndsWith("ied", StringComparison.Ordinal)) Add(q[..^3] + "y");
        else if (q.EndsWith("ed", StringComparison.Ordinal)) { Add(q[..^2]); AddE(q[..^1]); Doubled(q[..^2]); }
        if (q.EndsWith("ing", StringComparison.Ordinal)) { Add(q[..^3]); AddE(q[..^3] + "e"); Doubled(q[..^3]); }
        return forms;
    }

    double WordsScore(int clip, List<string[]> tokens, TokenScores[] tables, bool[]? counted)
    {
        if (tokens.Count == 0) return 0;
        var fields = _fields[clip];
        double total = 0;
        for (int t = 0; t < tokens.Count; t++)
        {
            // the best field's score, and the further fields' bonuses (of fields tied for best, the one with the least bonus counts as best)
            double best = 0, bestBonus = 0, bonuses = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                ref readonly var f = ref fields[i];
                // a phrase or compound counts only as the query lets it (see SearchFieldKind.Phrase and Compound)
                if (f.Hex is not null || (f.Phrase >= 0 && !counted![f.Phrase])) continue;
                double s = TokenScore(in f, tokens[t], tables[t]) * f.Weight;
                if (s <= 0) continue;
                bonuses += f.Bonus;
                if (s > best) (best, bestBonus) = (s, f.Bonus);
                else if (s == best) bestBonus = Math.Min(bestBonus, f.Bonus);
            }
            if (best == 0) return 0;
            total += best + Math.Min(MostBonus, bonuses - bestBonus);
        }
        return total;
    }

    /// <summary>Per phrase or compound of the list (<see cref="SearchFieldKind.Phrase"/>, <see cref="SearchFieldKind.Compound"/>),
    /// whether it counts for the query: a token matches one of its words that count alone (a phrase's last word, any word of a
    /// compound), and either a token matches one of its words that count only with those (a phrase's words before the last) or no
    /// token holds a rival as a whole word (a word of another one that shares a word counting alone with it). A rival only typed so far
    /// ("ri" on the way to "right" or "rifle") stands against nothing.</summary>
    bool[] PhrasesCounted(TokenScores[] tables)
    {
        var held = new Dictionary<int, bool>();
        var whole = new Dictionary<int, bool>();
        bool Held(int w) => held.TryGetValue(w, out bool h) ? h : held[w] = Matched(w, tables, wholeOnly: false);
        bool HeldWhole(int w) => whole.TryGetValue(w, out bool h) ? h : whole[w] = Matched(w, tables, wholeOnly: true);
        var counted = new bool[_anchors.Length];
        for (int p = 0; p < counted.Length; p++)
            counted[p] = Array.Exists(_anchors[p], Held) && (Array.Exists(_leads[p], Held) || !Array.Exists(_rivals[p], HeldWhole));
        return counted;
    }

    /// <summary>Whether a token of the query matches the phrase word <paramref name="w"/>: by one of its words in any way (whole, a
    /// start, inside, a typo, an inflection), or with <paramref name="wholeOnly"/> only as the whole word (as written or but for a
    /// typo) or an inflection of it; by its letters run together; or by running across a phrase pair that holds the word (the two words
    /// typed together, "northeast").</summary>
    bool Matched(int w, TokenScores[] tables, bool wholeOnly)
    {
        var pairs = _phrasePairsOf.GetValueOrDefault(w) ?? [];
        bool By(WordScores ws)
        {
            if ((wholeOnly ? ws.Whole[w] : ws.Any[w]) > 0 || Array.IndexOf(ws.Stems, w) >= 0) return true;
            if (ws.Pairs is { } across) foreach (int p in pairs) if (across[p]) return true;
            return false;
        }
        foreach (var t in tables)
        {
            foreach (var ws in t.Words) if (By(ws)) return true;
            if (t.Joined is { } j && By(j)) return true;
        }
        return false;
    }

    static double TokenScore(in Field f, string[] words, TokenScores scores)
    {
        double best = 0;
        if (words.Length == 1)
        {
            var ws = scores.Words[0];
            foreach (int w in f.Words) best = Math.Max(best, ws.Any[w]);
            if (f.Pairs is { } pairs)
            {
                // prose: an inflection of one of its words, or two neighbouring words typed together
                if (best < Inflected && ws.Stems.Length > 0)
                    foreach (int w in f.Words) if (Array.IndexOf(ws.Stems, w) >= 0) best = Inflected;
                if (best < AcrossWords && ws.Pairs is { } across)
                    foreach (int p in pairs) if (across[p]) best = AcrossWords;
                return best;
            }
            // a single letter or digit is always inside a run of the field's letters, so only longer query words can run across words;
            // a short run of a word that takes typos is mostly chance, below its typos
            double run = ws.Typos && words[0].Length < RunTogetherLength ? AcrossUnheld : AcrossWords;
            if (best < run && words[0].Length > 1 && f.Joined.Contains(words[0], StringComparison.Ordinal)) best = run;
            return best;
        }
        // words typed together: on neighbouring words of the field, in order, each whole but the last (whole or a start); the weakest counts
        int n = words.Length;
        for (int i = 0; i + n <= f.Words.Length; i++)
        {
            double s = scores.Words[n - 1].Start[f.Words[i + n - 1]];
            for (int j = 0; j < n - 1 && s > 0; j++) s = Math.Min(s, scores.Words[j].Whole[f.Words[i + j]]);
            best = Math.Max(best, s);
        }
        // or their letters run together as one word of the field
        var joined = scores.Joined!.Any;
        foreach (int w in f.Words) best = Math.Max(best, joined[w]);
        return best;
    }

    double HexScore(int clip, string hex)
    {
        double best = 0;
        foreach (var f in _fields[clip])
        {
            if (f.Hex is null) continue;
            int at = f.Hex.IndexOf(hex, StringComparison.Ordinal);
            if (at < 0) continue;
            double s = at > 0 ? InsideWord : hex.Length == f.Hex.Length ? WholeWord : WordStart;
            best = Math.Max(best, s * f.Weight);
        }
        return best;
    }

    /// <summary>The edits from <paramref name="q"/> to the whole of <paramref name="w"/>, and to its closest start (a prefix of
    /// <paramref name="w"/> shorter than it, of one letter or more): insertions, deletions, substitutions and swaps of two neighbours
    /// (optimal string alignment). Any count above <paramref name="k"/> is given as <paramref name="k"/> + 1; the work stops as soon as no
    /// alignment can stay within <paramref name="k"/>.</summary>
    internal static (int Whole, int Prefix) Edits(string q, string w, int k)
    {
        if (k is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(k), k, "at most two edits are counted");
        int over = k + 1, m = q.Length;
        if (m > MaxTypoLength || w.Length < m - k) return (over, over);
        int n = Math.Min(w.Length, m + k);
        // rows: q's first i letters; columns: w's first j letters, up to n (no prefix longer than m + k can be within k edits)
        Span<int> before = stackalloc int[n + 1], prev = stackalloc int[n + 1], cur = stackalloc int[n + 1];
        for (int j = 0; j <= n; j++) prev[j] = j;
        for (int i = 1; i <= m; i++)
        {
            cur[0] = i;
            int rowMin = i;
            for (int j = 1; j <= n; j++)
            {
                int d = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (q[i - 1] == w[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && q[i - 1] == w[j - 2] && q[i - 2] == w[j - 1]) d = Math.Min(d, before[j - 2] + 1);
                cur[j] = d;
                rowMin = Math.Min(rowMin, d);
            }
            // every value of a later row is at least one of this row's (a swap's too: it is never below this row's value one column to
            // its left), so once the whole row is over k, so is everything after it
            if (rowMin > k) return (over, over);
            Span<int> spare = before;
            before = prev;
            prev = cur;
            cur = spare;
        }
        int whole = w.Length <= m + k ? Math.Min(prev[w.Length], over) : over;
        int prefix = over;
        for (int j = Math.Max(1, m - k); j <= Math.Min(n, w.Length - 1); j++) prefix = Math.Min(prefix, prev[j]);
        return (whole, prefix);
    }
}

/// <summary>Unicode normalization by the system. The program runs with invariant globalization, under which
/// <see cref="string.Normalize(NormalizationForm)"/> returns its text unchanged, so the search asks Windows instead.</summary>
static partial class SystemNormalization
{
    internal const int FormKC = 5, FormKD = 6;
    const int ErrorInsufficientBuffer = 122;

    /// <summary><paramref name="s"/> in the given form, or null where the system can't give it (not Windows, or text it refuses).</summary>
    internal static string? Normalize(string s, int form)
    {
        if (s.Length == 0) return s;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            int size = NormalizeString(form, s, s.Length, null, 0);
            for (int round = 0; round < 3 && size > 0; round++)
            {
                var buffer = new char[size];
                int n = NormalizeString(form, s, s.Length, buffer, buffer.Length);
                if (n > 0) return new string(buffer, 0, n);
                if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer) return null;
                // the first size was an estimate; a failed call gives a better one, negated
                size = Math.Max(-n, buffer.Length * 2);
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NormalizeString(int form, string source, int sourceLength, [Out] char[]? destination, int destinationLength);
}
