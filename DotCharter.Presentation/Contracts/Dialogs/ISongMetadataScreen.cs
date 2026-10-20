using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     File ▸ Song metadata: artist, title, album, charter, genre and key
///     signature of the open project, saved back to the chart as ONE undo step.
///     It opens only over an open project — the shell says so otherwise.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when closed with Save.
/// </summary>
public interface ISongMetadataScreen : IDialogScreen<bool>
{
    string Artist { get; set; }

    string SongName { get; set; }

    /// <summary>The artist in its original script, which Rocksmith carries beside the Latin one.</summary>
    string JapaneseArtist { get; set; }

    string JapaneseTitle { get; set; }

    string AlbumName { get; set; }

    string AlbumYear { get; set; }

    string Charter { get; set; }

    string Genre { get; set; }

    /// <summary>Genres this user already authored in their own charts, as one-click picks; empty hides the strip.</summary>
    IReadOnlyList<string> GenreSuggestions { get; }

    bool HasGenreSuggestions { get; }

    /// <summary>Adopts the genre passed as the parameter.</summary>
    ICommand PickGenreCommand { get; }

    /// <summary>Positive counts sharps, negative counts flats, −7…7.</summary>
    int KeyAccidentals { get; set; }

    bool KeyIsMinor { get; set; }

    /// <summary>The other half of <see cref="KeyIsMinor" />, so each radio button owns a real two-way property.</summary>
    bool KeyIsMajor { get; set; }

    /// <summary>"2 ♯", "7 ♭" or "no accidentals".</summary>
    string KeyAccidentalsLabel { get; }

    /// <summary>The key the signature spells: "A minor", "Cb major".</summary>
    string KeySignatureLabel { get; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}
