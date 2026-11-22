namespace DotCharter.Analysis.Lyrics;

/// <summary>Tuning of <see cref="LyricOnsetSnap" />; the defaults are the measured ones.</summary>
public sealed record LyricOnsetSnapOptions
{
    /// <summary>How far an onset may move, either way.</summary>
    public double WindowSeconds { get; init; } = 0.08;

    /// <summary>How far an onset may move EARLIER; null uses <see cref="WindowSeconds" />.</summary>
    public double? EarlierWindowSeconds { get; init; } = 0.05;

    /// <summary>Share of a peak's strength lost at the window's edge (linear in distance), so a near peak wins a tie.</summary>
    public double DistancePenalty { get; init; }

    /// <summary>Half-width of the span whose median the peak must stand out from.</summary>
    public double ContextSeconds { get; init; } = 0.3;

    /// <summary>The peak must reach this multiple of the context median.</summary>
    public double PeakOverMedian { get; init; } = 3.0;

    /// <summary>The same for a syllable that opens on an unvoiced consonant (its 2-8 kHz flux).</summary>
    public double UnvoicedPeakOverMedian { get; init; } = 8.0;

    /// <summary>The peak must also reach this absolute onset strength (silence has none).</summary>
    public double MinPeak { get; init; } = 10.0;

    /// <summary>No syllable may be left shorter than this, and no onset may come this close to a neighbour.</summary>
    public double MinDurationSeconds { get; init; } = 0.05;

    /// <summary>Weight of the voicing rise in the onset function of a vowel-first syllable.</summary>
    public double VoicingWeight { get; init; } = 20.0;

    /// <summary>Added to a snapped onset: where in its peak frame an onset sits.</summary>
    public double PeakOffsetSeconds { get; init; }

    /// <summary>How far from an onset <see cref="LyricOnsetSnap" /> reads the stem.</summary>
    public double ReachSeconds => Math.Max(ContextSeconds, Math.Max(WindowSeconds, EarlierWindowSeconds ?? 0));

    /// <summary>Interpolated syllables were never heard; snap them too.</summary>
    public bool IncludeInterpolated { get; init; }
}

/// <summary>
///     After alignment, moves each syllable onset to the clear onset of the vocal stem
///     near it. CTC fires somewhere inside a syllable's first sound; the stem
///     shows where that sound begins: a consonant as a 2-8 kHz flux burst, a voiced
///     consonant as whole-band flux, a vowel as whole-band flux plus the voicing rise.
///     <para>
///         An onset moves only when the strongest peak of its own onset function within
///         <see cref="LyricOnsetSnapOptions.WindowSeconds" /> stands out from the median
///         of the surrounding <see cref="LyricOnsetSnapOptions.ContextSeconds" />. It never
///         crosses its neighbours and leaves every syllable at least
///         <see cref="LyricOnsetSnapOptions.MinDurationSeconds" /> long; a previous
///         syllable that would overlap the moved onset is ended there.
///     </para>
/// </summary>
public static class LyricOnsetSnap
{
    /// <summary>
    ///     Whether the snap runs for <paramref name="sheet" />: not on romaji, whose references are
    ///     already at the annotation noise floor and lost 0.4 points in the 50 ms bucket.
    /// </summary>
    public static bool AppliesTo(LyricSheet sheet) => !LyricScriptDetector.IsLikelyRomaji(sheet);

    /// <summary>
    ///     Snaps <paramref name="syllables" /> (index-parallel to <paramref name="sheet" />'s
    ///     syllables) to <paramref name="features" />. Returns a new list; the input is untouched.
    /// </summary>
    public static IReadOnlyList<AlignedSyllable> Apply(
        IReadOnlyList<AlignedSyllable> syllables, LyricSheet sheet, VocalOnsetFeatures features,
        LyricOnsetSnapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syllables);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(features);
        options ??= new LyricOnsetSnapOptions();

