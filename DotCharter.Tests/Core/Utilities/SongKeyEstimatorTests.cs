namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     #822: the song key shown to the charter — read from the chart's sounding notes
///     (duration-weighted, capo and cent offset folded in), with the runner-up and a
///     confidence; an empty chart is unknown, and a locked (overriding) authored key
///     always drives chord spelling.
/// </summary>
public sealed class SongKeyEstimatorTests
{
    private static readonly string[] Standard = ["E", "A", "D", "G", "B", "E"];

    // Sounding pitch class of each open string in E standard, low to high.
    private static readonly int[] OpenPc = [4, 9, 2, 7, 11, 4];

    [Theory]
    [InlineData(0, false, 0, false)] // C major
    [InlineData(7, false, 1, false)] // G major
    [InlineData(1, false, -5, false)] // Db major
    [InlineData(9, true, 0, true)] // A minor (harmonic: G#)
    [InlineData(4, true, 1, true)] // E minor
    [InlineData(5, true, -4, true)] // F minor
    public void AScalePlayedOnTheNeck_ReadsAsItsKey(int tonic, bool minor, int accidentals, bool expectMinor)
    {
        var arrangement = Played(ScaleWalk(tonic, minor));

        var estimate = SongKeyEstimator.FromArrangements([arrangement]);

        Assert.NotNull(estimate);
        Assert.Equal(new ChordKeySignature(accidentals, expectMinor), estimate.Value.Best);
        Assert.NotEqual(estimate.Value.Best, estimate.Value.RunnerUp);
        Assert.InRange(estimate.Value.Confidence, 0, 1);
    }

    /// <summary>
    ///     A I–IV–V–I cadence in G major and a i–iv–V–i cadence in E minor share most of
    ///     their notes (relative keys, one sharp each). The held tonic chord and the
    ///     minor's raised leading tone (D#) have to tell them apart.
    /// </summary>
    [Fact]
    public void RelativeCadences_ReadAsTheirOwnKeys()
    {
        // Each chord voiced with its root doubled, as a guitar voices it.
        // G major: G B D | C E G | D F# A | G B D (held)
        var major = Played(Chords(([7, 11, 2, 7], 1), ([0, 4, 7, 0], 1), ([2, 6, 9, 2], 1), ([7, 11, 2, 7], 4)));
        // E minor: E G B | A C E | B D# F# | E G B (held)
        var minor = Played(Chords(([4, 7, 11, 4], 1), ([9, 0, 4, 9], 1), ([11, 3, 6, 11], 1), ([4, 7, 11, 4], 4)));

        Assert.Equal(new ChordKeySignature(1, false), SongKeyEstimator.FromArrangements([major])!.Value.Best);
        Assert.Equal(new ChordKeySignature(1, true), SongKeyEstimator.FromArrangements([minor])!.Value.Best);
    }

    /// <summary>
    ///     Only the seven notes of C major / A minor, evenly: nothing says which of the
    ///     relative pair it is, so the two come first and second and the call is close.
    /// </summary>
    [Fact]
    public void AnEvenDiatonicSet_IsACloseCallBetweenTheRelativePair()
    {
        var histogram = new double[12];
        foreach (var pc in new[] { 0, 2, 4, 5, 7, 9, 11 })
            histogram[pc] = 2;

        var estimate = SongKeyEstimator.FromHistogram(histogram)!.Value;

        Assert.True(estimate.RunnerUpIsRelative, $"{estimate.Best} then {estimate.RunnerUp}");
        Assert.Equal(0, estimate.Best.Accidentals);
        Assert.True(estimate.IsClose, $"margin {estimate.Margin}");
        Assert.True(estimate.Confidence < 0.25, $"confidence {estimate.Confidence}");
    }

    [Fact]
    public void DurationWeightsTheNotes_SoAHeldTonicOutweighsPassingNotes()
    {
        // The same eight onsets; only which note is held differs.
        var heldA = Played(Chords(([9], 8), ([0], 0), ([4], 0), ([2], 0), ([11], 0), ([5], 0), ([7], 0), ([4], 0)));
        var heldC = Played(Chords(([9], 0), ([0], 8), ([4], 0), ([2], 0), ([11], 0), ([5], 0), ([7], 0), ([4], 0)));

        Assert.Equal(new ChordKeySignature(0, true), SongKeyEstimator.FromArrangements([heldA])!.Value.Best);
        Assert.Equal(new ChordKeySignature(0, false), SongKeyEstimator.FromArrangements([heldC])!.Value.Best);
    }

