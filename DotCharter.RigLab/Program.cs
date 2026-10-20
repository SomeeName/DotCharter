using Avalonia;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.Platform;

namespace DotCharter.RigLab;

internal static class Program
{
    /// <summary>The per-user folder RigLab keeps its settings, rig and logs in —
    /// never DotCharter's, so the two apps can live side by side.</summary>
    public const string AppFolderName = "DotCharter RigLab";

    [STAThread]
    public static void Main(string[] args)
    {
        // First, before anything resolves a path (the logger and the config do).
        WritablePaths.UseAppFolderName(AppFolderName);

        // A 1 ms scheduler tick for the audio engine's timers, as in DotCharter.
        WindowsTimerResolution.Raise();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            WindowsTimerResolution.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // The same CJK/emoji fallback chain the app uses, so the rig's
            // Japanese and German labels shape identically in both hosts.
            .With(TextFallbackFonts.Options())
            .UseSkia()
            .UseHarfBuzz()
            .LogToTrace();
}
