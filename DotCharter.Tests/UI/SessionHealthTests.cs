using DotCharter.UI.Diagnostics;
using DotCharter.UI.ViewModels.Diagnostics;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rules behind Session Health's status pills, its footer finding and
///     its log table. Pure, so every threshold is checkable without a booted app
///     or an audio device — which is the whole reason they were lifted out of the
///     view model.
///     <para>
///         What these pin is that the dashboard cannot QUIETLY start lying: a
///         card that reads "healthy" while its own metric is over the line, a
///         footer that names a healthy area, a startup number printed without
///         the ReadyToRun caveat, or a log filter that swallows a row it does not
///         recognise.
///     </para>
/// </summary>
public sealed class SessionHealthTests
{
    // ── audio ───────────────────────────────────────────────────────────────

    [Fact]
    public void ALostDeviceIsTheOnlyFailingStateTheAppCanReportAboutItself()
        => Assert.Equal(HealthTone.Bad, SessionHealthRules.Audio(true, 48000, 6));

    /// <summary>A chart-only session legitimately has no device open, so that is
    /// a WATCH and never a failure — nothing is broken, there is just nothing to
    /// play against.</summary>
    [Fact]
    public void NoDeviceOpenIsAWatch_NotAFailure()
        => Assert.Equal(HealthTone.Watch, SessionHealthRules.Audio(false, 0, 0));

    [Fact]
    public void AHealthyDeviceReadsHealthy()
        => Assert.Equal(HealthTone.Good, SessionHealthRules.Audio(false, 48000, 7.2));

    /// <summary>Latency is the metric this app exists to make trustworthy, so it
    /// gets a stated line rather than being reported without comment.</summary>
    [Theory]
    [InlineData(39.9, HealthTone.Good)]
    [InlineData(40.0, HealthTone.Good)]
    [InlineData(40.1, HealthTone.Watch)]
    public void LatencyPastTheStatedLineIsAWatch(double latencyMs, HealthTone expected)
        => Assert.Equal(expected, SessionHealthRules.Audio(false, 48000, latencyMs));

    // ── rendering ───────────────────────────────────────────────────────────

    /// <summary>The 3D preview is closed by default. A session that never opened
    /// it has drawn zero frames, and that must not read as broken.</summary>
    [Fact]
    public void ARendererThatNeverDrewIsIdle_NotUnhealthy()
        => Assert.Equal(HealthTone.Good, SessionHealthRules.Rendering(0, 0, 0, 0));

    /// <summary>…and its EMPTY ring must not contribute a worst frame either —
    /// a zero max from a renderer with no samples is not a fast renderer.</summary>
    [Fact]
    public void AnIdleRendererDoesNotDilateTheWorstFrame()
    {
        Assert.Equal(HealthTone.Watch, SessionHealthRules.Rendering(512, 969, 0, 0));
        Assert.Equal(HealthTone.Good, SessionHealthRules.Rendering(512, 12, 0, 0));
    }

    [Fact]
    public void AVisibleHitchIsAWatch()
        => Assert.Equal(HealthTone.Watch, SessionHealthRules.Rendering(512, 4.5, 300, 969));

    // ── runtime ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(512L * 1024 * 1024, HealthTone.Good)]
    [InlineData(2L * 1024 * 1024 * 1024 - 1, HealthTone.Good)]
    [InlineData(2L * 1024 * 1024 * 1024, HealthTone.Watch)]
    public void MemoryPastTheStatedLineIsAWatch(long workingSet, HealthTone expected)
        => Assert.Equal(expected, SessionHealthRules.Runtime(workingSet));

    // ── startup: the r2r rule ───────────────────────────────────────────────

    /// <summary>
    ///     THE rule this card exists for, and it is not "the numbers are large".
    ///     A run that is not ReadyToRun JIT-compiles its whole startup path and
    ///     measures roughly twice the shipped build, so its phases are a JIT
    ///     measurement — the card must say so rather than invite someone to
    ///     optimise a number no user ever pays.
    /// </summary>
    [Theory]
    [InlineData(true, true, HealthTone.Good)]
    [InlineData(true, false, HealthTone.Watch)]
    [InlineData(false, false, HealthTone.Watch)]
    public void StartupIsJudgedOnWhetherItsNumbersCanBeRead(
        bool hasData, bool precompiled, HealthTone expected)
        => Assert.Equal(expected, SessionHealthRules.Startup(new StartupPhasesView(hasData, precompiled)));

