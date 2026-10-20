namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     How tall a unit's picture stands in the ROUTING MAP. The map is AmpliTube's
///     lower panel: every unit its own gear, small and schematic, with the SELECTED
///     one standing big on the stage above — so a face takes the map's height up to
///     <see cref="Max" />, shares it between a split region's lanes, and gives way
///     to the map's width on a long chain. Pure, so the arithmetic is pinned in
///     the default gate.
/// </summary>
public static class RigBoardFaceSize
{
    /// <summary>The floor: a thumbnail you still recognise. Three lanes on a
    /// 1280×800 map need less than this, and the map's fit then scales the whole
    /// region down (AmpFxRigView.BoardFitScale fits the height too). Nothing is
    /// edited here — the stage above is where the knobs are.</summary>
    public const double Min = 56;

    /// <summary>The map is an overview: the gear is edited big on the stage, so a
    /// unit never grows past a thumbnail.</summary>
    public const double Max = 120;

    /// <summary>Everything in the map's height that is not a face: the scroller's
    /// padding (2 × 12), the lane caption over the path, a unit's frame and its
    /// one-line name, the caption row under the graph and a horizontal scrollbar.</summary>
    public const double BoardChrome = 24 + 16 + 44 + 34 + 10;

    /// <summary>What each lane past the first adds: its frame and name, the gap
    /// between stacked lanes and the lane row's padding.</summary>
    public const double LaneChrome = 44 + 6 + 6;

    /// <summary>The face height for a board <paramref name="boardHeight" /> DIPs tall
    /// running <paramref name="lanes" /> parallel lanes (1 for a series chain).</summary>
    public static double For(double boardHeight, int lanes)
    {
        if (double.IsNaN(boardHeight) || boardHeight <= 0) return Min;
        lanes = Math.Max(1, lanes);
        var room = boardHeight - BoardChrome - (lanes - 1) * LaneChrome;
        return Math.Round(Math.Clamp(room / lanes, Min, Max));
    }

    /// <summary>
    ///     The next WIDTH limit on the face height, from the height the board was
    ///     just laid out at and how far the chain had to shrink to fit the canvas
    ///     (<paramref name="fit" /> = room ÷ the path's natural width). A chain
    ///     wider than the board gets shorter faces rather than a shrinking
    ///     transform, so the name rows stay readable and the knobs keep the size
    ///     the arithmetic promised.
    ///     <para>
    ///         Converges without overshoot in both directions: the path's width is
    ///         affine in the face height (fixed jacks and wires plus Σ aspect × h),
    ///         so scaling h by <paramref name="fit" /> always lands at or inside the
    ///         room, and the next pass only closes the remaining gap.
    ///     </para>
    /// </summary>
    public static double NextWidthLimit(double laidOutHeight, double fit, double limit)
    {
        if (double.IsNaN(fit) || fit <= 0 || laidOutHeight <= 0) return limit;
        if (fit < 0.98) return Math.Max(Min, Math.Floor(laidOutHeight * fit));
        if (fit > 1.02 && !double.IsPositiveInfinity(limit)) return Math.Floor(laidOutHeight * fit);
        return limit;
    }
}
