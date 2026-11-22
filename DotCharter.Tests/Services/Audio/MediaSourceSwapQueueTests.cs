using DotCharter.Tone.Audio.Engine;
using Ownership = DotCharter.Tone.Audio.Engine.MediaSourceOwnership;

namespace DotCharter.Tests.Services.Audio;

/// <summary>Queue cancellation settles media authority before reporting cancellation to callers.</summary>
public sealed class MediaSourceSwapQueueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeTheWorkerFinishes_SettlesOnlyItsCandidate(bool newerSong)
    {
        var first = new MediaLoadRequest("take-1.wav", 1);
        var candidate = new MediaLoadRequest("take-2.wav", 2);
        var song = new MediaLoadRequest("song-c.wav", 3);
        var state = new Ownership.State(first, first, candidate);
        var gate = new object();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var queued = MediaSourceSwap.Queue(() =>
        {
            entered.SetResult();
            try
            {
                release.Wait();
                return false;
            }
            finally { exited.SetResult(); }
        }, cancellation.Token, gate, (@event, request, previous) =>
        {
            var decision = Ownership.Decide(state, @event, request, previous);
            state = decision.Next;
            return decision.Action;
        }, candidate, first);

        try
        {
            await entered.Task;
            if (newerSong)
            {
                lock (gate) state = Ownership.Decide(state, Ownership.Event.ExplicitLoadQueued, song).Next;
            }
            var settled = await Task.Run(() =>
            {
                cancellation.Cancel();
                lock (gate) return state;
            }, TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Null(settled.TemporaryOwner);
            Assert.Same(newerSong ? song : first, settled.Requested);
            Assert.Same(first, settled.Loaded);
        }
        finally
        {
            release.Set();
            await exited.Task;
        }
    }
}
