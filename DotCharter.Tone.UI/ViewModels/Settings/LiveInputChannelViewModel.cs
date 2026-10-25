using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.Tone.UI.ViewModels.Settings;

/// <summary>
///     The guitar-input JACK picker that lives where the amp is picked — the Amp
///     &amp; FX rig's footer — and applies immediately.
///     <para>
///         <b>Same setting, same writer.</b> This edits
///         <c>AppConfig.AsioInputChannelLeft</c> / <c>Right</c> through
///         <see cref="GuitarInputChannels.SelectJackForSeat" />, saves, and hands
///         the config to <c>IAudioDeviceControl.ApplyAudioConfiguration</c> —
///         the path Settings ▸ Audio's Save and the onboarding wizard's Apply
///         already take, and the ONLY place the live seat baseline is written
///         (<c>LiveInputSeatLayout.FromInputConfig</c>). It is a third door onto
///         one field, not a second copy of it.
///     </para>
///     <para>
///         Ships DISABLED WITH ITS REASON while input is off. On ASIO and WASAPI
///         alike the engine opens the picked jack as an input channel mask, and
///         the jack list is the engine driver's capture device's.
///     </para>
/// </summary>
public partial class LiveInputChannelViewModel : ViewModelBase
{
    private readonly IAudioDeviceControl? _audio;
    private readonly IRigConfigService? _config;
    private readonly IUiDispatcher? _ui;
    private readonly ILocalizationService _loc;
    private bool _loading;

    public LiveInputChannelViewModel(IAudioDeviceControl? audio, IRigConfigService? config,
        ILocalizationService localization, IUiDispatcher? ui = null)
    {
        _audio = audio;
        _config = config;
        _loc = localization;
        _ui = ui;
        Refresh();
    }

    /// <summary>Every jack of the current capture device, plus the configured
    /// ones — <see cref="GuitarInputChannels.JackOptions" />.</summary>
    public ObservableCollection<InputJackOption> Options { get; } = [];

    /// <summary>The jack this seat's guitar is on. Setting it applies at once.</summary>
    [ObservableProperty] private InputJackOption? _selected;

    /// <summary>The seat the picker addresses — follows the rig's seat picker.
    /// Seat 0 is the "left" box, seat 1 the "right" one.</summary>
    [ObservableProperty] private int _seat;

    /// <summary>False when the jack cannot be chosen; <see cref="Hint" /> says why.</summary>
    [ObservableProperty] private bool _isAvailable;

    /// <summary>The tooltip: what the picker does, or why it is disabled.</summary>
    [ObservableProperty] private string _hint = string.Empty;

    /// <summary>Why the picker is disabled, in the few words the footer's 80 px
    /// cell holds ("Input off"); <see cref="Hint" /> is its tooltip.
    /// Empty while the picker is available.</summary>
    [ObservableProperty] private string _shortHint = string.Empty;

    /// <summary>How many times a pick reached the engine — the test's evidence
    /// that a selection is a re-apply, not a saved number nothing reads.</summary>
    public int AppliedCount { get; private set; }

    /// <summary>
    ///     Re-reads the config and rebuilds the rows. The cheap half (config +
    ///     a generic jack count) lands synchronously so the screen is right on
    ///     its first frame; the device's real jack count is a JUCE enumeration
    ///     (a USB round trip, 200+ ms) and is fetched off-thread when a
    ///     dispatcher is available, then merged in.
    /// </summary>
    public void Refresh()
    {
        if (_config == null || _audio == null)
        {
            Options.Clear();
            Selected = null;
            IsAvailable = false;
            Hint = _loc["Rig_Input_Unavailable_Off"];
            ShortHint = _loc["Rig_Input_Unavailable_Off_Short"];
            return;
        }

        var config = _config.LoadRigConfig();
        Rebuild(config, deviceJackCount: 0, []);
        if (!IsAvailable) return;

        var driver = config.AudioDriver;
        var name = string.Equals(driver, "ASIO", StringComparison.OrdinalIgnoreCase)
            ? config.AsioInputDeviceName
            : config.WasapiInputDeviceName;
        if (_ui == null)
        {
            Rebuild(config, _audio.GetInputDeviceChannelCount(driver, name), _audio.GetInputChannelNames(driver, name));
            return;
        }

        _ = Task.Run(() =>
        {
            int count;
            IReadOnlyList<string> names;
            try
            {
                count = _audio.GetInputDeviceChannelCount(driver, name);
                names = _audio.GetInputChannelNames(driver, name);
            }
            catch
            {
                return;
            }

            if (count <= 0) return;
            _ui.Post(() => Rebuild(_config.LoadRigConfig(), count, names));
        });
    }

    private void Rebuild(RigConfig config, int deviceJackCount, IReadOnlyList<string> channelNames)
    {
        _loading = true;
        try
        {
            Options.Clear();
            if (!config.EnableInput)
            {
                Selected = null;
                IsAvailable = false;
                Hint = _loc["Rig_Input_Unavailable_Off"];
                ShortHint = _loc["Rig_Input_Unavailable_Off_Short"];
                return;
            }

            foreach (var o in GuitarInputChannels.JackOptions(_loc, deviceJackCount, channelNames,
                         config.AsioInputChannelLeft, config.AsioInputChannelRight))
            {
                Options.Add(o);
            }

            var jack = GuitarInputChannels.JackForSeat(config, Seat);
            Selected = Options.FirstOrDefault(o => o.Jack == jack);
            IsAvailable = true;
            Hint = _loc["Rig_Input_Tip"];
            ShortHint = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSeatChanged(int value)
    {
        _ = value;
        Refresh();
    }

    partial void OnSelectedChanged(InputJackOption? value)
    {
        if (_loading || value == null || _config == null || _audio == null) return;
        var changed = false;
        _config.UpdateRigConfig(c => changed = GuitarInputChannels.SelectJackForSeat(c, Seat, value.Jack));
        if (!changed) return;
        // The engine re-opens the input with the new mask and, through
        // ApplyAudioConfigurationCore, re-derives the live seat baseline — the
        // one writer of it. Nothing here touches the seat layout directly.
        _audio.ApplyAudioConfiguration(_config.LoadRigConfig());
        AppliedCount++;
    }
}
