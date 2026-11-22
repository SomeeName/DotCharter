namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     The editor/charting session settings the toolbar and its popovers expose,
///     saved globally so a restart brings the editor back as it was left.
///     Defaults equal the view model's own first-run values, so a config that has
///     never written this section restores to exactly what the app did before.
/// </summary>
public sealed class EditorSessionConfig
{
    public double Volume { get; set; } = 1.0;
    public double SongVolume { get; set; } = 1.0;
    public double InputVolume { get; set; } = 1.0;
    public double MidiVolume { get; set; } = 1.0;
    public double MonitorVolume { get; set; } = 1.0;
    public bool IsMasterMuted { get; set; }
    public bool IsSongMuted { get; set; }
    public bool IsSongSoloed { get; set; }
    public bool IsInputMuted { get; set; }
    public bool IsInputSoloed { get; set; }
    public bool IsMidiMuted { get; set; }
    public bool IsMidiSoloed { get; set; }
    public double Pan { get; set; }
    public double PitchShift { get; set; }

    /// <summary>MIDI out / chart synth switch (Mix popover "play the chart").</summary>
    public bool MidiEnabled { get; set; }

    /// <summary>General MIDI patch index of the built-in instrument (0..127).</summary>
    public int MidiInstrumentIndex { get; set; } = 27;

    public bool MetronomeEnabled { get; set; }
    public bool ClapsEnabled { get; set; }
    public double PlaybackSpeed { get; set; } = 1.0;
    public int GridDivisor { get; set; } = 4;

    /// <summary>Tone preview toolbar toggle (chart tones through the rig).</summary>
    public bool TonePreviewOn { get; set; }

    /// <summary>Tone preview runs the Rocksmith tones rather than the song's own.</summary>
    public bool TonePreviewInRocksmith { get; set; }
}
