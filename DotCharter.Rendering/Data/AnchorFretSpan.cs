namespace DotCharter.Rendering.Data;

/// <summary>
///     THE fret range an anchor (fret-hand position) opens, and the world span it
///     covers. Pure, so the clamping can be proven without a GPU.
/// </summary>
/// <remarks>
///     <para>
///         The rule is <b>clamp the fret FIRST, then derive the width from the
///         clamped value</b>. Six call sites re-derived it by hand and three of
///         them clamped only the LOWER bound — <c>fMin = Math.Max(1, fret)</c>
///         paired with <c>fMax = Math.Min(24, fret + width - 1)</c> — so a
///         fret-0 anchor made the FHP lane fill, the anchor sweep and the
///         fret-number rail one fret NARROWER than the beat lines, the chord
///         box, the open-note bar and the camera, all of which clamp first.
///         Two more clamped nothing at all and drew the hand-shape box and the
///         arpeggio bracket starting at <c>GetLinearFretPosition(-1)</c> — a
///         whole fret to the LEFT of the nut.
///     </para>
///     <para>
///         A fret-0 anchor IS malformed (the inspector's L02
///         <c>CapoAnchorRangeRule</c> flags it) but three importers still admit
///         one — see <see cref="HighwayLaneOpacity" />, which was extracted for
///         the same reason after a fret-0 anchor threw the frame away entirely.
///         The renderer must never be what enforces chart validity, and it must
///         never disagree with itself about where the hand is.
///     </para>
/// </remarks>
public static class AnchorFretSpan
{
    /// <summary>Highest fret the highway draws.</summary>
    public const int MaxFret = 24;

    /// <summary>Width an anchor is treated as having when it declares none.</summary>
    public const int DefaultWidth = 4;

    /// <summary>Inclusive fret range for an anchor at <paramref name="fret" />
    /// spanning <paramref name="width" /> frets. Safe for any input, including
    /// a fret-0, zero-width or past-fret-24 anchor.</summary>
    public static (int minFret, int maxFret) FretRange(int fret, int width)
    {
        var minFret = fret <= 0 ? 1 : Math.Min(MaxFret, fret);
        var maxFret = Math.Min(MaxFret, minFret + Math.Max(1, width) - 1);
        if (maxFret < minFret) maxFret = minFret;
        return (minFret, maxFret);
    }

    /// <summary>The same range as world x, nut-side edge to bridge-side edge.</summary>
    public static (float x0, float x1) WorldSpan(int fret, int width)
    {
        var (minFret, maxFret) = FretRange(fret, width);
        return (FretboardConstants.GetLinearFretPosition(minFret - 1),
            FretboardConstants.GetLinearFretPosition(maxFret));
    }

    /// <summary>Fret range of the anchor governing <paramref name="atTime" />
    /// — the latest one at or before it, else the first, else the default first
    /// position.</summary>
    public static (int minFret, int maxFret) FretRangeAt(Render.RenderAnchor[] anchors, int anchorCount, double atTime)
    {
        var anchor = AnchorAt(anchors, anchorCount, atTime);
        return anchor.HasValue
            ? FretRange(anchor.Value.Fret, anchor.Value.Width)
            : FretRange(1, DefaultWidth);
    }

    /// <summary>World span of the anchor governing <paramref name="atTime" />.</summary>
    public static (float x0, float x1) WorldSpanAt(Render.RenderAnchor[] anchors, int anchorCount, double atTime)
    {
        var (minFret, maxFret) = FretRangeAt(anchors, anchorCount, atTime);
        return (FretboardConstants.GetLinearFretPosition(minFret - 1),
            FretboardConstants.GetLinearFretPosition(maxFret));
    }

    /// <summary>The latest anchor at or before <paramref name="atTime" />,
    /// falling back to the first one (a chart whose first anchor starts after
    /// the playhead still has a hand position).</summary>
    public static Render.RenderAnchor? AnchorAt(Render.RenderAnchor[] anchors, int anchorCount, double atTime)
    {
        for (var i = Math.Min(anchorCount, anchors.Length) - 1; i >= 0; i--)
        {
            if (anchors[i].Time <= atTime)
                return anchors[i];
        }

        return anchorCount > 0 && anchors.Length > 0 ? anchors[0] : null;
    }
}
