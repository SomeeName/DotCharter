// The composed surfaces' pixel grid, in a REAL window: a fractional shell
// scale must still hand the compositor a whole-pixel rectangle.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <b>Why a window is needed at all.</b> <c>ComposedSurfaceGeometryTests</c>
///     pins the arithmetic and runs in the default gate. What it cannot see is
///     the input: whether a control under the shell's <c>RootScaler</c> really
///     does report inner-unit <c>Bounds</c> and a fractional device origin, so
///     that the arithmetic has anything to correct. Both halves of the answer
///     come from Avalonia's layout, and Avalonia's layout needs a top level.
///     <para>
///         <c>RenderScaling</c> is 1.0 on the headless backend, so the OS half
///         of the product is the pure test's job; what a headless window can
///         speak to honestly is the TRANSFORM half — and that is the half the
///         interface-scale wave added.
///     </para>
///     <para>
///         <c>[Collection("HeadlessUI")]</c>, i.e. the separate headless
///         invocation (root <c>AGENTS.md</c> § Validation).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ComposedSurfaceGridUiPerfSmokeTests
{
    /// <summary>The shell factors <c>AutoUiScale</c>'s 0.05 grid produces on the
    /// panels that put a user on Auto in the first place.</summary>
    [Theory]
    [InlineData(1.05)]
    [InlineData(1.15)]
    [InlineData(1.30)]
    public async Task UnderAFractionalShellScale_TheSurfaceRectangleIsWholePixels(double uiScale)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // A fractional offset above the surface, exactly as a dock row and a
            // splitter leave it: this is what makes the ORIGIN fractional once
            // the shell factor multiplies it.
            var surface = new Border { Margin = new Thickness(0, 37, 0, 0) };
            var host = new Grid { Children = { surface } };
            var scaler = new LayoutTransformControl { Name = WindowUiScale.ScalerName, Child = host };
            var window = new Window { Width = 1600, Height = 901, Content = scaler };
            window.Show();
            try
            {
                scaler.LayoutTransform = new ScaleTransform(uiScale, uiScale);
                scaler.Measure(new Size(1600, 901));
                scaler.Arrange(new Rect(0, 0, 1600, 901));
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

                var bounds = surface.Bounds;
                var pixelScale = VisualPixelScale.For(surface);
                Assert.Equal(uiScale, pixelScale, 6);

                // The shipped pairing: ceil() texels into an unrounded
                // destination. Stated first so this case cannot pass vacuously —
                // if the geometry were already whole there would be nothing to
                // fix and nothing below would be evidence.
                var shippedTexels = Math.Ceiling(bounds.Height * pixelScale);
                Assert.NotEqual(shippedTexels, bounds.Height * pixelScale, 6);

                var size = ComposedSurfaceGeometry.Measure(bounds.Width, bounds.Height, pixelScale);
                Assert.Equal(size.PixelWidth, size.DipWidth * pixelScale, 6);
                Assert.Equal(size.PixelHeight, size.DipHeight * pixelScale, 6);

                // …and the origin the compositor will place it at.
                var origin = VisualPixelScale.DeviceOrigin(surface);
                Assert.NotNull(origin);
                Assert.NotEqual(Math.Round(origin!.Value.Y), origin.Value.Y, 6);

                var snappedY = origin.Value.Y +
                               ComposedSurfaceGeometry.SnapOffset(origin.Value.Y, pixelScale) * pixelScale;
                Assert.Equal(Math.Round(snappedY), snappedY, 6);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>The no-transform case must come out of the same code path
    /// untouched — the surface that was never blurry must not start moving.</summary>
    [Fact]
    public async Task WithNoShellScale_NothingIsResizedAndNothingIsNudged()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var surface = new Border { Margin = new Thickness(0, 37, 0, 0) };
            var window = new Window { Width = 1600, Height = 901, Content = new Grid { Children = { surface } } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

                var bounds = surface.Bounds;
                var pixelScale = VisualPixelScale.For(surface);
                var size = ComposedSurfaceGeometry.Measure(bounds.Width, bounds.Height, pixelScale);

                Assert.Equal(bounds.Width, size.DipWidth, 6);
                Assert.Equal(bounds.Height, size.DipHeight, 6);

                var origin = VisualPixelScale.DeviceOrigin(surface);
                Assert.NotNull(origin);
                Assert.Equal(0.0, ComposedSurfaceGeometry.SnapOffset(origin!.Value.Y, pixelScale), 9);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
