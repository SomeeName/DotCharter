using DotCharter.Presentation.Contracts.Settings;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views.Settings;

/// <summary>
///     Builds the root <see cref="ScreenHost" /> the shell's <c>SettingsHost</c>
///     shows and shows the settings shell screen in it. Every tab is a screen of
///     its own inside the shell's tab host.
/// </summary>
public static class SettingsSurface
{
    public static ScreenHost Create(IServiceProvider services)
    {
        var host = new ScreenHost(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ScreenCatalog>());
        host.Navigator.Show<ISettingsShellScreen>();
        return host;
    }
}
