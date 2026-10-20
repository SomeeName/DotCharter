using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotCharter.Localization;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.UI.Platform;

namespace DotCharter.RigLab;

public sealed class App : Application
{
    /// <summary>Set by the headless tests: build everything, open no audio device.</summary>
    public const string UseNullAudioSwitch = "DotCharter.RigLab.UseNullAudio";

    private RigLabHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var config = new RigLabConfigService();
        LocalizationService.Instance.SetConfiguredCulture(config.Load().LanguageCulture);

        AppContext.TryGetSwitch(UseNullAudioSwitch, out var useNullAudio);
        var host = new RigLabHost(config, new AvaloniaUiDispatcher(), useNullAudio);
        _host = host;
        host.Logger.Info(AppLogCategory.Application,
            $"RigLab starting. Settings: {config.FilePath}");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new RigLabShellViewModel(host.Screen, host.Plugins, host.Settings, host.Ai);
            host.OpenAiSettings = () => shell.Tab = RigLabTab.Ai;
            var window = new RigLabWindow { DataContext = shell };
            RestoreWindowSize(window, config.Load());
            window.Closing += (_, _) => SaveWindowSize(window, config);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => host.Dispose();
            window.Opened += (_, _) => _ = host.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RestoreWindowSize(Window window, RigLabConfig config)
    {
        window.Width = Math.Clamp(config.WindowWidth, 1100, 7680);
        window.Height = Math.Clamp(config.WindowHeight, 700, 4320);
        if (config.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    private static void SaveWindowSize(Window window, RigLabConfigService config)
    {
        var maximized = window.WindowState == WindowState.Maximized;
        var width = window.Bounds.Width;
        var height = window.Bounds.Height;
        config.Update(c =>
        {
            c.WindowMaximized = maximized;
            // A maximized window's bounds are the screen's; keep the restored size.
            if (!maximized && width > 0 && height > 0)
            {
                c.WindowWidth = width;
                c.WindowHeight = height;
            }
        });
    }
}
