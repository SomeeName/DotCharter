using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Tests.Perf;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The module editor's UNIT panel, constructed, attached and LAID OUT — the
///     four XAML fault classes the root guide names all build perfectly clean
///     and only show up here: an unresolvable <c>StaticResource</c>, a
///     <c>StringFormat</c> given a Binding, a literal CommandParameter on a
///     typed command, and a view that fails to construct and renders as a
///     "Not Found" placeholder. A subtree whose styles failed still ARRANGES,
///     so every assertion below is that something MEASURED non-zero.
///     <para>
///         Named <c>*UiPerfSmokeTests</c> and in the HeadlessUI collection
///         deliberately: <c>Avalonia.Headless</c>'s dispatcher is process-global,
///         so these run as their own <c>dotnet test</c> invocation and not in
///         the default gate.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigUnitPanelUiPerfSmokeTests
{
    private static DspModuleType Looper() => new("looper", "Looper",
    [
        new DspParamInfo("state", "State", 0f, 3f, 0f)
            { ValueLabels = ["Stop", "Record", "Play", "Overdub"] },
        new DspParamInfo("undo", "Undo", 0f, 1f, 0f) { ValueLabels = ["-", "Undo"] },
        new DspParamInfo("maxLoopS", "Max Loop (s)", 1f, 60f, 30f),
        new DspParamInfo("mix", "Mix", 0f, 1f, 1f)
    ]);

    private static DspModuleType Ducker() => new("ducker", "Ducker",
    [
        new DspParamInfo("depthDb", "Depth (dB)", -40f, 0f, -12f),
        new DspParamInfo("releaseMs", "Release (ms)", 10f, 500f, 120f)
    ])
    { SupportsSidechain = true };

    private static DspModuleType Svf() => new("svf", "State Variable Filter",
    [
        new DspParamInfo("cutoffHz", "Cutoff (Hz)", 20f, 20000f, 1000f),
        new DspParamInfo("resonance", "Resonance", 0f, 1f, 0.3f)
    ]);

    [Fact]
    public async Task TheLoopersTransportAndTrigger_AreRealControlsThatMeasure()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var chain = new RigCapabilityChain([Looper(), Ducker(), Svf()]);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
            var node = rig.AddGear(chain.Catalog.Single(m => m.Id == "looper"))!;
            rig.SelectNodeAt(0);

            var model = new AmpFxRigViewModel(rig) { IsDetailsOpen = true }; // the unit panel is a DETAILS item
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                var unit = model.DockUnit!;
                Assert.True(unit.HasUnitPanel);

                var panel = Panel(view, "RigUnitPanel");
                Assert.True(panel.IsVisible && panel.Bounds.Width > 0 && panel.Bounds.Height > 0,
                    $"the unit panel did not measure: {panel.Bounds}");

                // FOUR buttons, one per ABI label, and each one has real size.
                var transport = Panel(view, "RigUnitTransport");
                var states = transport.GetVisualDescendants().OfType<ToggleButton>().ToList();
                Assert.Equal(4, states.Count);
                Assert.All(states, b => Assert.True(b.Bounds.Width > 0 && b.Bounds.Height > 0,
                    "a transport button rendered nothing"));
                Assert.True(states[0].IsChecked, "Stop is the resting state");

                // …and the transport is NOT also a picker. That is the whole
                // point of taking it out of ModelSelectors: two renderings of
                // one footswitch, one of which fights the engine.
                Assert.Empty(node.ModelSelectors);

                // Pressing Record drives the chain and the strip follows.
                states[1].Command!.Execute(states[1].CommandParameter);
                window.UpdateLayout();
                Assert.Equal((node.NodeId, "state", 1f), Assert.Single(chain.ParamSets));
                Assert.True(states[1].IsChecked);
                Assert.False(states[0].IsChecked);

                // The momentary trigger is a plain Button and shows NO state.
                var momentary = Panel(view, "RigUnitMomentary");
                var undo = Assert.Single(momentary.GetVisualDescendants().OfType<Button>());
                Assert.True(undo.Bounds.Width > 0 && undo.Bounds.Height > 0);
                Assert.Equal("Undo", undo.Content);
                Assert.IsNotType<ToggleButton>(undo);

                // A looper is not keyable and reports no latency, so those two
                // rows are collapsed inside a panel that is still up.
                Assert.False(Panel(view, "RigUnitSidechain").IsVisible);
                Assert.False(Panel(view, "RigUnitLatency").IsVisible);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheSidechainPicker_AppearsOnAKeyableUnitOnly_AndTheLatencyLineMeasures()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var chain = new RigCapabilityChain([Ducker(), Svf()]);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
            rig.AddGear(chain.Catalog.Single(m => m.Id == "ducker"));
            rig.AddGear(chain.Catalog.Single(m => m.Id == "svf"));
            foreach (var n in rig.Nodes) chain.Latency[n.NodeId] = n.ModuleTypeId == "ducker" ? 0 : 64;
            rig.SelectNodeAt(0);

            var model = new AmpFxRigViewModel(rig) { IsDetailsOpen = true }; // the unit panel is a DETAILS item
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                var sidechain = Panel(view, "RigUnitSidechain");
                Assert.True(sidechain.IsVisible && sidechain.Bounds.Height > 0,
                    $"the sidechain row did not measure: {sidechain.Bounds}");
                var picker = Assert.Single(sidechain.GetVisualDescendants().OfType<ComboBox>());
                Assert.Equal(3, picker.ItemCount);
                Assert.True(picker.Bounds.Width > 0 && picker.Bounds.Height > 0);

                // The ITEMS carry a localized label, not an enum name — an empty
                // one is what a missing resx key looks like on screen.
                var options = rig.Nodes[0].SidechainOptions;
                Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
                Assert.Equal(3, options.Select(o => o.Label).Distinct().Count());

                picker.SelectedItem = options.Single(o => o.Source == JuceSharp.Dsp.JuceDspSidechainSource.SongMix);
                window.UpdateLayout();
                Assert.Equal((rig.Nodes[0].NodeId, JuceSharp.Dsp.JuceDspSidechainSource.SongMix),
                    Assert.Single(chain.SidechainSets));

                // …and the svf, which is NOT keyable, has no picker but DOES
                // have the latency line.
                rig.SelectNodeAt(1);
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                Assert.False(Panel(view, "RigUnitSidechain").IsVisible);

                var latency = (TextBlock)Panel(view, "RigUnitLatency");
                Assert.True(latency.IsVisible && latency.Bounds.Height > 0,
                    $"the latency line did not measure: {latency.Bounds}");
                Assert.Contains("64", latency.Text!, StringComparison.Ordinal);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The refusal has to be VISIBLE, which is the only thing that
    /// separates "the engine kept the old IR" from "it loaded and sounds the
    /// same".</summary>
    [Fact]
    public async Task ARefusedFile_PutsAVisibleLineUnderThePicker()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var convreverb = new DspModuleType("convreverb", "Convolution Reverb",
                [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)])
            {
                StringParams = [new DspStringParamInfo("irFile", "IR File", "*.wav")]
            };

            var chain = new RigCapabilityChain([convreverb]) { FailSetString = true };
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
            var node = rig.AddGear(convreverb)!;
            rig.SelectNodeAt(0);

            var model = new AmpFxRigViewModel(rig) { IsDetailsOpen = true }; // the unit panel is a DETAILS item
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                Assert.False(Panel(view, "RigUnitFileError").IsVisible);

                node.StringParams.Single(sp => sp.Id == "irFile").LoadFile(@"C:\irs\broken.wav");
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                var line = (TextBlock)Panel(view, "RigUnitFileError");
                Assert.True(line.IsVisible && line.Bounds.Height > 0,
                    $"the refusal is invisible: {line.Bounds}");
                Assert.Contains("broken.wav", line.Text!, StringComparison.Ordinal);

                // …and the strip beside the picker says so too, because that is
                // where the user is looking when it happens.
                var beside = (TextBlock)Panel(view, "RigDockFileError");
                Assert.True(beside.IsVisible && beside.Bounds.Height > 0);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The named control, found through the REALIZED tree — a name
    /// that no longer exists fails here rather than silently asserting nothing.</summary>
    private static Control Panel(Control root, string name)
    {
        var hit = root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        Assert.True(hit is not null, $"'{name}' is not in the realized visual tree");
        return hit!;
    }
}
