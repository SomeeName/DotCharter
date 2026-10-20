namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>Where a level sits against the two thresholds that matter.</summary>
public enum FireLevelBand
{
    /// <summary>Below −6 dB — green. Playing at a healthy level.</summary>
    Good,

    /// <summary>−6 dB to −2 dB — amber. Close to the ceiling.</summary>
    Hot,

    /// <summary>Above −2 dB — red. Clipping territory.</summary>
    Over
}

/// <summary>
/// The pure half of <see cref="FireLevelMeter"/>: where a dB level falls
/// against the design's two thresholds, and which of a meter's bars are lit.
///
/// <para><b>The thresholds are SEMANTIC, not decorative</b> — "Green below −6,
/// amber −6..−2, red above" is what the meter MEANS, so it is a pure function
/// pinned on both sides of both boundaries (<c>FireLevelScaleTests</c>) rather
/// than a colour picked per bar index. A meter that colours by index looks
/// identical and lies the moment its scale changes.</para>
///
/// <para>The default −30 dB … +1 dB scale is not arbitrary: it is the range at
/// which the two thresholds land exactly on the mockup's 14-bar footer meter
/// (<c>Amp and FX Rig.dc.html</c>: <c>i &gt; 11 → red, i &gt; 9 → amber</c>),
/// i.e. ten green, two amber, two red. The picture falls out of the rule
/// instead of being drawn beside it.</para>
/// </summary>
public static class FireLevelScale
{
    /// <summary>At and above this, a bar is amber. "Green BELOW −6" — so −6
    /// itself is already amber.</summary>
    public const double HotThresholdDb = -6.0;

    /// <summary>ABOVE this, a bar is red. "Red above −2" — so −2 itself is
    /// still amber.</summary>
    public const double OverThresholdDb = -2.0;

    /// <summary>Bottom of the default scale.</summary>
    public const double DefaultMinimumDb = -30.0;

    /// <summary>Top of the default scale — 1 dB of "you are over" headroom past
    /// the red threshold.</summary>
    public const double DefaultMaximumDb = 1.0;

    /// <summary>The mockup's footer meter is 14 bars.</summary>
    public const int DefaultSegmentCount = 14;

    /// <summary>Bar geometry: 4 px wide, 2 px apart, 1 px corner (spec: "3–4px
    /// bars, 2px gap").</summary>
    public const double DefaultBarWidth = 4.0;

    /// <inheritdoc cref="DefaultBarWidth"/>
    public const double DefaultBarGap = 2.0;

    /// <inheritdoc cref="DefaultBarWidth"/>
    public const double BarCornerRadius = 1.0;

    /// <summary>Bar heights ramp across the row (spec: "heights 5–14px"). Set
    /// both to the same number for the flat footer row.</summary>
    public const double DefaultMinBarHeight = 5.0;

    /// <inheritdoc cref="DefaultMinBarHeight"/>
    public const double DefaultMaxBarHeight = 14.0;

    /// <summary>Alpha of an UNLIT bar (spec: "Unlit rgba(255,255,255,.10–.12)").
    /// Applied to the shared <c>FireSurfaceTrack</c> token, which already IS
    /// 10 % white — so this is 1.0 and exists to say the decision was made.</summary>
    public const double UnlitAlpha = 1.0;

    /// <summary>
    ///     The colour of an UNLIT bar: the track token at its OWN translucency,
    ///     scaled by <see cref="UnlitAlpha"/>. The alpha is MULTIPLIED, never
    ///     replaced — <c>FireUnitState.At</c> REPLACES the alpha channel, and
    ///     replacing promoted the 10 %-white track to solid white, so an idle
    ///     meter (level −∞, zero lit segments) drew a wall of bright bars and
    ///     read as a HOT input while showing no audio (live-audit finding).
    /// </summary>
    public static Avalonia.Media.Color UnlitColor(Avalonia.Media.Color track)
        => Avalonia.Media.Color.FromArgb(
            (byte)Math.Clamp(Math.Round(UnlitAlpha * track.A), 0, 255),
            track.R, track.G, track.B);

    /// <summary>Which band a dB level falls in.</summary>
    public static FireLevelBand BandFor(double db)
        => db > OverThresholdDb ? FireLevelBand.Over
            : db >= HotThresholdDb ? FireLevelBand.Hot
            : FireLevelBand.Good;

    /// <summary>
    /// The dB a bar TOPS OUT at — the level at which it lights, and the level
    /// its colour is decided from. Bar 0 is the quietest.
    /// </summary>
    public static double SegmentTopDb(int index, int count, double minimumDb, double maximumDb)
    {
        if (count <= 0) return maximumDb;
        var span = maximumDb - minimumDb;
        return minimumDb + (index + 1) * span / count;
    }

    /// <summary>The colour band of one bar of the row.</summary>
    public static FireLevelBand BandForSegment(int index, int count, double minimumDb, double maximumDb)
        => BandFor(SegmentTopDb(index, count, minimumDb, maximumDb));

    /// <summary>
    /// How many bars are lit at <paramref name="db"/>. Silence (or anything at
    /// or below the floor, including <see cref="double.NegativeInfinity"/>)
    /// lights none; the top of the scale lights all of them.
    /// <para>This is also the meter's REPAINT GATE: a level that moves without
    /// changing this number changes nothing on screen, and a meter fed from
    /// audio must not invalidate for it.</para>
    /// </summary>
    public static int LitSegments(double db, int count, double minimumDb, double maximumDb)
    {
        if (count <= 0) return 0;
        var span = maximumDb - minimumDb;
        if (span <= 0 || double.IsNaN(db)) return 0;
        if (double.IsNegativeInfinity(db)) return 0;

        var lit = (int)Math.Ceiling((db - minimumDb) / span * count);
        return Math.Clamp(lit, 0, count);
    }

    /// <summary>Height of one bar of the ramp — <paramref name="minHeight"/> at
    /// the left, <paramref name="maxHeight"/> at the right.</summary>
    public static double SegmentHeight(int index, int count, double minHeight, double maxHeight)
    {
        if (count <= 1) return maxHeight;
        var t = Math.Clamp(index / (double)(count - 1), 0.0, 1.0);
        return minHeight + t * (maxHeight - minHeight);
    }

    /// <summary>
    ///     The same height with a repeating SAWTOOTH silhouette instead of one
    ///     ramp: <c>min + (index % cycle) × step</c>, which is
    ///     `Play Menus.dc.html`'s own <c>4 + (i % 3) * 2</c>.
    ///     <para>
    ///         A <paramref name="cycle" /> of 0 or 1 means "no cycle" and falls
    ///         through to <see cref="SegmentHeight(int,int,double,double)" />, so
    ///         the amp rig's wedge is untouched. Which silhouette a meter wears
    ///         is decoration; the COLOUR stays semantic in both
    ///         (<see cref="BandForSegment" />) — a meter whose amber is chosen by
    ///         index is a meter that lies the moment the scale moves.
    ///     </para>
    /// </summary>
    public static double SegmentHeight(int index, int count, double minHeight, double maxHeight, int cycle)
    {
        if (cycle <= 1) return SegmentHeight(index, count, minHeight, maxHeight);
        var step = (maxHeight - minHeight) / (cycle - 1);
        return minHeight + index % cycle * step;
    }

    /// <summary>The row's width for a bar count/width/gap.</summary>
    public static double RowWidth(int count, double barWidth, double gap)
        => count <= 0 ? 0 : count * barWidth + (count - 1) * gap;
}
