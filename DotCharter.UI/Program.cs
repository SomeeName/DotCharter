using Avalonia;

namespace DotCharter.UI;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    //
    // The runtime uses default Workstation GC settings (configured via the
    // .csproj). No playback-time GC tweaking is performed — the previous
    // PlaybackGcRegionController experiments did not eliminate the audible
    // dropouts the user reported, so the simpler default behaviour is back.
    [STAThread]
    public static void Main(string[] args)
    {
        // A split install runs from app\ and keeps the voice packs at its
        // top; the native store would otherwise look in app\voices. Before
        // anything can load juce_sharp.dll.
        DotCharter.Tone.Infrastructure.Storage.InstallLocation.ExportVoiceDirectory();

        // The renderer cannot name UserDataFolders (Rendering is Core-only), so
        // the host hands it the folder; a DOTCHARTER_DATA_ROOT launch keeps the
        // blob under the override like every other per-user path.
        DotCharter.Rendering.Vulkan.VulkanPipelineManager.PipelineCacheDirectory = PipelineCacheDirectory();

        // Guarantee 1 ms timer resolution for the whole process: the 3D render
        // loop paces its fps cap and idle park with Thread.Sleep(1..4), which
        // Windows quantises to the system timer resolution (~15.6 ms by
        // default). Without this the loop caps at ~64 fps on a high-refresh
        // monitor and every Sleep injects a 15 ms hitch. See
        // Tone.UI.Platform.WindowsTimerResolution.
        Tone.UI.Platform.WindowsTimerResolution.Raise();
        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Tone.UI.Platform.WindowsTimerResolution.Release();
        }
    }

    /// <summary>Where the Vulkan pipeline cache lives: <c>%LOCALAPPDATA%\DotCharter</c>,
    /// or its stand-in under a data-root override.</summary>
    internal static string PipelineCacheDirectory() => System.IO.Path.Combine(
        DotCharter.Tone.Infrastructure.Storage.UserDataFolders.LocalAppData, "DotCharter");

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        // ONE read of the raw config for every startup-only platform option
        // below (Platform.StartupConfigProbe): resolving ConfigRoot probes the
        // directory with a real file write, and doing that per option would repeat it.
        var rawConfig = ProbeRawAppConfig();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Every theme face is Latin-only, so without a fallback chain a CJK
            // song title renders as tofu boxes — which reads as mangled TEXT and
            // sends you looking for an encoding bug that is not there. See
            // Tone.UI.Platform.TextFallbackFonts.
            .With(Tone.UI.Platform.TextFallbackFonts.Options())
            .UseSkia()
            .UseHarfBuzz()
            .With(new Win32PlatformOptions
            {
                // AngleEgl is pinned for AVALONIA'S OWN compositor, not for
                // any app GL code (the app's OpenGL 2D path was removed in
                // v0.33 — no OpenGlControlBase remains). Both composed-Vulkan
                // paths (3D preview + 2D timeline) import D3D11 keyed-mutex
                // shared textures through the compositor's GPU interop, and
                // their visuals carry a Scale(1,-1,1) counter-flip that was
                // verified live against the ANGLE (D3D11) backend — ANGLE
                // imports D3D11 textures bottom-up. Dropping the pin would
                // require re-verifying interop availability AND the flip
                // orientation on whatever backend Avalonia then picks; keep
                // it until that verification is done deliberately.
                RenderingMode = new[] { Win32RenderingMode.AngleEgl },
                // Overlay popups render flyouts/menus inside the main window's
                // composition instead of spawning a native popup HWND per open
                // — the per-open HWND + GPU-surface creation measurably
                // hitched the 2D timeline (a ~200 ms freeze
                // on every Tempo/Mix/Tools/Tracks open). Always
                // on: the 3D preview is composed (no child HWND), so nothing
                // in the window paints over an in-window popup.
                OverlayPopups = true,
                // The Win32 COMPOSITION MODE is the 2D timeline's fps ceiling
                // (v0.32.5 finding) AND the popup hosts' transparency budget.
                // LowLatencyDxgiSwapChain ticks on the render timer instead of
                // per DWM commit (2D interval p50 9.5 → 3.8-7 ms) but gives
                // every top-level an opaque DXGI swapchain, so it is opt-in
                // only — see Win32CompositionModeSelector for the full rule and
                // why the default must stay transparency-capable.
                CompositionMode = Platform.Win32CompositionModeSelector.Select(
                    Platform.StartupConfigProbe.StringValue(rawConfig, "Win32CompositionMode"))
            })
            .LogToTrace();
    }

    /// <summary>Pre-Avalonia read of the raw appconfig.json (platform options
    /// are startup-only, long before DI/config services exist). Tolerant: a
    /// missing or unreadable file is null, which the policy reads as the
    /// default.</summary>
    private static string? ProbeRawAppConfig()
    {
        try
        {
            var path = System.IO.Path.Combine(
                DotCharter.Tone.Infrastructure.Storage.WritablePaths.ConfigRoot, "appconfig.json");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
