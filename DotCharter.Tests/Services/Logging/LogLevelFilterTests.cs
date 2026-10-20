using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Logging;

/// <summary>
///     Severity and category are independent questions.
///     <para>
///         Until 0.40 the only filter was the category bit, and the noise sat at
///         the SAME category as the signal: silencing ~470 minimap-seek lines meant
///         turning off <c>Input</c>, which also silenced every click, selection
///         change and marquee — the most useful record of how charting actually
///         goes.
///     </para>
/// </summary>
public sealed class LogLevelFilterTests : IDisposable
{
    // Every fact logs to a FILE here, never the default path: a default
    // AppConfig resolves to the user's real %APPDATA%\DotCharter\logs, so each
    // fact used to write a one-line log there and rotate a real session's log
    // out of retention. File logging stays ON because AppLogger raises
    // LogEmitted only when some sink is enabled.
    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "dotcharter-tests", "loglevel-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_logDir);

    private AppLogger Logger(AppConfig config, List<string> sink)
    {
        config.EnableConsoleLogging = false;
        config.EnableFileLogging = true;
        config.LogFilePath = Path.Combine(_logDir, "dotcharter.log");
        var logger = new AppLogger(new FakeConfigurationService { AppConfig = config });
        logger.LogEmitted += e => sink.Add($"{e.Level}|{e.Category}|{e.Message}");
        return logger;
    }

    [Fact]
    public void TheDefaultFloorIsInfo() => Assert.Equal(AppLogLevel.Info, new AppConfig().MinimumLogLevel);

    /// <summary>
    ///     The whole point: a click (Info) survives while the scrub that buried it
    ///     (Trace) does not — same category, opposite outcomes.
    /// </summary>
    [Fact]
    public void TheClickSurvivesAndTheScrubDoesNot()
    {
        var sink = new List<string>();
        using var logger = Logger(new AppConfig(), sink);

        logger.Info(AppLogCategory.Input, "Left-click highway at string=4");
        logger.Trace(AppLogCategory.Input, "Minimap seek. PointerX=164");

        Assert.Single(sink);
        Assert.Contains("Left-click highway", sink[0]);
    }

    [Fact]
    public void TheAudioHeartbeatIsBelowTheFloor()
    {
        var sink = new List<string>();
        using var logger = Logger(new AppConfig(), sink);

        logger.Debug(AppLogCategory.Audio, "Audio health: 0 dropout(s)");
        logger.Warning(AppLogCategory.Audio, "Audio dropout: the callback stalled ~201.1 ms");

        Assert.Single(sink);
        Assert.Contains("dropout: the callback stalled", sink[0]);
    }

    /// <summary>An override can LOWER a category's floor to debug one area.</summary>
    [Fact]
    public void AnOverrideCanOpenOneCategoryBackUp()
    {
        var config = new AppConfig();
        config.LogLevelOverrides["Audio"] = AppLogLevel.Trace;
        var sink = new List<string>();
        using var logger = Logger(config, sink);

        logger.Trace(AppLogCategory.Audio, "detail");
        logger.Trace(AppLogCategory.Input, "noise");

        Assert.Single(sink);
        Assert.Contains("detail", sink[0]);
    }

    /// <summary>...and RAISE one that is in the way. Both directions, deliberately.</summary>
    [Fact]
    public void AnOverrideCanSilenceOneCategoryEntirely()
    {
        var config = new AppConfig();
        config.LogLevelOverrides["Input"] = AppLogLevel.Warning;
        var sink = new List<string>();
        using var logger = Logger(config, sink);

        logger.Info(AppLogCategory.Input, "click");
        logger.Info(AppLogCategory.Editing, "edit");

        Assert.Single(sink);
        Assert.Contains("edit", sink[0]);
    }

    /// <summary>A disabled category stays off however low the floor goes.</summary>
    [Fact]
    public void TheCategoryGateStillWins()
    {
        var config = new AppConfig
        {
            MinimumLogLevel = AppLogLevel.Trace,
            EnabledLogCategories = AppLogCategory.All & ~AppLogCategory.Input
        };
        var sink = new List<string>();
        using var logger = Logger(config, sink);

        logger.Info(AppLogCategory.Input, "an ordinary line");

        Assert.Empty(sink);
    }

    /// <summary>
    ///     An ERROR escapes a disabled category, because the logger ORs
    ///     <see cref="AppLogCategory.Errors" /> into every error — losing a fault
    ///     because its subsystem was muted is not a trade worth making. That
    ///     multi-flag category matches no override KEY, so it falls through to the
    ///     global floor, which is exactly the documented behaviour.
    /// </summary>
    [Fact]
    public void AnErrorEscapesADisabledCategoryUnderTheErrorsFlag()
    {
        var config = new AppConfig
        {
            EnabledLogCategories = AppLogCategory.All & ~AppLogCategory.Input
        };
        var sink = new List<string>();
        using var logger = Logger(config, sink);

        logger.Error(AppLogCategory.Input, "a real fault");

        Assert.Contains("a real fault", Assert.Single(sink));
    }

    /// <summary>
    ///     ...and a multi-flag category is governed by the GLOBAL floor rather
    ///     than by an arbitrary pick between two overrides.
    /// </summary>
    [Fact]
    public void AMultiFlagCategoryFallsThroughToTheGlobalFloor()
    {
        var config = new AppConfig { MinimumLogLevel = AppLogLevel.Info };
        config.LogLevelOverrides["Project"] = AppLogLevel.Trace;
        var sink = new List<string>();
        using var logger = Logger(config, sink);

        logger.Trace(AppLogCategory.Project | AppLogCategory.Performance, "timing detail");

        Assert.Empty(sink);
    }