        var classes = new SyllableOnsetClass[syllables.Count];
        for (var i = 0; i < classes.Length; i++)
            classes[i] = SyllableOnsetClassifier.Classify(i < sheet.Syllables.Count ? sheet.Syllables[i].Text : null);

        return Apply(syllables, classes, features, options);
    }

    /// <summary>The same with the onset classes given directly (tests).</summary>
    public static IReadOnlyList<AlignedSyllable> Apply(
        IReadOnlyList<AlignedSyllable> syllables, IReadOnlyList<SyllableOnsetClass> classes,
        VocalOnsetFeatures features, LyricOnsetSnapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syllables);
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(features);
        options ??= new LyricOnsetSnapOptions();

        var result = syllables.ToArray();
        if (features.FrameCount == 0) return result;

        var context = new List<float>();
        for (var i = 0; i < result.Length; i++)
        {
            var syllable = result[i];
            if (syllable.IsInterpolated && !options.IncludeInterpolated) continue;

            var onsetClass = i < classes.Count ? classes[i] : SyllableOnsetClass.Vowel;
            var target = FindOnset(syllable.StartSeconds, onsetClass, features, options, context);
            if (target is not { } start) continue;

            start += options.PeakOffsetSeconds;
            var lower = i > 0 ? result[i - 1].StartSeconds + options.MinDurationSeconds : double.NegativeInfinity;
            var upper = Math.Min(
                syllable.EndSeconds - options.MinDurationSeconds,
                i + 1 < result.Length ? result[i + 1].StartSeconds - options.MinDurationSeconds : double.PositiveInfinity);
            if (start < lower || start > upper || start == syllable.StartSeconds) continue;

            result[i] = syllable with { StartSeconds = start };
            if (i > 0 && result[i - 1].EndSeconds > start) result[i - 1] = result[i - 1] with { EndSeconds = start };
        }

        return result;
    }

    /// <summary>The time of the clear onset near <paramref name="seconds" />, or null when there is none.</summary>
    private static double? FindOnset(
        double seconds, SyllableOnsetClass onsetClass, VocalOnsetFeatures features, LyricOnsetSnapOptions options,
        List<float> context)
    {
        var from = features.FrameAt(seconds - (options.EarlierWindowSeconds ?? options.WindowSeconds));
        var to = features.FrameAt(seconds + options.WindowSeconds);
        var best = -1;
        var bestStrength = 0f;
        var bestScore = 0.0;
        for (var k = from; k <= to; k++)
        {
            // A peak far from the aligned onset must be stronger to win than one close to it.
            var strength = Strength(k, onsetClass, features, options);
            var distance = Math.Abs(features.FrameSeconds(k) - seconds) / options.WindowSeconds;
            var score = strength * (1 - options.DistancePenalty * Math.Min(1, distance));
            if (score <= bestScore) continue;

            best = k;
            bestStrength = strength;
            bestScore = score;
        }

        if (best < 0 || bestStrength < options.MinPeak) return null;

        context.Clear();
        var contextFrom = features.FrameAt(seconds - options.ContextSeconds);
        var contextTo = features.FrameAt(seconds + options.ContextSeconds);
        for (var k = contextFrom; k <= contextTo; k++) context.Add(Strength(k, onsetClass, features, options));
        context.Sort();
        var median = context[context.Count / 2];

        var ratio = onsetClass == SyllableOnsetClass.Unvoiced ? options.UnvoicedPeakOverMedian : options.PeakOverMedian;

        return bestStrength >= ratio * median ? features.FrameSeconds(best) : null;
    }

    private static float Strength(int frame, SyllableOnsetClass onsetClass, VocalOnsetFeatures features, LyricOnsetSnapOptions options) =>
        onsetClass switch
        {
            SyllableOnsetClass.Unvoiced => features.ConsonantFlux(frame),
            SyllableOnsetClass.Voiced => features.Flux(frame),
            _ => features.Flux(frame) + (float)options.VoicingWeight * features.VoicingRise(frame)
        };
}
