using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.Gallery;
using DotCharter.UI.Gallery.Pages;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Every page × variant in <see cref="GalleryCatalog.All" />, built for
///     real, attached to a window and laid out.
///     <para>
///         The gallery is where the UI redesign is drawn, so a page that stops
///         constructing there is a screen nobody notices is gone until they open
///         it. The assertion is the smoke-test minimum — the subtree MEASURED
///         NON-ZERO — because a view whose styles failed still arranges; it just
///         has no size, and that is the shape every XAML fault of this class
///         takes.
///     </para>
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///         <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, each case also writes
///         <c>gallery-&lt;section&gt;-&lt;page&gt;-&lt;variant&gt;.png</c> at
///         1600×900, the same way <see cref="PlayModeScreensCaptureUiPerfSmokeTests" />
///         does. Same file names as <c>DotCharter.Gallery.exe --capture</c>.
///     </para>
///     <para>
///         The headless session boots the REAL app, which is fine and is the
///         point: the gallery renders the production styles and the production
///         colour tokens, so a page laid out here is laid out on the same theme
///         the app ships.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class GalleryPagesUiPerfSmokeTests
{
    private const int WindowWidth = 1600;
    private const int WindowHeight = 900;

    public static IEnumerable<object[]> PagesAndVariants() =>
        from page in GalleryCatalog.All
        from variant in page.Variants
        select new object[] { page.Section, page.Name, variant.Name };

    [Theory]
    [MemberData(nameof(PagesAndVariants))]
    public async Task EveryGalleryPage_BuildsAttachesAndMeasuresNonZero(
        string section, string pageName, string variantName)
    {
        var page = GalleryCatalog.All.Single(p => p.Section == section && p.Name == pageName);
        var variant = page.Variants.Single(v => v.Name == variantName);

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var control = variant.Create();
            var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = control };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                Assert.True(control.Bounds is { Width: > 1000, Height: > 500 },
                    $"{section}/{pageName}/{variantName} laid out as {control.Bounds} — it rendered nothing");

                // …and its CONTENT, not just the stretched root. A page whose
                // body collapsed still fills the window: the root is stretched
                // by the host, so asserting only on it passes for a page that
                // renders nothing (measured — a MaxHeight="0" on the body did
                // not move the root's bounds by a pixel).
                var measured = control.GetVisualDescendants()
                    .Count(v => v.Bounds is { Width: > 1, Height: > 1 });
                Assert.True(measured > 10,
                    $"{section}/{pageName}/{variantName}: only {measured} visual descendants measured " +
                    "non-zero — the page's content collapsed inside a root the host still stretched");

                var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;

                Directory.CreateDirectory(dir);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(dir, GalleryCapture.FileName(page, variant)),
                    new PngBitmapEncoderOptions());
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The catalogue is not empty, and every page names at least one
    ///     variant. Without this the Theory above passes by having nothing to
    ///     run — the exact way a registry-driven suite goes vacuous.
    /// </summary>
    [Fact]
    public void TheCatalogue_HasPages_AndEveryPageHasAVariant()
    {
        Assert.NotEmpty(GalleryCatalog.All);
        foreach (var page in GalleryCatalog.All)
            Assert.True(page.Variants.Count > 0, $"{page.Section}/{page.Name} declares no variant");
    }
}