    [Fact]
    public void DroppingTheFloorRestoresEverything()
    {
        var config = new AppConfig { MinimumLogLevel = AppLogLevel.Trace };
        var sink = new List<string>();
        using var logger = Logger(config, sink);

        logger.Trace(AppLogCategory.Input, "seek");
        logger.Info(AppLogCategory.Input, "click");

        Assert.Equal(2, sink.Count);
    }
}

/// <summary>
///     What a chord-shape edit CHANGED — the single most valuable line missing
///     from the charting log. One session logged 63 template actions and not one
///     recorded what was edited.
/// </summary>
public sealed class ChordTemplateDiffTests
{
    private static ChordTemplate Shape(string name, (int s, int f)[] frets, (int s, int f)[]? fingers = null)
    {
        var t = new ChordTemplate { Id = 0, Name = name };
        foreach (var (s, f) in frets) t.Frets[s] = f;
        foreach (var (s, f) in fingers ?? []) t.Fingers[s] = f;
        return t;
    }

    [Fact]
    public void AFingeringChangeIsNamedPerString()
    {
        var before = Shape("A5", [(1, 7)], [(1, 2)]);
        var after = Shape("A5", [(1, 7)], [(1, 1)]);

        Assert.Equal("fingers s1:2→1", ChordTemplateDiff.Describe(before, after));
    }

    [Fact]
    public void ARenameIsNamed()
    {
        var before = Shape("A5", [(1, 7)]);
        var after = Shape("A#5", [(1, 7)]);

        Assert.Equal("name 'A5'→'A#5'", ChordTemplateDiff.Describe(before, after));
    }

    [Fact]
    public void AReshapeIsNamed()
    {
        var before = Shape("A5", [(1, 5), (2, 7)]);
        var after = Shape("A5", [(1, 6), (2, 8)]);

        Assert.Equal("frets s1:5→6 s2:7→8", ChordTemplateDiff.Describe(before, after));
    }

    /// <summary>
    ///     "Lost a string" and "moved a fret" are different edits — a diff that
    ///     spelled them the same would hide the one that changes what is played.
    /// </summary>
    [Fact]
    public void AddingAndRemovingAStringAreDistinguishable()
    {
        var added = ChordTemplateDiff.Describe(Shape("x", [(1, 5)]), Shape("x", [(1, 5), (2, 7)]));
        var removed = ChordTemplateDiff.Describe(Shape("x", [(1, 5), (2, 7)]), Shape("x", [(1, 5)]));

        Assert.Equal("frets s2:-→7", added);
        Assert.Equal("frets s2:7→-", removed);
    }

    [Fact]
    public void SeveralFacetsAreAllReported()
    {
        var before = Shape("A5", [(1, 5)], [(1, 2)]);
        var after = Shape("B5", [(1, 7)], [(1, 1)]);

        var described = ChordTemplateDiff.Describe(before, after);
        Assert.Contains("name", described);
        Assert.Contains("frets", described);
        Assert.Contains("fingers", described);
    }

    /// <summary>An unchanged facet is omitted, so every token is a real change.</summary>
    [Fact]
    public void AnIdenticalShapeReportsNothing()
    {
        var shape = Shape("A5", [(1, 5)], [(1, 1)]);
        Assert.Equal("nothing", ChordTemplateDiff.Describe(shape, shape));
    }

    [Fact]
    public void TheArpeggioFlagIsReported()
    {
        var before = Shape("A5", [(1, 5)]);
        var after = Shape("A5", [(1, 5)]);
        after.Arpeggio = true;

        Assert.Contains("arpeggio", ChordTemplateDiff.Describe(before, after));
    }
}

/// <summary>
///     The exit summary. The per-event lines are the evidence; this says whether
///     the evidence is worth reading.
/// </summary>
public sealed class ChartingSessionLogTests
{
    [Fact]
    public void AFreshSessionSummarizesCleanly()
    {
        var summary = new ChartingSessionLog().Summarize();
        Assert.Contains("edits=0", summary);
        Assert.Contains("undo=0", summary);
    }

    [Fact]
    public void CountsAreReported()
    {
        var log = new ChartingSessionLog();
        for (var i = 0; i < 5; i++) log.CountEdit();
        log.CountUndo();
        log.CountSave();

        var summary = log.Summarize();
        Assert.Contains("edits=5", summary);
        Assert.Contains("undo=1", summary);
        Assert.Contains("saves=1", summary);
    }

    /// <summary>
    ///     The ratio is the number that would have shown the 111-node hand-shape
    ///     resize at a glance: one undo step holding many ticks.
    /// </summary>
    [Fact]
    public void TicksPerUndoStepExposesADragHeavySession()
    {
        var log = new ChartingSessionLog();
        log.CountEdit();
        for (var i = 0; i < 110; i++) log.CountGestureTick();

        Assert.Contains("111.0 ticks per undo step", log.Summarize());
    }

    /// <summary>
    ///     Undo is a SHARE of edits because the absolute count means nothing
    ///     without it — 40 of 400 is exploration, 40 of 60 is the app fighting back.
    /// </summary>
    [Fact]
    public void UndoIsReportedAsAShareOfEdits()
    {
        var log = new ChartingSessionLog();
        for (var i = 0; i < 10; i++) log.CountEdit();
        for (var i = 0; i < 4; i++) log.CountUndo();

        Assert.Contains("undo=4 (40% of edits)", log.Summarize());
    }

    /// <summary>A summary is pasted into chats; it must carry nothing to redact.</summary>
    [Fact]
    public void TheSummaryIsCountsOnly()
    {
        var log = new ChartingSessionLog();
        log.CountEdit();
        var summary = log.Summarize();

        Assert.DoesNotContain("\\", summary);
        Assert.DoesNotContain("/", summary);
        Assert.DoesNotContain(".rscp", summary);
    }
}
