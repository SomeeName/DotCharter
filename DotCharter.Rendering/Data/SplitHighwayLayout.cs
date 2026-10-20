namespace DotCharter.Rendering.Data;

/// <summary>How a split highway divides the frame between its players.</summary>
public enum SplitHighwayOrientation
{
    /// <summary>Pick per frame from the frame's own aspect — see
    /// <see cref="SplitHighwayLayout.ResolveOrientation" />.</summary>
    Auto = 0,

    /// <summary>One highway above the other (the frame is cut horizontally).
    /// Each viewport keeps the FULL width, which is the aspect family the
    /// owner-settled camera framing was tuned for.</summary>
    Stacked = 1,

    /// <summary>Highways beside each other (the frame is cut vertically). Each
    /// viewport keeps the full height and half the width.</summary>
    SideBySide = 2
}

/// <summary>A viewport rectangle in the same pixel space the caller hands
/// <c>Fretboard3DEngine.RenderFrame</c> (i.e. the REQUESTED panel size, not the
/// render-scaled swapchain extent — the renderer rescales).</summary>
public readonly record struct HighwayViewportRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
///     Pure tiling policy for the couch co-op split highway: given a frame size
///     and a player count, where does each player's highway go.
///     <para>
///         It is a separate pure type on purpose. Getting this wrong is not a
///         cosmetic bug — a zero- or negative-extent viewport is a Vulkan
///         validation error (VUID-VkViewport-width-01770 /
///         VUID-VkViewport-height-01773) and an overlapping pair means one
///         player's highway draws over the other inside a shared depth buffer.
///         Both are cheap to pin in the default gate and expensive to find on a
///         GPU.
///     </para>
/// </summary>
public static class SplitHighwayLayout
{
    /// <summary>Two players, because the audio interface has two inputs. The
    /// tiling math itself is general, so the cap is a product decision rather
    /// than a limit of this type.</summary>
    public const int MaxPlayers = 4;

    /// <summary>Black band left between two viewports so the split reads as a
    /// deliberate divider rather than a seam. The render pass clears the whole
    /// frame to black and neither viewport covers the band, so it costs no
    /// draw.</summary>
    public const int DividerPx = 2;

    /// <summary>Below this a viewport is not worth rendering (and, at 0, is
    /// illegal). A frame that cannot give every player at least this much in
    /// the split axis falls back to a SINGLE full-frame viewport.</summary>
    public const int MinViewportExtent = 32;

    /// <summary>
    ///     Chooses the split axis for <see cref="SplitHighwayOrientation.Auto" />.
    ///     <para>
    ///         Stacked is the default because the camera's framing is
    ///         aspect-derived (<c>screenScaleX = min(0.5, 1/aspect)</c>,
    ///         <c>screenScaleY = min(1, 0.5·aspect) + 0.05</c>) and was settled
    ///         by the owner against WIDE viewports; halving the width of a
    ///         16:9 frame produces a ~0.9 aspect the framing has never faced and
    ///         squashes the board vertically — the exact failure both rejected
    ///         reframe attempts produced (see
    ///         docs/agents/rendering/highway-pace-and-shaders.md, and
    ///         docs/agents/rendering/split-highway.md for the split's own rules).
    ///         Halving the HEIGHT keeps each viewport in the wide family the
    ///         editor's preview pane already lives in. A portrait window (taller
    ///         than wide) is the one case where stacking would produce the
    ///         narrow viewports instead, so it flips.
    ///     </para>
    /// </summary>
    public static SplitHighwayOrientation ResolveOrientation(
        int frameWidth, int frameHeight, SplitHighwayOrientation requested)
    {
        if (requested != SplitHighwayOrientation.Auto) return requested;
        return frameWidth >= frameHeight
            ? SplitHighwayOrientation.Stacked
            : SplitHighwayOrientation.SideBySide;
    }

    /// <summary>
    ///     Tiles <paramref name="frameWidth" />×<paramref name="frameHeight" />
    ///     into <paramref name="playerCount" /> viewports, writing them into
    ///     <paramref name="destination" /> in player order and returning how many
    ///     were written.
    ///     <para>
    ///         Guarantees, all pinned by <c>SplitHighwayLayoutTests</c>:
    ///         every returned rect has positive extents and lies inside the
    ///         frame; no two rects overlap; consecutive rects are separated by
    ///         exactly <see cref="DividerPx" /> along the split axis; the union
    ///         plus the dividers covers the frame edge to edge (the LAST
    ///         viewport absorbs the integer-division remainder, so a 1081 px
    ///         frame leaves no unpainted row); and a frame too small to split
    ///         degrades to ONE full-frame viewport rather than to something
    ///         illegal.
    ///     </para>
    /// </summary>
    public static int Compute(
        int frameWidth, int frameHeight, int playerCount,
        SplitHighwayOrientation orientation, HighwayViewportRect[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length == 0)
            throw new ArgumentException("Destination must hold at least one rect.", nameof(destination));

        var full = new HighwayViewportRect(0, 0, Math.Max(1, frameWidth), Math.Max(1, frameHeight));

        var count = Math.Clamp(playerCount, 1, Math.Min(MaxPlayers, destination.Length));
        if (count <= 1 || frameWidth <= 0 || frameHeight <= 0)
        {
            destination[0] = full;
            return 1;
        }

        var resolved = ResolveOrientation(frameWidth, frameHeight, orientation);
        var axisLength = resolved == SplitHighwayOrientation.Stacked ? frameHeight : frameWidth;

        // Every divider eats DividerPx of the axis; what remains is shared.
        var usable = axisLength - DividerPx * (count - 1);
        var slice = usable / count;
        if (slice < MinViewportExtent)
        {
            // Too small to split honestly — one player gets the frame. The
            // caller decides what to do about the others; returning a
            // degenerate viewport would be a validation error on the GPU.
            destination[0] = full;
            return 1;
        }

        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            // The last slice absorbs the remainder so the tiling reaches the
            // far edge exactly (integer division otherwise leaves up to
            // count-1 unpainted pixels showing the raw clear colour).
            var extent = i == count - 1 ? axisLength - offset : slice;
            destination[i] = resolved == SplitHighwayOrientation.Stacked
                ? new HighwayViewportRect(0, offset, frameWidth, extent)
                : new HighwayViewportRect(offset, 0, extent, frameHeight);
            offset += extent + DividerPx;
        }

        return count;
    }
}
