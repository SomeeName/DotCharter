using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Core.Graphics;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Graphics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ Graphics. Every value applies LIVE so the user judges it on
///     screen; Save persists, and Revert / Reset reload the persisted values,
///     which also puts back what is being drawn.
/// </summary>
public sealed partial class GraphicsSettingsViewModel : ObservableObject,
    IGraphicsSettingsScreen, ISettingsTab, ISettingsChangeSource, IDisposable
{
    private static readonly int[] FpsSteps = [30, 60, 120, 144, 240];

    private readonly IConfigurationService _config;
    private readonly ProjectState _state;
    private readonly IGraphicsDeviceSettings _device;
    private readonly ILocalizationService _localization;
    private readonly ILatencyCalibrationPrompt _latencyWizard;
    private readonly IAppLogger? _logger;
    private readonly SettingsChangeSet _changes = new();

    [ObservableProperty] private double _renderScalePercent = GraphicsSettingsPolicy.DefaultRenderScalePercent;
    [ObservableProperty] private IGraphicsChoice? _selectedMaxFps;
    [ObservableProperty] private double _idleFps = GraphicsSettingsPolicy.DefaultIdleFps;
    [ObservableProperty] private IGraphicsChoice? _selectedPresentMode;
    [ObservableProperty] private IGraphicsChoice? _selectedMsaa;
    [ObservableProperty] private double _anchorPreviewLeadMs = GraphicsSettingsPolicy.DefaultAnchorPreviewLeadMs;
    [ObservableProperty] private double _visualLeadMs;
    [ObservableProperty] private bool _highwayScrollSpeedOverrideEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HighwayVisibleSecondsLabel))]
    private double _highwayScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HighwayVisibleSecondsLabel))]
    private double _highwayDrawDistanceZ = HighwaySpeedPolicy.DefaultDrawDistanceZ;

    [ObservableProperty] private double _cameraDistancePercent = 200 - GraphicsSettingsPolicy.DefaultCameraFillPercent;
    [ObservableProperty] private bool _show3DFingering;

    public GraphicsSettingsViewModel(
        IConfigurationService config,
        ProjectState state,
        IGraphicsDeviceSettings device,
        ILocalizationService localization,
        ILatencyCalibrationPrompt latencyWizard,
        IAppLogger? logger = null)
    {
        _config = config;
        _state = state;
        _device = device;
        _localization = localization;
        _latencyWizard = latencyWizard;
        _logger = logger;

        _state.PropertyChanged += OnStateChanged;
        _latencyWizard.CalibrationApplied += OnCalibrationApplied;
        TrackSettings();
        _changes.Rebaseline();
    }

    public IReadOnlyList<IGraphicsChoice> MaxFpsOptions { get; private set; } = [];

    public IReadOnlyList<IGraphicsChoice> PresentModeOptions { get; private set; } = [];

    public IReadOnlyList<IGraphicsChoice> MsaaOptions { get; private set; } = [];

    public double MaxVisualLeadMs => _device.MaxVisualLeadMs;

    public string HighwayVisibleSecondsLabel =>
        (HighwaySpeedPolicy.HorizonMs((float)HighwayScrollSpeed, (float)HighwayDrawDistanceZ) / 1000f)
        .ToString("0.0", CultureInfo.CurrentCulture) + " s";

    public string Key => SettingsTabKeys.Graphics;

    public Type? Screen => typeof(IGraphicsSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    public void Load()
    {
        var config = _config.LoadAppConfig();

        RenderScalePercent = GraphicsSettingsPolicy.ClampRenderScalePercent(config.RenderScalePercent);
        LoadChoices(config);
        IdleFps = GraphicsSettingsPolicy.ClampIdleFps(config.IdleFps);

        LoadEffects(config);
        AnchorPreviewLeadMs = GraphicsSettingsPolicy.ClampAnchorPreviewLeadMs(config.AnchorPreviewLeadMs);
        VisualLeadMs = Math.Clamp(config.VisualLeadMs, 0, _device.MaxVisualLeadMs);

        // The speed is set BEFORE the override flag so the flag's change applies the right value in one pass.
        HighwayScrollSpeed = HighwaySpeedPolicy.ClampScrollSpeed((float)config.HighwayScrollSpeed);
        HighwayScrollSpeedOverrideEnabled = config.HighwayScrollSpeedOverrideEnabled;
        HighwayDrawDistanceZ = HighwaySpeedPolicy.ClampDrawDistanceZ((float)config.HighwayDrawDistanceZ);
        CameraDistancePercent = 200 - GraphicsSettingsPolicy.ClampCameraFillPercent(config.Preview3DCameraFill);
        Show3DFingering = config.Show3DFingering;
        Rebaseline();
    }

    public void Save()
    {
        var config = _config.LoadAppConfig();

        config.RenderScalePercent = GraphicsSettingsPolicy.ClampRenderScalePercent((int)Math.Round(RenderScalePercent));
        config.MaxFps = GraphicsSettingsPolicy.ClampMaxFps(ValueOf(SelectedMaxFps));
        config.IdleFps = GraphicsSettingsPolicy.ClampIdleFps((int)Math.Round(IdleFps));
        config.PresentMode = (SelectedPresentMode as Choice)?.Id ?? "Fifo";
        config.MsaaSamples = SelectedMsaa is Choice msaa ? msaa.Value : _device.DefaultMsaaSamples;

        SaveEffects(config);
        config.AnchorPreviewLeadMs = GraphicsSettingsPolicy.ClampAnchorPreviewLeadMs(_state.AnchorPreviewLeadMs);
        config.VisualLeadMs = Math.Clamp(VisualLeadMs, 0, _device.MaxVisualLeadMs);

        // From the screen, not from ProjectState: the state carries the RESOLVED
        // speed, and writing that back would turn a chart's own value into a
        // global override.
        config.HighwayScrollSpeedOverrideEnabled = HighwayScrollSpeedOverrideEnabled;
        config.HighwayScrollSpeed = HighwaySpeedPolicy.ClampScrollSpeed((float)HighwayScrollSpeed);
        config.HighwayDrawDistanceZ = HighwaySpeedPolicy.ClampDrawDistanceZ((float)HighwayDrawDistanceZ);
        config.Preview3DCameraFill =
            GraphicsSettingsPolicy.ClampCameraFillPercent(200 - (int)Math.Round(CameraDistancePercent));
        config.Show3DFingering = Show3DFingering;

        _config.SaveAppConfig(config);
        _device.SetFingeringOverlay(config.Show3DFingering);
        _logger?.Info(AppLogCategory.Settings,
            $"Graphics settings applied. RenderScale={config.RenderScalePercent}%, maxFps={config.MaxFps}, " +
            $"idleFps={config.IdleFps}, presentMode={config.PresentMode}, msaa={config.MsaaSamples}x.");
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
        _state.PropertyChanged -= OnStateChanged;
        _latencyWizard.CalibrationApplied -= OnCalibrationApplied;
    }

    /// <summary>
    ///     The latency wizard writes the lead to the config itself when the player
    ///     accepts a result. Without this the slider keeps its pre-wizard value and
    ///     the next Save puts the stale number back over the calibrated one.
    /// </summary>
    private void OnCalibrationApplied(object? sender, double lead) =>
        VisualLeadMs = Math.Clamp(lead, 0, _device.MaxVisualLeadMs);

    private void LoadChoices(AppConfig config)
    {
        MaxFpsOptions = [new Choice(_localization["Settings_Graphics_MaxFps_Unlimited"], 0, ""),
            .. FpsSteps.Select(f => new Choice(f.ToString(CultureInfo.CurrentCulture), f, ""))];
        OnPropertyChanged(nameof(MaxFpsOptions));
        SelectedMaxFps = MaxFpsOptions.Cast<Choice>().FirstOrDefault(o => o.Value == config.MaxFps) ?? MaxFpsOptions[0];

        // Only the modes the device reports, PLUS the persisted one when it is
        // not among them: before the device initialises the list is FIFO only,
        // and a Save would otherwise clobber a persisted Mailbox or Immediate.
        var modes = _device.SupportedPresentModes.Select(id => new Choice(PresentModeName(id), 0, id)).ToList();
        if (!string.IsNullOrEmpty(config.PresentMode) &&
            !modes.Any(m => string.Equals(m.Id, config.PresentMode, StringComparison.OrdinalIgnoreCase)))
        {
            modes.Add(new Choice(PresentModeName(config.PresentMode), 0, config.PresentMode));
        }

        PresentModeOptions = modes;
        OnPropertyChanged(nameof(PresentModeOptions));
        SelectedPresentMode = modes.FirstOrDefault(m =>
            string.Equals(m.Id, config.PresentMode, StringComparison.OrdinalIgnoreCase)) ?? modes.FirstOrDefault();

        var levels = _device.SupportedMsaaSamples.Select(s => new Choice(
            s == 1 ? _localization["Settings_Graphics_Msaa_Off"] : $"{s}×", s, "")).ToList();
        MsaaOptions = levels;
        OnPropertyChanged(nameof(MsaaOptions));
        SelectedMsaa = levels.FirstOrDefault(l => l.Value == config.MsaaSamples)
                       ?? levels.FirstOrDefault(l => l.Value == _device.DefaultMsaaSamples)
                       ?? levels.LastOrDefault();
    }

    private string PresentModeName(string id) => id switch
    {
        "Mailbox" => _localization["Settings_Graphics_PresentMode_Mailbox"],
        "Immediate" => _localization["Settings_Graphics_PresentMode_Immediate"],
        _ => _localization["Settings_Graphics_PresentMode_Fifo"]
    };

    private static int ValueOf(IGraphicsChoice? choice) => (choice as Choice)?.Value ?? 0;

    private void ApplyHighwayScrollSpeed() =>
        _state.HighwayScrollSpeed = HighwaySpeedPolicy.ResolveScrollSpeed(
            perRun: null,
            globalOverride: HighwayScrollSpeedOverrideEnabled ? (float)HighwayScrollSpeed : null,
            chartValue: (float?)_state.ActiveArrangement?.ScrollSpeed);

    partial void OnRenderScalePercentChanged(double value) => _device.SetRenderScalePercent((int)Math.Round(value));

    partial void OnSelectedMaxFpsChanged(IGraphicsChoice? value)
    {
        if (value is not null) _device.SetMaxFps(ValueOf(value));
    }

    partial void OnIdleFpsChanged(double value) => _device.SetIdleFps((int)Math.Round(value));

    partial void OnSelectedPresentModeChanged(IGraphicsChoice? value)
    {
        if (value is Choice mode) _device.SetPresentMode(mode.Id);
    }

    partial void OnSelectedMsaaChanged(IGraphicsChoice? value)
    {
        if (value is Choice level) _device.SetMsaaSamples(level.Value);
    }

    partial void OnAnchorPreviewLeadMsChanged(double value) =>
        _state.AnchorPreviewLeadMs = GraphicsSettingsPolicy.ClampAnchorPreviewLeadMs((int)Math.Round(value));

    partial void OnVisualLeadMsChanged(double value) => _device.SetVisualLeadTrimMs(value);

    partial void OnHighwayScrollSpeedOverrideEnabledChanged(bool value) => ApplyHighwayScrollSpeed();

    partial void OnHighwayScrollSpeedChanged(double value) => ApplyHighwayScrollSpeed();

    partial void OnHighwayDrawDistanceZChanged(double value) =>
        _state.HighwayDrawDistanceZ = HighwaySpeedPolicy.ClampDrawDistanceZ((float)value);

    partial void OnCameraDistancePercentChanged(double value) =>
        _state.Preview3DCameraFill = GraphicsSettingsPolicy.ClampCameraFillPercent(200 - (int)Math.Round(value));

    // Live like every other row on this screen: a flip must show on the 3D
    // view at once, not after Save.
    partial void OnShow3DFingeringChanged(bool value) => _device.SetFingeringOverlay(value);

    private sealed record Choice(string Label, int Value, string Id) : IGraphicsChoice;
}
