using System.Reflection;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Headless;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Graphics.Vulkan2D;
using DotCharter.UI.Input;

namespace DotCharter.Tests.Perf.Guide;

/// <summary>
///     The two Vulkan surfaces of the editor — the 2D timeline and the 3D preview — draw nothing in
///     the headless session (no compositor GPU interop), so the guide's shots would show two black
///     holes. This renders them OFFSCREEN through the product's own headless facades
///     (<see cref="HeadlessVulkanTimelineRenderer" /> with the real layer stack,
///     <see cref="HeadlessHighwayRenderer" />) from the SAME snapshot the live
///     <see cref="ChartRenderer" /> builds (its private snapshot context, read by reflection), and
///     hands back BGRA pixels the capture paints into the controls' boxes. Null when no Vulkan
///     device is available: the shot is still written, with the panes empty.
/// </summary>
internal static class GuideChartFrames
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static byte[]? Timeline(ChartRenderer renderer, int width, int height, double viewStartMs, Action<string> log)
    {
        try
        {
            var editor = renderer.Editor ?? throw new InvalidOperationException("the timeline has no editor");
            var ctx = Context(renderer);
            var zoom = renderer.PixelsPerMs;
            var viewEndMs = viewStartMs + (width / zoom);
            var playheadMs = viewStartMs + (ChartEditorConstants.PlayheadX / zoom);
            var snapshot = new FrameSnapshot();
            FrameSnapshot.BuildSnapshot(viewStartMs, viewEndMs, playheadMs, snapshot, ctx, []);

            var ribbon = renderer.TopRibbon;
            var stringCount = StringDigitTargeting.StringCountOf(editor.ActiveArrangement);
            var layout = TimelineGutterLayout.For(height, ribbon, stringCount, renderer.LeftyFlip ^ renderer.InvertStrings,
                renderer.BottomMargin, renderer.DrawLabels);
            var rc = new ChartRenderContext
            {
                Width = width,
                Height = height,
                CurrentZoom = zoom,
                ViewStartMs = viewStartMs,
                ViewEndMs = viewEndMs,
                CenterScreenX = ChartEditorConstants.PlayheadX,
                PlayheadScreenX = ChartEditorConstants.PlayheadX,
                TopMargin = layout.TopMargin,
                BottomMargin = layout.BottomMargin,
                LaneAreaHeight = layout.LaneAreaHeight,
                LaneHeight = layout.LaneHeight,
                FhpY = layout.TopMargin + layout.LaneAreaHeight + ChartEditorConstants.RenderMetrics.FhpYOffset,
                StringCount = stringCount,
                LeftyFlip = renderer.LeftyFlip,
                InvertStrings = renderer.InvertStrings,
                CurrentTimeMs = playheadMs,
                DrawLabels = renderer.DrawLabels,
                WaveformData = renderer.ShowWaveform ? renderer.WaveformData : null,
                Editor = editor,
                Transport = renderer.Transport!,
                Snapshot = snapshot,
                RendererPixelsPerMs = zoom,
                Ribbon = ribbon
            };
            using var gpu = new HeadlessVulkanTimelineRenderer(width, height);
            return gpu.Render(rc, HeadlessVulkanTimelineRenderer.DefaultLayers());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"timeline: no offscreen frame ({e.GetType().Name}: {e.Message})");
            return null;
        }
    }

    public static byte[]? Highway(ChartRenderer renderer, int width, int height, double timeMs, Action<string> log)
    {
        try
        {
            var snapshot = new FrameSnapshot { HighwayScrollSpeed = 1.3f };
            FrameSnapshot.BuildSnapshot(timeMs - 1_000, timeMs + 8_000, timeMs, snapshot, Context(renderer), []);
            using var gpu = new HeadlessHighwayRenderer(useRealChartTextures: true);
            return gpu.Render(snapshot, width, height, timeMs);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"3D preview: no offscreen frame ({e.GetType().Name}: {e.Message})");
            return null;
        }
    }

    /// <summary>The renderer's own snapshot inputs, refreshed from its editor exactly as a frame does.</summary>
    private static SnapshotUpdateContext Context(ChartRenderer renderer)
    {
        typeof(ChartRenderer).GetMethod("UpdateSnapshotContext", Private)!.Invoke(renderer, [renderer.Editor]);
        return (SnapshotUpdateContext)typeof(ChartRenderer).GetField("_snapshotCtx", Private)!.GetValue(renderer)!;
    }
}
