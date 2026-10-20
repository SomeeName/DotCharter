namespace DotCharter.Services.Editing.Refret;

/// <summary>
///     The refret engine: re-assigns string/fret positions for the SAME sounding
///     pitches to minimize hand movement, stretch and string crossing. Pitch
///     preservation is the invariant that can never break — every candidate is
///     derived from the original note's sounding MIDI pitch under the
///     arrangement's tuning and capo.
///     Mutates the given arrangement in place; callers snapshot for undo
///     (see ChartMaintenanceService's ProjectSnapshotAction pattern).
/// </summary>
public interface IRefretService
{
    /// <summary>Refrets every difficulty level of the arrangement.</summary>
    RefretResult RefretArrangement(Arrangement arrangement, IList<BeatMarker> beats, RefretOptions? options = null);

    /// <summary>
    ///     Refrets one level within [rangeStart, rangeEnd]. Sounds outside the
    ///     range stay untouched and act as fixed DP boundary anchors.
    /// </summary>
    RefretResult RefretRange(
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        FractionalPosition rangeStart,
        FractionalPosition rangeEnd,
        RefretOptions? options = null);
}
