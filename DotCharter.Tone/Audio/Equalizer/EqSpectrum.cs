namespace DotCharter.Tone.Audio.Equalizer;

/// <summary>
///     Which spectrum the plot is showing, and why.
/// </summary>
public enum EqSpectrumKind
{
    /// <summary>The engine's analyser, with real signal in it.</summary>
    Live = 0,

    /// <summary>A fixed, made-up average spectrum — drawn dimmer and MARKED,
    /// because nothing is playing (or the engine cannot answer).</summary>
    Preview = 1
}

/// <summary>
///     The analyser's two shapes: the level mapping the live frame is drawn
///     through, and the fixed PREVIEW spectrum the plot falls back to while
///     nothing is playing.
///     <para>
///         <b>Why a preview exists at all.</b> Drawing nothing
///         without a live frame would rest on the argument that an invented hump is "the
///         control that lies". That argument is right about an UNMARKED one: the
///         mockup (<c>EQ and Filters.dc.html</c>, the <c>SPEC</c> table) draws a
///         static average spectrum behind the curve at all times and labels it
///         "Average spectrum" in its legend. Without it a stopped transport gives
///         a bare grid, and the whole point of the zone tints and the string
///         ticks — showing the user WHERE in the spectrum they are cutting — has
///         nothing to sit on. So the honesty is kept where it belongs: the
///         preview is dimmer, it is TAGGED in the plot, and
///         <see cref="EqSpectrumKind" /> makes "which one am I looking at" a
///         value rather than an inference.
///     </para>
///     <para>
///         Pure and static for the same reason <see cref="EqCurve" /> is: the
///         shape is the thing that can regress, and asserting it must not need a
///         UI thread.
///     </para>
/// </summary>
public static class EqSpectrum
{
    /// <summary>The bottom of the analyser well, in dBFS. A bin at or below this
    /// draws flat on the floor.</summary>
    public const double FloorDb = -72.0;

    /// <summary>
    ///     At or below this, a whole frame counts as SILENCE and the plot shows
    ///     the preview instead.
    ///     <para>
    ///         Deliberately above <see cref="FloorDb" />: a stopped transport
    ///         still feeds the analyser the interface's own noise floor, so
    ///         "every bin is exactly at the floor" is a state that essentially
    ///         never occurs and a test for it would leave the live analyser
    ///         drawing a flat line whenever nothing is playing — which is the
    ///         owner-reported behaviour this replaces.
    ///     </para>
    /// </summary>
    public const double SilenceThresholdDb = -70.0;

    /// <summary>How many points the mockup's own <c>SPEC</c> table has. The
    /// preview's jitter is seeded off this scale, so the drawn shape is identical
    /// at any requested resolution.</summary>
    public const int PreviewPoints = 150;

    /// <summary>One magnitude in dBFS to a 0..1 fill height.</summary>
    public static double LevelFor(double magnitudeDb) =>
        Math.Clamp((magnitudeDb - FloorDb) / -FloorDb, 0.0, 1.0);

    /// <summary>
    ///     Whether a frame from the engine carries no signal worth drawing —
    ///     null, too short to be a spectrum, or every bin at or under
    ///     <see cref="SilenceThresholdDb" />.
    /// </summary>
    public static bool IsSilent(IReadOnlyList<float>? frameDb)
    {
        if (frameDb is null || frameDb.Count < 2) return true;

        for (var i = 0; i < frameDb.Count; i++)
        {
            var value = frameDb[i];
            // A NaN bin is not a loud bin. Left untested it would read as "not
            // silent" (every comparison against NaN is false) and pin the plot
            // on a live frame that draws nothing.
            if (float.IsNaN(value)) continue;
            if (value > SilenceThresholdDb) return false;
        }

        return true;
    }

    /// <summary>The live frame as 0..1 fill heights, one per bin.</summary>
    public static double[] Levels(IReadOnlyList<float> frameDb)
    {
        ArgumentNullException.ThrowIfNull(frameDb);

        var result = new double[frameDb.Count];
        Levels(frameDb, result);
        return result;
    }

    /// <summary>
    ///     <see cref="Levels(IReadOnlyList{float})" /> into a caller-owned
    ///     buffer of at least <c>frameDb.Count</c> elements; the first
    ///     <c>frameDb.Count</c> are written. The plot keeps one buffer across
    ///     its ~15 Hz repaints instead of allocating a <c>double[128]</c> per frame.
    /// </summary>
    public static void Levels(IReadOnlyList<float> frameDb, Span<double> into)
    {
        ArgumentNullException.ThrowIfNull(frameDb);
        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, frameDb.Count);

        for (var i = 0; i < frameDb.Count; i++)
        {
            var value = frameDb[i];
            into[i] = float.IsNaN(value) ? 0.0 : LevelFor(value);
        }
    }

    /// <summary>
    ///     The preview spectrum as 0..1 fill heights, evenly spaced along the
    ///     LOG frequency axis (bin <c>i</c> is at
    ///     <see cref="EqCurve.NormalizedToFrequency" /> of <c>i/(n-1)</c>, i.e.
    ///     exactly one plot column).
    ///     <para>
    ///         Three log-Gaussian humps plus a floor and a deterministic jitter —
    ///         the mockup's <c>SPEC</c> expression, ported term for term: a body
    ///         hump at 190 Hz, a midrange one at 2.1 kHz, air at 9 kHz. It is a
    ///         PICTURE of what a mixed rock track looks like, not a measurement,
    ///         which is why it is fixed and why the caller must mark it.
    ///     </para>
    /// </summary>
    /// <param name="pointCount">How many bins to produce. At least 2.</param>
    public static double[] Preview(int pointCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pointCount, 2);

        var result = new double[pointCount];
        for (var i = 0; i < pointCount; i++)
        {
            var t = i / (double)(pointCount - 1);
            var f = EqCurve.NormalizedToFrequency(t);

            var body = Math.Exp(-Square(Math.Log(f / 190.0)) / 1.05) * 0.86;
            var mid = Math.Exp(-Square(Math.Log(f / 2100.0)) / 2.3) * 0.5;
            var air = Math.Exp(-Square(Math.Log(f / 9000.0)) / 2.6) * 0.16;

            // The mockup's own hash, seeded on the SAME 0..149 scale whatever the
            // requested resolution — so a 128-bin preview is a resampling of one
            // shape rather than a different shape.
            var seed = t * (PreviewPoints - 1);
            var jitter = Frac(Frac(Math.Sin(seed * 12.9898) * 43758.5453) + 1.0);

            result[i] = Math.Clamp(body + mid + air + 0.05 + jitter * 0.07, 0.0, 1.0);
        }

        return result;
    }

    private static double Square(double x) => x * x;

    /// <summary>JavaScript's <c>%</c> on doubles, which is C#'s too: the
    /// remainder keeps the dividend's SIGN, which is why the mockup adds one and
    /// takes it again.</summary>
    private static double Frac(double x) => x % 1.0;
}
