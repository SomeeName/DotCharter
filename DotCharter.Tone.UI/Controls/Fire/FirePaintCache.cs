using Avalonia.Controls;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     Per-control paint cache for the custom-drawn Fire controls: theme
///     colours by token key, and immutable brushes / pens by final colour.
///     <para>
/// Without it <c>FireTunerMeter</c>
///         would walk the resource tree twice per <c>Brush()</c> (the second time
///         only to read the alpha) and build ~8 brushes per paint on six live
///         instances; <c>FireLevelMeter</c> would resolve four tokens and allocate
///         one <c>SolidColorBrush</c> per SEGMENT per paint on three meters at
///         20–30 Hz (~1,000 brushes a second); <c>FireKnob</c> would build ~14 pens,
///         brushes and geometries per drag frame. A <c>TryFindResource</c> is a
///         walk up every style scope to <c>Application.Resources</c>, and none
///         of these colours change between one paint and the next.
///     </para>
///     <para>
///         The cache is cleared on attach (the tree above the control — and so
///         the answer — can differ between hosts) and on a theme-variant change;
///         <c>FireUnitState.ColorFrom</c> stays the ONE resolver so the answer
///         is the same as an uncached lookup, just remembered.
///     </para>
/// </summary>
internal sealed class FirePaintCache
{
    private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IBrush?> _dictionaryBrushes = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, IBrush> _brushes = [];
    private readonly Dictionary<PenKey, Pen> _pens = [];

    private readonly record struct PenKey(uint Color, double Thickness, PenLineCap Cap);

    /// <summary>A token's colour, resolved once per attach/theme
    /// (<see cref="FireUnitState.ColorFrom" /> on the miss).</summary>
    public Color Color(Control control, string key)
    {
        if (_colors.TryGetValue(key, out var color)) return color;
        color = FireUnitState.ColorFrom(control, key);
        _colors[key] = color;
        return color;
    }

    /// <summary>A token's brush as the dictionary holds it (gradients included),
    /// resolved once (<see cref="FireUnitState.BrushFrom" /> on the miss).</summary>
    public IBrush? DictionaryBrush(Control control, string key)
    {
        if (_dictionaryBrushes.TryGetValue(key, out var brush)) return brush;
        brush = FireUnitState.BrushFrom(control, key);
        _dictionaryBrushes[key] = brush;
        return brush;
    }

    /// <summary>An immutable solid brush of exactly this colour (alpha
    /// included), one per distinct colour.</summary>
    public IBrush Brush(Color color)
    {
        var key = color.ToUInt32();
        if (_brushes.TryGetValue(key, out var brush)) return brush;
        brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color);
        _brushes[key] = brush;
        return brush;
    }

    /// <summary>A pen of this colour, thickness and cap, one per distinct triple.</summary>
    public Pen Pen(Color color, double thickness, PenLineCap cap = PenLineCap.Flat)
    {
        var key = new PenKey(color.ToUInt32(), thickness, cap);
        if (_pens.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(Brush(color), thickness, lineCap: cap);
        _pens[key] = pen;
        return pen;
    }

    /// <summary>How many token colours are remembered — the test seam.</summary>
    public int CachedColorCount => _colors.Count;

    public void Clear()
    {
        _colors.Clear();
        _dictionaryBrushes.Clear();
        _brushes.Clear();
        _pens.Clear();
    }
}
