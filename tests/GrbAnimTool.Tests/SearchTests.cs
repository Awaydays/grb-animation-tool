using System.Diagnostics;
using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using Xunit.Abstractions;

namespace GrbAnimTool.Tests;

/// <summary>Tests of the search box's matcher with no game data: words, scores, typos, IDs, extra fields, and the filters and sorts
/// around it.</summary>
public sealed class SearchTests(ITestOutputHelper output)
{
    /// <summary>Clips of these names, indexes 0, 1, ... and IDs 0x000001DE00000000 + index, with no record.</summary>
    static ClipEntry[] List(params string[] names) =>
        names.Select((n, i) => new ClipEntry { Index = i, Id = 0x000001DE00000000UL + (ulong)i, Name = n, Bytes = ReadOnlyMemory<byte>.Empty }).ToArray();

    static ClipEntry Entry(int index, string name, float seconds, ClipStatus status = ClipStatus.Vanilla)
    {
        var bytes = Synthetic.Clip(name, 0x000001DE00000000UL + (ulong)index, seconds, [(1, Synthetic.PosCurve([0], Vector3.Zero))]);
        return new ClipEntry { Index = index, Id = 0x000001DE00000000UL + (ulong)index, Name = name, Bytes = bytes, Record = ClipRecord.Parse(bytes), Status = status };
    }

    static string[] Found(IEnumerable<ClipEntry> clips, string text) => new ClipSearch(clips).Search(text).Select(h => h.Clip.Name).ToArray();
    static string[] Names(IEnumerable<ClipEntry> clips) => clips.Select(c => c.Name).ToArray();

