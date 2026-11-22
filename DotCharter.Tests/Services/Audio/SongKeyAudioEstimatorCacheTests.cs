using DotCharter.Services.Audio.Key;

namespace DotCharter.Tests.Services.Audio;

/// <summary>#822: the audio key is read once per unchanged file for the session, off the calling thread.</summary>
public sealed class SongKeyAudioEstimatorCacheTests : IDisposable
{
    private static readonly SongKeyEstimate Answer = new(new ChordKeySignature(2, false), new ChordKeySignature(2, true), 0.6, 0.4);

    private readonly string _dir = Directory.CreateTempSubdirectory("dc-key-cache").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task ASecondRequestForTheSameUnchangedFile_DoesNotRecompute()
    {
        var calls = 0;
        var estimator = new SongKeyAudioEstimator((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Answer;
        });
        var path = Write("a.wav", "one");

        var first = await estimator.EstimateAsync(path, CancellationToken.None);
        var second = await estimator.EstimateAsync(path, CancellationToken.None);

        Assert.Equal(Answer, first);
        Assert.Equal(Answer, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AChangedFile_IsComputedAgain()
    {
        var calls = 0;
        var estimator = new SongKeyAudioEstimator((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Answer;
        });
        var path = Write("b.wav", "one");
        await estimator.EstimateAsync(path, CancellationToken.None);

        File.WriteAllText(path, "longer content");
        await estimator.EstimateAsync(path, CancellationToken.None);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
        await estimator.EstimateAsync(path, CancellationToken.None);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task TheComputeRunsOffTheCallingThread_AndAMissingFileIsNull()
    {
        var caller = Environment.CurrentManagedThreadId;
        var worker = -1;
        using var started = new ManualResetEventSlim();
        var estimator = new SongKeyAudioEstimator((_, _) =>
        {
            worker = Environment.CurrentManagedThreadId;
            started.Set();
            return Answer;
        });

        // The test itself may run on a pool thread, which would pick its own queued work up inline once it
        // awaits. Blocking the caller until the compute signals leaves any OTHER thread to run it.
        var pending = estimator.EstimateAsync(Write("c.wav", "x"), CancellationToken.None);
        started.Wait(TestContext.Current.CancellationToken);
        await pending;
        var missing = await estimator.EstimateAsync(Path.Combine(_dir, "none.wav"), CancellationToken.None);

        Assert.NotEqual(caller, worker);
        Assert.Null(missing);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }
}
