using System.Buffers.Binary;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The partial-update download: only the bytes this install is missing, pulled
///     out of the one full release zip over HTTP ranges.
///     <para>
///         Half of these tests are about REFUSING. A partial download is an
///         optimisation over a path that already works, so every fault — a server
///         that will not serve ranges, an archive we cannot address, a hash that
///         does not match — has to end as "download the full zip", never as a
///         failed update and never as a payload that is applied anyway.
///     </para>
/// </summary>
public sealed class PartialUpdateDownloaderTests
{
    private const string RootFolder = "DotCharter-v0.43.0-win-x64";

    // ---- the happy path ----------------------------------------------------

    [Fact]
    public async Task OnlyTheChangedFilesAreFetched_AndTheyVerify()
    {
        var scenario = Scenario();
        using var staging = new TempDirectory();

        var result = await Fetch(scenario, staging.Path);

        Assert.Equal(PartialUpdateOutcome.Fetched, result.Outcome);

        // The one changed file plus the five AlwaysInclude ones, and nothing else.
        var staged = Directory.GetFiles(staging.Path, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(staging.Path, p).Replace('\\', '/'))
            .Order()
            .ToArray();
        Assert.Contains("app.dll", staged);
        Assert.DoesNotContain("runtime/big.dll", staged);
        foreach (var required in PayloadManifest.AlwaysInclude) Assert.Contains(required, staged);

        // Every staged file is byte-identical to what the release shipped.
        foreach (var name in staged)
            Assert.Equal(scenario.Files[name], File.ReadAllBytes(Path.Combine(staging.Path, name)));
    }

    /// <summary>The whole point: the wire cost is a fraction of the asset. The
    /// unchanged 4 MB member stands in for the .NET runtime — the bulk a full
    /// download re-sends to change one assembly.</summary>
    [Fact]
    public async Task TheDownloadIsAFractionOfTheAsset()
    {
        var scenario = Scenario();
        using var staging = new TempDirectory();

        var result = await Fetch(scenario, staging.Path);

        Assert.Equal(PartialUpdateOutcome.Fetched, result.Outcome);
        Assert.True(result.BytesFetched < scenario.Zip.Length / 2,
            $"fetched {result.BytesFetched} of {scenario.Zip.Length}");
    }

    /// <summary>The staged payload faces the same validator the apply path runs —
    /// it has to be applicable, not merely small.</summary>
    [Fact]
    public async Task TheStagedPayloadPassesTheApplyValidator()
    {
        var scenario = Scenario();
        using var staging = new TempDirectory();

        await Fetch(scenario, staging.Path);

        Assert.Null(AppPayload.ValidateDirectory(staging.Path, InstallLayout.SelfContained));
    }

    [Fact]
    public async Task AFileTheReleaseDroppedIsReportedForDeletion()
    {
        var scenario = Scenario(withDroppedFile: true);
        using var staging = new TempDirectory();

        var result = await Fetch(scenario, staging.Path);

        Assert.Equal(new[] { "gone.dll" }, result.Deletions);
    }

    /// <summary>
    ///     A HASHED inventory may never produce deletions. On a portable install the
    ///     config root IS the install directory, so an inventory that did not come
    ///     from a release-written manifest could hand the updater a
    ///     payload-deletions.txt naming the user's own settings and profiles.
    /// </summary>
    [Fact]
    public async Task AHashedInventoryNeverProducesDeletions()
    {
        var scenario = Scenario(withDroppedFile: true);
        using var staging = new TempDirectory();

        var hashed = new InstalledPayload(scenario.Installed.Manifest, InstalledPayloadSource.Hashed);
        var result = await new PartialUpdateDownloader(scenario.Fetcher).FetchAsync(
            "asset://zip", scenario.Zip.Length, scenario.Release, hashed, staging.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PartialUpdateOutcome.Fetched, result.Outcome);
        Assert.Empty(result.Deletions);
    }

    // ---- the fallbacks -----------------------------------------------------

