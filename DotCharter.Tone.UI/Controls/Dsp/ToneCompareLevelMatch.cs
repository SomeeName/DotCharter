using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
///     How loud each A/B slot plays, learnt from the rig's own output meter while
/// the slot is live, and the trim that matches the incoming slot to A.
///     <para>
///         <b>Measured, not guessed, once both slots have been heard.</b> The level
///         is the RMS leaving the last unit (<c>IDspMeterSource</c>), smoothed in
///         the power domain and gated, so silence between phrases does not drag a
///         slot's level down. A slot not heard yet falls back to the static walker
///         (<see cref="CompareLevelTrim.EstimateDb" />), for both sides at once —
///         one measured side against one estimated side compares two different
///         scales.
///     </para>
///     <para>
///         <b>Raw levels.</b> A reading is taken with whatever trim is on the chain,
///         so the trim is subtracted before it is stored: a slot's level is what it
///         plays UNTRIMMED, and the next match is computed from that.
///     </para>
///     Pure: no clock, no engine. The rig feeds it readings and asks for trims.
/// </summary>
public sealed class ToneCompareLevelMatch
{
    /// <summary>Readings quieter than this are silence, not the slot's level.</summary>
    public const double GateDb = -55;

    /// <summary>Gated readings a slot needs before its level counts as heard
    /// (half a second of playing at the rig's 50 ms meter clock).</summary>
    public const int MinReadings = 10;

    /// <summary>Per-reading smoothing in the power domain (about a one-second
    /// memory at 50 ms), so an edit to the slot shows up within a phrase.</summary>
    public const double Smoothing = 0.05;

    private readonly SlotLevel _a = new();
    private readonly SlotLevel _b = new();

    /// <summary>Feeds one output-meter reading of <paramref name="slot" />, taken
    /// with <paramref name="appliedTrimDb" /> on the chain.</summary>
    public void Record(ToneCompareSlot slot, double measuredDb, double appliedTrimDb)
    {
        if (!double.IsFinite(measuredDb) || measuredDb < GateDb || !double.IsFinite(appliedTrimDb)) return;
        var level = For(slot);
        var power = Math.Pow(10, (measuredDb - appliedTrimDb) / 10);
        level.Power = level.Readings == 0 ? power : level.Power + Smoothing * (power - level.Power);
        level.Readings++;
    }

    /// <summary>The slot's untrimmed level in dB, or null until it has been heard.</summary>
    public double? MeasuredDb(ToneCompareSlot slot)
    {
        var level = For(slot);
        return level.Readings >= MinReadings ? 10 * Math.Log10(level.Power) : null;
    }

    /// <summary>
    ///     The trim to put on the chain when <paramref name="incoming" /> becomes
    ///     live. Slot A is the reference and always plays as stored (0); slot B is
    ///     matched to A, by measurement when both have been heard and by the static
    ///     estimate of the two stored rigs otherwise.
    /// </summary>
    public double TrimFor(ToneCompareSlot incoming, DcRigPreset? a, DcRigPreset? b)
    {
        if (incoming == ToneCompareSlot.A) return 0;
        if (MeasuredDb(ToneCompareSlot.A) is { } heardA && MeasuredDb(ToneCompareSlot.B) is { } heardB)
            return CompareLevelTrim.TrimDb(heardA, heardB);
        return CompareLevelTrim.TrimDb(CompareLevelTrim.EstimateDb(a), CompareLevelTrim.EstimateDb(b));
    }

    /// <summary>Forgets one slot's level — its contents were replaced.</summary>
    public void Forget(ToneCompareSlot slot)
    {
        var level = For(slot);
        level.Power = 0;
        level.Readings = 0;
    }

    /// <summary>Forgets both (a seed, a reset, a new tone).</summary>
    public void Reset()
    {
        Forget(ToneCompareSlot.A);
        Forget(ToneCompareSlot.B);
    }

    private SlotLevel For(ToneCompareSlot slot) => slot == ToneCompareSlot.A ? _a : _b;

    private sealed class SlotLevel
    {
        public double Power;
        public int Readings;
    }
}
