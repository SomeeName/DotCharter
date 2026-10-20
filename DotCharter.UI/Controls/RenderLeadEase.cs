namespace DotCharter.UI.Controls;

/// <summary>
///     How much of the visual lead (<see cref="VisualLeadTiming" />) a render clock
/// applies this frame — ONE rule for the 2D timeline and the 3D highway:
///     <list type="bullet">
///         <item>while PLAYING, the live lead (the caller scales it for speed);</item>
///         <item>on the playing→paused edge, the last applied lead eases OUT over
///         <see cref="DecayMs" /> wall-ms as <c>lead × (1 − t)²</c>, so a pause does
///         not snap the picture back;</item>
///         <item>once parked, exactly 0 — a parked playhead must be drawn at its own
///         time. Adding the live lead while paused redrew every idle frame at a
///         slightly different time, because the present pump moves the lead's EMA
///         after every frame: the 3D highway crept and flickered while paused.</item>
///     </list>
///     One instance per render clock, used only on that clock's thread; the tick is
///     passed in, so it is pure under test.
/// </summary>
public sealed class RenderLeadEase
{
    /// <summary>Wall-clock length of the pause ease-out.</summary>
    public const int DecayMs = 120;

    private double _lastAppliedLeadMs;
    private long _decayStartTick;
    private bool _decaying;
    private bool _wasPlaying;

    /// <summary>True while a pause ease-out is running: the render loop must keep
    /// drawing until it settles, or the last eased frame stays on screen.</summary>
    public bool IsEasing => _decaying;

    /// <summary>The lead (ms) to add to the render clock this frame.</summary>
    /// <param name="isPlaying">Whether the transport is playing now.</param>
    /// <param name="liveLeadMs">The lead playback would use now (already speed-scaled
    /// where the caller's clock needs it).</param>
    /// <param name="nowTickMs">Wall clock, ms (<c>Environment.TickCount64</c>).</param>
    public double Resolve(bool isPlaying, double liveLeadMs, long nowTickMs)
    {
        double applied;
        if (isPlaying)
        {
            _lastAppliedLeadMs = liveLeadMs;
            _decaying = false;
            applied = liveLeadMs;
        }
        else
        {
            if (_wasPlaying && _lastAppliedLeadMs > 0.01)
            {
                _decaying = true;
                _decayStartTick = nowTickMs;
            }

            applied = 0;
            if (_decaying)
            {
                var elapsed = nowTickMs - _decayStartTick;
                if (elapsed >= DecayMs)
                {
                    _decaying = false;
                    _lastAppliedLeadMs = 0;
                }
                else
                {
                    var t = elapsed / (double)DecayMs;
                    applied = _lastAppliedLeadMs * (1 - t) * (1 - t);
                }
            }
        }

        _wasPlaying = isPlaying;
        return applied;
    }
}
