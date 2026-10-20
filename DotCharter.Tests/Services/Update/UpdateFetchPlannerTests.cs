using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     Which bytes of a release asset an install actually needs. Pure — no archive,
///     no network — because every refusal here is a decision to download 414 MB
///     instead, and that decision has to be inspectable.
/// </summary>
public sealed class UpdateFetchPlannerTests
{
    private static PayloadFile File(string path, string hash, long size = 100) => new(path, hash, size);

    private static PayloadManifest M(string version, params PayloadFile[] files) =>
        new(version, InstallLayout.SelfContained, files);

    private static PayloadFile[] Host(string tag) =>
    [
        File(AppPayload.ExeName, $"exe-{tag}"),
        File(AppPayload.RuntimeConfigName, $"cfg-{tag}"),
        File(AppPayload.DepsName, $"deps-{tag}"),
        File(AppPayload.HostFxrName, "hostfxr"),
        File(AppPayload.CoreClrName, "coreclr"),
    ];

    /// <summary>Members laid out back to back at 200 bytes each, the shape a real zip has.</summary>
    private static List<ZipDirectoryEntry> Layout(IEnumerable<PayloadFile> files, string root = "")
    {
        var entries = new List<ZipDirectoryEntry>();
        long offset = 0;
        foreach (var file in files)
        {
            entries.Add(new ZipDirectoryEntry(root + file.Path, offset, 100, file.Size, ZipDirectoryEntry.Deflate));
            offset += 200;
        }

        return entries;
    }

    private static long DirectoryOffset(IReadOnlyList<ZipDirectoryEntry> entries)
        => entries.Max(e => e.LocalHeaderOffset) + 200;

    // ---- what gets fetched -------------------------------------------------

