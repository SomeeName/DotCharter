// AiChatArrangementPicker: one or several arrangements (or all) for one chat request, the picker's text, the scope line over the selection, and the scope each arrangement runs on.
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.State;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The arrangement picker the chat pane owns: its checkable rows, its button text, and the arrangements a request covers.</summary>
public sealed partial class AiChatArrangementPicker(ProjectState state, ISelectionService selection, ILocalizationService localization,
    Func<AiChartJob> job, Func<AiChatScopeKind> scope) : ObservableObject, IAiChatArrangementPicker
{
    private bool _syncing;

    [ObservableProperty] private IReadOnlyList<IAiChatArrangementChoice> _choices = [];
    [ObservableProperty] private string _pickerText = "";

    /// <summary>Raised after any change of the checked arrangements (the scope line follows).</summary>
    public event Action? Changed;

    /// <summary>Raised when the user's check moved the first checked arrangement; the owner mirrors it into its own selection.</summary>
    public event Action<Arrangement?>? FirstChosen;

    /// <summary>Replaces the rows with one per arrangement under "All arrangements"; nothing is checked yet.</summary>
    public void Rebuild(Arrangement[] arrangements) =>
        Choices =
        [
            new AiChatArrangementChoice(localization["AiChat_AllArrangements"], null, OnChoiceToggled),
            .. arrangements.Select(a => new AiChatArrangementChoice(a.Name, a, OnChoiceToggled))
        ];

    public void Clear() => Choices = [];

    /// <summary>The checked arrangements, in chart order; the active one when the picker has none yet.</summary>
    public List<Arrangement> Chosen()
    {
        var chosen = Rows().Where(c => c is { IsAll: false, IsChecked: true, Key: Arrangement }).Select(c => (Arrangement)c.Key!).ToList();
        if (chosen.Count == 0 && state.ActiveArrangement is { } active) chosen.Add(active);
        return chosen;
    }

    /// <summary>Checks only <paramref name="arrangement" /> (the first one when null or unknown).</summary>
    public void SelectOnly(Arrangement? arrangement)
    {
        var each = Each();
        var only = each.FirstOrDefault(c => ReferenceEquals(c.Key, arrangement)) ?? each.FirstOrDefault();
        foreach (var c in each) c.Set(ReferenceEquals(c, only));
        SyncAllRow(each);
        Refresh();
    }

    /// <summary>The scope line over several arrangements, or null for one (the single-arrangement text).</summary>
    public string? ManySummary(string single)
    {
        var chosen = Chosen();
        if (chosen.Count < 2) return null;
        if (job() == AiChartJob.Tones)
        {
            return scope() == AiChatScopeKind.Arrangement
                ? localization.Format("AiChat_Summary_TonesMany", chosen.Count, chosen.Sum(a => a.ToneChanges.Count))
                : null;
        }

        switch (scope())
        {
            case AiChatScopeKind.Selection:
                // The selection is the editor's: it belongs to ONE arrangement.
                return selection.SelectedNotes.Count + selection.SelectedChords.Count == 0
                    ? null
                    : localization.Format("AiChat_Summary_SelectionOnly", single, SelectionArrangement(chosen).Name);
            case AiChatScopeKind.Section:
                return string.IsNullOrWhiteSpace(state.CurrentSectionDisplay)
                    ? null
                    : localization.Format("AiChat_Summary_SectionMany", single, chosen.Count);
            default:
                var levels = chosen.Select(a => a.Levels.MaxBy(l => l.DifficultyIndex)).OfType<ChartLevel>().ToList();
                return localization.Format("AiChat_Summary_ArrangementMany", chosen.Count,
                    AiChatText.Counts(localization, levels.Sum(l => l.Notes.Count), levels.Sum(l => l.Chords.Count)));
        }
    }

    /// <summary>The arrangements a request runs on: every checked one, but a selection scope runs on the one the selection belongs to.</summary>
    public List<Arrangement> RunArrangements()
    {
        var chosen = Chosen();
        return scope() == AiChatScopeKind.Selection && job() != AiChartJob.Tones && chosen.Count > 1 ? [SelectionArrangement(chosen)] : chosen;
    }

    private List<AiChatArrangementChoice> Rows() => [.. Choices.OfType<AiChatArrangementChoice>()];

    private List<AiChatArrangementChoice> Each() => [.. Rows().Where(c => !c.IsAll)];

    private void SyncAllRow(List<AiChatArrangementChoice> each)
    {
        if (Rows().FirstOrDefault(c => c.IsAll) is { } all) all.Set(each.Count > 0 && each.All(c => c.IsChecked));
    }

    private void OnChoiceToggled(AiChatArrangementChoice choice)
    {
        if (_syncing) return;
        var each = Each();
        if (choice.IsAll)
        {
            // Unchecking "All" falls back to the active arrangement: a request always covers one.
            if (!choice.IsChecked)
            {
                SelectOnly(state.ActiveArrangement);
                return;
            }

            each.ForEach(c => c.Set(true));
        }
        else if (!each.Any(c => c.IsChecked))
        {
            choice.Set(true);
        }

        SyncAllRow(each);
        _syncing = true;
        try
        {
            FirstChosen?.Invoke(each.FirstOrDefault(c => c.IsChecked)?.Key as Arrangement);
        }
        finally
        {
            _syncing = false;
        }

        Refresh();
    }

    /// <summary>The picker's text follows the checked arrangements.</summary>
    private void Refresh()
    {
        var chosen = Chosen();
        PickerText = chosen.Count switch
        {
            0 => localization["AiChat_Summary_NoArrangement"],
            1 => chosen[0].Name,
            _ when chosen.Count == Each().Count => localization["AiChat_AllArrangements"],
            _ => localization.Format("AiChat_ArrangementsN", chosen.Count)
        };
        Changed?.Invoke();
    }

    private Arrangement SelectionArrangement(List<Arrangement> chosen) =>
        state.ActiveArrangement is { } active && chosen.Contains(active) ? active : chosen[0];
}
