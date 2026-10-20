using DotCharter.Services.Diagnostics;

namespace DotCharter.Tests.Services.Diagnostics;

/// <summary>
///     The counts that answer "what do I actually use while charting". The claim
///     worth pinning is the one a bare counter cannot make: what was NEVER used.
/// </summary>
public sealed class UsageStatisticsTests
{
    private const long Day = 86_400_000L;
    private const long Now = 1_800_000_000_000L;

    [Fact]
    public void RepeatedUseAccumulatesAndKeepsTheFirstAndLastTimes()
    {
        var stats = new UsageStatistics();

        stats.Record(UsageKind.Command, "tools.refret", Now - 10 * Day);
        stats.Record(UsageKind.Command, "tools.refret", Now - 2 * Day);

        var entry = Assert.Single(stats.Entries);
        Assert.Equal(2, entry.Count);
        Assert.Equal(Now - 10 * Day, entry.FirstUsedUnixMs);
        Assert.Equal(Now - 2 * Day, entry.LastUsedUnixMs);
    }

    [Fact]
    public void KindsAreCountedSeparately()
    {
        var stats = new UsageStatistics();

        stats.Record(UsageKind.Command, "same", Now);
        stats.Record(UsageKind.Dialog, "same", Now);

        Assert.Equal(1, stats.CountOf(UsageKind.Command, "same"));
        Assert.Equal(1, stats.CountOf(UsageKind.Dialog, "same"));
    }

    [Fact]
    public void EntriesAreRankedByUse()
    {
        var stats = new UsageStatistics();
        stats.Record(UsageKind.Command, "rare", Now);
        for (var i = 0; i < 5; i++) stats.Record(UsageKind.Command, "common", Now);

        Assert.Equal("common", stats.Entries[0].Id);
    }

    [Fact]
    public void ABlankIdIsIgnoredRatherThanCountedAsAnUnnamedBucket()
    {
        var stats = new UsageStatistics();

        stats.Record(UsageKind.Command, "  ", Now);
        stats.Record("", "x", Now);

        Assert.Empty(stats.Entries);
    }

    /// <summary>
    ///     The whole point of the feature: "which of the 40 things I ship does
    ///     nobody ever touch". A report that only listed what WAS used could
    ///     never answer it.
    /// </summary>
    [Fact]
    public void TheReportNamesWhatWasNeverUsed()
    {
        var stats = new UsageStatistics();
        stats.Record(UsageKind.Command, "tools.refret", Now);

        var report = stats.BuildReport(
            new Dictionary<string, IReadOnlyList<string>>
            {
                [UsageKind.Command] = ["tools.refret", "tools.neverClicked", "tools.alsoNever"],
            },
            Now);

        Assert.Contains("NEVER USED (2)", report);
        Assert.Contains("tools.neverClicked", report);
        Assert.Contains("tools.alsoNever", report);
        Assert.Contains("3 available, 1 used, 1 uses", report);
    }

    /// <summary>
    ///     A renamed command must not silently read as "nobody used it" — that
    ///     would be evidence for deleting something people rely on.
    /// </summary>
    [Fact]
    public void TheReportSurfacesCountsForIdsNoLongerOffered()
    {
        var stats = new UsageStatistics();
        stats.Record(UsageKind.Command, "tools.oldName", Now);

        var report = stats.BuildReport(
            new Dictionary<string, IReadOnlyList<string>> { [UsageKind.Command] = ["tools.newName"] }, Now);

        Assert.Contains("counted outside the catalogue", report);
        Assert.Contains("tools.oldName", report);
    }

