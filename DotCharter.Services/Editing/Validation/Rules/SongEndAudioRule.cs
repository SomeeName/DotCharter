namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     The "no audio attached" half of the song-end boundary (D05). The
///     per-sound I29 checks in <see cref="NoteRule" />/<see cref="ChordRule" />
///     only run while an audio length is KNOWN — the validation boundary
///     (<c>ArrangementValidationService.Validate</c>) normalizes a raw
///     <c>TotalAudioLengthMs ≤ 0</c> ("no audio decoded") to null, because
///     treating it as "the song ends at 0:00" flooded an audio-less GP import
///     with one I29 per sound (a measured 1,011 duplicates). This rule states
///     the honest fact ONCE per arrangement that has content: the check could
///     not run, and how many sounds it left unchecked. No Auto-Fix touches it
///     — nothing mechanical fixes "attach your audio".
/// </summary>
public sealed class SongEndAudioRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        if (context.TotalAudioLengthMs is not null) return;

        var sounds = 0;
        foreach (var level in context.Arrangement.Levels)
            sounds += level.Notes.Count + level.Chords.Count;
        if (sounds == 0) return;

        var counted = sounds == 1 ? "1 sound is" : $"{sounds} sounds are";
        issues.Add(new ArrangementIssue(
            ArrangementIssueType.SongEndUnverifiedNoAudio,
            $"No audio is attached, so the after-song-end check (I29) cannot run — {counted} unchecked. " +
            "Attach the song audio before exporting."));
    }
}
