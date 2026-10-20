using DotCharter.Localization;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
/// The two ghost-gem verbs as the shell runs them: read the selection, plan with
///     <see cref="GhostGems" />, commit ONE undo node, say what happened. A collaborator of
///     <c>ChartMaintenanceService</c> (which forwards to it) rather than more of that class.
/// </summary>
public static class GhostGemCommands
{
    /// <summary>EOF's Ctrl+G: the selected notes and the masked strings of the selected chords
    /// become ghost gems of the hand shape over them; a selection of hand shapes alone gives its
    /// ghosts back to the grip.</summary>
    public static void Toggle(ProjectState state, IChartEditorSurface editor, IHistoryService history,
        ILocalizationService localization, IAppLogger logger)
    {
        var arrangement = state.ActiveArrangement;
        var level = state.ActiveLevel;
        if (arrangement == null || level == null)
        {
            state.MainDisplayMessage = localization["Status_NoActiveLevel"];
            return;
        }

        var notes = editor.SelectedNotes.Where(level.Notes.Contains).ToList();
        var chords = editor.SelectedChords.Where(level.Chords.Contains).ToList();
        var unghost = notes.Count == 0 && chords.Count == 0;
        var plan = unghost
            ? GhostGems.Unghost(arrangement, level, [.. editor.Selection.SelectedHandShapes])
            : GhostGems.Ghost(arrangement, level, notes, chords, state.StringEditMask.Includes);
        if (plan.Action is not { } action)
        {
            state.MainDisplayMessage = localization["Status_GhostGems_None"];
            return;
        }

        if (!history.Do(action)) return;

        // A ghosted note left the level; a selection still pointing at it dangles.
        if (!unghost) editor.Selection.ClearAll();
        editor.RequestRedraw();
        state.MainDisplayMessage = unghost
            ? localization.Plural("Status_GhostGems_UnghostedOne", "Status_GhostGems_Unghosted", plan.Gems)
            : localization.Plural("Status_GhostGems_GhostedOne", "Status_GhostGems_Ghosted", plan.Gems);
        logger.Info(AppLogCategory.Editing,
            $"Ghost gems {(unghost ? "returned to the grip" : "set")}: {plan.Gems} gem(s), {plan.TemplatesMinted} template(s) minted, " +
            $"{plan.HandShapesAdded} hand shape(s) added, arrangement '{arrangement.Name}'.");
    }

    /// <summary>One command for a tapped harmonic: harmonic + tap on each selected note, and a
    /// ghost fretting finger <paramref name="fretOffset" /> frets lower in its hand shape.</summary>
    public static void TappedHarmonic(ProjectState state, IChartEditorSurface editor, IHistoryService history,
        ILocalizationService localization, IAppLogger logger, int fretOffset)
    {
        var arrangement = state.ActiveArrangement;
        var level = state.ActiveLevel;
        if (arrangement == null || level == null)
        {
            state.MainDisplayMessage = localization["Status_NoActiveLevel"];
            return;
        }

        var notes = editor.SelectedNotes.Where(level.Notes.Contains).ToList();
        var plan = GhostGems.TappedHarmonic(arrangement, level, notes, fretOffset);
        if (plan.Action is not { } action)
        {
            state.MainDisplayMessage = localization["Status_TappedHarmonic_None"];
            return;
        }

        if (!history.Do(action)) return;
        editor.RequestRedraw();
        state.MainDisplayMessage =
            localization.Plural("Status_TappedHarmonic_DoneOne", "Status_TappedHarmonic_Done", plan.Gems, fretOffset);
        logger.Info(AppLogCategory.Editing,
            $"Tapped harmonic: {plan.Gems} note(s), held {fretOffset} fret(s) lower, {plan.HandShapesAdded} hand shape(s) added, " +
            $"arrangement '{arrangement.Name}'.");
    }
}
