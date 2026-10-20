using DotCharter.Core.Domain.Project;
using DotCharter.Services.Editing.DraftMerge;
using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
/// Chart from audio's "Merge into the current arrangement": cuts the draft by the
///     arrangement's phrases and HOLDS it on the merge card (<see cref="DraftMergeReviewHost" />).
///     Nothing is written here.
/// </summary>
public sealed class ChartFromAudioMerge(ChartEditDialogServices edit, DraftMergeReviewHost host)
{
    /// <summary>The active level when it is <paramref name="arrangement" />'s, else null.</summary>
    public ChartLevel? TargetLevel(Arrangement? arrangement) =>
        arrangement is not null && edit.State.ActiveLevel is { } level && arrangement.Levels.Contains(level) ? level : null;

    /// <summary>Null when the review is on the card; otherwise the status line saying why there is nothing to review.</summary>
    public string? Hold(ChartProject project, Arrangement arrangement, ChartFromAudioDraft draft, bool replaceExisting)
    {
        var loc = edit.Localization;
        if (TargetLevel(arrangement) is not { } level) return loc["ChartFromAudio_MergeNoLevel"];

        var review = DraftMergeReview.Plan(project, arrangement, level, edit.State.Beats, draft.Notes, draft.Confidences,
            draft.AverageConfidence, replaceExisting);
        edit.Logger.Info(AppLogCategory.Import,
            $"Chart-from-audio merge planned. Arrangement='{arrangement.Name}', phrases={review.Phrases.Count}, " +
            $"occupied={review.Occupied}, replace={replaceExisting}, notes={draft.Notes.Count}.");
        if (review.Phrases.Count == 0)
        {
            return review.Occupied > 0
                ? loc.Plural("ChartFromAudio_MergeAllOccupiedOne", "ChartFromAudio_MergeAllOccupied", review.Occupied)
                : loc["ChartFromAudio_MergeNothing"];
        }

        host.Hold(review);
        edit.State.SetMessage(loc.Plural("ChartFromAudio_MergeHeldOne", "ChartFromAudio_MergeHeld", review.Phrases.Count,
            arrangement.Name));
        return null;
    }
}
