// TransportViewModel: the built-in chart synth (ABI v38/v40) - the play-the-chart switch, its tooltip and the instrument route.
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.ChartSynth;

namespace DotCharter.UI.ViewModels.Editor;

// The built-in chart synth (ABI v38/v40) and the instrument route: the toggles and
// the INSTRUMENT pickers (guitar / bass model, pickup selector, coil split)
// next to the MIDI synth, in the editor's Mixer and in Play Mode's Amp/FX panel
// (both host Views/Editor/Inspector/ChartSynthInstrumentView over
// ChartSynthInstrument, its own view model). The audio service is reached with an
// `as` cast, so the null transport has no synth and everything shows disabled
// with its reason.
public partial class TransportViewModel
{
    private IChartSynthEngine? ChartSynth => _audioService as IChartSynthEngine;

    // The instrument pickers' and the EQ's labels. DI hands it in; a test transport
    // built without one shows the resx KEYS, which no test reads as English.
    private readonly DotCharter.Localization.ILocalizationService _localization;

    /// <summary>The instrument pickers and the song-tone override, bound by
    /// <c>ChartSynthInstrumentView</c> in the Mixer and in Play Mode's Amp/FX panel.</summary>
    public Inspector.ChartSynthInstrumentViewModel ChartSynthInstrument { get; }

    /// <summary>
    ///     The audio service has a chart synth (false on the null transport). Not
    ///     "an engine is up": the service records the request and replays it onto
    ///     the engine when one starts, so the toggle is honest before that too.
    /// </summary>
    public bool IsChartSynthAvailable => ChartSynth != null;

    // ---- Play the chart: the one switch's availability --------------------

    /// <summary>
    ///     The chart can SOUND now: the service has a chart synth and an engine is
    ///     up to render it (or the VST instrument). The play-the-chart switch — the
    ///     toolbar toggle, the Mixer's checkbox, the keybind and the palette entry —
    ///     is disabled without it and says why (<see cref="ChartSynthToggleTip" />):
    ///     the service would keep the request, but nothing would play, so the switch
    ///     must not look live. An engine (re)build re-reads it.
    /// </summary>
    public bool CanPlayChart => ChartSynth is { IsChartSynthAvailable: true };

    /// <summary>No voice pack is installed, so the built-in voice plays its
    /// stand-in. Not a reason to disable the switch — it does sound — but the
    /// tooltip says so. False while the model table is still loading, and with a
    /// VST instrument loaded (that plays instead).</summary>
    public bool ChartSynthPlaysStandIn =>
        !HasInstrumentVst && ChartSynthInstrument.NoVoicePackInstalled;

    /// <summary>The toolbar toggle's tooltip: a format whose <c>{0}</c> is the live
    /// keybind — what the switch does, or why it cannot.</summary>
    public string ChartSynthToggleTip => Loc(
        !CanPlayChart ? "Toolbar_ToggleChartSynth_Unavailable"
        : ChartSynthPlaysStandIn ? "Toolbar_ToggleChartSynth_StandIn"
        : "Toolbar_ToggleChartSynth_Hint");

    /// <summary>Flips <see cref="MidiEnabled" /> — the one play-the-chart switch —
    /// for the keybind and the palette. Never while the chart cannot sound.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayChart))]
    private void ToggleChartSynth()
    {
        if (CanPlayChart) MidiEnabled = !MidiEnabled;
    }

