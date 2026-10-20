using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The rig's SIGNAL card with a reading showing: each "-74.6 dBFS" stays inside its own half
///     and clear of the other meter. The reading used to sit beside its meter in a horizontal
///     StackPanel, so the meter took its full natural width and pushed the input's reading into
///     the output half, where the output meter painted over it (live capture, 2026-09-24). An
///     empty fixture cannot show this: without a reading the text is hidden.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigNodeMetersLayoutUiPerfSmokeTests
{
    private sealed class Source : IDspMeterSource
    {
        public bool IsMeteringActive => true;
        public IDisposable AcquireMetering() => new Lease();

        public bool TryGetNodeMeter(int nodeId, out DspNodeMeter meter)
        {
            // -74.6 dBFS in, -52.0 dBFS out: the longest readings the card shows in practice.
            meter = new DspNodeMeter(0.000186f, 0.0001f, 0.00251f, 0.001f);
            return true;
        }

        private sealed class Lease : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Theory]
    [InlineData(440)]
    [InlineData(560)]
    public async Task EachReading_StaysInItsHalf_AndClearOfTheOtherMeter(double cardWidth)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var vm = new RigNodeMetersViewModel(() => new Source(), () => 3);
            vm.SetSurfaceLive(this, true);
            vm.Poll();
            Assert.True(vm.HasReading, "the fake source gave no reading");

            var view = new RigNodeMetersView { DataContext = vm, Width = cardWidth };
            var window = new Window { Width = cardWidth + 40, Height = 300, Content = new Border { Padding = new Thickness(20), Child = view } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                var readings = view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible && (t.Text ?? "").EndsWith("dBFS", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, readings.Count);
                var meters = view.GetVisualDescendants().OfType<FireLevelMeter>().ToList();
                Assert.Equal(2, meters.Count);

                Rect InView(Visual v) => new(v.TranslatePoint(default, view)!.Value, v.Bounds.Size);
                var half = cardWidth / 2;

                // A meter DRAWS at its natural width whatever its bounds, so the natural width is
                // what has to fit its half — or the input meter runs under the output one.
                foreach (var meter in meters)
                {
                    var natural = FireLevelScale.RowWidth(meter.SegmentCount, meter.BarWidth, meter.BarGap);
                    var left = InView(meter).X;
                    var halfRight = left < half ? half : cardWidth;
                    Assert.True(left + natural <= halfRight + 1,
                        $"a meter drawn {natural:0.#} px wide from x={left:0.#} runs past its half ({halfRight:0.#}, card {cardWidth})");
                }
                foreach (var reading in readings)
                {
                    var r = InView(reading);
                    var ownHalfRight = r.X < half ? half : cardWidth;
                    Assert.True(r.Right <= ownHalfRight + 1,
                        $"'{reading.Text}' runs to x={r.Right:0.#} past its half, which ends at {ownHalfRight:0.#} (card {cardWidth})");
                    foreach (var meter in meters)
                    {
                        var natural = FireLevelScale.RowWidth(meter.SegmentCount, meter.BarWidth, meter.BarGap);
                        var m = new Rect(InView(meter).Position, new Size(natural, meter.Bounds.Height));
                        Assert.False(r.Intersects(m), $"'{reading.Text}' at {r} is painted over by a meter drawn at {m}");
                    }
                }
            }
            finally
            {
                window.Close();
                vm.SetSurfaceLive(this, false);
            }
        }, TestContext.Current.CancellationToken);
    }
}
