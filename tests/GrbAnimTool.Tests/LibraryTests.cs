using System.Numerics;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;

namespace GrbAnimTool.Tests;

/// <summary>Tests of the Library screen's logic, with no game data: search and filters, the play head, a clip's facts, the mover's
/// path, the command line and the remembered settings.</summary>
public sealed class LibraryTests
{
    static ClipEntry Entry(int index, ulong id, string name, float seconds, ClipStatus status = ClipStatus.Vanilla, int tracks = 1)
    {
        var parts = Enumerable.Range(0, tracks).Select(i => ((uint)i + 1, Synthetic.PosCurve([0], Vector3.Zero))).ToArray();
        var bytes = Synthetic.Clip(name, id, seconds, parts);
        return new ClipEntry { Index = index, Id = id, Name = name, Bytes = bytes, Record = ClipRecord.Parse(bytes), Status = status };
    }

    static readonly ClipEntry[] Clips =
    [
        Entry(5, 0x000001DE00AA0001, "mca_emote_Hello", 4.5f),
        Entry(2, 0x000001DE00BB0002, "mcm_nwp_fm_std_V1_cycle", 1.1f, ClipStatus.Replaced, tracks: 3),
        Entry(9, 0x000001DE00CC0003, "nme_dth_front_01", 2.0f, ClipStatus.Added, tracks: 2),
        Entry(7, 0x000001DE00DD0004, "MCA_Sprint_Fwd", 0.8f),
        Entry(1, 0x00000000DEADBEEF, "plain", 1.1f),
    ];

    static string[] Names(IEnumerable<ClipEntry> c) => c.Select(x => x.Name).ToArray();

    [Theory]
    [InlineData("mca_emote_Hello", "mca")]
    [InlineData("MCA_Sprint_Fwd", "mca")]
    [InlineData("plain", "plain")]
    [InlineData("_odd", "_odd")]
    public void TheFamilyIsTheNamesFirstPart(string name, string family) => Assert.Equal(family, ClipQuery.FamilyOf(name));

    [Fact]
    public void FamiliesAreCountedAndSorted()
    {
        var f = ClipQuery.Families(Clips);
        Assert.Equal(["mca", "mcm", "nme", "plain"], f.Select(x => x.Family).ToArray());
        Assert.Equal(2, f[0].Count);
    }

    [Fact]
    public void AnEmptyQueryLetsEverythingThroughInIndexOrder()
    {
        var r = new ClipQuery().Apply(Clips);
        Assert.Equal([1, 2, 5, 7, 9], r.Select(c => c.Index).ToArray());
    }

    [Theory]
    [InlineData("hello", new[] { "mca_emote_Hello" })]
    [InlineData("SPRINT", new[] { "MCA_Sprint_Fwd" })]
    [InlineData("  dth ", new[] { "nme_dth_front_01" })]
    [InlineData("0x00BB", new[] { "mcm_nwp_fm_std_V1_cycle" })]
    [InlineData("0xdeadbeef", new[] { "plain" })]
    [InlineData("deadbeef", new[] { "plain" })]            // eight hex digits match IDs too
    [InlineData("dead", new string[0])]                    // fewer are only a name search
    [InlineData("0x", new[] { "mca_emote_Hello", "mcm_nwp_fm_std_V1_cycle", "nme_dth_front_01", "MCA_Sprint_Fwd", "plain" })]   // no text yet
    [InlineData("0x 00BB", new[] { "mcm_nwp_fm_std_V1_cycle" })]
    [InlineData("0xZZ", new string[0])]
    [InlineData("zzz", new string[0])]
    public void SearchMatchesNamesAndHexIds(string text, string[] expected) =>
        Assert.Equal(expected.Order().ToArray(), Names(new ClipQuery { Text = text }.Apply(Clips)).Order().ToArray());

