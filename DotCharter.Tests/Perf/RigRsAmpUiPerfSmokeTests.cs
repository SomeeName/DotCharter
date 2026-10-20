using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tests.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode.RigEditors;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <c>rsamp</c>, Rocksmith's own amp chains (#88), in the rig editor over a real
///     catalogue: the drawn enclosure measures with its model picker, ten knobs and its level,
///     the knobs carry the picked amp's own knob names, and picking another amp
///     relabels them and greys the ones that amp does not have. Own process
///     (headless suite), not the default gate.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigRsAmpUiPerfSmokeTests
{
    [Fact]
    public Task TheEditor_NamesTheKnobsAfterThePickedAmp_AndRelabelsOnAPick() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            // The chains are the install's (#755): the synthetic one here.
            using var game = SyntheticRsGame.Use();
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "rsamp"));
            var node = rig.Nodes.Single();
            rig.SelectNodeAt(0);
            var model = new AmpFxRigViewModel(rig) { IsDetailsOpen = true };
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                Assert.True(model.DockUnit!.ShowsDrawnEnclosure);

                var pedal = Assert.Single(view.GetVisualDescendants().OfType<RigPedalEditorView>());
                var enclosure = Assert.Single(pedal.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("FireEnclosure"));
                Assert.True(enclosure.Bounds.Width > 200 && enclosure.Bounds.Height > 150, $"enclosure {enclosure.Bounds}");
                var knobs = enclosure.GetVisualDescendants().OfType<FireKnob>().ToList();
                Assert.Equal(11, knobs.Count);   // ten knob positions and the output level
                Assert.All(knobs, k => Assert.True(k.Bounds.Width > 10 && k.Bounds.Height > 10, "a knob rendered nothing"));
                Assert.Single(enclosure.GetVisualDescendants().OfType<ComboBox>());   // the amp picker

                // The synthetic Amp_TestA: Gain, Bass, Mode.
                string Name(string id) => node.Params.Single(p => p.Id == id).DisplayName;
                bool Active(string id) => node.Params.Single(p => p.Id == id).IsControlActive;
                node.Params.Single(p => p.Id == "model").Value = SyntheticRsGame.Model("Amp_TestA");
                for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
                Assert.Equal(["Gain", "Bass", "Mode"], new[] { "k1", "k2", "k3" }.Select(Name));
                Assert.False(Active("k4"));
                Assert.Contains(enclosure.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Mode");

                // Pick DI_Amp_TestC: two knobs, Level and Tone.
                node.Params.Single(p => p.Id == "model").Value = SyntheticRsGame.Model("DI_Amp_TestC");
                for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
                Assert.Equal("Tone", Name("k2"));
                Assert.True(Active("k2"));
                Assert.False(Active("k3"));
                Assert.Contains(enclosure.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Tone");
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
}
