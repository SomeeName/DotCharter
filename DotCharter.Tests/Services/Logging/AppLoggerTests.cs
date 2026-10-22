using System.Globalization;

namespace DotCharter.Tests.Services.Logging;

/// <summary>
///     <see cref="AppLogger.Flush" /> used to rebuild the Serilog pipeline OUTSIDE
///     <c>_sync</c> — a non-atomic swap-and-dispose of <c>_serilogLogger</c> racing
///     every other thread sitting inside <c>lock (_sync) -> WriteEventLocked</c>.
///     Both rebuild sites are now under the lock, and <see cref="AppLogger.ReloadSettings" />
///     rebuilds ONCE against the NEW config (it used to Flush() first, rebuilding a
///     second time with the stale one).
/// </summary>
/// <remarks>
///     These tests deliberately configure NO sinks. A file/console sink makes every
///     rebuild spin up and tear down Serilog async-sink worker threads plus file
///     handles, and that background churn perturbs unrelated allocation-sensitive
///     tests in the shared suite. The pipeline swap under test is the same either way.
/// </remarks>
// Sets CultureInfo.CurrentCulture for the calling flow only and restores it in a
// finally, so no collection is needed. Infrastructure/CultureCollectionLintTests
// serializes the classes that write the process-wide defaults.
public sealed class AppLoggerTests
{
    private static FakeConfigurationService SinklessConfig(string logPath) => new()
    {
        AppConfig = new AppConfig
        {
            EnableFileLogging = false,
            EnableConsoleLogging = false,
            LogFilePath = logPath,
            EnabledLogCategories = AppLogCategory.Application
        }
    };

    /// <summary>
    ///     Dropping the "redundant" rebuild is only safe if the surviving one runs
    ///     AFTER the new config is loaded — delete the wrong one and the logger keeps
    ///     writing to the previous log file forever.
    /// </summary>
    [Fact]
    public void ReloadSettings_AppliesTheNewConfiguration()
    {
        var first = Path.Combine(Path.GetTempPath(), "dotcharter_first.log");
        var config = SinklessConfig(first);

        using var logger = new AppLogger(config);
        Assert.Equal(first, logger.CurrentLogPath);

        var second = Path.Combine(Path.GetTempPath(), "dotcharter_second.log");
        config.AppConfig.LogFilePath = second;

        logger.ReloadSettings();

        Assert.Equal(second, logger.CurrentLogPath);
    }

    [Fact]
    public void FlushAndReload_AreSafeToCallRepeatedly_AndDoNotDisturbLogging()
    {
        var config = SinklessConfig(Path.Combine(Path.GetTempPath(), "dotcharter_flush.log"));

        using var logger = new AppLogger(config);

        // Each of these swaps and disposes the Serilog root logger; interleaving them
        // with writes is exactly what SaveAppConfig() does on every project save.
        logger.Info(AppLogCategory.Application, "one");
        logger.Flush();
        logger.Flush();
        logger.Info(AppLogCategory.Application, "two");
        logger.ReloadSettings();
        logger.Info(AppLogCategory.Application, "three");
        logger.Flush();

        Assert.True(logger.IsEnabled(AppLogCategory.Application));
    }

    // ---- coalesced-repeat summaries -----------------------------------------
    //
    // A `[xN] … suppressed over …` line is written by the 1 s flush timer, so its
    // OWN timestamp is the flush time and can sit up to
    // LogRepeatSummaryIntervalMs (1000) + one 1 s tick after the last occurrence.
    // That is deliberate — a line may not be stamped earlier than the lines
    // already above it — but until 0.45 the line said nothing about WHEN the
    // occurrences happened, and a whole misdiagnosis came out of it: v0.44's
    // tone-restore AddNode failures all occurred at 12:40:48.44x, their three
    // summaries were flushed together at 12:40:50.124, and that second cluster
    // was read as a SECOND, separate premature tone apply ~1.7 s later on a
    // different code path. It was the same one. The window is in the text now.

