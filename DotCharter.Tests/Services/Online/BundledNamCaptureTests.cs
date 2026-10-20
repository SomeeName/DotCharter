using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
/// Tests for the bundled-capture install path: <see cref="BundledNamCaptureInstaller"/>
/// manifest parsing, <see cref="INamCaptureLibrary.ImportBundled"/> (copy + sidecar +
/// idempotency), and <see cref="BundledNamCapture.SyntheticId"/>.
/// </summary>
public sealed class BundledNamCaptureTests : IDisposable
{
    private readonly DirectoryInfo _libDir = Directory.CreateTempSubdirectory("dc-nam-lib-");
    private readonly DirectoryInfo _bundleDir = Directory.CreateTempSubdirectory("dc-nam-bundle-");

    public void Dispose()
    {
        foreach (var d in new[] { _libDir, _bundleDir })
            try { d.Delete(recursive: true); } catch { /* best effort */ }
    }

    private sealed class StubTone3000Service : ITone3000Service
    {
        public Task<OnlineResult<Tone3000SearchPage>> SearchAsync(Tone3000SearchQuery q, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<Tone3000SearchPage>.Success(new Tone3000SearchPage([], 1, 0)));
        public Task<OnlineResult<Tone3000Tone>> GetToneAsync(long id, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<Tone3000Tone>.Fail(OnlineError.InvalidRequest));
        public Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(long id, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<IReadOnlyList<Tone3000Model>>.Success([]));
        public Task<OnlineResult<long>> DownloadModelAsync(
            Tone3000Model m, Stream dest, long max, IProgress<double>? p = null, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<long>.Fail(OnlineError.InvalidRequest));
    }

    private NamCaptureLibrary NewLibrary() => new(new StubTone3000Service(), rootDirectoryOverride: _libDir.FullName);

    private static BundledNamCapture Meta(string fileName) => new(
        fileName, "Deluxe Reverb Clean", "Steven Atkinson", "amp", "nam", "lstm",
        "MIT", "https://example/license", "https://example/attribution");

    private string WriteBundleFile(string fileName, string body = "{\"fake\":\"nam\"}")
    {
        var path = Path.Combine(_bundleDir.FullName, fileName);
        File.WriteAllText(path, body);
        return path;
    }

    // ---------------------------------------------------------- ImportBundled

    [Fact]
    public void ImportBundled_CopiesFile_WritesManifest_WithLicenseAndAttribution()
    {
        var library = NewLibrary();
        var source = WriteBundleFile("fender-deluxe-reverb-clean.nam");
        var changed = 0;
        library.Changed += (_, _) => changed++;

        Assert.True(library.ImportBundled(source, Meta("fender-deluxe-reverb-clean.nam")));

        var entry = Assert.Single(library.GetAll());
        Assert.Equal("fender-deluxe-reverb-clean.nam", entry.Manifest.FileName);
        Assert.Equal("MIT", entry.Manifest.License);
        Assert.Equal("https://example/attribution", entry.Manifest.AttributionUrl);
        Assert.Equal("Steven Atkinson", entry.Manifest.Author);
        Assert.True(File.Exists(entry.FilePath));
        Assert.NotEqual(0, entry.Manifest.FileBytes);
        Assert.False(string.IsNullOrEmpty(entry.Manifest.Sha256));
        // Bundled captures carry a synthetic NEGATIVE id (never a server id).
        Assert.True(entry.Manifest.ModelId < 0);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ImportBundled_IsIdempotent_SecondCallDoesNotDuplicate()
    {
        var library = NewLibrary();
        var source = WriteBundleFile("cap.nam");

        Assert.True(library.ImportBundled(source, Meta("cap.nam")));
        Assert.True(library.ImportBundled(source, Meta("cap.nam"))); // no-op, still true

        Assert.Single(library.GetAll());
    }

    [Fact]
    public void ImportBundled_MissingSource_ReturnsFalse_NoResidue()
    {
        var library = NewLibrary();
        Assert.False(library.ImportBundled(
            Path.Combine(_bundleDir.FullName, "does-not-exist.nam"), Meta("does-not-exist.nam")));
        Assert.Empty(library.GetAll());
    }

    // ---------------------------------------------------------- installer

    [Fact]
    public void InstallFromDirectory_InstallsEveryManifestEntry_ThenIsIdempotent()
    {
        WriteBundleFile("a.nam");
        WriteBundleFile("b.nam");
        var manifest = """
            [
              { "fileName":"a.nam","title":"A","author":"x","gearType":"amp","format":"nam","sizeTier":"lite","license":"MIT","licenseUrl":"u","attributionUrl":"u" },
              { "fileName":"b.nam","title":"B","author":"x","gearType":"amp","format":"nam","sizeTier":"lite","license":"MIT","licenseUrl":"u","attributionUrl":"u" }
            ]
            """;
        File.WriteAllText(Path.Combine(_bundleDir.FullName, BundledNamCaptureInstaller.ManifestFileName), manifest);
        var library = NewLibrary();

        Assert.Equal(2, BundledNamCaptureInstaller.InstallFromDirectory(library, _bundleDir.FullName));
        Assert.Equal(0, BundledNamCaptureInstaller.InstallFromDirectory(library, _bundleDir.FullName)); // already present
        Assert.Equal(2, library.GetAll().Count);
    }

    [Fact]
    public void InstallFromDirectory_MissingDirOrManifest_IsZero_Throwfree()
    {
        var library = NewLibrary();
        Assert.Equal(0, BundledNamCaptureInstaller.InstallFromDirectory(library, null));
        Assert.Equal(0, BundledNamCaptureInstaller.InstallFromDirectory(
            library, Path.Combine(_bundleDir.FullName, "nope")));
        // Dir exists but no manifest.json:
        Assert.Equal(0, BundledNamCaptureInstaller.InstallFromDirectory(library, _bundleDir.FullName));
    }

    // ---------------------------------------------------------- ParseManifest

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void ParseManifest_BadInput_IsEmpty_NeverThrows(string? json)
        => Assert.Empty(BundledNamCaptureInstaller.ParseManifest(json));

    [Fact]
    public void ParseManifest_DropsEntriesWithoutAFileName()
    {
        var list = BundledNamCaptureInstaller.ParseManifest("""
            [ { "fileName":"ok.nam","license":"MIT" }, { "title":"no file" } ]
            """);
        Assert.Equal("ok.nam", Assert.Single(list).FileName);
    }

    // ---------------------------------------------------------- SyntheticId

    [Fact]
    public void SyntheticId_IsStable_Negative_AndDistinctPerName()
    {
        var a1 = BundledNamCapture.SyntheticId("a.nam");
        var a2 = BundledNamCapture.SyntheticId("a.nam");
        var b = BundledNamCapture.SyntheticId("b.nam");
        Assert.Equal(a1, a2);
        Assert.True(a1 < 0 && b < 0);
        Assert.NotEqual(a1, b);
        // Case-insensitive on the file name.
        Assert.Equal(a1, BundledNamCapture.SyntheticId("A.NAM"));
    }
}
