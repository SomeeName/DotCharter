using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Services.Parsing.Eof;

public class EofTextEventInfo
{
    public string Text { get; set; } = "";
    public uint BeatPos { get; set; }
    public bool IsMs { get; set; }   // FLOATING_POS — pos is milliseconds rather than beat index
    public ushort Track { get; set; }
    public ushort Flags { get; set; }
}

public class EofTrackInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Format { get; set; }

    /// <summary>
    ///     Note count discovered during the track-listing pre-pass. Lets the import
    ///     dialog hide arrangements that would import as completely empty
    ///     (EOF often saves stub pro guitar tracks for tunings that aren't charted).
    /// </summary>
    public int NoteCount { get; set; }
}

public class EofBeatInfo
{
    public double TimeMs { get; set; }
    public double Bpm { get; set; } = 120.0;
    public int BeatsInMeasure { get; set; } = 4;
    public int NoteDenominator { get; set; } = 4;
    public bool IsMeasureStart { get; set; }

    /// <summary>EOF_BEAT_FLAG_ANCHOR — the beat's timing was explicitly pinned by the charter.</summary>
    public bool IsAnchor { get; set; }
}

public class EofImportMetadata
{
    public List<EofBeatInfo> Beats { get; } = [];
    public List<EofTextEventInfo> TextEvents { get; } = [];
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public string Year { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Charter { get; set; } = "";
    public uint Revision { get; set; }
    public uint TimeDivision { get; set; }

    /// <summary>
    ///     Filename of the first OGG profile in the project (typically <c>guitar.ogg</c>).
    ///     Resolved relative to the project file's directory by the importer.
    /// </summary>
    public string AudioFileName { get; set; } = "";

    /// <summary>
    ///     The first OGG profile's MIDI offset in ms. EOF's invariant: this IS
    ///     the first beat's position (beat.c re-bases the whole beat map on it).
    /// </summary>
    public uint MidiOffsetMs { get; set; }
}

public class EofImportResult
{
    public List<Arrangement> Arrangements { get; } = [];
    public EofImportMetadata Metadata { get; } = new();
}
