using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.RigLab;

/// <summary>One row of the provider picker, with whether this machine can offer it.</summary>
public sealed record RigLabAiProvider(AiProviderKind Kind, string Label, bool IsAvailable)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One level of the Reasoning picker: the same levels and resx keys as
/// DotCharter's Settings ▸ AI row (<see cref="AiReasoningText" />).</summary>
public sealed record RigLabAiEffortOption(AiReasoningEffort Effort, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
///     RigLab's AI tab: which model answers "Design a tone" in the rig's Tones
///     mode, and the TONE3000 account a design downloads amp captures through.
///
///     <para>
///         <b>Every change applies at once</b> — there is no Save button on this
///         tab, as on the Audio tab. The provider, model id and address/path go to
///         <c>riglab.json</c> (<see cref="RigConfig.Ai" />) and the design service
///         is told, so an open panel picks the provider up on its next request. The
///         API key never goes to the file: <see cref="IAiSecretStore" /> seals it
///         (DPAPI), and the box is emptied once it is stored.
///     </para>
///     <para>
///         <b>The two CLI providers are opt-in and run under the user's own login.</b>
///         Nothing here reads a token or passes a key to them, and the note under
///         each says whose usage a design spends. Neither is ever a default.
///     </para>
/// </summary>
public sealed partial class AiSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>The model id Claude Code is given when it is picked with none chosen yet.</summary>
    public const string DefaultClaudeModel = ClaudeCliToneModel.DefaultModelId;

    /// <summary>OpenAI's own API, as an OpenAI-compatible base URL.</summary>
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";

    private readonly RigLabConfigService _config;
    private readonly IAiSecretStore _secrets;
    private readonly IToneDesignModelFactory _factory;
    private readonly IToneDesignService _design;
    private readonly ITone3000AuthService _tone3000;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger? _logger;
    private readonly ILocalizationService _loc;
    private CancellationTokenSource? _signIn;
    private bool _loading;

    /// <summary>Creates the tab over RigLab's own services.</summary>
    /// <param name="config">riglab.json.</param>
    /// <param name="secrets">Where API keys are sealed.</param>
    /// <param name="factory">Mints a provider; also says which kinds this machine can offer.</param>
    /// <param name="design">Told when the provider changes.</param>
    /// <param name="tone3000">The TONE3000 account designs download captures through.</param>
    /// <param name="ui">Marshals sign-in state changes onto the UI thread.</param>
    /// <param name="localization">Resolves the text the screen shows.</param>
    /// <param name="logger">Optional app logger.</param>
    public AiSettingsViewModel(
        RigLabConfigService config,
        IAiSecretStore secrets,
        IToneDesignModelFactory factory,
        IToneDesignService design,
        ITone3000AuthService tone3000,
        IUiDispatcher ui,
        ILocalizationService localization,
        IAppLogger? logger = null)
    {
        _config = config;
        _secrets = secrets;
        _factory = factory;
        _design = design;
        _tone3000 = tone3000;
        _ui = ui;
        _logger = logger;
        _loc = localization;

        // Every row but None is enabled by the FACTORY, never by a list kept here:
        // a second copy of what this machine can offer is what goes stale.
        Providers =
        [
            new RigLabAiProvider(AiProviderKind.None, _loc["Ai_Provider_None"], true),
            new RigLabAiProvider(AiProviderKind.Claude, _loc["Ai_Provider_Claude"], factory.Supports(AiProviderKind.Claude)),
            new RigLabAiProvider(AiProviderKind.OpenAiCompatible, _loc["Ai_Provider_OpenAiCompatible"],
                factory.Supports(AiProviderKind.OpenAiCompatible)),
            new RigLabAiProvider(AiProviderKind.ClaudeCli, _loc["Ai_Provider_ClaudeCli"], factory.Supports(AiProviderKind.ClaudeCli)),
            new RigLabAiProvider(AiProviderKind.CodexCli, _loc["Ai_Provider_CodexCli"], factory.Supports(AiProviderKind.CodexCli))
        ];
        _selectedProvider = Providers[0];
        EffortLevels = [.. AiReasoningText.Levels.Select(e => new RigLabAiEffortOption(e, _loc[AiReasoningText.Key(e)]))];
        _selectedEffort = EffortLevels[0];

        _tone3000.AuthStateChanged += OnAuthStateChanged;
        Load();
    }

    /// <summary>The picker's rows.</summary>
    public IReadOnlyList<RigLabAiProvider> Providers { get; }

    [ObservableProperty] private RigLabAiProvider _selectedProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelRowVisible), nameof(IsServerRowVisible), nameof(IsCliPathRowVisible),
        nameof(IsApiKeyRowVisible), nameof(IsClaudeCliNoteVisible), nameof(IsCodexCliNoteVisible),
        nameof(AreClaudePresetsVisible), nameof(AreCodexPresetsVisible), nameof(IsTestRowVisible), nameof(CliPathLabel), nameof(CliPathDesc),
        nameof(ModelDesc), nameof(IsWebResearchRowVisible), nameof(IsEffortRowVisible), nameof(IsEffortSupported),
        nameof(EffortDesc))]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(SaveKeyCommand), nameof(ClearKeyCommand))]
    private AiProviderKind _providerKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEffortSupported), nameof(EffortDesc))]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _modelId = "";

    /// <summary>The Reasoning picker's levels: Default / Low / Medium / High.</summary>
    public IReadOnlyList<RigLabAiEffortOption> EffortLevels { get; }

    /// <summary>How hard the model reasons before it designs (<see cref="AiProviderSettings.Effort" />).
    /// Kept when the provider or model changes, so going back restores it; what is SENT
    /// is decided per request by <see cref="AiReasoning.WireValue" />.</summary>
    [ObservableProperty] private RigLabAiEffortOption _selectedEffort;

    /// <summary>The Reasoning row, for any provider — hidden with None, which sends nothing,
    /// as DotCharter's Settings ▸ AI row is.</summary>
    public bool IsEffortRowVisible => ProviderKind != AiProviderKind.None;

    /// <summary>False for a model that cannot take a level (a Claude model older than the
    /// effort parameter): the picker is then disabled and <see cref="EffortDesc" /> says why.</summary>
    public bool IsEffortSupported => AiReasoningText.Support(ProviderKind, ModelId) == AiReasoningUnsupported.None;

    /// <summary>What the level does, or why this provider and model cannot take one.</summary>
    public string EffortDesc => _loc[AiReasoningText.DescriptionKey(ProviderKind, ModelId)];

    /// <summary>The server address for an OpenAI-compatible server, or the optional
    /// explicit binary path for a CLI — the one settings slot both reuse.</summary>
    [ObservableProperty] private string _baseUrl = "";

    /// <summary>May the CLI look a named song's recorded rig up on the web first
    /// (<see cref="AiProviderSettings.WebResearch" />). On by default.</summary>
    [ObservableProperty] private bool _webResearch = true;

    /// <summary>The research row, for the two CLIs — the only providers with web tools.</summary>
    public bool IsWebResearchRowVisible => ProviderKind is AiProviderKind.ClaudeCli or AiProviderKind.CodexCli;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    private string _apiKeyEntry = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyStateText))]
    [NotifyCanExecuteChangedFor(nameof(ClearKeyCommand))]
    private bool _hasStoredKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    [ObservableProperty] private bool _isStatusError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isBusy;

    /// <summary>True when there is a status line to show.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Every provider has a model id; the CLIs' is optional.</summary>
    public bool IsModelRowVisible => ProviderKind != AiProviderKind.None;

    /// <summary>The server address row, for an OpenAI-compatible server only.</summary>
    public bool IsServerRowVisible => ProviderKind == AiProviderKind.OpenAiCompatible;

    /// <summary>The optional binary path, for the two CLIs.</summary>
    public bool IsCliPathRowVisible => ProviderKind is AiProviderKind.ClaudeCli or AiProviderKind.CodexCli;

    /// <summary>The key row. HIDDEN for the CLIs: they take no key — they answer
    /// under the user's own login — and a key box they would ignore invites a
    /// secret into a field that does nothing with it.</summary>
    public bool IsApiKeyRowVisible => ProviderKind is AiProviderKind.Claude or AiProviderKind.OpenAiCompatible;

    /// <summary>Whose usage a Claude Code design spends.</summary>
    public bool IsClaudeCliNoteVisible => ProviderKind == AiProviderKind.ClaudeCli;

    /// <summary>Whose usage a Codex design spends.</summary>
    public bool IsCodexCliNoteVisible => ProviderKind == AiProviderKind.CodexCli;

    /// <summary>"Use Opus" / "Use Sonnet", for the two Claude providers.</summary>
    public bool AreClaudePresetsVisible => ProviderKind is AiProviderKind.Claude or AiProviderKind.ClaudeCli;

    /// <summary>"Use GPT-6 Sol" / "Use GPT-6 Luna", for the Codex CLI only. An empty
    /// model box still means Codex's own default.</summary>
    public bool AreCodexPresetsVisible => ProviderKind == AiProviderKind.CodexCli;

    /// <summary>The Test row, for any provider.</summary>
    public bool IsTestRowVisible => ProviderKind != AiProviderKind.None;

    /// <summary>"Claude Code path" or "Codex path".</summary>
    public string CliPathLabel => _loc[ProviderKind == AiProviderKind.CodexCli ? "Ai_CodexPathLabel" : "Ai_CliPathLabel"];

    /// <summary>What the path row does when left empty, for the CLI in force.</summary>
    public string CliPathDesc => _loc[ProviderKind == AiProviderKind.CodexCli ? "Ai_CodexPathDesc" : "Ai_CliPathDesc"];

    /// <summary>The model row's line: optional for Codex, required elsewhere.</summary>
    public string ModelDesc => _loc[ProviderKind == AiProviderKind.CodexCli ? "Ai_CodexModelDesc" : "Ai_ModelDesc"];

    /// <summary>"Key stored" / "No key stored" — presence only, never the value.</summary>
    public string KeyStateText => _loc[HasStoredKey ? "Ai_KeyStored" : "Ai_KeyMissing"];

    /// <summary>True where the store cannot SEAL the key, so it lives for the session only.</summary>
    public bool IsKeySessionOnly => !_secrets.IsPersistent;

    /// <summary>The settings as the factory wants them — trimmed, an empty address as null.</summary>
    public AiProviderSettings CurrentSettings => new(
        ProviderKind, ModelId.Trim(), string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.Trim(),
        WebResearch: WebResearch, Effort: SelectedEffort.Effort);

    // ---- TONE3000 ---------------------------------------------------------------

    /// <summary>The OAuth app's client id (riglab.json). TONE3000 refuses every
    /// request without a signed-in account, and signing in needs this.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _tone3000ClientId = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tone3000AccountText), nameof(CaptureSearchText))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(SignOutCommand))]
    private bool _isTone3000SignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(CancelSignInCommand))]
    private bool _isSigningIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTone3000Status))]
    private string _tone3000Status = "";

    /// <summary>True when there is a TONE3000 status line to show.</summary>
    public bool HasTone3000Status => Tone3000Status.Length > 0;

    /// <summary>The address to register in the TONE3000 app.</summary>
    public string Tone3000RedirectUri => Tone3000OAuth.RedirectUri;

    /// <summary>"Signed in as …" or the hint to sign in.</summary>
    public string Tone3000AccountText => IsTone3000SignedIn
        ? _loc.Format("Tone3000_SignedInAs", _tone3000.UserName)
        : _loc["Tone3000_AccountHint"];

    /// <summary>What a design can do about captures right now.</summary>
    public string CaptureSearchText =>
        _loc[IsTone3000SignedIn ? "RigLab_Ai_CaptureSearch_On" : "RigLab_Ai_CaptureSearch_Off"];

    /// <summary>Re-reads riglab.json and the key's PRESENCE, never its value.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var config = _config.Load();
            var settings = config.Ai ?? AiProviderSettings.Disabled;
            ProviderKind = settings.Kind;
            ModelId = settings.ModelId ?? "";
            BaseUrl = settings.BaseUrl ?? "";
            WebResearch = settings.WebResearch;
            SelectedEffort = EffortLevels.FirstOrDefault(l => l.Effort == settings.Effort) ?? EffortLevels[0];
            SelectedProvider = Providers.FirstOrDefault(p => p.Kind == settings.Kind) ?? Providers[0];
            Tone3000ClientId = config.Tone3000ClientId ?? "";
        }
        finally
        {
            _loading = false;
        }

        ApiKeyEntry = "";
        RaiseKeyState();
        RefreshTone3000();
    }

    partial void OnSelectedProviderChanged(RigLabAiProvider value)
    {
        if (!value.IsAvailable)
        {
            // A disabled row in a ComboBox is not reliably unpickable, so the
            // refusal lives here: bounce back and SAY why. An option that silently
            // reverts reads as a broken control.
            SetStatus(_loc[value.Kind == AiProviderKind.CodexCli
                ? "Ai_Provider_CodexCliMissing"
                : "Ai_Provider_ClaudeCliMissing"], isError: false);
            _loading = true; // the bounce is not a pick: keep the message
            try { SelectedProvider = Providers.First(p => p.Kind == ProviderKind); }
            finally { _loading = false; }
            return;
        }

        if (_loading) return;
        var previous = ProviderKind;
        ProviderKind = value.Kind;
        ModelId = AiDefaultModels.Seed(previous, value.Kind, ModelId);
        SetStatus("", isError: false);
        Persist();
        RaiseKeyState();
    }

    partial void OnModelIdChanged(string value) => Persist();

    partial void OnBaseUrlChanged(string value) => Persist();

    partial void OnWebResearchChanged(bool value) => Persist();

    partial void OnSelectedEffortChanged(RigLabAiEffortOption value) => Persist();

    partial void OnTone3000ClientIdChanged(string value)
    {
        if (_loading) return;
        var id = value.Trim();
        _config.Update(c => c.Tone3000ClientId = id);
    }

    /// <summary>Writes the provider settings when they moved, and tells the design
    /// service — an open "Design a tone" panel republishes on the next request.</summary>
    private void Persist()
    {
        if (_loading) return;
        var settings = CurrentSettings;
        if (_config.Load().Ai == settings) return;
        _config.Update(c => c.Ai = settings);
        _design.NotifyConfigurationChanged();
        _logger?.Info(AppLogCategory.Settings,
            $"AI provider set to {settings.Kind} (model '{settings.ModelId}', enabled={settings.IsEnabled}).");
    }

    [RelayCommand]
    private void UseClaudeHaiku() => ModelId = ClaudeCliToneModel.DefaultModelId;

    [RelayCommand]
    private void UseClaudeOpus() => ModelId = ClaudeCliToneModel.OpusModelId;

    [RelayCommand]
    private void UseClaudeSonnet() => ModelId = ClaudeCliToneModel.AlternateModelId;

    [RelayCommand]
    private void UseCodexSol() => ModelId = CodexCliToneModel.SolModelId;

    [RelayCommand]
    private void UseCodexLuna() => ModelId = CodexCliToneModel.LunaModelId;

    [RelayCommand]
    private void UseLmStudio() => BaseUrl = AiProviderPresets.LmStudioBaseUrl;

    [RelayCommand]
    private void UseOllama() => BaseUrl = AiProviderPresets.OllamaBaseUrl;

    [RelayCommand]
    private void UseOpenAi() => BaseUrl = OpenAiBaseUrl;

    private bool CanSaveKey => IsApiKeyRowVisible && ApiKeyEntry.Trim().Length > 0;

    /// <summary>Seals the key for the CURRENT provider and empties the box.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveKey))]
    private void SaveKey()
    {
        _secrets.Write(ProviderKind, ApiKeyEntry.Trim());
        ApiKeyEntry = "";
        RaiseKeyState();
        _design.NotifyConfigurationChanged();
        SetStatus(_loc["Ai_KeySaved"], isError: false);
    }

    private bool CanClearKey => IsApiKeyRowVisible && HasStoredKey;

    /// <summary>Erases the stored key for the CURRENT provider.</summary>
    [RelayCommand(CanExecute = nameof(CanClearKey))]
    private void ClearKey()
    {
        _secrets.Write(ProviderKind, null);
        ApiKeyEntry = "";
        RaiseKeyState();
        SetStatus(_loc["Ai_KeyCleared"], isError: false);
    }

    private bool CanTestConnection => !IsBusy && ProviderKind != AiProviderKind.None
        && (ProviderKind == AiProviderKind.CodexCli || ModelId.Trim().Length > 0);

    /// <summary>Asks the configured provider to answer once. The CLIs answer with
    /// their version and spend nothing; an API provider makes one tiny call.</summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        SetStatus(_loc["Ai_Testing"], isError: false);
        try
        {
            var model = _factory.Create(CurrentSettings);
            try
            {
                var probe = await model.ProbeAsync(CancellationToken.None).ConfigureAwait(true);
                SetStatus(probe.Ok
                    ? _loc.Format("Ai_TestOk", probe.ModelId ?? probe.Message)
                    : _loc.Format("Ai_TestFailed", probe.Message), isError: !probe.Ok);
            }
            finally
            {
                (model as IDisposable)?.Dispose();
            }
        }
        catch (ToneDesignException ex)
        {
            SetStatus(_loc.Format("Ai_TestFailed", ex.Message), isError: true);
            _logger?.Warning(AppLogCategory.Settings, $"AI probe failed: {ex.Fault} — {ex.Detail}");
        }
        catch (Exception ex)
        {
            SetStatus(_loc.Format("Ai_TestFailed", ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSignIn => !IsSigningIn && !IsTone3000SignedIn && Tone3000ClientId.Trim().Length > 0;

    /// <summary>Opens the browser for the TONE3000 sign-in and waits for its
    /// loopback callback.</summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        _config.Flush();
        IsSigningIn = true;
        Tone3000Status = "";
        using var cts = _signIn = new CancellationTokenSource();
        try
        {
            var result = await _tone3000.SignInAsync(cts.Token).ConfigureAwait(true);
            if (!result.Ok && !cts.IsCancellationRequested)
            {
                Tone3000Status = result.Error == DotCharter.Tone.Online.OnlineError.NotConfigured
                    ? _loc["Tone3000_NeedClientId"]
                    : _loc["Nam_SignInFailed"];
            }
        }
        catch (OperationCanceledException)
        {
            // The user cancelled; the account row says where things stand.
        }
        finally
        {
            _signIn = null;
            IsSigningIn = false;
            RefreshTone3000();
        }
    }

    private bool CanCancelSignIn => IsSigningIn;

    [RelayCommand(CanExecute = nameof(CanCancelSignIn))]
    private void CancelSignIn()
    {
        try { _signIn?.Cancel(); }
        catch (ObjectDisposedException) { /* the sign-in already finished */ }
    }

    private bool CanSignOut => IsTone3000SignedIn;

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private void SignOut()
    {
        _tone3000.SignOut();
        Tone3000Status = _loc["Tone3000_SignedOut"];
        RefreshTone3000();
    }

    private void OnAuthStateChanged() => _ui.Post(RefreshTone3000);

    private void RefreshTone3000()
    {
        IsTone3000SignedIn = _tone3000.IsSignedIn;
        OnPropertyChanged(nameof(Tone3000AccountText));
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        IsStatusError = isError;
    }

    private void RaiseKeyState()
    {
        HasStoredKey = ProviderKind != AiProviderKind.None && !string.IsNullOrEmpty(_secrets.Read(ProviderKind));
        SaveKeyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Unhooks the account and cancels a sign-in in flight.</summary>
    public void Dispose()
    {
        _tone3000.AuthStateChanged -= OnAuthStateChanged;
        CancelSignIn();
    }
}
