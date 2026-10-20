using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Online;
using DotCharter.Services.Online.MusicBrainz;

namespace DotCharter.Services.Session;

/// <summary>What the New project dialog reads before the project exists: the picked audio's tags and cover
///     art, and the official Latin-script names for tags in another script.</summary>
public interface INewProjectSources
{
    /// <summary>Tags and a 512×512 cover PNG, honouring the configured ffmpeg; blocking, so call it off the UI thread.</summary>
    AudioMetadataResult ReadTags(string audioFilePath);

    /// <summary>
    ///     MusicBrainz's Latin-script names. Slow and rate-gated (a real lookup measured 20.5 s), so the artist —
    ///     the reliable half, and the first to arrive — is published through <paramref name="onArtist" /> on its own.
    ///     Callbacks arrive on a pool thread.
    /// </summary>
    Task<OnlineResult<LatinNameSuggestion>> FindLatinNamesAsync(
        string artist, string title, string album, Action<LatinNameSuggestion> onArtist);
}

/// <summary>The real sources: ffprobe/ffmpeg through <see cref="AudioMetadataExtractor" /> and a MusicBrainz client.</summary>
public sealed class NewProjectSources(IConfigurationService config, IAppLogger logger) : INewProjectSources
{
    public AudioMetadataResult ReadTags(string audioFilePath)
    {
        var ffmpeg = config.LoadAppConfig().FfmpegPath;
        return AudioMetadataExtractor.Extract(audioFilePath, null, string.IsNullOrWhiteSpace(ffmpeg) ? null : ffmpeg);
    }

    public async Task<OnlineResult<LatinNameSuggestion>> FindLatinNamesAsync(
        string artist, string title, string album, Action<LatinNameSuggestion> onArtist)
    {
        using var client = new MusicBrainzClient(logger: logger);
        return await client.FindLatinNamesAsync(artist, title, album, CancellationToken.None, onArtist)
            .ConfigureAwait(false);
    }
}
