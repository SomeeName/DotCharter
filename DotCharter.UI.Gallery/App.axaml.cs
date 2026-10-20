using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace DotCharter.UI.Gallery;

/// <summary>
///     The gallery's own <see cref="Application" />. Deliberately tiny: it
///     loads the shared styles and shows <see cref="GalleryWindow" />. Nothing
///     is registered, nothing is started, no engine is opened.
/// </summary>
public sealed class GalleryApp : Application
{
    /// <summary>
    ///     Set by <see cref="GalleryCapture" /> so the app exits after writing
    ///     PNGs instead of showing a window.
    /// </summary>
    public static string? CaptureDirectory { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (CaptureDirectory is { Length: > 0 } dir)
            {
                // POSTED, not run here: Shutdown() called before the lifetime has
                // entered its main loop throws "Dispatcher shut down" when the
                // loop then starts. One turn later the loop is running and
                // Shutdown ends it cleanly.
                Dispatcher.UIThread.Post(() =>
                {
                    GalleryCapture.Run(dir);
                    desktop.Shutdown();
                });
            }
            else
            {
                desktop.MainWindow = new GalleryWindow { DataContext = new GalleryViewModel() };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
