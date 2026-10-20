using Avalonia;
using Avalonia.Controls;

namespace DotCharter.Tone.UI.Controls;

/// <summary>
///     A simple masonry layout panel. Splits children into N columns of equal width
///     and packs each new child into the currently-shortest column, eliminating the
///     ragged vertical gaps a plain WrapPanel leaves when row items differ in height.
/// </summary>
public class MasonryPanel : Panel, IChunkedPanel
{
    /// <summary>
    /// Opt-in: how many children one HIDDEN chunk lays out — 0, the
    ///     default, lays out every child at once. Set it in markup on a hidden
    ///     surface whose sections are heavy (the inspector's Mixer tab), and the
    ///     startup prewarm realizes it a chunk per dispatcher turn
    ///     (<see cref="HiddenChunkGate" />). Shown, the panel lays out as always.
    /// </summary>
    public static readonly StyledProperty<int> ChunkSizeProperty =
        AvaloniaProperty.Register<MasonryPanel, int>(nameof(ChunkSize));

    public static readonly StyledProperty<int> ColumnCountProperty =
        AvaloniaProperty.Register<MasonryPanel, int>(nameof(ColumnCount), 1);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(ColumnSpacing), 10);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(RowSpacing), 10);

    private HiddenChunkGate? _gate;
    private int[] _columnAssignments = [];
    private double[] _desiredHeights = [];
    private double[] _columnHeights = [];
    private double[] _xPositions = [];
    private double[] _yCursor = [];

    static MasonryPanel()
    {
        AffectsMeasure<MasonryPanel>(ColumnCountProperty, ColumnSpacingProperty, RowSpacingProperty);
        AffectsArrange<MasonryPanel>(ColumnCountProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    public int ColumnCount
    {
        get => GetValue(ColumnCountProperty);
        set => SetValue(ColumnCountProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary><inheritdoc cref="ChunkSizeProperty" /></summary>
    public int ChunkSize
    {
        get => GetValue(ChunkSizeProperty);
        set => SetValue(ChunkSizeProperty, value);
    }

    /// <inheritdoc />
    public bool HasBacklog => _gate?.HasBacklog == true;

    /// <inheritdoc />
    public bool Withholds(Control child) => _gate?.Withholds(child) == true;

    /// <inheritdoc />
    public bool RealizeNextChunk() => _gate?.Grant() == true;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The gate watches the panel's effective visibility from its first attach,
        // so it is made when markup sets a chunk size — never for the panels that
        // do not chunk.
        if (change.Property == ChunkSizeProperty && ChunkSize > 0)
            _gate ??= new HiddenChunkGate(this, () => ChunkSize);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cols = Math.Max(1, ColumnCount);
        var gap = ColumnSpacing;
        var rowGap = RowSpacing;

        var totalWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var columnWidth = Math.Max(0, (totalWidth - gap * (cols - 1)) / cols);
        var childConstraint = new Size(columnWidth, double.PositiveInfinity);

        if (_gate is { IsChunking: true } gate)
        {
            // Hidden and chunking: the granted sections only, with the constraint
            // the real layout gives them, and the stack's whole height estimated
            // from them (nobody sees it; a scroller around it does).
            var count = gate.Count;
            var height = 0.0;
            for (var i = 0; i < count; i++)
            {
                Children[i].Measure(childConstraint);
                height += Children[i].DesiredSize.Height;
            }

            var rows = Math.Ceiling(Children.Count / (double)cols);
            return count == 0 ? default : new Size(totalWidth, height / count * rows + rowGap * Math.Max(0, rows - 1));
        }

        EnsureScratch(Children.Count, cols);

        // Measure all children.
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(childConstraint);
            _desiredHeights[i] = Children[i].DesiredSize.Height;
        }

        AssignColumns(_desiredHeights, Children.Count, cols, rowGap, _columnAssignments);

        Array.Clear(_columnHeights, 0, cols);
        for (var i = 0; i < Children.Count; i++)
        {
            var col = _columnAssignments[i];
            if (_columnHeights[col] > 0) _columnHeights[col] += rowGap;
            _columnHeights[col] += _desiredHeights[i];
        }

        var maxHeight = 0.0;
        for (var i = 0; i < cols; i++)
        {
            if (_columnHeights[i] > maxHeight)
                maxHeight = _columnHeights[i];
        }

        return new Size(totalWidth, maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_gate is { IsChunking: true } gate)
        {
            gate.ArrangeMeasured();
            return finalSize;
        }

        var cols = Math.Max(1, ColumnCount);
        var gap = ColumnSpacing;
        var rowGap = RowSpacing;

        EnsureScratch(Children.Count, cols);
        var columnWidth = Math.Max(0, (finalSize.Width - gap * (cols - 1)) / cols);
        for (var i = 0; i < cols; i++)
        {
            _xPositions[i] = i * (columnWidth + gap);
            _yCursor[i] = 0;
        }

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var col = i < _columnAssignments.Length ? _columnAssignments[i] : 0;
            var y = _yCursor[col] > 0 ? _yCursor[col] + rowGap : 0;
            child.Arrange(new Rect(_xPositions[col], y, columnWidth, child.DesiredSize.Height));
            _yCursor[col] = y + child.DesiredSize.Height;
        }

        var maxHeight = 0.0;
        for (var i = 0; i < cols; i++)
        {
            if (_yCursor[i] > maxHeight)
                maxHeight = _yCursor[i];
        }

        return new Size(finalSize.Width, maxHeight);
    }

    private void EnsureScratch(int childCount, int cols)
    {
        if (_columnAssignments.Length < childCount) _columnAssignments = new int[Math.Max(childCount, 8)];
        if (_desiredHeights.Length < childCount) _desiredHeights = new double[Math.Max(childCount, 8)];
        if (_columnHeights.Length < cols) _columnHeights = new double[Math.Max(cols, 4)];
        if (_xPositions.Length < cols) _xPositions = new double[Math.Max(cols, 4)];
        if (_yCursor.Length < cols) _yCursor = new double[Math.Max(cols, 4)];
    }

    /// <summary>
    ///     Bin-packing pass: walks children in source order and places each into
    ///     the currently-shortest column. This produces visually balanced
    ///     column heights even when one item is dramatically taller than the
    ///     rest (e.g. the MIDI-synth section with many controls), which the
    ///     previous "fill column then advance" strategy would shove into the
    ///     last column alone, leaving the other columns nearly empty.
    /// </summary>
    private static void AssignColumns(double[] heights, int n, int cols, double rowGap, int[] result)
    {
        if (n == 0) return;
        if (cols <= 1)
        {
            for (var i = 0; i < n; i++) result[i] = 0;
            return;
        }

        var colHeights = cols <= 16 ? stackalloc double[cols] : new double[cols];
        for (var i = 0; i < n; i++)
        {
            var bestCol = 0;
            var bestHeight = colHeights[0];
            for (var c = 1; c < cols; c++)
            {
                if (colHeights[c] < bestHeight)
                {
                    bestHeight = colHeights[c];
                    bestCol = c;
                }
            }

            result[i] = bestCol;
            if (colHeights[bestCol] > 0) colHeights[bestCol] += rowGap;
            colHeights[bestCol] += heights[i];
        }
    }

}
