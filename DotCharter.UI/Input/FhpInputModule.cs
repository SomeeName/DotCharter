using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Input;

/// <summary>The dialog doors and row-input launcher share one singleton and one draft host.</summary>
internal static class FhpInputModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ShellDialogsViewModel>();
        services.AddSingleton<IFhpEditLauncher>(sp => sp.GetRequiredService<ShellDialogsViewModel>());
    }
}
