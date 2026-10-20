using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Audio.Calibration;

/// <summary>
///     The loopback calibration's CHANNEL search: one
///     <see cref="LatencyCalibrationSession" /> per open input channel, tried in
///     order, so a click that never comes back on the configured channel is
///     looked for on the others before the run is called a failure.
///     <para>
///         <b>Why.</b> The guitar sat on the
///         interface's second input with both channels open. Seat 0's capture
///         ring — the buffer the calibration analyses — reads open channel 0, so
///         the wizard reported "the input never heard the click" while the meter
///         lit on every click; looping the output into channel 0 made it "work".
///         The fix has two halves: the wizard listens on the CONFIGURED guitar
///         channel by default, and when nothing comes back there it moves on to
///         the next open channel and reports which one it heard the click on.
///     </para>
///     <para>
///         Pure and engine-free, like the session it wraps: the caller re-routes
///         the capture ring when <see cref="Advance" /> reports a channel change
///         and feeds onsets back in. Deterministic under a synthetic clock.
///     </para>
/// </summary>
public sealed class LoopbackChannelSearch
{
    private readonly IReadOnlyList<int> _order;
    private readonly Func<LatencyCalibrationSession> _sessionFactory;
    private readonly List<int> _tried = [];
    private int _index;

    /// <param name="channelOrder">Open channel indexes to try, preferred first —
    /// <see cref="DotCharter.Tone.Audio.Abstractions.GuitarInputChannels.CalibrationChannelOrder" />.</param>
    /// <param name="sessionFactory">Builds the per-channel session; the default
    /// tolerates two missed clicks per channel so a wrong channel costs a few
    /// seconds rather than the session's full patience.</param>
    public LoopbackChannelSearch(IReadOnlyList<int> channelOrder, Func<LatencyCalibrationSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(channelOrder);
        if (channelOrder.Count == 0) throw new ArgumentException("At least one channel is required.", nameof(channelOrder));
        _order = channelOrder;
        _sessionFactory = sessionFactory ?? (static () => new LatencyCalibrationSession { MaxMissedEchoes = 2 });
        Session = _sessionFactory();
    }

    /// <summary>The channel the search started on — the configured one.</summary>
    public int PreferredChannel => _order[0];

    /// <summary>The channel currently being listened on.</summary>
    public int CurrentChannel => _order[_index];

    /// <summary>Channels that were tried and heard nothing, in order.</summary>
    public IReadOnlyList<int> ChannelsGivenUpOn => _tried;

    /// <summary>The session listening on <see cref="CurrentChannel" />.</summary>
    public LatencyCalibrationSession Session { get; private set; }

    /// <summary>True once the LAST channel failed too.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>The search's state: the current session's, except that a failed
    /// session on a non-final channel is a channel switch, not a failure.</summary>
    public LatencyCalibrationState State =>
        Exhausted ? LatencyCalibrationState.Failed : Session.State;

    /// <summary>The channel the click came back on; null until complete.</summary>
    public int? HeardOnChannel => State == LatencyCalibrationState.Complete ? CurrentChannel : null;

    /// <summary>True when the click was heard on a channel OTHER than the
    /// configured one — the case the result text must call out, because it
    /// means live judging is listening to the wrong jack too.</summary>
    public bool FellBack => HeardOnChannel is { } heard && heard != PreferredChannel;

    /// <summary>Median round trip on the channel that heard it; null until complete.</summary>
    public double? ResultMs => Session.ResultMs;

    public void Start(long nowTicks)
    {
        _index = 0;
        _tried.Clear();
        Exhausted = false;
        Session = _sessionFactory();
        Session.Start(nowTicks);
    }

    public bool ShouldEmitClick(long nowTicks) => !Exhausted && Session.ShouldEmitClick(nowTicks);

    public void NotifyClickEmitted(long nowTicks) => Session.NotifyClickEmitted(nowTicks);

    public bool OfferOnset(long onsetHostTicks) => !Exhausted && Session.OfferOnset(onsetHostTicks);

    /// <summary>
    ///     Drives timeouts. Returns true when the listening channel CHANGED —
    ///     the caller must re-route the capture ring to <see cref="CurrentChannel" />
    ///     and drain whatever the old channel left in it before feeding more onsets.
    /// </summary>
    public bool Advance(long nowTicks)
    {
        if (Exhausted) return false;
        Session.Advance(nowTicks);
        if (Session.State != LatencyCalibrationState.Failed) return false;

        _tried.Add(CurrentChannel);
        if (_index + 1 >= _order.Count)
        {
            Exhausted = true;
            return false;
        }

        _index++;
        Session = _sessionFactory();
        Session.Start(nowTicks);
        return true;
    }
}
