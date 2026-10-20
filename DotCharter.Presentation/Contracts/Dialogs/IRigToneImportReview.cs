using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     The Tone designer's "from tone rig" review: the Rocksmith chain the rig became, slot by
///     slot, and every loss by name, before anything is written. Unticking a row leaves that gear
///     out and converts again; a rail over Rocksmith's four slots greys Accept with
///     <see cref="RefusedReason" />. Accept writes ONE undo step: a new tone, or the body of the
///     edited tone when <see cref="ReplaceEdited" />.
/// </summary>
public interface IRigToneImportReview : INotifyPropertyChanged
{
    /// <summary>The chain in Rocksmith's order: pre-pedals, amp, cabinet, racks, loop pedals.</summary>
    IReadOnlyList<IRigToneImportRow> Rows { get; }

    /// <summary>Every loss, one sentence each, naming the module and what stood in for it.</summary>
    IReadOnlyList<string> Losses { get; }

    bool HasLosses { get; }

    /// <summary>How close the tone is to the rig, through the forward converter.</summary>
    string Summary { get; }

    /// <summary>Why Accept is greyed (a rail over four slots); empty otherwise.</summary>
    string RefusedReason { get; }

    bool IsRefused { get; }

    /// <summary>A conversion is running off the UI thread.</summary>
    bool IsBusy { get; }

    /// <summary>A tone is being edited, so "replace it" is offered.</summary>
    bool CanReplace { get; }

    string ReplaceLabel { get; }

    /// <summary>True: replace the edited tone's body. False: add a new tone.</summary>
    bool ReplaceEdited { get; set; }

    bool AddAsNew { get; set; }

    ICommand AcceptCommand { get; }

    ICommand CancelCommand { get; }
}

/// <summary>One piece of Rocksmith gear in the review, and the rig modules it came from.</summary>
public interface IRigToneImportRow : INotifyPropertyChanged
{
    /// <summary>"Pre-pedal 1", "Amplifier", "Rack 2" — or "Left out".</summary>
    string Slot { get; }

    string Gear { get; }

    /// <summary>The rig modules it stands for.</summary>
    string From { get; }

    /// <summary>The knob match as a percentage, empty for a stand-in.</summary>
    string Match { get; }

    /// <summary>Unticked leaves the gear out of the tone.</summary>
    bool Include { get; set; }

    /// <summary>False for a stand-in (an amp or cabinet the tone cannot do without).</summary>
    bool CanToggle { get; }
}
