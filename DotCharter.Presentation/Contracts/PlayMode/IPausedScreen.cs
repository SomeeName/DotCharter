using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     The redesigned pause screen: one primary verb, a tool grid, and a quiet
///     rail for the ways out.
///     <para>
///         The current shape — "one primary + a tool grid + a quiet leave rail,
///         not nine equal word buttons" — is kept deliberately: it is the one
///         screen where every choice is a different KIND of thing, and nine
///         equal buttons is how that stopped reading.
///     </para>
/// </summary>
public interface IPausedScreen : INotifyPropertyChanged
{
    string SongTitle { get; }

    /// <summary>
    ///     False before the first judged note — the figures are then hidden
    ///     rather than printed as zeroes, which is the difference between "you
    ///     have not played yet" and "you played badly".
    /// </summary>
    bool HasRun { get; }

    string ScoreLabel { get; }

    string AccuracyLabel { get; }

    string StreakLabel { get; }

    string ElapsedLabel { get; }

    string TotalLabel { get; }

    /// <summary>How far through the song the pause happened, 0..1.</summary>
    double Progress { get; }

    bool HasSection { get; }

    /// <summary>Where you stopped.</summary>
    string SectionLabel { get; }

    ICommand ResumeCommand { get; }

    ICommand RestartCommand { get; }

    /// <summary>The tool grid — tuner, riff repeater, settings, rig.</summary>
    IReadOnlyList<IPauseTool> Tools { get; }

    /// <summary>Stops here and goes to the results screen with what you have.</summary>
    ICommand EndRunCommand { get; }

    ICommand LeaveToSongSelectCommand { get; }

    /// <summary>Leaves Play Mode for Home.</summary>
    ICommand LeaveToMenuCommand { get; }

    /// <summary>The running setlist: skip this song, or end the setlist.</summary>
    IPausedSetlist Setlist { get; }
}

/// <summary>One tile in the pause menu's tool grid.</summary>
public interface IPauseTool : INotifyPropertyChanged
{
    string Title { get; }

    /// <summary>What it does, in one quiet line.</summary>
    string Caption { get; }

    ICommand InvokeCommand { get; }
}
