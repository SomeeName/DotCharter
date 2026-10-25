using System.ComponentModel;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Editor: the rules the chart editor applies while you edit,
///     what a normal paste brings in, the defaults a new project starts with,
///     and how a Guitar Pro file is imported. Saving re-applies the minimum
///     note distance to the open project.
/// </summary>
public interface IEditorSettingsScreen : INotifyPropertyChanged
{
    // ── chart editing ────────────────────────────────────────────────────

    bool UseFastChartingInput { get; set; }

    bool SyncHandShapeToChordTemplate { get; set; }

    bool SyncAnchorToChordFingering { get; set; }

    bool AutoNameNewChords { get; set; }

    bool SuppressDoubleStopChordNames { get; set; }

    bool KeepPowerChordNames { get; set; }

    /// <summary>
    ///     Spell suggested chord names in the song's key with slash chords
    ///     (<c>Bbm7</c>, <c>Cm7/Bb</c>) instead of Editor on Fire's spelling
    /// (<c>A#min7</c>). Off by default.
    /// </summary>
    bool MusicalChordNames { get; set; }

    /// <summary>How many frets below a tapped harmonic the tapped-harmonic shortcut puts the
    /// hand's ghost fret; 12 is the octave.</summary>
    int TappedHarmonicFretOffset { get; set; }

    bool LyricSpanWordGap { get; set; }

    bool LyricSpanPhraseGap { get; set; }

    /// <summary>0 milliseconds, 1 a fraction of a measure, 2 a fraction of a beat.</summary>
    int MinNoteDistanceModeIndex { get; set; }

    /// <summary>String order: 0 Rocksmith (low string on top), 1 Tab (low string at the bottom). Drives the 2D timeline, its
    /// string-move keys and the editor's 3D preview, and applies as it is picked.</summary>
    int StringOrderIndex { get; set; }

    /// <summary>The 2D timeline's polish pass.</summary>
    bool Timeline2DPolish { get; set; }

    /// <summary>Each string's lane tinted in its own colour.</summary>
    bool Timeline2DLaneTint { get; set; }

    /// <summary>True in the milliseconds mode: the millisecond row applies and the denominator does not.</summary>
    bool IsMinNoteDistanceInMilliseconds { get; }

    double MinNoteDistanceMs { get; set; }

    int MinNoteDistanceDivisor { get; set; }

    // ── copy and paste ───────────────────────────────────────────────────

    bool PasteIncludeNotes { get; set; }

    bool PasteIncludeChords { get; set; }

    bool PasteIncludeHandShapes { get; set; }

    bool PasteIncludeFretAnchors { get; set; }

    bool PasteIncludeEvents { get; set; }

    bool PasteIncludeTones { get; set; }

    // ── new project ──────────────────────────────────────────────────────

    string DefaultCharterName { get; set; }

    bool PadSilenceOnNewProject { get; set; }

    double LeadingSilenceSeconds { get; set; }

    double TrailingSilenceSeconds { get; set; }

    // ── search ───────────────────────────────────────────────────────────

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}
