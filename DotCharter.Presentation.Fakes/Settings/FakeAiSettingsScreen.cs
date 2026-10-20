using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free AI tab in every state the screen can be in.</summary>
public sealed class FakeAiSettingsScreen : ObservableObject, IAiSettingsScreen
{
    private IAiProviderOption _selectedProvider;
    private string _modelId = "";
    private string _baseUrl = "";
    private string _apiKeyEntry = "";
    private bool _useLibraryTones = true;
    private bool _webResearch = true;

    private FakeAiSettingsScreen(int provider, FakeMatchModelPart match)
    {
        Providers =
        [
            new Option("Off"),
            new Option("Claude (Anthropic API)"),
            new Option("OpenAI-compatible server"),
            new Option("Claude Code (local CLI)"),
            new Option("Built in", false)
        ];
        _selectedProvider = Providers[provider];
        Match = match;
        UseClaudeHaikuCommand = new RelayCommand(() => { });
        UseClaudeOpusCommand = new RelayCommand(() => { });
        UseClaudeSonnetCommand = new RelayCommand(() => { });
        UseCodexSolCommand = new RelayCommand(() => { });
        UseCodexLunaCommand = new RelayCommand(() => { });
        UseLmStudioCommand = new RelayCommand(() => { });
        UseOllamaCommand = new RelayCommand(() => { });
        ClearKeyCommand = new RelayCommand(() => { });
        TestConnectionCommand = new RelayCommand(() => { });
    }

