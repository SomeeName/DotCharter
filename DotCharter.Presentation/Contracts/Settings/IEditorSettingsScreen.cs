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

    // ── Guitar Pro import ────────────────────────────────────────────────

    bool GpImportTruncateShortNotes { get; set; }

    bool GpImportTruncateShortChords { get; set; }

    bool GpImportPreserveShortRests { get; set; }

    bool OnlineLatinNameLookup { get; set; }

    bool GpImportBeatTextTechniques { get; set; }

    bool GpImportMarkersToSections { get; set; }

    bool GpImportTimeSignatures { get; set; }

    bool GpImportDropMidBeatTempoChanges { get; set; }

    /// <summary>Frets below or above the target a slide-in starts from, 1 to 3.</summary>
    int GpImportSlideInFretDistance { get; set; }

    // ── search ───────────────────────────────────────────────────────────

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}
