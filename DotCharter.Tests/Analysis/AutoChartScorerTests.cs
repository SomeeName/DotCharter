using DotCharter.Analysis.AudioToChart;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     The arithmetic behind the auto-chart corpus score (#296), pinned in the
///     default gate. The corpus run that reads numbers off it is env-gated and
///     skipped in every ordinary run, so without these the thing that computes
///     the published figures would never have been executed. They assert the
///     matcher's CONTRACT on synthetic fixtures, never a quality threshold.
/// </summary>
public sealed class AutoChartScorerTests
{
    private const double Tol = 0.050;

    private static ScoredSound S(double t, int midi, int stringIndex = 0, bool checkable = true) =>
        new(t, midi, stringIndex, checkable);

    /// <summary>
    ///     One fixture with every outcome the report distinguishes: a clean hit, a
    ///     right pitch on the wrong string, a wrong pitch, an octave error, a chord
    ///     with one tone missing, a missed onset and an extra one.
    /// </summary>
    [Fact]
    public void TheFixture_CountsHitsMissesExtrasAndWrongPitchesExactly()
    {
        ScoredSound[] truth =
        [
            S(1.00, 45, 1), // A: found, right pitch, right string
            S(2.00, 50, 2), // B: found, right pitch, WRONG string
            S(3.00, 55, 3), // C: found, wrong pitch (a semitone)
            S(4.00, 52, 2), // D: found, an octave off
            S(5.00, 40, 0), S(5.00, 47, 1), S(5.00, 52, 2), // E: chord, one tone missing
            S(6.00, 45, 1) // F: never found
        ];
        ScoredSound[] draft =
        [
            S(1.01, 45, 1),
            S(2.02, 50, 1),
            S(3.00, 56, 3),
            S(4.03, 64, 4),
            S(5.00, 40, 0), S(5.00, 47, 1),
            S(7.00, 60, 3) // extra
        ];

        var score = AutoChartScorer.Score(truth, draft, Tol);

        Assert.Equal(6, score.TruthOnsets);
        Assert.Equal(6, score.DraftOnsets);
        Assert.Equal(5, score.MatchedOnsets);
        Assert.Equal(8, score.TruthNotes);
        Assert.Equal(7, score.DraftNotes);
        Assert.Equal(7, score.TruthNotesAtMatchedOnsets);
        Assert.Equal(4, score.PitchHits); // A, B, two chord tones
        Assert.Equal(5, score.PitchClassHits); // + D's octave
        Assert.Equal(3, score.PositionHits); // A and the two chord tones; B is on the wrong string
        Assert.Equal(4, score.SingleNotesAtMatchedOnsets);
        Assert.Equal(2, score.SinglePitchHits);
        Assert.Equal(3, score.ChordNotesAtMatchedOnsets);
        Assert.Equal(2, score.ChordPitchHits);

        Assert.Equal(5.0 / 6, score.OnsetPrecision, 9);
        Assert.Equal(5.0 / 6, score.OnsetRecall, 9);
        Assert.Equal(4.0 / 7, score.PitchAccuracy, 9);
        Assert.Equal(3.0 / 4, score.PositionAgreement, 9);
        Assert.Equal(4.0 / 7, score.NotePrecision, 9);
        Assert.Equal(4.0 / 8, score.NoteRecall, 9);
        Assert.Equal(10.0, score.MedianOnsetErrorMs, 6); // errors 0, 0, 10, 20, 30 ms
    }

    /// <summary>One-to-one is the load-bearing property: the analyzer emits
    /// several onsets close together by design, and a many-to-one match would
    /// let them all claim the one charted onset.</summary>
    [Fact]
    public void TheMatcher_IsOneToOne_SoOneChartedOnsetCannotBeFoundTwice()
    {
        var score = AutoChartScorer.Score([S(1.000, 45)], [S(0.990, 45), S(1.010, 45), S(1.030, 45)], Tol);

        Assert.Equal(1, score.MatchedOnsets);
        Assert.Equal(3, score.DraftOnsets);
        Assert.Equal(1, score.PitchHits);
        Assert.Equal(1.0 / 3, score.OnsetPrecision, 9);
    }

    /// <summary>Inside one onset the pairing is one-to-one too, and string-aware:
    /// a charted unison (one pitch on two strings) drafted once is ONE hit, and
    /// that hit is credited to the string the draft actually used.</summary>
    [Fact]
    public void TheNotePairing_IsOneToOne_AndPairsAUnisonStringWithString()
    {
        var score = AutoChartScorer.Score([S(1.0, 52, 1), S(1.0, 52, 2)], [S(1.0, 52, 2)], Tol);

        Assert.Equal(2, score.TruthNotesAtMatchedOnsets);
        Assert.Equal(1, score.PitchHits);
        Assert.Equal(1, score.PitchClassHits);
        Assert.Equal(1, score.PositionHits);
    }

    /// <summary>The window is inclusive at the edge and hard outside it — an
    /// off-by-one here silently moves every number the report prints.</summary>
    [Theory]
    [InlineData(0.049, 1)]
    [InlineData(0.050, 1)]
    [InlineData(0.051, 0)]
    [InlineData(-0.050, 1)]
    [InlineData(-0.051, 0)]
    public void TheMatcher_CountsAHitExactlyInsideTheWindow(double offset, int expected)
    {
        var score = AutoChartScorer.Score([S(1.000, 45)], [S(1.000 + offset, 45)], Tol);

        Assert.Equal(expected, score.MatchedOnsets);
    }

    /// <summary>A sweep must not let an early draft onset consume a truth onset
    /// a later draft onset needed: two charted notes 60 ms apart, each drafted
    /// 40 ms late, are both found.</summary>
    [Fact]
    public void TheMatcher_FindsTheMaximumMatching_InADensePassage()
    {
        var score = AutoChartScorer.Score([S(1.00, 45), S(1.06, 47)], [S(1.04, 45), S(1.10, 47)], Tol);

        Assert.Equal(2, score.MatchedOnsets);
        Assert.Equal(2, score.PitchHits);
    }

    /// <summary>A harmonic is an onset — the player strikes it — but its fretted
    /// pitch is not what sounds, so it stays out of every pitch figure.</summary>
    [Fact]
    public void AnUncheckablePitch_CountsAsAnOnset_ButNotAsAPitch()
    {
        var score = AutoChartScorer.Score([S(1.00, 76, 5, checkable: false)], [S(1.00, 88, 5)], Tol);

        Assert.Equal(1, score.MatchedOnsets);
        Assert.Equal(0, score.TruthNotes);
        Assert.Equal(0, score.TruthNotesAtMatchedOnsets);
        Assert.Equal(0, score.PitchHits);
        Assert.Equal(0, score.PitchAccuracy);
    }

    [Fact]
    public void Scores_MergeByAddingCounts_AndNothingOverNothingIsZero()
    {
        var empty = new AutoChartScore();
        Assert.Equal(0, empty.OnsetF1);
        Assert.Equal(0, empty.NoteF1);
        Assert.True(double.IsNaN(empty.MedianOnsetErrorMs));

        var a = AutoChartScorer.Score([S(1.0, 45)], [S(1.0, 45)], Tol);
        var b = AutoChartScorer.Score([S(1.0, 45), S(2.0, 47)], [], Tol);
        var sum = AutoChartCorpus.Sum([a, b]);

        Assert.Equal(3, sum.TruthOnsets);
        Assert.Equal(1, sum.MatchedOnsets);
        Assert.Equal(1.0 / 3, sum.OnsetRecall, 9); // pooled, not the mean of 100 % and 0 %
    }

    /// <summary>
    ///     The ground truth is the HARDEST level (by difficulty index, not by
    ///     position in the list), chord tones are expanded, full mutes dropped, and
    ///     harmonics and pre-bends kept as onsets but not pitch-checkable.
    /// </summary>
    [Fact]
    public void GroundTruth_IsTheHardestLevel_WithMutesDroppedAndUnpitchablesFlagged()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var hardest = new ChartLevel { DifficultyIndex = 1 };
        hardest.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 1, Fret = 0 });
        hardest.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 1, Fret = 5, Mute = MuteType.Full
        });
        hardest.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(3), StringIndex = 5, Fret = 12, Harmonic = HarmonicType.Normal
        });
        var chord = new Chord { Position = new FractionalPosition(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        hardest.Chords.Add(chord);
        var preBent = new GuitarNote { Position = new FractionalPosition(5), StringIndex = 2, Fret = 7 };
        preBent.BendValues.Add(new BendValue(new FractionalPosition(5), 1.0));
        hardest.Notes.Add(preBent);

        var easiest = new ChartLevel { DifficultyIndex = 0 };
        easiest.Notes.Add(new GuitarNote { Position = new FractionalPosition(6), StringIndex = 0, Fret = 0 });
        arrangement.Levels.Add(hardest);
        arrangement.Levels.Add(easiest);

        var truth = AutoChartCorpus.GroundTruth(arrangement, AnalysisTestSignals.ConstantBeats(120, 8));

        Assert.Equal(
        [
            S(0.5, 45, 1),
            S(1.5, 76, 5, checkable: false),
            S(2.0, 43, 0), S(2.0, 47, 1),
            S(2.5, 57, 2, checkable: false)
        ], truth);
    }

    /// <summary>The bias diagnostic finds a constant lead and takes it out —
    /// signed, draft minus truth — so a detector that fires 40 ms early on every
    /// note is told apart from one that scatters.</summary>
    [Fact]
    public void RemoveBias_FindsAConstantLead_AndShiftsItOut()
    {
        ScoredSound[] truth = [S(1.0, 45), S(2.0, 47), S(3.0, 48)];
        var early = truth.Select(s => s with { TimeSeconds = s.TimeSeconds - 0.040 }).ToList();

        var (shifted, biasMs) = AutoChartCorpus.RemoveBias(truth, early);

        Assert.Equal(-40.0, biasMs, 6);
        Assert.Equal(3, AutoChartScorer.Score(truth, shifted, 0.001).MatchedOnsets);
        Assert.Equal(0, AutoChartScorer.Score(truth, early, 0.030).MatchedOnsets);
    }

    /// <summary>The fingerprint is what the determinism check compares, so it
    /// must see a one-fret change.</summary>
    [Fact]
    public void TheFingerprint_IsStable_AndSeesAOneFretChange()
    {
        static AudioToChartDraft Draft(int fret) => new(
        [
            new DraftNote(
                new GuitarNote { Position = new FractionalPosition(1), StringIndex = 1, Fret = fret },
                0.5, 45 + fret, 110f, 0.8f)
        ]);

        Assert.Equal(AutoChartCorpus.Fingerprint(Draft(0)), AutoChartCorpus.Fingerprint(Draft(0)));
        Assert.NotEqual(AutoChartCorpus.Fingerprint(Draft(0)), AutoChartCorpus.Fingerprint(Draft(1)));
    }

    /// <summary>The CSV is diffed between runs and read by tools in any locale:
    /// invariant numbers (spelled out with the invariant culture, never the
    /// thread's), and a song name with a comma ("Yu, Sansan") quoted.</summary>
    [Fact]
    public void TheCsv_IsInvariant_AndQuotesACommaInASongName()
    {
        var score = AutoChartScorer.Score([S(1.0, 45), S(2.0, 47)], [S(1.0, 45)], Tol);
        var row = new AutoChartRow("Yorushika - Yu, Sansan", "Lead_RS2", "Lead", score, score, score, score, score,
            [], "ABC");

        var line = AutoChartCorpus.Csv([row]).Split('\n')[1];

        Assert.StartsWith("\"Yorushika - Yu, Sansan\",Lead_RS2,Lead,2,1,1,1.0000,0.5000,", line);
        Assert.EndsWith(",ABC", line);
    }
}
