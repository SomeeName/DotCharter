namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D110 (<see cref="ArrangementIssueType.NeedsAudioCheck" />): an imported chart is a draft, and the spots the
///     import guessed (<see cref="Arrangement.AudioCheckSpots" />) still need a listen against the audio. ONE finding
///     per arrangement, never one per spot: it counts them, names what the first one guessed and points at it, so a
///     jump lands on the first unchecked spot. Its "Checked" and "All checked" are <see cref="AudioCheckMarks" />.
/// </summary>
public sealed class AudioCheckRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var spots = context.Arrangement.AudioCheckSpots;
        if (AudioCheckMarks.First(spots) is not { } first) return;

        issues.Add(ArrangementIssues.Make(ArrangementIssueType.NeedsAudioCheck,
            spots.Count == 1 ? "Validation_NeedsAudioCheck_One" : "Validation_NeedsAudioCheck_Many",
            first.Position.GetPositionMs(context.Beats),
            args: [spots.Count, new IssueTextRef(ReasonKey(first.Reason))]));
    }

    private static string ReasonKey(AudioCheckReason reason) => reason switch
    {
        AudioCheckReason.TempoFromTab => "Validation_AudioCheckReason_TempoFromTab",
        AudioCheckReason.GraceTiming => "Validation_AudioCheckReason_GraceTiming",
        _ => "Validation_AudioCheckReason_DeadNoteSlideLanding"
    };
}
