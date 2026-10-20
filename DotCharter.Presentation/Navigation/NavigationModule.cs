using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Presentation.Navigation;

/// <summary>Registers navigation per host scope (target-architecture.md § 3.1).</summary>
public static class NavigationModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<ScreenPresenterSlot>();
        services.AddScoped<INavigator>(sp => new Navigator(sp.GetRequiredService<ScreenPresenterSlot>()));
    }
}
