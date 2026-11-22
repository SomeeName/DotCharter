using DotCharter.Services.Import.Audio;
using DotCharter.Services.Online.MusicBrainz;
using DotCharter.Services.Session;
using DotCharter.Tone.Online;

namespace DotCharter.Tests.Infrastructure;

/// <summary>New project sources that read no tags and find no names, for view models that only need the seam.</summary>
internal sealed class InertNewProjectSources : INewProjectSources
{
    public AudioMetadataResult ReadTags(string audioFilePath) => AudioMetadataResult.Empty;

    public Task<OnlineResult<LatinNameSuggestion>> FindLatinNamesAsync(string artist, string title, string album,
        Action<LatinNameSuggestion> onArtist) =>
        Task.FromResult(OnlineResult<LatinNameSuggestion>.Fail(OnlineError.NotConfigured, "offline in tests"));
}
