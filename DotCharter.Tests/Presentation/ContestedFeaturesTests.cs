using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.StemSeparation.Components;
using DotCharter.Services.Import.Youtube;
using DotCharter.Services.Infrastructure.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The public-build switch (#824), both directions. The compile constant
///     cannot flip inside one test run, so every consumer takes the switch as a
///     parameter whose default is <see cref="ContestedFeatures.Enabled" />; these
///     tests pass <c>true</c> and <c>false</c> through that seam.
/// </summary>
public sealed class ContestedFeaturesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every service type the contested features register.</summary>
    private static readonly Type[] ContestedServices =
    [
        typeof(IYtDlpAudioDownloader),
        typeof(YoutubeAudioDownload),
        typeof(YoutubeAudioLauncher),
        typeof(IYoutubeAudioScreen)
    ];

    private static ServiceCollection Dialogs(bool contested)
    {
        var services = new ServiceCollection();
        DialogsModule.Register(services, contested);
        return services;
    }

    [Fact]
    public void SwitchOff_RegistersNoContestedService_ButStillRegistersTheOtherDialogs()
    {
        var services = Dialogs(contested: false);

        foreach (var type in ContestedServices)
            Assert.DoesNotContain(services, d => d.ServiceType == type);

        // Not vacuous: the module is not simply empty.
        Assert.Contains(services, d => d.ServiceType == typeof(IMidiImportScreen));
        Assert.Contains(services, d => d.ServiceType == typeof(IDialogs));
    }

    [Fact]
    public void SwitchOn_RegistersEveryContestedService()
    {
        var services = Dialogs(contested: true);

        foreach (var type in ContestedServices)
            Assert.Contains(services, d => d.ServiceType == type);
    }

    [Fact]
    public void TheDefaultFollowsTheCompileConstant()
    {
        var services = new ServiceCollection();
        DialogsModule.Register(services);

        Assert.Equal(ContestedFeatures.Enabled, services.Any(d => d.ServiceType == typeof(IYoutubeAudioScreen)));
    }

    [Fact]
    public void SwitchOff_TheContestedLauncherCannotBeResolved()
    {
        using var provider = Dialogs(contested: false).BuildServiceProvider();

        Assert.Null(provider.GetService<YoutubeAudioLauncher>());
        Assert.Null(provider.GetService<IYtDlpAudioDownloader>());
    }

    [Fact]
    public void ModuleRegistersNothingWhenOff_ThroughTheModuleAlone()
    {
        var services = new ServiceCollection();
        ContestedFeaturesModule.Register(services, enabled: false);

        Assert.Empty(services);

        ContestedFeaturesModule.Register(services, enabled: true);
        Assert.Equal(ContestedServices.Length, services.Count);
    }

    private sealed record Entry(string Id);

    [Fact]
    public void PaletteFilter_DropsExactlyTheRegisteredIds_OnlyWhenOff()
    {
        var all = new List<Entry>
        {
            new("file.attachYoutubeAudio"), new("file.attachAudio"), new("file.importMidi"), new("transport.playPause")
        };

        var off = ContestedFeatures.FilterPalette(all, e => e.Id, enabled: false);
        Assert.Equal(["file.attachAudio", "file.importMidi", "transport.playPause"], off.Select(e => e.Id));

        var on = ContestedFeatures.FilterPalette(all, e => e.Id, enabled: true);
        Assert.Equal(all.Select(e => e.Id), on.Select(e => e.Id));
    }

    [Fact]
    public void YtDlp_IsListedOnlyWhenOn_AndFfmpegAlways()
    {
        var on = ComponentCatalog.ExternalTools(@"C:\app", contestedFeatures: true).Select(c => c.Id).ToList();
        var off = ComponentCatalog.ExternalTools(@"C:\app", contestedFeatures: false).Select(c => c.Id).ToList();

        Assert.Contains("tool-yt-dlp", on);
        Assert.Contains("tool-ffmpeg", on);
        Assert.DoesNotContain("tool-yt-dlp", off);
        Assert.Contains("tool-ffmpeg", off);
    }

    [Fact]
    public void TheYoutubeEntry_IsTheOnlyContestedFeature_AndCoversItsMenuRowAndPaletteEntry()
    {
        var feature = Assert.Single(ContestedFeatures.All);

        Assert.Equal(ContestedFeatures.YoutubeImportId, feature.Id);
        Assert.Equal(["Menu_AttachYoutubeAudio"], feature.MenuKeys);
        Assert.Equal(["file.attachYoutubeAudio"], feature.PaletteCommandIds);
        Assert.Equal(["tool-yt-dlp"], feature.ComponentIds);
    }
}
