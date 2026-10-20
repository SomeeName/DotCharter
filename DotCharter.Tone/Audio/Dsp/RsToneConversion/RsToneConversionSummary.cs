namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// Aggregates the per-tone <see cref="ConversionReport"/>s for the tones an
/// arrangement actually references into exactly what the Play Mode pre-play badge
/// needs, so the view model does zero domain logic. Produced by
/// <c>RsTonePlaybackCoordinator.PrepareForSong</c>.
/// </summary>
public sealed record RsToneConversionSummary(
    bool HasTones,               // the song exposed ≥1 parseable gear tone for this arrangement
    bool DspAvailable,           // false when IDspChainService is the Null impl (no native rig)
    int ToneCount,               // distinct referenced tones rebuilt (ToneBase + ToneA..D / changes)
    int ApproximatedEffectCount, // ConversionNote kind == Approximated across those tones
    int SkippedEffectCount,      // kind == Skipped (pitch / wah / unknown gear)
    IReadOnlyList<string> Notes) // human tooltip lines (already plain text from the converter)
{
    /// <summary>
    ///     The tones that really rebuilt, by the key the chart names them with —
    ///     the pre-play screen's tone chips. Deliberately the RESOLVED list (a
    ///     referenced tone with no converted preset is not in it), so a chip can
    ///     never claim a tone the rig will not actually play.
    ///     <para>Not a positional ctor parameter: it was added after the record
    ///     shipped, and every existing construction site means "no tones named"
    ///     rather than "an empty tone set I forgot to fill".</para>
    /// </summary>
    public IReadOnlyList<string> ToneNames { get; init; } = Array.Empty<string>();

    /// <summary>The subset of <see cref="ToneNames" /> the player has edited in the
    /// rig and saved for this song — they play the edit, not a conversion.</summary>
    public IReadOnlyList<string> EditedToneNames { get; init; } = Array.Empty<string>();

    /// <summary>True when a tone edit may be saved for this song: its tones came
    /// from a library package and the song has an identity to key the edit by.
    /// False for an own chart, whose tones are edited in the Tone Designer.</summary>
    public bool CanSaveToneEdits { get; init; }

    /// <summary>The neutral "no in-game tones here" summary (own charts, RS1
    /// imports). DSP is assumed available — flip it via the ctor when it isn't.</summary>
    public static readonly RsToneConversionSummary None =
        new(false, true, 0, 0, 0, Array.Empty<string>());

    /// <summary>At least one effect couldn't be reproduced 1:1 — surface the
    /// non-blocking "approximated" note. Never a hard failure.</summary>
    public bool HasApproximations => ApproximatedEffectCount + SkippedEffectCount > 0;
}
