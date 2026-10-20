using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Panels;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #69: the bend editor draws its curve over the note's WHOLE sustain — a rise from 0 on the
///     attack into the first point and a hold after the last one, what playback does
///     (<see cref="BendEditorGeometry.BuildSustainSegments" />) — not only between the first and
///     the last point. Builds the control on a real window, lays it out and asserts the curve
///     spans the plot from its left edge to its right edge. Set <c>DOTCHARTER_BEND_CAPTURE_DIR</c>
///     (in a Skia-drawing session, <see cref="UiSweepRunner.CaptureEnabled" />) to write the rendered graph as a PNG.
/// </summary>
[Collection("HeadlessUI")]
public sealed class BendEditorSustainCurveUiPerfSmokeTests
{
    [Fact]
    public async Task Curve_CoversTheWholeSustain() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = new BendEditorViewModel(() => [], _ => { }, () => new FractionalPosition(0),
                () => new FractionalPosition(4), () => { });
            vm.AddOrMovePoint(3, 1.0); // ½ a quarter into the sustain
            vm.AddOrMovePoint(8, 2.0); // Full two thirds in

            var editor = new BendEditorControl { DataContext = vm };
            var window = new Window { Width = 520, Height = 260, Content = editor };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(editor.Bounds is { Width: > 1, Height: > 1 }, $"the bend editor laid out as {editor.Bounds}.");

                var dir = Environment.GetEnvironmentVariable("DOTCHARTER_BEND_CAPTURE_DIR");
                if (UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(dir, "bend-graph.png"), new PngBitmapEncoderOptions());
                }

                var canvas = editor.FindControl<Canvas>("GraphCanvas")!;
                var curve = canvas.Children.OfType<ShapePath>().Single(p => p.Name == BendEditorControl.CurvePathName);
                var left = BendEditorGeometry.PaddingLeft;
                var right = canvas.Bounds.Width - BendEditorGeometry.PaddingRight;
                var span = curve.Data!.Bounds;
                Assert.True(Math.Abs(span.Left - left) < 0.5, $"the curve starts at x={span.Left}, not the attack (x={left}).");
                Assert.True(Math.Abs(span.Right - right) < 0.5, $"the curve ends at x={span.Right}, not the sustain end (x={right}).");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
}
