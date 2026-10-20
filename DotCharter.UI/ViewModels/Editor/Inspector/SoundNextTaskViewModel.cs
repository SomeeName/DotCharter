using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The Sound tab with nothing selected: the NEXT thing that needs work, not a wall of
///     statistics. Nothing selected is most of a charting session, so the Task section leads with
///     one sentence ("7 chords need fingerings") and one verb ("Fix the next one"), then the
///     other open tasks one line each. Every number is the queue's own count and every verb is the
///     queue's own triage (<see cref="SoundQueueViewModel.TriageFilter" />: set the filter, jump to
///     its first match) — this adds an ORDER, never a second way to edit. Exposed as
///     <c>InspectorViewModel.NextTask</c>.
/// </summary>
public sealed partial class SoundNextTaskViewModel : ObservableObject
{
    private readonly SoundQueueViewModel _queue;
    private readonly ILocalizationService _localization;

    public SoundNextTaskViewModel(SoundQueueViewModel queue, ILocalizationService localization)
    {
        _queue = queue;
        _localization = localization;
        _queue.PropertyChanged += OnQueueChanged;
    }

    /// <summary>The queue filter the next task works through, or null when nothing needs work.</summary>
    public SoundQueueFilter? Filter => NextTaskFor(_queue.MissingFingeringCount, _queue.UnnamedChordCount, _queue.BlocksExportCount);

    /// <summary>
    ///     The order the tasks come in: fingerings first (a shape without them exports with blank
    ///     fingering), then names, then whatever blocks the export; null when nothing needs work.
    /// </summary>
    internal static SoundQueueFilter? NextTaskFor(int missingFingering, int unnamedChords, int blocksExport) =>
        missingFingering > 0 ? SoundQueueFilter.MissingFingering
        : unnamedChords > 0 ? SoundQueueFilter.UnnamedChord
        : blocksExport > 0 ? SoundQueueFilter.BlocksExport
        : null;

    public bool HasTask => Filter is not null;

    /// <summary>"Next up" or "Next up · 1 of 3"; "Nothing waiting" when every count is zero.</summary>
    public string Kicker => TaskCount switch
    {
        0 => _localization["Inspector_Task_NothingWaiting"],
        1 => _localization["Inspector_Task_NextUp"],
        var n => _localization.Format("Inspector_Task_NextUpOf", n)
    };

    public string Title => Filter switch
    {
        SoundQueueFilter.MissingFingering => _localization.Plural("Inspector_Task_FingeringsOne", "Inspector_Task_Fingerings",
            _queue.MissingFingeringCount),
        SoundQueueFilter.UnnamedChord => _localization.Plural("Inspector_Task_UnnamedOne", "Inspector_Task_Unnamed", _queue.UnnamedChordCount),
        SoundQueueFilter.BlocksExport => _localization.Plural("Inspector_Task_BlockersOne", "Inspector_Task_Blockers", _queue.BlocksExportCount),
        _ => _localization["Inspector_Task_AllDone"]
    };

    public string Detail => Filter switch
    {
        SoundQueueFilter.MissingFingering => _localization["Inspector_Task_FingeringsDetail"],
        SoundQueueFilter.UnnamedChord => _localization["Inspector_Task_UnnamedDetail"],
        SoundQueueFilter.BlocksExport => _localization["Inspector_Task_BlockersDetail"],
        _ => _localization["Inspector_Task_AllDoneDetail"]
    };

    /// <summary>The OTHER open tasks, one line each under the card.</summary>
    public bool ShowFingeringRow => _queue.MissingFingeringCount > 0 && Filter != SoundQueueFilter.MissingFingering;

    public bool ShowUnnamedRow => _queue.UnnamedChordCount > 0 && Filter != SoundQueueFilter.UnnamedChord;

    public bool ShowBlockingRow => _queue.BlocksExportCount > 0 && Filter != SoundQueueFilter.BlocksExport;

    private int TaskCount =>
        (_queue.MissingFingeringCount > 0 ? 1 : 0) + (_queue.UnnamedChordCount > 0 ? 1 : 0) + (_queue.BlocksExportCount > 0 ? 1 : 0);

    /// <summary>"Fix the next one": the queue's own triage for the next task.</summary>
    [RelayCommand]
    private void FixNext()
    {
        if (Filter is { } filter) _queue.TriageFilter(filter);
    }

    /// <summary>One of the other tasks' lines: the same triage for that filter.</summary>
    [RelayCommand]
    private void Triage(SoundQueueFilter filter) => _queue.TriageFilter(filter);

    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SoundQueueViewModel.MissingFingeringCount) or nameof(SoundQueueViewModel.UnnamedChordCount)
            or nameof(SoundQueueViewModel.BlocksExportCount)))
        {
            return;
        }

        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(HasTask));
        OnPropertyChanged(nameof(Kicker));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ShowFingeringRow));
        OnPropertyChanged(nameof(ShowUnnamedRow));
        OnPropertyChanged(nameof(ShowBlockingRow));
    }
}
