using System.IO.Compression;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     Fetch once, never overwrite. The models are hundreds of megabytes and do not
///     change between releases, so the presence check is the whole gate — and a
///     half-finished download must never be mistaken for an installed model.
/// </summary>
public sealed class LyricModelInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dc-lyricmodel-{Guid.NewGuid():N}");

    public void Dispose() => BestEffortDelete.Directory(_root);

    private static readonly LyricModelDownload Model =
        new("lyric-align-en", "lyric-align-en-v1.zip", Sha256: "", ApproximateBytes: 1024);

    /// <summary>A well-formed model archive: the two files the installer requires.</summary>
    private static Stream Archive(bool nested = false, bool complete = true)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var prefix = nested ? "lyric-align-en/" : "";
            using (var w = new StreamWriter(zip.CreateEntry(prefix + "model.onnx").Open())) w.Write("onnx");
            if (complete)
                using (var w = new StreamWriter(zip.CreateEntry(prefix + "vocab.json").Open())) w.Write("{}");
        }

        buffer.Position = 0;
        return buffer;
    }

    private LyricModelInstaller Installer(Func<string, CancellationToken, Task<Stream>> fetch) =>
        new(fetch, FakeLocalization.English(), new FakeAppLogger(), [_root]);

    [Fact]
    public async Task AMissingModelIsDownloadedAndBecomesInstalled()
    {
        var installer = Installer((_, _) => Task.FromResult(Archive()));

        Assert.False(installer.IsInstalled(Model.Id));
        Assert.True(await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken));
        Assert.True(installer.IsInstalled(Model.Id));
        Assert.True(File.Exists(Path.Combine(_root, Model.Id, "model.onnx")));
    }

    /// <summary>
    ///     The whole point: an existing model is authoritative whatever its
    ///     provenance — hand-installed, a newer export, or in use on another thread.
    /// </summary>
    [Fact]
    public async Task AnInstalledModelIsNeitherRefetchedNorOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(_root, Model.Id));
        File.WriteAllText(Path.Combine(_root, Model.Id, "model.onnx"), "MINE");
        File.WriteAllText(Path.Combine(_root, Model.Id, "vocab.json"), "MINE");

        var fetches = 0;
        var installer = Installer((_, _) => { fetches++; return Task.FromResult(Archive()); });

        Assert.True(await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken));
        Assert.Equal(0, fetches);
        Assert.Equal("MINE", File.ReadAllText(Path.Combine(_root, Model.Id, "model.onnx")));
    }

    [Fact]
    public async Task CallingItTwiceDownloadsOnce()
    {
        var fetches = 0;
        var installer = Installer((_, _) => { fetches++; return Task.FromResult(Archive()); });

        await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);
        await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, fetches);
    }

    /// <summary>Some exports wrap their files in a folder; both shapes must work.</summary>
    [Fact]
    public async Task AnArchiveThatWrapsItsFilesInAFolderStillInstalls()
    {
        var installer = Installer((_, _) => Task.FromResult(Archive(nested: true)));

        Assert.True(await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(_root, Model.Id, "vocab.json")));
    }

    /// <summary>
    ///     An incomplete archive must leave NOTHING behind — a half-model would pass
    ///     the presence check forever and the download would never be retried.
    /// </summary>
    [Fact]
    public async Task AnIncompleteArchiveInstallsNothing()
    {
        var installer = Installer((_, _) => Task.FromResult(Archive(complete: false)));

        Assert.False(await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken));
        Assert.False(installer.IsInstalled(Model.Id));
        Assert.False(Directory.Exists(Path.Combine(_root, Model.Id)));
    }

    /// <summary>
    ///     An optional model that cannot be fetched degrades to "not installed" — the
    ///     aligner then falls back to the multilingual model — and never throws into
    ///     the caller.
    /// </summary>
    [Fact]
    public async Task ANetworkFailureIsReportedNotThrown()
    {
        var installer = Installer((_, _) => throw new HttpRequestException("no network"));

        Assert.False(await installer.EnsureInstalledAsync(Model, ct: TestContext.Current.CancellationToken));
        Assert.False(installer.IsInstalled(Model.Id));
    }

    [Fact]
    public async Task AHashMismatchDiscardsTheDownload()
    {
        var wrongHash = Model with { Sha256 = new string('a', 64) };
        var installer = Installer((_, _) => Task.FromResult(Archive()));

        Assert.False(await installer.EnsureInstalledAsync(wrongHash, ct: TestContext.Current.CancellationToken));
        Assert.False(installer.IsInstalled(Model.Id));
    }

    /// <summary>Cancellation is the one thing that propagates.</summary>
    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var installer = Installer((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Archive()); });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => installer.EnsureInstalledAsync(Model, ct: cts.Token));
    }

    [Fact]
    public void TargetDirectoryIsUnderThePreferredRoot()
    {
        var installer = Installer((_, _) => Task.FromResult(Archive()));

        Assert.Equal(Path.Combine(_root, Model.Id), installer.TargetDirectory(Model.Id));
    }
}
