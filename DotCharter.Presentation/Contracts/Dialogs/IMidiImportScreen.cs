using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Parsing.Midi;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Opens <see cref="IMidiImportScreen" /> on a Standard MIDI File already parsed off the UI thread.</summary>
public sealed record MidiImportRequest(string FileName, MidiFileData File)
    : IDialogRequest<MidiImportRequest, IMidiImportScreen, bool>;

/// <summary>
///     File ▸ Import ▸ MIDI: choose which track to chart, preview the draft, then
///     accept it as a NEW draft arrangement — rejecting the import is deleting an
///     arrangement, so nothing hand-made can be corrupted. The active
///     arrangement's tuning drives the pitch → string/fret mapping.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once imported.
/// </summary>
public interface IMidiImportScreen : IDialogScreen<bool>
{
    string FileName { get; }

    /// <summary>"All tracks" first, then every track that has notes, with its note count.</summary>
    IReadOnlyList<string> Tracks { get; }

    /// <summary>Index into <see cref="Tracks" />; changing it re-converts the preview.</summary>
    int SelectedTrackIndex { get; set; }

    bool IncludeDrumChannel { get; set; }

    /// <summary>
    ///     Replace the project's beat grid with the MIDI's own tempo map, which is
    ///     what lands the draft at the right times. Ticked on open only while the
    ///     project has nothing charted against its grid.
    /// </summary>
    bool ReplaceBeatGrid { get; set; }

    /// <summary>True while a preview converts off the UI thread.</summary>
    bool IsBusy { get; }

    /// <summary>How many notes the selection drafts, or the honest "nothing chartable here".</summary>
    string Summary { get; }

    /// <summary>A CONVERSION CRASH — never shown as the empty summary, which is a claim about the music.</summary>
    string ErrorMessage { get; }

    /// <summary>Enabled only while a draft with notes is in hand and nothing is converting.</summary>
    ICommand ImportCommand { get; }

    ICommand CancelCommand { get; }
}
