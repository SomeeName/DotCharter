using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DotCharter.Tone.UI.Controls;

/// <summary>
///     A <see cref="WrapPanel" /> whose FIRST layout, while it is hidden, is paid a
///     chunk of children at a time — so a prewarm can realize a long,
/// non-virtualized list across several dispatcher turns instead of one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why.</b> The rig's tone browser lays out every preset card in one
///         panel (the owner rejected virtualizing it: scroll lag). Realized ahead of
///         time by the startup prewarm, that was ONE 1.1-2.0 s turn per rig
///         instance (Release R2R), and a click that landed in it waited for all of
///         it. A turn cannot be interrupted, so the only fix is smaller turns.
///     </para>
///     <para>
///         <b>How</b> (<see cref="HiddenChunkGate" />). While the panel is NOT
///         effectively visible, a measure lays out only the first
///         <see cref="RealizedLimit" /> children — one <see cref="ChunkSize" /> to
///         begin with — and <see cref="RealizeNextChunk" /> raises that limit by
///         one chunk. Each child is measured with exactly the constraint
///         <see cref="WrapPanel" /> gives it, so the real layout later finds every
///         realized child's measure valid and only places it. The moment the panel
///         becomes visible the limit is gone for good and it is a plain
///         <see cref="WrapPanel" />: whatever the prewarm has not reached yet is laid
///         out by that show, as it was before. Nothing here writes
///         <c>IsVisible</c>, and nothing changes how the panel looks.
///     </para>
/// </remarks>
public class ChunkedWrapPanel : WrapPanel, IChunkedPanel
{
    /// <summary>How many children one hidden chunk lays out (0: no chunking).</summary>
    public static readonly StyledProperty<int> ChunkSizeProperty =
        AvaloniaProperty.Register<ChunkedWrapPanel, int>(nameof(ChunkSize), 8);

    private readonly HiddenChunkGate _gate;

    /// <summary>Builds the panel, hidden-chunked until it is first shown.</summary>
    public ChunkedWrapPanel() => _gate = new HiddenChunkGate(this, () => ChunkSize);

    /// <summary><inheritdoc cref="ChunkSizeProperty" /></summary>
    public int ChunkSize
    {
        get => GetValue(ChunkSizeProperty);
        set => SetValue(ChunkSizeProperty, value);
    }

    /// <summary>How many children a hidden measure lays out; unbounded once the
    /// panel has been shown (or is showing).</summary>
    public int RealizedLimit => _gate.Limit;

    /// <inheritdoc />
    public bool HasBacklog => _gate.HasBacklog;

    /// <inheritdoc />
    public bool Withholds(Control child) => _gate.Withholds(child);

    /// <inheritdoc />
    public bool RealizeNextChunk() => _gate.Grant();

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_gate.IsChunking) return base.MeasureOverride(availableSize);

        // WrapPanel's own child constraint, so the real layout's measure of an
        // already-realized child is a cache hit.
        var childConstraint = new Size(
            double.IsNaN(ItemWidth) ? availableSize.Width : ItemWidth,
            double.IsNaN(ItemHeight) ? availableSize.Height : ItemHeight);
        var count = _gate.Count;
        for (var i = 0; i < count; i++)
            Children[i].Measure(childConstraint);
        return EstimatedSize(availableSize, count);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_gate.IsChunking) return base.ArrangeOverride(finalSize);
        _gate.ArrangeMeasured();
        return finalSize;
    }

    /// <summary>
    ///     The size the whole list will have, as <see cref="WrapPanel" /> wraps it,
    ///     with each child not yet laid out standing in at the size of the last one
    ///     that was. Nobody sees it — the panel is hidden, and its first show
    ///     measures it again in full — but the scroller around it does: an extent
    ///     of one chunk would leave its scroll bar's parts unrealized, to be built
    ///     on the click.
    /// </summary>
    private Size EstimatedSize(Size availableSize, int measured)
    {
        if (measured == 0) return default;
        var horizontal = Orientation == Orientation.Horizontal;
        var limitU = horizontal ? availableSize.Width : availableSize.Height;
        double lineU = 0, lineV = 0, panelU = 0, panelV = 0;
        var lines = 0;
        var stand = Children[measured - 1].DesiredSize;
        for (var i = 0; i < Children.Count; i++)
        {
            var size = i < measured ? Children[i].DesiredSize : stand;
            var u = !double.IsNaN(horizontal ? ItemWidth : ItemHeight)
                ? horizontal ? ItemWidth : ItemHeight
                : horizontal ? size.Width : size.Height;
            var v = !double.IsNaN(horizontal ? ItemHeight : ItemWidth)
                ? horizontal ? ItemHeight : ItemWidth
                : horizontal ? size.Height : size.Width;
            var spacing = lineU > 0 ? ItemSpacing : 0;
            if (lineU > 0 && lineU + spacing + u > limitU)
            {
                panelU = Math.Max(panelU, lineU);
                panelV += lineV + (lines++ > 0 ? LineSpacing : 0);
                lineU = u;
                lineV = v;
            }
            else
            {
                lineU += spacing + u;
                lineV = Math.Max(lineV, v);
            }
        }

        panelU = Math.Max(panelU, lineU);
        panelV += lineV + (lines > 0 ? LineSpacing : 0);
        return horizontal ? new Size(panelU, panelV) : new Size(panelV, panelU);
    }
}
