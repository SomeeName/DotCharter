using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     The redesigned in-game HUD.
///     <para>
///         <b>The highway is not in this contract and never can be</b> — it is a
///         native Vulkan renderer, so the redesigned view draws a token-coloured
///         placeholder <c>Border</c> where the 3D surface goes and designs the
///         chrome AROUND it. What the contract carries is everything the HUD
///         itself prints: the score cluster, the run's progress through the song
///         and the section, the energy meter and the pause hint.
///     </para>
/// </summary>
public interface IPlayingScreen : INotifyPropertyChanged
{
    string SongTitle { get; }

    /// <summary>The path being played.</summary>
    string ArrangementName { get; }

    string ScoreLabel { get; }

    /// <summary>"x4" — the streak multiplier in force.</summary>
    string MultiplierLabel { get; }

    string AccuracyLabel { get; }

    string StreakLabel { get; }

    string HitsLabel { get; }

    string MissesLabel { get; }

    /// <summary>The section being played, or empty on a chart without sections.</summary>
    string SectionLabel { get; }

    bool HasSection { get; }

    /// <summary>How far through the SECTION, 0..1.</summary>
    double SectionProgress { get; }

    /// <summary>"12 of 28" — which section this is.</summary>
    string SectionCountLabel { get; }

    /// <summary>How far through the SONG, 0..1.</summary>
    double SongProgress { get; }

    string ElapsedLabel { get; }

    string TotalLabel { get; }

    /// <summary>The performance meter, 0..1. Running out ends the run.</summary>
    double EnergyLevel { get; }

    /// <summary>True when the meter is low enough that failing is close.</summary>
    bool IsEnergyLow { get; }

    /// <summary>Esc.</summary>
    ICommand PauseCommand { get; }
}
