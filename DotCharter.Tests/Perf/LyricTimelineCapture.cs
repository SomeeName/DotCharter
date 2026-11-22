using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DotCharter.Rendering.Data;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Graphics.Vulkan2D;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>Composes the offscreen production timeline beneath the real headless editor for captures.</summary>
internal static class LyricTimelineCapture
{
    public static void Capture(Window window, ChartRenderer renderer, MainWindowViewModel vm, string name,
        int expectedPhraseCount, int expectedWordCount)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        var state = vm.ProjectState;
        var width = (int)renderer.Bounds.Width;
        var height = (int)renderer.Bounds.Height;
        var bounds = ChartHitTester.GetViewportBounds(state.TimelineViewAnchorEffectiveMs, state.PixelsPerMs, width);
        var snapshot = new FrameSnapshot { StringCount = 6 };
        var context = new SnapshotUpdateContext
        {
            ActiveBeats = vm.ChartEditor.Beats,
            LyricPhrases = vm.ChartEditor.ActiveVocalTrack!.Phrases,
            SelectedLyric = vm.ChartEditor.Selection.SelectedLyric
        };
        FrameSnapshot.BuildSnapshot(bounds.StartMs, bounds.EndMs, 0, snapshot, context, []);
        Assert.Equal(expectedPhraseCount, snapshot.LyricPhraseCount);
        Assert.Equal(expectedWordCount, snapshot.LyricWordCount);
        var frame = new ChartRenderContext
        {
            Width = width, Height = height, CurrentZoom = state.PixelsPerMs,
            ViewStartMs = bounds.StartMs, ViewEndMs = bounds.EndMs,
            TopMargin = (float)renderer.TopMargin, BottomMargin = (float)renderer.BottomMargin,
            LaneAreaHeight = (float)(height - renderer.TopMargin - renderer.BottomMargin),
            LaneHeight = (float)(height - renderer.TopMargin - renderer.BottomMargin) / 6,
            StringCount = 6, DrawLabels = true, Snapshot = snapshot, Ribbon = renderer.TopRibbon,
            Editor = vm.ChartEditor, Transport = vm.Transport,
            Project = state.ActiveProject, Arrangement = state.ActiveArrangement,
            RendererPixelsPerMs = state.PixelsPerMs
        };
        using var offscreen = new HeadlessVulkanTimelineRenderer(width, height);
        var pixels = offscreen.Render(frame,
            [new BackgroundLayer(), new LaneLayer(), new EventLayer(new PinnedLabelHitRegistry(), renderer)]);
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock()) Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        var previous = renderer.Child;
        try
        {
            renderer.Child = new Image { Source = bitmap, IsHitTestVisible = false };
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, name);
        }
        finally
        {
            renderer.Child = previous;
        }
    }
}
