using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Domain.Project;

public class ChartData
{
    // Metadata sentinels are EMPTY, never human-readable placeholders or
    // whitespace padding. A blank default is what every downstream blank check
    // (RsXmlSongWriter.FirstNonBlank, RocksmithPackageBuilder, RscpWriter)
    // expects; "Unknown Artist" / a run of spaces silently PASS those checks
    // and get exported verbatim — that is how <albumName> shipped 19 literal
    // spaces and <artistNameSort> shipped "Unknown Artist" next to a real
    // <artistName>. Consumers that want a placeholder apply their own.
    public string AlbumName { get; set; } = "";
    public string AlbumYear { get; set; } = "";
    public int ArrangementId { get; set; }
    public string Artist { get; set; } = "";
    public string ArtistSort { get; set; } = "";

    /// <summary>
    ///     The song's title and artist in their ORIGINAL script, kept alongside
    ///     the Latin ones rather than instead of them. Rocksmith carries exactly
    ///     this pair (<c>JapaneseSongName</c> / <c>JapaneseArtistName</c> in the
    ///     manifest) and DLC Builder edits it, so a psarc round-trip would
    ///     throw it away in both directions.
    ///     <para>
    ///     There is deliberately NO Japanese album name: the Rocksmith manifest
    ///     has no field for one, so storing it would be a value we could never
    ///     write out.
    ///     </para>
    ///     <para>
    ///     Named "Japanese" after the manifest attributes, not after the script —
    ///     nothing here is Japanese-specific and any non-Latin original belongs
    ///     in it. Empty is the sentinel, like every other field on this type.
    ///     </para>
    /// </summary>
    public string JapaneseTitle { get; set; } = "";

    public string JapaneseArtist { get; set; } = "";
    public double BeatsPerMinute { get; set; } = 120.0;
    public int ChartFormatVersion { get; set; } = 3;
    public string EditMode { get; set; } = "GUITAR";
    public int Level { get; set; }
    public double MainAudioOffsetAdded { get; set; }
    public int BarsAdded { get; set; }
    public int SelectedStem { get; set; } = -1;
    public string SongName { get; set; } = "Untitled Song";
    public string Charter { get; set; } = "";
    public string Genre { get; set; } = "";
    public string ReleaseDate { get; set; } = "";

    /// <summary>
    ///     The song's package version (DLC Builder's <c>project.Version</c>): "1" until the
    ///     charter bumps it. Written to the package's <c>toolkit.version</c> and into the
    ///     release file name. Saved as <c>&lt;dcVersion&gt;</c>, absent at "1"; blank reads as "1".
    /// </summary>
    public string Version { get; set; } = "1";

    // Embedded cover art, stored as a path relative to the project folder (like
    // AudioFileName). Empty when the project has no cover art.
    public string CoverArtPath { get; set; } = "";
    public double Time { get; set; }

    // There is NO project-level tuning here, and adding one back is the bug this
    // comment exists to prevent. A chart states its tuning ONCE, on the
    // ARRANGEMENT (`Arrangement.Tuning` = note letters, `Arrangement.TuningPitches`
    // = per-string offsets, resolved through `Utilities/TuningOffsetResolution`) —
    // and it has to, because a project holds several arrangements at once and a
    // 6-string lead beside a 4-string bass has TWO right answers, not one.
    // A project-level tuning would be a lie in every path that
    // fills it: the psarc importer's `if (Chart.Tuning.Length == 0)` guard could
    // never fire against a six-name class default, so every Rocksmith import
    // would report E standard whatever the song was tuned to, and the `.dcproj` header
    // parser would stamp standard-tuning NAMES sized by the first arrangement's string
    // count, so an Eb chart would come back as E. Nothing persists such a value (the `.dcproj`
    // writer never emits one), so it is not part of the format.
    // The lane count every reader wants is `Arrangement.Tuning.Length` — via
    // `DotCharter.UI/Input/StringDigitTargeting.StringCountOf` where a null-safe
    // answer is needed. Pinned by `UI/Input/ProjectTuningSingleSourceTests`.

    // Number of accidentals in the key signature.
    // Positive = sharps (1=G, 2=D, 3=A...), negative = flats (-1=F, -2=Bb...), 0 = C/Am.
    public int KeyAccidentals { get; set; }
    public bool KeyIsMinor { get; set; }

    public ObservableCollection<GuitarNote> SingleNotes { get; set; } = [];
    public ObservableCollection<Chord> Chords { get; set; } = [];
}