    [Fact]
    public void StatusFamilyAndLengthFilter()
    {
        Assert.Equal(["mcm_nwp_fm_std_V1_cycle"], Names(new ClipQuery { Status = StatusFilter.Replaced }.Apply(Clips)));
        Assert.Equal(["nme_dth_front_01"], Names(new ClipQuery { Status = StatusFilter.Added }.Apply(Clips)));
        Assert.Equal(3, new ClipQuery { Status = StatusFilter.Vanilla }.Apply(Clips).Count);
        Assert.Equal(["mca_emote_Hello", "MCA_Sprint_Fwd"], Names(new ClipQuery { Family = "mca" }.Apply(Clips, ClipSort.Name)));
        Assert.Equal(["plain", "mcm_nwp_fm_std_V1_cycle", "nme_dth_front_01"],
            Names(new ClipQuery { MinSeconds = 1.0f, MaxSeconds = 2.0f }.Apply(Clips)));
        Assert.Empty(new ClipQuery { Family = "mca", Status = StatusFilter.Added }.Apply(Clips));
    }

    [Fact]
    public void SortsAreStableByIndexAndCanBeReversed()
    {
        // two clips of 1.1 s: the lower index first, and the reverse order reverses that too
        Assert.Equal([7, 1, 2, 9, 5], new ClipQuery().Apply(Clips, ClipSort.Length).Select(c => c.Index).ToArray());
        Assert.Equal([5, 9, 2, 1, 7], new ClipQuery().Apply(Clips, ClipSort.Length, descending: true).Select(c => c.Index).ToArray());
        Assert.Equal("mca_emote_Hello", new ClipQuery().Apply(Clips, ClipSort.Name)[0].Name);
        Assert.Equal(2, new ClipQuery().Apply(Clips, ClipSort.Tracks, descending: true)[0].Index);
        Assert.Equal([1, 5, 7, 2, 9], new ClipQuery().Apply(Clips, ClipSort.Status).Select(c => c.Index).ToArray());
    }

    [Theory]
    [InlineData("mca_emote_hello", 5)]
    [InlineData("0x000001DE00CC0003", 9)]
    [InlineData("1de00cc0003", 9)]
    [InlineData("DEADBEEF", 1)]
    public void FindsByNameOrHexId(string text, int index) => Assert.Equal(index, ClipQuery.Find(Clips, text)!.Index);

    [Fact]
    public void FindsNothingForAnUnknownClip()
    {
        Assert.Null(ClipQuery.Find(Clips, "nope_not_here"));
        Assert.Null(ClipQuery.Find(Clips, "0x1234"));
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("1.5", true, 1.5f)]
    [InlineData("1,5", true, 1.5f)]
    [InlineData("0", true, 0f)]
    [InlineData("-1", false, null)]
    [InlineData("abc", false, null)]
    [InlineData("NaN", false, null)]
    public void LengthBoundsParse(string text, bool ok, float? value)
    {
        Assert.Equal(ok, ClipQuery.TryParseSeconds(text, out var v));
        Assert.Equal(value, v);
    }

    [Fact]
    public void TheClockLoopsAndStops()
    {
        var c = new PlaybackClock();
        c.Reset(1.0f, 60);
        Assert.False(c.Advance(0.5));            // paused: nothing moves
        c.Play();
        Assert.True(c.Advance(0.25));
        Assert.Equal(0.25f, c.Time, 5);
        Assert.Equal(15, c.Tick);
        c.Advance(0.85);                          // past the end: wraps
        Assert.Equal(0.10f, c.Time, 4);
        Assert.True(c.Playing);

        c.Loop = false;
        c.Advance(2);
        Assert.Equal(1.0f, c.Time);
        Assert.False(c.Playing);
        Assert.Equal(60, c.Tick);
        c.Play();                                 // at the end without loop: from the start again
        Assert.Equal(0f, c.Time);
        Assert.True(c.Playing);
    }

    [Fact]
    public void TheClocksSpeedScalesTime()
    {
        var c = new PlaybackClock();
        c.Reset(10, 600);
        c.Speed = 0.25f;
        c.Play();
        c.Advance(1);
        Assert.Equal(0.25f, c.Time, 5);
        c.Speed = 2;
        c.Advance(1);
        Assert.Equal(2.25f, c.Time, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Speed = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Speed = float.NaN);
        Assert.Equal([0.25f, 0.5f, 1f, 2f], PlaybackClock.Speeds.ToArray());
    }

