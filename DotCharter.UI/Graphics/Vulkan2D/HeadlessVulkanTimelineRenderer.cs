using DotCharter.Rendering.Timeline2D;
using DotCharter.Rendering.Vulkan2D;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Graphics.Vulkan2D;

/// <summary>
///     Test-facing facade that runs the REAL 2D timeline layers through the
///     REAL <see cref="TimelineBatcher" /> into the offscreen composed-sibling
///     Vulkan renderer (<see cref="Timeline2DHeadlessRenderer" />) and returns
///     the pixels on the CPU (tightly-packed BGRA8, top row first). No
///     window, no compositor — THE 2D headless facade since the v0.33 OpenGL
///     removal.
///     <para>
///         Determinism: the output is a pure function of the supplied
///         <see cref="ChartRenderContext" /> — pin every input. Not
///         thread-safe: one instance per test thread.
///     </para>
/// </summary>
public sealed class HeadlessVulkanTimelineRenderer : IDisposable
{
    private readonly TimelineBatcher _batcher = new();
    private readonly Timeline2DHeadlessRenderer _renderer;
    private bool _disposed;

    public HeadlessVulkanTimelineRenderer(int width, int height)
    {
        _renderer = new Timeline2DHeadlessRenderer(width, height);
        _batcher.FontAtlas = _renderer.FontAtlas;
        _renderer.FontAtlas.RegisterCharacters(LinkLayer.Glyphs);
    }

    public int Width => _renderer.Width;
    public int Height => _renderer.Height;

    /// <summary>
    ///     The real <c>ChartRenderer</c> layer stack, in the exact composition
    ///     order (including <see cref="EventLayer" /> at index 4, fed a fresh
    ///     <see cref="PinnedLabelHitRegistry" />). A caller that only wants a
    ///     subset (e.g. just the waveform) passes its own list instead — most
    ///     seed tests do, because several layers require a populated
    ///     <c>rc.Editor</c>/<c>rc.Transport</c> the headless harness does not
    ///     spin up.
    /// </summary>
    public static IReadOnlyList<ITimelineRenderLayer> DefaultLayers()
    {
        return new ITimelineRenderLayer[]
        {
            new BackgroundLayer { IncludeBeatGrid = false },
            new WaveformLayer(),
            new BeatGridLayer(),
            new LaneLayer(),
            new EventLayer(new PinnedLabelHitRegistry(), new object()),
            new CursorLayer(),
            new HandShapeLayer(),
            new FretAnchorLayer(),
            new NoteLayer(),
            new LinkLayer(),
            new PlayheadLayer()
        };
    }

    /// <summary>
    ///     Renders one frame: atlas rebuild → batcher reset → layer loop →
    ///     draw, exactly the app frame order
    ///     (<c>ChartRenderer.RenderTimelineFrame</c> through the Vulkan
    ///     backend).
    /// </summary>
    public byte[] Render(ChartRenderContext rc, IReadOnlyList<ITimelineRenderLayer> layers, byte[]? reusePixels = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(rc);
        ArgumentNullException.ThrowIfNull(layers);

        // Rebuild BEFORE batching — glyph metrics must exist when layers
        // measure/emit text quads (the app backend does the same in
        // BeginFrame).
        _renderer.FontAtlas.RebuildIfNeeded();

        _batcher.Reset();
        for (var i = 0; i < layers.Count; i++) layers[i].Render(_batcher, rc);

        var bg = ChartTheme.BottomMarginBgColor;
        return _renderer.Render(_batcher, (float)rc.Width, (float)rc.Height,
            bg.R / 255f, bg.G / 255f, bg.B / 255f, reusePixels);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.Dispose();
    }
}
