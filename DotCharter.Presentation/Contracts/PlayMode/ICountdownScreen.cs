using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     The redesigned count-in screen: one numeral and the song it is counting
///     into.
///     <para>
///         It is a FIGURE centred in an empty window, which is why the current
///         one takes a <c>Viewbox</c> with a fractional ceiling rather than a
///         literal width — at a literal size it covered 1.6 % of a 1080p window
///         and 0.4 % of a 4K one. The redesign keeps the figure shape and puts
///         the song lockup under it, so the frame says WHAT is starting.
///     </para>
/// </summary>
public interface ICountdownScreen : INotifyPropertyChanged
{
    /// <summary>"3", "2", "1" or the GO! word — already localised.</summary>
    string CountLabel { get; }

    /// <summary>True on the last beat, when the numeral becomes a word.</summary>
    bool IsGo { get; }

    /// <summary>One pip per beat of the count-in, lit as they pass.</summary>
    IReadOnlyList<ICountdownPip> Pips { get; }

    string SongTitle { get; }

    string Artist { get; }

    bool HasArtist { get; }

    /// <summary>The path the run takes.</summary>
    string ArrangementName { get; }

    /// <summary>The tuning it expects, with the capo folded in.</summary>
    string TuningLabel { get; }

    /// <summary>Esc — back out before the first note.</summary>
    ICommand CancelCommand { get; }
}

/// <summary>One beat of the count-in.</summary>
public interface ICountdownPip : INotifyPropertyChanged
{
    bool IsLit { get; }
}
