using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     The TONE3000 API terms (issue #867) ask for the creator, the tone's
///     metadata and its licence to be preserved and shown. This follows one
///     capture from the API JSON through the real <see cref="NamCaptureLibrary" />
///     (download, manifest on disk, reload) to the rows the capture browser shows,
///     and pins the "Powered by TONE3000" link target and its localization.
/// </summary>
public sealed class Tone3000AttributionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-t3k-attr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best effort */ }
    }

    private sealed class WritingService : ITone3000Service
    {
        public Task<OnlineResult<Tone3000SearchPage>> SearchAsync(Tone3000SearchQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OnlineResult<Tone3000Tone>> GetToneAsync(long toneId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(long toneId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<OnlineResult<long>> DownloadModelAsync(
            Tone3000Model model, Stream destination, long maxBytes,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct);
            return OnlineResult<long>.Success(4);
        }
    }

    private const string SearchJson = """
        { "data": [ { "id": 77, "title": "Plexi Crunch", "gear": "amp",
            "user": { "username": "riff_wizard" },
            "license": { "name": "CC-BY-4.0", "url": "https://creativecommons.org/licenses/by/4.0/" },
            "url": "https://www.tone3000.com/tones/plexi-crunch-77",
            "formats": ["nam"], "sizes": ["standard"] } ], "page": 1, "total": 1 }
        """;

    [Fact]
    public async Task CreatorAndLicence_RoundTrip_FromApiJson_ToTheRowsTheBrowserShows()
    {
        var tone = Tone3000Json.ParseSearchPage(SearchJson, 1).Tones.Single();
        Assert.Equal("riff_wizard", tone.Author);
        Assert.Equal("CC-BY-4.0", tone.License);

        var loc = FakeLocalization.English();
        var searchRow = new NamToneRowViewModel(tone, isDownloaded: false, localization: loc);
        Assert.True(searchRow.HasAuthor);
        Assert.Equal("riff_wizard", searchRow.Author);
        Assert.Equal("CC-BY-4.0", searchRow.LicenseLabel);

        var model = new Tone3000Model(5, 77, "m", "nam", "standard", "1", "https://example.invalid/m.nam", 0);
        var library = new NamCaptureLibrary(new WritingService(), rootDirectoryOverride: _root);
        var downloaded = await library.DownloadAsync(tone, model, ct: TestContext.Current.CancellationToken);
        Assert.True(downloaded.Ok, downloaded.Message);

        // What is stored survives a cold reload from disk...
        var reloaded = new NamCaptureLibrary(new WritingService(), rootDirectoryOverride: _root).GetAll().Single().Manifest;
        Assert.Equal("riff_wizard", reloaded.Author);
        Assert.Equal("CC-BY-4.0", reloaded.License);
        Assert.Equal("https://creativecommons.org/licenses/by/4.0/", reloaded.LicenseUrl);
        Assert.Equal("https://www.tone3000.com/tones/plexi-crunch-77", reloaded.AttributionUrl);
        Assert.Equal("Plexi Crunch", reloaded.Title);

        // ...and is what the library row shows.
        var libraryRow = new NamLibraryRowViewModel(reloaded, loc);
        Assert.True(libraryRow.HasAuthor);
        Assert.Equal("riff_wizard", libraryRow.Author);
        Assert.Equal("CC-BY-4.0", libraryRow.LicenseLabel);
        Assert.True(libraryRow.HasAttribution);
    }

    [Fact]
    public void PoweredByTone3000_LinksToTheTone3000Home_AndIsLocalizedInAllThreeLanguages()
    {
        string? opened = null;
        var browser = new NamCaptureBrowserViewModel(
            new WritingService(), new NullAuth(), new NamCaptureLibrary(new WritingService(), rootDirectoryOverride: _root),
            () => new FakeDspChainService(), FakeLocalization.English(), () => { },
            openUrl: url =>
            {
                opened = url;
                return true;
            });

        browser.OpenTone3000Command.Execute(null);

        Assert.Equal("https://www.tone3000.com", opened);
        Assert.Equal("https://www.tone3000.com", browser.Tone3000Url);
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file));
            Assert.Contains("name=\"Nam_PoweredByAutomation\"", text);
            Assert.Contains("TONE3000", text);
        }
    }

    private sealed class NullAuth : ITone3000AuthService
    {
        public bool IsConfigured => false;
        public bool IsSignedIn => false;
        public string UserName => string.Empty;
        public event Action? AuthStateChanged { add { } remove { } }
        public void InvalidateSession(string reason) { }
        public Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void SignOut() { }
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
