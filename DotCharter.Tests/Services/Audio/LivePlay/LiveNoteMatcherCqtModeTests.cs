using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>Detection v8: the CQT chart-verifier (pull-based) judging mode.</summary>
public sealed class LiveNoteMatcherCqtModeTests
{
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
        => [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * (60000.0 / bpm), Bpm = bpm })];

    private static Arrangement OneNoteAt(int beat, int fret)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), Fret = fret, StringIndex = 0 });
        return arr;
    }

    private static LiveNoteMatcher Loaded(Arrangement arr, Func<ExpectedSound, ChartVerifyOutcome> verify)
    {
        var matcher = new LiveNoteMatcher { ChartVerify = verify };
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    // CQT mode with the early check wired, as the app runs it: the same delegate answers both.
    private static LiveNoteMatcher LoadedWithEarly(Arrangement arr, Func<ExpectedSound, ChartVerifyOutcome> verify)
    {
        var matcher = new LiveNoteMatcher { ChartVerify = verify, ChartVerifyEarly = verify };
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    [Fact]
    public void ChartVerifyMode_IsOnWhenDelegateSet()
    {
        Assert.True(new LiveNoteMatcher { ChartVerify = _ => new ChartVerifyOutcome(true, 1f) }.ChartVerifyMode);
        Assert.False(new LiveNoteMatcher().ChartVerifyMode);
    }

    [Fact]
    public void PitchedSound_ConfirmedByVerifier_OnWindowClose_IsHit()
    {
        var arr = OneNoteAt(beat: 2, fret: 3);           // MIDI 43 at 1000 ms
        var matcher = Loaded(arr, _ => new ChartVerifyOutcome(true, 0.8f));

        // Pushed candidates are ignored for pitched sounds in CQT mode.
        Assert.Null(matcher.OfferNote(midi: 43, timeMs: 1040));
        Assert.Equal(0, matcher.Stats.Hits);

        // The pull-verifier judges it when the hit window closes.
        var events = matcher.AdvanceTo(playheadMs: 1500);
        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Hit, events[0].Verdict);
        Assert.Equal(0.8f, events[0].Confidence, 3);
        Assert.Equal(1, matcher.Stats.Hits);
    }

    [Fact]
    public void PitchedSound_RejectedByVerifier_IsMiss()
    {
        var arr = OneNoteAt(beat: 2, fret: 3);
        var matcher = Loaded(arr, _ => new ChartVerifyOutcome(false, 0f));

        var events = matcher.AdvanceTo(playheadMs: 1500);
        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Miss, events[0].Verdict);
        Assert.Equal(1, matcher.Stats.Misses);
    }

    [Fact]
    public void Verifier_ReceivesSoundingMidiAndChartTime()
    {
        var arr = OneNoteAt(beat: 2, fret: 3);           // MIDI 43 at 1000 ms
        ExpectedSound? seen = null;
        var matcher = Loaded(arr, s => { seen = s; return new ChartVerifyOutcome(true, 1f); });

        matcher.AdvanceTo(playheadMs: 1500);
        Assert.NotNull(seen);
        Assert.Equal(1000, seen!.TimeMs, 0);
        Assert.Equal(new[] { 43 }, seen.MidiNotes);
    }

    /// <summary>A hit shows as it is played, not a quarter-second later: the first tick at or
    /// after the note's time whose verifier says "confirmed" credits it, well before the
    /// 250 ms window closes.</summary>
    [Fact]
    public void AConfirmedSound_IsCreditedOnTheFirstTickItConfirms_NotWhenItsWindowCloses()
    {
        var confirmFromMs = 1060.0;                      // the audio arrives 60 ms after the note
        var playhead = 0.0;
        var matcher = LoadedWithEarly(OneNoteAt(beat: 2, fret: 3), _ => new ChartVerifyOutcome(playhead >= confirmFromMs, 1f));

        playhead = 990;                                  // not due yet: never asked
        Assert.Empty(matcher.AdvanceTo(playhead));
        playhead = 1030;                                 // due, but not confirmed yet
        Assert.Empty(matcher.AdvanceTo(playhead));
        playhead = 1063;                                 // confirmed: credited now
        var events = matcher.AdvanceTo(playhead);

        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Hit, events[0].Verdict);
        Assert.Equal(1063, events[0].DetectedAtMs, 0);
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Empty(matcher.AdvanceTo(1500));           // and never judged twice
        Assert.Equal(1, matcher.Stats.Judged);
    }

    /// <summary>Without an early check the matcher behaves as before: a confirmed sound is
    /// credited only when its window closes.</summary>
    [Fact]
    public void WithoutAnEarlyCheck_AHitStillWaitsForTheWindowToClose()
    {
        var matcher = Loaded(OneNoteAt(beat: 2, fret: 3), _ => new ChartVerifyOutcome(true, 1f));

        Assert.Empty(matcher.AdvanceTo(1100));
        Assert.Single(matcher.AdvanceTo(1500));
    }

    /// <summary>The early check is not asked before <see cref="LiveNoteMatcher.EarlyCreditAfterMs" />
    /// — the first frames after an attack are broadband and can pass a semitone-off note.</summary>
    [Fact]
    public void TheEarlyCheck_WaitsOutTheAttack()
    {
        var asked = new List<double>();
        var playhead = 0.0;
        var matcher = new LiveNoteMatcher
        {
            ChartVerify = _ => new ChartVerifyOutcome(true, 1f),
            ChartVerifyEarly = _ => { asked.Add(playhead); return new ChartVerifyOutcome(true, 1f); },
            EarlyCreditAfterMs = 90,
        };
        var arr = OneNoteAt(beat: 2, fret: 3);                // 1000 ms
        matcher.Load(arr, arr.Levels[0], Beats());

        foreach (var ms in new[] { 1030.0, 1060, 1089 })
        {
            playhead = ms;
            Assert.Empty(matcher.AdvanceTo(ms));
        }
        playhead = 1093;
        Assert.Single(matcher.AdvanceTo(1093));
        Assert.Equal([1093.0], asked);
    }

    /// <summary><see cref="LiveNoteMatcher.EarlyCreditConfirmations" />: a confirmation that does
    /// not hold on the next tick starts the count again.</summary>
    [Fact]
    public void TheEarlyCheck_NeedsItsConfirmationsInARow()
    {
        var answers = new Queue<bool>([true, false, true, true]);
        var matcher = new LiveNoteMatcher
        {
            ChartVerify = _ => new ChartVerifyOutcome(true, 1f),
            ChartVerifyEarly = _ => new ChartVerifyOutcome(answers.Dequeue(), 1f),
            EarlyCreditConfirmations = 2,
        };
        var arr = OneNoteAt(beat: 2, fret: 3);
        matcher.Load(arr, arr.Levels[0], Beats());

        Assert.Empty(matcher.AdvanceTo(1010));               // 1st confirm
        Assert.Empty(matcher.AdvanceTo(1040));               // broken
        Assert.Empty(matcher.AdvanceTo(1070));               // 1st again
        Assert.Single(matcher.AdvanceTo(1100));              // 2nd in a row: credited
    }

    /// <summary>The factory takes every early-credit knob and the CQT late edge from
    /// <c>LiveCqtConfig</c>. The late edge was a bare 230 ms that the 8192 kernel and the
    /// chord window had outgrown.</summary>
    [Fact]
    public void TheFactory_WiresTheEarlyCheckAndTheLateEdgeFromTheConfig()
    {
        Func<ExpectedSound, ChartVerifyOutcome> v = _ => new ChartVerifyOutcome(true, 1f);
        var m = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Strict, chartVerify: v, chartVerifyEarly: v);

        Assert.Same(v, m.ChartVerifyEarly);
        Assert.Equal(JuceSharp.Dsp.LiveCqtConfig.EarlyCreditAfterMs, m.EarlyCreditAfterMs);
        Assert.Equal(JuceSharp.Dsp.LiveCqtConfig.EarlyCreditConfirmations, m.EarlyCreditConfirmations);
        Assert.Equal(JuceSharp.Dsp.LiveCqtConfig.FinalVerdictAfterMs, m.HitWindowLateMs);
        Assert.True(m.HitWindowLateMs >= JuceSharp.Dsp.LiveCqtConfig.ChordPostWindowAfterSeconds * 1000
                    + JuceSharp.Dsp.LiveCqtConfig.MaxKernelSamples / 2 * 1000.0 / 44100,
            "the late edge closes before the chord window's frames exist");
    }

    /// <summary>A charted PINCH harmonic also counts from the attack detector's evidence in CQT
    /// mode (<see cref="LiveNoteMatcher.PinchHarmonicAttackEvidence" />): it rings at an overtone
    /// that depends on where the pick caught the string. Without the switch a pushed candidate is
    /// ignored, as it is for every other pitched sound in CQT mode.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void APinchHarmonic_CountsFromTheAttackEvidence(bool attackEvidence, bool credited)
    {
        var arr = OneNoteAt(beat: 2, fret: 3);                                        // MIDI 43
        arr.Levels[0].Notes[0].Harmonic = HarmonicType.Pinch;
        var matcher = new LiveNoteMatcher
        {
            ChartVerify = _ => new ChartVerifyOutcome(false, 0f),
            PinchHarmonicAttackEvidence = attackEvidence,
        };
        matcher.Load(arr, arr.Levels[0], Beats());

        var judged = matcher.OfferNote(midi: 43 + 19, timeMs: 1010);                  // the 3rd partial

        Assert.Equal(credited, judged is { Verdict: LivePlayVerdict.Hit });
    }

    /// <summary>The factory switches it on for CQT mode only.</summary>
    [Fact]
    public void TheFactory_TakesPinchAttackEvidenceFromTheConfig_InCqtModeOnly()
    {
        Func<ExpectedSound, ChartVerifyOutcome> v = _ => new ChartVerifyOutcome(true, 1f);
        Assert.Equal(JuceSharp.Dsp.LiveCqtConfig.PinchHarmonicAttackEvidence,
            LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal, chartVerify: v).PinchHarmonicAttackEvidence);
        Assert.False(LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal).PinchHarmonicAttackEvidence);
    }

    /// <summary>Only a MISS still waits: a sound the verifier never confirms is left open
    /// until its window closes, and is a Miss then — not the moment it is due.</summary>
    [Fact]
    public void AnUnconfirmedSound_StaysOpenUntilItsWindowCloses()
    {
        var matcher = LoadedWithEarly(OneNoteAt(beat: 2, fret: 3), _ => new ChartVerifyOutcome(false, 0f));

        Assert.Empty(matcher.AdvanceTo(1100));
        Assert.Empty(matcher.AdvanceTo(1240));
        var events = matcher.AdvanceTo(1260);

        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Miss, events[0].Verdict);
    }
}