    [Fact]
    public void ARepeatSummary_NamesTheWindowTheOccurrencesFellIn_NotJustTheirSpan()
    {
        var first = new DateTimeOffset(2026, 8, 8, 12, 40, 48, 443, TimeSpan.FromHours(2));

        var summary = AppLogger.FormatRepeatSummary(
            2, "DcRigApplier: AddNode('cab') failed; module skipped for this tone.",
            first, first.AddMilliseconds(2.8));

        // The anchor: without it the reader has only the flush timestamp, which
        // is not when anything happened.
        Assert.Contains("first at 12:40:48.443", summary, StringComparison.Ordinal);
        // …and the span still has to be there, or the anchor hides how far the
        // run stretched.
        Assert.Contains("suppressed over 2.8ms", summary, StringComparison.Ordinal);
        Assert.StartsWith("[x2] DcRigApplier: AddNode('cab') failed;", summary, StringComparison.Ordinal);
        Assert.Contains("1 duplicate log entry ", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatSummarysAnchor_IsTheFIRSTOccurrence_NotTheLast()
    {
        // The two are a whole second apart here on purpose: an implementation that
        // anchored on the LAST occurrence would still print a plausible-looking
        // time, and the assertion above alone would not catch it.
        var first = new DateTimeOffset(2026, 8, 8, 12, 40, 48, 443, TimeSpan.FromHours(2));
        var last = first.AddSeconds(1);

        var summary = AppLogger.FormatRepeatSummary(3, "engine watchdog fired", first, last);

        Assert.Contains("first at 12:40:48.443", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("12:40:49.443", summary, StringComparison.Ordinal);
        Assert.Contains("2 duplicate log entries ", summary, StringComparison.Ordinal);
        Assert.Contains("suppressed over 1s", summary, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A log file is read by whoever is DIAGNOSING, not by whoever produced it,
    ///     so neither the anchor nor the span may change shape with the player's
    ///     locale. The span was the live bug: <c>$"{ms:0.#}ms"</c> carried no
    ///     format provider, so the owner's German machine wrote <c>2,8ms</c> where
    ///     every doc, test and reader expects <c>2.8ms</c>. de-DE is the app's
    ///     other shipped culture and it separates decimals with a comma.
    /// </summary>
    [Fact]
    public void ARepeatSummary_IsCultureInvariant_AnchorAndSpanAlike()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var first = new DateTimeOffset(2026, 8, 8, 12, 40, 48, 443, TimeSpan.FromHours(2));
            var summary = AppLogger.FormatRepeatSummary(2, "m", first, first.AddMilliseconds(2.8));

            Assert.Contains("first at 12:40:48.443", summary, StringComparison.Ordinal);
            Assert.Contains("suppressed over 2.8ms", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("2,8ms", summary, StringComparison.Ordinal);
        }
        finally
        {
            // Restored on the FAILURE path too: a leaked culture would colour
            // whatever the same flow runs next.
            CultureInfo.CurrentCulture = previous;
        }
    }

    // -------------------------------------------- pipeline-rebuild deferral

    /// <summary>
    ///     <c>ReloadSettings</c> is called after EVERY app-config write, not just
    ///     a logging one — and <c>ProjectSessionService.UpdateAppConfig</c> makes
    ///     three of those on a single File ▸ Open (<c>LastRscpDir</c>,
    ///     <c>LastProjectPath</c>, the MRU list), on the UI thread. Each rebuild
    ///     built a fresh async file sink and then BLOCKED disposing the retired
    ///     one until its background pump drained (see the note in
    ///     <c>BuildSerilogPipelineLocked</c>), so opening a project paid three
    ///     logger teardown/rebuild cycles for three strings that say nothing
    ///     about logging.
    ///     <para>
    ///         Structural, not wall-clock: the guard counts PIPELINES BUILT.
    ///     </para>
    /// </summary>
    [Fact]
    public void ReloadSettings_AfterANonLoggingConfigChange_DoesNotRebuildThePipeline()
    {
        var config = SinklessConfig(Path.Combine(Path.GetTempPath(), "dotcharter_nonlogging.log"));
        using var logger = new AppLogger(config);
        var afterConstruction = logger.PipelineBuildCount;

        // Exactly what a project open writes: three unrelated strings.
        config.AppConfig.LastRscpDir = @"C:\songs";
        logger.ReloadSettings();
        config.AppConfig.LastProjectPath = @"C:\songs\a.rscp";
        logger.ReloadSettings();
        config.AppConfig.RecentProjects.Add(new RecentProjectEntry { Path = @"C:\songs\a.rscp" });
        logger.ReloadSettings();

        Assert.Equal(afterConstruction, logger.PipelineBuildCount);
    }

    /// <summary>The other half — without it the guard above is satisfied by a
    /// logger that ignores its settings. Each of the seven signature members
    /// must still force exactly one rebuild.</summary>
    [Theory]
    [InlineData(nameof(AppConfig.EnableConsoleLogging))]
    [InlineData(nameof(AppConfig.EnableConsoleColors))]
    [InlineData(nameof(AppConfig.EnableFileLogging))]
    [InlineData(nameof(AppConfig.RotateLogFiles))]
    [InlineData(nameof(AppConfig.LogFileMaxBytes))]
    [InlineData(nameof(AppConfig.LogFileMaxRollovers))]
    [InlineData(nameof(AppConfig.LogFilePath))]
    public void ReloadSettings_AfterALoggingChange_RebuildsThePipelineOnce(string member)
    {
        var config = SinklessConfig(Path.Combine(Path.GetTempPath(), "dotcharter_signature.log"));
        using var logger = new AppLogger(config);
        var before = logger.PipelineBuildCount;

        var property = typeof(AppConfig).GetProperty(member);
        Assert.NotNull(property);
        // Console/file logging stay OFF for the two sink-owning flags — see the
        // class remarks; flipping them ON here would start real sink threads.
        // Every other member moves to a value that is not its current one.
        object changed = property!.PropertyType == typeof(bool)
            ? !(bool)property.GetValue(config.AppConfig)!
            : property.PropertyType == typeof(int)
                ? (int)property.GetValue(config.AppConfig)! + 1
                : Path.Combine(Path.GetTempPath(), "dotcharter_signature_moved.log");
        property.SetValue(config.AppConfig, changed);

        logger.ReloadSettings();

        Assert.Equal(before + 1, logger.PipelineBuildCount);
    }

    /// <summary>
    ///     <c>AppLogger.LogPipelineSignature</c> is a hand-maintained parallel
    ///     list of what the pipeline builder reads, and this repo has watched
    ///     that shape rot before. A member the builder reads and the signature
    ///     omits is a settings change that stops taking effect until restart —
    ///     so read the builder's own source and compare.
    /// </summary>
    [Fact]
    public void ThePipelineSignatureNamesEveryConfigMemberTheBuilderReads()
    {
        var source = File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Tone", "Logging", "AppLogger.cs"));

        var builder = MethodBody(source, "private IDisposable? BuildSerilogPipelineLocked(RigConfig config)");
        Assert.NotNull(builder);
        var signature = MethodBody(source,
            "internal static LogPipelineSignature SignatureOf(RigConfig config, string resolvedLogPath) =>");
        Assert.NotNull(signature);

        var read = System.Text.RegularExpressions.Regex
            .Matches(builder!, @"\bconfig\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Non-vacuity: the builder really does read a handful of them.
        Assert.True(read.Count >= 5,
            $"Expected the pipeline builder to read several config members; found {read.Count}.");

        var missing = read
            .Where(name => !signature!.Contains("config." + name, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "BuildSerilogPipelineLocked reads AppConfig members that LogPipelineSignature does not " +
            $"carry, so ReloadSettings would skip a rebuild that matters: {string.Join(", ", missing)}.");
    }

    /// <summary>The brace-balanced body (or expression body) of the member whose
    /// declaration line starts with <paramref name="declaration" />.</summary>
    private static string? MethodBody(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        if (start < 0) return null;
        var cursor = start + declaration.Length;

        if (declaration.EndsWith("=>", StringComparison.Ordinal))
        {
            var end = source.IndexOf(';', cursor);
            return end < 0 ? null : source[cursor..end];
        }

        var open = source.IndexOf('{', cursor);
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..i];
        }

        return null;
    }
}
