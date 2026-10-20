using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.Perf;

/// <summary>
///     What ONE paint of each live Fire meter and of the knob costs on the UI
///     thread, in bytes — LOGGED, never asserted (static perf audit 2026-08-29
///     P1 #18, #19, #22). The level meter is driven at 20–30 Hz by three timers
///     and the tuner meter by six live detectors, so a per-paint allocation is
///     a steady Gen0 drip for as long as the rig is on screen.
///     <para>
///         Measured with this harness (headless drawing, value moving every
///         frame), before / after <c>FirePaintCache</c>: FireLevelMeter
///         <b>8,840 / 217 B per frame</b>, FireTunerMeter <b>6,440 / 227</b>,
///         FireKnob <b>12,899 / 1,208</b> (the knob still builds its LIT arc
///         geometry per frame; it changes with the value). The remaining bytes
///         are the drawing context's own per-call cost, which is why the number
///         is logged and the structure is pinned in <c>UI/FirePaintCacheTests</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class FireMeterRenderAllocationUiPerfSmokeTests(ITestOutputHelper output)
{
    private const int Frames = 60;

    [Fact]
    public async Task OnePaintOfEachControl_IsLogged()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var level = new FireLevelMeter { LevelDb = -12, Width = 160 };
            var tuner = new FireTunerMeter { Cents = 8, HasReading = true, Width = 200 };
            var knob = new FireKnob { Label = "Gain", ValueText = "5.0", Minimum = 0, Maximum = 10, Value = 5 };
            FireUnitState.SetCategory(knob, FireUnitCategory.Drive);

            var host = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            host.Children.Add(level);
            host.Children.Add(tuner);
            host.Children.Add(knob);
            var window = new Window { Width = 400, Height = 300, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                using var target = new RenderTargetBitmap(new PixelSize(400, 300));

                Log("FireLevelMeter", target, (i, ctx) => { level.LevelDb = -30 + i % 25; level.Render(ctx); });
                Log("FireTunerMeter", target, (i, ctx) => { tuner.Cents = -20 + i % 40; tuner.Render(ctx); });
                Log("FireKnob", target, (i, ctx) => { knob.Value = i % 10; knob.Render(ctx); });
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    private void Log(string what, RenderTargetBitmap target, Action<int, Avalonia.Media.DrawingContext> paint)
    {
        for (var i = 0; i < 3; i++) Paint(target, paint, i);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Frames; i++) Paint(target, paint, i);
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Frames;
        output.WriteLine($"{what}.Render: {perFrame:0} B/frame over {Frames} frames (value moving every frame).");
    }

    private static void Paint(RenderTargetBitmap target, Action<int, Avalonia.Media.DrawingContext> paint, int frame)
    {
        using var ctx = target.CreateDrawingContext(false);
        paint(frame, ctx);
    }
}
