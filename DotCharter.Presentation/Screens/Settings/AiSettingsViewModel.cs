using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Ai;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>AI settings and pending key input; persisted secrets are never read into the box.</summary>
public sealed partial class AiSettingsViewModel : ObservableObject,
    IAiSettingsScreen, ISettingsTab, ISettingsChangeSource, IDisposable
{
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
    private int _statusGeneration;

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

    [ObservableProperty] private AiProviderOption _selectedProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnsavedSettings))]
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

    public AiProviderShape Shape => AiProviderShape.For(ProviderKind);

    public string CliPathLabel => _localization[Shape.CliPathLabelKey];

    public string CliPathDesc => _localization[Shape.CliPathDescKey];

    public string ModelDesc => _localization[Shape.ModelDescKey];

    public string KeyStateText => _localization[HasStoredKey ? "Ai_KeyStored" : "Ai_KeyMissing"];

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

    public IReadOnlyList<string> UnsavedSettings => ApiKeyEntry.Trim().Length > 0
        ? [.. _changes.Differences(), "ai.ApiKeyEntry"] : _changes.Differences();

    public bool HasSaveError { get; private set; }

    public AiProviderSettings CurrentSettings => new(
        ProviderKind, ModelId.Trim(), string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.Trim(), UseLibraryTones, WebResearch,
        Reasoning.Effort);

    /// <summary>Re-reads the persisted settings and the key's PRESENCE, never its value. Also the page's revert.</summary>
    public void Load()
    {
        _statusGeneration++;
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

        _match.Refresh();
        ApplySearch(_query);
        Rebaseline();
    }

    /// <summary>Saves settings and pending key input; an empty key box preserves the stored key.</summary>
    public void Save()
    {
        HasSaveError = false;
        var settings = CurrentSettings;
        var config = _config.LoadAppConfig();
        if (config.Ai != settings)
        {
            config.Ai = settings;
            _config.SaveAppConfig(config);
            _logger?.Info(Tone.Infrastructure.Configuration.AppLogCategory.Settings,
                $"AI provider set to {settings.Kind} (model '{settings.ModelId}', enabled={settings.IsEnabled}).");
            _design?.NotifyConfigurationChanged();
        }

        if (ApiKeyEntry.Trim().Length > 0)
        {
            HasSaveError = _secrets is null || !_secrets.Write(settings.Kind, ApiKeyEntry.Trim());
            if (!HasSaveError) ApiKeyEntry = "";
            SetStatus(_localization[HasSaveError ? "Ai_KeySaveFailed" : "Ai_KeySaved"], HasSaveError);
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

    public void Dispose()
    {
        _statusGeneration++;
        _match.Dispose();
    }

    partial void OnSelectedProviderChanged(AiProviderOption value)
    {
        if (!value.IsAvailable)
        {
            // Refuse unavailable providers even when the picker allows the selection.
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

    [RelayCommand]
    private void ClearKey()
    {
        if (_secrets is null || !_secrets.Write(ProviderKind, null))
        {
            SetStatus(_localization["Ai_KeyClearFailed"], isError: true);
            return;
        }

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
        if (HasSaveError) return;
        IsBusy = true;
        SetStatus(_localization["Ai_Testing"], isError: false);
        var statusGeneration = _statusGeneration;
        try
        {
            var model = _factory.Create(CurrentSettings);
            var probe = await model.ProbeAsync(CancellationToken.None).ConfigureAwait(true);
            if (statusGeneration != _statusGeneration) return;
            SetStatus(probe.Ok
                ? _localization.Format("Ai_TestOk", probe.ModelId ?? CurrentSettings.ModelId)
                : _localization.Format("Ai_TestFailed", probe.Message), isError: !probe.Ok);
        }
        catch (ToneDesignException ex)
        {
            if (statusGeneration == _statusGeneration)
                SetStatus(_localization.Format("Ai_TestFailed", ex.Message), isError: true);
            _logger?.Warning(Tone.Infrastructure.Configuration.AppLogCategory.Settings, $"AI probe failed: {ex.Fault} — {ex.Detail}");
        }
        catch (Exception ex)
        {
            if (statusGeneration == _statusGeneration)
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
        _statusGeneration++;
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

