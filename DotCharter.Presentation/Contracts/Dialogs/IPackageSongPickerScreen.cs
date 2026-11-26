using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>One song a package holds, as the picker lists it.</summary>
public sealed record PackageSongChoice(string Title, string Detail);

/// <summary>
///     Opens <see cref="IPackageSongPickerScreen" /> over the songs of one package
///     (<paramref name="PackageName" /> is its file name). The answer is the index
///     into <paramref name="Songs" />; null when nothing was chosen.
/// </summary>
public sealed record PackageSongPickerRequest(string PackageName, IReadOnlyList<PackageSongChoice> Songs)
    : IDialogRequest<PackageSongPickerRequest, IPackageSongPickerScreen, int?>;

/// <summary>
///     A game package opened from outside the Library holds several songs: pick the
///     one to open. <see cref="IDialogScreen{TResult}.Result" /> is the chosen
///     song's index, or null on Cancel (and when closed from outside).
/// </summary>
public interface IPackageSongPickerScreen : IDialogScreen<int?>
{
    /// <summary>The package's file name.</summary>
    string PackageName { get; }

    IReadOnlyList<IPackageSongRow> Songs { get; }

    /// <summary>The highlighted song; the first one when the dialog opens.</summary>
    IPackageSongRow? Selected { get; set; }

    /// <summary>Answers the <see cref="Selected" /> song's index; off while none is selected.</summary>
    ICommand OpenCommand { get; }

    ICommand CancelCommand { get; }
}

public interface IPackageSongRow
{
    string Title { get; }

    /// <summary>Artist and length, whichever are known.</summary>
    string Detail { get; }
}