    /// <summary>"?" is not "yes". An unanswerable question about the numbers
    /// carries the same warning as a negative answer.</summary>
    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData("?", false)]
    public void OnlyAConfirmedPrecompiledImageCounts(string probe, bool expected)
        => Assert.Equal(expected, new StartupPhases(0, 0, 0, 0, 0, 0, 0, 0, probe).IsPrecompiled);

    /// <summary>The bar's last segment is the time the six named phases do not
    /// account for. Dropping it would draw a faster start than happened; a
    /// negative one (phases summing past interactive) must clamp, not invert.</summary>
    [Fact]
    public void TheStartupBarNeverLosesOrInventsTime()
    {
        var p = new StartupPhases(100, 300, 380, 20, 900, 200, 2000, 4000, "no");
        var segments = p.Segments;

        Assert.Equal(7, segments.Count);
        Assert.All(segments, s => Assert.True(s >= 0));
        // 100 + 200 + 380 + 20 + 900 + 200 = 1800 named, so 2200 unaccounted.
        Assert.Equal(2200, segments[^1]);
        Assert.Equal(4000, segments.Sum());

        var overrun = new StartupPhases(100, 300, 380, 20, 900, 200, 500, 900, "yes");
        Assert.Equal(0, overrun.Segments[^1]);
    }

    // ── the footer's one thing ──────────────────────────────────────────────

    [Fact]
    public void TheFooterNamesTheWorstCard_NotTheFirstOne()
    {
        var finding = SessionHealthRules.Headline(
            HealthTone.Good, HealthTone.Watch, HealthTone.Good, HealthTone.Bad);

        Assert.Equal(HealthTone.Bad, finding.Tone);
        Assert.Equal("Diagnostics_Finding_Startup", finding.MessageKey);
    }

    [Fact]
    public void AtEqualToneTheMostActionableCardWins()
    {
        var finding = SessionHealthRules.Headline(
            HealthTone.Good, HealthTone.Watch, HealthTone.Watch, HealthTone.Watch);

        Assert.Equal("Diagnostics_Finding_Rendering", finding.MessageKey);
    }

    [Fact]
    public void AllHealthyIsItsOwnAnswer()
    {
        var finding = SessionHealthRules.Headline(
            HealthTone.Good, HealthTone.Good, HealthTone.Good, HealthTone.Good);

        Assert.Equal(HealthTone.Good, finding.Tone);
        Assert.Equal("Diagnostics_Finding_None", finding.MessageKey);
    }

    // ── the log table ───────────────────────────────────────────────────────

    private const string RealTail =
        "2026-08-02 19:16:52.001 +02:00 [INFO] [Audio] [T22] Stem cache: stored 6 stems as Opus\n" +
        "2026-08-02 19:16:53.113 +02:00 [WARN] [Audio] [T22] Stems ready for an unsaved session\n" +
        "2026-08-02 19:16:54.900 +02:00 [EROR] [Project] [T1] Could not read the genre\n" +
        "System.IO.IOException: the file is locked\n" +
        "   at DotCharter.Services.Parsing.Rscp.RscpGenreReader.ReadGenre(String path)\n" +
        "2026-08-02 19:16:55.113 +02:00 [VERB] [Audio] [T22] detail\n";

    /// <summary>Parsed straight out of the sink's own output template — the four
    /// columns the panel prints, and nothing invented.</summary>
    [Fact]
    public void ARowIsSplitIntoTheColumnsTheSinkActuallyWrites()
    {
        var rows = DiagnosticsLogParser.Parse(RealTail, 100);

        Assert.Equal(4, rows.Count);
        // Newest FIRST: the interesting line is always the last one written.
        Assert.Equal("19:16:55.113", rows[0].Time);
        Assert.Equal("VERB", rows[0].Level);
        Assert.Equal("Audio", rows[0].Category);
        Assert.Equal("detail", rows[0].Message);
        Assert.Equal(LogSeverity.Info, rows[0].Severity);
    }

    /// <summary>
    ///     A stack trace is many lines that do not match the sink's shape. They
    ///     fold into the entry ABOVE them: dropping them hides the one thing
    ///     anybody opens this panel for, and promoting them to rows would make
    ///     one exception look like forty events.
    /// </summary>
    [Fact]
    public void AStackTraceFoldsIntoTheEntryItBelongsTo()
    {
        var rows = DiagnosticsLogParser.Parse(RealTail, 100);
        var error = Assert.Single(rows, r => r.Severity == LogSeverity.Error);

        Assert.Contains("Could not read the genre", error.Message);
        Assert.Contains("System.IO.IOException", error.Message);
        Assert.Contains("RscpGenreReader.ReadGenre", error.Message);
    }

    [Theory]
    [InlineData("WARN", LogSeverity.Warning)]
    [InlineData("EROR", LogSeverity.Error)]
    [InlineData("FATL", LogSeverity.Error)]
    [InlineData("INFO", LogSeverity.Info)]
    [InlineData("DBUG", LogSeverity.Info)]
    // A level the sink grows later must still show up as an ordinary row rather
    // than vanishing from every filter.
    [InlineData("NOTC", LogSeverity.Info)]
    public void SeverityFoldsTheSinksOwnLevelTokens(string level, LogSeverity expected)
        => Assert.Equal(expected, DiagnosticsLogParser.SeverityOf(level));

    [Fact]
    public void TheSeverityFilterIsASubsetLadder()
    {
        var rows = DiagnosticsLogParser.Parse(RealTail, 100);

        Assert.Equal(4, rows.Count(r => r.MatchesFilter(LogSeverityFilter.All)));
        Assert.Equal(2, rows.Count(r => r.MatchesFilter(LogSeverityFilter.Warnings)));
        Assert.Equal(1, rows.Count(r => r.MatchesFilter(LogSeverityFilter.Errors)));
    }

    [Fact]
    public void TheRowCapKeepsTheNEWESTRows()
    {
        var rows = DiagnosticsLogParser.Parse(RealTail, 2);

        Assert.Equal(2, rows.Count);
        Assert.Equal("19:16:55.113", rows[0].Time);
    }

    [Fact]
    public void AnUnreadableTailIsEmpty_NotAnException()
    {
        Assert.Empty(DiagnosticsLogParser.Parse(null, 10));
        Assert.Empty(DiagnosticsLogParser.Parse("", 10));
        Assert.Empty(DiagnosticsLogParser.Parse("not a log line at all", 10));
        Assert.Empty(DiagnosticsLogParser.Parse(RealTail, 0));
    }
}
