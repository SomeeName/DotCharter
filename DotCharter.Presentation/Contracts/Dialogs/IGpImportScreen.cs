using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Opens <see cref="IGpImportScreen" /> on a score whose track list was read off the UI thread.</summary>
public sealed record GpImportRequest(string FilePath, IReadOnlyList<GpTrackInfo> Tracks)
    : IDialogRequest<GpImportRequest, IGpImportScreen, bool>;

/// <summary>
///     File ▸ Import ▸ Guitar Pro: choose the tracks and how they land — as new
///     arrangements on the score's tempo map, as techniques folded onto
///     arrangements the project has, or as only what changed since an older
///     version of the score. The score is parsed off the UI thread and applied
///     as ONE undo step; a failure is shown here and leaves the dialog open.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once imported.
/// </summary>
public interface IGpImportScreen : IDialogScreen<bool>
{
    /// <summary>The three modes are one radio group: setting one true clears the other two.</summary>
    bool IsAddNewMode { get; set; }

    bool IsTechniqueOnlyMode { get; set; }

    bool IsDiffCompareMode { get; set; }

    /// <summary>Diff-compare needs the OLDER score as well.</summary>
    bool RequiresOldFile { get; }

    /// <summary>The older score's path; empty while none is picked.</summary>
    string OldFilePath { get; }

    ICommand PickOldFileCommand { get; }

    /// <summary>The merge modes write into an existing arrangement, chosen per track.</summary>
    bool RequiresTargetSelection { get; }

    bool HasTracks { get; }

    IReadOnlyList<IGpTrackRow> Tracks { get; }

    /// <summary>True while the score is parsed: every choice and Import are off.</summary>
    bool IsBusy { get; }

    /// <summary>Why the last import failed or was refused; empty otherwise.</summary>
    string ErrorMessage { get; }

    /// <summary>Enabled while at least one track is chosen and nothing is parsing.</summary>
    ICommand ImportCommand { get; }

    /// <summary>Stops a parse in flight (nothing is applied); otherwise closes.</summary>
    ICommand CancelCommand { get; }
}

public interface IGpTrackRow : INotifyPropertyChanged
{
    string Name { get; }

    bool IsSelected { get; set; }

    /// <summary>The project's arrangement names, for the merge modes' target.</summary>
    IReadOnlyList<string> Targets { get; }

    /// <summary>Index into <see cref="Targets" />: the same-named arrangement when there is one, else the first.</summary>
    int TargetIndex { get; set; }
}
