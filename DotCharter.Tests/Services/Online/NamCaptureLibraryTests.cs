using System.Security.Cryptography;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
/// Tests for <see cref="NamCaptureLibrary" /> against a fake
/// <see cref="ITone3000Service" /> and a temp directory: download hardening
/// (temp file + size sanity + hash + atomic rename), the manifest sidecar
/// round-trip (license/attribution preserved) and delete.
/// </summary>
public sealed class NamCaptureLibraryTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dc-nam-tests-");

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch
        {
            // temp cleanup best-effort
        }
    }

    private sealed class FakeTone3000Service : ITone3000Service
    {
        public byte[] Payload = [1, 2, 3, 4, 5];
        public OnlineResult<long>? DownloadOverride;

        public Task<OnlineResult<Tone3000SearchPage>> SearchAsync(
            Tone3000SearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<Tone3000SearchPage>.Success(new Tone3000SearchPage([], 1, 0)));

        public Task<OnlineResult<Tone3000Tone>> GetToneAsync(long toneId, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<Tone3000Tone>.Fail(OnlineError.InvalidRequest));

        public Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(
            long toneId, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<IReadOnlyList<Tone3000Model>>.Success([]));

        public async Task<OnlineResult<long>> DownloadModelAsync(
            Tone3000Model model, Stream destination, long maxBytes,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (DownloadOverride != null) return DownloadOverride;
            await destination.WriteAsync(Payload, ct);
            progress?.Report(1.0);
            return OnlineResult<long>.Success(Payload.Length);
        }
    }

    private static Tone3000Tone Tone(long id = 41) => new(
        id, "Plexi Crunch", "capturist", "amp", "CC-BY",
        "https://creativecommons.org/licenses/by/4.0/",
        "https://www.tone3000.com/tones/41", "hot plexi", ["nam"], ["standard"], 5);

    private static Tone3000Model Model(long id = 9, long declaredBytes = 0) => new(
        id, 41, "Standard", "nam", "standard", "wavenet",
        "https://cdn.tone3000.com/models/9.nam", declaredBytes);

    private NamCaptureLibrary NewLibrary(FakeTone3000Service service) =>
        new(service, rootDirectoryOverride: _dir.FullName);

    [Fact]
    public async Task Download_StoresFileAndManifest_WithHashAndAttribution()
    {
        var service = new FakeTone3000Service();
        var library = NewLibrary(service);
        var changed = 0;
        library.Changed += (_, _) => changed++;

        var result = await library.DownloadAsync(Tone(), Model(), ct: Ct);

        Assert.True(result.Ok);
        var entry = result.Value!;
        Assert.True(File.Exists(entry.FilePath));
        Assert.Equal(service.Payload, await File.ReadAllBytesAsync(entry.FilePath, Ct));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(service.Payload)), entry.Manifest.Sha256);
        Assert.Equal("CC-BY", entry.Manifest.License);
        Assert.Equal("https://www.tone3000.com/tones/41", entry.Manifest.AttributionUrl);
        Assert.Equal(1, changed);

        // No .partial residue.
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*.partial"));

        // A fresh library instance reads it back from disk (manifest round-trip).
        var reloaded = NewLibrary(service).GetAll();
        var manifest = Assert.Single(reloaded).Manifest;
        Assert.Equal(41, manifest.ToneId);
        Assert.Equal(9, manifest.ModelId);
        Assert.Equal("Plexi Crunch", manifest.Title);
        Assert.Equal("capturist", manifest.Author);
        Assert.Equal("amp", manifest.GearType);
        Assert.Equal("nam", manifest.Format);
        Assert.Equal("standard", manifest.SizeTier);
        Assert.Equal(service.Payload.Length, manifest.FileBytes);
        Assert.True(NewLibrary(service).IsModelDownloaded(9));
        Assert.True(NewLibrary(service).IsToneDownloaded(41));
    }

    [Fact]
    public async Task Download_FormatlessModel_TakesTheExtensionFromTheTone()
    {
        // The live API's model objects carry NO `format` — it is a property of
        // the parent TONE — and the extension is load-bearing (NeuralAudio
        // dispatches its loader by it). A formatless model whose URL is also
        // extensionless must NOT default to .nam when the tone says aida-x.
        var service = new FakeTone3000Service();
        var library = NewLibrary(service);
        var tone = Tone() with { Formats = ["aida-x"] };
        var model = new Tone3000Model(
            9, 41, "Standard", string.Empty, "standard", "2",
            "https://www.tone3000.com/api/v1/models/9/file", 0);

        var result = await library.DownloadAsync(tone, model, ct: Ct);

        Assert.True(result.Ok);
        Assert.EndsWith(".aidax", result.Value!.FilePath);
        Assert.Equal("aida-x", result.Value.Manifest.Format);
    }

    [Fact]
    public async Task Download_ServiceFailure_LeavesNoResidue()
    {
        var service = new FakeTone3000Service
        {
            DownloadOverride = OnlineResult<long>.Fail(OnlineError.NotSignedIn, "sign in")
        };
        var library = NewLibrary(service);

        var result = await library.DownloadAsync(Tone(), Model(), ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.NotSignedIn, result.Error);
        Assert.Empty(Directory.GetFiles(_dir.FullName));
        Assert.Empty(library.GetAll());
    }

    [Fact]
    public async Task Download_DeclaredSizeMismatch_IsRejected()
    {
        var service = new FakeTone3000Service(); // writes 5 bytes
        var library = NewLibrary(service);

        var result = await library.DownloadAsync(Tone(), Model(declaredBytes: 999), ct: Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Server, result.Error);
        Assert.Empty(Directory.GetFiles(_dir.FullName));
    }

    [Fact]
    public async Task Download_EmptyPayload_IsRejected()
    {
        var service = new FakeTone3000Service
        {
            Payload = [],
            DownloadOverride = OnlineResult<long>.Success(0)
        };
        var library = NewLibrary(service);

        var result = await library.DownloadAsync(Tone(), Model(), ct: Ct);

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFiles(_dir.FullName));
    }

    [Fact]
    public async Task Delete_RemovesFileAndManifest()
    {
        var service = new FakeTone3000Service();
        var library = NewLibrary(service);
        await library.DownloadAsync(Tone(), Model(), ct: Ct);
        var changed = 0;
        library.Changed += (_, _) => changed++;

        Assert.True(library.Delete(9));

        Assert.Empty(Directory.GetFiles(_dir.FullName));
        Assert.Empty(library.GetAll());
        Assert.False(library.IsModelDownloaded(9));
        Assert.Equal(1, changed);

        Assert.False(library.Delete(9)); // unknown now
    }

    [Fact]
    public async Task GetAll_SkipsCorruptManifests_AndOrphanSidecars()
    {
        var service = new FakeTone3000Service();
        var library = NewLibrary(service);
        await library.DownloadAsync(Tone(), Model(), ct: Ct);

        // Corrupt sidecar + orphan sidecar (its capture file is missing).
        await File.WriteAllTextAsync(
            Path.Combine(_dir.FullName, "broken.nam.t3k.json"), "{ not json", Ct);
        await File.WriteAllTextAsync(
            Path.Combine(_dir.FullName, "gone.nam.t3k.json"),
            """{ "fileName": "gone.nam", "modelId": 77 }""", Ct);

        var entries = library.GetAll();

        Assert.Single(entries);
        Assert.Equal(9, entries[0].Manifest.ModelId);
    }
}
