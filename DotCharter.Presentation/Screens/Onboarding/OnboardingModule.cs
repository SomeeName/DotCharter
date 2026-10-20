using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Presentation.Screens.Onboarding;

/// <summary>The first-run wizard's Presentation parts; one check per process.</summary>
public static class OnboardingModule
{
    public static void Register(IServiceCollection services) => services.AddSingleton<SystemCheckViewModel>();
}