    /// <summary>
    ///     Every keybind label was a compiler lambda name until 2026-09-03
    ///     (<c>&lt;RegisterAll&gt;b__53</c>): 107 of 110 "never used" rows in the
    ///     owner's real report were those. They name nothing and renumber on
    ///     every edit of the registrar, so they are refused on record and
    ///     dropped from a stored file rather than reported as verbs.
    /// </summary>
    [Fact]
    public void ACompilerGeneratedIdIsNeitherCountedNorLoaded()
    {
        var stats = new UsageStatistics();
        stats.Record(UsageKind.Keybind, "<RegisterAll>b__53", Now);
        stats.Record(UsageKind.Keybind, "PlayPause", Now);
        Assert.Equal(["PlayPause"], stats.Entries.Select(e => e.Id));

        const string stored = """
            [
              { "Kind": "keybind", "Id": "<RegisterAll>b__1", "Count": 150, "FirstUsedUnixMs": 1, "LastUsedUnixMs": 2 },
              { "Kind": "keybind", "Id": "SeekLeft", "Count": 3, "FirstUsedUnixMs": 1, "LastUsedUnixMs": 2 }
            ]
            """;
        var loaded = UsageStatistics.FromJson(stored);
        Assert.Equal(["SeekLeft"], loaded.Entries.Select(e => e.Id));
    }

    /// <summary>
    ///     Dwell time separates "opened often, dismissed instantly" — a misclick,
    ///     or the wrong route to something — from "opened rarely, worked in for
    ///     minutes". A bare count reports those two identically.
    /// </summary>
    [Fact]
    public void DurationAccumulatesWithoutCountingAnExtraUse()
    {
        var stats = new UsageStatistics();
        stats.Record(UsageKind.Dialog, "Settings", Now);
        stats.RecordDuration(UsageKind.Dialog, "Settings", 4_000);
        stats.RecordDuration(UsageKind.Dialog, "Settings", 6_000);

        var entry = Assert.Single(stats.Entries);
        Assert.Equal(1, entry.Count);
        Assert.Equal(10_000, entry.TotalOpenMs);
    }

    /// <summary>A close with no matching open must not mint a phantom entry.</summary>
    [Fact]
    public void DurationForSomethingNeverOpenedIsIgnored()
    {
        var stats = new UsageStatistics();

        stats.RecordDuration(UsageKind.Dialog, "NeverOpened", 5_000);

        Assert.Empty(stats.Entries);
    }

    [Fact]
    public void TheReportShowsAverageDwellWhereItWasMeasured()
    {
        var stats = new UsageStatistics();
        for (var i = 0; i < 4; i++) stats.Record(UsageKind.Dialog, "Settings", Now);
        stats.RecordDuration(UsageKind.Dialog, "Settings", 20_000);

        var report = stats.BuildReport(
            new Dictionary<string, IReadOnlyList<string>> { [UsageKind.Dialog] = ["Settings"] }, Now);

        Assert.Contains("5s each", report);
    }

    [Fact]
    public void CountsRoundTripThroughJson()
    {
        var stats = new UsageStatistics();
        stats.Record(UsageKind.Dialog, "Settings", Now - Day);
        stats.Record(UsageKind.Dialog, "Settings", Now);

        var restored = UsageStatistics.FromJson(stats.ToJson());

        Assert.Equal(2, restored.CountOf(UsageKind.Dialog, "Settings"));
        Assert.Equal(Now - Day, restored.Entries[0].FirstUsedUnixMs);
    }

    /// <summary>A corrupt counter file must never cost the user their session.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[{\"Kind\":null,\"Id\":null,\"Count\":3}]")]
    public void AnUnreadableFileDegradesToNoHistory(string? json) => Assert.Empty(UsageStatistics.FromJson(json).Entries);

    /// <summary>
    ///     Bounded so an id source that turns out to be unbounded (a per-song or
    ///     per-timestamp key sneaking in) cannot grow the file without limit.
    /// </summary>
    [Fact]
    public void TheEntryCountIsBounded()
    {
        var stats = new UsageStatistics();

        for (var i = 0; i < UsageStatistics.MaxEntries + 50; i++)
            stats.Record(UsageKind.Command, $"id{i}", Now);

        Assert.Equal(UsageStatistics.MaxEntries, stats.Entries.Count);
    }
}