    [Fact]
    public void StepsMoveByTicksWithinTheClip()
    {
        var c = new PlaybackClock();
        c.Reset(0.5f, 30);
        c.Play();
        c.Step(1);
        Assert.False(c.Playing);                  // stepping pauses
        Assert.Equal(1, c.Tick);
        Assert.Equal(1 / 60f, c.Time, 6);
        c.Step(-5);
        Assert.Equal(0, c.Tick);
        c.Seek(0.26f);                            // nearest tick 16 (15.6)
        c.Step(1);
        Assert.Equal(17, c.Tick);
        c.Step(100);
        Assert.Equal(30, c.Tick);
        Assert.Equal(0.5f, c.Time, 6);
        c.Seek(-3);
        Assert.Equal(0f, c.Time);
        c.Seek(99);
        Assert.Equal(0.5f, c.Time);
        c.Seek(float.NaN);
        Assert.Equal(0f, c.Time);
    }

    [Fact]
    public void AClipOfNoLengthNeverPlays()
    {
        var c = new PlaybackClock();
        c.Reset(0, 0);
        c.Play();
        Assert.False(c.Playing);
        Assert.False(c.Advance(1));
        c.Step(1);
        Assert.Equal(0, c.Tick);
        Assert.Equal(0f, c.Time);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1 / 60f, 1)]
    [InlineData(0.5f, 30)]
    [InlineData(0.509f, 31)]
    public void TicksAreSixtyASecond(float s, int tick)
    {
        Assert.Equal(tick, PlaybackClock.TickOf(s));
        Assert.Equal(tick / 60f, PlaybackClock.SecondsOf(tick), 6);
    }

    [Fact]
    public void FactsCountTrackKindsAndTheMover()
    {
        var b = Synthetic.Clip("facts", 7, 10 / 60f, [
            (0, Synthetic.PosCurve([0, 10], Vector3.Zero, new Vector3(0, 1, 0))),
            (0, Synthetic.RotCurve([0], Quaternion.Identity)),
            (Crc32.Of("Hips"), Synthetic.RotCurve([0], Quaternion.Identity)),
            (Crc32.Of("Spine"), Synthetic.RotCurve([0], Quaternion.Identity)),
            (Crc32.Of("Hips"), Synthetic.PosCurve([0], new Vector3(0, 0, 0.96f))),
            (0x12345678, Synthetic.Curve(0x36, [0], [new byte[4]])),     // a float track
        ]);
        var f = ClipFacts.Of(ClipRecord.Parse(b), null);
        Assert.Equal(new ClipFacts(3, 2, 1, true), f);

        var noMover = Synthetic.Clip("nomover", 8, 1, [(Crc32.Of("Hips"), Synthetic.RotCurve([0], Quaternion.Identity))]);
        Assert.Equal(new ClipFacts(1, 0, 0, false), ClipFacts.Of(ClipRecord.Parse(noMover), null));
    }

    [Fact]
    public void TheMoverPathFollowsTrackZero()
    {
        var b = Synthetic.Clip("walk", 9, 0.5f, [
            (0, Synthetic.PosCurve([0, 30], Vector3.Zero, new Vector3(0, 1.5f, 0))),
            (Crc32.Of("Hips"), Synthetic.PosCurve([0, 30], Vector3.Zero, new Vector3(5, 5, 5))),
        ]);
        var p = MoverPath.Of(ClipRecord.Parse(b), null);
        Assert.Equal(16, p.Length);                  // ticks 0, 2, ..., 30
        Assert.Equal(Vector3.Zero, p[0]);
        Assert.Equal(1.5f, p[^1].Y, 5);
        Assert.Equal(0.8f, p[8].Y, 4);                // tick 16 of 30
        Assert.Equal(31, MoverPath.Of(ClipRecord.Parse(b), null, step: 1).Length);
        Assert.Equal(3, MoverPath.Of(ClipRecord.Parse(b), null, step: 29).Length);   // 0, 29, 30

        var none = Synthetic.Clip("still", 10, 0.5f, [(0, Synthetic.RotCurve([0], Quaternion.Identity))]);
        Assert.Empty(MoverPath.Of(ClipRecord.Parse(none), null));
    }

    [Fact]
    public void TheCommandLineParses()
    {
        // the forge's folder isn't where the picture goes: a picture beside the forge read is refused (see GuardTests)
        var a = ViewerArgs.Parse(["--game", @"Games\GRB", "--forge", @"Forges\f.forge", "--clip", "mca_emote_Hello", "--time", "1.25", "--mover", "OFF", "--screenshot", "x.png"]);
        Assert.Equal(@"Games\GRB", a.Game);
        Assert.Equal(@"Forges\f.forge", a.Forge);
        Assert.Equal("mca_emote_Hello", a.Clip);
        Assert.Equal(1.25f, a.Time);
        Assert.False(a.Mover);
        Assert.Equal("x.png", a.Screenshot);
        Assert.True(a.Automated);
        Assert.False(ViewerArgs.Parse([]).Automated);
        Assert.True(ViewerArgs.Parse(["--game", "g", "--mover", "on"]).Mover);
        Assert.Equal(new[] { new ShotSpec("x.png", null, null, null) }, a.Shots);
    }

    [Fact]
    public void ManyShotsComeFromOneRun()
    {
        var a = ViewerArgs.Parse(["--game", "g", "--clip", "base", "--screenshot", "first.png",
            "--shot", @"shots\a b.png|mca_emote_Hello|1.5|off", "--shot", "b.png||2", "--shot", "c.png|0x1234||on"]);
        Assert.True(a.TakesShots);
        Assert.True(a.Automated);
        Assert.Equal(
            new[] { new ShotSpec("first.png", null, null, null), new ShotSpec(@"shots\a b.png", "mca_emote_Hello", 1.5f, false),
                    new ShotSpec("b.png", null, 2f, null), new ShotSpec("c.png", "0x1234", null, true) },
            a.Shots);
        Assert.False(ViewerArgs.Parse(["--game", "g"]).TakesShots);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--game")]
    [InlineData("--game", "g", "--time", "-1")]
    [InlineData("--game", "g", "--time", "soon")]
    [InlineData("--game", "g", "--mover", "maybe")]
    [InlineData("--screenshot", "x.png")]
    [InlineData("--clip", "mca_emote_Hello")]
    [InlineData("--shot", "a.png")]
    [InlineData("--game", "g", "--shot", "|clip")]
    [InlineData("--game", "g", "--shot", "a.png|c|soon")]
    [InlineData("--game", "g", "--shot", "a.png|c|1|sideways")]
    [InlineData("--game", "g", "--shot", "a.png|c|1|on|extra")]
    public void ABadCommandLineIsRefused(params string[] args) => Assert.Throws<ArgumentException>(() => ViewerArgs.Parse(args));

    [Fact]
    public void SettingsRoundTrip()
    {
        var s = new ViewerSettings { GameFolder = Path.Combine("Games", "Ghost Recon Breakpoint") };
        Assert.Equal(s, ViewerSettings.FromJson(s.ToJson()));
        Assert.Null(ViewerSettings.FromJson("").GameFolder);
        Assert.Null(ViewerSettings.FromJson("{ not json").GameFolder);
        Assert.Null(ViewerSettings.FromJson("null").GameFolder);
        Assert.Null(ViewerSettings.Load(Path.Combine(AppContext.BaseDirectory, "no-such-folder", "settings.json")).GameFolder);
    }

    [Fact]
    public void SettingsSaveAndLoadOnDisk()
    {
        // a folder of the test's own beside the test assembly, deleted afterwards; Save makes the folders under it
        string dir = Path.Combine(AppContext.BaseDirectory, "settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "GrbAnimTool", "settings.json");
            var s = new ViewerSettings { GameFolder = Path.Combine("Games", "Ghost Recon Breakpoint") };
            s.Save(path);
            Assert.Equal(s, ViewerSettings.Load(path));
            (s with { GameFolder = "other" }).Save(path);                 // a second save replaces the first
            Assert.Equal("other", ViewerSettings.Load(path).GameFolder);
            File.WriteAllText(path, "{ damaged");
            Assert.Null(ViewerSettings.Load(path).GameFolder);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AFractionalDurationEndsOnItsOwnTime()
    {
        // 0.51 s is 30.6 ticks: the last tick is 31, whose time (0.5167 s) is past the end, so the play head stops at 0.51 s
        var c = new PlaybackClock();
        c.Reset(0.51f, 31);
        Assert.Equal(31, c.LastTick);
        c.Loop = false;
        c.Play();
        c.Advance(5);
        Assert.Equal(0.51f, c.Time);
        Assert.Equal(31, c.Tick);
        c.Step(-1);
        Assert.Equal(30, c.Tick);
        Assert.Equal(0.5f, c.Time, 6);
        c.Step(1);
        Assert.Equal(0.51f, c.Time);
        Assert.Equal(31, c.Tick);
        c.Loop = true;
        c.Play();
        c.Advance(0.02);                                                // wraps past 0.51 s
        Assert.Equal(0.02f, c.Time, 4);
        c.Reset(0.51f, 99);                                             // a last tick past the duration is cut to it
        Assert.Equal(31, c.LastTick);
    }

    [Theory]
    [InlineData(1e6f)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NaN)]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void AHugeOrBadDurationIsNotShown(float seconds)
    {
        var b = Synthetic.Clip("huge", 11, seconds, [(0, Synthetic.PosCurve([0, 30], Vector3.Zero, new Vector3(0, 1, 0)))]);
        var e = new ClipEntry { Index = 1, Id = 11, Name = "huge", Bytes = b, Record = ClipRecord.Parse(b) };
        Assert.Equal(0, e.LastTick);
        Assert.Contains("duration", ClipFacts.WhyNotShown(e));
        // the path and the clock do no work for it, however long it claims to be
        var p = Within.Seconds(5, () => MoverPath.Of(e.Record!, null));
        Assert.Empty(p);
        var c = new PlaybackClock();
        c.Reset(seconds, int.MaxValue);
        Assert.Equal(0f, c.Duration);
        Assert.Equal(0, c.LastTick);
        c.Play();
        Assert.False(c.Playing);
    }

    [Fact]
    public void TheLongestClipsPathIsCapped()
    {
        var b = Synthetic.Clip("long", 12, ClipRecord.MaxDuration, [(0, Synthetic.PosCurve([0], new Vector3(1, 2, 0)))]);
        var r = ClipRecord.Parse(b);
        Assert.Equal(36_000, r.LastTick);
        var p = Within.Seconds(5, () => MoverPath.Of(r, null));
        Assert.InRange(p.Length, MoverPath.MaxPoints / 2, MoverPath.MaxPoints + 1);
        Assert.Equal(new Vector3(1, 2, 0), p[^1]);
        Assert.True(MoverPath.Of(r, null, step: 1).Length <= MoverPath.MaxPoints + 1);     // the cap holds whatever step is asked for
    }

    [Fact]
    public void WhyAClipCantBeShown()
    {
        Assert.Null(ClipFacts.WhyNotShown(Clips[0]));
        var unread = new ClipEntry { Index = 1, Id = 1, Name = "x", Bytes = new byte[8], ParseError = "clip name length -1" };
        Assert.Contains("clip name length -1", ClipFacts.WhyNotShown(unread));
        // a record whose curves stop short of its end
        var b = Synthetic.Clip("cut", 13, 1, [(Crc32.Of("Hips"), Synthetic.RotCurve([0], Quaternion.Identity))]);
        var cut = ClipRecord.Parse([.. b, 0, 0, 0, 0]);
        Assert.False(cut.WalkedToEnd);
        Assert.Contains("curves", ClipFacts.WhyNotShown(new ClipEntry { Index = 2, Id = 13, Name = "cut", Bytes = cut.Bytes, Record = cut }));
        // shorter than a tick
        var tiny = ClipRecord.Parse(Synthetic.Clip("tiny", 14, 0.005f, [(Crc32.Of("Hips"), Synthetic.RotCurve([0], Quaternion.Identity))]));
        Assert.True(tiny.WalkedToEnd);
        Assert.Contains("one tick", ClipFacts.WhyNotShown(new ClipEntry { Index = 3, Id = 14, Name = "tiny", Bytes = tiny.Bytes, Record = tiny }));
    }
}
