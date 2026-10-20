using DotCharter.Rendering.Data.Render;

namespace DotCharter.Rendering.Data;

/// <summary>
///     Pure per-fret lane opacity for the 3D highway's fret lines: the base
///     wash, the dimmed preview of anchors inside the look-ahead horizon, and
///     the full-brightness span of the anchor the playhead is currently inside.
/// </summary>
/// <remarks>
///     <para>
///         Extracted from <c>HighwayRenderComponent.RenderFretboardLanes</c> so
///         the indexing can be proven without a GPU. It was a raw
///         <c>int[25]</c> walked with <c>anchor.Fret - 1</c> and no lower
///         clamp: a fret-0 anchor indexed −1 and threw
///         <see cref="IndexOutOfRangeException" /> out of the frame. The 3D
///         driver retried, hit its consecutive-failure ceiling and killed the
///         render loop — so one malformed anchor left the preview permanently
///         dead for the session with a single log line, while the 2D timeline
///         kept working and made it read as a 3D bug rather than a data bug.
///     </para>
///     <para>
///         A fret-0 anchor IS malformed (the inspector's L02
///         CapoAnchorRangeRule flags it), but three importers still admit one
///         (<c>RsXmlSongReader</c>, <c>RscpArrangementParser</c>,
///         the package reader's SNG mapper) and the RENDERER must never be what
///         enforces chart validity.
///     </para>
/// </remarks>
public static class HighwayLaneOpacity
{
    /// <summary>Number of fret lines the highway draws (0..24 inclusive).</summary>
    public const int LaneCount = 25;

    /// <summary>Baseline wash for a fret no anchor touches.</summary>
    public const int BaseOpacity = 32;

    /// <summary>An anchor inside the look-ahead horizon, not yet reached.</summary>
    public const int UpcomingOpacity = 96;

    /// <summary>The anchor span the playhead is currently inside.</summary>
    public const int ActiveOpacity = 255;

    /// <summary>
    ///     Fills <paramref name="opacity" /> (which must hold at least
    ///     <see cref="LaneCount" /> entries) for the given playhead time.
    ///     Allocation-free; safe for any anchor data, including malformed
    ///     fret-0 / zero-width / past-fret-24 anchors.
    /// </summary>
    public static void Fill(RenderAnchor[] anchors, int anchorCount, double timeMs, double horizonMs,
        Span<int> opacity)
    {
        for (var i = 0; i < LaneCount; i++) opacity[i] = BaseOpacity;

        for (var i = 0; i < anchorCount; i++)
        {
            var anchor = anchors[i];
            if (anchor.Time <= timeMs || anchor.Time >= timeMs + horizonMs) continue;

            var fMin = Math.Max(0, (int)anchor.Fret);
            var fMax = Math.Min(LaneCount - 1, anchor.Fret + Math.Max((byte)1, anchor.Width) - 1);
            for (var fret = fMin; fret <= fMax; fret++)
                opacity[fret] = Math.Max(opacity[fret], UpcomingOpacity);
        }

        RenderAnchor? current = null;
        for (var i = anchorCount - 1; i >= 0; i--)
        {
            if (anchors[i].Time <= timeMs)
            {
                current = anchors[i];
                break;
            }
        }

        if (!current.HasValue) return;

        // `Fret - 1` lights the fret BELOW the anchor as a lead-in. The
        // Math.Max is the required guard; every sibling call
        // site (BoardGeometryComponent.CalculateMinFret,
        // HighwayRenderComponent.GetAnchorSpan, Fretboard3DEngine
        // .CalculateCameraMvp) already clamps the same way.
        //
        // The UPPER clamp matters just as much: `Fret` is a byte, so an anchor
        // past fret 25 gave `Math.Clamp(value, min: 26, max: 24)`, and Clamp
        // THROWS when min > max — the same permanent-preview-death failure
        // through a second door.
        var width = Math.Max((byte)1, current.Value.Width);
        var activeMin = Math.Clamp(current.Value.Fret - 1, 0, LaneCount - 1);
        var activeMax = Math.Clamp(current.Value.Fret + width - 1, activeMin, LaneCount - 1);
        for (var fret = activeMin; fret <= activeMax; fret++) opacity[fret] = ActiveOpacity;
    }
}