    [Fact]
    public void OnlyTheFilesThatDifferAreFetched()
    {
        var installed = M("0.42.0", [.. Host("a"), File("app.dll", "old"), File("runtime.dll", "same", 400_000)]);
        var release = M("0.43.0", [.. Host("a"), File("app.dll", "new"), File("runtime.dll", "same", 400_000)]);
        var entries = Layout(release.Files);

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries));

        Assert.True(plan.Succeeded, plan.Refusal + ": " + plan.Detail);
        Assert.Contains(plan.Plan!.Files, f => f.File.Path == "app.dll");
        Assert.DoesNotContain(plan.Plan.Files, f => f.File.Path == "runtime.dll");
    }

    /// <summary>
    ///     The host pair does not change in a managed-only release, so a purely
    ///     content-diffed set would omit it — and <c>AppPayload.Validate</c> refuses
    ///     a self-contained payload without it, on the user's machine, after the
    ///     download. The planner shares <see cref="PayloadDelta.Between" /> exactly
    ///     so this cannot be forgotten in a second place.
    /// </summary>
    [Fact]
    public void TheHostPairIsFetchedEvenWhenItDidNotChange()
    {
        var installed = M("0.42.0", [.. Host("a"), File("app.dll", "old")]);
        var release = M("0.43.0", [.. Host("a"), File("app.dll", "new")]);
        var entries = Layout(release.Files);

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries)).Plan!;
        var fetched = plan.Files.Select(f => f.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var required in PayloadManifest.AlwaysInclude)
            Assert.Contains(required, fetched);
    }

    /// <summary>An update robocopies with NO PURGE, so a file the release dropped
    /// survives forever unless the plan says to remove it.</summary>
    [Fact]
    public void AFileTheReleaseDroppedIsReportedForDeletion()
    {
        var installed = M("0.42.0", [.. Host("a"), File("gone.dll", "x")]);
        var release = M("0.43.0", Host("a"));
        var entries = Layout(release.Files);

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries)).Plan!;

        Assert.Equal(new[] { "gone.dll" }, plan.Deletions);
    }

    /// <summary>A release zip wraps its payload in one folder; the plan has to look
    /// through that or every single file reads as missing.</summary>
    [Fact]
    public void TheReleasesTopLevelFolderIsSeenThrough()
    {
        var installed = M("0.42.0", [.. Host("a"), File("app.dll", "old")]);
        var release = M("0.43.0", [.. Host("a"), File("app.dll", "new")]);
        var entries = Layout(release.Files, "DotCharter-v0.43.0-win-x64/");

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries));

        Assert.True(plan.Succeeded, plan.Refusal + ": " + plan.Detail);
        Assert.Contains(plan.Plan!.Files, f => f.File.Path == "app.dll");
    }

    // ---- refusals ----------------------------------------------------------

    [Fact]
    public void AManifestFileTheArchiveDoesNotHave_RefusesTheWholePlan()
    {
        var installed = M("0.42.0", Host("a"));
        var release = M("0.43.0", [.. Host("a"), File("app.dll", "new")]);
        var entries = Layout(release.Files.Where(f => f.Path != "app.dll"));

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries));

        Assert.Equal(FetchPlanRefusal.ArchiveMissingFile, plan.Refusal);
        Assert.Null(plan.Plan);
    }

    /// <summary>If the archive and the manifest disagree about a file's size, one of
    /// them is not what we think it is — and reconstructing from the wrong one is
    /// exactly the silent corruption this path must never have.</summary>
    [Fact]
    public void AnArchiveThatDisagreesWithTheManifest_IsRefused()
    {
        var installed = M("0.42.0", Host("a"));
        var release = M("0.43.0", [.. Host("a"), File("app.dll", "new", 100)]);
        var entries = Layout(release.Files)
            .Select(e => e.Name == "app.dll" ? e with { UncompressedSize = 999 } : e)
            .ToList();

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries));

        Assert.Equal(FetchPlanRefusal.ArchiveDisagreesWithManifest, plan.Refusal);
    }

    [Fact]
    public void AnUnsupportedCompressionMethod_IsRefusedNotGuessed()
    {
        var installed = M("0.42.0", Host("a"));
        var release = M("0.43.0", [.. Host("a"), File("app.dll", "new")]);
        var entries = Layout(release.Files)
            .Select(e => e.Name == "app.dll" ? e with { CompressionMethod = 93 } : e) // zstd
            .ToList();

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries));

        Assert.Equal(FetchPlanRefusal.UnsupportedCompression, plan.Refusal);
    }

    /// <summary>Overlapping members mean a member's extent would swallow the next
    /// one's header. Real writers never do it; a hostile one would.</summary>
    [Fact]
    public void OverlappingMembers_AreRefused()
    {
        var installed = M("0.42.0", Host("a"));
        var release = M("0.43.0", Host("a"));
        var entries = Layout(release.Files);
        entries[1] = entries[1] with { LocalHeaderOffset = entries[0].LocalHeaderOffset + 10 };

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries));

        Assert.Equal(FetchPlanRefusal.MalformedLayout, plan.Refusal);
    }

    [Fact]
    public void TwoMembersWithTheSameName_AreRefused()
    {
        var installed = M("0.42.0", Host("a"));
        var release = M("0.43.0", Host("a"));
        var entries = Layout(release.Files);
        entries.Add(entries[0] with { LocalHeaderOffset = DirectoryOffset(entries) });

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries) + 200);

        Assert.Equal(FetchPlanRefusal.MalformedLayout, plan.Refusal);
    }

    /// <summary>Past a budget the partial fetch is not worth its complexity — the
    /// runtime has moved and the full zip is the honest answer. Same rule the
    /// release script used to apply when deciding to publish a patch at all.</summary>
    [Fact]
    public void APlanBiggerThanItsBudget_FallsBackToTheFullZip()
    {
        var installed = M("0.42.0", Host("a"));
        var release = M("0.43.0", Host("b"));
        var entries = Layout(release.Files);

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries),
            new FetchPlanLimits(MaxTotalBytes: 10));

        Assert.Equal(FetchPlanRefusal.NotWorthIt, plan.Refusal);
    }

    // ---- coalescing --------------------------------------------------------

    [Fact]
    public void NearbyRangesBecomeOneRequest()
    {
        var merged = UpdateFetchPlanner.Coalesce(
            [new ByteRange(0, 100), new ByteRange(150, 250)], maxGapBytes: 64, maxRequestBytes: 1000);

        Assert.Equal(new[] { new ByteRange(0, 250) }, merged);
    }

    [Fact]
    public void ARangeBeyondTheGapStaysItsOwnRequest()
    {
        var merged = UpdateFetchPlanner.Coalesce(
            [new ByteRange(0, 100), new ByteRange(1000, 1100)], maxGapBytes: 64, maxRequestBytes: 10_000);

        Assert.Equal(new[] { new ByteRange(0, 100), new ByteRange(1000, 1100) }, merged);
    }

    /// <summary>One coalesced request is one buffer in memory, so the merge stops at
    /// the request ceiling however close the members are.</summary>
    [Fact]
    public void CoalescingStopsAtTheRequestCeiling()
    {
        var merged = UpdateFetchPlanner.Coalesce(
            [new ByteRange(0, 600), new ByteRange(600, 1200)], maxGapBytes: 64, maxRequestBytes: 1000);

        Assert.Equal(new[] { new ByteRange(0, 600), new ByteRange(600, 1200) }, merged);
    }

    [Fact]
    public void CoalescingIsOrderIndependentAndDropsEmptyRanges()
    {
        var merged = UpdateFetchPlanner.Coalesce(
            [new ByteRange(600, 700), new ByteRange(50, 50), new ByteRange(0, 100)],
            maxGapBytes: 0, maxRequestBytes: 10_000);

        Assert.Equal(new[] { new ByteRange(0, 100), new ByteRange(600, 700) }, merged);
    }

    /// <summary>The plan's requests must actually COVER every file it plans to
    /// write, or reconstruction silently drops members.</summary>
    [Fact]
    public void EveryPlannedFileLiesInsideSomePlannedRange()
    {
        var installed = M("0.42.0", [.. Host("a"), File("keep.dll", "same")]);
        var release = M("0.43.0",
            [.. Host("b"), File("keep.dll", "same"), File("one.dll", "1"), File("two.dll", "2")]);
        var entries = Layout(release.Files);

        var plan = UpdateFetchPlanner.Plan(installed, release, entries, DirectoryOffset(entries)).Plan!;

        foreach (var file in plan.Files)
            Assert.Contains(plan.Ranges, r => r.Contains(file.Extent.Start, file.Extent.Length));
    }
}
