using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>Builds the tone-conversion badge from the presets and reports the current request resolved.</summary>
internal static class RsToneSummaryBuilder
{
    /// <summary>The half of a summary that is the same whichever SOURCE the tone
    /// bodies came from: resolve the arrangement's referenced keys against the
    /// converted presets and roll up their conversion notes.</summary>
    public static RsToneConversionSummary Build(
        string source,
        Arrangement arrangement,
        Dictionary<string, DcRigPreset> presets,
        Dictionary<string, ConversionReport> reports,
        bool dspAvailable, IAppLogger? logger)
    {
        var referenced = RsTonePlaybackCoordinator.ReferencedToneKeys(arrangement);
        var resolved = new List<string>();
        foreach (var key in referenced)
        {
            if (presets.ContainsKey(key.Trim()))
            {
                resolved.Add(key);
            }
            else
            {
                logger?.Info(AppLogCategory.Audio,
                    $"RsTone: chart tone '{key}' has no converted preset (available: {RsTonePlaybackCoordinator.DescribeKeys(presets)}).");
            }
        }

        logger?.Info(AppLogCategory.Audio,
            $"RsTone: summary for '{source}' — dspAvailable={dspAvailable}, " +
            $"referencedTones={referenced.Count}, resolved={resolved.Count}, presetKeys={presets.Count}.");

        if (resolved.Count == 0)
            return new RsToneConversionSummary(false, dspAvailable, 0, 0, 0, Array.Empty<string>());

        var approx = 0;
        var skipped = 0;
        var notes = new List<string>();
        foreach (var key in resolved)
        {
            if (!reports.TryGetValue(key.Trim(), out var rep)) continue;
            approx += rep.ApproximatedCount;
            skipped += rep.SkippedCount;
            foreach (var note in rep.Notes)
            {
                if (note.Kind is ConversionNoteKind.Approximated or ConversionNoteKind.Skipped &&
                    notes.Count < 16)
                {
                    notes.Add($"{key}: {note.Message}");
                }
            }
        }

        return new RsToneConversionSummary(true, dspAvailable, resolved.Count, approx, skipped, notes)
        {
            // The RESOLVED keys, not the referenced ones: a chip must name a tone
            // the rig will really play.
            ToneNames = resolved
        };
    }
}
