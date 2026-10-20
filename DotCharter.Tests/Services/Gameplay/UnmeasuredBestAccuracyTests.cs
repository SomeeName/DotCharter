using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     A stored <c>BestAccuracy</c> of 0 means UNMEASURED, and no readout may
///     render it as a confident <b>F</b> / <b>0.0%</b>.
///     <para>
///         <c>PracticeProfileService.Record</c> deliberately declines to raise
///         the stored best for a run below
///         <see cref="PracticeProfileService.MinCoverageForBestAccuracy" /> — so
///         a player whose runs are all section drills or mid-song quits has
///         <c>Sessions.Count &gt; 0</c> and a best accuracy still at its 0
///         default, which <c>GradeCalculator.FromAccuracy</c> maps to F. The song
///         row, the local leaderboard and the (since removed) session-history
///         header each printed
///         that letter (and "0.0%") beside a real score, while the Get Ready
///         screen — gating on <c>stats is { BestAccuracy: &gt; 0 }</c> over the
///         SAME store — showed nothing. This is the
///         <c>judged == 0 ? 1.0</c> lie with the sign flipped, and it is now one
///         predicate: <see cref="AccuracyMath.IsMeasuredBest" />.
///     </para>
///     <para>Pure + file-backed store, no clock, no app session.</para>
/// </summary>
public sealed class UnmeasuredBestAccuracyTests : IDisposable
{
    private const string SongKey = "lib:SongA";
    private const string Arrangement = "Lead:Lead";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private PlayerProfileService NewPlayers()
        => new(FakeLocalization.English(), logger: null, filePath: Path.Combine(_dir, "player-profiles.json"));

    private PracticeProfileService NewStore(IPlayerProfileService players)
        => new(players, logger: null, legacyFilePath: Path.Combine(_dir, "legacy.json"));

    /// <summary>A real run of a section: judged sounds, a real score, and
    /// coverage far below the best-accuracy floor — the shape that leaves
    /// <c>BestAccuracy</c> at 0 forever.</summary>
    private static LivePlaySessionResult SectionDrill(long score = 4200)
        => new(score, 0.8, Hits: 20, Misses: 5, TotalSounds: 400, BestStreak: 9,
            DurationSeconds: 40, SpeedPercent: 100, SongProgress: 0.06);

    /// <summary>A full pass: coverage above the floor, so the store records a
    /// best accuracy the readouts may print.</summary>
    private static LivePlaySessionResult FullRun(double accuracy = 0.96, long score = 9000)
        => new(score, accuracy, Hits: 96, Misses: 4, TotalSounds: 100, BestStreak: 40,
            DurationSeconds: 200, SpeedPercent: 100, SongProgress: 1.0);

    // ── the predicate itself ────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.0001, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    public void IsMeasuredBest_IsWhetherAQualifyingRunEverSetIt(double best, bool expected)
        => Assert.Equal(expected, AccuracyMath.IsMeasuredBest(best));

    /// <summary>The premise the whole fix rests on: a drill run IS recorded and
    /// does NOT set a best accuracy. If this ever stops being true the readout
    /// assertions below stop testing anything.</summary>
    [Fact]
    public void ASectionDrill_IsRecordedButNeverSetsABestAccuracy()
    {
        var store = NewStore(NewPlayers());
        var profile = store.Record(SongKey, Arrangement, SectionDrill());

        Assert.Single(profile.Sessions);
        Assert.Equal(4200, profile.BestScore);
        Assert.Equal(0.0, profile.BestAccuracy);
        Assert.False(PracticeProfileService.QualifiesForBestAccuracy(SectionDrill()));
    }

    // ── readout 1: the song-select row ──────────────────────────────────────

    [Fact]
    public void SongStats_OfADrilledSong_HasNoMeasuredBest_AndPrintsNoGrade()
    {
        var store = NewStore(NewPlayers());
        store.Record(SongKey, Arrangement, SectionDrill());
        var stats = new GameplayStatsService(store);

        // Both entry points into the song row: the per-arrangement one the Get
        // Ready screen asks, and the best-across-arrangements one song select
        // stamps on every list row.
        AssertUnmeasured(stats.GetSongStats(SongKey, Arrangement));
        AssertUnmeasured(stats.GetBestForSong(SongKey));
        return;

        static void AssertUnmeasured(SongPlayStats? maybe)
        {
            Assert.NotNull(maybe);
            var row = maybe!;
            Assert.True(row.SessionCount > 0);
            Assert.False(row.HasBestAccuracy);
            Assert.Equal(AccuracyMath.UnmeasuredLabel, row.GradeLabel);
            Assert.NotEqual("F", row.GradeLabel);
        }
    }

    [Fact]
    public void SongStats_OfAFullRun_StillPrintsItsGrade()
    {
        var store = NewStore(NewPlayers());
        store.Record(SongKey, Arrangement, FullRun());
        var stats = new GameplayStatsService(store);

        AssertGraded(stats.GetSongStats(SongKey, Arrangement));
        AssertGraded(stats.GetBestForSong(SongKey));
        return;

        static void AssertGraded(SongPlayStats? maybe)
        {
            Assert.NotNull(maybe);
            var row = maybe!;
            Assert.True(row.HasBestAccuracy);
            Assert.Equal(LetterGrade.A, row.Grade);
            Assert.Equal("A", row.GradeLabel);
        }
    }