    [Theory]
    [InlineData("mca_emote_Hello", "mca emote hello")]
    [InlineData("civ_m_col_std_V0_N_Reaction_Hello_02", "civ m col std v 0 n reaction hello 02")]
    [InlineData("IsExhausted", "is exhausted")]
    [InlineData("mca_emote_ImWatchingYou", "mca emote im watching you")]
    [InlineData("ACD_DS_GEN_Mood_Angry_lvl-05_Angry", "acd ds gen mood angry lvl 05 angry")]
    [InlineData("nme_STDInj_front", "nme std inj front")]
    [InlineData("Hello02World 3rd", "hello 02 world 3 rd")]
    [InlineData("  emo   hel ", "emo hel")]
    [InlineData("a&b--c", "a b c")]
    [InlineData("", "")]
    [InlineData("___", "")]
    [InlineData("ＨＥＬＬＯ Ｅｍｏｔｅ", "hello emote")]   // full width
    [InlineData("héllo", "hello")]                                                      // a combining accent
    [InlineData("héllo", "hello")]                                                       // an accented letter
    [InlineData("ﬁne_Turn", "fine turn")]                                                // a ligature
    [InlineData("hello\uD800emote", "hello emote")]                                           // a lone surrogate breaks words
    public void NamesSplitIntoWords(string text, string words) =>
        Assert.Equal(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), ClipSearch.Words(text));

    [Fact]
    public void WholeWordsComeBeforeStartsPartsAndRunsAcrossWords()
    {
        var hits = new ClipSearch(List("mca_sidewalk_01", "mca_wa_lk_01", "mca_walking_01", "mca_walk_01", "mca_run_01")).Search("walk");
        Assert.Equal(["mca_walk_01", "mca_walking_01", "mca_sidewalk_01", "mca_wa_lk_01"], hits.Select(h => h.Clip.Name));
        Assert.Equal([ClipSearch.WholeWord, ClipSearch.WordStart, ClipSearch.InsideWord, ClipSearch.AcrossWords], hits.Select(h => h.Score));
        // a run across words as the old search found it: part of the name with its capitals' breaks typed together
        Assert.Equal(["mca_emote_ImWatchingYou"], Found(List("mca_emote_ImWatchingYou", "mca_emote_Hello"), "imwatching"));
    }

    [Fact]
    public void EqualScoresComeShorterNamesFirstThenByName() =>
        Assert.Equal(["A_walk", "b_walk", "mca_walk", "mca_walk_long_name"], Found(List("mca_walk_long_name", "b_walk", "A_walk", "mca_walk"), "walk"));

    [Fact]
    public void EveryQueryWordMustMatchInAnyOrderAndTheScoresAdd()
    {
        var clips = List("mca_emote_Hello", "mca_emote_Clap", "civ_Reaction_Hello_01", "mca_emotes_Hello_loop");
        Assert.Equal(["mca_emote_Hello", "mca_emotes_Hello_loop"], Found(clips, "hello emote"));
        Assert.Equal(["mca_emote_Hello", "mca_emotes_Hello_loop"], Found(clips, "EMOTE hello"));
        var h = new ClipSearch(clips).Search("hello emote");
        Assert.Equal(2 * ClipSearch.WholeWord, h[0].Score);
        Assert.Equal(ClipSearch.WholeWord + ClipSearch.WordStart, h[1].Score);
        Assert.Equal(["mca_emote_Hello", "mca_emotes_Hello_loop"], Found(clips, "emo hel"));
        Assert.Equal(["mca_emote_Hello", "mca_emotes_Hello_loop"], Found(clips, "emote_hel"));
        Assert.Empty(Found(clips, "hello clap"));
        Assert.Equal(4, Found(clips, "  ").Length);           // no text: every clip
    }

    [Theory]
    [InlineData("")]
    [InlineData("_")]
    [InlineData("---")]
    [InlineData("__ -")]
    [InlineData("0x")]
    [InlineData("0X  ")]
    [InlineData("😀")]   // an emoji: no letter or digit
    public void TextWithNoWordIsNoQueryAndShowsEveryClip(string text)
    {
        var clips = List("mca_emote_Hello", "mca_emote_Clap", "nme_walk_01");
        Assert.False(ClipSearch.HasQuery(text));
        Assert.Equal(Names(clips), Found(clips, text));
        Assert.Equal(Names(clips), Names(new ClipQuery { Text = text }.Apply(clips, ClipSort.Relevance)));
    }

    [Fact]
    public void ALongPasteIsSearchedByItsFirstWords()
    {
        var clips = List("mca_emote_Hello", "mca_emote_Clap", "nme_walk_01");
        var s = new ClipSearch(clips);
        var rnd = new Random(3);
        string Word() => new(Enumerable.Range(0, 6).Select(_ => (char)('a' + rnd.Next(26))).ToArray());
        // 32 words that all match, then 2,000 that match nothing: only the first 32 are read
        string paste = string.Join(" ", Enumerable.Repeat("emote", 32).Concat(Enumerable.Range(0, 2000).Select(_ => Word())));
        long t0 = Stopwatch.GetTimestamp();
        var hits = s.Search(paste);
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Assert.Equal(["mca_emote_Clap", "mca_emote_Hello"], hits.Select(h => h.Clip.Name));
        Assert.True(ms < 250, $"{ms:F0} ms");
        // one token of thousands of words typed together, and 100,000 letters: read in part, no hang
        Assert.Empty(s.Search(string.Concat(Enumerable.Repeat("HelloEmote", 2500))));
        Assert.Empty(s.Search(new string('a', 100_000)));
    }

    [Fact]
    public void ATypoIsForgivenBelowEveryMatchAsWritten()
    {
        // "greting" (7 letters) is one edit from greeting, and from greetings' start
        var hits = new ClipSearch(List("mca_emote_Greetings_02", "mca_emote_Hello", "mca_emote_Greeting")).Search("greting");
        Assert.Equal(["mca_emote_Greeting", "mca_emote_Greetings_02"], hits.Select(h => h.Clip.Name));
        Assert.Equal([20.0, 15.0], hits.Select(h => h.Score));
        Assert.True(hits[0].Score < ClipSearch.AcrossWords);
        // two typos for a word of eight letters or more: whole word 12, word start 9
        Assert.Equal([12.0, 9.0], new ClipSearch(List("x_greeting", "x_greetingss")).Search("grxxting").Select(h => h.Score));
        // a second, exact word adds its own score
        Assert.Equal(ClipSearch.WholeWord + 20, new ClipSearch(List("mca_emote_Greeting")).Search("emote greting")[0].Score);
    }

    [Theory]
    [InlineData("hlelo", true)]       // 5 letters, two neighbours swapped: one edit
    [InlineData("helo", true)]        // 4 letters, one missing
    [InlineData("hxllo", true)]       // one changed
    [InlineData("hxllx", false)]      // 5 letters, two edits: too many
    [InlineData("hellooo", false)]    // 7 letters, two extra: too many
    [InlineData("hxl", false)]        // under 4 letters: no typo at all
    [InlineData("grxxting", true)]    // 8 letters: two edits
    [InlineData("grxxxing", false)]   // three edits
    [InlineData("grxting", false)]    // 7 letters, two edits from greeting
    public void TyposStayWithinTheirBounds(string query, bool found) =>
        Assert.Equal(found, Found(List("mca_emote_Hello", "mca_emote_Greeting"), query).Length > 0);

    [Theory]
    [InlineData("hlelo", "hello", 1, 1, 2)]
    [InlineData("hello", "yellow", 1, 2, 1)]       // the whole word two edits off, its start "yello" one
    [InlineData("greting", "greetings", 1, 2, 1)]  // the start "greeting" one edit off
    [InlineData("abcd", "abcd", 1, 0, 1)]          // its start "abc" one off
    [InlineData("abcd", "wxyz", 1, 2, 2)]
    [InlineData("grxxting", "greeting", 2, 2, 3)]
    public void EditsAreCountedToTheWordAndToItsStart(string q, string w, int k, int whole, int prefix) =>
        Assert.Equal((whole, prefix), ClipSearch.Edits(q, w, k));

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(32, 2)]
    [InlineData(33, 0)]
    public void TheTypoAllowanceGrowsWithTheWord(int length, int typos) => Assert.Equal(typos, ClipSearch.MaxTypos(length));

    [Fact]
    public void AWordFoundAsAWholeWordTakesNoTypos()
    {
        // "head" is a word of one clip, so "heal" and "dead" (one edit away) stay out
        Assert.Equal(["nme_death_head_back"], Found(List("nme_death_head_back", "nme_heal_self", "nme_dead_body"), "head"));
        // with no clip holding it, the same word finds them
        Assert.Equal(["nme_dead_body", "nme_heal_self"], Found(List("nme_heal_self", "nme_dead_body"), "head"));
        // held only inside a word or as a word's start, it takes typos too, ranked below; a run across words of a word that takes typos
        // is mostly chance, so it comes after them
        Assert.Equal(["nme_forehead", "nme_heal"], Found(List("nme_forehead", "nme_heal"), "head"));
        Assert.Equal(["nme_heal", "nme_he_ad"], Found(List("nme_he_ad", "nme_heal"), "head"));
        Assert.Equal(["nme_headset", "nme_heal"], Found(List("nme_headset", "nme_heal"), "head"));
    }

    [Fact]
    public void ACommonTypoFindsItsWordThoughAnotherWordHoldsIt()
    {
        // "deth" runs across grena-de-th-row, "trun" starts trunk, "idel" runs across outs-ide-l, "rigt" starts the misspelt rigth:
        // none of them is a whole word, so each also finds the word it was meant for
        var clips = List("mil_grenade_throw", "nme_death_head_back", "nme_dead_head_front", "nme_turn_left", "mch_grabTRUNK_exit",
            "mca_turr_FromOutside_L", "fm_crh_V0_cool_idle_neutral_01", "mca_entry_from_rigth", "npc_entry_from_right", "nme_heal_self");
        Assert.Equal(["nme_death_head_back"], Found(clips, "deth head back"));
        Assert.Equal(["nme_turn_left"], Found(clips, "trun left"));
        Assert.Equal(["fm_crh_V0_cool_idle_neutral_01"], Found(clips, "idel cool"));
        // the word meant first, then the chance run across words of a word that takes typos
        var deth = new ClipSearch(clips).Search("deth");
        Assert.Equal(["nme_death_head_back", "mil_grenade_throw"], deth.Select(h => h.Clip.Name));
        Assert.Equal([20, ClipSearch.AcrossUnheld], deth.Select(h => h.Score));
        Assert.Equal(["fm_crh_V0_cool_idle_neutral_01", "mca_turr_FromOutside_L"], Found(clips, "idel"));
        Assert.Equal(["mch_grabTRUNK_exit", "nme_turn_left"], Found(clips, "trun"));
        Assert.Equal(["mca_entry_from_rigth", "npc_entry_from_right"], Found(clips, "rigt"));
        // the order of every kind of match: whole, start, inside, across (a word some clip holds whole: as written only)
        var ranked = new ClipSearch(List("x_death", "x_deth", "x_dethrone", "x_undeth", "x_de_th")).Search("deth");
        Assert.Equal(["x_deth", "x_dethrone", "x_undeth", "x_de_th"], ranked.Select(h => h.Clip.Name));   // a whole word: no typo, death stays out
        Assert.Equal([ClipSearch.WholeWord, ClipSearch.WordStart, ClipSearch.InsideWord, ClipSearch.AcrossWords], ranked.Select(h => h.Score));
        // a word no clip holds whole: start, inside, the typo, then the run across words
        ranked = new ClipSearch(List("x_death", "x_dethrone", "x_undeth", "x_de_th")).Search("deth");
        Assert.Equal(["x_dethrone", "x_undeth", "x_death", "x_de_th"], ranked.Select(h => h.Clip.Name));
        Assert.Equal([ClipSearch.WordStart, ClipSearch.InsideWord, 20, ClipSearch.AcrossUnheld], ranked.Select(h => h.Score));
        // the words as written still keep the others out
        Assert.Equal(["nme_death_head_back"], Found(clips, "death head back"));
    }

    [Fact]
    public void AQueryThatFindsNothingIsTriedAgainWithTyposForEveryWord()
    {
        // "heal" is a word (of nme_heal_self), so at first it takes no typo and "heal back" finds nothing; tried again, it finds head
        var clips = List("nme_death_head_back", "nme_heal_self");
        var hits = new ClipSearch(clips).Search("heal back");
        Assert.Equal(["nme_death_head_back"], hits.Select(h => h.Clip.Name));
        Assert.Equal(20 + ClipSearch.WholeWord, hits[0].Score);
        Assert.Empty(Found(clips, "heal zzzz"));
        // after the filters: a match as written that the filters hide doesn't stop the typos
        ClipEntry[] entries = [Entry(0, "nme_heal_back", 1, ClipStatus.Added), Entry(1, "nme_head_back", 1)];
        var q = new ClipQuery { Text = "heal back" };
        Assert.Equal(["nme_heal_back"], Names(q.Apply(entries, ClipSort.Relevance)));
        Assert.Equal(["nme_head_back"], Names((q with { Status = StatusFilter.Vanilla }).Apply(entries, ClipSort.Relevance)));
    }

    [Fact]
    public void WordsTypedTogetherMatchOnNeighbouringWords()
    {
        var clips = List("civ_std_V0_N_hello_01", "civ_std_V1_N_hello", "civ_std_V0_N_hello_11", "Mch_Std_CvrInjFM_V1", "Mch_STDInj_V2_trav",
            "nme_Std_Inj_front", "mca_idlebreak_01", "x_v_lvl_1");
        // v then 1 on neighbouring words: not v anywhere and a 1 inside 01 or 11
        Assert.Equal(["civ_std_V1_N_hello", "Mch_Std_CvrInjFM_V1"], Found(clips, "V1"));
        Assert.Equal(["civ_std_V1_N_hello", "Mch_Std_CvrInjFM_V1"], Found(clips, "v1"));
        Assert.Equal(["civ_std_V1_N_hello"], Found(clips, "V1 hello"));
        Assert.Equal(["civ_std_V0_N_hello_01", "civ_std_V0_N_hello_11"], Found(clips, "hello V0"));
        // typed apart, each matches anywhere as a whole word or a word's start
        Assert.Equal(["x_v_lvl_1", "civ_std_V1_N_hello", "Mch_Std_CvrInjFM_V1", "civ_std_V0_N_hello_11"], Found(clips, "v 1"));
        // std then inj, side by side, however they are written; not std and inj apart
        Assert.Equal(["nme_Std_Inj_front", "Mch_STDInj_V2_trav"], Found(clips, "STDInj"));
        Assert.Equal(["nme_Std_Inj_front", "Mch_STDInj_V2_trav"], Found(clips, "StdInj"));
        Assert.Equal(["nme_Std_Inj_front", "Mch_STDInj_V2_trav"], Found(clips, "StdIn"));   // the last word may be a start
        Assert.Contains("Mch_Std_CvrInjFM_V1", Found(clips, "std inj"));
        Assert.Equal(ClipSearch.WholeWord, new ClipSearch(clips).Search("STDInj")[0].Score);
        // or their letters as one word of the clip
        Assert.Equal(["mca_idlebreak_01"], Found(clips, "IdleBreak"));
    }

    [Theory]
    [InlineData("1", new[] { "x_1", "x_12" })]           // a digit alone: a whole word or a word's start, not inside 01 or 21
    [InlineData("e", new[] { "x_emote" })]               // a letter alone: not inside hello
    [InlineData("el", new[] { "x_hello" })]              // two letters may be inside a word
    public void ASingleLetterOrDigitMatchesOnlyAWholeWordOrAWordsStart(string query, string[] found) =>
        Assert.Equal(found, Found(List("x_1", "x_12", "x_01", "x_21", "x_emote", "x_hello"), query));

    [Fact]
    public void HexIdsAreFound()
    {
        var hello = new ClipEntry { Index = 102, Id = 0x000001A625068E05, Name = "mca_emote_Hello", Bytes = ReadOnlyMemory<byte>.Empty };
        var clap = new ClipEntry { Index = 7, Id = 0x000001A6250600AA, Name = "mca_emote_Clap", Bytes = ReadOnlyMemory<byte>.Empty };
        var s = new ClipSearch([hello, clap]);
        Assert.Equal([hello], s.Search("0x000001A625068E05").Select(h => h.Clip));
        Assert.Equal(ClipSearch.WholeWord, s.Search("0x000001a625068e05")[0].Score);
        Assert.Equal([hello], s.Search("625068E05").Select(h => h.Clip));              // the tail, nine hex digits
        Assert.Equal([hello], s.Search("0x8e05").Select(h => h.Clip));
        Assert.Equal([clap, hello], s.Search("0x000001A62506").Select(h => h.Clip));   // both start so: the shorter name first
        Assert.Equal(2, s.Search("0x").Count);                                        // 0x alone: no text yet, every clip
        Assert.Equal([hello], s.Search("0x 8E05").Select(h => h.Clip));                // spaces after 0x left out
        Assert.Equal([hello], s.Search("0x 8E 05").Select(h => h.Clip));
        Assert.Empty(s.Search("8E05"));                                               // fewer than eight hex digits: a name search
        Assert.Empty(s.Search("0xhello"));                                            // 0x: IDs only
    }

    [Fact]
    public void FurtherFieldsWithWeightsAreSearched()
    {
        var clips = List("mca_emote_Hello", "mca_emote_Clap", "mca_hello_wave");
        // a readable name as a further field, of weight 0.9, for two of the clips
        IEnumerable<SearchField> Fields(ClipEntry c) =>
            [.. ClipSearch.NameAndId(c), new SearchField(c.Name == "mca_emote_Clap" ? "Applause" : c.Name == "mca_emote_Hello" ? "Greeting" : "", 0.9)];
        var s = new ClipSearch(clips, Fields);
        Assert.Equal(["mca_emote_Hello"], s.Search("greeting").Select(h => h.Clip.Name));
        Assert.Equal(0.9 * ClipSearch.WholeWord, s.Search("greeting")[0].Score, 9);
        Assert.Equal(["mca_emote_Hello"], s.Search("greting").Select(h => h.Clip.Name));
        Assert.Equal(["mca_emote_Clap"], Names(new ClipQuery { Text = "applause", Fields = Fields }.Apply(clips, ClipSort.Relevance)));
        Assert.True(new ClipQuery { Text = "applause", Fields = Fields }.Matches(clips[1]));
        Assert.Empty(new ClipQuery { Text = "applause" }.Apply(clips, ClipSort.Relevance));
        Assert.Throws<ArgumentException>(() => new ClipSearch(clips, c => [new SearchField(c.Name, 0)]));
        Assert.Throws<ArgumentException>(() => new ClipSearch(clips, c => [new SearchField(c.Name, double.NaN)]));
        // a source with a field of no text, or no fields, for some clips (a readable name the index lacks): read as nothing
        var gaps = new ClipSearch(clips, c => c.Name == "mca_emote_Clap" ? [new SearchField(null!)] : c.Name == "mca_hello_wave" ? null! : ClipSearch.NameAndId(c));
        Assert.Equal(["mca_emote_Hello"], gaps.Search("hello").Select(h => h.Clip.Name));
        Assert.Equal(["mca_emote_Hello"], gaps.Search("mca").Select(h => h.Clip.Name));
        Assert.Throws<ArgumentException>(() => new ClipSearch(clips, c => [new SearchField(c.Name, 1, SearchFieldKind.Words, ClipSearch.MostBonus + 1)]));
        Assert.Throws<ArgumentException>(() => new ClipSearch(clips, c => [new SearchField(c.Name, 1, SearchFieldKind.Words, -1)]));
    }

    [Fact]
    public void AWordInMoreThanOneFieldAddsALittle()
    {
        // the same name's word in each; a category of the word (bonus 1), a readable name saying it again (0.5), or the name alone
        var clips = List("x_death_a", "x_death_b", "x_death_c", "x_death_d");
        IEnumerable<SearchField> Fields(ClipEntry c) => c.Index switch
        {
            0 => [new SearchField(c.Name), new SearchField("Death threat", 1, SearchFieldKind.Prose)],
            1 => [new SearchField(c.Name), new SearchField("Death", 0.5, SearchFieldKind.Prose, 1)],
            2 => [new SearchField(c.Name)],
            _ => [new SearchField(c.Name), new SearchField("Death", 0.5, SearchFieldKind.Prose, 1), new SearchField("Soldier death", 1, SearchFieldKind.Prose),
                new SearchField("death", 0.7, SearchFieldKind.Prose), new SearchField("deaths", 0.7, SearchFieldKind.Prose)],
        };
        var hits = new ClipSearch(clips, Fields).Search("death");
        Assert.Equal(["x_death_d", "x_death_b", "x_death_a", "x_death_c"], hits.Select(h => h.Clip.Name));
        // the best field's score, then each further field's bonus, at most MostBonus in all: below the smallest step between kinds of
        // match (a typo's 12 and 9), so a better match always comes first
        Assert.Equal([100 + ClipSearch.MostBonus, 101, 100 + ClipSearch.FurtherField, 100], hits.Select(h => h.Score));
        // the bound the notes state: the kinds lie 3 apart, so the bonus stays under a step within a field of weight above 2/3 (the
        // glossary's 0.7, not a category's 0.5, whose whole word 50 lies 1 above a glossary meaning's word start 49)
        const double Glossary = Core.Index.ClipIndexEntry.GlossaryWeight, Category = Core.Index.ClipIndexEntry.CategoryWeight;
        Assert.True(ClipSearch.MostBonus < 3 * Glossary);
        Assert.False(ClipSearch.MostBonus < 3 * Category);
        Assert.Equal(1, ClipSearch.WholeWord * Category - ClipSearch.WordStart * Glossary, 9);
        // a word in one field only adds nothing; each query word adds its own
        Assert.Equal(ClipSearch.WholeWord, new ClipSearch(clips, Fields).Search("threat")[0].Score);
        Assert.Equal(2 * ClipSearch.WholeWord + ClipSearch.MostBonus, new ClipSearch(clips, Fields).Search("soldier death")[0].Score);
        // a typo forgiven in two fields: the clip whose category also holds it first
        Assert.Equal(["x_death_d", "x_death_b", "x_death_a", "x_death_c"], new ClipSearch(clips, Fields).Search("deth").Select(h => h.Clip.Name));
    }

    [Fact]
    public void TwoProseWordsTypedTogetherMatchWhenTheQueryWordIsLong()
    {
        var clips = List("mca_emote_DeathThreat", "mca_emote_Wave", "civ_reaction_01");
        IEnumerable<SearchField> Fields(ClipEntry c) =>
            [new SearchField(c.Name), new SearchField(c.Index == 1 ? "Greeting wave" : c.Index == 2 ? "Death, electrocuted" : "", 1, SearchFieldKind.Prose)];
        var s = new ClipSearch(clips, Fields);
        Assert.Equal(["mca_emote_Wave"], s.Search("greetingwave").Select(h => h.Clip.Name));
        Assert.Equal(ClipSearch.AcrossWords, s.Search("greetingwave")[0].Score);
        Assert.Equal(["mca_emote_Wave"], s.Search("greetingwa").Select(h => h.Clip.Name));        // the second word's start
        Assert.Equal(["civ_reaction_01"], s.Search("deathelectro").Select(h => h.Clip.Name));
        // from the first word's start, across exactly one break, eight letters or more
        Assert.Empty(s.Search("etingwave"));
        Assert.Empty(s.Search("greetingwavex"));
        Assert.Empty(s.Search("deathele"[..7]));
        Assert.Empty(s.Search("hel"));                                                             // deat(h el)ectrocuted, as ever
        // a name's run across words as before: the name's own capitals
        Assert.Equal(["mca_emote_DeathThreat"], s.Search("deaththreat").Select(h => h.Clip.Name));
    }

    [Fact]
    public void AProseWordsInflectionMatchesIt()
    {
        var clips = List("mcm_nwp_fm_std_V1_cycle", "mca_walk_01", "civ_carry_body", "x_move");
        IEnumerable<SearchField> Fields(ClipEntry c) =>
            [new SearchField(c.Name), new SearchField(c.Index switch { 0 => "Holstered walk, standing", 2 => "Carry a body", 3 => "Move", _ => "" }, 1, SearchFieldKind.Prose)];
        var s = new ClipSearch(clips, Fields);
        // in a prose field only: the name's "walk" isn't matched by "walking"
        foreach (var q in new[] { "walking", "walked" })
        {
            Assert.Equal(["mcm_nwp_fm_std_V1_cycle"], s.Search(q).Select(h => h.Clip.Name));
            Assert.Equal(ClipSearch.Inflected, s.Search(q)[0].Score);
        }
        Assert.Contains("mcm_nwp_fm_std_V1_cycle", s.Search("walks").Select(h => h.Clip.Name));   // (and the name's walk, one edit off)
        Assert.Equal(["mcm_nwp_fm_std_V1_cycle"], s.Search("holstered walking").Select(h => h.Clip.Name));
        Assert.Equal(["civ_carry_body"], s.Search("carries").Select(h => h.Clip.Name));
        Assert.Equal(["x_move"], s.Search("moving").Select(h => h.Clip.Name));
        Assert.Equal(["x_move"], s.Search("moved").Select(h => h.Clip.Name));
        // the light rule's forms: three letters or more, no plural of a word in ss
        Assert.Contains("run", ClipSearch.Stems("running"));
        Assert.Contains("stop", ClipSearch.Stems("stopped"));
        Assert.Contains("move", ClipSearch.Stems("moves"));
        Assert.Empty(ClipSearch.Stems("class"));
        Assert.Empty(ClipSearch.Stems("sing"));
        Assert.Empty(ClipSearch.Stems("walk"));
        // a dropped-e form of four letters or more: "thing" isn't "the", nor "shed" "she"
        Assert.Empty(ClipSearch.Stems("thing"));
        Assert.DoesNotContain("she", ClipSearch.Stems("shed"));
        Assert.Contains("move", ClipSearch.Stems("moved"));
        var the = new ClipSearch(List("x_the_one"), c => [new SearchField(c.Name), new SearchField("Soldier hit, the head", 1, SearchFieldKind.Prose)]);
        Assert.Empty(the.Search("thing"));
    }

    [Fact]
    public void AShortChanceRunComesAfterTyposALongOneDoesNot()
    {
        // a word no clip holds whole, run across a name's words: under eight letters mostly chance (6), from eight on a name's words
        // typed together (30)
        var s = new ClipSearch(List("mca_emote_ImWatchingYou", "Mch_HG_Std_VeryLowHealth_V0", "mca_GasMask_on", "mil_grenade_throw"));
        Assert.Equal(ClipSearch.AcrossWords, s.Search("imwatchingyou")[0].Score);
        Assert.Equal(ClipSearch.AcrossWords, s.Search("imwatching")[0].Score);
        Assert.Equal(ClipSearch.AcrossWords, s.Search("verylowhealth")[0].Score);
        Assert.Equal(ClipSearch.AcrossUnheld, s.Search("gasmask")[0].Score);   // seven letters
        Assert.Equal(ClipSearch.AcrossUnheld, s.Search("deth")[0].Score);
    }

    [Fact]
    public void APhrasesLastWordCountsAloneItsLeadingWordsOnlyWithIt()
    {
        // "left shoulder" for two clips, "right shoulder" for one, "turn left" as plain prose for another, and the compass's
        // "north-east", "north-west" and "south-east" as compounds for the last three; the names say right, left and aim besides
        var clips = List("x_LookRight_LHS", "x_TurnL_aim", "x_RHS_grip", "x_aim_LHS", "x_walk_NE", "x_walk_NW", "x_run_SE");
        IEnumerable<SearchField> Fields(ClipEntry c) => [new SearchField(c.Name), c.Index switch
        {
            0 or 3 => new SearchField("left shoulder", 0.7, SearchFieldKind.Phrase),
            1 => new SearchField("turn left", 0.7, SearchFieldKind.Prose),
            2 => new SearchField("right shoulder", 0.7, SearchFieldKind.Phrase),
            4 => new SearchField("north-east", 0.7, SearchFieldKind.Compound),
            5 => new SearchField("north-west", 0.7, SearchFieldKind.Compound),
            _ => new SearchField("south-east", 0.7, SearchFieldKind.Compound),
        }];
        var s = new ClipSearch(clips, Fields);
        string[] Found(string q) => s.Search(q).Select(h => h.Clip.Name).Order(StringComparer.Ordinal).ToArray();
        // the last word alone, or beside any other word, in any order
        Assert.Equal(["x_LookRight_LHS", "x_RHS_grip", "x_aim_LHS"], Found("shoulder"));
        Assert.Equal(["x_aim_LHS"], Found("shoulder aim"));
        Assert.Equal(["x_aim_LHS"], Found("aim shoulder"));
        // a leading word only with the last: alone or beside another word it finds nothing through the phrase, as one word or several
        Assert.Equal(["x_TurnL_aim"], Found("left"));
        Assert.Equal(["x_TurnL_aim"], Found("aim left"));
        Assert.Equal(["x_LookRight_LHS"], Found("right"));
        // with it: the whole phrase, in any order, the last as typed so far
        Assert.Equal(["x_LookRight_LHS", "x_aim_LHS"], Found("left shoulder"));
        Assert.Equal(["x_LookRight_LHS", "x_aim_LHS"], Found("shoulder left"));
        Assert.Equal(["x_LookRight_LHS", "x_aim_LHS"], Found("left sh"));
        Assert.Equal(["x_aim_LHS"], Found("left shoulder aim"));
        // a phrase's two words typed together hold both, beside another word too
        Assert.Equal(["x_LookRight_LHS", "x_aim_LHS"], Found("leftshoulder"));
        Assert.Equal(["x_aim_LHS"], Found("leftshoulder aim"));
        // phrases with the same last word stand against each other: "right shoulder" brings no left-shoulder clip in through a name
        // that says right, "left shoulder" no right-shoulder clip
        Assert.Equal(["x_RHS_grip"], Found("right shoulder"));
        Assert.DoesNotContain("x_RHS_grip", Found("left shoulder"));
        // but only by a whole word: a side word typed so far ("ri" on the way to right or to rifle) leaves the other shoulder in
        Assert.Equal(["x_LookRight_LHS", "x_RHS_grip"], Found("shoulder ri"));
        Assert.Equal(["x_LookRight_LHS", "x_RHS_grip"], Found("shoulder rig"));
        Assert.Equal(["x_RHS_grip"], Found("shoulder right"));
        Assert.Equal(["x_RHS_grip"], Found("shoulder rightshoulder"));
        // a compound's words each count alone, beside another word too: east finds the diagonals to the right, north those ahead
        Assert.Equal(["x_run_SE", "x_walk_NE"], Found("east"));
        Assert.Equal(["x_walk_NE"], Found("walk east"));
        Assert.Equal(["x_walk_NE", "x_walk_NW"], Found("north"));
        Assert.Equal(["x_walk_NE", "x_walk_NW"], Found("walk north"));
        Assert.Equal(["x_walk_NE", "x_walk_NW"], Found("north walk"));
        Assert.Equal(["x_walk_NE", "x_walk_NW"], Found("walk nort"));
        Assert.Equal(["x_run_SE"], Found("south"));
        Assert.Equal(["x_run_SE"], Found("run south"));
        // compounds that share a word stand against each other by their other words: two compass words find their diagonal only
        Assert.Equal(["x_walk_NE"], Found("north east"));
        Assert.Equal(["x_walk_NE"], Found("north-east"));
        Assert.Equal(["x_walk_NE"], Found("east north"));
        Assert.Equal(["x_walk_NE"], Found("northeast"));
        Assert.Equal(["x_walk_NE"], Found("walk north east"));
        Assert.Equal(["x_walk_NE"], Found("walk northeast"));
        Assert.Equal(["x_walk_NE"], Found("northeast walk"));
        Assert.Equal(["x_walk_NW"], Found("north west"));
        Assert.Equal(["x_run_SE"], Found("south east"));
        Assert.Equal(["x_run_SE"], Found("southeast"));
        Assert.Empty(Found("walk south east"));
        // plain prose matches word by word, as ever
        Assert.Equal(["x_TurnL_aim"], Found("turn left"));
        // a phrase of one word is plain prose
        var one = new ClipSearch(clips, c => [new SearchField(c.Name), new SearchField("draw", 1, SearchFieldKind.Phrase)]);
        Assert.Equal(clips.Length, one.Search("draw x").Count);
    }

    [Fact]
    public void MatchesAgreesWithApply()
    {
        var clips = List("nme_death_head_back", "nme_heal_self", "nme_dead_body", "civ_std_V0_N_hello_01", "civ_std_V1_N_hello", "nme_forehead");
        foreach (var text in new[] { "head", "deth", "heal back", "V1", "v 1", "hello", "_", "0x", "0xZZ", "zzzz" })
        {
            var q = new ClipQuery { Text = text };
            var shown = q.Apply(clips);
            foreach (var c in clips)
            {
                Assert.Equal(shown.Contains(c), q.Matches(c, clips));
                Assert.Equal(q.Apply([c]).Count > 0, q.Matches(c));
            }
        }
        // a clip judged as a list of its own takes typos for a word it lacks; in its list, not when another clip holds the word whole
        var head = new ClipQuery { Text = "head" };
        Assert.True(head.Matches(clips[1]));
        Assert.False(head.Matches(clips[1], clips));
        Assert.False(head.Matches(clips[0], clips[1..]));   // not in the list
    }

    [Fact]
    public void AHeaderClickSortsUpThenDownThenGoesBackToBestMatch()
    {
        static (ClipSort Sort, bool Descending) Click((ClipSort Sort, bool Descending) s, ClipSort column) => ClipQuery.NextSort(s.Sort, s.Descending, column);
        (ClipSort Sort, bool Descending) best = (ClipSort.Relevance, false);
        var up = Click(best, ClipSort.Name);
        var down = Click(up, ClipSort.Name);
        Assert.Equal((ClipSort.Name, false), up);
        Assert.Equal((ClipSort.Name, true), down);
        Assert.Equal(best, Click(down, ClipSort.Name));                     // the third click
        Assert.Equal((ClipSort.Length, false), Click(up, ClipSort.Length));   // another column: up, whatever came before
        Assert.Equal((ClipSort.Length, false), Click(down, ClipSort.Length));
        Assert.Equal(best, Click(up, ClipSort.Relevance));
        // the list follows the three clicks: best match, by name up, by name down, best match again
        var clips = List("mca_sidewalk_04", "mca_walking_02", "mca_walk_01");
        var q = new ClipQuery { Text = "walk" };
        string[] Shown((ClipSort Sort, bool Descending) s) => Names(q.Apply(clips, s.Sort, s.Descending));
        Assert.Equal(["mca_walk_01", "mca_walking_02", "mca_sidewalk_04"], Shown(best));
        Assert.Equal(["mca_sidewalk_04", "mca_walking_02", "mca_walk_01"], Shown(up));
        Assert.Equal(["mca_walk_01", "mca_walking_02", "mca_sidewalk_04"], Shown(down));
        Assert.Equal(Shown(best), Shown(Click(down, ClipSort.Name)));
    }

    [Fact]
    public void AListsSearchIsBuiltOnceAndAgainWhenTheListChanges()
    {
        var clips = List("a_walk", "b_run");
        var s = ClipSearch.For(clips);
        Assert.Same(s, ClipSearch.For(clips));
        clips[1] = new ClipEntry { Index = 1, Id = 9, Name = "c_jump", Bytes = ReadOnlyMemory<byte>.Empty };
        var t = ClipSearch.For(clips);
        Assert.NotSame(s, t);
        Assert.Equal(["c_jump"], t.Search("jump").Select(h => h.Clip.Name));
        Assert.NotSame(t, ClipSearch.For(clips, c => [new SearchField(c.Name)]));
        // a few field sources on one list are kept side by side: each is built once, not again at every switch between them
        Func<ClipEntry, IEnumerable<SearchField>> a = c => [new SearchField(c.Name)], b = c => [new SearchField(c.Name, 0.5)];
        var sa = ClipSearch.For(clips, a);
        var sb = ClipSearch.For(clips, b);
        Assert.NotSame(sa, sb);
        for (int i = 0; i < 3; i++)
        {
            Assert.Same(sa, ClipSearch.For(clips, a));
            Assert.Same(sb, ClipSearch.For(clips, b));
            Assert.Same(t, ClipSearch.For(clips));
        }
    }

    [Fact]
    public void FiltersAndColumnSortsStillApplyToTheResults()
    {
        ClipEntry[] clips =
        [
            Entry(5, "mca_walk_01", 2.0f),
            Entry(3, "mca_walking_02", 1.0f, ClipStatus.Replaced),
            Entry(8, "nme_walk_03", 3.0f, ClipStatus.Added),
            Entry(1, "mca_sidewalk_04", 0.5f),
            Entry(2, "mca_run_05", 1.0f),
        ];
        var q = new ClipQuery { Text = "walk" };
        string[] best = ["mca_walk_01", "nme_walk_03", "mca_walking_02", "mca_sidewalk_04"];
        Assert.Equal(best, Names(q.Apply(clips, ClipSort.Relevance)));                       // no column chosen: best match first
        Assert.Equal(Enumerable.Reverse(best), Names(q.Apply(clips, ClipSort.Relevance, descending: true)));
        Assert.Equal(["mca_sidewalk_04", "mca_walking_02", "mca_walk_01", "nme_walk_03"], Names(q.Apply(clips, ClipSort.Length)));   // a column wins
        Assert.Equal(["nme_walk_03", "mca_walk_01", "mca_walking_02", "mca_sidewalk_04"], Names(q.Apply(clips, ClipSort.Length, descending: true)));
        // by name as the column always sorted: ordinal, case ignored (so "walki" before "walk_")
        Assert.Equal(["mca_sidewalk_04", "mca_walking_02", "mca_walk_01", "nme_walk_03"], Names(q.Apply(clips, ClipSort.Name)));
        Assert.Equal([1, 3, 5, 8], q.Apply(clips).Select(c => c.Index));                    // the default: index order, as before
        Assert.Equal(["mca_walk_01", "mca_sidewalk_04"], Names((q with { Status = StatusFilter.Vanilla }).Apply(clips, ClipSort.Relevance)));
        Assert.Equal(["mca_walk_01", "mca_walking_02", "mca_sidewalk_04"], Names((q with { Family = "mca" }).Apply(clips, ClipSort.Relevance)));
        Assert.Equal(["mca_walk_01", "mca_walking_02"], Names((q with { MinSeconds = 1, MaxSeconds = 2 }).Apply(clips, ClipSort.Relevance)));
        Assert.Equal([1, 2, 3, 5, 8], new ClipQuery().Apply(clips, ClipSort.Relevance).Select(c => c.Index));   // no text: index order
        Assert.True(q.Matches(clips[0]));
        Assert.False(q.Matches(clips[4]));
        Assert.False((q with { Status = StatusFilter.Added }).Matches(clips[0]));
        // a clip judged on its own tries typos for a word it doesn't hold
        Assert.True(new ClipQuery { Text = "wakl" }.Matches(clips[0]));
    }

    [Fact]
    public void TenThousandNamesAreSearchedInAFewMillisecondsAKeystroke()
    {
        // names built like the game's: family, weapon, stance, then action words and a number
        string[][] parts =
        [
            ["mca", "mcm", "nme", "civ", "pmc", "mil", "vip", "Mch"],
            ["rif", "hg", "ar", "smg", "nwp", "sr", "lmg"],
            ["std", "crh", "prnFD", "prnFU", "col", "ros"],
            ["V0", "V1", "N", "fm", "ale", "inj"],
            ["walk", "run", "sprint", "death", "reaction", "reload", "cover", "emote", "IdleBreak", "PeekLeft", "ExitR", "Hello", "turn", "aim"],
            ["fwd", "back", "left", "right", "head", "chest", "start", "stop", "loop", "regular", "special"],
        ];
        var rnd = new Random(1);
        var clips = Enumerable.Range(0, 10_000).Select(i => new ClipEntry
        {
            Index = i, Id = 0x000001DE00000000UL + (ulong)i, Bytes = ReadOnlyMemory<byte>.Empty,
            Name = string.Join("_", parts.Select(p => p[rnd.Next(p.Length)])) + $"_{rnd.Next(1, 5):D2}",
        }).ToList();
        var times = Keystrokes(clips, ["death head back", "emo hel", "greting", "0x000001DE00001234", "sprnt fwd loop"]);
        output.WriteLine($"10,000 names: {Summary(times)}");
        Assert.True(times.Average() < 20, Summary(times));
        Assert.True(times.Max() < 250, Summary(times));
    }

    /// <summary>The time of each keystroke of each query, typed one letter at a time, as the Library's list runs it (filters, then best
    /// match first), after one search to build the list's search and warm up; with <paramref name="fields"/> (the clip index's, say) as
    /// the search's fields.</summary>
    internal static List<double> Keystrokes(IReadOnlyList<ClipEntry> clips, string[] queries, Func<ClipEntry, IEnumerable<SearchField>>? fields = null)
    {
        new ClipQuery { Text = "warm up", Fields = fields }.Apply(clips, ClipSort.Relevance);
        var times = new List<double>();
        foreach (var q in queries)
            for (int n = 1; n <= q.Length; n++)
            {
                long t0 = Stopwatch.GetTimestamp();
                new ClipQuery { Text = q[..n], Fields = fields }.Apply(clips, ClipSort.Relevance);
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
        return times;
    }

    internal static string Summary(List<double> times)
    {
        var s = times.Order().ToList();
        return FormattableString.Invariant($"{s.Count} keystrokes, mean {s.Average():F2} ms, median {s[s.Count / 2]:F2} ms, 95% under {s[(int)(s.Count * 0.95)]:F2} ms, worst {s[^1]:F2} ms");
    }
}

