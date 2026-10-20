using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The separation in flight as a screen follows it (#710): a late watcher starts at
///     the latest progress, the run's cancel reaches the backend's token, and nothing a
///     screen does after the run ended can throw or resurrect it.
/// </summary>
public sealed class SongStemSeparationRunTests
{
    private const string Song = @"C:\Charts\Song\song.wav";

    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public void A_late_watcher_starts_at_the_latest_progress_and_hears_what_follows()
    {
        var run = new SongStemSeparationRun(Song, CancellationToken.None);
        run.Report(0.3);
        var late = new Recorder();

        using (run.Watch(late))
        {
            Assert.Equal(0.3, run.Progress);
            run.Report(0.5);
        }

        run.Report(0.7);
        Assert.Equal([0.5], late.Values);
        Assert.Equal(0.7, run.Progress);
    }

    [Fact]
    public void Cancel_and_the_callers_token_both_cancel_the_run_token()
    {
        var mine = new SongStemSeparationRun(Song, CancellationToken.None);
        mine.Cancel();
        Assert.True(mine.Token.IsCancellationRequested);

        using var caller = new CancellationTokenSource();
        var theirs = new SongStemSeparationRun(Song, caller.Token);
        caller.Cancel();
        Assert.True(theirs.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task An_ended_run_ignores_a_late_cancel_and_a_second_outcome_and_drops_its_watchers()
    {
        var run = new SongStemSeparationRun(Song, CancellationToken.None);
        var watcher = new Recorder();
        run.Watch(watcher);

        run.Complete(new SongStemSeparationOutcome(SongStemSeparationStatus.Separated));
        run.Complete(new SongStemSeparationOutcome(SongStemSeparationStatus.Failed, "late"));
        run.Cancel();
        run.Report(0.9);

        Assert.Equal(SongStemSeparationStatus.Separated, (await run.Completion).Status);
        Assert.False(run.Token.IsCancellationRequested);
        Assert.Empty(watcher.Values);
    }

    [Theory]
    [InlineData(@"C:\Charts\Song\song.wav", true)]
    [InlineData(@"c:\charts\SONG\Song.WAV", true)]
    [InlineData(@"C:\Charts\Song\..\Song\song.wav", true)]
    [InlineData(@"C:\Charts\Other\song.wav", false)]
    [InlineData("", false)]
    public void Separates_compares_full_paths_the_way_Windows_does(string path, bool expected) =>
        Assert.Equal(expected, new SongStemSeparationRun(Song, CancellationToken.None).Separates(path));
}
