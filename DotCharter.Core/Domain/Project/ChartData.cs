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

    /// <summary>The title's sort name (DLC Builder's <c>Title Sort</c>); empty derives it (the leading article dropped).</summary>
    public string TitleSort { get; set; } = "";

    /// <summary>The album's sort name (DLC Builder's <c>Album Name Sort</c>); empty derives it.</summary>
    public string AlbumSort { get; set; } = "";

    /// <summary>
    ///     The package's DLC key (DLC Builder's <c>DLC Key</c>); empty derives it from the artist and title
    ///     (<c>RocksmithDlcKey.Create</c>). Saved as <c>&lt;dcDlcKey&gt;</c>, absent when empty.
    /// </summary>
    public string DlcKey { get; set; } = "";

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

    /// <summary>
    ///     The song's own export volume in dB (DLC Builder's convention: BS.1770 loudness against -16), measured from
    ///     the audio that ships; null until measured. Saved as <c>&lt;dcAudioVolume&gt;</c>.
    /// </summary>
    public double? AudioVolume { get; set; }

    /// <summary>The preview's volume in dB, measured from the preview that ships (the file or the clip cut from the audio); null until measured.</summary>
    public double? PreviewVolume { get; set; }

    /// <summary>Content signature of what <see cref="AudioVolume" /> was measured from; a different one means the audio changed.</summary>
    public string AudioVolumeSignature { get; set; } = "";

    /// <summary>Content signature of what <see cref="PreviewVolume" /> was measured from.</summary>
    public string PreviewVolumeSignature { get; set; } = "";

    /// <summary>True when the charter typed <see cref="AudioVolume" />: it is never re-measured.</summary>
    public bool AudioVolumeManual { get; set; }

    /// <summary>True when the charter typed <see cref="PreviewVolume" />: it is never re-measured.</summary>
    public bool PreviewVolumeManual { get; set; }

    /// <summary>
    ///     Where in the song (ms) the Rocksmith / <c>.dcsong</c> preview clip starts; null = automatic (a quarter into
    ///     the song). Saved as <c>&lt;dcPreviewStartMs&gt;</c>, absent when null. The build clamps it so a whole clip fits.
    /// </summary>
    public double? PreviewStartMs { get; set; }

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

    // True when the charter LOCKED the key above as the song's key: chord
    // spelling then uses it as written instead of checking it against the notes,
    // and C major counts as a real key. False in every older project.
    public bool KeyLocked { get; set; }

    public ObservableCollection<GuitarNote> SingleNotes { get; set; } = [];
    public ObservableCollection<Chord> Chords { get; set; } = [];
}
