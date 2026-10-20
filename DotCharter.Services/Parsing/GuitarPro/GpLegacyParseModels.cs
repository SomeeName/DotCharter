namespace DotCharter.Services.Parsing.GuitarPro;

// Internal DTO types used by GpLegacyImporter while parsing a binary
// .gp3/.gp4/.gp5 file. Kept as their own file so tests can construct them
// directly without spinning up an importer instance.

internal sealed class GpLegacyTrackHeader
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int StringCount { get; set; }
    public int[] Tuning { get; set; } = new int[7];
    public int Capo { get; set; }

    /// <summary>The header's fret count, raw: read through <see cref="GpPickScrape.LastFret" />.</summary>
    public int Frets { get; set; }

    public bool IsDrums { get; set; }
}

internal sealed class GpLegacyMeasureHeader
{
    public int Numerator { get; set; } = 4;
    public int Denominator { get; set; } = 4;

    /// <summary>
    /// The bar's swing feel, spelled with the SAME vocabulary the GPIF
    /// <c>&lt;TripletFeel&gt;</c> element uses ("Triplet8th" / "Triplet16th"),
    /// so <see cref="GpBarInfo.TripletFeel"/> means one thing no matter which
    /// importer filled it. Empty string = no feel (GP's "none").
    /// </summary>
    public string TripletFeel { get; set; } = "";

    /// <summary>
    ///     The bar's repeat markers, spelled exactly as
    ///     <see cref="GpMasterBarSequence.BarRepeat" /> wants them so the binary
    ///     path unfolds through the SAME code the gpif path does. The reader
    ///     must not consume these three bits and discard them, which would import a
    ///     <c>|: 8 bars :|</c> eight bars short.
    ///     <para><see cref="RepeatCount" /> is the TOTAL number of passes
    ///     (GP3/4's byte counts the extra ones, so the reader adds 1);
    ///     <see cref="AlternateEndingsMask" /> is a bitmask over passes with
    ///     bit 0 = pass 1.</para>
    /// </summary>
    public bool RepeatStart { get; set; }

    /// <inheritdoc cref="RepeatStart" />
    public bool RepeatEnd { get; set; }

    /// <inheritdoc cref="RepeatStart" />
    public int RepeatCount { get; set; }

    /// <inheritdoc cref="RepeatStart" />
    public int AlternateEndingsMask { get; set; }
}

internal sealed class GpLegacyChannelInfo
{
    public int Patch { get; set; }
    public int Volume { get; set; }
    public int Pan { get; set; }
}

internal sealed class GpLegacyParseResult
{
    public List<GpTrackInfo> Tracks { get; set; } = [];
    public List<GpBarInfo> Bars { get; } = [];
    public List<GpTempoPoint> TempoPoints { get; } = [];
    public int KeyAccidentals { get; set; }
    public bool KeyIsMinor { get; set; }

    // Song-attributes header metadata (title/subtitle/artist/album/…/tab).
    // The reader does not DISCARD these strings; they travel
    // into GpImportMetadata so an import can fill the project's Song Metadata.
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Tabber { get; set; } = "";
}

/// <summary>
/// Where one GP3-5 beat lands: its track, voice lane and level, the bar it sits
/// in, its onset, and the lists its sounds go to — the level's for lane 0 (the
/// playable chart), a notation-only sidecar's for lane 1.
/// </summary>
internal readonly record struct GpLegacyBeatContext(
    GpLegacyTrackHeader Track,
    DotCharter.Core.Domain.Project.ChartLevel Level,
    int Voice,
    int MeasureIndex,
    FractionalPosition MeasureStart,
    Fraction MeasureLength,
    FractionalPosition Position,
    IList<DotCharter.Core.Domain.Guitar.GuitarNote> Notes,
    IList<DotCharter.Core.Domain.Guitar.Chord> Chords)
{
    /// <summary>Lane 0 builds the chart: let-ring, trills, beat-text techniques,
    /// authored fingers and chord names. The sidecar lane is notation only.</summary>
    public bool IsPlayable => Voice == 0;
}
