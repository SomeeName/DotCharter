using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     What live judging expects a BASS to sound.
///     <para>
///         <c>LivePitchAcceptance.SoundingMidi</c> anchored every arrangement at E2
///         (MIDI 40), so a bass chart expected pitches an octave above what the
///         instrument produces — while <c>SoundingPitch.MidiFor</c> (the Core
///         derivation the SNG mapper and the inspector use) and
///         <c>ArrangementTunerLogic</c> (which tells the player to tune string 0
///         to E1 = 28) both said 28. The classic candidate path hid the
///         disagreement behind its ±12 detector-error tolerance; the two paths
///         that deliberately drop that tolerance — string-aware verification and
///         the CQT chart-verifier — did not.
///     </para>
/// </summary>
public sealed class BassSoundingPitchJudgingTests
{
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static Arrangement Bass()
    {
        var arr = new Arrangement
        {
            ArrangementType = ArrangementType.Bass,
            StringCount = 4,
            Tuning = ["E", "A", "D", "G"]
        };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    /// <summary>The instrument's own pitch: a bass's open low E is E1, MIDI 28.</summary>
    [Theory]
    [InlineData(0, 0, 28)]  // E1
    [InlineData(1, 0, 33)]  // A1
    [InlineData(2, 0, 38)]  // D2
    [InlineData(3, 0, 43)]  // G2
    [InlineData(0, 5, 33)]  // fret 5 of the low E is the open A
    public void ABassIsChartedAnOctaveAboveWhatItSounds(int stringIndex, int fret, int expected)
        => Assert.Equal(expected, LivePitchAcceptance.SoundingMidi(Bass(), stringIndex, fret));

    /// <summary>
    ///     The two derivations must agree. They are separate methods for a
    ///     reason (this one supports a 7th string and prices a fret behind the
    ///     capo), but wherever both answer, one app cannot hold two opinions
    ///     about what a note sounds.
    /// </summary>
    [Theory]
    [InlineData(ArrangementType.Bass, 0)]
    [InlineData(ArrangementType.Bass, 3)]
    [InlineData(ArrangementType.Lead, 0)]
    [InlineData(ArrangementType.Lead, 3)]
    public void ItAgreesWithTheCoreSoundingPitchDerivation(ArrangementType type, int capo)
    {
        var arr = new Arrangement
        {
            ArrangementType = type,
            StringCount = type == ArrangementType.Bass ? 4 : 6,
            Tuning = type == ArrangementType.Bass
                ? ["E", "A", "D", "G"]
                : ["E", "A", "D", "G", "B", "e"],
            Capo = capo
        };
        var steps = type == ArrangementType.Bass ? new[] { -2, 0, 0, 0 } : [-2, 0, 0, 0, 0, 0];
        foreach (var step in steps)
            arr.TuningPitches.Add(new TuningOffset(step));

        for (var s = 0; s < arr.StringCount; s++)
        {
            for (var fret = capo == 0 ? 0 : capo; fret <= 12; fret++)
            {
                Assert.Equal(SoundingPitch.MidiFor(arr, s, fret),
                    LivePitchAcceptance.SoundingMidi(arr, s, fret));
                // …and fret 0 is the open (capo'd) string in both.
                Assert.Equal(SoundingPitch.MidiFor(arr, s, 0), LivePitchAcceptance.SoundingMidi(arr, s, 0));
            }
        }
    }

    /// <summary>
    ///     Judging agrees with the TUNER. If the guided check tells the player to
    ///     tune string 0 to 28 and the matcher then expects 40, the player is
    ///     being graded against a note they were just instructed not to produce.
    /// </summary>
    [Fact]
    public void JudgingExpectsTheSamePitchTheGuidedCheckToldThePlayerToTune()
    {
        var arr = Bass();
        var targets = TuningCheckTargets.Build(arr);

        for (var s = 0; s < targets.Length; s++)
            Assert.Equal(targets[s].Midi, LivePitchAcceptance.SoundingMidi(arr, s, 0));
    }

    /// <summary>
    ///     String-aware verification drops the ±12 octave tolerance because the
    ///     chart knows the string — which is exactly what stopped hiding the
    ///     octave error. Before the fix this note was an automatic Miss.
    /// </summary>
    [Fact]
    public void StringAware_ABassNotePlayedAtItsRealPitch_IsAHit()
    {
        var arr = Bass();
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), // 1000 ms
            StringIndex = 0,
            Fret = 3
        });

        var matcher = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            LiveChordVoicingStrictness.Standard, stringAware: true);
        matcher.Load(arr, arr.Levels[0], Beats());

        // A bass's low E fret 3 is G1 = MIDI 31, not G2 = 43.
        var result = matcher.OfferNote(midi: 31, timeMs: 1000);

        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
        Assert.Equal(1, matcher.Stats.Hits);
    }

    /// <summary>
    ///     The CQT chart-verifier is handed the sounding MIDI. It is asked for a
    ///     pitch a bass can actually make — which is what
    ///     <c>LiveCqtVerifier.LiveMinMidi = 28</c> was widened for, and what
    ///     nothing ever asked it for while the anchor was E2.
    /// </summary>
    [Fact]
    public void CqtMode_TheVerifierIsAskedForThePitchTheBassSounds()
    {
        var arr = Bass();
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 0 });

        var asked = new List<int>();
        var matcher = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            LiveChordVoicingStrictness.Standard, stringAware: false,
            chartVerify: sound =>
            {
                asked.AddRange(sound.MidiNotes);
                return new ChartVerifyOutcome(true, 1f);
            });
        matcher.Load(arr, arr.Levels[0], Beats());

        matcher.AdvanceTo(3000);

        Assert.Equal(new[] { 28 }, asked);
    }

    /// <summary>
    ///     A chart may state its tuning as note LETTERS with no per-string
    ///     offsets at all — RS-XML always writes offsets, other importers do not.
    ///     Reading <c>TuningPitches</c> raw judged such a chart as E standard.
    /// </summary>
    [Fact]
    public void ATuningStatedOnlyAsLetters_IsStillJudgedAtItsRealPitch()
    {
        var dropD = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            StringCount = 6,
            Tuning = ["D", "A", "D", "G", "B", "e"]
        };
        Assert.Empty(dropD.TuningPitches);

        Assert.Equal(38, LivePitchAcceptance.SoundingMidi(dropD, 0, 0)); // D2, not E2
        Assert.Equal(45, LivePitchAcceptance.SoundingMidi(dropD, 1, 0)); // A2, unchanged
    }
}
