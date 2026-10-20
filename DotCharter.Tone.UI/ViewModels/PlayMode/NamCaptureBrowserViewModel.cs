using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// The "NAM Captures" section of the pedalboard's preset browser: a TONE3000
/// (tone3000.com) online search (title/author + gear/format/size/license chips,
/// per-row download with progress) and a "Library" view over the local
/// <see cref="INamCaptureLibrary"/> (delete, attribution, load-into-rig).
///
/// "Load into rig" is enabled ONLY when the live DSP chain's module catalogue
/// exposes a <c>namamp</c> module type — the native NAM engine lands with
/// Wave 3 (ABI v22), so today the button renders disabled with a tooltip and
/// lights up with zero UI changes once the catalogue grows the module.
///
/// Auth/library state is re-read after every awaited call and on
/// <see cref="Activate"/> instead of subscribing to service events — those fire
/// on worker threads and bound state must only change on the UI thread.
/// Unit-tested against fake services (<c>NamCaptureBrowserViewModelTests</c>).
/// </summary>
public sealed partial class NamCaptureBrowserViewModel : ObservableObject
{
    /// <summary>The native module type id the Wave-3 NAM engine will register.</summary>
    public const string NamampModuleTypeId = "namamp";

    private readonly ITone3000Service _service;
    private readonly ITone3000AuthService _auth;
    private readonly INamCaptureLibrary _library;

    /// <summary>
    ///     The chain "Load into rig" writes to, resolved PER CALL.
    ///     <para>
    ///         <b>Never a captured instance.</b> The rig surface that hosts this
    ///         browser can be pointed at any SEAT (ABI v28), and it does that
    ///         by REPOINTING its own <c>IDspChainService</c> — so a chain captured
    ///         when this view model was constructed is permanently seat 0. Loading
    ///         a capture while the board showed player 2 therefore dropped a neural
    ///         amp onto player 1's rig and rebuilt the board of the seat on screen,
    ///         which shows nothing: the wrong-guitar shape the preset browser (0.61)
    ///         and the compare strip (0.59) each shipped.
    ///     </para>
    /// </summary>
    private readonly Func<IDspChainService> _chain;

    private IDspChainService Dsp => _chain();
    private readonly Action _onRigChanged;
    private readonly Func<string, bool> _openUrl;
    private readonly IRigConfigService? _config;
    private readonly ILocalizationService _loc;

    public NamCaptureBrowserViewModel(
        ITone3000Service service,
        ITone3000AuthService auth,
        INamCaptureLibrary library,
        Func<IDspChainService> dsp,
        ILocalizationService localization,
        Action onRigChanged,
        Func<string, bool>? openUrl = null,
        IRigConfigService? config = null,
        Action<Action>? searchPost = null)
    {
        _service = service;
        _auth = auth;
        _library = library;
        _chain = dsp ?? throw new ArgumentNullException(nameof(dsp));
        _onRigChanged = onRigChanged;
        _openUrl = openUrl ?? (_ => false);
        _config = config;
        _loc = localization;
        // The library view filters live; typing coalesces into ONE refresh per
        // dispatcher turn (the CoalescedFilterPass rule).
        // Without a post (the tests) it runs inline.
        _searchPass = new Shell.CoalescedFilterPass(RefreshLibrary, searchPost ?? (action => action()));

        GearOptions = BuildGearOptions();
        _selectedGear = GearOptions[0];
        ArchitectureOptions = BuildArchitectureOptions();
        _selectedArchitecture = ArchitectureOptions[0];
        _clientIdEntry = _config?.LoadRigConfig().Tone3000ClientId ?? string.Empty;
        // NO RefreshLibrary() here: it reads every capture manifest off disk,
        // and this ctor runs while the Play Mode view POPULATES at the
        // DataContext bind (sampled at 20 ms of the boot's bind phase for a
        // panel nothing shows yet — the startup rule reaches every view model
        // the bind path constructs). The rows load when the panel is reached:
        // selecting the NAM rail calls Activate(), and the ctor-time rail
        // selection is always Rail[0], never NAM captures.
    }

    // ---- filters / options ------------------------------------------------------

    /// <summary>Gear-type filter options (All + the seven TONE3000 gear types).</summary>
    public IReadOnlyList<NamGearOption> GearOptions { get; }

