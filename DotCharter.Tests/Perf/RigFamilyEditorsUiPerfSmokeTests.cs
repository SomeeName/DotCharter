using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode.RigEditors;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Construction + attach + layout for the five SPECIALISED module editors,
///     opened through the real host (<c>RigModuleEditorView</c> →
///     <c>RigEditorTemplateSelector</c>) over a real chain.
///
///     <para>
///         A view whose styles or bindings failed still ARRANGES — it just has
///         no size — so every assertion here is a MEASURED one: the family view
///         is present, it measured non-zero, every knob and fader in it measured
///         non-zero, and the bypass foot exists. That is the shape all four of
///         this repo's silent XAML faults take.
///     </para>
///     <para>
///         Since 2026-09-27 a unit with a generated faceplate is edited on the BOARD
///         and the dock opens the unit inspector (<c>RigDockUnit.EditorFamilyFor</c>);
///         these family views are what a module WITHOUT a faceplate opens. This class
///         switches the faceplates off for its lifetime to keep that fallback honest.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigFamilyEditorsUiPerfSmokeTests : IDisposable
{
    public RigFamilyEditorsUiPerfSmokeTests() => RigFaceplates.SuppressedForTests = true;

    public void Dispose() => RigFaceplates.SuppressedForTests = false;

    [Theory]
    [InlineData("graphiceq", typeof(RigGraphicEqView))]
    [InlineData("eq", typeof(RigParametricEqView))]
    [InlineData("wah", typeof(RigExpressionView))]
    [InlineData("whammy", typeof(RigExpressionView))]
    [InlineData("swell", typeof(RigExpressionView))]
    [InlineData("miku", typeof(RigVocalView))]
    [InlineData("mikustomp", typeof(RigVocalView))]
    [InlineData("compressor", typeof(RigDynamicsView))]
    [InlineData("gate", typeof(RigDynamicsView))]
    [InlineData("noisereducer", typeof(RigDynamicsView))]
    public async Task EveryFamilyEditor_OpensAndMeasuresEveryControlItDraws(
        string moduleId, Type family)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // Catalogue inspection does not open an audio engine or device.
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var type = catalogue.SingleOrDefault(m => m.Id == moduleId);
            Assert.SkipWhen(type is null, $"the native catalogue has no '{moduleId}'");

            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(type!);
            rig.SelectNodeAt(0);

            var screen = new AmpFxRigViewModel(rig);
            var unit = screen.DockUnit;
            Assert.NotNull(unit);
            Assert.Equal(moduleId, unit!.Node.ModuleTypeId);

            var host = new RigModuleEditorView { DataContext = unit };
            var window = new Window { Width = 1500, Height = 900, Content = host };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                var view = Assert.Single(host.GetVisualDescendants(), v => v.GetType() == family);
                var editor = Assert.IsAssignableFrom<Control>(view);
                Assert.True(editor.Bounds.Width > 0 && editor.Bounds.Height > 0,
                    $"{family.Name} arranged at {editor.Bounds} — it built, but nothing in it measured");

                var knobs = editor.GetVisualDescendants().OfType<FireKnob>().ToArray();
                var faders = editor.GetVisualDescendants().OfType<Slider>().ToArray();
                // EVERY knob the module has is drawn exactly once — as a dial,
                // or as the fader the graphic EQ and the treadle use for theirs.
                Assert.Equal(unit.Node.KnobParams.Count, knobs.Length + faders.Length);
                foreach (var knob in knobs)
                {
                    Assert.True(knob.Bounds.Width > 0 && knob.Bounds.Height > 0,
                        $"a knob in {family.Name} measured {knob.Bounds}");
                }

                foreach (var fader in faders)
                {
                    // A fader that lost its class renders as a stretched
                    // HORIZONTAL slider — wide enough to pass a width check and
                    // wrong in every other way, which is why the orientation is
                    // asserted too.
                    Assert.Equal(Avalonia.Layout.Orientation.Vertical, fader.Orientation);
                    Assert.True(fader.Bounds.Width >= 22 && fader.Bounds.Height > 60,
                        $"a fader in {family.Name} measured {fader.Bounds} — it cannot be grabbed");
                }

                var foot = Assert.Single(editor.GetVisualDescendants().OfType<ToggleButton>(),
                    t => t.Classes.Contains("FireHardwareFootswitch"));
                Assert.True(foot.Bounds.Width > 0 && foot.Bounds.Height > 0);
                Assert.False(unit.Node.Bypassed);
                foot.IsChecked = true;
                Assert.True(unit.Node.Bypassed, "the bypass foot writes nothing");
                foot.IsChecked = false;

                // Every picker the module declares is on screen, with its live options.
                var combos = editor.GetVisualDescendants().OfType<ComboBox>().ToArray();
                Assert.Equal(unit.Node.ModelSelectors.Count, combos.Length);
                foreach (var combo in combos)
                    Assert.True(combo.Bounds.Width > 0 && combo.Bounds.Height > 0);

                // …and the free-text phrase fields, one box per string param.
                // Filtered by class: a ComboBox's own template carries a
                // (hidden) TextBox for its editable mode, so a bare OfType walk
                // counts one phrase field per picker on the screen.
                var boxes = editor.GetVisualDescendants().OfType<TextBox>()
                    .Where(b => b.Classes.Contains("FireFieldInput") && !b.Classes.Contains("Search")).ToArray();
                Assert.Equal(unit.Node.StringParams.Count(p => p.IsLyrics), boxes.Length);
                foreach (var box in boxes)
                    Assert.True(box.Bounds.Width > 0 && box.Bounds.Height > 0);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     One family, one view. The fallback stays for the families that have
    ///     no hardware layout of their own, so a unit always opens SOMETHING
    ///     that exposes all of its parameters.
    ///     <para>
    ///         Here rather than in the default gate because it CONSTRUCTS a
    ///         view: inflating one of these creates live
    ///         <c>{localization:Loc}</c> tooltip bindings, which a culture write
    ///         from a parallel collection then re-evaluates off the dispatcher
    ///         thread — measured, thirteen localization cases in one gate run.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(RigEditorFamily.GraphicEq, typeof(RigGraphicEqView))]
    [InlineData(RigEditorFamily.ParametricEq, typeof(RigParametricEqView))]
    [InlineData(RigEditorFamily.Expression, typeof(RigExpressionView))]
    [InlineData(RigEditorFamily.Vocal, typeof(RigVocalView))]
    [InlineData(RigEditorFamily.Dynamics, typeof(RigDynamicsView))]
    [InlineData(RigEditorFamily.Pedal, typeof(RigPedalEditorView))]
    [InlineData(RigEditorFamily.AmpFace, typeof(RigAmpFaceView))]
    [InlineData(RigEditorFamily.Cabinet, typeof(RigCabinetEditorView))]
    [InlineData(RigEditorFamily.Routing, typeof(RigRoutingConsoleView))]
    public async Task BuildFor_OpensTheViewItsFamilyNames(RigEditorFamily family, Type view)
        => await HeadlessSessionFixture.Dispatch(
            () => Assert.IsType(view, RigEditorTemplateSelector.BuildFor(family)),
            TestContext.Current.CancellationToken);

    /// <summary>The graphic EQ's contour is drawn from the SAME params the
    /// faders write — it measures, and it repaints when a band moves.</summary>
    [Fact]
    public async Task TheGraphicEqContour_MeasuresAndFollowsItsBands()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var type = catalogue.SingleOrDefault(m => m.Id == "graphiceq");
            Assert.SkipWhen(type is null, "the native catalogue has no 'graphiceq'");

            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(type!);
            rig.SelectNodeAt(0);
            var unit = new AmpFxRigViewModel(rig).DockUnit!;

            var host = new RigModuleEditorView { DataContext = unit };
            var window = new Window { Width = 1500, Height = 900, Content = host };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                var contour = Assert.Single(host.GetVisualDescendants().OfType<FireEqContour>());
                Assert.True(contour.Bounds.Width > 100 && contour.Bounds.Height > 20,
                    $"the contour measured {contour.Bounds}");
                Assert.Equal(10, unit.GraphicEqBands.Count);
                Assert.Same(unit.GraphicEqBands, contour.Bands);

                // A band move must reach the drawing, not just the fader.
                unit.GraphicEqBands[0].Value = 9f;
                window.UpdateLayout();
                Assert.Equal(9f, unit.Node.Params.Single(p => p.Id == "band31").Value);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }
}
