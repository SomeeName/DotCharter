using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
///     The generated faceplates, attached and laid out over a real chain: the
///     editor draws the unit's face with its LIVE controls on the printed nuts,
///     every control measures and stands inside the face, turning one turns the
///     param, and the board tile draws the same face as a preview. With
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set,
///     each case writes a PNG for review.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigFaceplateUiPerfSmokeTests
{
    [Theory]
    [InlineData("rat")]
    [InlineData("multitap")]
    [InlineData("tubepre")]
    [InlineData("bassamp")]
    [InlineData("looper")]
    [InlineData("graphiceq")]
    public async Task TheEditor_DrawsTheFaceplate_WithEveryControlOnItsNut(string moduleId)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var face = RigFaceplates.For(moduleId);
            Assert.NotNull(face);

            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "overdrive"));
            rig.AddGear(catalogue.Single(m => m.Id == moduleId));
            var node = rig.Nodes.Single(n => n.ModuleTypeId == moduleId);
            rig.SelectNodeAt(rig.Nodes.IndexOf(node));
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 5; pass++) window.UpdateLayout();
                var unit = model.DockUnit!;
                if (!unit.IsPedalFamily) return; // a family view: the faceplate is the pedal family's face

                Assert.True(unit.ShowsFaceplate);
                var editor = Assert.Single(view.GetVisualDescendants().OfType<RigPedalEditorView>());
                var plate = Assert.Single(editor.GetVisualDescendants().OfType<RigFaceplateView>());
                Assert.True(plate.HasFaceplate);
                Assert.True(plate.Bounds.Width > 150 && plate.Bounds.Height > 100, $"faceplate {plate.Bounds}");
                Assert.DoesNotContain(editor.GetVisualDescendants().OfType<Border>(),
                    b => b.Classes.Contains("FireEnclosure") && b.IsEffectivelyVisible);

                // Every face control whose param the node has is a live control on
                // its nut, measured, and inside the face.
                var expected = face.Controls.Count(c => node.Params.Any(p => p.Id == c.Param));
                Assert.Equal(expected, plate.Placed.Count);
                Assert.True(expected > 0, "nothing to place");
                var controls = plate.Children.Where(c => c.Tag is RigFaceControl).ToList();
                Assert.Equal(expected, controls.Count);
                var faceRect = plate.FaceRect(plate.Bounds.Size);
                foreach (var control in controls)
                {
                    var spec = (RigFaceControl)control.Tag!;
                    Assert.True(control.Bounds.Width > 6 && control.Bounds.Height > 6, $"{spec.Param} measured {control.Bounds}");
                    var centre = control.Bounds.Center;
                    var want = new Point(faceRect.X + spec.X * faceRect.Width, faceRect.Y + spec.Y * faceRect.Height);
                    Assert.True(Math.Abs(centre.X - want.X) < 1.5 && Math.Abs(centre.Y - want.Y) < 1.5,
                        $"{spec.Param} stands at {centre}, its nut is at {want}");
                    Assert.True(faceRect.Contains(centre), $"{spec.Param} is off the face");
                }

                // A knob on the face IS the param: turning one moves the value.
                // (a continuous one: a stepped knob snaps, which is its own test)
                var knob = controls.OfType<FireKnob>()
                    .First(k => node.Params.Single(p => p.Id == ((RigFaceControl)k.Tag!).Param) is { Step: 0, HasValueLabels: false });
                var param = node.Params.Single(p => p.Id == ((RigFaceControl)knob.Tag!).Param);
                var target = param.Minimum + (param.Maximum - param.Minimum) * 0.73f;
                knob.Value = target;
                Assert.Equal(target, param.Value, 2);
                Assert.All(controls.OfType<FireKnob>(), k => Assert.False(string.IsNullOrEmpty(k.CapFile)));

                // Every face control's tooltip opens at once: the exact value is why the
                // player hovers.
                Assert.All(controls, c => Assert.Equal(0, ToolTip.GetShowDelay(c)));

                // Drawn scaled (down on the chain, up on the stage): the face and every
                // cap must use the high-quality filter, or the printed scales blur.
                Assert.Equal(Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality,
                    Avalonia.Media.RenderOptions.GetBitmapInterpolationMode(plate.Children.OfType<FaceSurface>().Single()));
                Assert.All(controls.OfType<FireKnob>(), k => Assert.Equal(Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality,
                    Avalonia.Media.RenderOptions.GetBitmapInterpolationMode(k)));

                // The board tile draws the same face as a preview.
                var tile = view.GetVisualDescendants().OfType<RigFaceplateView>()
                    .First(f => !f.IsInteractive && ReferenceEquals(f.Node, node));
                Assert.True(tile.HasFaceplate && tile.Bounds.Height > 20, $"board tile {tile.Bounds}");

                Capture(window, $"faceplate-{moduleId}.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A selector points AT its printed labels, not between them: the miku's
    ///     octave prints −1 Oct left, Unison top, +1 Oct right and +2 Oct bottom
    ///     (owner, 2026-09-27), so each value turns the cap to that side.
    /// </summary>
    [Fact]
    public Task ASelector_PointsAtThePrintedLabelForEachValue() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "miku"));
            var node = rig.Nodes.Single();
            var plate = new RigFaceplateView { Node = node };
            var window = new Window { Width = 800, Height = 1000, Content = plate };
            window.Show();
            try
            {
                window.UpdateLayout();
                var octave = plate.Children.OfType<FireKnob>().Single(k => ((RigFaceControl)k.Tag!).Param == "octave");
                var param = node.Params.Single(p => p.Id == "octave");
                Assert.NotNull(octave.StepAngles);
                foreach (var (value, angle) in new[] { (-1f, -90.0), (0f, 0.0), (1f, 90.0), (2f, 180.0) })
                {
                    param.Value = value;
                    Assert.Equal(angle, octave.IndicatorAngleDegrees, 3);
                }
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>Every faceplate names only params its module really has, and every
    /// knob-like control on it is backed by a live param on a fresh node — a face
    /// control nothing drives would be a knob that turns nothing.</summary>
    [Fact]
    public Task EveryFaceplate_MatchesItsModulesParams() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            var problems = new List<string>();
            foreach (var module in catalogue)
            {
                var face = RigFaceplates.For(module.Id);
                if (RigFaceplates.DrawnEnclosureByDesign.Contains(module.Id))
                {
                    if (face is not null) problems.Add($"{module.Id}: has a faceplate, but is drawn as an enclosure by design");
                    continue;
                }

                if (face is null)
                {
                    problems.Add($"{module.Id}: no faceplate");
                    continue;
                }

                var ids = module.Params.Select(p => p.Id).Concat(module.StringParams.Select(p => p.Id)).ToHashSet();
                problems.AddRange(face.Controls.Where(c => !ids.Contains(c.Param)).Select(c => $"{module.Id}.{c.Param}: not a param"));
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }, TestContext.Current.CancellationToken);

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
