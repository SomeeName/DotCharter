using DotCharter.Services.Audio.Key;

namespace DotCharter.Tests.Infrastructure;

/// <summary>A song-key audio estimator that hears nothing: every file answers "no key".</summary>
public sealed class FakeSongKeyAudioEstimator : ISongKeyAudioEstimator
{
    public Task<SongKeyEstimate?> EstimateAsync(string audioPath, CancellationToken ct) => Task.FromResult<SongKeyEstimate?>(null);
}