    [ObservableProperty] private NamGearOption? _selectedGear;

    /// <summary>
    ///     Architecture filter options. TONE3000 has NO "all architectures"
    ///     value: omitting the parameter returns its legacy A1 + Custom set and
    ///     EXCLUDES A2-only tones, so A2 captures are only reachable by asking
    ///     for them explicitly. The default is therefore the server default
    ///     (the catalogue's bulk, and what every pre-A2 client sees) with A2 one
    ///     click away rather than invisible.
    /// </summary>
    public IReadOnlyList<NamArchitectureOption> ArchitectureOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchitectureHint))]
    private NamArchitectureOption? _selectedArchitecture;

    /// <summary>Explains what the current architecture selection excludes —
    /// the whole reason A2 captures looked absent.</summary>
    public string ArchitectureHint =>
        (SelectedArchitecture?.Value ?? Tone3000Architecture.Default) switch
        {
            Tone3000Architecture.A2 => _loc["Nam_ArchHintA2"],
            Tone3000Architecture.A1 => _loc["Nam_ArchHintA1"],
            Tone3000Architecture.Custom => _loc["Nam_ArchHintCustom"],
            _ => _loc["Nam_ArchHintDefault"]
        };

    [ObservableProperty] private string _searchText = string.Empty;

    private readonly Shell.CoalescedFilterPass _searchPass;

    /// <summary>The typing funnel — read by the test that pins the coalescing.</summary>
    internal Shell.CoalescedFilterPass SearchPass => _searchPass;

    partial void OnSearchTextChanged(string value)
    {
        // The library view filters live (pure NamCaptureIndex); the online view
        // only re-queries on an explicit Search so typing never spams the API.
        if (IsLibraryView) _searchPass.Request();
    }

    partial void OnSelectedGearChanged(NamGearOption? value)
    {
        if (IsLibraryView) RefreshLibrary();
    }

    // ---- view state ---------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnlineView))]
    [NotifyPropertyChangedFor(nameof(ShowSignInCta))]
    [NotifyPropertyChangedFor(nameof(ShowOnlineEmpty))]
    private bool _isLibraryView;

    public bool IsOnlineView => !IsLibraryView;

    /// <summary>True when a query has been entered/attempted and is waiting for
    /// a sign-in to complete — set the search re-runs itself once signed in.</summary>
    private bool _searchPendingSignIn;

    [RelayCommand]
    private void ShowOnline() => IsLibraryView = false;

    [RelayCommand]
    private void ShowLibrary()
    {
        IsLibraryView = true;
        RefreshLibrary();
    }

    /// <summary>
    ///     A network search is running. It existed and was bound by NOTHING —
    ///     no spinner, no disabled primary, no <c>CanExecute</c> — so on a slow
    ///     link the Search button looked dead and every further click was a
    ///     silent no-op behind <c>if (IsSearching) return;</c>.
    /// </summary>
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [ObservableProperty] private bool _isSearching;

    /// <summary>
    ///     No run in flight. Deliberately NOT also "signed in": pressing Search
    ///     while signed out is a designed step — it remembers the query, raises
    ///     the sign-in call-to-action and re-runs itself afterwards — so the
    ///     button stays live and the state prints its own reason instead.
    /// </summary>
    public bool CanSearch => !IsSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>Refreshes everything view-external (auth flags, library rows,
    /// namamp availability). Called by the browser when this section opens.</summary>
    public void Activate()
    {
        // Re-seed the inline client-id box from config (it may have been set in
        // Settings meanwhile — signing in there also lights this panel up).
        if (_config != null) ClientIdEntry = _config.LoadRigConfig().Tone3000ClientId;
        RefreshAuthState();
        RefreshLibrary();
        OnPropertyChanged(nameof(CanLoadIntoRig));
        OnPropertyChanged(nameof(LoadIntoRigTooltip));
    }

    // ---- auth ---------------------------------------------------------------------

    /// <summary>False until the owner mints a client id at tone3000.com/settings
    /// and sets <c>AppConfig.Tone3000ClientId</c> — the UI shows a hint then.
    /// Search still works (public API); sign-in and downloads need the id.</summary>
    public bool IsConfigured => _auth.IsConfigured;

    public bool IsNotConfigured => !IsConfigured;

    public bool IsSignedIn => _auth.IsSignedIn;

    public bool IsSignedOut => !IsSignedIn;

    /// <summary>Show the friendly sign-in call-to-action on the Online view.
    /// Distinct from the not-configured hint: only when a client id IS set but
    /// no session exists (search now requires a TONE3000 sign-in — the live API
    /// rejects anonymous requests). Not configured ⇒ the configure hint wins.</summary>
    public bool ShowSignInCta => IsOnlineView && IsConfigured && IsSignedOut;

    /// <summary>Show the "search for captures" placeholder only when signed in
    /// and nothing has been searched yet — never behind the sign-in CTA, and
    /// never after a search RAN. A search that matched nothing or failed would
    /// leave this invitation on screen, which reads as "your click did nothing";
    /// the status line says which of the two it was.</summary>
    public bool ShowOnlineEmpty => IsOnlineView && !HasResults && !ShowSignInCta && !_hasSearched;

    /// <summary>True once a search actually reached the service and came back
    /// (either way). Distinguishes "not searched yet" from "searched, nothing".</summary>
    private bool _hasSearched;

    public string SignedInLabel => IsSignedIn
        ? _loc.Format("Nam_SignedInAs",
            _auth.UserName.Length > 0 ? _auth.UserName : "TONE3000")
        : string.Empty;

    private void RefreshAuthState()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(IsNotConfigured));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(SignedInLabel));
        OnPropertyChanged(nameof(ShowSignInCta));
        OnPropertyChanged(nameof(ShowOnlineEmpty));
        OnPropertyChanged(nameof(CanSearch));
        SearchCommand.NotifyCanExecuteChanged();
    }

    // ---- inline client-id setup (the "not configured" wall) -----------------------

    /// <summary>The client-id typed into the inline setup box shown in the
    /// not-configured state, so the owner can configure TONE3000 right where they
    /// hit the wall instead of hand-editing appconfig.json.</summary>
    [ObservableProperty] private string _clientIdEntry = string.Empty;

    /// <summary>Persists <see cref="ClientIdEntry"/> to <c>Tone3000ClientId</c>
    /// through <see cref="IRigConfigService"/> (whole-config save, only when
    /// changed) and refreshes the configured/CTA state so the panel switches from
    /// the hint to the sign-in call-to-action without a reopen.</summary>
    [RelayCommand]
    private void SaveClientId()
    {
        if (_config == null) return;
        var id = (ClientIdEntry ?? string.Empty).Trim();
        if (_config.LoadRigConfig().Tone3000ClientId != id)
            _config.UpdateRigConfig(c => c.Tone3000ClientId = id);
        RefreshAuthState();
        // If a client id is now set, nudge the user toward the sign-in CTA.
        if (IsConfigured && IsSignedOut)
            StatusMessage = _loc["Nam_SignInRequired"];
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        StatusMessage = string.Empty;
        var result = await _auth.SignInAsync();
        if (!result.Ok)
        {
            StatusMessage = result.Message.Length > 0
                ? result.Message
                : _loc["Nam_SignInFailed"];
        }

        RefreshAuthState();

        // Search now requires a session — if a query was waiting on sign-in,
        // run it automatically now that we're in.
        if (result.Ok && _searchPendingSignIn)
        {
            _searchPendingSignIn = false;
            await SearchAsync();
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        _auth.SignOut();
        // Drop the signed-in session's search results: their Download buttons would
        // now all fail the IsSignedIn guard, and leaving them listed hides the
        // sign-in CTA (ShowOnlineEmpty is suppressed while HasResults).
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        _searchPendingSignIn = false;
        _hasSearched = false;
        StatusMessage = string.Empty;
        RefreshAuthState();
    }

    // ---- online search --------------------------------------------------------------

    public ObservableCollection<NamToneRowViewModel> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    public async Task SearchAsync()
    {
        if (IsSearching) return;

        // The live TONE3000 API rejects anonymous search (401). Rather than
        // fire a request doomed to fail, surface the sign-in call-to-action and
        // remember the query so it re-runs itself once the user signs in.
        if (IsSignedOut)
        {
            // …and SAY so. This branch blanked the status, so when the panel is
            // not configured (no CTA on screen either) the click produced
            // literally no pixel change — the one shape this whole file's
            // comments say was designed out of every other branch.
            _searchPendingSignIn = true;
            StatusMessage = _loc[
                IsConfigured ? "Nam_SignInRequired" : "Nam_NotConfigured"];
            RefreshAuthState();
            return;
        }

        IsSearching = true;
        StatusMessage = string.Empty;
        try
        {
            var gear = SelectedGear?.Value ?? Tone3000GearType.Any;
            var architecture = SelectedArchitecture?.Value ?? Tone3000Architecture.Default;
            var result = await _service.SearchAsync(
                new Tone3000SearchQuery(SearchText, gear, Architecture: architecture));

            Results.Clear();
            _hasSearched = true;
            if (result.Ok)
            {
                var page = result.Value!;
                // ONE library scan for the whole page. IsToneDownloaded re-reads
                // the capture directory and deserializes EVERY manifest sidecar,
                // so asking it per row was a page-size × library-size fan-out of
                // synchronous file I/O — 25 rows over the 63 bundled captures is
                // ~1,600 reads — and it ran on the UI thread in this await's
                // continuation, between the click and the results appearing.
                var downloaded = DownloadedToneIds();
                foreach (var tone in page.Tones)
                    Results.Add(new NamToneRowViewModel(tone, downloaded.Contains(tone.Id), _loc));

                // A search that RAN says so, with a count. Silence on success
                // is indistinguishable from a click that did nothing, which is
                // how a completely broken search went unnoticed.
                StatusMessage = Results.Count == 0
                    ? _loc["Nam_NoResults"]
                    : page.TotalCount > Results.Count
                        ? _loc.Format("Nam_SearchResultCountOfTotal", Results.Count, page.TotalCount)
                        : _loc.Plural("Nam_SearchResultCountOne", "Nam_SearchResultCount", Results.Count);
            }
            else if (result.Error == OnlineError.NotSignedIn)
            {
                // The session stopped working server-side — revoked, or simply
                // rejected. NOTHING dropped it: the refresh path only signs out
                // when the PROVIDER rejects a refresh, and a token whose local
                // expiry is still in the future is never refreshed at all. So
                // IsSignedIn stayed true, the header went on saying "Signed in
                // as X", ShowSignInCta stayed false — and because this branch
                // also cleared the results and left the status blank, the user
                // got the never-searched-yet invitation back. The caller that
                // SAW the 401 is the only one that knows.
                _auth.InvalidateSession("a request came back unauthorized");
                _searchPendingSignIn = true;
                _hasSearched = false;
                StatusMessage = _loc["Nam_SessionExpired"];
            }
            else
            {
                // A FAILURE reads as a failure. A bare server string ("dns
                // down") on its own looks like a result, not an error.
                StatusMessage = result.Message.Length > 0
                    ? _loc.Format("Nam_SearchFailedDetail", result.Message)
                    : _loc["Nam_SearchFailed"];
            }

            OnPropertyChanged(nameof(HasResults));
            RefreshAuthState();
        }
        finally
        {
            IsSearching = false;
        }
    }

    // ---- download -----------------------------------------------------------------

    [RelayCommand]
    public async Task DownloadAsync(NamToneRowViewModel? row)
    {
        if (row == null || row.IsDownloading || row.IsDownloaded) return;
        if (!IsSignedIn)
        {
            StatusMessage = _loc[
                IsConfigured ? "Nam_SignInRequired" : "Nam_NotConfigured"];
            return;
        }

        row.IsDownloading = true;
        row.Progress = 0;
        StatusMessage = string.Empty;
        try
        {
            var models = await _service.GetModelsAsync(row.Tone.Id);
            if (models.Error == OnlineError.NotSignedIn)
            {
                // Same fault as the search path: a 401 here left the header
                // claiming a live session while nothing would download.
                _auth.InvalidateSession("a download request came back unauthorized");
                RefreshAuthState();
                StatusMessage = _loc["Nam_SessionExpired"];
                return;
            }

            if (!models.Ok || models.Value!.Count == 0)
            {
                StatusMessage = _loc["Nam_DownloadFailed"];
                return;
            }

            // The models endpoint lists EVERY architecture of the tone, so a
            // search filtered to A2 would otherwise hand back the A1 file.
            var model = PickModel(models.Value, SelectedArchitecture?.Value ?? Tone3000Architecture.Default);
            var progress = new Progress<double>(f => row.Progress = f);
            var result = await _library.DownloadAsync(row.Tone, model, progress);
            if (result.Ok)
            {
                row.IsDownloaded = true;
                RefreshLibrary();
            }
            else
            {
                StatusMessage = result.Message.Length > 0
                    ? result.Message
                    : _loc["Nam_DownloadFailed"];
            }
        }
        finally
        {
            row.IsDownloading = false;
        }
    }

    /// <summary>Which of a tone's models to download — the shared rule, see
    /// <see cref="Tone3000ModelPicker.Pick" />.</summary>
    internal static Tone3000Model PickModel(
        IReadOnlyList<Tone3000Model> models,
        Tone3000Architecture architecture = Tone3000Architecture.Default)
        => Tone3000ModelPicker.Pick(models, architecture);

    // ---- library ------------------------------------------------------------------

    public ObservableCollection<NamLibraryRowViewModel> LibraryRows { get; } = [];

    public bool HasLibraryRows => LibraryRows.Count > 0;

    /// <summary>Rebuilds the library rows from disk, filtered by the shared
    /// search text + gear filter through the pure <see cref="NamCaptureIndex"/>.</summary>
    public void RefreshLibrary()
    {
        var entries = _library.GetAll();
        var manifests = NamCaptureIndex.Filter(
            entries.Select(e => e.Manifest).ToList(),
            IsLibraryView ? SearchText : null,
            Tone3000Wire.GearTypeValue(SelectedGear?.Value ?? Tone3000GearType.Any));

        LibraryRows.Clear();
        foreach (var manifest in manifests)
            LibraryRows.Add(new NamLibraryRowViewModel(manifest, _loc));
        OnPropertyChanged(nameof(HasLibraryRows));
    }

    [RelayCommand]
    private void Delete(NamLibraryRowViewModel? row)
    {
        if (row == null) return;
        _library.Delete(row.Manifest.ModelId);
        RefreshLibrary();
        // One scan, not one per matching result row — see SearchAsync.
        var downloaded = DownloadedToneIds();
        foreach (var result in Results)
        {
            if (result.Tone.Id == row.Manifest.ToneId)
                result.IsDownloaded = downloaded.Contains(result.Tone.Id);
        }
    }

    /// <summary>Every tone id the local library holds, from ONE directory scan.
    /// <see cref="INamCaptureLibrary.IsToneDownloaded" /> re-reads the whole
    /// capture directory per call, so it must never be asked inside a loop over
    /// rows. Throw-free: a broken library reads as "nothing downloaded", which is
    /// what an unreadable library honestly is.</summary>
    private HashSet<long> DownloadedToneIds()
    {
        try { return [.. _library.GetAll().Select(e => e.Manifest.ToneId)]; }
        catch { return []; }
    }

    /// <summary>The TONE3000 home page the "Powered by TONE3000" credit links to
    /// (the API terms' branding and attribution clause).</summary>
    public const string Tone3000Home = "https://www.tone3000.com";

    public string Tone3000Url => Tone3000Home;

    [RelayCommand]
    private void OpenTone3000() => _openUrl(Tone3000Home);

    [RelayCommand]
    private void OpenAttribution(NamLibraryRowViewModel? row)
    {
        if (row?.Manifest.AttributionUrl is { Length: > 0 } url) _openUrl(url);
    }

    // ---- load into rig (dormant until Wave 3 / ABI v22) -----------------------------

    /// <summary>True only when the native catalogue exposes the
    /// <c>namamp</c> module type (queried at runtime — Wave 3 lights this up
    /// with zero UI changes).</summary>
    public bool CanLoadIntoRig => Dsp.Catalog.Any(m => m.Id == NamampModuleTypeId);

    public string LoadIntoRigTooltip => CanLoadIntoRig
        ? _loc["Nam_LoadIntoRig"]
        : _loc["Nam_LoadDisabledHint"];

    /// <summary>The namamp string param that binds a capture file to the node.</summary>
    private const string ModelPathParam = "modelPath";
    /// <summary>Parametric EQ auto-appended after a loaded capture (bypassed).</summary>
    private const string EqModuleTypeId = "eq";

    [RelayCommand]
    private void LoadIntoRig(NamLibraryRowViewModel? row)
    {
        if (row == null || !CanLoadIntoRig) return;

        // Reuse an existing namamp node if the chain has one (swap the capture)
        // rather than stacking a second neural amp; only add when none exists.
        var existing = Dsp.GetNodes().FirstOrDefault(n => n.ModuleTypeId == NamampModuleTypeId);
        int nodeId;
        var added = false;
        if (existing != null)
        {
            nodeId = existing.NodeId;
        }
        else
        {
            var node = Dsp.AddNode(NamampModuleTypeId);
            if (node == null)
            {
                StatusMessage = _loc["Nam_LoadFailed"];
                return;
            }
            nodeId = node.NodeId;
            added = true;
        }

        // Bind the capture file to the node via the ABI-v22 string-param channel.
        // Without this the namamp node has no model loaded and plays silently —
        // the whole point of "Load into rig".
        var filePath = System.IO.Path.Combine(_library.RootDirectory, row.Manifest.FileName);
        if (!Dsp.SetNodeString(nodeId, ModelPathParam, filePath))
        {
            StatusMessage = _loc["Nam_LoadFailed"];
            if (added) Dsp.RemoveNode(nodeId); // don't leave a modelless amp behind
            return;
        }

        // Auto-append a Parametric EQ directly after the amp, BYPASSED — captures
        // bake in their amp settings and expose only input/output trims, so a
        // ready-to-grab tone stack is handy. Only when the module exists and there
        // isn't already an eq right after this namamp (so a reload never dupes it).
        EnsureBypassedEqAfterNamamp(nodeId);

        _onRigChanged();
        StatusMessage = _loc.Format("Nam_LoadedIntoRig", row.Title);
    }

    private void EnsureBypassedEqAfterNamamp(int namampNodeId)
    {
        if (!Dsp.Catalog.Any(m => m.Id == EqModuleTypeId)) return;

        var nodes = Dsp.GetNodes();
        var idx = IndexOfNode(nodes, namampNodeId);
        if (idx < 0) return;
        if (idx + 1 < nodes.Count && nodes[idx + 1].ModuleTypeId == EqModuleTypeId)
            return; // already has an eq immediately after — don't add a second

        var eq = Dsp.AddNode(EqModuleTypeId); // appended at the chain tail
        if (eq == null) return;
        Dsp.SetBypass(eq.NodeId, true); // catalogue defaults, bypassed

        // Move it to sit directly after the namamp (a no-op when namamp was at
        // the tail, i.e. the eq is already right behind it).
        var after = Dsp.GetNodes();
        var namIdx = IndexOfNode(after, namampNodeId);
        if (namIdx >= 0 && (namIdx + 1 >= after.Count || after[namIdx + 1].NodeId != eq.NodeId))
            Dsp.MoveNode(eq.NodeId, namIdx + 1);
    }

    private static int IndexOfNode(IReadOnlyList<DspNode> nodes, int nodeId)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (nodes[i].NodeId == nodeId) return i;
        return -1;
    }

    // ---- helpers ------------------------------------------------------------------

    private IReadOnlyList<NamGearOption> BuildGearOptions()
    {
        return
        [
            new NamGearOption(Tone3000GearType.Any, _loc["Nam_GearAll"]),
            new NamGearOption(Tone3000GearType.Amp, _loc["Nam_GearAmp"]),
            new NamGearOption(Tone3000GearType.AmpCab, _loc["Nam_GearAmpCab"]),
            new NamGearOption(Tone3000GearType.Pedal, _loc["Nam_GearPedal"]),
            new NamGearOption(Tone3000GearType.Outboard, _loc["Nam_GearOutboard"]),
            new NamGearOption(Tone3000GearType.Cab, _loc["Nam_GearCab"]),
            new NamGearOption(Tone3000GearType.Space, _loc["Nam_GearSpace"]),
            new NamGearOption(Tone3000GearType.Experimental, _loc["Nam_GearExperimental"])
        ];
    }

    private IReadOnlyList<NamArchitectureOption> BuildArchitectureOptions()
    {
        return
        [
            new NamArchitectureOption(Tone3000Architecture.Default, _loc["Nam_ArchDefault"]),
            new NamArchitectureOption(Tone3000Architecture.A2, _loc["Nam_ArchA2"]),
            new NamArchitectureOption(Tone3000Architecture.A1, _loc["Nam_ArchA1"]),
            new NamArchitectureOption(Tone3000Architecture.Custom, _loc["Nam_ArchCustom"])
        ];
    }

    /// <summary>Localized label for a wire gear-type string (unknown values
    /// pass through raw — a lenient client survives new server-side types).</summary>
    public static string GearLabel(ILocalizationService localization, string gearType) => gearType switch
    {
        "amp" => localization["Nam_GearAmp"],
        "amp-cab" => localization["Nam_GearAmpCab"],
        "pedal" => localization["Nam_GearPedal"],
        "outboard" => localization["Nam_GearOutboard"],
        "cab" => localization["Nam_GearCab"],
        "space" => localization["Nam_GearSpace"],
        "experimental" => localization["Nam_GearExperimental"],
        _ => gearType
    };

}

