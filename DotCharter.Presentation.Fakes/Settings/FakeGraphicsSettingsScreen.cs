using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Graphics tab in every state the screen can be in.</summary>
public sealed partial class FakeGraphicsSettingsScreen : ObservableObject, IGraphicsSettingsScreen
{
    [ObservableProperty] private double _renderScalePercent = 100;
    [ObservableProperty] private IGraphicsChoice? _selectedMaxFps;
    [ObservableProperty] private double _idleFps = 10;
    [ObservableProperty] private bool _animatedHomeHero;
    [ObservableProperty] private IGraphicsChoice? _selectedPresentMode;
    [ObservableProperty] private IGraphicsChoice? _selectedMsaa;
    [ObservableProperty] private bool _enableAnchorPreview = true;
    [ObservableProperty] private bool _enableAnchorSweep = true;
    [ObservableProperty] private double _anchorPreviewLeadMs = 1000;
    [ObservableProperty] private double _visualLeadMs = 12;
    [ObservableProperty] private bool _highwayScrollSpeedOverrideEnabled;
    [ObservableProperty] private double _highwayScrollSpeed = 1.3;
    [ObservableProperty] private double _highwayDrawDistanceZ = 45;
    [ObservableProperty] private double _cameraDistancePercent = 100;
    [ObservableProperty] private bool _show3DFingering = true;
    [ObservableProperty] private bool _timeline2DPolish = true;
    [ObservableProperty] private bool _timeline2DLaneTint;
    [ObservableProperty] private bool _invertStrings;
    [ObservableProperty] private bool _enableHighwayNearFade = true;
    [ObservableProperty] private bool _enableNoteHitExplosions = true;
    [ObservableProperty] private bool _enableEnhancedHitFeedback = true;
    [ObservableProperty] private bool _enableHitSparks = true;
    [ObservableProperty] private bool _enableBeatPulse = true;
    [ObservableProperty] private bool _enableDepthFog = true;
    [ObservableProperty] private bool _enableVignette = true;
    [ObservableProperty] private bool _enableHighwayLighting = true;
    [ObservableProperty] private bool _enableBloom = true;
    [ObservableProperty] private bool _enableStringShimmer = true;
    [ObservableProperty] private bool _enableFilmicTonemap = true;
    [ObservableProperty] private bool _enableShowlightBeams = true;
    [ObservableProperty] private bool _enableShowlightFog;
    [ObservableProperty] private bool _enableCrowdBackground = true;
    [ObservableProperty] private bool _isAdvancedOpen;

    private FakeGraphicsSettingsScreen()
    {
    }

    public static IReadOnlyList<FakeVariant<IGraphicsSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("AdvancedOpen", AdvancedOpen),
        new("Overridden", Overridden)
    ];

    /// <summary>A device that reports FIFO and Mailbox and MSAA up to 8×, on the default settings.</summary>
    public static FakeGraphicsSettingsScreen Default() =>
        With(["FIFO (vsync)", "Mailbox (low latency)"], ["Off", "2×", "4×", "8×"], mode: 0, msaa: 2);

    /// <summary>The device has not initialised yet: FIFO is the only mode and MSAA is off.</summary>
    public static FakeGraphicsSettingsScreen Empty() => With(["FIFO (vsync)"], ["Off"], mode: 0, msaa: 0);

    public static FakeGraphicsSettingsScreen LongText() =>
        With(["FIFO (vertikale Synchronisation, keine Tearing-Artefakte)", "メールボックス（低遅延・ティアリングなし）"],
            ["Aus", "2× Kantenglättung", "4× Kantenglättung"], mode: 1, msaa: 1, secondsLabel: "12,5 s");

    /// <summary>Every mode and every level a device can report, so the lists are at their longest.</summary>
    public static FakeGraphicsSettingsScreen Full() =>
        With(["FIFO (vsync)", "Mailbox (low latency)", "Immediate (tearing)"], ["Off", "2×", "4×", "8×", "16×"],
            mode: 1, msaa: 3);

    /// <summary>The Advanced disclosure open, so the effect cards are on screen.</summary>
    public static FakeGraphicsSettingsScreen AdvancedOpen()
    {
        var screen = Default();
        screen.IsAdvancedOpen = true;
        return screen;
    }

    /// <summary>The scroll-speed override on, which enables the speed row under it.</summary>
    public static FakeGraphicsSettingsScreen Overridden()
    {
        var screen = Default();
        screen.HighwayScrollSpeedOverrideEnabled = true;
        screen.HighwayScrollSpeed = 0.8;
        return screen;
    }

    public IReadOnlyList<IGraphicsChoice> MaxFpsOptions { get; private init; } = [];

    public IReadOnlyList<IGraphicsChoice> PresentModeOptions { get; private init; } = [];

    public IReadOnlyList<IGraphicsChoice> MsaaOptions { get; private init; } = [];

    public double MaxVisualLeadMs => 50;

    public string HighwayVisibleSecondsLabel { get; private init; } = "2.3 s";

    public IReadOnlySet<string> HiddenKeys { get; } = new HashSet<string>();

    private static FakeGraphicsSettingsScreen With(
        string[] modes, string[] levels, int mode, int msaa, string secondsLabel = "2.3 s")
    {
        var fps = Choices(["Unlimited", "30", "60", "120", "144", "240"]);
        var modeChoices = Choices(modes);
        var msaaChoices = Choices(levels);
        return new FakeGraphicsSettingsScreen
        {
            MaxFpsOptions = fps,
            PresentModeOptions = modeChoices,
            MsaaOptions = msaaChoices,
            HighwayVisibleSecondsLabel = secondsLabel,
            SelectedMaxFps = fps[2],
            SelectedPresentMode = modeChoices[Math.Min(mode, modeChoices.Count - 1)],
            SelectedMsaa = msaaChoices[Math.Min(msaa, msaaChoices.Count - 1)]
        };
    }

    private static IReadOnlyList<IGraphicsChoice> Choices(IEnumerable<string> labels) =>
        [.. labels.Select(l => new Choice(l))];

    private sealed record Choice(string Label) : IGraphicsChoice;
}
