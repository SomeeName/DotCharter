using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free lyric-model section in every state its rows can be in.</summary>
public sealed class FakeLyricModelsSettingsScreen : ObservableObject, ILyricModelsSettingsScreen
{
    private FakeLyricModelsSettingsScreen(params FakeLyricModelRow[] models) => Models = models;

    public static IReadOnlyList<FakeVariant<ILyricModelsSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Downloading", Downloading)
    ];

    /// <summary>The required model installed and on the GPU; the English one not downloaded.</summary>
    public static FakeLyricModelsSettingsScreen Default() => new(
        Multilingual(installed: true, "Runs on the GPU (DirectML)."),
        English(installed: false, "Uses the GPU when one is available. Not run yet in this session."));

    /// <summary>Nothing downloaded yet.</summary>
    public static FakeLyricModelsSettingsScreen Empty() => new(
        Multilingual(installed: false, "Uses the GPU when one is available. Not run yet in this session."),
        English(installed: false, "Uses the GPU when one is available. Not run yet in this session."));

    /// <summary>Both rows carrying their longest lines: a failed download and the CPU fallback.</summary>
    public static FakeLyricModelsSettingsScreen LongText()
    {
        var english = English(installed: false, "Runs on the CPU: no GPU provider is available.");
        english.ProgressText = "The download failed. The log says why.";
        return new(Multilingual(installed: true, "Runs on the CPU: it failed on the GPU in this session.", cpuFallback: true), english);
    }

    /// <summary>Both installed; one fell back to the CPU, so Retry GPU shows.</summary>
    public static FakeLyricModelsSettingsScreen Full() => new(
        Multilingual(installed: true, "Runs on the CPU: it failed on the GPU in this session.", cpuFallback: true),
        English(installed: true, "Runs on the GPU (DirectML)."));

    /// <summary>The English model mid-download.</summary>
    public static FakeLyricModelsSettingsScreen Downloading()
    {
        var english = English(installed: false, "Uses the GPU when one is available. Not run yet in this session.");
        english.IsBusy = true;
        english.ProgressText = "Downloading the lyric model (180 MB)…";
        return new(Multilingual(installed: true, "Runs on the GPU (DirectML)."), english);
    }

    public IReadOnlyList<ILyricModelRow> Models { get; }

    public IReadOnlySet<string> HiddenKeys { get; } = new HashSet<string>();

    private static FakeLyricModelRow Multilingual(bool installed, string provider, bool cpuFallback = false) => new(
        "lyricModels.lyric-align", "Multilingual model",
        "Required. Aligns romanized lyrics in 1,100+ languages, Japanese charted as romaji included.",
        installed, installed ? "Installed" : "Not installed · 216 MB download", provider, cpuFallback);

    private static FakeLyricModelRow English(bool installed, string provider) => new(
        "lyricModels.lyric-align-en", "English model",
        "Optional. Sharper timing for English lyrics; runs alongside the multilingual model.",
        installed, installed ? "Installed" : "Not installed · 180 MB download", provider, cpuFallback: false);
}

/// <summary>One fake lyric model row.</summary>
public sealed partial class FakeLyricModelRow(
    string key, string name, string description, bool isInstalled, string statusText, string providerText, bool cpuFallback)
    : ObservableObject, ILyricModelRow
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";

    public string Key { get; } = key;

    public bool IsShown => true;

    public string Name { get; } = name;

    public string Description { get; } = description;

    public bool IsInstalled { get; } = isInstalled;

    public string StatusText { get; } = statusText;

    public string ProviderText { get; } = providerText;

    public bool IsCpuFallback { get; } = cpuFallback;

    public ICommand DownloadCommand { get; } = new RelayCommand(() => { });

    public ICommand RepairCommand { get; } = new RelayCommand(() => { });

    public ICommand RetryGpuCommand { get; } = new RelayCommand(() => { });
}
