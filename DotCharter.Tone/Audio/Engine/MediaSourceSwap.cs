using JuceSharp;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>Serializes native decode and publishes only a request that still owns the transport.</summary>
internal static class MediaSourceSwap
{
    public static async Task<bool> Queue(Func<bool> worker, CancellationToken cancellation, object gate,
        Func<MediaSourceOwnership.Event, MediaLoadRequest, MediaLoadRequest?, MediaSourceOwnership.Action> decide,
        MediaLoadRequest request, MediaLoadRequest? previous, object? operationGate = null)
    {
        operationGate ??= gate;
        using var registration = cancellation.Register(() =>
        {
            lock (operationGate)
                lock (gate) decide(MediaSourceOwnership.Event.DecodeFail, request, previous);
        });
        try { return await Task.Run(worker, cancellation).WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            lock (operationGate)
                lock (gate) decide(MediaSourceOwnership.Event.DecodeFail, request, previous);
            throw;
        }
    }

    internal sealed class Context
    {
        public required object MediaGate { get; init; }
        public required object OperationGate { get; init; }
        public required object Gate { get; init; }
        public required Func<JuceAudioEngine?> Engine { get; init; }
        public required Func<MediaSourceOwnership.Event, MediaLoadRequest, MediaLoadRequest?, MediaSourceOwnership.Action> Decide { get; init; }
        public required Func<JuceAudioSource?> Source { get; init; }
        public required Func<bool> MayLoad { get; init; }
        public required Action Detach { get; init; }
        public required Func<JuceAudioEngine, JuceAudioSource> Open { get; init; }
        public required Action<JuceAudioEngine, JuceAudioSource, bool> Publish { get; init; }
        public required Action<Exception> Failed { get; init; }
        public Action<JuceAudioSource, bool>? Activate { get; init; }
    }

    public static bool Run(Context context, MediaLoadRequest request, MediaLoadRequest? previousRequest, CancellationToken cancellation)
    {
        lock (context.MediaGate)
        {
            ThrowIfCancelled(context, request, previousRequest, cancellation);
            var preserve = previousRequest is not null;
            JuceAudioEngine? engine;
            JuceAudioSource? previous;
            lock (context.OperationGate)
                lock (context.Gate)
                {
                    if (!context.MayLoad()) return false;
                    engine = context.Engine();
                    if (engine is null) return false;
                    var action = context.Decide(MediaSourceOwnership.Event.WorkerStarts, request, previousRequest);
                    if (action == MediaSourceOwnership.Action.None) return false;
                    preserve = action == MediaSourceOwnership.Action.DecodePreserving;
                    previous = context.Source();
                    if (!preserve) context.Detach();
                }
            if (!preserve) Dispose(previous);

            JuceAudioSource opened;
            try { opened = context.Open(engine); }
            catch (Exception ex)
            {
                lock (context.OperationGate)
                    lock (context.Gate) context.Failed(ex);
                throw;
            }

            var published = false;
            try
            {
                lock (context.OperationGate)
                {
                    lock (context.Gate)
                    {
                        ThrowIfCancelled(context, request, previousRequest, cancellation);
                        if (!context.MayLoad() || !ReferenceEquals(context.Engine(), engine) ||
                            context.Decide(MediaSourceOwnership.Event.DecodeOk, request, previousRequest) != MediaSourceOwnership.Action.ReadyToSwap)
                        {
                            return false;
                        }
                        if (preserve) previous = context.Source();
                        context.Decide(MediaSourceOwnership.Event.Swap, request, previousRequest);
                        context.Publish(engine, opened, preserve);
                        published = true;
                    }
                    context.Activate?.Invoke(opened, preserve);
                }
            }
            finally
            {
                if (!published) Dispose(opened);
            }
            if (preserve) Dispose(previous);
            return true;
        }
    }

    private static void ThrowIfCancelled(Context context, MediaLoadRequest request, MediaLoadRequest? previous,
        CancellationToken cancellation)
    {
        if (!cancellation.IsCancellationRequested) return;
        lock (context.OperationGate)
            lock (context.Gate) context.Decide(MediaSourceOwnership.Event.DecodeFail, request, previous);
        cancellation.ThrowIfCancellationRequested();
    }

    public static bool Resume(JuceAudioSource opened, bool playing, bool preserve, IEnumerable<JuceAudioSource?> stems)
    {
        if (!playing || preserve) return false;
        try { opened.Seek(TimeSpan.Zero); } catch { /* ignored */ }
        try { opened.Play(); } catch { /* ignored */ }
        foreach (var stem in stems)
        {
            try { stem?.Seek(TimeSpan.Zero); } catch { /* ignored */ }
            try { stem?.Play(); } catch { /* ignored */ }
        }
        return true;
    }

    public static double Length(JuceAudioSource? src, ref double lastKnownLengthMs)
    {
        if (src == null) return lastKnownLengthMs;
        try
        {
            var len = src.Length.TotalMilliseconds;
            if (len > 0) lastKnownLengthMs = len;
            return len;
        }
        catch
        {
            return lastKnownLengthMs;
        }
    }

    private static void Dispose(JuceAudioSource? source)
    {
        try { source?.Dispose(); } catch { /* ignored */ }
    }
}
