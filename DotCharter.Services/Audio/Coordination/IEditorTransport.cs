namespace DotCharter.Services.Audio.Coordination;

/// <summary>
///     The editor transport's state and verbs the Services-side coordinators use:
///     the click/clap/MIDI toggles the playback scheduler reads each tick, the
///     project BPM, and the seek/pause/sync verbs. Implemented by the UI's
///     <c>TransportViewModel</c>.
/// </summary>
public interface IEditorTransport
{
    double Bpm { get; set; }

    /// <summary>The editor grid's subdivisions per beat (4 = 16ths).</summary>
    int GridDivisor { get; }
    bool MetronomeEnabled { get; }
    bool ClapsEnabled { get; }

    /// <summary>The Play Mode run's metronome flag, armed by the shell for the run.</summary>
    bool GameplayMetronomeActive { get; }

    /// <summary>The Play Mode run's clap flag, armed by the shell for the run.</summary>
    bool GameplayClapsActive { get; }

    /// <summary>MIDI Out also feeds a MIDI output device picked in Settings (an
    /// external synth). The plugin and the built-in synth are the engine's own
    /// sinks; this is the one the per-tick MIDI preview exists for.</summary>
    bool PreviewsMidiToExternalDevice { get; }
    bool VstEnabled { get; }

    void SeekTo(double timeMs);

    /// <summary>Pauses playback and updates the bound playing state. UI thread only.</summary>
    void Pause();

    /// <summary>Puts the bound playing flag back in step with the engine. UI thread only.</summary>
    void SyncPlayingStateFromEngine();
}
