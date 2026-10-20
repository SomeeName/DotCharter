namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     The last playhead <c>JuceAudioPlaybackService</c> reported, and the monotonic floor its
/// per-frame <c>CurrentTimeMs</c> read holds during playback. Pure; every value is
///     stamped with the <see cref="PlayheadInterpolator.Epoch" /> it was computed in.
///     <para>
///         <b>The race this ends.</b> The floor is not one plain field: a render read that
///         extrapolated from the PRE-seek sample, then wrote the field after <c>SeekTo</c> had
///         set it to the target, would put the old position back — and floor every read after it to
///         the old position until the next 250 Hz tick overwrote it: up to 4 ms of the playhead
///         stuck where the seek had just left. A value from an older epoch cannot
///         overwrite a newer one, and is never floored against it.
///     </para>
///     <para>
///         A short lock, never taken on an audio thread: the writers are the position tick (a
///         pool timer), the transport commands (UI thread) and the readers of
///         <c>CurrentTimeMs</c> (UI and render threads).
///     </para>
/// </summary>
public sealed class PlayheadFloor
{
    private readonly object _gate = new();
    private double _ms;
    private long _epoch;

    /// <summary>The last value published; what a reader gets when there is nothing newer.</summary>
    public double Ms => Volatile.Read(ref _ms);

    /// <summary>An authoritative position — a seek's target, zero on stop or load. Call it
    /// AFTER <see cref="PlayheadInterpolator.Reset" />, with the epoch that reset produced.</summary>
    public void Reset(double ms, long epoch)
    {
        lock (_gate)
        {
            Volatile.Write(ref _ms, ms);
            if (epoch > _epoch) _epoch = epoch;
        }
    }

    /// <summary>
    ///     A reading that is not floored — the position tick's, or the raw native position
    ///     while the interpolator is not running. Returns what to report: the reading, or
    ///     the newer value when the reading predates a reset.
    /// </summary>
    public double Publish(double ms, long epoch)
    {
        lock (_gate)
        {
            if (epoch < _epoch) return _ms;
            _epoch = epoch;
            Volatile.Write(ref _ms, ms);
            return ms;
        }
    }

    /// <summary>
    ///     An extrapolated render read: never behind the last value of the SAME epoch
    ///     (latency compensation can shift mid-playback — a mirror device opening).
    ///     A reading from an older epoch straddled a seek: it reports the seek's value
    ///     and publishes nothing.
    /// </summary>
    public double Clamp(double ms, long epoch)
    {
        lock (_gate)
        {
            if (epoch < _epoch) return _ms;
            if (epoch == _epoch && ms < _ms) ms = _ms;
            _epoch = epoch;
            Volatile.Write(ref _ms, ms);
            return ms;
        }
    }
}
