// Composition root, online slice: the TONE3000 NAM-capture stack and its settings
// view model, plus GitHub sign-in + the app update service. The account/scoreboard/
// social backend and its token stores are not part of the app.

using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI;

public partial class App
{
    private static void AddOnlineServices(IServiceCollection services)
    {
        // TONE3000 NAM-capture browser (0.16): OAuth PKCE sign-in (DPAPI
        // session, loopback callback on port 47821 — in the firewall-allowed
        // range), the API client (public search; Bearer downloads) and the
        // local capture library under %APPDATA%/DotCharter/models/nam. The
        // native "namamp" playback module lands with Wave 3 (ABI v22); until
        // then the pedalboard's "Load into rig" renders disabled.
        services.AddSingleton<DotCharter.Tone.Online.Tone3000.ITone3000TokenStore>(sp =>
            new DotCharter.Tone.Online.Tone3000.DpapiTone3000TokenStore(sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Tone.Online.Tone3000.ITone3000AuthService>(sp =>
            new DotCharter.Tone.Online.Tone3000.Tone3000AuthService(
                () => sp.GetRequiredService<IConfigurationService>().LoadAppConfig(),
                sp.GetRequiredService<DotCharter.Tone.Online.Tone3000.ITone3000TokenStore>(),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Tone.Online.Tone3000.ITone3000Service>(sp =>
            new DotCharter.Tone.Online.Tone3000.Tone3000Client(
                ct => sp.GetRequiredService<DotCharter.Tone.Online.Tone3000.ITone3000AuthService>()
                    .GetAccessTokenAsync(ct),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Tone.Online.Tone3000.INamCaptureLibrary>(sp =>
            new DotCharter.Tone.Online.Tone3000.NamCaptureLibrary(
                sp.GetRequiredService<DotCharter.Tone.Online.Tone3000.ITone3000Service>(),
                sp.GetService<IAppLogger>()));


        // Auto-update + GitHub sign-in (0.12.5): DPAPI token store, Device
        // Flow / PAT auth and the GitHub releases update checker. The token
        // provider hook is what makes authenticated (private-repo) update
        // checks work once the user signs in under Settings → Updates. The feed is the
        // public repo unless a loaded plugin registered its own IUpdateFeed.
        services.AddSingleton<DotCharter.Services.Update.GitHubTokenStore>(_ =>
            new DotCharter.Services.Update.GitHubTokenStore());
        services.AddSingleton<DotCharter.Services.Update.IGitHubAuthService>(sp =>
            new DotCharter.Services.Update.GitHubAuthService(
                sp.GetRequiredService<DotCharter.Services.Update.GitHubTokenStore>(),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Update.IAppUpdateService>(sp =>
            DotCharter.Services.Update.GitHubUpdateService.For(
                DotCharter.Services.Update.UpdateFeed.Select(sp.GetServices<DotCharter.Services.Update.IUpdateFeed>()),
                System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0),
                () => sp.GetRequiredService<DotCharter.Services.Update.IGitHubAuthService>().GetToken(),
                sp.GetService<IAppLogger>(), currentVersionText: DotCharter.Services.Infrastructure.AppVersionInfo.Current));
        // Files published on the app's own private releases (the 13 s BS-RoFormer SW stem
        // model), downloaded with the same sign-in and the same asset endpoint as an
        // update. Stem models reach it through StemModelManager.
        services.AddSingleton<DotCharter.Services.Update.IGitHubReleaseAssetDownloader>(sp =>
            DotCharter.Services.Update.GitHubReleaseAssetDownloader.For(
                sp.GetRequiredService<DotCharter.Services.Update.IGitHubAuthService>(),
                sp.GetService<IAppLogger>(),
                appVersion: null));
    }
}
