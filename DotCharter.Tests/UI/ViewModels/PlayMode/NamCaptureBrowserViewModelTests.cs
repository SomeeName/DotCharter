using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
/// VM tests for the pedalboard's "NAM Captures" section against fake TONE3000
/// services: search fills rows (typed errors surface as status, never throws),
/// download drives the library + flips the row state, the library view filters
/// through NamCaptureIndex, and "Load into rig" stays disabled until the DSP
/// catalogue exposes a namamp module type (the Wave-3 dormancy contract). Also
/// covers the rail integration: the NAM entry only exists when the browser is
/// wired, and selecting it empties the card grid. No Avalonia instance.
/// </summary>
public sealed class NamCaptureBrowserViewModelTests
{
    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeTone3000Service : ITone3000Service
    {
        public OnlineResult<Tone3000SearchPage> SearchResult =
            OnlineResult<Tone3000SearchPage>.Success(new Tone3000SearchPage([], 1, 0));

        public OnlineResult<IReadOnlyList<Tone3000Model>> ModelsResult =
            OnlineResult<IReadOnlyList<Tone3000Model>>.Success([]);

        public Tone3000SearchQuery? LastQuery;

        public Task<OnlineResult<Tone3000SearchPage>> SearchAsync(
            Tone3000SearchQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(SearchResult);
        }

        public Task<OnlineResult<Tone3000Tone>> GetToneAsync(long toneId, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<Tone3000Tone>.Fail(OnlineError.InvalidRequest));

        public Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(
            long toneId, CancellationToken ct = default) => Task.FromResult(ModelsResult);

        public Task<OnlineResult<long>> DownloadModelAsync(
            Tone3000Model model, Stream destination, long maxBytes,
            IProgress<double>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<long>.Success(0));
    }

    private sealed class FakeTone3000AuthService : ITone3000AuthService
    {
        public bool Configured = true;
        public bool SignedIn;
        public string User = string.Empty;
        public int SignInCalls, SignOutCalls;
        public void InvalidateSession(string reason) => SignedIn = false;

        public bool IsConfigured => Configured;
        public bool IsSignedIn => SignedIn;
        public string UserName => User;
        public event System.Action? AuthStateChanged { add { } remove { } }

        public Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default)
        {
            SignInCalls++;
            SignedIn = true;
            return Task.FromResult(OnlineResult<Tone3000User>.Success(new Tone3000User("u", User)));
        }

