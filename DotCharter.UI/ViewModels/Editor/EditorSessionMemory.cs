// The editor's session switches - saved on change, restored by the TransportViewModel constructor so a restart brings the editor back as it was left.
using DotCharter.UI.Constants;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     Reads and writes <see cref="AppConfig.EditorSession" /> for the
///     <see cref="TransportViewModel" />: the mix levels with their mute/solo, pan and
///     pitch, the MIDI out switch and its instrument, the metronome and claps, the
///     playback speed, the grid snap and the tone preview toggle. Writes ride
///     <see cref="IConfigurationService.Update" />, whose file write is coalesced, so a
///     slider drag costs one memory update per step and one disk write per burst. The
///     project-specific half (active arrangement, ghost and stack selections, playhead)
///     is <c>ProjectViewMemory</c>.
/// </summary>
internal sealed class EditorSessionMemory(IConfigurationService config)
{
    private static readonly HashSet<string> Properties =
    [
        nameof(TransportViewModel.Volume), nameof(TransportViewModel.SongVolume), nameof(TransportViewModel.InputVolume),
        nameof(TransportViewModel.MidiVolume), nameof(TransportViewModel.MonitorVolume),
        nameof(TransportViewModel.IsMasterMuted), nameof(TransportViewModel.IsSongMuted),
        nameof(TransportViewModel.IsSongSoloed), nameof(TransportViewModel.IsInputMuted),
        nameof(TransportViewModel.IsInputSoloed), nameof(TransportViewModel.IsMidiMuted),
        nameof(TransportViewModel.IsMidiSoloed), nameof(TransportViewModel.Pan), nameof(TransportViewModel.PitchShift),
        nameof(TransportViewModel.MidiEnabled), nameof(TransportViewModel.SelectedMidiInstrumentIndex),
        nameof(TransportViewModel.MetronomeEnabled), nameof(TransportViewModel.ClapsEnabled),
        nameof(TransportViewModel.PlaybackSpeed), nameof(TransportViewModel.GridDivisor),
    ];

    public static bool Remembers(string? propertyName) => propertyName != null && Properties.Contains(propertyName);

    public void Restore(TransportViewModel t)
    {
        var s = config.LoadAppConfig().EditorSession ?? new EditorSessionConfig();
        t.Volume = Math.Clamp(s.Volume, 0.0, 1.5);
        t.SongVolume = Math.Clamp(s.SongVolume, 0.0, 1.5);
        t.InputVolume = Math.Clamp(s.InputVolume, 0.0, 2.0);
        t.MidiVolume = Math.Clamp(s.MidiVolume, 0.0, 2.0);
        t.MonitorVolume = Math.Clamp(s.MonitorVolume, 0.0, 1.5);
        t.IsMasterMuted = s.IsMasterMuted;
        t.IsSongMuted = s.IsSongMuted;
        t.IsSongSoloed = s.IsSongSoloed;
        t.IsInputMuted = s.IsInputMuted;
        t.IsInputSoloed = s.IsInputSoloed;
        t.IsMidiMuted = s.IsMidiMuted;
        t.IsMidiSoloed = s.IsMidiSoloed;
        t.Pan = Math.Clamp(s.Pan, -1.0, 1.0);
        t.PitchShift = s.PitchShift;
        t.MetronomeEnabled = s.MetronomeEnabled;
        t.ClapsEnabled = s.ClapsEnabled;
        t.PlaybackSpeed = Math.Clamp(s.PlaybackSpeed, ChartEditorConstants.PlaybackSpeedMin, ChartEditorConstants.PlaybackSpeedMax);
        if (s.GridDivisor > 0) t.GridDivisor = s.GridDivisor;
        if (s.MidiInstrumentIndex >= 0 && s.MidiInstrumentIndex < t.MidiInstruments.Count)
        {
            t.SelectedMidiInstrumentIndex = s.MidiInstrumentIndex;
            t.SelectedMidiInstrumentName = t.MidiInstruments[s.MidiInstrumentIndex];
        }

        // Last: it routes through the instrument and the chart synth.
        t.MidiEnabled = s.MidiEnabled;
    }

    public void Persist(TransportViewModel t) => config.Update(c =>
    {
        var s = c.EditorSession ??= new EditorSessionConfig();
        s.Volume = t.Volume;
        s.SongVolume = t.SongVolume;
        s.InputVolume = t.InputVolume;
        s.MidiVolume = t.MidiVolume;
        s.MonitorVolume = t.MonitorVolume;
        s.IsMasterMuted = t.IsMasterMuted;
        s.IsSongMuted = t.IsSongMuted;
        s.IsSongSoloed = t.IsSongSoloed;
        s.IsInputMuted = t.IsInputMuted;
        s.IsInputSoloed = t.IsInputSoloed;
        s.IsMidiMuted = t.IsMidiMuted;
        s.IsMidiSoloed = t.IsMidiSoloed;
        s.Pan = t.Pan;
        s.PitchShift = t.PitchShift;
        s.MidiEnabled = t.MidiEnabled;
        s.MidiInstrumentIndex = t.SelectedMidiInstrumentIndex;
        s.MetronomeEnabled = t.MetronomeEnabled;
        s.ClapsEnabled = t.ClapsEnabled;
        s.PlaybackSpeed = t.PlaybackSpeed;
        s.GridDivisor = t.GridDivisor;
    });

    /// <summary>What the tone preview toggle was left at.</summary>
    public (bool On, bool Rocksmith) SavedTonePreview
    {
        get
        {
            var s = config.LoadAppConfig().EditorSession;
            return (s?.TonePreviewOn == true, s?.TonePreviewInRocksmith == true);
        }
    }

    public void RememberTonePreview(bool on, bool rocksmith) => config.Update(c =>
    {
        var s = c.EditorSession ??= new EditorSessionConfig();
        s.TonePreviewOn = on;
        s.TonePreviewInRocksmith = rocksmith;
    });
}