/// <summary>An entry in the gear-type filter dropdown (enum value + label).</summary>
public sealed record NamGearOption(Tone3000GearType Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An entry in the architecture filter dropdown (enum value + label).</summary>
public sealed record NamArchitectureOption(Tone3000Architecture Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One online search result row: tone metadata + chips + the
/// download button's progress/downloaded state.</summary>
public sealed partial class NamToneRowViewModel : ObservableObject
{
    public NamToneRowViewModel(Tone3000Tone tone, bool isDownloaded, ILocalizationService localization)
    {
        Tone = tone;
        _isDownloaded = isDownloaded;
        GearLabel = tone.GearType.Length > 0
            ? NamCaptureBrowserViewModel.GearLabel(localization, tone.GearType)
            : string.Empty;
        // Which architectures this tone actually ships, so "why can't I see the
        // A2 ones" is answerable by looking at the results. Absent counts (an
        // older/partial payload) show nothing rather than a wrong claim.
        ArchitectureLabel = string.Join('/', new[]
        {
            tone.A2ModelCount > 0 ? localization["Nam_ArchA2"] : string.Empty,
            tone.A1ModelCount > 0 ? localization["Nam_ArchA1"] : string.Empty,
            tone.CustomModelCount > 0 ? localization["Nam_ArchCustom"] : string.Empty
        }.Where(s => s.Length > 0));
        FormatSizeLabel = string.Join(" · ",
            new[]
            {
                string.Join("/", tone.Formats.Select(f => f.ToUpperInvariant())),
                string.Join("/", tone.Sizes)
            }.Where(s => s.Length > 0));
    }

    public Tone3000Tone Tone { get; }
    public string Title => Tone.Title;
    public string Author => Tone.Author;
    public string GearLabel { get; }
    public string ArchitectureLabel { get; }
    public string FormatSizeLabel { get; }
    public string LicenseLabel => Tone.License;

    public bool HasGear => GearLabel.Length > 0;
    public bool HasArchitecture => ArchitectureLabel.Length > 0;
    public bool HasFormatSize => FormatSizeLabel.Length > 0;
    public bool HasLicense => LicenseLabel.Length > 0;
    public bool HasAuthor => Author.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloaded;

    [ObservableProperty] private double _progress;

    public bool CanDownload => !IsDownloading && !IsDownloaded;
}

/// <summary>One local library row: the stored manifest (title/author/chips/
/// license/attribution) — actions live on the parent VM.</summary>
public sealed class NamLibraryRowViewModel
{
    public NamLibraryRowViewModel(NamCaptureManifest manifest, ILocalizationService localization)
    {
        Manifest = manifest;
        GearLabel = manifest.GearType.Length > 0
            ? NamCaptureBrowserViewModel.GearLabel(localization, manifest.GearType)
            : string.Empty;
        FormatSizeLabel = string.Join(" · ",
            new[] { manifest.Format.ToUpperInvariant(), manifest.SizeTier }
                .Where(s => s.Length > 0));
    }

    public NamCaptureManifest Manifest { get; }
    public string Title => Manifest.Title;
    public string Author => Manifest.Author;
    public string GearLabel { get; }
    public string FormatSizeLabel { get; }
    public string LicenseLabel => Manifest.License;

    public bool HasGear => GearLabel.Length > 0;
    public bool HasFormatSize => FormatSizeLabel.Length > 0;
    public bool HasLicense => LicenseLabel.Length > 0;
    public bool HasAuthor => Author.Length > 0;
    public bool HasAttribution => Manifest.AttributionUrl.Length > 0;
}
