namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     The room around the chart, as suggestions (Warning): D42 (<see cref="ArrangementIssueType.LeadInShort" />)
///     the first note starts under <see cref="SuggestedLeadInMs" /> in, D43
///     (<see cref="ArrangementIssueType.TailShort" />) the audio ends under <see cref="SuggestedTailMs" /> after
///     the last sound, and D44 (<see cref="ArrangementIssueType.NoCountMeasure" />) no empty measure comes
///     before the first note.
///     <para>
///         The hard limit stays D02 (<see cref="ArrangementIssueType.FirstNoteTooEarly" />, 1 s) and the export
///         gate's own 1 s check, so an existing project exports exactly as before; below 1 s only D02 speaks.
///         D43 runs only while the audio length is known. Nothing here changes the beat map: D44 points at
///         Tempo map ▸ Add measures at start, the explicit, undoable verb. The findings are about the room
///         around the chart, so they carry its time, not the first sound itself.
///     </para>
/// </summary>
public sealed class LeadInRule : IArrangementRule
{
    /// <summary>The lead-in experienced charters leave before the first note.</summary>
    public const double SuggestedLeadInMs = 10_000.0;

    /// <summary>The room they leave between the last note and the end of the audio.</summary>
    public const double SuggestedTailMs = 2_000.0;

    /// <summary>D02's limit: under it the lead-in is an error D02 already reports.</summary>
    private const double HardLeadInMs = 1_000.0;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        if (beats.Count == 0) return;

        var first = FractionalPosition.Zero;
        var last = FractionalPosition.Zero;
        var any = false;
        foreach (var level in context.Arrangement.Levels)
        {
            foreach (var note in level.Notes)
            {
                if (!any || note.Position < first) first = note.Position;
                if (!any || note.EndPosition > last) last = note.EndPosition;
                any = true;
            }

            foreach (var chord in level.Chords)
            {
                if (!any || chord.Position < first) first = chord.Position;
                if (!any || chord.EndPosition > last) last = chord.EndPosition;
                any = true;
            }
        }

        if (!any) return;

        var firstMs = first.GetPositionMs(beats);
        if (firstMs is >= HardLeadInMs and < SuggestedLeadInMs)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.LeadInShort, "Validation_LeadInShort", firstMs,
                args: [firstMs / 1000.0, SuggestedLeadInMs / 1000.0]));
        }

        var lastMs = last.GetPositionMs(beats);
        if (context.TotalAudioLengthMs is { } audioMs && audioMs - lastMs < SuggestedTailMs)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.TailShort, "Validation_TailShort", lastMs,
                args: [Math.Max(0, audioMs - lastMs) / 1000.0, SuggestedTailMs / 1000.0]));
        }

        if (first < SecondDownbeat(beats))
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.NoCountMeasure, "Validation_NoCountMeasure", firstMs));
        }
    }

    /// <summary>
    ///     Where the second measure starts: the second beat flagged as a measure start, or, on a grid without
    ///     measure flags, the first beat's <see cref="BeatMarker.BeatsInMeasure" /> beats in.
    /// </summary>
    private static FractionalPosition SecondDownbeat(IList<BeatMarker> beats)
    {
        var starts = 0;
        for (var i = 0; i < beats.Count; i++)
        {
            if (!beats[i].IsMeasure) continue;
            if (++starts == 2) return new FractionalPosition(i);
        }

        return starts == 0 ? new FractionalPosition(Math.Max(1, beats[0].BeatsInMeasure)) : new FractionalPosition(beats.Count);
    }
}
