namespace DotCharter.Tone.Audio.Engine;

internal sealed record MediaLoadRequest(string? Identity, long Generation);

/// <summary>The media portion of a rebuild. The caller's lock objects retain the service's lock order.</summary>
/// <remarks>
/// <c>keepStems</c> receives whether the requested song is still the snapshot's and
/// makes the final call on the stem replay: the caller knows of an explicit stem clear, and of a
/// borrowed take whose hand-back changed the identity but not the song that owns the stems.
/// </remarks>
internal static class MediaRebuildReplay
{
    public static void Replay<TSource, TStems>(object mediaGate, object playbackGate,
        MediaLoadRequest snapshot, Func<MediaLoadRequest> current, object engine, Func<object?> currentEngine,
        Func<TSource>? open, Action<TSource> publish, Func<bool> mayPublish, TStems? stems, Action<TStems> restoreStems,
        Func<bool, bool>? keepStems = null, object? operationGate = null)
        where TSource : class, IDisposable
        where TStems : class
    {
        operationGate ??= playbackGate;
        lock (mediaGate)
        {
            try
            {
                bool currentReload;
                lock (operationGate)
                    lock (playbackGate) currentReload = snapshot.Generation == current().Generation && mayPublish();
                if (currentReload && open is not null)
                {
                    var reopened = open();
                    var published = false;
                    try
                    {
                        lock (operationGate)
                            lock (playbackGate)
                            {
                                if (snapshot.Generation == current().Generation && ReferenceEquals(engine, currentEngine()) && mayPublish())
                                {
                                    publish(reopened);
                                    published = true;
                                }
                            }
                    }
                    finally
                    {
                        if (!published) reopened.Dispose();
                    }
                }
            }
            finally
            {
                var requested = current();
                var sameSong = string.Equals(snapshot.Identity, requested.Identity, StringComparison.OrdinalIgnoreCase)
                    && (snapshot.Identity is not null || snapshot.Generation == requested.Generation);
                var keep = keepStems?.Invoke(sameSong) ?? sameSong;
                if (keep && ReferenceEquals(engine, currentEngine()) && stems is not null) restoreStems(stems);
            }
        }
    }
}