    /// <summary>
    ///     The row the player actually sees. A drilled song still reports
    ///     <c>HasBest</c> (there IS a score) — so the panel is on screen, and both
    ///     the grade cell and the accuracy cell must show the placeholder rather
    ///     than "F" and "0.0%".
    /// </summary>
    [Fact]
    public void SongRow_OfADrilledSong_ShowsNeitherAnFNorZeroPercent()
    {
        var store = NewStore(NewPlayers());
        store.Record(SongKey, Arrangement, SectionDrill());
        var row = PlaySongItem.FromProject(@"C:\songs\a.rscp", false, FakeLocalization.English());

        row.ApplyStats(new GameplayStatsService(store).GetBestForSong(SongKey));

        Assert.True(row.HasBest);
        Assert.Equal(4200, row.BestScore);
        Assert.Equal(AccuracyMath.UnmeasuredLabel, row.GradeLabel);
        Assert.Equal(AccuracyMath.UnmeasuredLabel, row.AccuracyLabel);
        Assert.DoesNotContain("0.0", row.AccuracyLabel);
    }

    [Fact]
    public void SongRow_OfAFullRun_ShowsTheGradeAndThePercentage()
    {
        var store = NewStore(NewPlayers());
        store.Record(SongKey, Arrangement, FullRun());
        var row = PlaySongItem.FromProject(@"C:\songs\a.rscp", false, FakeLocalization.English());

        row.ApplyStats(new GameplayStatsService(store).GetBestForSong(SongKey));

        Assert.True(row.HasBest);
        Assert.Equal("A", row.GradeLabel);
        Assert.Contains("96", row.AccuracyLabel);
        Assert.EndsWith("%", row.AccuracyLabel);
    }

    /// <summary>An unplayed song keeps the OLD contract: empty strings, not a
    /// placeholder — the row hides the whole panel behind <c>HasBest</c> and
    /// prints "never played" instead.</summary>
    [Fact]
    public void SongRow_OfAnUnplayedSong_StaysEmpty()
    {
        var row = PlaySongItem.FromProject(@"C:\songs\a.rscp", false, FakeLocalization.English());
        row.ApplyStats(null);

        Assert.False(row.HasBest);
        Assert.Equal("", row.GradeLabel);
        Assert.Equal("", row.AccuracyLabel);
    }

    // ── readout 2: the profile stats page ───────────────────────────────────

    /// <summary>
    ///     The grade histogram counted a drilled arrangement as an F bar, which
    ///     reports a player who practises sections as FAILING songs nobody ever
    ///     graded them on. Only a measured best earns a bar.
    /// </summary>
    [Fact]
    public void GradeDistribution_CountsOnlyMeasuredBests()
    {
        var store = NewStore(NewPlayers());
        store.Record(SongKey, Arrangement, SectionDrill());
        store.Record("lib:SongB", "Bass:Bass", FullRun(0.99));

        var stats = new GameplayStatsService(store).GetStatistics();

        Assert.Equal(2, stats.SongsPlayed);
        Assert.Equal(1, stats.GradeDistribution[LetterGrade.S]);
        Assert.False(stats.GradeDistribution.ContainsKey(LetterGrade.F));
    }

    [Fact]
    public void LifetimeBestAccuracy_IsUnmeasuredUntilAQualifyingRun()
    {
        var store = NewStore(NewPlayers());
        store.Record(SongKey, Arrangement, SectionDrill());

        var drilled = new GameplayStatsService(store).GetStatistics();
        Assert.True(drilled.TotalSessions > 0);
        Assert.False(drilled.HasBestAccuracy);

        store.Record(SongKey, Arrangement, FullRun());
        Assert.True(new GameplayStatsService(store).GetStatistics().HasBestAccuracy);
    }

    // ── readout 3: the local leaderboard ────────────────────────────────────

    [Fact]
    public void LeaderboardRow_OfADrilledProfile_RanksOnScoreButPrintsNoGrade()
    {
        var players = NewPlayers();
        var store = NewStore(players);
        store.Record(SongKey, Arrangement, SectionDrill());

        var entry = Assert.Single(new LeaderboardService(players).GetSongLeaderboard(SongKey, Arrangement, LiveDetectorMode.Unknown));

        Assert.Equal(4200, entry.BestScore);
        Assert.False(entry.HasBestAccuracy);
        Assert.Equal(AccuracyMath.UnmeasuredLabel, entry.GradeLabel);
        Assert.NotEqual("F", entry.GradeLabel);
    }

    [Fact]
    public void LeaderboardRow_OfAFullRun_PrintsItsGrade()
    {
        var players = NewPlayers();
        var store = NewStore(players);
        store.Record(SongKey, Arrangement, FullRun());

        var entry = Assert.Single(new LeaderboardService(players).GetSongLeaderboard(SongKey, Arrangement, LiveDetectorMode.Unknown));

        Assert.True(entry.HasBestAccuracy);
        Assert.Equal(LetterGrade.A, entry.Grade);
        Assert.Equal("A", entry.GradeLabel);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
