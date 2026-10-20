using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using DotCharter.Tone.Audio.Equalizer;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.Perf;

/// <summary>
///     What ONE <see cref="EqPlotControl.Render" /> costs on the UI thread, in
///     bytes — LOGGED, never asserted. The plot is repainted at ~15 Hz for as
///     long as the EQ module is attached (<c>EqModuleView</c>'s spectrum poll
///     writes <c>Spectrum</c>, which is <c>AffectsRender</c>), so a per-frame
///     allocation here is a steady 15 Hz Gen0 drip under an idle inspector.
///     <para>
///         Static perf audit 2026-08-29, P0 #1–#4: ~26 <c>TryFindResource</c>
///         tree walks, 7 interpolated resource keys, ~15 <c>FormattedText</c>
///         shapings, 3 <c>double[160]</c> and ~10 <c>Pen</c>s per frame.
///         Measured with this harness (headless drawing, 600×102, live
///         spectrum, two audible bands, one selected): <b>105,054 B/frame
///         before, 30,277 B/frame after</b>, and 18,784 B/frame with no section
///         at all — i.e. the floor is the drawing context's own per-call cost
///         for the same zones/grid/spectrum/axis calls, not anything the
///         control still resolves. The structural pins are in
///         <c>UI/EqPlotPaintCacheTests</c>.
///     </para>
///     <para>
///         The number is process-thread-local (<see cref="GC.GetAllocatedBytesForCurrentThread" />)
///         and the harness's own capture is outside the window — only the
///         control's <c>Render</c> is metered. It is not asserted because the
///         headless drawing context has its own per-call cost that changes with
///         the Avalonia version, and a wall-clock or byte threshold in a shared
///         suite is the flake this repo has already paid for several times.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class EqPlotRenderAllocationUiPerfSmokeTests(ITestOutputHelper output)
{
    private const int Frames = 60;

    [Fact]
    public async Task OneRender_AllocationIsLogged()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var section = new EqSection();
            section.SetBandEnabled(0, true);
            section.SetBandEnabled(2, true);
            section.SetBand(2, section.Bands[2] with { GainDb = 4.5 });

            var spectrum = new float[128];
            for (var i = 0; i < spectrum.Length; i++) spectrum[i] = -40f + 20f * MathF.Sin(i / 9f);

            var plot = new EqPlotControl
            {
                Section = section,
                SelectedBand = 2,
                Spectrum = spectrum,
                PreviewLabel = "PREVIEW",
                Width = 600
            };
            var window = new Window
            {
                Width = 640, Height = 200,
                Content = new Border { Child = plot, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(plot.Bounds.Width > 100 && plot.Bounds.Height > 50, $"plot laid out as {plot.Bounds}");

                using var target = new RenderTargetBitmap(new PixelSize(640, 200));

                // Warm: first paint resolves and caches whatever the control caches.
                for (var i = 0; i < 3; i++) RenderOnce(plot, target, spectrum, i);

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < Frames; i++) RenderOnce(plot, target, spectrum, i);
                var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Frames;

                output.WriteLine($"EqPlotControl.Render: {perFrame:0} B/frame over {Frames} frames (live spectrum, 2 audible bands, band 2 selected).");

                plot.Section = null;
                before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < Frames; i++) RenderOnce(plot, target, spectrum, i);
                output.WriteLine($"  no section (the drawing context's floor for zones+grid+spectrum+axis): {(GC.GetAllocatedBytesForCurrentThread() - before) / (double)Frames:0} B/frame");

            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void RenderOnce(EqPlotControl plot, RenderTargetBitmap target, float[] spectrum, int frame)
    {
        // A new spectrum every frame, as the poll delivers it — the cache must
        // not be "warm" because the data never changed.
        spectrum[frame % spectrum.Length] += 0.01f;
        using var ctx = target.CreateDrawingContext(false);
        plot.Render(ctx);
    }
}
