using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
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
///     The cabinet editor and its MIC over the real catalogue: the family view opens,
///     the shell, both IR slots, the voicing row and the response guide lay out; a cab
///     with no mic offers one, and adding it puts the native mic model straight after
///     the cab; real pointer drags on the speaker and on the side ruler write the mic
///     model's position and distance; and the mic unit on its own opens the full
///     placement. With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///     <c>DOTCHARTER_RIG_CAPTURE_DIR</c> each case writes a PNG. Own process.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigCabinetEditorUiPerfSmokeTests
{
    [Fact]
    public Task TheCabinet_OffersAMic_AndItsSpeakerAndRulerPlaceTheRealMicModel() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "namamp"));
            var cabNode = rig.AddGear(catalogue.Single(m => m.Id == "cab"))!;
            rig.AddGear(catalogue.Single(m => m.Id == "delay"));
            rig.SelectNodeAt(1);
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                Settle(window);
                var unit = model.DockUnit!;
                Assert.Equal(RigEditorFamily.Cabinet, unit.Family);
                var cab = Assert.Single(view.GetVisualDescendants().OfType<RigCabinetEditorView>());
                var shell = cab.FindControl<Border>("RigCabShell")!;
                Assert.True(shell.Bounds.Width > 200 && shell.Bounds.Height >= 200, $"shell {shell.Bounds}");
                var plot = cab.FindControl<RigFilterResponsePlot>("RigCabResponse")!;
                Assert.True(plot.Bounds.Height > 100);
                Assert.Equal(unit.CabVoicingKnobs.Count,
                    cab.FindControl<ItemsControl>("RigCabVoicing")!.GetVisualDescendants().OfType<FireKnob>().Count());

                // The cab model rail offers the whole 26-model roster (#74: 12-25 are
                // the 4x10 / 1x10 / 2x15 guitar, PA, hi-fi and bass layouts), laid
                // out, and picking one writes the native `model` param.
                var rail = unit.ModelRail!;
                Assert.Equal(26, rail.Options.Count);
                Assert.Equal("4x10 Tweed Open", rail.Options[12].Label);
                Assert.Equal("1x15 + 1x10 Bass", rail.Options[25].Label);
                var combo = cab.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.DataContext, rail));
                Assert.True(combo.IsEffectivelyVisible && combo.Bounds is { Width: > 80, Height: > 16 }, $"cab model rail {combo.Bounds}");
                rail.SelectedOption = rail.Options[23];
                Settle(window);
                Assert.Equal(23f, cabNode.Params.Single(p => p.Id == "model").Value);
                Assert.Same(rail.Options[23], combo.SelectedItem);

                // No mic yet: the speaker alone, and the way to put one on it.
                Assert.False(unit.HasCabMic);
                var add = cab.FindControl<Button>("RigCabAddMic")!;
                Assert.True(add.IsEffectivelyVisible && add.Bounds.Width > 20, $"add a mic {add.Bounds}");
                Capture(window, "cab-no-mic.png");
                add.Command!.Execute(null);
                Settle(window);

                // …which puts the mic model STRAIGHT AFTER the cab, and keeps the cab open.
                Assert.Equal(RigDockUnit.MicModuleId, rig.Nodes[rig.Nodes.IndexOf(cabNode) + 1].ModuleTypeId);
                Assert.Same(cabNode, rig.SelectedNode);
                unit = model.DockUnit!;
                Assert.True(unit.HasCabMic);
                var mic = unit.CabMic!;
                cab = Assert.Single(view.GetVisualDescendants().OfType<RigCabinetEditorView>());

                // A real drag on the speaker: the mic goes to the cone's edge.
                var stage = cab.FindControl<RigMicStage>("RigCabMicStage")!;
                Assert.True(stage.IsEffectivelyVisible && stage.Bounds.Width > 100
                                                      && Math.Abs(stage.Bounds.Width - stage.Bounds.Height) < 2, $"speaker {stage.Bounds}");
                var (centre, cone) = stage.Geometry();
                var from = stage.TranslatePoint(centre, window)!.Value;
                var to = stage.TranslatePoint(new Point(centre.X + cone * 1.2, centre.Y), window)!.Value;
                window.MouseDown(from, MouseButton.Left);
                window.MouseMove(to);
                window.MouseUp(to, MouseButton.Left);
                Assert.Equal(1.0, mic.Position, 2); // past the edge clamps to it
                Assert.Equal(90.0, mic.BearingDegrees, 1);
                Assert.Equal(3, mic.Zone);

                // The side ruler: a press 15 cm along it sets 15 cm.
                var side = cab.FindControl<RigMicSideView>("RigCabMicSide")!;
                Assert.True(side.IsEffectivelyVisible && side.Bounds.Height > 40, $"ruler {side.Bounds}");
                var (grille, length) = side.Ruler();
                var at = side.TranslatePoint(new Point(grille + length * 0.5, side.Bounds.Height * 0.9), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Assert.True(Math.Abs(mic.DistanceCm - 15) < 0.6,
                    $"ruler press gave {mic.DistanceCm} cm; hit {window.InputHitTest(at)?.GetType().Name} at {at}, ruler {side.Bounds} grille {grille} length {length}");

                // The mic picker: the ribbon.
                var chips = cab.FindControl<ItemsControl>("RigCabMicTypes")!.GetVisualDescendants().OfType<ToggleButton>().ToList();
                // one chip per mic the model offers: 57 / 121 / 421 / Condenser / Tube (#75)
                Assert.Equal(5, chips.Count);
                Assert.All(chips, c => Assert.True(c.Bounds is { Width: > 20, Height: > 10 }, $"mic chip {c.Bounds}"));
                chips[1].Command!.Execute(null);
                Assert.Equal(1, mic.MicIndex);
                Assert.Equal(1, stage.MicType);
                Settle(window);
                Capture(window, "cab-with-mic.png");
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheMicUnit_OpensTheFullPlacement() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "namamp"));
            rig.AddGear(catalogue.Single(m => m.Id == "cab"));
            var micNode = rig.AddGear(catalogue.Single(m => m.Id == RigDockUnit.MicModuleId))!;
            rig.SelectNodeAt(rig.Nodes.IndexOf(micNode));
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                Settle(window);
                var unit = model.DockUnit!;
                Assert.Equal(RigEditorFamily.MicPlacement, unit.Family);
                var placement = unit.MicPlacement!;
                var editor = Assert.Single(view.GetVisualDescendants().OfType<RigMicEditorView>());
                var full = Assert.Single(editor.GetVisualDescendants().OfType<RigMicPlacementView>());
                Assert.True(full.Bounds is { Width: > 600, Height: > 300 }, $"placement {full.Bounds}");

                var stage = full.FindControl<RigMicStage>("RigMicStage")!;
                Assert.True(stage.Bounds.Width > 250, $"speaker {stage.Bounds}");
                var cards = full.FindControl<ItemsControl>("RigMicChoices")!.GetVisualDescendants().OfType<ToggleButton>().ToList();
                // five cards since the tube condenser (#75), all in one row
                Assert.Equal(5, cards.Count);
                Assert.All(cards, c => Assert.True(c.Bounds is { Width: > 100, Height: > 40 }, $"mic card {c.Bounds}"));
                Assert.Single(cards.Select(c => c.TranslatePoint(default, full)!.Value.Y).Distinct());
                // angle (#75), room, second mic, level
                var knobs = full.FindControl<UniformGrid>("RigMicKnobs")!.GetVisualDescendants().OfType<FireKnob>().ToList();
                Assert.Equal(4, knobs.Count);
                // The model's knobs (now four, with the angle) never run into the
                // mic cards below them: the controls column is taller than the
                // speaker well, and a grid row does not clip what overflows it.
                var knobPanel = full.FindControl<Border>("RigMicKnobPanel")!;
                var knobsBottom = knobPanel.TranslatePoint(new Point(0, knobPanel.Bounds.Height), full)!.Value.Y;
                var cardsTop = cards.Min(c => c.TranslatePoint(default, full)!.Value.Y);
                Assert.True(knobsBottom <= cardsTop,
                    $"the mic knobs end at y={knobsBottom:0.#} but the mic cards start at y={cardsTop:0.#}");
                var angle = full.FindControl<FireKnob>("RigMicAngleKnob")!;
                Assert.True(angle.IsEffectivelyVisible && angle.Bounds is { Width: > 30, Height: > 30 }, $"angle knob {angle.Bounds}");
                Assert.Same(placement.Angle, angle.DataContext);

                // a click on the cap puts the mic there; the wheel brings it in
                var (centre, _) = stage.Geometry();
                var cap = stage.TranslatePoint(centre, window)!.Value;
                window.MouseDown(cap, MouseButton.Left);
                window.MouseUp(cap, MouseButton.Left);
                Assert.Equal(0.0, placement.Position, 2);
                Assert.Equal(0, placement.Zone);
                var before = placement.DistanceCm;
                window.MouseWheel(cap, new Vector(0, 1));
                Assert.Equal(before - 1, placement.DistanceCm, 1);

                cards[3].Command!.Execute(null);
                Assert.Equal(3, placement.MicIndex);
                Assert.True(placement.Mics[3].IsActive && !placement.Mics[0].IsActive);
                cards[4].Command!.Execute(null);
                Assert.Equal(4, placement.MicIndex);
                Assert.Equal(4, stage.MicType);
                placement.Angle!.Value = 45f;
                Settle(window);
                Capture(window, "mic-placement.png");
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);

    private static IReadOnlyList<DspModuleType> Catalogue()
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        return metadata.Catalog;
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
