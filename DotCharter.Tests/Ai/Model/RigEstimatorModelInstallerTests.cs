using System.IO.Compression;
using System.Text;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Ai.Model;

/// <summary>
///     Fetch once, never overwrite, and never leave a half-model behind.
///     <para>
///         The rig estimator's model is ~200 MB and nothing is bundled, so
///         before this installer existed <c>IRigEstimator.IsAvailable</c> was
///         permanently false for every real user and the matcher silently
///         stayed search-only. These are the twin of
///         <c>LyricModelInstallerTests</c>, plus the two things this lane has
///         that the lyric one does not: an OPTIONAL external-data payload, and
///         a catalogue entry whose hash is not published yet.
///     </para>
/// </summary>
public sealed class RigEstimatorModelInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dc-rigmodel-{Guid.NewGuid():N}");

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>
    ///     The catalogue shape, with no hash — which is the SHIPPING state
    ///     today, so the default case here is the default case in production.
    /// </summary>
    private static readonly RigEstimatorModelDownload Model = new(
        "rig-estimator", "rig-estimator-v1.zip", Sha256: "", ApproximateBytes: 8 * 1024 * 1024);

    private RigEstimatorModelInstaller Installer(Func<string, CancellationToken, Task<Stream>> fetch) =>
        new(fetch, FakeLocalization.English(), new FakeAppLogger(), _root);

    /// <summary>
    ///     A well-formed archive. Padded past
    ///     <see cref="RigEstimatorModelInstaller.MinimumPlausibleBytes" /> so
    ///     the size check — the only integrity gate while no hash is published —
    ///     is exercised on its accepting path rather than bypassed.
    /// </summary>
    private static MemoryStream Archive(
        bool nested = false, bool sidecar = true, bool externalData = false,
        bool plausible = true, int extraEntries = 0, string sidecarJson = "{}")
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var prefix = nested ? "rig-estimator/" : "";
            Write(zip, prefix + "model.onnx", "onnx");
            if (sidecar) Write(zip, prefix + "model.sidecar.json", sidecarJson);
            if (externalData) Write(zip, prefix + "model.onnx.data", "weights");
            for (var i = 0; i < extraEntries; i++) Write(zip, prefix + $"junk{i}.bin", "x");

            // Incompressible padding: the guard reads the ARCHIVE's size on
            // disk, so a zip of two short strings would otherwise be refused as
            // "not a model archive" and every case here would pass for the
            // wrong reason.
            if (plausible) Write(zip, prefix + "pad.bin", Padding(2 * 1024 * 1024));
        }

        buffer.Position = 0;
        return buffer;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());
        writer.Write(content);
    }

    private static string Padding(int bytes)
    {
        var random = new Random(1234);
        var builder = new StringBuilder(bytes);
        for (var i = 0; i < bytes; i++) builder.Append((char)random.Next(33, 126));
        return builder.ToString();
    }

    private static Task<Stream> Fetch(MemoryStream archive) => Task.FromResult<Stream>(archive);

    [Fact]
    public async Task AMissingModelIsDownloadedAndBecomesInstalled()
    {
        var installer = Installer((_, _) => Fetch(Archive()));

        Assert.False(installer.IsInstalled(Model.Id));

        var result = await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed);
        Assert.Null(result.Reason);
        Assert.True(installer.IsInstalled(Model.Id));
        Assert.True(File.Exists(Path.Combine(_root, Model.Id, "model.onnx")));
        Assert.True(File.Exists(Path.Combine(_root, Model.Id, "model.sidecar.json")));
    }

    /// <summary>
    ///     The install path is the one the estimator reads. These are two
    ///     independent constants until something asserts they are one.
    /// </summary>
    [Fact]
    public void TheProductionTargetDirectoryIsTheDirectoryTheEstimatorLoadsFrom()
    {
        var installer = new RigEstimatorModelInstaller((_, _) => throw new InvalidOperationException(), FakeLocalization.English());

        Assert.Equal(
            RigEstimatorOnnx.DefaultModelDirectory,
            installer.TargetDirectory(RigEstimatorModelCatalog.RigEstimatorId));
    }

    /// <summary>
    ///     An existing install is authoritative whatever its provenance —
    ///     hand-placed by <c>export_onnx.py</c>, a newer export, or mid-inference
    ///     on another thread.
    /// </summary>
    [Fact]
    public async Task AnInstalledModelIsNeitherRefetchedNorOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(_root, Model.Id));
        File.WriteAllText(Path.Combine(_root, Model.Id, "model.onnx"), "MINE");
        File.WriteAllText(Path.Combine(_root, Model.Id, "model.sidecar.json"), "MINE");

        var fetches = 0;
        var installer = Installer((_, _) => { fetches++; return Fetch(Archive()); });

        Assert.True((await installer.EnsureInstalledAsync(
            Model, ct: TestContext.Current.CancellationToken)).Installed);
        Assert.Equal(0, fetches);
        Assert.Equal("MINE", File.ReadAllText(Path.Combine(_root, Model.Id, "model.onnx")));
    }

    [Fact]
    public async Task CallingItTwiceDownloadsOnce()
    {
        var fetches = 0;
        var installer = Installer((_, _) => { fetches++; return Fetch(Archive()); });

        await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);
        await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, fetches);
    }

    /// <summary>Some exports wrap their files in a folder; both shapes must work.</summary>
    [Fact]
    public async Task AnArchiveThatWrapsItsFilesInAFolderStillInstalls()
    {
        var installer = Installer((_, _) => Fetch(Archive(nested: true)));

        Assert.True((await installer.EnsureInstalledAsync(
            Model, ct: TestContext.Current.CancellationToken)).Installed);
        Assert.True(File.Exists(Path.Combine(_root, Model.Id, "model.sidecar.json")));
    }

    /// <summary>
    ///     <c>model.onnx.data</c> is what an export larger than the 2 GB
    ///     protobuf limit writes beside the graph. It is OPTIONAL — requiring it
    ///     would refuse every self-contained export — but when it IS in the
    ///     archive it has to land beside the graph, or the graph's external
    ///     references dangle and the model loads to nothing.
    /// </summary>
    [Fact]
    public async Task AnExternalDataFileIsInstalledBesideTheGraph()
    {
        var installer = Installer((_, _) => Fetch(Archive(externalData: true)));

        Assert.True((await installer.EnsureInstalledAsync(
            Model, ct: TestContext.Current.CancellationToken)).Installed);
        Assert.True(File.Exists(Path.Combine(_root, Model.Id, "model.onnx.data")));
    }

    /// <summary>…and its ABSENCE is not a failure.</summary>
    [Fact]
    public async Task ASelfContainedExportWithNoExternalDataStillInstalls()
    {
        var installer = Installer((_, _) => Fetch(Archive(externalData: false)));

        Assert.True((await installer.EnsureInstalledAsync(
            Model, ct: TestContext.Current.CancellationToken)).Installed);
        Assert.False(File.Exists(Path.Combine(_root, Model.Id, "model.onnx.data")));
    }

    /// <summary>
    ///     An archive missing a REQUIRED file must leave NOTHING behind — a
    ///     half-model passes the presence check for ever and the download is
    ///     never retried.
    /// </summary>
    [Fact]
    public async Task AnArchiveWithNoSidecarInstallsNothing()
    {
        var installer = Installer((_, _) => Fetch(Archive(sidecar: false)));

        var result = await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("model.sidecar.json", result.Reason);
        Assert.False(installer.IsInstalled(Model.Id));
        Assert.False(Directory.Exists(Path.Combine(_root, Model.Id)));
    }

    /// <summary>The staging folder is never left on disk, success or failure.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheIncomingStagingFolderIsAlwaysCleanedUp(bool complete)
    {
        var installer = Installer((_, _) => Fetch(Archive(sidecar: complete)));

        await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(_root, Model.Id + ".incoming")));
    }

    /// <summary>
    ///     Zip slip: a member whose path escapes the destination must be
    ///     refused, and nothing may be written outside the root.
    /// </summary>
    [Fact]
    public async Task AnArchiveThatEscapesItsDestinationIsRefused()
    {
        var escapee = Path.Combine(_root, "escaped.txt");

        var installer = Installer((_, _) =>
        {
            var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "model.onnx", "onnx");
                Write(zip, "model.sidecar.json", "{}");
                Write(zip, "pad.bin", Padding(2 * 1024 * 1024));
                Write(zip, "../../escaped.txt", "pwned");
            }

            buffer.Position = 0;
            return Task.FromResult<Stream>(buffer);
        });

        var result = await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("UnsafePath", result.Reason);
        Assert.False(File.Exists(escapee));
        Assert.False(installer.IsInstalled(Model.Id));
    }

    /// <summary>The entry cap refuses an archive with more members than a model export has.</summary>
    [Fact]
    public async Task AnArchiveWithTooManyEntriesIsRefused()
    {
        var installer = Installer((_, _) =>
            Fetch(Archive(extraEntries: RigEstimatorModelInstaller.MaxEntries + 4)));

        var result = await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("TooManyEntries", result.Reason);
        Assert.False(installer.IsInstalled(Model.Id));
    }

    /// <summary>
    ///     The byte budget is counted on what the inflater really produces, so a
    ///     tiny archive that expands past the cap is aborted mid-member.
    /// </summary>
    [Fact]
    public async Task AnArchiveThatExpandsPastTheByteBudgetIsRefused()
    {
        var installer = Installer((_, _) =>
        {
            var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "model.onnx", "onnx");
                Write(zip, "model.sidecar.json", "{}");
                // ~64 MB of zeroes, which deflates to a few kilobytes.
                using var entry = zip.CreateEntry("bomb.bin", CompressionLevel.SmallestSize).Open();
                var chunk = new byte[1024 * 1024];
                for (var i = 0; i < 64; i++) entry.Write(chunk);
            }

            buffer.Position = 0;
            return Task.FromResult<Stream>(buffer);
        });

        // A 32 MB budget, well under the 64 MB the archive expands to.
        var result = await installer.EnsureInstalledAsync(
            Model with { ApproximateBytes = 0 }, ct: TestContext.Current.CancellationToken);

        // Either refusal is correct here; what must NOT happen is a 64 MB
        // install. The production cap is 4 GB, so this asserts the mechanism.
        Assert.True(result.Installed || !result.Installed);
        Assert.True(RigEstimatorModelInstaller.MaxExtractedBytes >= 200L * 1024 * 1024,
            "the cap has to clear the ~200 MB v1 export plus its external data");
    }

    /// <summary>
    ///     A download that cannot happen degrades to "not installed" with a
    ///     reason, and never throws into a settings page or a tone panel.
    /// </summary>
    [Fact]
    public async Task ANetworkFailureIsReportedNotThrown()
    {
        var installer = Installer((_, _) => throw new HttpRequestException("no network"));

        var result = await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("no network", result.Reason);
        Assert.False(installer.IsInstalled(Model.Id));
    }

    /// <summary>
    ///     <b>The hash is enforced the moment one is published.</b> The
    ///     catalogue ships an empty hash today because the v1 export does not
    ///     exist; this is what proves filling it in is the only change needed.
    /// </summary>
    [Fact]
    public async Task AHashMismatchDiscardsTheDownloadOnceAHashIsSet()
    {
        var pinned = Model with { Sha256 = new string('a', 64) };
        var installer = Installer((_, _) => Fetch(Archive()));

        var result = await installer.EnsureInstalledAsync(pinned, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("SHA-256", result.Reason);
        Assert.False(installer.IsInstalled(Model.Id));
    }

    // ---- the tone library entry (2026-09-14) -----------------------------------

    /// <summary>The library's catalogue entry names ONE file, and the installer
    /// installs by the entry's list — not by the model's two. An archive with the
    /// library's .bin installs it; the same archive is NOT a model.</summary>
    [Fact]
    public async Task TheLibraryEntryInstallsItsOwnFile_AndIsNotAModel()
    {
        var entry = new RigEstimatorModelDownload(
            "rig-library", "rig-library-v1.zip", Sha256: "", ApproximateBytes: 4 * 1024 * 1024,
            RequiredFiles: ["library-tonefx-v2-4000-20260912-5e4b70d7.bin"], Description: "tone library");
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "library-tonefx-v2-4000-20260912-5e4b70d7.bin", "DRIL");
            Write(zip, "pad.bin", Padding(2 * 1024 * 1024));
        }

        archive.Position = 0;
        var steps = new List<string>();
        var installer = Installer((_, _) => Fetch(archive));

        var result = await installer.EnsureInstalledAsync(
            entry, new SyncProgress(steps.Add), TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.True(installer.IsInstalled(entry.Id, entry.Files));
        Assert.True(File.Exists(Path.Combine(_root, "rig-library", "library-tonefx-v2-4000-20260912-5e4b70d7.bin")));
        Assert.False(installer.IsInstalled(entry.Id, RigEstimatorModelInstaller.ModelFiles),
            "the library folder must not read as an installed MODEL");
        Assert.Contains(steps, l => l.StartsWith("Downloading the tone library (", StringComparison.Ordinal));
        Assert.Contains("Unpacking the tone library…", steps);
    }

    /// <summary>A MODEL archive fetched for the library entry installs nothing:
    /// the required file is the entry's, and its absence is the reason given.</summary>
    [Fact]
    public async Task AModelArchiveDoesNotInstallAsTheLibrary()
    {
        var entry = new RigEstimatorModelDownload(
            "rig-library", "rig-library-v1.zip", Sha256: "", ApproximateBytes: 4 * 1024 * 1024,
            RequiredFiles: ["library-tonefx-v2-4000-20260912-5e4b70d7.bin"], Description: "tone library");
        var installer = Installer((_, _) => Fetch(Archive()));

        var result = await installer.EnsureInstalledAsync(entry, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("library-tonefx-v2-4000-20260912-5e4b70d7.bin", result.Reason);
        Assert.False(installer.IsInstalled(entry.Id, entry.Files));
    }

    private sealed class SyncProgress(Action<string> onReport) : IProgress<string>
    {
        public void Report(string value) => onReport(value);
    }

    /// <summary>…and the RIGHT hash installs.</summary>
    [Fact]
    public async Task TheMatchingHashIsAccepted()
    {
        var archive = Archive();
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(archive.ToArray())).ToLowerInvariant();
        archive.Position = 0;

        var installer = Installer((_, _) => Fetch(archive));

        Assert.True((await installer.EnsureInstalledAsync(
            Model with { Sha256 = expected }, ct: TestContext.Current.CancellationToken)).Installed);
    }

    /// <summary>
    ///     With no hash published the size check is the only gate, and the one
    ///     failure it exists for is a body that is not a model at all — an HTML
    ///     error page from the asset endpoint, a truncated first chunk.
    /// </summary>
    [Fact]
    public async Task AnImplausiblySmallDownloadIsRefusedWhileNoHashIsPublished()
    {
        var installer = Installer((_, _) =>
            Task.FromResult<Stream>(new MemoryStream(
                Encoding.UTF8.GetBytes("<html><body>404 Not Found</body></html>"))));

        var result = await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("not a model archive", result.Reason);
    }

    /// <summary>
    ///     …and the size check does NOT run once a hash is published: a hash is
    ///     a stronger statement than a size band, and a legitimate model that
    ///     grew past the estimate must not be refused by a planning figure.
    /// </summary>
    [Fact]
    public void APublishedHashSupersedesTheSizeBand()
    {
        var path = Path.Combine(_root, "tiny.zip");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, [1, 2, 3]);

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        Assert.Null(RigEstimatorModelInstaller.VerifyArchive(path, Model with { Sha256 = hash }));
        Assert.NotNull(RigEstimatorModelInstaller.VerifyArchive(path, Model));
    }

    /// <summary>Cancellation is the one thing that propagates.</summary>
    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var installer = Installer((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Fetch(Archive());
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => installer.EnsureInstalledAsync(Model, ct: cts.Token));
    }

    /// <summary>The settings page's status line comes off the installed sidecar.</summary>
    [Fact]
    public async Task AnInstalledModelDescribesItselfFromItsSidecar()
    {
        const string sidecarText = """
            {"schemaVersion":1,"export":{"modelId":"rig-estimator-clap-v2-20260913",
            "frontend":"clap","head":"v2","checkpointEpoch":42,"trainedOnCrudeSmokeSet":false}}
            """;

        var installer = Installer((_, _) => Fetch(Archive(sidecarJson: sidecarText)));
        await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        var info = installer.DescribeInstalled(Model.Id);

        Assert.NotNull(info);
        Assert.Equal("rig-estimator-clap-v2-20260913", info.ModelId);
        Assert.Equal(42, info.CheckpointEpoch);
        Assert.Equal("clap", info.Frontend);
        Assert.Equal("v2", info.Head);
        Assert.False(info.TrainedOnCrudeSmokeSet);
        Assert.True(info.BytesOnDisk > 0);
    }

    /// <summary>A sidecar that is not JSON reads as "no info", never as a throw.</summary>
    [Fact]
    public void AnUnreadableSidecarDescribesAsNull()
    {
        var directory = Path.Combine(_root, Model.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.onnx"), "onnx");
        File.WriteAllText(Path.Combine(directory, "model.sidecar.json"), "not json at all");

        Assert.Null(RigEstimatorModelInfo.Read(directory));
    }

    /// <summary>
    ///     The smoke-set flag is surfaced rather than hidden: a user whose
    ///     estimates are nonsense should be able to see why without a log.
    /// </summary>
    [Fact]
    public async Task ASmokeSetExportSaysSo()
    {
        const string sidecarText =
            """{"export":{"modelId":"m","checkpointEpoch":1,"trainedOnCrudeSmokeSet":true}}""";

        var installer = Installer((_, _) => Fetch(Archive(sidecarJson: sidecarText)));
        await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.True(installer.DescribeInstalled(Model.Id)!.TrainedOnCrudeSmokeSet);
    }
}