    public static IReadOnlyList<FakeVariant<IAiSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Off", Off),
        new("LocalCli", LocalCli),
        new("OpenAiCompatible", OpenAiCompatible),
        new("Unavailable", Unavailable),
        new("Downloading", Downloading),
        new("Error", Error)
    ];

    /// <summary>Claude with a stored key and both downloads in place.</summary>
    public static FakeAiSettingsScreen Default() =>
        new(1, FakeMatchModelPart.Installed())
        {
            _modelId = "claude-opus-5",
            HasStoredKey = true,
            IsApiKeyRowVisible = true
        };

    /// <summary>Nothing chosen, nothing installed, no status anywhere.</summary>
    public static FakeAiSettingsScreen Empty() =>
        new(0, FakeMatchModelPart.Missing()) { IsEnabledRows = false };

    public static FakeAiSettingsScreen LongText() =>
        new(2, FakeMatchModelPart.LongText())
        {
            _modelId = "meta-llama-3.1-70b-instruct-abliterated-gguf-q5-k-m-sehr-langer-name",
            _baseUrl = "http://localhost:1234/v1/chat/completions/mit/einem/sehr/langen/pfad",
            IsBaseUrlRowVisible = true,
            IsApiKeyRowVisible = true,
            IsKeySessionOnlyNoteVisible = true,
            StatusText = "Die Verbindung ist fehlgeschlagen: 非常に長いエラーメッセージのテストケースです。サーバーの応答を確認してください。",
            IsStatusError = true
        };

    /// <summary>Every provider-conditional row on screen at once, for layout.</summary>
    public static FakeAiSettingsScreen Full() =>
        new(3, FakeMatchModelPart.Missing())
        {
            _modelId = "claude-opus-5",
            _baseUrl = "http://localhost:1234/v1",
            IsClaudeCliNoteVisible = true,
            IsCodexCliNoteVisible = true,
            AreClaudeModelPresetsVisible = true,
            AreCodexModelPresetsVisible = true,
            IsCliPathRowVisible = true,
            IsBaseUrlRowVisible = true,
            IsApiKeyRowVisible = true,
            IsKeySessionOnlyNoteVisible = true,
            HasStoredKey = true,
            StatusText = "Connected — answered as claude-opus-5."
        };

    public static FakeAiSettingsScreen Off() => new(0, FakeMatchModelPart.Installed()) { IsEnabledRows = false };

    public static FakeAiSettingsScreen LocalCli() =>
        new(3, FakeMatchModelPart.Installed())
        {
            _modelId = "claude-opus-5",
            IsClaudeCliNoteVisible = true,
            AreClaudeModelPresetsVisible = true,
            IsCliPathRowVisible = true,
            _baseUrl = @"C:\Users\me\AppData\Roaming\npm\claude.cmd"
        };

    public static FakeAiSettingsScreen OpenAiCompatible() =>
        new(2, FakeMatchModelPart.Missing())
        {
            _modelId = "qwen2.5-coder-32b",
            _baseUrl = "http://localhost:1234/v1",
            IsBaseUrlRowVisible = true,
            IsApiKeyRowVisible = true,
            StatusText = "Connected — answered as qwen2.5-coder-32b."
        };

    /// <summary>A build whose composition root registered no AI services.</summary>
    public static FakeAiSettingsScreen Unavailable() =>
        new(0, FakeMatchModelPart.Missing()) { IsAvailable = false, IsEnabledRows = false };

    public static FakeAiSettingsScreen Downloading() =>
        new(1, FakeMatchModelPart.Downloading()) { _modelId = "claude-opus-5", HasStoredKey = true, IsApiKeyRowVisible = true };

    public static FakeAiSettingsScreen Error() =>
        new(1, FakeMatchModelPart.Failed())
        {
            _modelId = "claude-opus-4-1",
            Reasoning = FakeReasoningPart.Unsupported(),
            IsApiKeyRowVisible = true,
            StatusText = "The test failed: 401 Unauthorized — check the API key.",
            IsStatusError = true
        };

    /// <summary>Every provider-conditional row at once, for the states that show them all.</summary>
    private bool IsEnabledRows { get; init; } = true;

    public bool IsAvailable { get; private init; } = true;

    public bool IsUnavailableNoteVisible => !IsAvailable;

    public bool IsProviderGroupVisible => true;

    public IReadOnlyList<IAiProviderOption> Providers { get; }

    public IAiProviderOption SelectedProvider
    {
        get => _selectedProvider;
        set => SetProperty(ref _selectedProvider, value);
    }

    public bool IsProviderRowVisible => true;

    public bool IsClaudeCliNoteVisible { get; private init; }

    public bool IsCodexCliNoteVisible { get; private init; }

    public string ModelId
    {
        get => _modelId;
        set => SetProperty(ref _modelId, value);
    }

    public bool IsModelRowVisible => IsEnabledRows;

    public string ModelDesc { get; private init; } = "The model id the provider expects.";

    public bool AreClaudeModelPresetsVisible { get; private init; }

    public ICommand UseClaudeHaikuCommand { get; }

    public ICommand UseClaudeOpusCommand { get; }

    public ICommand UseClaudeSonnetCommand { get; }

    public bool AreCodexModelPresetsVisible { get; private init; }

    public ICommand UseCodexSolCommand { get; }

    public ICommand UseCodexLunaCommand { get; }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public bool IsBaseUrlRowVisible { get; private init; }

    public bool IsCliPathRowVisible { get; private init; }

    public string CliPathLabel { get; private init; } = "Claude Code path";

    public string CliPathDesc { get; private init; } = "Optional. Leave empty to use the “claude” command on your PATH.";

    public ICommand UseLmStudioCommand { get; }

    public ICommand UseOllamaCommand { get; }

    public string ApiKeyEntry
    {
        get => _apiKeyEntry;
        set => SetProperty(ref _apiKeyEntry, value);
    }

    public bool IsApiKeyRowVisible { get; private init; }

    public bool IsKeySessionOnlyNoteVisible { get; private init; }

    public string KeyStateText => HasStoredKey ? "A key is stored." : "No key stored.";

    public bool HasStoredKey { get; private init; }

    public ICommand ClearKeyCommand { get; }

    public bool UseLibraryTones
    {
        get => _useLibraryTones;
        set => SetProperty(ref _useLibraryTones, value);
    }

    public bool IsUseLibraryTonesRowVisible => IsEnabledRows;

    public bool WebResearch
    {
        get => _webResearch;
        set => SetProperty(ref _webResearch, value);
    }

    public bool IsWebResearchRowVisible => IsEnabledRows;

    public IAiReasoningPart Reasoning { get; private init; } = FakeReasoningPart.Live();

    public bool IsEffortRowVisible => IsEnabledRows;

    public bool IsTestRowVisible => IsEnabledRows;

    public ICommand TestConnectionCommand { get; }

    public string StatusText { get; private init; } = "";

    public bool HasStatus => StatusText.Length > 0;

    public bool IsStatusError { get; private init; }

    public IMatchModelPart Match { get; }

    private sealed record Option(string Label, bool IsAvailable = true) : IAiProviderOption;

    private sealed record Level(string Label) : IAiEffortOption;

    /// <summary>The Reasoning row: a level picked, or disabled with its reason.</summary>
    private sealed class FakeReasoningPart : ObservableObject, IAiReasoningPart
    {
        private IAiEffortOption _selected;

        private FakeReasoningPart(int selected, bool isSupported, string description)
        {
            Levels = [new Level("Default"), new Level("Low"), new Level("Medium"), new Level("High")];
            _selected = Levels[selected];
            IsSupported = isSupported;
            Description = description;
        }

        public IReadOnlyList<IAiEffortOption> Levels { get; }

        public IAiEffortOption Selected
        {
            get => _selected;
            set => SetProperty(ref _selected, value);
        }

        public bool IsSupported { get; }

        public string Description { get; }

        internal static FakeReasoningPart Live() => new(3, true,
            "How long the model thinks before it answers. Default leaves it to the provider; higher is slower and costs more.");

        internal static FakeReasoningPart Unsupported() => new(0, false,
            "This Claude model takes no reasoning level (that arrived with Claude Opus 4.5 and Sonnet 4.6), so it runs at its own.");
    }

    /// <summary>The match-model block on its own, so each download state is reachable.</summary>
    private sealed class FakeMatchModelPart : ObservableObject, IMatchModelPart
    {
        private FakeMatchModelPart()
        {
            DownloadModelCommand = new RelayCommand(() => { });
            CancelModelDownloadCommand = new RelayCommand(() => { });
            DownloadLibraryCommand = new RelayCommand(() => { });
            CancelLibraryDownloadCommand = new RelayCommand(() => { });
            OpenFolderCommand = new RelayCommand(() => { });
            RefreshCommand = new RelayCommand(() => { });
        }

        internal static FakeMatchModelPart Installed() => new()
        {
            ModelStatus = "Installed: rig-estimator-v3, epoch 41, 196 MB.",
            LibraryStatus = "Installed: 118 MB."
        };

        internal static FakeMatchModelPart Missing() => new()
        {
            ModelStatus = "Not installed — about 200 MB to download.",
            LibraryStatus = "Not installed — about 120 MB to download.",
            CanDownloadModel = true,
            CanDownloadLibrary = true
        };

        internal static FakeMatchModelPart Downloading() => new()
        {
            ModelStatus = "Not installed — about 200 MB to download.",
            ModelDetail = "Downloading rig-estimator.onnx (84 MB of 196 MB)…",
            IsModelDownloading = true,
            LibraryStatus = "Installed: 118 MB."
        };

        internal static FakeMatchModelPart Failed() => new()
        {
            ModelStatus = "Installed, but the model could not be loaded.",
            ModelIsError = true,
            ModelDetail = "The download failed: the server answered 503 Service Unavailable.",
            LibraryStatus = "Not installed — about 120 MB to download.",
            LibraryDetail = "The download failed: the connection was reset.",
            LibraryIsError = true,
            CanDownloadLibrary = true
        };

        internal static FakeMatchModelPart LongText() => new()
        {
            ModelStatus = "Installiert: rig-estimator-v3-mit-einem-sehr-langen-namen, Epoche 41, 196 MB. 注意: このモデルは合成データで学習されています。",
            LibraryStatus = "Installiert: 118 MB — 非常に長いステータス行のテストケースです。",
            ModelDetail = "Der Download ist fehlgeschlagen: Der Server hat mit 503 Service Unavailable geantwortet.",
            ModelIsError = true
        };

        public bool IsGroupVisible => true;

        public bool IsModelRowVisible => true;

        public string ModelStatus { get; private init; } = "";

        public bool ModelIsError { get; private init; }

        public bool IsLibraryRowVisible => true;

        public string LibraryStatus { get; private init; } = "";

        public bool LibraryIsError { get; private init; }

        public bool CanDownloadModel { get; private init; }

        public ICommand DownloadModelCommand { get; }

        public bool IsModelDownloading { get; private init; }

        public ICommand CancelModelDownloadCommand { get; }

        public bool CanDownloadLibrary { get; private init; }

        public ICommand DownloadLibraryCommand { get; }

        public bool IsLibraryDownloading { get; private init; }

        public ICommand CancelLibraryDownloadCommand { get; }

        public string ModelDirectory => @"C:\Users\me\AppData\Local\DotCharter\ai-match\rig-estimator";

        public ICommand OpenFolderCommand { get; }

        public ICommand RefreshCommand { get; }

        public string ModelDetail { get; private init; } = "";

        public bool HasModelDetail => ModelDetail.Length > 0;

        public string LibraryDetail { get; private init; } = "";

        public bool HasLibraryDetail => LibraryDetail.Length > 0;
    }
}
