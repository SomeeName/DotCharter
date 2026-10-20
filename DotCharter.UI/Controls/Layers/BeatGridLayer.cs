using DotCharter.Rendering.Timeline2D;

namespace DotCharter.UI.Controls.Layers;

/// <summary>Renders beat / measure / subdivision grid lines. Lives on top
/// of the waveform layer so the grid stays visible against the waveform
/// (the waveform is otherwise opaque enough to swallow finer subdivisions).
/// Delegates to <see cref="BackgroundLayer.RenderBeatGrid"/> so the actual
/// drawing code is not duplicated.</summary>
public sealed class BeatGridLayer : ITimelineRenderLayer
{
    private readonly BackgroundLayer _impl = new() { IncludeBeatGrid = true };

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
        => _impl.RenderBeatGrid(batcher, rc);
}
