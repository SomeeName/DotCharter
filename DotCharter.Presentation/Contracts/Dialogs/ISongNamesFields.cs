using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     The song-name fields the New project dialog and the Song metadata dialog share, in DLC Builder's
///     words and grouping: the Japanese (original-script) fields and the sort fields sit behind two toggles,
///     the sort values, DLC key and version come prefilled with what a build would derive and stay editable,
///     and the author and the tool version close the form as "Additional metadata". A field left at its
///     derived value is saved blank, so it keeps following the artist and title.
/// </summary>
public interface ISongNamesFields
{
    /// <summary>The artist in its original script, which Rocksmith carries beside the Latin one.</summary>
    string JapaneseArtist { get; set; }

    /// <summary>The song name in its original script.</summary>
    string JapaneseTitle { get; set; }

    /// <summary>Shows the Japanese Artist Name and Japanese Title rows (on while either holds text).</summary>
    bool ShowJapaneseFields { get; set; }

    /// <summary>Shows the Artist Name Sort, Title Sort and Album Name Sort rows (on while one was authored).</summary>
    bool ShowSortFields { get; set; }

    /// <summary>The artist's sort name; follows the artist while it is left alone.</summary>
    string ArtistSort { get; set; }

    /// <summary>The title's sort name; follows the title while it is left alone.</summary>
    string TitleSort { get; set; }

    /// <summary>The album's sort name; follows the album while it is left alone.</summary>
    string AlbumSort { get; set; }

    /// <summary>The package's DLC key; follows the artist and title while it is left alone.</summary>
    string DlcKey { get; set; }

    /// <summary>The package version DLC Builder calls Version; blank reads as 1.</summary>
    string Version { get; set; }

    /// <summary>The author of the package: the default charter name, overridable per project.</summary>
    string Charter { get; set; }

    /// <summary>The tool a built package names (<c>DotCharter v1.3.0</c>); read-only, automatic.</summary>
    string ToolVersion { get; }

    /// <summary>True while the main Artist Name or Title holds non-Latin text, which exports as the main name.</summary>
    bool HasOriginalScriptOffer { get; }

    /// <summary>
    ///     Moves a non-Latin main name to its Japanese field and fills the main one from the official
    ///     (MusicBrainz) Latin name; the main field is left empty for the charter to type when there is none.
    /// </summary>
    ICommand MoveToOriginalScriptCommand { get; }
}
