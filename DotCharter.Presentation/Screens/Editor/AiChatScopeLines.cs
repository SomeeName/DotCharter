using DotCharter.Localization;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.State;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The scope line under the AI chat's pickers for one arrangement and a chart task.</summary>
internal static class AiChatScopeLines
{
    public static string Single(ILocalizationService localization, ISelectionService selection, ProjectState state,
        AiChatScopeKind scope, Arrangement? arrangement)
    {
        return scope switch
        {
            AiChatScopeKind.Selection => selection.SelectedNotes.Count + selection.SelectedChords.Count == 0
                ? localization["AiChat_Summary_NothingSelected"]
                : localization.Format("AiChat_Summary_Selection",
                    AiChatText.Counts(localization, selection.SelectedNotes.Count, selection.SelectedChords.Count)),
            AiChatScopeKind.Section => string.IsNullOrWhiteSpace(state.CurrentSectionDisplay)
                ? localization["AiChat_Summary_NoSection"]
                : localization.Format("AiChat_Summary_Section", state.CurrentSectionDisplay),
            _ => arrangement?.Levels.MaxBy(l => l.DifficultyIndex) is { } level
                ? localization.Format("AiChat_Summary_Arrangement", arrangement.Name,
                    AiChatText.Counts(localization, level.Notes.Count, level.Chords.Count))
                : localization["AiChat_Summary_NoArrangement"]
        };
    }
}
