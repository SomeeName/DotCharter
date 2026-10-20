using Avalonia;

namespace DotCharter.UI.Gallery;

public static class Program
{
    /// <summary>
    ///     <c>DotCharter.Gallery.exe</c> — opens the gallery window.
    ///     <c>DotCharter.Gallery.exe --capture &lt;dir&gt;</c> renders every page
    ///     and variant to PNG and exits.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        GalleryApp.CaptureDirectory = ParseCaptureDirectory(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static string? ParseCaptureDirectory(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--capture", StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < args.Length ? args[i + 1] : null;
        }

        return null;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    // Mirrors DotCharter.UI.Program's font + rendering setup so the gallery
    // shapes text exactly the way the app does (a Latin-only face with no
    // fallback chain renders CJK as tofu, which reads as an encoding bug).
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<GalleryApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(DotCharter.Tone.UI.Platform.TextFallbackFonts.Options())
            .UseSkia()
            .UseHarfBuzz();
}
