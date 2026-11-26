using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Services.Parsing.GuitarPro;

public class GpTrackInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class GpImportMetadata
{
    public List<GpBarInfo> Bars { get; } = [];
    public List<GpTempoPoint> TempoPoints { get; } = [];
    public int KeyAccidentals { get; set; }
    public bool KeyIsMinor { get; set; }

    internal ChartData ChordNamingChart() => new() { KeyAccidentals = KeyAccidentals, KeyIsMinor = KeyIsMinor };

    // Score-level song metadata (gpif <Score>, GP3-5 song-attributes header).
    // Both GP importers fill these; the import dialog copies a NON-EMPTY value
    // onto the project's ChartData (SongName/Artist/AlbumName/Charter — all
    // fields the .dcproj round-trips). Empty = the file did not carry it.
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";

    /// <summary>The "tabbed by" credit (gpif <c>&lt;Tabber&gt;</c>, binary
    /// header "tab" field) — maps onto <c>ChartData.Charter</c>.</summary>
    public string Tabber { get; set; } = "";
}

public class GpBarInfo
{
    public int BeatsInMeasure { get; set; } = 4;
    public int NoteDenominator { get; set; } = 4;
    public string TripletFeel { get; set; } = "";

    /// <summary>The AUTHORED master bar this (unfolded) bar plays: two bars with the same
    /// index are one written bar played twice through a repeat. -1 = not known.</summary>
    public int SourceBarIndex { get; set; } = -1;

    /// <summary>Which pass of its repeat this bar is (1 = the first time through).</summary>
    public int RepeatPass { get; set; } = 1;

    public int EndingMask { get; set; }
    public bool StartsBlock { get; set; }
    public bool EndsBlock { get; set; }
    public string Marker { get; set; } = "";
}

public class GpTempoPoint
{
    public int BarIndex { get; set; }

    // Fractional position within the bar in range [0, 1).
    public double BarPosition { get; set; }
    public double Bpm { get; set; }
}

public class GpImportResult
{
    public List<Arrangement> Arrangements { get; } = [];
    /// <summary>Source track ids survive filtering and duplicate display names.</summary>
    public Dictionary<int, Arrangement> ArrangementsByTrackId { get; } = [];
    public GpImportMetadata Metadata { get; } = new();
    internal HashSet<IGuitarSound> DeadSlideSounds { get; } = [];

    /// <summary>The sounds a grace ornament was placed against without the tab stating before or on the beat (D110).</summary>
    internal HashSet<IGuitarSound> GraceTimingGuesses { get; } = [];

    /// <summary>
    ///     Per arrangement, how many slide-in ornaments became real slides — a short
    ///     origin sound that slides into the authored note
    ///     (<see cref="GpPostProcessor.MaterializeSlideIns"/>). That origin is
    ///     indistinguishable from an authored slide in the chart, so this tally is
    ///     the only witness the tab-corpus census has for the ornament.
    /// </summary>
    public Dictionary<Arrangement, int> SlideInsMaterialized { get; } = [];

    /// <summary>Per arrangement, the hand shapes wider than four frets: kept, but named
    /// in the import report instead of passing silently.</summary>
    public Dictionary<Arrangement, int> WideHandShapes { get; } = [];

    /// <summary>Per arrangement, the source notes the import could not place and dropped:
    /// a string the track does not have, or a negative fret.</summary>
    public Dictionary<Arrangement, int> DroppedNotes { get; } = [];
}
