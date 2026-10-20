using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using DotCharter.UI.Gallery.Pages;

namespace DotCharter.UI.Gallery;

/// <summary>
///     <c>DotCharter.Gallery.exe --capture &lt;dir&gt;</c>: every page × variant
///     to <c>gallery-&lt;section&gt;-&lt;page&gt;-&lt;variant&gt;.png</c> at
///     1600×900, then exit.
/// </summary>
internal static class GalleryCapture
{
    public const int Width = 1600;
    public const int Height = 900;

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);

        foreach (var page in GalleryCatalog.All)
        {
            foreach (var variant in page.Variants)
            {
                // Hosted in a REAL Window, not rendered detached: a detached subtree
                // has no style scope, so every DynamicResource would resolve against
                // nothing and the capture would show an unstyled page (the same trap
                // as realizing a popover while it is detached).
                var window = new Window
                {
                    Width = Width,
                    Height = Height,
                    ShowInTaskbar = false,
                    Content = variant.Create()
                };

                window.Show();
                try
                {
                    window.UpdateLayout();
                    using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(directory, FileName(page, variant)), new PngBitmapEncoderOptions());
                    Console.WriteLine($"wrote {FileName(page, variant)}");
                }
                finally
                {
                    window.Close();
                }
            }
        }
    }

    /// <summary>Same slugs as the goldens (<c>ScreenEntry.GoldenPath</c>), so a gallery PNG and its golden name the same screen.</summary>
    public static string FileName(GalleryPage page, GalleryVariant variant) =>
        $"gallery-{Slug(page.Section)}-{Slug(page.Name)}-{Slug(variant.Name)}.png";

    private static string Slug(string s) => DotCharter.UI.Views.Shell.ScreenCatalog.Slug(s);
}
