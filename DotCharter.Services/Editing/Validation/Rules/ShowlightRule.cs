namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>Checks authored Rocksmith showlights for missing lanes and a closing fog change.</summary>
public sealed class ShowlightRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        if (context.Project?.Showlights is not { Count: > 0 } showlights) return;

        var beats = context.Beats;
        var songEnd = context.TotalAudioLengthMs is { } length
            ? TimelineMath.PositionFromMs(length, beats)
            : new FractionalPosition(beats.Count - 1);
        var ordered = showlights.OrderBy(light => light.Position)
            .ThenBy(light => ShowlightCatalog.GetSortOrder(light.Type, light.Note)).ToList();
        var firstTimeMs = ordered[0].GetTimeMs(beats);
        var hasBeam = false;
        Showlight? firstFog = null;
        Showlight? lastInSongFog = null;
        var lastInSongFogNote = 0;
        var hasClosingFog = false;

        foreach (var light in ordered)
        {
            if (!ShowlightCatalog.TryGetDefinition(light.Type, light.Note, out var definition)) continue;
            if (definition.Lane == ShowlightLane.Beams) hasBeam = true;
            if (definition.Lane != ShowlightLane.Fog) continue;

            firstFog ??= light;
            if (light.Position < songEnd)
            {
                lastInSongFog = light;
                lastInSongFogNote = definition.Note;
            }
            else if (lastInSongFog != null && definition.Note != lastInSongFogNote)
            {
                hasClosingFog = true;
            }
        }

        if (!hasBeam)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.ShowlightSequence,
                "Validation_ShowlightsMissingBeam", firstTimeMs));
        }

        if (firstFog == null)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.ShowlightSequence,
                "Validation_ShowlightsMissingFog", firstTimeMs));
        }
        else if (!hasClosingFog)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.ShowlightSequence,
                "Validation_ShowlightsClosingFog", (lastInSongFog ?? firstFog).GetTimeMs(beats)));
        }
    }
}
