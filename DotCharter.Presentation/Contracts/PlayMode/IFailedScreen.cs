using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     The redesigned fail screen. Three choices, deliberately short.
///     <para>
///         It is the other FIGURE screen: a card centred in an empty window,
///         which is the shape that measured 5.3 % of a 1080p window and 1.3 % of
///         a 4K one before the current one took a fractional ceiling. The
///         redesign keeps it to a terminal card and says HOW FAR you got, which
///         is the one fact the current screen does not carry and the only one
///         that makes "retry from section" a considered choice.
///     </para>
/// </summary>
public interface IFailedScreen : INotifyPropertyChanged
{
    string SongTitle { get; }

    /// <summary>The path that was being played.</summary>
    string ArrangementName { get; }

    /// <summary>True when the chart has sections and one can be retried.</summary>
    bool HasSection { get; }

    /// <summary>The section it ended in.</summary>
    string SectionLabel { get; }

    /// <summary>How far into the song it ended, 0..1.</summary>
    double Progress { get; }

    /// <summary>"2:14 of 4:32", already formatted.</summary>
    string ProgressLabel { get; }

    /// <summary>The accuracy up to the fall, already formatted.</summary>
    string AccuracyLabel { get; }

    /// <summary>Picks the song back up at <see cref="SectionLabel" />.</summary>
    ICommand RetrySectionCommand { get; }

    ICommand RestartCommand { get; }

    ICommand QuitCommand { get; }
}
