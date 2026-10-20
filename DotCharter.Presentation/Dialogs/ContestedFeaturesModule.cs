using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Import.Youtube;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Presentation.Dialogs;

/// <summary>The one place the contested features register; nothing is registered when the switch is off.</summary>
public static class ContestedFeaturesModule
{
    public static void Register(IServiceCollection services, bool enabled)
    {
        if (!enabled) return;

        services.AddSingleton<IYtDlpAudioDownloader>(sp =>
            new YtDlpAudioDownloader(sp.GetRequiredService<IConfigurationService>(), sp.GetService<IAppLogger>()));
        services.AddSingleton<YoutubeAudioDownload>();
        services.AddSingleton<YoutubeAudioLauncher>();
        services.AddScoped<IYoutubeAudioScreen, YoutubeAudioViewModel>();
    }
}