    private void RaiseChartSynthToggleState()
    {
        OnPropertyChanged(nameof(CanPlayChart));
        OnPropertyChanged(nameof(ChartSynthPlaysStandIn));
        OnPropertyChanged(nameof(ChartSynthToggleTip));
        ToggleChartSynthCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    ///     Plays the chart's notes through the guitar rig with the built-in voice.
    ///     Note detection and scoring never hear it. Switching it off releases the
    ///     voice's sample pack (the service pushes the change off the UI thread).
    /// </summary>
    public bool ChartSynthEnabled
    {
        get => ChartSynth?.IsChartSynthEnabled ?? false;
        set
        {
            var synth = ChartSynth;
            if (synth == null || synth.IsChartSynthEnabled == value) return;
            synth.SetChartSynthEnabled(value);
            OnPropertyChanged();
        }
    }

    // ---- MIDI Out ---------------------------------------------------------
    //
    // ONE switch plays the chart: MIDI Out (MidiEnabled, Tools ▸ MIDI instrument).
    // With a VST instrument chosen it plays that plugin, as it always did; without
    // one it plays the built-in guitar / bass voice (never the Windows GS Wavetable). A MIDI device picked in Settings ▸ Audio
    // (an external synth) still receives the notes alongside.

    private bool HasInstrumentVst => !string.IsNullOrEmpty(SelectedInstrumentVst?.PluginPath);

    /// <summary>The MIDI preview's note feed goes to a MIDI output device the user
    /// picked in Settings (not the default mapper / GS Wavetable). Read when Out is
    /// switched on and when the instrument changes.</summary>
    public bool PreviewsMidiToExternalDevice { get; private set; }

    /// <summary>Puts the plugin, the built-in synth and the external device in step
    /// with MIDI Out and the instrument choice.</summary>
    internal void ApplyMidiOutRouting()
    {
        var plugin = HasInstrumentVst;
        _audioService.IsVstInstrumentEnabled = MidiEnabled && plugin;
        ChartSynthEnabled = MidiEnabled && !plugin;
        PreviewsMidiToExternalDevice = MidiEnabled && !plugin && ChoosesExternalMidiDevice();
        // A loaded or dropped instrument plugin changes whether the stand-in plays.
        RaiseChartSynthToggleState();
    }

    private bool ChoosesExternalMidiDevice()
    {
        var cfg = _configService.LoadAppConfig();
        return cfg.MidiOutputDevice >= 0 &&
               !cfg.MidiOutputDeviceName.Contains("Wavetable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The loaded VST instrument can be routed into the rig — false while
    /// plugins run in the out-of-process sandbox.</summary>
    public bool CanRouteInstrumentToRig => ChartSynth?.CanRouteInstrumentToRig ?? false;

    /// <summary>Plays the VST instrument through the guitar rig instead of after the mix.</summary>
    public bool RouteInstrumentToRig
    {
        get => ChartSynth?.RouteInstrumentToRig ?? false;
        set
        {
            var synth = ChartSynth;
            if (synth == null || synth.RouteInstrumentToRig == value) return;
            synth.RouteInstrumentToRig = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The synth's tone: the player's own rig instead of the chart's tones.
    ///     Saved in the app config; <c>ToneRigViewModel</c> hands it to the editor's
    ///     tone preview, which stops (or never starts) driving the chart's tones for
    ///     the synth.
    /// </summary>
    public bool ChartSynthPlaysOwnRig
    {
        get => _configService.LoadAppConfig().ChartSynthPlaysOwnRig;
        set
        {
            var cfg = _configService.LoadAppConfig();
            if (cfg.ChartSynthPlaysOwnRig == value) return;
            cfg.ChartSynthPlaysOwnRig = value;
            _configService.SaveAppConfig(cfg);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChartSynthPlaysSongTones));
        }
    }

    /// <summary>The other half of the Synth tone switch: the chart's tones (the default).</summary>
    public bool ChartSynthPlaysSongTones
    {
        get => !ChartSynthPlaysOwnRig;
        set
        {
            if (value) ChartSynthPlaysOwnRig = false;
        }
    }

    /// <summary>Re-reads the toggles' availability (the sandbox mode can change under them).</summary>
    public void RefreshChartSynthAvailability()
    {
        OnPropertyChanged(nameof(IsChartSynthAvailable));
        OnPropertyChanged(nameof(CanRouteInstrumentToRig));
        OnPropertyChanged(nameof(RouteInstrumentToRig));
        OnPropertyChanged(nameof(ChartSynthEnabled));
        RaiseChartSynthToggleState();
        ChartSynthInstrument.Refresh();
    }

    // The pickers refreshed — the model table landed, or the instrument moved:
    // whether the voice packs are installed is known now.
    private void OnChartSynthInstrumentRefreshed()
    {
        OnPropertyChanged(nameof(ChartSynthPlaysStandIn));
        OnPropertyChanged(nameof(ChartSynthToggleTip));
    }

    private string Loc(string key) => _localization[key];
}
