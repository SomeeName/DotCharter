using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Wiring for the passive tuning-drift watchdog.
///     <para>
///     The evidence SOURCE is the interesting part. The CQT chart verifier
///     cannot provide it — its native result is {Hit, Confidence, Level}, with
///     no signed cent offset, and its bins are quarter-tones wide. So the
///     evidence comes from the managed tuner core (<see cref="TunerPitchEngine"/>,
///     which reports FrequencyHz + Clarity) and is measured against the NEAREST
///     equal-tempered semitone: an instrument that has gone x cents flat plays
///     every note x cents flat, and the harmonics of a flat note are flat by the
///     same amount, so the residual IS the drift regardless of which partial the
///     detector locked onto.
///     </para>
///     The watchdog's own decay/hysteresis/clamp behaviour is pinned by
///     <c>TuningDriftWatchdogTests</c>; this pins the cents math that feeds it
///     and that the two compose into the verdict the HUD shows.
/// </summary>
public sealed class TuningDriftWiringTests
{
    private static double Cents(double hz) => LiveTuningDriftMonitor.SignedCentsFromNearestSemitone(hz);

    [Fact]
    public void A_perfectly_tuned_note_reads_as_zero_cents()
    {
        Assert.Equal(0.0, Cents(440.0), 6);        // A4
        Assert.Equal(0.0, Cents(82.4069), 2);      // low E2
    }

    [Fact]
    public void A_flat_instrument_reads_negative_and_a_sharp_one_positive()
    {
        // 10 cents flat / sharp of A4.
        Assert.Equal(-10.0, Cents(440.0 * Math.Pow(2, -10.0 / 1200.0)), 4);
        Assert.Equal(10.0, Cents(440.0 * Math.Pow(2, 10.0 / 1200.0)), 4);
    }

    [Fact]
    public void The_offset_is_always_against_the_NEAREST_semitone_so_it_stays_in_a_quarter_tone()
    {
        // Any pitch at all folds into (-50, +50] — that is what makes the
        // watchdog's outlier clamp meaningful rather than a truncation.
        foreach (var hz in new[] { 61.7, 98.0, 123.47, 329.63, 700.0, 1234.5 })
            Assert.InRange(Cents(hz), -50.0, 50.0);
    }

    [Fact]
    public void The_same_drift_is_measured_on_a_harmonic_as_on_the_fundamental()
    {
        // The 2nd and 3rd harmonics of a note that is 12 cents flat are themselves
        // 12 cents flat — which is why a detector that locks onto a partial (or onto
        // a chord's root) still yields the instrument's true offset.
        var fundamental = 82.4069 * Math.Pow(2, -12.0 / 1200.0); // E2, 12 cents flat
        Assert.Equal(-12.0, Cents(fundamental), 3);
        Assert.Equal(-12.0, Cents(fundamental * 2), 3);
        Assert.Equal(-12.0, Cents(fundamental * 4), 3);
    }

    [Fact]
    public void Nonsense_frequencies_produce_no_evidence()
    {
        Assert.True(double.IsNaN(Cents(0)));
        Assert.True(double.IsNaN(Cents(-1)));

        // ...and the watchdog itself refuses NaN, so a bad reading can never move
        // the mean (this is the contract the tick path leans on).
        var watchdog = new TuningDriftWatchdog();
        Assert.Equal(TuningDriftVerdict.Insufficient, watchdog.AddEvidence(Cents(0), 0));
        Assert.Equal(0.0, watchdog.EvidenceWeight);
    }

    [Fact]
    public void Sustained_flat_playing_raises_the_flat_verdict()
    {
        // What the tick path does: one clarity-weighted piece of evidence per
        // 250 ms of pitched playing. The evidence floor (8.0) means ~10 readings.
        var watchdog = new TuningDriftWatchdog();
        var flatHz = 110.0 * Math.Pow(2, -15.0 / 1200.0); // A2, 15 cents flat
        var verdict = TuningDriftVerdict.Insufficient;

        for (var i = 0; i < 12; i++)
            verdict = watchdog.AddEvidence(Cents(flatHz), i * 250.0, weight: 0.95);

        Assert.Equal(TuningDriftVerdict.DriftFlat, verdict);
        Assert.InRange(watchdog.MeanCents, -16.0, -14.0);
    }

    [Fact]
    public void An_authored_cent_offset_is_not_drift()
    {
        // A chart deliberately tuned to A=445 (roughly +20 cents) would otherwise
        // read as "you're sharp" for its whole length. The tick path subtracts the
        // arrangement's CentOffset before feeding the watchdog — same arithmetic here.
        const double authoredOffset = 20.0;
        var watchdog = new TuningDriftWatchdog();
        var hz = 110.0 * Math.Pow(2, authoredOffset / 1200.0);

        for (var i = 0; i < 12; i++)
            watchdog.AddEvidence(Cents(hz) - authoredOffset, i * 250.0, weight: 0.95);

        Assert.Equal(TuningDriftVerdict.Stable, watchdog.Verdict);
    }
}
