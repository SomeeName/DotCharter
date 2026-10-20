using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DotCharter.Tone.UI.Controls;

/// <summary>
///     A <see cref="StackPanel" /> whose FIRST layout, while it is hidden, is paid a
/// chunk of children at a time (<see cref="HiddenChunkGate" />) — for a
///     hidden surface whose cost is a stack of heavy groups: the settings tabs'
///     keybind groups and colour groups. Visible, or once it has been shown, it is
///     a plain <see cref="StackPanel" />; it changes when the groups are laid out,
///     never how.
/// </summary>
public class ChunkedStackPanel : StackPanel, IChunkedPanel
{
    /// <summary>How many children one hidden chunk lays out (0: no chunking).</summary>
    public static readonly StyledProperty<int> ChunkSizeProperty =
        AvaloniaProperty.Register<ChunkedStackPanel, int>(nameof(ChunkSize), 1);

    private readonly HiddenChunkGate _gate;

    /// <summary>Builds the panel, hidden-chunked until it is first shown.</summary>
    public ChunkedStackPanel() => _gate = new HiddenChunkGate(this, () => ChunkSize);

    /// <summary><inheritdoc cref="ChunkSizeProperty" /></summary>
    public int ChunkSize
    {
        get => GetValue(ChunkSizeProperty);
        set => SetValue(ChunkSizeProperty, value);
    }

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

        // StackPanel's own child constraint (unbounded along the stack), so the
        // real layout's measure of an already-realized child is a cache hit.
        var horizontal = Orientation == Orientation.Horizontal;
        var slot = horizontal
            ? availableSize.WithWidth(double.PositiveInfinity)
            : availableSize.WithHeight(double.PositiveInfinity);
        var count = _gate.Count;
        double along = 0, across = 0;
        for (var i = 0; i < count; i++)
        {
            Children[i].Measure(slot);
            var size = Children[i].DesiredSize;
            along += horizontal ? size.Width : size.Height;
            across = Math.Max(across, horizontal ? size.Height : size.Width);
        }

        // The whole stack's size, each child not laid out yet standing in at the
        // average of those that were: nobody sees it (the panel is hidden), but a
        // scroller around it sizes its bar from it.
        if (count > 0) along = along / count * Children.Count + Spacing * (Children.Count - 1);
        return horizontal ? new Size(along, across) : new Size(across, along);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_gate.IsChunking) return base.ArrangeOverride(finalSize);
        _gate.ArrangeMeasured();
        return finalSize;
    }
}
