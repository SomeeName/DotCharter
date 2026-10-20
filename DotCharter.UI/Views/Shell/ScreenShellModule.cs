using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     The screen and dialog machinery's registrations, called once from
///     <c>App.ConfigureServices</c>: the <see cref="ScreenCatalog" />, a
///     navigator per <see cref="ScreenHost" /> scope, and the app-wide dialogs.
/// </summary>
public static class ScreenShellModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(ScreenCatalog.Default);
        NavigationModule.Register(services);
        DialogsModule.Register(services);
    }
}
