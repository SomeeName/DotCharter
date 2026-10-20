using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The analysis monitor's per-string detection confidence (#314): the certainty of one verdict,
///     the per-string aggregate over an injected chart-time clock, and the matcher carrying the
///     verifier's sub-threshold evidence on a miss WITHOUT changing any verdict.
/// </summary>
public sealed class LiveStringConfidenceTests
{
    private static ExpectedSound Sound(int stringIndex = 0, int mask = 0, double timeMs = 1000) =>
        new() { TimeMs = timeMs, MidiNotes = [40], StringIndex = stringIndex, StringMask = mask };

    private static LivePlayEvent Hit(float confidence, ExpectedSound? sound = null) =>
        new(LivePlayVerdict.Hit, sound ?? Sound(), 1000, confidence);

    private static LivePlayEvent Miss(float? evidence, ExpectedSound? sound = null) =>
        new(LivePlayVerdict.Miss, sound ?? Sound(), 1250) { MissEvidence = evidence };

    // ── one verdict ─────────────────────────────────────────────────────────

    [Fact]
    public void AHit_IsAsCertainAsItsEvidence()
    {
        Assert.Equal(0.8f, LiveDetectionCertainty.Of(Hit(0.8f))!.Value, 3);
        Assert.Equal(0.2f, LiveDetectionCertainty.Of(new LivePlayEvent(LivePlayVerdict.HitWrongTechnique, Sound(), 1000, 0.2f))!.Value, 3);
    }

    [Fact]
    public void AMiss_IsCertainInProportionToHowLittleTheVerifierFound()
    {
        Assert.Equal(1f, LiveDetectionCertainty.Of(Miss(0f))!.Value, 3);
        Assert.Equal(0.3f, LiveDetectionCertainty.Of(Miss(0.7f))!.Value, 3);
    }

    [Fact]
    public void AMissNoVerifierLookedAt_HasNoCertainty_NotAConfidentOne()
    {
        Assert.Null(LiveDetectionCertainty.Of(Miss(null)));
        Assert.False(LiveDetectionCertainty.IsUnsureMiss(Miss(null)));
    }

    [Fact]
    public void OutOfRangeEvidence_IsClamped()
    {
        Assert.Equal(1f, LiveDetectionCertainty.Of(Hit(1.7f))!.Value, 3);
        Assert.Equal(0f, LiveDetectionCertainty.Of(Hit(float.NaN))!.Value, 3);
        Assert.Equal(1f, LiveDetectionCertainty.Of(Miss(-0.5f))!.Value, 3);
    }

    [Theory]
    [InlineData(0.0f, false)]
    [InlineData(0.49f, false)]
    [InlineData(0.51f, true)]
    [InlineData(0.9f, true)]
    public void AMiss_IsUnsure_OnlyWhenTheEvidenceOutweighsItsAbsence(float evidence, bool unsure) =>
        Assert.Equal(unsure, LiveDetectionCertainty.IsUnsureMiss(Miss(evidence)));

    [Fact]
    public void AHit_IsNeverAnUnsureMiss() => Assert.False(LiveDetectionCertainty.IsUnsureMiss(Hit(0.1f)));

    // ── the per-string aggregate ────────────────────────────────────────────

    [Fact]
    public void AStringWithNothingJudged_HasNoData()
    {
        var readings = new LiveStringConfidenceTracker().Sample(nowMs: 5000);

        Assert.Equal(LiveStringConfidenceTracker.MaxStrings, readings.Length);
        Assert.All(readings, r => Assert.False(r.HasData));
        Assert.All(readings, r => Assert.False(r.IsUnsure));
    }

    [Fact]
    public void EachString_AveragesItsOwnJudgments()
    {
        var tracker = new LiveStringConfidenceTracker();
        tracker.Record(Hit(0.9f, Sound(stringIndex: 0, mask: 1 << 0, timeMs: 1000)));
        tracker.Record(Hit(0.7f, Sound(stringIndex: 0, mask: 1 << 0, timeMs: 1500)));
        tracker.Record(Miss(0.8f, Sound(stringIndex: 5, mask: 1 << 5, timeMs: 1500)));

        var readings = tracker.Sample(nowMs: 2000);

        Assert.Equal(2, readings[0].Samples);
        Assert.Equal(0.8f, readings[0].Certainty, 3);
        Assert.False(readings[0].IsUnsure);
        Assert.Equal(1, readings[5].Samples);
        Assert.Equal(0.2f, readings[5].Certainty, 3);
        Assert.True(readings[5].IsUnsure);
        Assert.False(readings[3].HasData);
    }

    [Fact]
    public void AChord_CountsTowardEveryStringItSounds()
    {
        var tracker = new LiveStringConfidenceTracker();
        tracker.Record(Hit(0.6f, Sound(stringIndex: 1, mask: 0b0001_1110, timeMs: 1000)));

        var readings = tracker.Sample(nowMs: 1200);

        Assert.Equal([false, true, true, true, true, false], readings.Take(6).Select(r => r.HasData));
        Assert.All(readings.Skip(1).Take(4), r => Assert.Equal(0.6f, r.Certainty, 3));
    }

    [Fact]
    public void ASoundWithoutAMask_FallsBackToItsString()
    {
        var tracker = new LiveStringConfidenceTracker();
        tracker.Record(Hit(0.4f, Sound(stringIndex: 2, mask: 0)));

        Assert.True(tracker.Sample(nowMs: 1000)[2].HasData);
    }

    [Fact]
    public void AMissWithNoCertainty_IsNotRecorded()
    {
        var tracker = new LiveStringConfidenceTracker();
        tracker.Record(Miss(null, Sound(stringIndex: 0, mask: 1)));

        Assert.False(tracker.Sample(nowMs: 1000)[0].HasData);
    }

