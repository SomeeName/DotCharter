// Statics: the process-wide hooks the composition root wires from its services, once, at startup.
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.UI.ViewModels.CommandPalette;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI;

/// <summary>
///     The types that are not built by DI but need a service: each gets it through ONE assignment
///     here, which <c>App.OnFrameworkInitializationCompleted</c> makes right after the provider
///     is built and before any catalogue or editor exists.
/// </summary>
internal static class AppStatics
{
    public static void Wire(IServiceProvider services)
    {
        var tracker = services.GetService<IUsageTracker>();
        // AppCommand is a plain data holder built from a catalogue rather than by DI.
        // Null in tests, where nothing is counted.
        AppCommand.Tracker = tracker;
        // ONE class handler counts every menu item and button in the app. The menu binds
        // its commands straight to view-model RelayCommands without going through the
        // palette, so without this the tools reached from the menu read as unused.
        Diagnostics.UsageInstrumentation.Install(tracker);
        // The static chord-creation paths read "Name new chords automatically" here; the
        // config read is cached, so each new chord sees the current setting.
        var config = services.GetRequiredService<IConfigurationService>();
        NewChordNames.Configure(() => config.LoadAppConfig().AutoNameNewChords);
    }
}
