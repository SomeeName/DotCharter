using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
/// Tests for <see cref="INamCaptureLibrary.MigrateFileExtensions"/>: a capture
/// stored with the wrong extension (a pre-fix download that wrote <c>.bin</c>, or
/// a wrong-case <c>.NAM</c>) is renamed to the correct lowercase extension so the
/// NeuralAudio loader — which dispatches by extension — can actually load it.
/// The owner hit exactly this: a valid NAM stored as <c>…-standard.bin</c> played
/// silently.
/// </summary>
public sealed class NamCaptureLibraryMigrationTests : IDisposable
{
    private readonly DirectoryInfo _libDir = Directory.CreateTempSubdirectory("dc-nam-mig-");
    private readonly DirectoryInfo _srcDir = Directory.CreateTempSubdirectory("dc-nam-mig-src-");

    public void Dispose()
    {
        foreach (var d in new[] { _libDir, _srcDir })
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

    // A minimal but schema-shaped modern NAM body: the loader sniff keys on
    // "architecture"; the content need not be a real model for the rename test.
    private const string NamBody =
        """{ "version":"0.5.4", "architecture":"WaveNet", "config":{}, "weights":[0.0] }""";

    /// <summary>Seeds one library entry via ImportBundled under the given file
    /// name (its manifest.Format becomes the extension, e.g. "bin"), returning
    /// the library.</summary>
    private NamCaptureLibrary SeedEntry(string fileName, string body = NamBody)
    {
        var src = Path.Combine(_srcDir.FullName, "src-" + fileName);
        File.WriteAllText(src, body);
        var library = NewLibrary();
        var meta = new BundledNamCapture(
            fileName, "Marshall JCM 800", "someone", "amp", /*format*/ "", "standard",
            "MIT", "https://x/license", "https://x/attr");
        Assert.True(library.ImportBundled(src, meta));
        return library;
    }

    [Fact]
    public void Migrate_RenamesBinToNam_UpdatesManifest_AndFile()
    {
        var library = SeedEntry("82067-marshall-jcm-800-standard.bin");
        Assert.Equal("82067-marshall-jcm-800-standard.bin", library.GetAll().Single().Manifest.FileName);

        Assert.Equal(1, library.MigrateFileExtensions());

        var entry = library.GetAll().Single();
        Assert.Equal("82067-marshall-jcm-800-standard.nam", entry.Manifest.FileName);
        Assert.True(File.Exists(entry.FilePath));
        Assert.EndsWith(".nam", entry.FilePath);
        // The old .bin file and its stale sidecar are gone.
        Assert.False(File.Exists(Path.Combine(_libDir.FullName, "82067-marshall-jcm-800-standard.bin")));
        Assert.False(File.Exists(Path.Combine(_libDir.FullName,
            NamCaptureIndex.ManifestFileName("82067-marshall-jcm-800-standard.bin"))));
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var library = SeedEntry("cap.bin");
        Assert.Equal(1, library.MigrateFileExtensions());
        Assert.Equal(0, library.MigrateFileExtensions()); // already correct now
        Assert.Single(library.GetAll());
    }

    [Fact]
    public void Migrate_FixesWrongCaseExtension()
    {
        // ImportBundled records Format from the extension ("NAM"); the loader needs
        // lowercase ".nam", so a wrong-case file must be repaired.
        var library = SeedEntry("loud.NAM");
        Assert.Equal(1, library.MigrateFileExtensions());
        Assert.EndsWith(".nam", library.GetAll().Single().Manifest.FileName);
    }

    [Fact]
    public void Migrate_LeavesCorrectlyNamedFileAlone()
    {
        var library = SeedEntry("good-standard.nam");
        Assert.Equal(0, library.MigrateFileExtensions());
        Assert.Equal("good-standard.nam", library.GetAll().Single().Manifest.FileName);
    }
}
