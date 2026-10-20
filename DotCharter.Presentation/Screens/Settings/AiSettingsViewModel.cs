using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Ai;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ AI. The values that persist through <c>AppConfig.Ai</c> — the
///     provider, the model id, the base URL, the library-tones anchor, web research
///     and the reasoning level — are what the change set tracks; everything else on the tab is
///     status. The API key goes to <see cref="IAiSecretStore" /> instead, and is
///     never read back into the box.
/// </summary>
public sealed partial class AiSettingsViewModel : ObservableObject,
    IAiSettingsScreen, ISettingsTab, ISettingsChangeSource, IDisposable
{
    /// <summary>The model id Claude Code is given when it is picked with nothing chosen yet.</summary>
    public const string DefaultClaudeModel = ClaudeCliToneModel.DefaultModelId;

    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly MatchModelViewModel _match;
    private readonly IAiSecretStore? _secrets;
    private readonly IToneDesignModelFactory? _factory;
    private readonly IToneDesignService? _design;
    private readonly IAppLogger? _logger;
    private readonly SettingsChangeSet _changes = new();
    private string _query = "";
    private bool _loading;

    public AiSettingsViewModel(
        IConfigurationService config,
        ILocalizationService localization,
        MatchModelViewModel match,
        IAiSecretStore? secrets = null,
        IToneDesignModelFactory? factory = null,
        IToneDesignService? design = null,
        IAppLogger? logger = null)
    {
        _config = config;
        _localization = localization;
        _match = match;
        _secrets = secrets;
        _factory = factory;
        _design = design;
        _logger = logger;

        // Every row but None is enabled by the FACTORY, never by a list kept
        // here: a second hand-kept copy of what a build can offer is what goes stale.
        Providers = AiProviderShape.Options(localization, Supports);
        _selectedProvider = Providers[0];
        Reasoning = new AiReasoningPart(localization);
        Reasoning.PropertyChanged += (_, _) => OnPropertyChanged(nameof(UnsavedSettings));

        _changes.Track("ai.ProviderKind", () => ProviderKind);
        _changes.Track("ai.ModelId", () => ModelId);
        _changes.Track("ai.BaseUrl", () => BaseUrl);
        _changes.Track("ai.UseLibraryTones", () => UseLibraryTones);
        _changes.Track("ai.WebResearch", () => WebResearch);
        _changes.Track("ai.Effort", () => Reasoning.Effort);
        _changes.Rebaseline();
    }

    public IReadOnlyList<AiProviderOption> Providers { get; }

    IReadOnlyList<IAiProviderOption> IAiSettingsScreen.Providers => Providers;

    public bool IsAvailable => _secrets != null && _factory != null;

    public bool IsUnavailableNoteVisible => !IsAvailable && IsProviderRowVisible;

    public IMatchModelPart Match => _match;

    public AiReasoningPart Reasoning { get; }

    IAiReasoningPart IAiSettingsScreen.Reasoning => Reasoning;

    [ObservableProperty] private bool _isEffortRowVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabledProvider), nameof(CliPathLabel), nameof(CliPathDesc), nameof(ModelDesc))]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private AiProviderKind _providerKind;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _modelId = "";

    [ObservableProperty] private string _baseUrl = "";

    [ObservableProperty] private bool _useLibraryTones = true;

    /// <summary>The combo's selection: an option OBJECT, which the change set cannot compare — <see cref="ProviderKind" /> is the value it writes.</summary>
    [ObservableProperty] private AiProviderOption _selectedProvider;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _apiKeyEntry = "";

    [ObservableProperty] private bool _hasStoredKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    [ObservableProperty] private bool _isStatusError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isProviderGroupVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailableNoteVisible))]
    private bool _isProviderRowVisible = true;

    [ObservableProperty] private bool _isClaudeCliNoteVisible;

    [ObservableProperty] private bool _isCodexCliNoteVisible;

    [ObservableProperty] private bool _isModelRowVisible;

    [ObservableProperty] private bool _areClaudeModelPresetsVisible;

    [ObservableProperty] private bool _areCodexModelPresetsVisible;

    [ObservableProperty] private bool _isCliPathRowVisible;

    [ObservableProperty] private bool _isBaseUrlRowVisible;

    [ObservableProperty] private bool _isApiKeyRowVisible;

    [ObservableProperty] private bool _isKeySessionOnlyNoteVisible;

    [ObservableProperty] private bool _isUseLibraryTonesRowVisible;

    [ObservableProperty] private bool _webResearch = true;

    [ObservableProperty] private bool _isWebResearchRowVisible;

    [ObservableProperty] private bool _isTestRowVisible;

    IAiProviderOption IAiSettingsScreen.SelectedProvider
    {
        get => SelectedProvider;
        set => SelectedProvider = value as AiProviderOption ?? Providers[0];
    }

    public bool HasStatus => StatusText.Length > 0;

    public bool IsEnabledProvider => ProviderKind != AiProviderKind.None;

    /// <summary>The rows, notes and words the chosen provider wants.</summary>
    public AiProviderShape Shape => AiProviderShape.For(ProviderKind);

    public string CliPathLabel => _localization[Shape.CliPathLabelKey];

    public string CliPathDesc => _localization[Shape.CliPathDescKey];

    public string ModelDesc => _localization[Shape.ModelDescKey];

    public string KeyStateText => _localization[HasStoredKey ? "Ai_KeyStored" : "Ai_KeyMissing"];

    /// <summary>True where the store cannot SEAL the key, so it lives for the session only.</summary>
    public bool KeyIsSessionOnly => _secrets is { IsPersistent: false };

    ICommand IAiSettingsScreen.UseClaudeHaikuCommand => UseClaudeHaikuCommand;

    ICommand IAiSettingsScreen.UseClaudeOpusCommand => UseClaudeOpusCommand;

    ICommand IAiSettingsScreen.UseClaudeSonnetCommand => UseClaudeSonnetCommand;

    ICommand IAiSettingsScreen.UseCodexSolCommand => UseCodexSolCommand;

    ICommand IAiSettingsScreen.UseCodexLunaCommand => UseCodexLunaCommand;

    ICommand IAiSettingsScreen.UseLmStudioCommand => UseLmStudioCommand;

    ICommand IAiSettingsScreen.UseOllamaCommand => UseOllamaCommand;

    ICommand IAiSettingsScreen.ClearKeyCommand => ClearKeyCommand;

    ICommand IAiSettingsScreen.TestConnectionCommand => TestConnectionCommand;

    public string Key => SettingsTabKeys.Ai;

    public Type? Screen => typeof(IAiSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    /// <summary>The settings as the factory wants them — trimmed, and an empty base URL reported as null.</summary>
    public AiProviderSettings CurrentSettings => new(
        ProviderKind, ModelId.Trim(), string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.Trim(), UseLibraryTones, WebResearch,
        Reasoning.Effort);

    /// <summary>Re-reads the persisted settings and the key's PRESENCE, never its value. Also the page's revert.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var settings = _config.LoadAppConfig().Ai ?? AiProviderSettings.Disabled;
            ProviderKind = settings.Kind;
            ModelId = settings.ModelId ?? "";
            BaseUrl = settings.BaseUrl ?? "";
            UseLibraryTones = settings.UseLibraryTones;
            WebResearch = settings.WebResearch;
            Reasoning.Effort = settings.Effort;
            SelectedProvider = Providers.FirstOrDefault(p => p.Kind == settings.Kind) ?? Providers[0];
        }
        finally
        {
            _loading = false;
        }

        ApiKeyEntry = "";
        StatusText = "";
        IsStatusError = false;
        RaiseKeyState();

        // Cheap: a file-presence check and at most one already-taken verdict.
        _match.Refresh();
        ApplySearch(_query);
        Rebaseline();
    }

    /// <summary>
    ///     Persists the non-secret settings and, when the box holds something,
    ///     the key. An EMPTY box means "leave the stored key alone"; Clear is
    ///     the erase.
    /// </summary>
    public void Save()
    {
        var settings = CurrentSettings;
        var config = _config.LoadAppConfig();
        if (config.Ai != settings)
        {
            config.Ai = settings;
            _config.SaveAppConfig(config);
            _logger?.Info(Tone.Infrastructure.Configuration.AppLogCategory.Settings,
                $"AI provider set to {settings.Kind} (model '{settings.ModelId}', enabled={settings.IsEnabled}).");
            // Inside the change test on purpose: a Save that moved nothing must
            // not make an open "Design a tone" panel republish.
            _design?.NotifyConfigurationChanged();
        }

        if (ApiKeyEntry.Trim().Length > 0 && _secrets != null)
        {
            _secrets.Write(settings.Kind, ApiKeyEntry.Trim());
            ApiKeyEntry = "";
        }

        RaiseKeyState();
        Rebaseline();
    }

    public void Revert() => Load();

    public void Reset() => Load();

    public void Rebaseline()
    {
        _changes.Rebaseline();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    public void Dispose() => _match.Dispose();

    partial void OnSelectedProviderChanged(AiProviderOption value)
    {
        if (!value.IsAvailable)
        {
            // A disabled row in a ComboBox is not reliably unpickable, so the
            // refusal lives here: bounce the selection back and SAY why. An
            // option that silently reverts reads as a broken control.
            SetStatus(_localization[AiProviderShape.For(value.Kind).MissingKey], isError: false);
            SelectedProvider = Providers.First(p => p.Kind == ProviderKind);
            return;
        }

        var previous = ProviderKind;
        ProviderKind = value.Kind;
        if (_loading) return;

        ModelId = AiDefaultModels.Seed(previous, value.Kind, ModelId);

        RaiseKeyState();
    }

    partial void OnProviderKindChanged(AiProviderKind value)
    {
        RaiseKeyState();
        ApplySearch(_query);
        Reasoning.Refresh(value, ModelId);
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    partial void OnModelIdChanged(string value)
    {
        Reasoning.Refresh(ProviderKind, value);
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    partial void OnBaseUrlChanged(string value) => OnPropertyChanged(nameof(UnsavedSettings));

    partial void OnUseLibraryTonesChanged(bool value) => OnPropertyChanged(nameof(UnsavedSettings));

    partial void OnWebResearchChanged(bool value) => OnPropertyChanged(nameof(UnsavedSettings));

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

    /// <summary>Erases the stored key for the CURRENT provider.</summary>
    [RelayCommand]
    private void ClearKey()
    {
        _secrets?.Write(ProviderKind, null);
        ApiKeyEntry = "";
        RaiseKeyState();
        SetStatus(_localization["Ai_KeyCleared"], isError: false);
    }

    private bool CanTestConnection =>
        IsAvailable && !IsBusy && ProviderKind != AiProviderKind.None
        && (ModelId.Trim().Length > 0 || Shape.ModelIsOptional);

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        if (IsBusy || _factory is null) return;

        Save();
        IsBusy = true;
        SetStatus(_localization["Ai_Testing"], isError: false);
        try
        {
            var model = _factory.Create(CurrentSettings);
            var probe = await model.ProbeAsync(CancellationToken.None).ConfigureAwait(true);
            SetStatus(probe.Ok
                ? _localization.Format("Ai_TestOk", probe.ModelId ?? CurrentSettings.ModelId)
                : _localization.Format("Ai_TestFailed", probe.Message), isError: !probe.Ok);
        }
        catch (ToneDesignException ex)
        {
            SetStatus(_localization.Format("Ai_TestFailed", ex.Message), isError: true);
            _logger?.Warning(Tone.Infrastructure.Configuration.AppLogCategory.Settings, $"AI probe failed: {ex.Fault} — {ex.Detail}");
        }
        catch (Exception ex)
        {
            SetStatus(_localization.Format("Ai_TestFailed", ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool Supports(AiProviderKind kind) => _factory?.Supports(kind) ?? true;

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        IsStatusError = isError;
    }

    private void RaiseKeyState()
    {
        HasStoredKey = _secrets != null
                       && ProviderKind != AiProviderKind.None
                       && !string.IsNullOrEmpty(_secrets.Read(ProviderKind));
        OnPropertyChanged(nameof(KeyStateText));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsUnavailableNoteVisible));
    }
}

