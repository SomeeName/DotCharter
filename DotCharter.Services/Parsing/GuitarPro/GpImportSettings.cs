namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     The tab-import options threaded through every shared import entry point
///     (gpif <see cref="GpArrangementParser"/>, legacy binary
///     <see cref="GpLegacyImporter"/>, <see cref="GpPostProcessor"/> and the
///     Songsterr converter). Carries the Editor-on-Fire import settings ported
///     from EOF. Built from
///     <see cref="AppConfig"/> via <see cref="FromConfig"/>; <see cref="Default"/>
///     carries EOF's defaults and is used whenever a caller passes none.
///     <para>
///     The time-signature / tempo options (<c>GpImportTimeSignatures</c>,
///     <c>GpImportDropMidBeatTempoChanges</c>) are NOT here — they are consumed
///     when the UI turns <see cref="GpImportMetadata"/> into a beat map
///     (<see cref="GpBeatMapBuilder"/>), not inside the note/chord importers.
///     </para>
/// </summary>
public sealed record GpImportSettings
{
    /// <summary>Drop the sustain of a single note authored shorter than a quarter
    /// note (EOF <c>truncate_short_notes</c>). Default on.</summary>
    public bool TruncateShortNotes { get; init; } = true;

    /// <summary>Drop the sustain of a chord authored shorter than a quarter note
    /// (EOF <c>truncate_short_chords</c>). Default OFF — the handshape convention
    /// already carries chord duration.</summary>
    public bool TruncateShortChords { get; init; }

    /// <summary>Honour the SHORT RESTS a tab writes between repeated attacks —
    /// Rocksmith cannot spell them. A note followed by a rest of at most its
    /// own value (and at most a quarter) imports ACCENTED, the charting
    /// convention for staccato; a rest between two identical CHORDS breaks their
    /// shared hand-shape bar instead of being swallowed by it. Default on.</summary>
    public bool PreserveShortRests { get; init; } = true;

    /// <summary>Map a "T"/"S"/"P" beat text to a tap / slap / pop technique on the
    /// beat's note. Default on.</summary>
    public bool BeatTextTechniques { get; init; } = true;

    /// <summary>Import section markers (and qualifying beat texts) as RS phrases +
    /// section EventPoints. Default OFF. gpif only.</summary>
    public bool MarkersToSections { get; init; }

    /// <summary>How far below/above the target a materialized slide-in's origin
    /// note sits when the file names no origin fret (GP <c>Slide</c> 16/32).
    /// Default <see cref="GpPostProcessor.DefaultSlideInFretDistance"/> (2, Log
    /// Charter's choice); EOF uses 1. Clamped to 1–3 by the materializer.</summary>
    public int SlideInFretDistance { get; init; } = GpPostProcessor.DefaultSlideInFretDistance;

    /// <summary>EOF defaults, used when a caller passes no settings.</summary>
    public static GpImportSettings Default { get; } = new();

    /// <summary>Projects the persisted <see cref="AppConfig"/> flags onto the
    /// importer-facing settings.</summary>
    public static GpImportSettings FromConfig(AppConfig config) => new()
    {
        TruncateShortNotes = config.GpImportTruncateShortNotes,
        TruncateShortChords = config.GpImportTruncateShortChords,
        PreserveShortRests = config.GpImportPreserveShortRests,
        BeatTextTechniques = config.GpImportBeatTextTechniques,
        MarkersToSections = config.GpImportMarkersToSections,
        SlideInFretDistance = config.GpImportSlideInFretDistance
    };
}
