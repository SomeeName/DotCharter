using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Spectrum;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Construction + attach + layout smoke for the tone lab's views (#299): the
///     node spectrum under the module meters, the level-match control in the rig
///     header, and the board's per-card meters and chain-latency readout. Each is
///     asserted to have MEASURED non-zero — a view whose styles failed still
///     arranges, it just has no size.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneLabConstructionUiPerfSmokeTests
{
    private sealed class Renderer : IOfflineToneRenderer
    {
        public bool TryRender(DcRigPreset rig, float[] left, float[] right, int sampleRate) => true;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static void AssertMeasured(Visual? visual, string what)
    {
        Assert.NotNull(visual);
        Assert.True(visual!.IsEffectivelyVisible, $"{what} is not visible");
        Assert.True(visual.Bounds.Width > 1 && visual.Bounds.Height > 1, $"{what} measured {visual.Bounds.Size}");
    }

    [Fact]
    public async Task TheNodeSpectrum_LaysOutUnderTheModuleMeters_WithBothCurves()
    {
        var chain = new FakeDspChainService();
        var node = chain.AddNode("overdrive")!.NodeId;
        var spectrum = new RigNodeSpectrumViewModel(() => new Renderer(), () => chain, () => node);
        await spectrum.MeasureCommand.ExecuteAsync(null);
        Assert.True(spectrum.IsReady);

        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var meters = new RigNodeMetersViewModel(() => chain, () => node) { Spectrum = spectrum };
            var view = new RigNodeMetersView { DataContext = meters, Width = 520 };
            var window = new Window { Width = 560, Height = 420, Content = view };
            window.Show();
            try
            {
                Settle(window);
                var host = view.GetVisualDescendants().OfType<RigNodeSpectrumView>().SingleOrDefault();
                AssertMeasured(host, "the spectrum view");
                var plot = host!.GetVisualDescendants().OfType<RigSpectrumPlot>().Single();
                AssertMeasured(plot, "the spectrum plot");
                Assert.Same(spectrum.Bands, plot.Bands);
                Assert.Same(spectrum.ReferenceBands, plot.ReferenceBands);
                AssertMeasured(host!.FindControl<Button>("RigSpectrumMeasure"), "the Measure button");
                AssertMeasured(host!.FindControl<ToggleButton>("RigSpectrumPre"), "the PRE tap");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task MetersWithoutASpectrum_HostNone() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var chain = new FakeDspChainService();
            using var meters = new RigNodeMetersViewModel(() => chain, () => null);
            var view = new RigNodeMetersView { DataContext = meters, Width = 520 };
            var window = new Window { Width = 560, Height = 300, Content = view };
            window.Show();
            try
            {
                Settle(window);
                Assert.False(view.GetVisualDescendants().OfType<RigNodeSpectrumView>().Single().IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheRig_ShowsTheLevelMatch_TheCardMeters_AndTheChainLatency() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            RigFaceplates.SuppressedForTests = true;
            var chain = new FakeDspChainService { MeteringActive = true };
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
            var drive = chain.AddNode("overdrive")!.NodeId;
            var delay = chain.AddNode("delay")!.NodeId;
            chain.Latency[drive] = 0;
            chain.Latency[delay] = 1024;
            chain.Meters[drive] = new DspNodeMeter(0.1f, 0.05f, 0.4f, 0.2f);
            chain.Meters[delay] = new DspNodeMeter(0.4f, 0.2f, 0.3f, 0.1f);
            rig.Sync();
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1600, Height = 1000, Content = view };
            window.Show();
            try
            {
                Settle(window);
                model.PollMeters();
                Settle(window);

                AssertMeasured(view.FindControl<RigCompareLevelView>("RigCompareLevel"), "the level-match control");
                var toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "RigMatchLevelToggle");
                AssertMeasured(toggle, "the Match level toggle");
                Assert.True(toggle.IsChecked);

                Assert.True(chain.MeterLeases > 0, "the visible rig holds no metering lease");
                var cardMeters = view.GetVisualDescendants().OfType<FireLevelMeter>().Where(m => m.Classes.Contains("RigBoardMeter")).ToList();
                Assert.Equal(rig.Nodes.Count, cardMeters.Count);
                foreach (var meter in cardMeters) AssertMeasured(meter, "a card meter");

                var latency = view.FindControl<TextBlock>("RigChainLatency");
                AssertMeasured(latency, "the chain latency");
                Assert.Contains("1024", latency!.Text);
            }
            finally
            {
                window.Close();
                RigFaceplates.SuppressedForTests = false;
            }
        }, TestContext.Current.CancellationToken);
}