    /// <summary>
    ///     The fallback that matters most. A server that ignores <c>Range</c>
    ///     answers 200 with the WHOLE 414 MB asset — a client that just reads the
    ///     body has done the thing it was avoiding and then mis-parses it as the
    ///     slice it asked for. Nothing may be staged.
    /// </summary>
    [Fact]
    public async Task AServerThatAnswers200InsteadOf206_FallsBackAndStagesNothing()
    {
        var scenario = Scenario();
        scenario.Fetcher.IgnoreRanges = true;
        using var staging = new TempDirectory();

        var result = await Fetch(scenario, staging.Path);

        Assert.Equal(PartialUpdateOutcome.NotRangeCapable, result.Outcome);
        Assert.Equal(0, result.FilesWritten);
        Assert.Empty(Directory.GetFiles(staging.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    ///     The other half of the safety property: the staged bytes are checked
    ///     against the release manifest BEFORE anything is handed on. A manifest
    ///     hash that does not match what came off the wire — a corrupted CDN
    ///     response, a mismatched asset — must not produce a payload.
    /// </summary>
    [Fact]
    public async Task AHashThatDoesNotMatchTheManifest_FallsBack()
    {
        var scenario = Scenario();
        var tampered = scenario.Release with
        {
            Files = scenario.Release.Files
                .Select(f => f.Path == "app.dll" ? f with { Sha256 = new string('0', 64) } : f)
                .ToList()
        };
        using var staging = new TempDirectory();

        var result = await new PartialUpdateDownloader(scenario.Fetcher).FetchAsync(
            "asset://zip", scenario.Zip.Length, tampered, scenario.Installed, staging.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PartialUpdateOutcome.VerificationFailed, result.Outcome);
        Assert.Contains("app.dll", result.Reason!);
    }

    /// <summary>Same check from the other side: a member whose BYTES were swapped
    /// for someone else's is caught even though every declared size still fits.</summary>
    [Fact]
    public async Task AMemberWhoseBytesWereSwappedOnTheWire_FallsBack()
    {
        var scenario = Scenario();
        var corrupted = (byte[])scenario.Zip.Clone();
        // Flip a byte deep inside the compressed data of the first member.
        corrupted[200] ^= 0xFF;
        using var staging = new TempDirectory();

        var result = await new PartialUpdateDownloader(new InMemoryRangeFetcher(corrupted)).FetchAsync(
            "asset://zip", corrupted.Length, scenario.Release, scenario.Installed, staging.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(PartialUpdateOutcome.Fetched, result.Outcome);
    }

    /// <summary>A zip64 archive stores sentinels where the 32-bit records go.
    /// Reading those as real numbers is how a parser silently addresses the wrong
    /// bytes, so it is refused outright.</summary>
    [Fact]
    public async Task AZip64Archive_FallsBackInsteadOfBeingMisread()
    {
        var scenario = Scenario();
        var zip64 = (byte[])scenario.Zip.Clone();
        var eocd = FindEocd(zip64);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(eocd + 10), ushort.MaxValue);
        using var staging = new TempDirectory();

        var result = await new PartialUpdateDownloader(new InMemoryRangeFetcher(zip64)).FetchAsync(
            "asset://zip", zip64.Length, scenario.Release, scenario.Installed, staging.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PartialUpdateOutcome.ArchiveNotUnderstood, result.Outcome);
        Assert.Contains("Zip64", result.Reason!);
    }

    [Fact]
    public async Task ADroppedConnection_FallsBackInsteadOfThrowing()
    {
        var scenario = Scenario();
        scenario.Fetcher.ThrowOnCall = 1;
        using var staging = new TempDirectory();

        var result = await Fetch(scenario, staging.Path);

        Assert.Equal(PartialUpdateOutcome.TransportFailed, result.Outcome);
    }

    /// <summary>A connection that dies AFTER the plan is made must fall back too —
    /// the partially staged tree is the caller's to delete, and it must never be
    /// mistaken for a complete payload.</summary>
    [Fact]
    public async Task ADroppedConnectionMidwayThroughTheFetch_FallsBack()
    {
        var scenario = Scenario();
        scenario.Fetcher.ThrowOnCall = 2;
        using var staging = new TempDirectory();

        var result = await Fetch(scenario, staging.Path);

        Assert.Equal(PartialUpdateOutcome.TransportFailed, result.Outcome);
    }

    /// <summary>An asset whose real length differs from what the release announced
    /// is not the asset the manifest describes.</summary>
    [Fact]
    public async Task AnAssetOfADifferentLengthThanAnnounced_FallsBack()
    {
        var scenario = Scenario();
        scenario.Fetcher.TotalLengthOverride = scenario.Zip.Length + 1;
        using var staging = new TempDirectory();

        var result = await Fetch(scenario, staging.Path);

        Assert.Equal(PartialUpdateOutcome.ArchiveNotUnderstood, result.Outcome);
    }

    [Fact]
    public async Task ANeedThatIsNotMeaningfullySmallerThanTheAsset_FallsBack()
    {
        var scenario = Scenario();
        using var staging = new TempDirectory();

        var result = await new PartialUpdateDownloader(scenario.Fetcher).FetchAsync(
            "asset://zip", scenario.Zip.Length, scenario.Release, scenario.Installed, staging.Path,
            new FetchPlanLimits(MaxTotalBytes: 16),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PartialUpdateOutcome.NotWorthIt, result.Outcome);
    }

    // ---- fixtures ----------------------------------------------------------

    /// <summary>
    ///     #220: a changed member bigger than the fetcher's per-request cap (32 MiB in
    ///     the product; v0.60.0 changed two 160 MB files) is fetched as several
    ///     requests and joined. Before, the fetcher refused the one big range and every
    ///     such update fell back to the full download.
    /// </summary>
    [Fact]
    public async Task AChangedFileBiggerThanOneRequest_IsFetchedInPieces()
    {
        var (zip, release, installed, files) = BigMemberScenario();
        var fetcher = new InMemoryRangeFetcher(zip) { MaxRangeBytes = PieceCap };
        using var staging = new TempDirectory();

        var result = await new PartialUpdateDownloader(fetcher, maxRequestBytes: PieceCap).FetchAsync(
            "asset://zip", zip.Length, release, installed, staging.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PartialUpdateOutcome.Fetched, result.Outcome);
        Assert.Equal(files["big.dll"], File.ReadAllBytes(Path.Combine(staging.Path, "big.dll")));
        Assert.All(fetcher.Requests, r => Assert.True(r.Length <= PieceCap, $"a {r.Length}-byte request went over the cap"));
        Assert.True(fetcher.Requests.Count(r => r.Length == PieceCap) >= 4,
            "the 300 KB member was not split into several capped requests");
    }

    /// <summary>A piece that comes back short is refused, never stitched.</summary>
    [Fact]
    public async Task AShortPieceOfASplitRange_FallsBack()
    {
        var (zip, release, installed, _) = BigMemberScenario();
        // Every piece past the cut comes back short.
        var truncated = zip.AsSpan(0, zip.Length / 2).ToArray();
        var fetcher = new InMemoryRangeFetcher(truncated) { MaxRangeBytes = PieceCap, TotalLengthOverride = zip.Length };
        using var staging = new TempDirectory();

        var result = await new PartialUpdateDownloader(fetcher, maxRequestBytes: PieceCap).FetchAsync(
            "asset://zip", zip.Length, release, installed, staging.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(PartialUpdateOutcome.Fetched, result.Outcome);
    }

    private const int PieceCap = 64 * 1024;

    /// <summary>A 300 KB changed member (incompressible, so its zip entry stays over
    /// <see cref="PieceCap" />) next to the usual unchanged 4 MB one.</summary>
    private static (byte[] Zip, PayloadManifest Release, InstalledPayload Installed, Dictionary<string, byte[]> Files)
        BigMemberScenario()
    {
        var files = FakeRelease.HostFiles("0.43.0");
        files["big.dll"] = FakeRelease.Content("big-new", 300_000);
        files["runtime/big.dll"] = FakeRelease.Content("runtime", 4_000_000);
        var installedFiles = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase)
        {
            ["big.dll"] = FakeRelease.Content("big-old", 300_000),
        };
        var installed = new InstalledPayload(
            FakeRelease.Manifest("0.42.0", installedFiles), InstalledPayloadSource.ShippedManifest);
        return (FakeRelease.Zip(files, RootFolder), FakeRelease.Manifest("0.43.0", files), installed, files);
    }

    private sealed record Fixture(
        byte[] Zip,
        PayloadManifest Release,
        InstalledPayload Installed,
        Dictionary<string, byte[]> Files,
        InMemoryRangeFetcher Fetcher);

    /// <summary>
    ///     A release whose payload is the five host files plus a small changed
    ///     assembly and one 4 MB member that did NOT change — the shape a real
    ///     update has, and the one that makes the saving visible.
    /// </summary>
    private static Fixture Scenario(bool withDroppedFile = false)
    {
        var files = FakeRelease.HostFiles("0.43.0");
        files["app.dll"] = FakeRelease.Content("app-new", 3000);
        files["runtime/big.dll"] = FakeRelease.Content("runtime", 4_000_000);

        var release = FakeRelease.Manifest("0.43.0", files);
        var zip = FakeRelease.Zip(files, RootFolder);

        var installedFiles = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase)
        {
            ["app.dll"] = FakeRelease.Content("app-old", 2500),
        };
        if (withDroppedFile) installedFiles["gone.dll"] = FakeRelease.Content("gone", 64);

        var installed = new InstalledPayload(
            FakeRelease.Manifest("0.42.0", installedFiles), InstalledPayloadSource.ShippedManifest);

        return new Fixture(zip, release, installed, files, new InMemoryRangeFetcher(zip));
    }

    private static Task<PartialUpdateResult> Fetch(Fixture fixture, string staging)
        => new PartialUpdateDownloader(fixture.Fetcher).FetchAsync(
            "asset://zip", fixture.Zip.Length, fixture.Release, fixture.Installed, staging,
            cancellationToken: TestContext.Current.CancellationToken);

    private static int FindEocd(byte[] zip)
    {
        for (var i = zip.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x06054b50)
                return i;
        }

        throw new InvalidOperationException("no end-of-central-directory record");
    }
}
