using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Opens <see cref="IEventScreen" /> on the marker at <paramref name="TimeMs" /> (snapped to the grid) of
///     <paramref name="Target" />, the arrangement the user clicked in; null means the active one.
/// </summary>
public sealed record EventRequest(double TimeMs, Arrangement? Target = null)
    : IDialogRequest<EventRequest, IEventScreen, bool>;

/// <summary>
///     One event marker: its section (a closed Rocksmith list), section status,
///     phrase and solo flag, tone change, RS event codes, and which arrangements
///     it is written to. Opening on empty ground repeats the last marker's answer.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once saved.
/// </summary>
public interface IEventScreen : IDialogScreen<bool>
{
    /// <summary>"None" first, then every section Rocksmith renders, then an off-list name the marker already carries.</summary>
    IReadOnlyList<IEventSectionOption> SectionOptions { get; }

    /// <summary>Picking one fills the phrase with the same name while the phrase is empty or still auto-filled.</summary>
    IEventSectionOption? SelectedSection { get; set; }

    /// <summary>The stored key of <see cref="SelectedSection" />; empty for none.</summary>
    string SectionName { get; }

    /// <summary>A non-empty section Rocksmith does not render: warned about, not refused.</summary>
    bool SectionNameIsUnknown { get; }

    /// <summary>None, To do, Draft, Review, Done.</summary>
    IReadOnlyList<string> SectionStatusOptions { get; }

    int SectionStatusIndex { get; set; }

    string PhraseName { get; set; }

    /// <summary>Every iteration of this phrase is a solo; only meaningful with a phrase name.</summary>
    bool SoloPhrase { get; set; }

    /// <summary>The tones this chart has, most relevant first. The field stays editable: a tone may be named first.</summary>
    IReadOnlyList<string> ToneOptions { get; }

    string ToneName { get; set; }

    /// <summary>A named tone the project has no body for: it will export and play silently.</summary>
    bool ToneHasNoBody { get; }

    /// <summary>The metronome ticks and the crowd controls; several can sit on one marker.</summary>
    IReadOnlyList<IEventCodeToggle> EventToggles { get; }

    /// <summary>Every arrangement; the clicked one is ticked and cannot be unticked.</summary>
    IReadOnlyList<IEventArrangementTarget> Arrangements { get; }

    bool HasMultipleArrangements { get; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}

public interface IEventSectionOption
{
    /// <summary>The label; an off-list name an import put on this marker reads "name — not a Rocksmith section".</summary>
    string Display { get; }
}

public interface IEventCodeToggle : INotifyPropertyChanged
{
    string Display { get; }

    bool IsSelected { get; set; }
}

public interface IEventArrangementTarget : INotifyPropertyChanged
{
    string DisplayName { get; }

    /// <summary>The arrangement the dialog opened on; always written to.</summary>
    bool IsActive { get; }

    bool IsSelected { get; set; }
}