    [Fact]
    public void AJudgmentOlderThanTheWindow_StopsCounting()
    {
        var tracker = new LiveStringConfidenceTracker();
        tracker.Record(1, timeMs: 1000, certainty: 0.2f);
        tracker.Record(1, timeMs: 9000, certainty: 1.0f);

        Assert.Equal(0.6f, tracker.Sample(nowMs: 1000 + LiveStringConfidenceTracker.WindowMs)[0].Certainty, 3);
        var later = tracker.Sample(nowMs: 1001 + LiveStringConfidenceTracker.WindowMs)[0];
        Assert.Equal(1, later.Samples);
        Assert.Equal(1.0f, later.Certainty, 3);
    }

    [Fact]
    public void AfterASeekBack_JudgmentsFromAheadOfThePlayheadAreDropped()
    {
        var tracker = new LiveStringConfidenceTracker();
        tracker.Record(1, timeMs: 30_000, certainty: 0.1f);

        // A miss is judged when its window closes, so "now" trails its chart time a little…
        Assert.True(tracker.Sample(nowMs: 30_000 - LiveStringConfidenceTracker.FutureToleranceMs)[0].HasData);
        // …but a seek back to the start leaves it in the future, and it no longer describes this pass.
        Assert.False(tracker.Sample(nowMs: 2_000)[0].HasData);
    }

    [Fact]
    public void OnlyTheNewestSamples_Count()
    {
        var tracker = new LiveStringConfidenceTracker();
        tracker.Record(1, timeMs: 100, certainty: 0f);
        for (var i = 0; i < LiveStringConfidenceTracker.SamplesPerString; i++)
            tracker.Record(1, timeMs: 200 + i, certainty: 1f);

        var reading = tracker.Sample(nowMs: 1000)[0];
        Assert.Equal(LiveStringConfidenceTracker.SamplesPerString, reading.Samples);
        Assert.Equal(1f, reading.Certainty, 3);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var tracker = new LiveStringConfidenceTracker();
        tracker.Record(0b11, timeMs: 1000, certainty: 0.5f);
        tracker.Clear();

        Assert.All(tracker.Sample(nowMs: 1000), r => Assert.False(r.HasData));
    }

    // ── the matcher reports, and decides nothing new ────────────────────────

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static Arrangement OneNote(int stringIndex = 0, int fret = 3)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), Fret = fret, StringIndex = stringIndex });
        return arr;
    }

    private static LiveNoteMatcher Loaded(Arrangement arr, Func<ExpectedSound, ChartVerifyOutcome>? verify)
    {
        var matcher = new LiveNoteMatcher { ChartVerify = verify };
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    [Fact]
    public void ACqtMiss_CarriesWhatTheVerifierFound_AndIsStillAMiss()
    {
        var calls = 0;
        var matcher = Loaded(OneNote(), _ =>
        {
            calls++;
            return new ChartVerifyOutcome(false, 0.62f);
        });

        var events = matcher.AdvanceTo(playheadMs: 1500);

        var miss = Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Miss, miss.Verdict);
        Assert.Equal(0f, miss.Confidence);
        Assert.Equal(0.62f, miss.MissEvidence!.Value, 3);
        Assert.Equal(1, matcher.Stats.Misses);
        Assert.Equal(1, calls);
        Assert.True(LiveDetectionCertainty.IsUnsureMiss(miss));
    }

    [Fact]
    public void ACqtHit_IsUnchanged_AndCarriesNoMissEvidence()
    {
        var matcher = Loaded(OneNote(), _ => new ChartVerifyOutcome(true, 0.8f));

        var hit = Assert.Single(matcher.AdvanceTo(playheadMs: 1500));

        Assert.Equal(LivePlayVerdict.Hit, hit.Verdict);
        Assert.Equal(0.8f, hit.Confidence, 3);
        Assert.Null(hit.MissEvidence);
        Assert.Equal(1, matcher.Stats.Hits);
    }

    [Fact]
    public void AClassicMiss_HasNoEvidence_BecauseNoVerifierLooked()
    {
        var miss = Assert.Single(Loaded(OneNote(), verify: null).AdvanceTo(playheadMs: 1500));

        Assert.Equal(LivePlayVerdict.Miss, miss.Verdict);
        Assert.Null(miss.MissEvidence);
        Assert.Null(LiveDetectionCertainty.Of(miss));
    }

    [Fact]
    public void APitchlessSound_IsNeverVerified_SoItsMissHasNoEvidence()
    {
        var arr = OneNote();
        arr.Levels[0].Notes[0].Mute = MuteType.Full;
        var verified = false;
        var matcher = Loaded(arr, _ =>
        {
            verified = true;
            return new ChartVerifyOutcome(false, 0.9f);
        });

        var miss = Assert.Single(matcher.AdvanceTo(playheadMs: 1500));

        Assert.False(verified);
        Assert.Null(miss.MissEvidence);
    }

    [Fact]
    public void ExpectedSounds_NameTheStringsTheirPitchedTonesAreOn()
    {
        var arr = OneNote(stringIndex: 4);
        var chord = new Chord { Position = new FractionalPosition(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5, Mute = MuteType.Full });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 4 });
        arr.Levels[0].Chords.Add(chord);

        var matcher = Loaded(arr, verify: null);

        var note = matcher.Expected.Single(s => !s.IsChord);
        var strum = matcher.Expected.Single(s => s.IsChord);
        Assert.Equal(1 << 4, note.StringMask);
        Assert.Equal(0b1011, strum.StringMask); // the fully muted string 2 carries no pitched tone
    }
}
