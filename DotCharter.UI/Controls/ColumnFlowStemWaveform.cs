using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DotCharter.UI.Controls;

/// <summary>
///     Inspector design D's mini stem waveform on the Mixer's Playback and roles card: the whole song's waveform as the
///     timeline draws it (<c>ProjectState.WaveformData</c> — the file's, or the mixer's audible blend while "Waveform follows
///     the mix" is on), squeezed into one short strip. It redraws only when <see cref="Samples" /> changes, which the stem
///     waveform coordinator already coalesces to one publish per pass (a fader drag does not repaint it per tick); it
///     owns no timer and no subscription.
/// </summary>
public sealed class ColumnFlowStemWaveform : Control
{
    public static readonly StyledProperty<WaveformSamples?> SamplesProperty =
        AvaloniaProperty.Register<ColumnFlowStemWaveform, WaveformSamples?>(nameof(Samples));

    static ColumnFlowStemWaveform() => AffectsRender<ColumnFlowStemWaveform>(SamplesProperty);

    public ColumnFlowStemWaveform() => Height = 28;

    /// <summary>The waveform pyramid; null or empty draws a flat centre line.</summary>
    public WaveformSamples? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>
    ///     One 0..1 peak per column: the coarsest level that still has at least <paramref name="columns" /> blocks, each
    ///     column the max of the blocks it covers. Empty for no data or no columns.
    /// </summary>
    internal static float[] Columns(WaveformSamples? samples, int columns)
    {
        if (samples is not { HasData: true } || columns <= 0) return [];
        var level = samples.Mipmaps[0];
        foreach (var candidate in samples.Mipmaps)
        {
            if (candidate.Length < columns) break;
            level = candidate;
        }

        var peaks = new float[columns];
        for (var c = 0; c < columns; c++)
        {
            var from = (int)((long)c * level.Length / columns);
            var to = Math.Max(from + 1, (int)((long)(c + 1) * level.Length / columns));
            var peak = 0f;
            for (var i = from; i < to && i < level.Length; i++) peak = Math.Max(peak, level[i]);
            peaks[c] = Math.Clamp(peak, 0f, 1f);
        }

        return peaks;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 2 || height < 2) return;
        var middle = height / 2;
        var brush = this.TryFindResource("FireAccent", ActualThemeVariant, out var found) && found is IBrush b ? b : Brushes.Gray;
        var pen = new Pen(brush, 1);
        var columns = Columns(Samples, (int)Math.Floor(width / 2));
        if (columns.Length == 0)
        {
            context.DrawLine(pen, new Point(0, middle), new Point(width, middle));
            return;
        }

        for (var c = 0; c < columns.Length; c++)
        {
            var x = c * 2 + 0.5;
            var half = Math.Max(0.5, columns[c] * (middle - 1));
            context.DrawLine(pen, new Point(x, middle - half), new Point(x, middle + half));
        }
    }
}
