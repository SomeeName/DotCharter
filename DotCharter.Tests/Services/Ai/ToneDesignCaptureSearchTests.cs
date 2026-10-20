using DotCharter.Tone.Ai;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     A design may ask TONE3000 for a capture the player does not have yet
///     (<see cref="ToneDraft.CaptureSearch" />): the prompt offers the field only
///     while a search can run, the APP runs it and downloads, and the <c>namamp</c>
///     comes back pointing at the downloaded file. Every failure on that path is a
///     warning and never a lost design.
/// </summary>
public sealed class ToneDesignCaptureSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The rule-12 sentence only the capture-search prompts carry.
    private const string SearchRule = "The app downloads the best";

    // ---- the prompt ------------------------------------------------------------

    [Fact]
    public void TheSearchRule_IsOfferedOnlyWhileASearchCanRun()
    {
        var none = NamCaptureCatalog.Empty;
        var some = NamCaptureCatalog.FromEntries([Entry("5150-blockletter.nam", "5150 Block Letter", -5)]);

        Assert.DoesNotContain(SearchRule, ToneDesignPromptBuilder.SystemPromptFor(none), StringComparison.Ordinal);
        Assert.DoesNotContain(SearchRule, ToneDesignPromptBuilder.SystemPromptFor(some), StringComparison.Ordinal);
        Assert.Contains(SearchRule, ToneDesignPromptBuilder.SystemPromptFor(none, captureSearch: true), StringComparison.Ordinal);
        Assert.Contains(SearchRule, ToneDesignPromptBuilder.SystemPromptFor(some, captureSearch: true), StringComparison.Ordinal);

        // With the search on, namamp is usable with an EMPTY library — the "avoid it"
        // note and the old rule 12 must both be gone, or the model is told two things.
        var searchOnly = ToneDesignPromptBuilder.SystemPromptFor(none, captureSearch: true);
        Assert.DoesNotContain(AiGearNotes.NamAmpNoteWithoutCaptures, searchOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not use namamp", searchOnly, StringComparison.Ordinal);
        // …and the anchored prompt takes the same flag.
        Assert.Contains(SearchRule, ToneDesignPromptBuilder.AnchoredSystemPromptFor(some, captureSearch: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheSearchPrompt_FitsTheBudget_WithTheWholeBundledSet()
    {
        var bundled = NamCaptureCatalog.FromEntries(Enumerable.Range(0, 63)
            .Select(i => Entry($"bundled-capture-number-{i:00}.nam", $"Bundled Capture Number {i:00}", -1 - i)));

        var tokens = ToneDesignPromptBuilder.EstimateTokens(
            ToneDesignPromptBuilder.SystemPromptFor(bundled, captureSearch: true));

        Assert.True(tokens < 12_000, $"the capture-search prompt is ~{tokens} estimated tokens; the budget is 12,000.");
    }

    // ---- picking a tone --------------------------------------------------------

    [Fact]
    public void PickTone_TakesTheMostDownloadedOfTheFirstFiveNamResults()
    {
        var tones = new[]
        {
            Tone(1, downloads: 10),
            Tone(2, downloads: 500, formats: ["ir"]), // not a NAM capture
            Tone(3, downloads: 90),
            Tone(4, downloads: 90), // tie: the server's order keeps 3
            Tone(5, downloads: 40),
            Tone(6, downloads: 20),
            Tone(7, downloads: 99_999) // sixth NAM result: past what is weighed
        };

        Assert.Equal(3, Tone3000CaptureSource.PickTone(tones)!.Id);
        Assert.Null(Tone3000CaptureSource.PickTone([Tone(9, 5, ["ir"])]));
        Assert.Null(Tone3000CaptureSource.PickTone([]));
        // An empty format list is unknown on the wire, not "not NAM".
        Assert.Equal(8, Tone3000CaptureSource.PickTone([Tone(8, 1, [])])!.Id);
    }

    [Fact]
    public void TheQuery_IsOneTrimmedCappedLine()
    {
        Assert.Equal("Fender Deluxe Reverb clean",
            Tone3000CaptureSource.NormalizeQuery("  \"Fender  Deluxe\nReverb\tclean\" "));
        Assert.Equal("", Tone3000CaptureSource.NormalizeQuery("   "));
        Assert.Equal(Tone3000CaptureSource.MaxQueryLength,
            Tone3000CaptureSource.NormalizeQuery(new string('a', 300)).Length);
    }

    // ---- the source ------------------------------------------------------------

    [Fact]
    public async Task ASignedOutSource_SearchesNothing()
    {
        var service = new FakeTone3000();
        var source = new Tone3000CaptureSource(service, new FakeAuth(signedIn: false), new FakeLibrary());

        var found = await source.FindAsync("Fender Twin", Ct);

        Assert.False(source.IsAvailable);
        Assert.Null(found.Entry);
        Assert.Contains("not signed in", found.Problem);
        Assert.Equal(0, service.Searches);
    }

    [Fact]
    public async Task ASearch_DownloadsTheLiteAmpCapture()
    {
        var service = new FakeTone3000 { Page = [Tone(41, 300)] };
        service.Models[41] = [Model(1, 41, "standard"), Model(2, 41, "lite")];
        var library = new FakeLibrary();
        var source = new Tone3000CaptureSource(service, new FakeAuth(signedIn: true), library);

        var found = await source.FindAsync("\"Fender Twin clean\"", Ct);

        Assert.True(found.Downloaded);
        Assert.Null(found.Problem);
        Assert.Equal("Fender Twin clean", service.LastQuery!.Query);
        Assert.Equal(Tone3000GearType.Amp, service.LastQuery.GearType);
        Assert.Equal(2, Assert.Single(library.Downloads).Model.Id);
        Assert.Equal(41, found.Entry!.Manifest.ToneId);
    }

    [Fact]
    public async Task ACaptureAlreadyInTheLibrary_IsReused_NotDownloadedAgain()
    {
        var service = new FakeTone3000 { Page = [Tone(41, 300)] };
        var library = new FakeLibrary();
        library.Entries.Add(Entry("tone-41.nam", "Tone 41", 7, toneId: 41));
        var source = new Tone3000CaptureSource(service, new FakeAuth(signedIn: true), library);

        var found = await source.FindAsync("Fender Twin", Ct);

        Assert.False(found.Downloaded);
        Assert.Equal("tone-41.nam", found.Entry!.Manifest.FileName);
        Assert.Equal(0, service.ModelLookups);
        Assert.Empty(library.Downloads);
    }

    [Fact]
    public async Task AnExpiredSession_IsInvalidated_AndSaysSo()
    {
        var auth = new FakeAuth(signedIn: true);
        var service = new FakeTone3000 { SearchError = OnlineError.NotSignedIn };
        var source = new Tone3000CaptureSource(service, auth, new FakeLibrary());

        var found = await source.FindAsync("Fender Twin", Ct);

        Assert.Null(found.Entry);
        Assert.Contains("expired", found.Problem);
        Assert.Equal(1, auth.Invalidations);
    }

    // ---- the design service ----------------------------------------------------

    private const string SearchReply = """
        {"name":"Twin Clean","rationale":"r","confidence":0.8,"captureSearch":"Fender Twin clean",
         "modules":[{"type":"namamp","params":[],"bypassed":false,"capture":""},
                    {"type":"cab","params":[],"bypassed":false,"capture":""}]}
        """;

    [Fact]
    public async Task ADesignThatSearches_ComesBackPlayingTheDownloadedCapture()
    {
        var library = new FakeLibrary();
        var source = new ScriptedSource(available: true, Entry("fender-twin-clean.nam", "Fender Twin Clean", 88, toneId: 41), library);
        var model = new CapturingModel(SearchReply);
        var service = Service(model, library, source);

        var result = await service.DesignAsync(new ToneDesignRequest("a Twin clean", [], null), null, Ct);

        Assert.Contains(SearchRule, model.LastPrompt!.System, StringComparison.Ordinal);
        Assert.Equal("Fender Twin clean", source.LastQuery);
        var namamp = Assert.Single(result.Tone.Rig.Modules, m => m.ModuleTypeId == "namamp");
        Assert.Equal(Path.Combine(Dir, "fender-twin-clean.nam"), namamp.StringParams!["modelPath"]);
        Assert.Equal("Fender Twin Clean", Assert.Single(result.DownloadedCaptures!).Title);
        Assert.Empty(result.Warnings);
        // The reply the next refinement turn sees names the capture by its list id.
        var raw = System.Text.Json.JsonSerializer.Deserialize<ToneDraft>(result.RawJson, ToneDraftJson.Options)!;
        Assert.Equal("fender-twin-clean", raw.Modules.Single(m => m.Type == "namamp").Capture);
        Assert.Equal("", raw.CaptureSearch);
    }

    [Fact]
    public async Task ASearchWithNoSignIn_IsAWarning_AndTheRestOfTheChainStillComesBack()
    {
        var library = new FakeLibrary();
        var source = new ScriptedSource(available: false, null, library);
        var model = new CapturingModel(SearchReply);
        var service = Service(model, library, source);

        var result = await service.DesignAsync(new ToneDesignRequest("a Twin clean", [], null), null, Ct);

        Assert.DoesNotContain(SearchRule, model.LastPrompt!.System, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("not signed in", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Tone.Rig.Modules, m => m.ModuleTypeId == "namamp");
        Assert.Contains(result.Tone.Rig.Modules, m => m.ModuleTypeId == "cab");
        Assert.Empty(result.DownloadedCaptures!);
    }

    [Fact]
    public async Task AListedCapture_WinsOverTheSearch_AndNothingIsDownloaded()
    {
        var library = new FakeLibrary();
        library.Entries.Add(Entry("5150-blockletter.nam", "5150 Block Letter", -5));
        var source = new ScriptedSource(available: true, Entry("x.nam", "X", 9), library);
        var reply = SearchReply.Replace("\"type\":\"namamp\",\"params\":[],\"bypassed\":false,\"capture\":\"\"",
            "\"type\":\"namamp\",\"params\":[],\"bypassed\":false,\"capture\":\"5150-blockletter\"", StringComparison.Ordinal);
        var service = Service(new CapturingModel(reply), library, source);

        var result = await service.DesignAsync(new ToneDesignRequest("5150", [], null), null, Ct);

        Assert.Null(source.LastQuery);
        Assert.Equal(Path.Combine(Dir, "5150-blockletter.nam"),
            Assert.Single(result.Tone.Rig.Modules, m => m.ModuleTypeId == "namamp").StringParams!["modelPath"]);
    }

    // ---- fixtures --------------------------------------------------------------

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "dotcharter-capture-search");

    private static ToneDesignService Service(IToneDesignModel model, FakeLibrary library, INamCaptureSource source)
    {
        var config = new RigConfig { Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null) };
        return new ToneDesignService(
            () => config,
            new SingleModelFactory(model),
            namCaptureDirectory: () => Dir,
            captures: () => NamCaptureCatalog.FromLibrary(library),
            captureSource: source,
            localization: FakeLocalization.English());
    }

    private static NamCaptureEntry Entry(string fileName, string title, long modelId, long toneId = 0)
        => new(
            new NamCaptureManifest(
                toneId == 0 ? modelId : toneId, modelId, title, "Someone", "amp", "nam", "lite",
                "MIT", "https://example.invalid/license", "https://example.invalid/attribution",
                fileName, "sha", 1024, DateTimeOffset.UnixEpoch),
            Path.Combine(Dir, fileName));

    private static Tone3000Tone Tone(long id, long downloads, string[]? formats = null)
        => new(id, $"Tone {id}", "Someone", "amp", "MIT", "", "", "", formats ?? ["nam"], ["lite"], downloads);

    private static Tone3000Model Model(long id, long toneId, string tier)
        => new(id, toneId, $"model {id}", "", tier, "1", $"https://example.invalid/{id}", 1024);

    /// <summary>A source that answers with a fixed entry and, like the real one, puts
    /// it in the library so the re-read catalogue lists it.</summary>
    private sealed class ScriptedSource(bool available, NamCaptureEntry? entry, FakeLibrary library) : INamCaptureSource
    {
        public string? LastQuery { get; private set; }
        public bool IsAvailable => available;

        public Task<NamCaptureSourcing> FindAsync(string query, CancellationToken ct)
        {
            LastQuery = query;
            if (!available || entry is null)
                return Task.FromResult(NamCaptureSourcing.Failed($"The design asked TONE3000 for \"{query}\", but TONE3000 is not signed in."));
            library.Entries.Add(entry);
            return Task.FromResult(new NamCaptureSourcing(entry, true, null));
        }
    }

    private sealed class FakeTone3000 : ITone3000Service
    {
        public IReadOnlyList<Tone3000Tone> Page { get; init; } = [];
        public Dictionary<long, IReadOnlyList<Tone3000Model>> Models { get; } = [];
        public OnlineError SearchError { get; init; }
        public Tone3000SearchQuery? LastQuery { get; private set; }
        public int Searches { get; private set; }
        public int ModelLookups { get; private set; }

        public Task<OnlineResult<Tone3000SearchPage>> SearchAsync(Tone3000SearchQuery query, CancellationToken ct = default)
        {
            Searches++;
            LastQuery = query;
            return Task.FromResult(SearchError != OnlineError.None
                ? OnlineResult<Tone3000SearchPage>.Fail(SearchError)
                : OnlineResult<Tone3000SearchPage>.Success(new Tone3000SearchPage(Page, 1, Page.Count)));
        }

        public Task<OnlineResult<Tone3000Tone>> GetToneAsync(long toneId, CancellationToken ct = default)
            => Task.FromResult(OnlineResult<Tone3000Tone>.Fail(OnlineError.InvalidRequest));

        public Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(long toneId, CancellationToken ct = default)
        {
            ModelLookups++;
            return Task.FromResult(Models.TryGetValue(toneId, out var models)
                ? OnlineResult<IReadOnlyList<Tone3000Model>>.Success(models)
                : OnlineResult<IReadOnlyList<Tone3000Model>>.Fail(OnlineError.InvalidRequest));
        }

        public Task<OnlineResult<long>> DownloadModelAsync(Tone3000Model model, Stream destination, long maxBytes,
            IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(OnlineResult<long>.Fail(OnlineError.InvalidRequest));
    }

    private sealed class FakeAuth(bool signedIn) : ITone3000AuthService
    {
        public int Invalidations { get; private set; }
        public bool IsConfigured => true;
        public bool IsSignedIn => signedIn;
        public string UserName => signedIn ? "player" : "";
        public event Action? AuthStateChanged { add { } remove { } }
        public Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default)
            => Task.FromResult(OnlineResult<Tone3000User>.Fail(OnlineError.NotConfigured));
        public void SignOut() { }
        public void InvalidateSession(string reason) => Invalidations++;
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeLibrary : INamCaptureLibrary
    {
        public List<NamCaptureEntry> Entries { get; } = [];
        public List<(Tone3000Tone Tone, Tone3000Model Model)> Downloads { get; } = [];
        public string RootDirectory => Dir;
        public IReadOnlyList<NamCaptureEntry> GetAll() => Entries.ToList();
        public bool IsModelDownloaded(long modelId) => Entries.Any(e => e.Manifest.ModelId == modelId);
        public bool IsToneDownloaded(long toneId) => Entries.Any(e => e.Manifest.ToneId == toneId);

        public Task<OnlineResult<NamCaptureEntry>> DownloadAsync(Tone3000Tone tone, Tone3000Model model,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Downloads.Add((tone, model));
            var entry = Entry($"tone-{tone.Id}-{model.Id}.nam", tone.Title, model.Id, tone.Id);
            Entries.Add(entry);
            return Task.FromResult(OnlineResult<NamCaptureEntry>.Success(entry));
        }

        public bool Delete(long modelId) => false;
        public int MigrateFileExtensions() => 0;
        public bool ImportBundled(string sourceFilePath, BundledNamCapture meta) => false;
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class SingleModelFactory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;
        public bool Supports(AiProviderKind kind) => kind is AiProviderKind.Claude;
    }

    private sealed class CapturingModel(string json) : IToneDesignModel
    {
        public ToneDesignPrompt? LastPrompt { get; private set; }
        public AiProviderKind Kind => AiProviderKind.Claude;

        public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult(new ToneDesignModelReply(json, new AiUsage("claude-opus-5", 9000, 200)));
        }

        public Task<AiProbeResult> ProbeAsync(CancellationToken ct)
            => Task.FromResult(new AiProbeResult(true, "ok", "claude-opus-5"));
    }
}
