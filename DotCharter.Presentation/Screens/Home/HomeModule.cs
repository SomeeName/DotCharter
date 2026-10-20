using DotCharter.Presentation.Contracts.Home;
using DotCharter.Services.Session;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Presentation.Screens.Home;

/// <summary>The Home area. Scoped, so the screen re-reads its figures per visit.</summary>
public static class HomeModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ISongLibraryLookup, SongLibraryLookup>();
        services.AddScoped<HomeSnapshotSource>();
        services.AddScoped<IHomeScreen, HomeViewModel>();
    }
}