        public void SignOut()
        {
            SignOutCalls++;
            SignedIn = false;
        }

        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(SignedIn ? "tok" : null);
    }

    private sealed class FakeNamCaptureLibrary : INamCaptureLibrary
    {
        public readonly List<NamCaptureEntry> Entries = [];
        public int GetAllCalls;
        public OnlineResult<NamCaptureEntry>? DownloadOverride { get; set; }
        public readonly List<(Tone3000Tone Tone, Tone3000Model Model)> Downloads = [];

        public string RootDirectory => "X:\\nam";
        public event System.EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<NamCaptureEntry> GetAll() { GetAllCalls++; return Entries.ToList(); }

        // Both of these go through GetAll, EXACTLY as the real library does
        // (a directory scan plus one JSON read per capture). Answering them from
        // a field instead made the per-row fan-out in the search free here and
        // invisible — which is how it shipped.
        public bool IsModelDownloaded(long modelId) => GetAll().Any(e => e.Manifest.ModelId == modelId);

        public bool IsToneDownloaded(long toneId) => GetAll().Any(e => e.Manifest.ToneId == toneId);

        public Task<OnlineResult<NamCaptureEntry>> DownloadAsync(
            Tone3000Tone tone, Tone3000Model model,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Downloads.Add((tone, model));
            if (DownloadOverride != null) return Task.FromResult(DownloadOverride);

            var manifest = new NamCaptureManifest(
                tone.Id, model.Id, tone.Title, tone.Author, tone.GearType, model.Format,
                model.SizeTier, tone.License, tone.LicenseUrl, tone.PageUrl,
                $"{model.Id}.nam", "00", 5, System.DateTimeOffset.UnixEpoch);
            var entry = new NamCaptureEntry(manifest, $"X:\\nam\\{model.Id}.nam");
            Entries.Add(entry);
            return Task.FromResult(OnlineResult<NamCaptureEntry>.Success(entry));
        }

        public bool Delete(long modelId) => Entries.RemoveAll(e => e.Manifest.ModelId == modelId) > 0;

        public int MigrateFileExtensions() => 0;

        public bool ImportBundled(string sourceFilePath, BundledNamCapture meta) => false;
    }

    private static Tone3000Tone Tone(long id = 41, string title = "Plexi Crunch", string gear = "amp") =>
        new(id, title, "capturist", gear, "CC-BY", "https://cc.org/by", $"https://t3k/tones/{id}",
            "", ["nam"], ["standard"], 3);

    private static NamCaptureBrowserViewModel NewVm(
        out FakeTone3000Service service,
        out FakeTone3000AuthService auth,
        out FakeNamCaptureLibrary library,
        out FakeDspChainService dsp,
        out System.Func<int> rigChanges,
        IReadOnlyList<DspModuleType>? catalog = null)
    {
        service = new FakeTone3000Service();
        auth = new FakeTone3000AuthService();
        library = new FakeNamCaptureLibrary();
        var chain = new FakeDspChainService(catalog);
        dsp = chain;
        var counter = 0;
        var vm = new NamCaptureBrowserViewModel(
            service, auth, library, () => chain, FakeLocalization.English(), () => counter++, openUrl: _ => true);
        rigChanges = () => counter;
        return vm;
    }

    // ── search ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     The "already downloaded" badge is resolved from ONE library scan per
    ///     search, not one per result row.
    ///     <para>
    ///         <c>IsToneDownloaded</c> re-enumerates the capture directory and
    ///         deserializes every manifest sidecar, so asking it per row was a
    ///         page-size × library-size fan-out of synchronous file I/O — a
    ///         25-row page over the 63 bundled captures is ~1,600 reads — and it
    ///         ran on the UI thread in the search's await continuation.
    ///     </para>
    ///     Structural, not timed: the assertion is that the scan count does not
    ///     grow with the number of rows.
    /// </summary>
    [Fact]
    public async Task Search_ScansTheLibraryOnce_NotOncePerResultRow()
    {
        var vm = NewVm(out var service, out var auth, out var library, out _, out _);
        auth.SignedIn = true;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage(
                [.. Enumerable.Range(1, 12).Select(i => Tone(i, $"Tone {i}"))], 1, 12));

        library.GetAllCalls = 0;
        await vm.SearchAsync();

        Assert.Equal(12, vm.Results.Count);
        // One scan for the page. A per-row lookup would be at least twelve.
        Assert.Equal(1, library.GetAllCalls);
    }

    [Fact]
    public async Task Search_FillsRows_WithChipsAndDownloadState()
    {
        var vm = NewVm(out var service, out var auth, out var library, out _, out _);
        auth.SignedIn = true;                          // search now requires a session
        library.Entries.Add(new NamCaptureEntry(
            new NamCaptureManifest(42, 9, "Owned", "a", "amp", "nam", "lite", "CC0", "", "",
                "9.nam", "00", 1, System.DateTimeOffset.UnixEpoch), "X:\\nam\\9.nam"));
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage([Tone(41), Tone(42, "Owned")], 1, 2));

        vm.SearchText = "plexi";
        vm.SelectedGear = vm.GearOptions.First(o => o.Value == Tone3000GearType.Amp);
        await vm.SearchAsync();

        Assert.Equal(2, vm.Results.Count);
        Assert.True(vm.HasResults);
        Assert.Equal("plexi", service.LastQuery!.Query);
        Assert.Equal(Tone3000GearType.Amp, service.LastQuery.GearType);

        var row = vm.Results[0];
        Assert.Equal("Plexi Crunch", row.Title);
        Assert.Equal("CC-BY", row.LicenseLabel);       // license chip is mandatory policy
        Assert.True(row.HasLicense);
        Assert.True(row.HasGear);
        Assert.Contains("NAM", row.FormatSizeLabel);
        Assert.False(row.IsDownloaded);
        Assert.True(vm.Results[1].IsDownloaded);       // already in the library
    }

    [Fact]
    public async Task Search_TypedFailure_SurfacesStatus_NeverThrows()
    {
        var vm = NewVm(out var service, out var auth, out _, out _, out _);
        auth.SignedIn = true;                          // signed in so the service is actually called
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Fail(OnlineError.Network, "dns down");

        await vm.SearchAsync();

        Assert.Empty(vm.Results);
        Assert.True(vm.HasStatus);
        // The server's own words, but framed as a FAILURE — a bare "dns down"
        // reads like a result.
        Assert.Contains("dns down", vm.StatusMessage);
        Assert.NotEqual("dns down", vm.StatusMessage);
        Assert.False(vm.IsSearching);
        // …and the "search TONE3000 for captures" invitation is gone: the click
        // DID do something.
        Assert.False(vm.ShowOnlineEmpty);
    }

    [Fact]
    public async Task Search_ZeroResults_IsDistinguishableFromNeverHavingSearched()
    {
        var vm = NewVm(out var service, out var auth, out _, out _, out _);
        auth.SignedIn = true;

        Assert.True(vm.ShowOnlineEmpty);               // nothing searched yet
        Assert.False(vm.HasStatus);

        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage([], 1, 0));
        await vm.SearchAsync();

        Assert.False(vm.ShowOnlineEmpty);              // a search RAN
        Assert.True(vm.HasStatus);                     // and said so
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Search_Success_ReportsHowManyItFound()
    {
        var vm = NewVm(out var service, out var auth, out _, out _, out _);
        auth.SignedIn = true;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage([Tone(41), Tone(42)], 1, 57));

        await vm.SearchAsync();

        // Silence on success is indistinguishable from a click that did
        // nothing, which is how a completely broken search went unnoticed.
        Assert.True(vm.HasStatus);
        Assert.Contains("2", vm.StatusMessage);
        Assert.Contains("57", vm.StatusMessage);       // more pages available
    }

    [Fact]
    public async Task Search_SignedOut_ShowsSignInCta_WithoutCallingService()
    {
        var vm = NewVm(out var service, out var auth, out _, out _, out _);
        auth.SignedIn = false;

        vm.SearchText = "plexi";
        await vm.SearchAsync();

        Assert.True(vm.ShowSignInCta);                 // friendly CTA, not an error row
        Assert.Equal(FakeLocalization.English()["Nam_SignInRequired"], vm.StatusMessage);
        Assert.Null(service.LastQuery);                // never hit the API anonymously
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Search_SessionExpired_DoesNotSurfaceRawError()
    {
        // A signed-in search whose token the server rejects (NotSignedIn — e.g.
        // the session lapsed) is treated as "needs sign-in", NOT surfaced as a
        // scary search-failed status row.
        var vm = NewVm(out var service, out var auth, out _, out _, out _);
        auth.SignedIn = true;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Fail(OnlineError.NotSignedIn);

        await vm.SearchAsync();

        Assert.Equal(FakeLocalization.English()["Nam_SessionExpired"], vm.StatusMessage);
        Assert.False(auth.SignedIn);
        Assert.True(vm.ShowSignInCta);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task SignIn_RerunsPendingSearch()
    {
        var vm = NewVm(out var service, out var auth, out _, out _, out _);
        auth.SignedIn = false;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage([Tone()], 1, 1));

        vm.SearchText = "plexi";
        await vm.SearchAsync();                         // pends on sign-in, no service call
        Assert.Null(service.LastQuery);
        Assert.True(vm.ShowSignInCta);

        await vm.SignInCommand.ExecuteAsync(null);      // fake sign-in succeeds → auto re-run

        Assert.Equal("plexi", service.LastQuery!.Query);
        Assert.Single(vm.Results);
        Assert.False(vm.ShowSignInCta);
    }

    // ── download ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_SignedIn_DrivesLibrary_AndMarksRow()
    {
        var vm = NewVm(out var service, out var auth, out var library, out _, out _);
        auth.SignedIn = true;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage([Tone()], 1, 1));
        service.ModelsResult = OnlineResult<IReadOnlyList<Tone3000Model>>.Success(
        [
            new Tone3000Model(1, 41, "Standard", "nam", "standard", "wavenet", "https://u/1", 0),
            new Tone3000Model(2, 41, "Lite", "nam", "lite", "wavenet", "https://u/2", 0)
        ]);
        await vm.SearchAsync();
        var row = vm.Results.Single();

        await vm.DownloadAsync(row);

        var download = Assert.Single(library.Downloads);
        Assert.Equal(2, download.Model.Id);            // lite tier preferred (PLAN-0.16 4.4)
        Assert.True(row.IsDownloaded);
        Assert.False(row.IsDownloading);
        Assert.Single(vm.LibraryRows);                 // library view refreshed
    }

    [Fact]
    public async Task Download_SignedOut_AsksForSignIn_WithoutTouchingLibrary()
    {
        // Search a signed-in session for a row, then sign out and try to
        // download it: the download guard must refuse without touching the
        // library (defends against a session lapsing between search and click).
        var vm = NewVm(out var service, out var auth, out var library, out _, out _);
        auth.SignedIn = true;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage([Tone()], 1, 1));
        await vm.SearchAsync();
        auth.SignedIn = false;

        await vm.DownloadAsync(vm.Results.Single());

        Assert.Empty(library.Downloads);
        Assert.True(vm.HasStatus);
        Assert.False(vm.Results.Single().IsDownloaded);
    }

    [Fact]
    public void PickModel_PrefersLiteTierNam_ThenFallsBack()
    {
        static Tone3000Model M(long id, string format, string size) =>
            new(id, 41, "m", format, size, "", "https://u", 0);

        Assert.Equal(2, NamCaptureBrowserViewModel.PickModel(
            [M(1, "nam", "standard"), M(2, "nam", "lite"), M(3, "ir", "lite")]).Id);
        Assert.Equal(1, NamCaptureBrowserViewModel.PickModel(
            [M(1, "nam", "feather"), M(2, "ir", "lite")]).Id);
        Assert.Equal(1, NamCaptureBrowserViewModel.PickModel(
            [M(1, "nam", "exotic"), M(2, "ir", "")]).Id);
        Assert.Equal(1, NamCaptureBrowserViewModel.PickModel([M(1, "ir", "")]).Id);

        // The live API sends NO format on a model (it belongs to the parent
        // tone), so an empty format must stay eligible — otherwise every tier
        // preference falls through to models[0].
        Assert.Equal(2, NamCaptureBrowserViewModel.PickModel(
            [M(1, "", "standard"), M(2, "", "lite")]).Id);
    }

    [Fact]
    public void PickModel_PrefersTheRequestedArchitecture()
    {
        // GET /models?tone_id= lists EVERY architecture of the tone, so a
        // search filtered to A2 must not hand back the A1 file.
        static Tone3000Model M(long id, string size, string arch) =>
            new(id, 41, "m", "", size, arch, "https://u", 0);
        Tone3000Model[] models = [M(1, "lite", "1"), M(2, "standard", "2"), M(3, "lite", "2")];

        Assert.Equal(3, NamCaptureBrowserViewModel.PickModel(models, Tone3000Architecture.A2).Id);
        Assert.Equal(1, NamCaptureBrowserViewModel.PickModel(models, Tone3000Architecture.A1).Id);
        Assert.Equal(1, NamCaptureBrowserViewModel.PickModel(models).Id); // no filter ⇒ tier order

        // A tone with nothing in the requested architecture keeps the full pool
        // rather than failing the download.
        Assert.Equal(1, NamCaptureBrowserViewModel.PickModel(
            [M(1, "lite", "1")], Tone3000Architecture.A2).Id);
    }

    // ── architecture filter ──────────────────────────────────────────────────

    [Fact]
    public async Task Search_SendsTheSelectedArchitecture_AndDefaultsToTheServerSet()
    {
        var vm = NewVm(out var service, out var auth, out _, out _, out _);
        auth.SignedIn = true;

        await vm.SearchAsync();
        Assert.Equal(Tone3000Architecture.Default, service.LastQuery!.Architecture);

        vm.SelectedArchitecture = vm.ArchitectureOptions.First(o => o.Value == Tone3000Architecture.A2);
        await vm.SearchAsync();

        Assert.Equal(Tone3000Architecture.A2, service.LastQuery.Architecture);
        // The hint must say what the selection EXCLUDES — the whole reason A2
        // captures looked absent.
        Assert.NotEqual(string.Empty, vm.ArchitectureHint);
    }

    [Fact]
    public async Task Search_A2Download_TakesTheA2Model()
    {
        var vm = NewVm(out var service, out var auth, out var library, out _, out _);
        auth.SignedIn = true;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage([Tone()], 1, 1));
        service.ModelsResult = OnlineResult<IReadOnlyList<Tone3000Model>>.Success(
        [
            new Tone3000Model(1, 41, "Lite A1", "", "lite", "1", "https://u/1", 0),
            new Tone3000Model(2, 41, "Lite A2", "", "lite", "2", "https://u/2", 0)
        ]);
        vm.SelectedArchitecture = vm.ArchitectureOptions.First(o => o.Value == Tone3000Architecture.A2);
        await vm.SearchAsync();

        await vm.DownloadAsync(vm.Results.Single());

        Assert.Equal(2, Assert.Single(library.Downloads).Model.Id);
    }

    [Fact]
    public async Task Search_RowShowsWhichArchitecturesTheToneShips()
    {
        var vm = NewVm(out var service, out var auth, out _, out _, out _);
        auth.SignedIn = true;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage(
            [
                Tone() with { A1ModelCount = 2, A2ModelCount = 1 },
                Tone(42) with { A2ModelCount = 3 },
                Tone(43) // counts absent (older payload) ⇒ no chip, not a wrong claim
            ], 1, 3));

        await vm.SearchAsync();

        Assert.True(vm.Results[0].HasArchitecture);
        Assert.Contains("A2", vm.Results[0].ArchitectureLabel);
        Assert.Contains("A1", vm.Results[0].ArchitectureLabel);
        Assert.Equal("A2", vm.Results[1].ArchitectureLabel);
        Assert.False(vm.Results[2].HasArchitecture);
    }

    // ── library view ─────────────────────────────────────────────────────────

    /// <summary>The ctor runs while the Play Mode view populates at the
    /// DataContext bind; reading every capture manifest there was 20 ms of
    /// boot for a panel nothing shows yet. Construction touches the library
    /// ZERO times; the first reach of the panel (Activate, what selecting the
    /// NAM rail calls) loads the rows.</summary>
    [Fact]
    public void Construction_DoesNotTouchTheLibrary_ActivateLoadsIt()
    {
        var vm = NewVm(out _, out _, out var library, out _, out _);
        library.Entries.Add(Entry(1, 10, "Plexi Crunch", "amp"));
        Assert.Equal(0, library.GetAllCalls);
        Assert.Empty(vm.LibraryRows);

        vm.Activate();

        Assert.True(library.GetAllCalls >= 1);
        Assert.Single(vm.LibraryRows);
    }

    [Fact]
    public void LibraryView_FiltersThroughIndex_LiveOnSearchText()
    {
        var vm = NewVm(out _, out _, out var library, out _, out _);
        library.Entries.Add(Entry(1, 10, "Plexi Crunch", "amp"));
        library.Entries.Add(Entry(2, 20, "Fuzz Wall", "pedal"));
        vm.ShowLibraryCommand.Execute(null);

        Assert.Equal(2, vm.LibraryRows.Count);

        vm.SearchText = "plexi";
        Assert.Single(vm.LibraryRows);
        Assert.Equal("Plexi Crunch", vm.LibraryRows[0].Title);

        vm.SearchText = "";
        vm.SelectedGear = vm.GearOptions.First(o => o.Value == Tone3000GearType.Pedal);
        Assert.Single(vm.LibraryRows);
        Assert.Equal("Fuzz Wall", vm.LibraryRows[0].Title);
    }

    /// <summary>Shared by the library-view tests; hoisted from a local when the
    /// ctor-deferral test (below the fake) started needing it too.</summary>
    private static NamCaptureEntry Entry(long toneId, long modelId, string title, string gear) =>
        new(new NamCaptureManifest(toneId, modelId, title, "a", gear, "nam", "lite",
            "CC0", "", "", $"{modelId}.nam", "00", 1, System.DateTimeOffset.UnixEpoch),
            $"X:\\nam\\{modelId}.nam");

    [Fact]
    public async Task Delete_RemovesRow_AndUnmarksSearchResult()
    {
        var vm = NewVm(out var service, out var auth, out var library, out _, out _);
        auth.SignedIn = true;
        service.SearchResult = OnlineResult<Tone3000SearchPage>.Success(
            new Tone3000SearchPage([Tone()], 1, 1));
        service.ModelsResult = OnlineResult<IReadOnlyList<Tone3000Model>>.Success(
            [new Tone3000Model(1, 41, "Lite", "nam", "lite", "", "https://u/1", 0)]);
        await vm.SearchAsync();
        await vm.DownloadAsync(vm.Results.Single());
        var libraryRow = vm.LibraryRows.Single();

        vm.DeleteCommand.Execute(libraryRow);

        Assert.Empty(vm.LibraryRows);
        Assert.False(vm.Results.Single().IsDownloaded);
    }

    // ── load into rig: dormant until Wave 3 (ABI v22) ────────────────────────

    [Fact]
    public void LoadIntoRig_Disabled_WhileCatalogueLacksNamamp()
    {
        var vm = NewVm(out _, out _, out var library, out var dsp, out var rigChanges);
        library.Entries.Add(new NamCaptureEntry(
            new NamCaptureManifest(41, 9, "Plexi", "a", "amp", "nam", "lite", "CC0", "", "",
                "9.nam", "00", 1, System.DateTimeOffset.UnixEpoch), "X:\\nam\\9.nam"));
        vm.ShowLibraryCommand.Execute(null);

        Assert.False(vm.CanLoadIntoRig);

        vm.LoadIntoRigCommand.Execute(vm.LibraryRows.Single());

        Assert.Empty(dsp.Adds);          // guarded — never reaches the chain
        Assert.Equal(0, rigChanges());
    }

    [Fact]
    public void LoadIntoRig_LightsUp_WhenCatalogueGrowsNamamp()
    {
        var catalog = FakeDspChainService.DefaultCatalog()
            .Append(new DspModuleType(
                NamCaptureBrowserViewModel.NamampModuleTypeId, "NAM Amp", []))
            .ToList();
        var vm = NewVm(out _, out _, out var library, out var dsp, out var rigChanges, catalog);
        library.Entries.Add(new NamCaptureEntry(
            new NamCaptureManifest(41, 9, "Plexi", "a", "amp", "nam", "lite", "CC0", "", "",
                "9.nam", "00", 1, System.DateTimeOffset.UnixEpoch), "X:\\nam\\9.nam"));
        vm.ShowLibraryCommand.Execute(null);

        Assert.True(vm.CanLoadIntoRig);

        vm.LoadIntoRigCommand.Execute(vm.LibraryRows.Single());

        Assert.Equal([NamCaptureBrowserViewModel.NamampModuleTypeId], dsp.Adds);
        Assert.Equal(1, rigChanges());
    }

    [Fact]
    public void LoadIntoRig_PushesModelPath_AndAppendsBypassedEq_NoDuplicateOnReload()
    {
        var catalog = FakeDspChainService.DefaultCatalog()
            .Append(new DspModuleType(NamCaptureBrowserViewModel.NamampModuleTypeId, "NAM Amp", []))
            .Append(new DspModuleType("eq", "Parametric EQ",
                [new DspParamInfo("low", "Low", -12f, 12f, 0f)]))
            .ToList();
        var vm = NewVm(out _, out _, out var library, out var dsp, out _, catalog);
        library.Entries.Add(new NamCaptureEntry(
            new NamCaptureManifest(41, 9, "Plexi", "a", "amp", "nam", "lite", "CC0", "", "",
                "9.nam", "00", 1, System.DateTimeOffset.UnixEpoch), "X:\\nam\\9.nam"));
        vm.ShowLibraryCommand.Execute(null);

        vm.LoadIntoRigCommand.Execute(vm.LibraryRows.Single());

        var nodes = dsp.GetNodes();
        Assert.Equal(2, nodes.Count);
        Assert.Equal(NamCaptureBrowserViewModel.NamampModuleTypeId, nodes[0].ModuleTypeId);
        Assert.Equal("eq", nodes[1].ModuleTypeId);
        Assert.True(nodes[1].Bypassed);                                   // EQ starts bypassed
        // The capture file is bound to the amp (without this it plays silent).
        Assert.Equal(System.IO.Path.Combine("X:\\nam", "9.nam"),
            dsp.GetNodeString(nodes[0].NodeId, "modelPath"));

        // Reload the SAME capture: reuse the namamp, never a duplicate EQ.
        vm.LoadIntoRigCommand.Execute(vm.LibraryRows.Single());
        var after = dsp.GetNodes();
        Assert.Equal(2, after.Count);
        Assert.Equal(1, after.Count(n => n.ModuleTypeId == "eq"));
        Assert.Equal(1, after.Count(n => n.ModuleTypeId == NamCaptureBrowserViewModel.NamampModuleTypeId));
    }

    [Fact]
    public void SaveClientId_writes_through_configuration()
    {
        var config = new DotCharter.Tests.Infrastructure.FakeConfigurationService();
        var vm = new NamCaptureBrowserViewModel(
            new FakeTone3000Service(), new FakeTone3000AuthService { Configured = false },
            new FakeNamCaptureLibrary(), () => new FakeDspChainService(), FakeLocalization.English(), () => { },
            openUrl: _ => true, config: config)
        {
            ClientIdEntry = "  my-client-id  "
        };
        vm.SaveClientIdCommand.Execute(null);

        Assert.Equal("my-client-id", config.LoadAppConfig().Tone3000ClientId); // trimmed + persisted
    }

    // ── auth surface ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SignInAndOut_RefreshBoundState()
    {
        var vm = NewVm(out _, out var auth, out _, out _, out _);
        auth.User = "capturist";
        Assert.False(vm.IsSignedIn);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.True(vm.IsSignedIn);
        Assert.Contains("capturist", vm.SignedInLabel);
        Assert.Equal(1, auth.SignInCalls);

        vm.SignOutCommand.Execute(null);
        Assert.False(vm.IsSignedIn);
        Assert.Equal(1, auth.SignOutCalls);
    }

    [Fact]
    public void NotConfigured_SurfacesHintFlag()
    {
        var vm = NewVm(out _, out var auth, out _, out _, out _);
        auth.Configured = false;
        Assert.True(vm.IsNotConfigured);
        auth.Configured = true;
        vm.Activate();
        Assert.False(vm.IsNotConfigured);
    }

    // ── rail integration (PresetBrowserViewModel) ────────────────────────────

    [Fact]
    public void PresetBrowser_WithoutNamBrowser_HasNoNamRail()
    {
        var svc = new FakeDspTonePresetService();
        var browser = new DotCharter.Tone.UI.ViewModels.PlayMode.PresetBrowserViewModel(
            svc, FakeLocalization.English(), () => new DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigPreset([]), () => { });

        Assert.DoesNotContain(browser.Rail, r => r.Kind == PresetFilterKind.NamCaptures);
        Assert.Null(browser.NamBrowser);
    }

    [Fact]
    public void PresetBrowser_WithNamBrowser_AddsRail_AndSwapsPanels()
    {
        var vm = NewVm(out _, out _, out _, out _, out _);
        var svc = new FakeDspTonePresetService();
        var browser = new DotCharter.Tone.UI.ViewModels.PlayMode.PresetBrowserViewModel(
            svc, FakeLocalization.English(), () => new DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigPreset([]), () => { }, vm);

        var namRail = browser.Rail.Last();
        Assert.Equal(PresetFilterKind.NamCaptures, namRail.Kind);
        Assert.False(browser.IsNamCapturesSelected);

        browser.SelectedRail = namRail;

        Assert.True(browser.IsNamCapturesSelected);
        Assert.Empty(browser.Cards);          // the card grid yields to the panel
    }

    /// <summary>Static perf audit 2026-08-29 P1 #16d: the LIBRARY view refreshed
    /// per keystroke (the online view was already protected). With a post the
    /// typing run coalesces into ONE refresh.</summary>
    [Fact]
    public void LibraryTyping_CoalescesIntoOneRefresh_WhenThePostDrains()
    {
        var pending = new List<System.Action>();
        var vm = new NamCaptureBrowserViewModel(
            new FakeTone3000Service(), new FakeTone3000AuthService(), new FakeNamCaptureLibrary(),
            () => new FakeDspChainService(null), FakeLocalization.English(), () => { }, openUrl: _ => true, searchPost: pending.Add)
        {
            IsLibraryView = true
        };
        var passes = vm.SearchPass.PassCount;

        vm.SearchText = "p";
        vm.SearchText = "pl";
        vm.SearchText = "plexi";

        Assert.Equal(passes, vm.SearchPass.PassCount);
        Assert.Single(pending);
        pending[0]();
        Assert.Equal(passes + 1, vm.SearchPass.PassCount);
    }
}