/// <summary>The search on the vanilla install's 9,938 clips: the example queries, typos, IDs and the time per keystroke.</summary>
[Collection("install")]
public sealed class SearchGameTests(InstallFixture f, ITestOutputHelper output)
{
    List<ClipEntry> Search(string text)
    {
        var r = new ClipQuery { Text = text }.Apply(f.Install.Clips, ClipSort.Relevance);
        output.WriteLine($"'{text}': {r.Count} found; first {string.Join(", ", r.Take(5).Select(c => c.Name))}");
        return r;
    }

    static bool HasWords(ClipEntry c, params string[] words) => words.All(ClipSearch.Words(c.Name).Contains);

    [GameDataFact]
    public void HelloEmoteAndEmoHelFindTheHelloEmoteFirst()
    {
        // the one clip holding both words whole (or both starts) comes first, however the words are typed
        foreach (var q in new[] { "hello emote", "emo hel", "Emote Hello", "mca_emote_hello" })
            Assert.Equal("mca_emote_Hello", Search(q)[0].Name);
    }

    [GameDataFact]
    public void DeathHeadBackFindsOnlyClipsHoldingAllThree()
    {
        var r = Search("death head back");
        Assert.NotEmpty(r);
        // every clip found holds all three as written (no typo let in a "dead", "heal" or "black")
        Assert.All(r, c => Assert.All(new[] { "death", "head", "back" }, w => Assert.Contains(w, c.Name, StringComparison.OrdinalIgnoreCase)));
        var regular = f.Install.Clips.Where(c => c.Name.Contains("_death_regular_head_back", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(4, regular.Count);
        Assert.All(regular, c => Assert.Contains(c, r));
        // those holding the three as whole words come before any that holds one only as part of a word
        int whole = r.Count(c => HasWords(c, "death", "head", "back"));
        Assert.Equal(8, whole);
        Assert.All(r.Take(whole), c => Assert.True(HasWords(c, "death", "head", "back"), c.Name));
    }

    static bool Holds(ClipEntry c, string word) => ClipSearch.Words(c.Name).Contains(word);

    static bool Neighbours(ClipEntry c, string a, string b)
    {
        var ws = ClipSearch.Words(c.Name);
        for (int i = 0; i + 1 < ws.Count; i++)
            if (ws[i] == a && ws[i + 1] == b) return true;
        return false;
    }

    [GameDataFact]
    public void CommonTyposFindTheWordsTheyMean()
    {
        var g = f.Install;
        var r = Search("deth head back");
        Assert.All(g.Clips.Where(c => c.Name.Contains("_death_regular_head_back", StringComparison.OrdinalIgnoreCase)), c => Assert.Contains(c, r));
        Assert.All(r, c => Assert.True(Holds(c, "head") && Holds(c, "back"), c.Name));
        // each held only inside a word, across words or as the start of another word (grena-de-th-row, grabTRUNK, rigth, outs-ide-l):
        // the clips holding the word meant are found too, every one of them
        foreach (var (typo, word) in new[] { ("deth", "death"), ("trun", "turn"), ("rigt", "right"), ("idel", "idle"), ("slef", "self") })
        {
            var found = Search(typo).ToHashSet();
            var meant = g.Clips.Where(c => Holds(c, word)).ToList();
            Assert.NotEmpty(meant);
            Assert.All(meant, c => Assert.Contains(c, found));
        }
        Assert.Contains(Search("trun left"), c => Holds(c, "turn") && Holds(c, "left"));
        Assert.Contains(Search("idel cool"), c => Holds(c, "idle") && Holds(c, "cool"));
        Assert.Contains(Search("slef heal"), c => Holds(c, "self") && ClipSearch.Words(c.Name).Any(w => w.StartsWith("heal", StringComparison.Ordinal)));
        // a typo ranks below every match as written: the clips holding a word as typed come before those found through its typo
        var rigt = new ClipQuery { Text = "rigt" }.Apply(g.Clips, ClipSort.Relevance);
        Assert.All(rigt.Take(2), c => Assert.True(Holds(c, "rigth"), c.Name));
    }

    [GameDataFact]
    public void EverydayTyposOfFrequentWordsFindTheirClips()
    {
        // one word of a frequent word typed with two letters swapped or one left out, and two words of a real name with one swapped:
        // the clip or word meant is among the results every time
        var g = f.Install;
        var s = ClipSearch.For(g.Clips);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in g.Clips) foreach (var w in ClipSearch.Words(c.Name).Distinct()) counts[w] = counts.GetValueOrDefault(w) + 1;
        var common = counts.Where(kv => kv.Key.Length is >= 4 and <= 12 && kv.Key.All(char.IsLetter) && kv.Value >= 10)
            .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(150).ToList();
        int tried = 0;
        var missed = new List<string>();
        foreach (var w in common)
        {
            var typos = Enumerable.Range(0, w.Length - 1).Select(i => w[..i] + w[i + 1] + w[i] + w[(i + 2)..])
                .Concat(Enumerable.Range(1, w.Length - 1).Select(i => w.Remove(i, 1)));
            foreach (var t in typos.Distinct().Where(t => t != w && t.Length >= 4 && !counts.ContainsKey(t)))
            {
                tried++;
                if (!s.Search(t).Any(h => Holds(h.Clip, w))) missed.Add($"{w}->{t}");
            }
        }
        var rnd = new Random(7);
        int pairs = 0;
        while (pairs < 1500)
        {
            var c = g.Clips[rnd.Next(g.Clips.Count)];
            var ws = ClipSearch.Words(c.Name).Where(x => x.Length >= 4 && x.All(char.IsLetter)).Distinct().ToList();
            if (ws.Count < 2) continue;
            var a = ws[rnd.Next(ws.Count)];
            var b = ws.Where(x => x != a).ElementAt(rnd.Next(ws.Count - 1));
            int i = rnd.Next(b.Length - 1);
            string typo = b[..i] + b[i + 1] + b[i] + b[(i + 2)..];
            if (typo == b || counts.ContainsKey(typo)) continue;
            pairs++;
            if (!s.Search(a + " " + typo).Any(h => h.Clip == c)) missed.Add($"{a} {typo} ({c.Name})");
        }
        output.WriteLine($"{tried} one-word typos and {pairs} two-word queries: {missed.Count} missed {string.Join(", ", missed.Take(20))}");
        Assert.Empty(missed);
    }

    [GameDataFact]
    public void WordsTypedTogetherFindTheClipsHoldingThemSideBySide()
    {
        var g = f.Install;
        var v1 = Search("V1");
        var holding = g.Clips.Where(c => Neighbours(c, "v", "1")).ToHashSet();
        output.WriteLine($"{holding.Count} clips hold V1");
        Assert.Equal(holding.Count, v1.Count);
        Assert.True(holding.SetEquals(v1));
        var hello = Search("V1 hello");
        Assert.All(hello, c => Assert.True(Neighbours(c, "v", "1"), c.Name));
        Assert.All(Search("V0 hello"), c => Assert.True(Neighbours(c, "v", "0"), c.Name));
        Assert.All(Search("V1 death"), c => Assert.True(Neighbours(c, "v", "1"), c.Name));
        // std then inj side by side, whole, first; then std followed by a word inj starts (Std_Injured); never the two apart
        var std = Search("STDInj");
        Assert.StartsWith("Mch_STDInj_", std[0].Name, StringComparison.Ordinal);
        int whole = g.Clips.Count(c => Neighbours(c, "std", "inj"));
        Assert.All(std.Take(whole), c => Assert.True(Neighbours(c, "std", "inj"), c.Name));
        Assert.All(std.Skip(whole), c => Assert.Contains("Std_Inj", c.Name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(std, c => c.Name.StartsWith("Mch_Std_CvrInjFM", StringComparison.Ordinal));
    }

    [GameDataFact]
    public void GretingFindsTheGreetingClipsThroughAReadableName()
    {
        var g = f.Install;
        // The game names its greeting clips Hello (mca_emote_Hello and the Reaction_Hello clips): no vanilla clip name holds "greet",
        // so a typo of "greeting" can reach them only through a readable name, a further field. Here the hello clips get "Greeting",
        // weighed as the name.
        Assert.DoesNotContain(g.Clips, c => c.Name.Contains("greet", StringComparison.OrdinalIgnoreCase));
        var greeting = g.Clips.Where(c => ClipSearch.Words(c.Name).Contains("hello")).ToHashSet();
        Assert.Equal(7, greeting.Count);
        IEnumerable<SearchField> Fields(ClipEntry c) =>
            greeting.Contains(c) ? [.. ClipSearch.NameAndId(c), new SearchField("Greeting")] : ClipSearch.NameAndId(c);
        var r = new ClipQuery { Text = "greting", Fields = Fields }.Apply(g.Clips, ClipSort.Relevance);
        output.WriteLine($"'greting' with the readable names: {r.Count} found; first {string.Join(", ", r.Take(5).Select(c => c.Name))}");
        Assert.Equal(greeting.Select(c => c.Name).Order(), r.Take(greeting.Count).Select(c => c.Name).Order());
        Assert.Equal("mca_emote_Hello", r[0].Name);
        // by the names alone a typo of a word they hold is forgiven: "hlelo" (two letters swapped) finds the same clips first
        var t = Search("hlelo");
        Assert.Equal(greeting.Select(c => c.Name).Order(), t.Take(greeting.Count).Select(c => c.Name).Order());
        Search("greting");
    }

    [GameDataFact]
    public void HexIdsStillFindTheClip()
    {
        Assert.Equal(["mca_emote_Hello"], Search("0x000001A625068E05").Select(c => c.Name));
        Assert.Equal("mca_emote_Hello", Search("625068E05")[0].Name);
        Assert.Equal("mca_emote_Hello", Search("0x625068e05")[0].Name);
    }

    [GameDataFact]
    public void EveryKeystrokeSearchesAllClipsInAFewMilliseconds()
    {
        var g = f.Install;
        Assert.Equal(9_938, g.Clips.Count);
        long t0 = Stopwatch.GetTimestamp();
        var built = new ClipSearch(g.Clips);
        output.WriteLine(FormattableString.Invariant(
            $"built over {built.Clips.Count:N0} clips ({built.VocabularySize:N0} distinct words) in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms"));
        var times = SearchTests.Keystrokes(g.Clips,
            ["hello emote", "emo hel", "death head back", "greting", "0x000001A625068E05", "sprint fwd", "reload rifle", "mca_emote_hello", "cover left peek", "hlelo"]);
        output.WriteLine($"vanilla, best match first: {SearchTests.Summary(times)}");
        // with a column sort and a filter, as the Library runs them when chosen
        var sorted = new List<double>();
        foreach (var q in new[] { "death head back", "greting" })
            for (int n = 1; n <= q.Length; n++)
            {
                long s0 = Stopwatch.GetTimestamp();
                new ClipQuery { Text = q[..n], Status = StatusFilter.Vanilla, MaxSeconds = 5 }.Apply(g.Clips, ClipSort.Length, descending: true);
                sorted.Add(Stopwatch.GetElapsedTime(s0).TotalMilliseconds);
            }
        output.WriteLine($"vanilla, filtered and sorted by length: {SearchTests.Summary(sorted)}");
        Assert.True(times.Average() < 20, SearchTests.Summary(times));
        Assert.True(sorted.Average() < 20, SearchTests.Summary(sorted));
        Assert.True(times.Concat(sorted).Max() < 250, "worst keystroke: " + SearchTests.Summary(times.Concat(sorted).ToList()));
    }
}
