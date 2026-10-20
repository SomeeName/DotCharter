using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Session;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     File ▸ New project, audio first: pick the song's audio (its tags and cover art
///     fill the form) or start without audio, confirm the details, Create.
///     <see cref="IDialogScreen{TResult}.Result" /> is the details to create from, or
///     null when cancelled; the folder is picked after the dialog closes.
/// </summary>
public interface INewProjectScreen : IDialogScreen<NewProjectDetails?>
{
    bool IsChooserStage { get; }

    /// <summary>Reading tags and cover art off the picked file.</summary>
    bool IsExtracting { get; }

    bool IsFormStage { get; }

    /// <summary>"MP3 · OGG · WAV …" — the formats the picker accepts.</summary>
    string AcceptedAudioFormats { get; }

    string Artist { get; set; }

    string Title { get; set; }

    /// <summary>The tags' own script when they are not Latin; carried into the project beside the Latin names.</summary>
    string JapaneseArtist { get; set; }

    string JapaneseTitle { get; set; }

    string Album { get; set; }

    string AlbumYear { get; set; }

    string Genre { get; set; }

    /// <summary>The embedded cover as PNG bytes; null without one.</summary>
    byte[]? CoverArtPng { get; }

    bool HasCoverArt { get; }

    /// <summary>MusicBrainz found official Latin-script names; never applied without the user.</summary>
    bool HasNameSuggestion { get; }

    ICommand ChooseAudioCommand { get; }

    ICommand StartWithoutAudioCommand { get; }

    ICommand ApplyNameSuggestionCommand { get; }

    /// <summary>Back to the chooser.</summary>
    ICommand BackCommand { get; }

    /// <summary>Refused, with a status message, while the artist or the title is empty.</summary>
    ICommand CreateCommand { get; }

    ICommand CancelCommand { get; }
}
