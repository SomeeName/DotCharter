using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Opens <see cref="IVocalTrackSettingsScreen" /> on the active vocal track, or
///     — with <paramref name="CreateNew" /> — on a new one that Save adds.
/// </summary>
public sealed record VocalTrackSettingsRequest(bool CreateNew = false)
    : IDialogRequest<VocalTrackSettingsRequest, IVocalTrackSettingsScreen, bool>;

/// <summary>
///     A vocal track's name and lyric colour. The colour leads with four
///     swatches — a lyric colour is picked to be legible on the timeline — and
///     the full picker stays for anything else. Save is one undo step.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once saved.
/// </summary>
public interface IVocalTrackSettingsScreen : IDialogScreen<bool>
{
    string Name { get; set; }

    /// <summary><c>#RRGGBB</c>, or <c>#AARRGGBB</c> when not opaque; the swatches and the picker both write it.</summary>
    string TrackColor { get; set; }

    /// <summary>The four offered colours; the one matching <see cref="TrackColor" /> (ignoring alpha) is selected.</summary>
    IReadOnlyList<ILyricColorSwatch> ColorSwatches { get; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}

/// <summary>One offered lyric colour. Colour is not its only signal: it has a name, and selection draws a ring.</summary>
public interface ILyricColorSwatch : INotifyPropertyChanged
{
    /// <summary>The colour in words, for a screen reader and the tooltip.</summary>
    string Name { get; }

    /// <summary><c>#RRGGBB</c>.</summary>
    string Color { get; }

    bool IsSelected { get; }

    /// <summary>Writes this colour to <see cref="IVocalTrackSettingsScreen.TrackColor" />.</summary>
    ICommand PickCommand { get; }
}
