using DotCharter.Presentation.Contracts.Home;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views.Home;

/// <summary>
///     Builds the <see cref="ScreenHost" /> the shell's <c>HomeHost</c> shows.
///     Two lines, because Home has no legacy half to strangle — it was written
///     on the target architecture from the first commit.
/// </summary>
public static class HomeSurface
{
    public static ScreenHost Create(IServiceProvider services)
    {
        var host = new ScreenHost(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ScreenCatalog>());
        host.Navigator.Show<IHomeScreen>();
        return host;
    }
}
