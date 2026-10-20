using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Tools ▸ Tuner: a live cents needle for the plucked string and one row per
///     open string of the active arrangement's tuning. Everything follows the
///     input while it is open. <see cref="IDialogScreen{TResult}.Result" /> is
///     true when closed with Close.
/// </summary>
public interface ITunerScreen : IDialogScreen<bool>
{
    /// <summary>"Drop pedal active (-2 st): …"; empty when the pedal is off.</summary>
    string PitchShiftInfo { get; }

    bool IsPitchShiftActive { get; }

    /// <summary>Why there is no needle (no input stream); empty while there is one.</summary>
    string StatusHint { get; }

    bool HasInput { get; }

    /// <summary>A pitch near one of the strings is being heard right now.</summary>
    bool HasReading { get; }

    /// <summary>The heard note ("E2"), or an en dash while nothing is heard.</summary>
    string NoteText { get; }

    /// <summary>"+3 ¢", with a true minus sign when flat; empty without a reading.</summary>
    string CentsText { get; }

    bool IsInTune { get; }

    /// <summary>The heard string's deviation in cents, ±50; the big meter draws it on the same scale as the rows.</summary>
    double CentsOff { get; }

    /// <summary>Low string first. The rows change in place; the list itself is fixed while open.</summary>
    IReadOnlyList<ITunerString> Strings { get; }

    /// <summary>"4 of 6 in tune"; empty when there are no strings.</summary>
    string TunedCountLabel { get; }

    bool AllTuned { get; }

    ICommand CloseCommand { get; }
}

/// <summary>One open string. Raises property changes as the reading moves.</summary>
public interface ITunerString : INotifyPropertyChanged
{
    /// <summary>The open string's target note, e.g. "E2".</summary>
    string Label { get; }

    /// <summary>The string being heard right now.</summary>
    bool IsNearest { get; }

    bool IsTuned { get; }

    /// <summary>This string's LAST reading in cents, ±50: it keeps the value after the string stops sounding.</summary>
    double CentsOff { get; }

    /// <summary>False for a string not yet plucked; the row then says "Pluck" instead of drawing a needle.</summary>
    bool HasReading { get; }
}