    [Fact]
    public void SoundingPitch_FoldsInTheCapoAndTheCentOffset()
    {
        // C-major shapes fingered behind a capo at 2 (open strings ring at the capo) sound
        // D major; the same walk tuned a semitone down (-100 cents) sounds B major.
        var capo = Played(ScaleWalk(0, false), capo: 2);
        var detuned = Played(ScaleWalk(0, false));
        detuned.CentOffset = -100;

        Assert.Equal(new ChordKeySignature(2, false), SongKeyEstimator.FromArrangements([capo])!.Value.Best);
        Assert.Equal(new ChordKeySignature(5, false), SongKeyEstimator.FromArrangements([detuned])!.Value.Best);
    }

    [Fact]
    public void AnEmptyChart_IsUnknown()
    {
        Assert.Null(SongKeyEstimator.FromArrangements([]));
        Assert.Null(SongKeyEstimator.FromArrangements([new Arrangement { Tuning = Standard }]));
        Assert.Null(SongKeyEstimator.FromHistogram(new double[12]));
        Assert.Null(SongKeyEstimator.FromHistogram(null));
    }

    [Fact]
    public void ALockedKey_DrivesChordSpelling_EvenWhenTheNotesDisagree_AndEvenAsCMajor()
    {
        var dbMajor = Played(ScaleWalk(1, false));

        var unlocked = ChordKeyEstimator.Resolve(dbMajor, new ChartData { KeyAccidentals = 4 });
        var locked = ChordKeyEstimator.Resolve(dbMajor, new ChartData { KeyAccidentals = 4, KeyLocked = true });
        var lockedC = ChordKeyEstimator.Resolve(dbMajor, new ChartData { KeyLocked = true });

        Assert.Equal(new ChordKeySignature(-5, false), unlocked);
        Assert.Equal(new ChordKeySignature(4, false), locked);
        Assert.Equal(new ChordKeySignature(0, false), lockedC);
    }

    // Each step a chord of sounding pitch classes held for some beats.
    private static List<(int[] Pcs, double Beats)> Chords(params (int[] Pcs, double Beats)[] steps) => [.. steps];

    // The scale up and down with tonic, third and fifth held a beat; minor uses the harmonic form.
    private static List<(int[] Pcs, double Beats)> ScaleWalk(int tonic, bool minor)
    {
        int[] steps = minor ? [0, 2, 3, 5, 7, 8, 11] : [0, 2, 4, 5, 7, 9, 11];
        var walk = new List<(int[], double)>();
        foreach (var step in steps.Concat(steps.Reverse()))
        {
            var held = step is 0 || step == steps[2] || step is 7 ? 1.0 : 0.25;
            walk.Add(([(tonic + step) % 12], held));
        }

        walk.Add(([tonic % 12, (tonic + steps[2]) % 12, (tonic + 7) % 12], 4));
        return walk;
    }

    // Fingers every pitch class on the next free string, as a shape relative to the capo:
    // a relative fret 0 is the open string (ringing at the capo), any other sits capo frets higher.
    private static Arrangement Played(List<(int[] Pcs, double Beats)> steps, int capo = 0)
    {
        var arrangement = new Arrangement { Tuning = Standard, Capo = capo };
        var level = new ChartLevel { DifficultyIndex = 0 };
        var beat = 0;
        foreach (var (pcs, beats) in steps)
        {
            var chord = new Chord { Position = new FractionalPosition(beat, Fraction.Zero) };
            var used = new HashSet<int>();
            foreach (var pc in pcs)
            {
                var stringIndex = Enumerable.Range(0, 6).First(s => !used.Contains(s));
                used.Add(stringIndex);
                var relative = ((pc - OpenPc[stringIndex]) % 12 + 12) % 12;
                var fret = relative == 0 ? 0 : relative + capo;
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = stringIndex,
                    Fret = fret,
                    Sustain = SustainOf(beats)
                });
            }

            level.Chords.Add(chord);
            beat += 4;
        }

        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static FractionalPosition SustainOf(double beats) =>
        new((int)beats, beats % 1 == 0 ? Fraction.Zero : new Fraction(1, 4));
}
