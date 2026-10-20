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

    // Score-level song metadata (gpif <Score>, GP3-5 song-attributes header).
    // Both GP importers fill these; the import dialog copies a NON-EMPTY value
    // onto the project's ChartData (SongName/Artist/AlbumName/Charter — all
    // fields the .rscp round-trips). Empty = the file did not carry it.
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
    public GpImportMetadata Metadata { get; } = new();
}
