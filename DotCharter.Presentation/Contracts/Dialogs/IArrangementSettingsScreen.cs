using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Opens <see cref="IArrangementSettingsScreen" /> on the active arrangement, or on a new one.</summary>
public sealed record ArrangementSettingsRequest(bool CreateNew = false)
    : IDialogRequest<ArrangementSettingsRequest, IArrangementSettingsScreen, bool>;

/// <summary>
///     Arrangement ▸ Settings: name, type, capo, cent offset, string count and tuning, and
///     the Rocksmith-facing block — scroll speed, picked bass, the path route and the
///     technique flags. One Save is one undo step. <see cref="IDialogScreen{TResult}.Result" />
///     is true once saved.
/// </summary>
public interface IArrangementSettingsScreen : IDialogScreen<bool>
{
    string Name { get; set; }

    ArrangementType Type { get; set; }

    /// <summary>Every type except Vocal.</summary>
    IReadOnlyList<ArrangementType> ArrangementTypes { get; }

    ArrangementSubtype Subtype { get; set; }

    IReadOnlyList<ArrangementSubtype> ArrangementSubtypes { get; }

    int Capo { get; set; }

    double CentOffset { get; set; }

    /// <summary>Changing it adds or drops rows in <see cref="TuningOffsets" />.</summary>
    int Strings { get; set; }

    /// <summary>Picking a named preset rewrites the offsets; Custom leaves them as typed.</summary>
    string TuningPreset { get; set; }

    IReadOnlyList<string> TuningPresets { get; }

    /// <summary>One half-step offset per string, low string first; the list follows <see cref="Strings" />.</summary>
    IReadOnlyList<ITuningStep> TuningOffsets { get; }

    IReadOnlyList<IArrangementPathOption> PathOptions { get; }

    IArrangementPathOption? SelectedPath { get; set; }

    /// <summary>Off means "no preference", which is not the same as the default speed.</summary>
    bool HasScrollSpeed { get; set; }

    /// <summary>0.5–5.0, lower is faster.</summary>
    double ScrollSpeed { get; set; }

    bool IsBass { get; }

    /// <summary>Only meaningful on bass.</summary>
    bool PickedBass { get; set; }

    /// <summary>The four flags no note field encodes; always editable.</summary>
    IReadOnlyList<IArrangementFlag> AuthoredFlags { get; }

    /// <summary>Off: the derived flags show what the chart's content says, read-only. On: they are pinned as set.</summary>
    bool OverrideDerivedFlags { get; set; }

    /// <summary>The twenty flags derived from the chart's content.</summary>
    IReadOnlyList<IArrangementFlag> DerivedFlags { get; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}

public interface ITuningStep : INotifyPropertyChanged
{
    /// <summary>Half steps from standard.</summary>
    int Step { get; set; }

    /// <summary>The open string this box tunes, in standard ("E", "A", … "e"): the row is lowest string first.</summary>
    string StringLabel { get; }

    /// <summary>The note the string sounds at with <see cref="Step" /> applied ("D" for Drop D's low string).</summary>
    string ResultingNote { get; }

    /// <summary>What a screen reader says for this box — distinct per string ("String 6 (E), half steps").</summary>
    string AccessibleName { get; }
}

public interface IArrangementPathOption
{
    string Label { get; }
}

public interface IArrangementFlag : INotifyPropertyChanged
{
    string Label { get; }

    bool IsSet { get; set; }
}
