namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     #493: the key a Musical chord name is spelled in — read from the chart's
///     notes, with the authored key kept while it fits them.
/// </summary>
public class ChordKeyEstimatorTests
{
    private static readonly string[] Standard = ["E", "A", "D", "G", "B", "E"];

    [Theory]
    [InlineData(0, false, 0)] // C major
    [InlineData(1, false, -5)] // Db major, not C# major (+7)
    [InlineData(6, false, -6)] // Gb major: the six-accidental tie goes flat
    [InlineData(11, false, 5)] // B major, not Cb major (-7)
    [InlineData(9, true, 0)] // A minor
    [InlineData(3, true, -6)] // Eb minor
    [InlineData(8, true, 5)] // G# minor
    public void FromTonic_SpellsTheKeyWithTheFewerAccidentals(int tonic, bool minor, int accidentals)
    {
        var key = ChordKeySignature.FromTonic(tonic, minor);

        Assert.Equal(accidentals, key.Accidentals);
        Assert.Equal(minor, key.IsMinor);
        Assert.Equal(tonic, key.Tonic);
    }

    [Theory]
    [InlineData(0, false, "Db Eb F# Ab Bb")] // C major: b2 b3 #4 b6 b7
    [InlineData(0, true, "C# D# F# G# Bb")] // A minor: the raised 3rd/4th/6th/7th, then the b2
    [InlineData(2, false, "C# Eb F# G# Bb")] // D major: C# and F# in the scale, Eb (b2) and Bb (b6) flats, G# (#4) sharp
    [InlineData(-3, false, "Db Eb Gb Ab Bb")] // Eb major: Eb Ab Bb in the scale, Gb (b3) and Db (b7) flats
    [InlineData(4, false, "C# D# F# G# A#")] // E major: four in the scale, A# the #4
    public void SpellsFlat_FollowsTheSignatureThenTheDegree(int accidentals, bool minor, string expected)
    {
        var key = new ChordKeySignature(accidentals, minor);
        string[] sharps = ["C#", "D#", "F#", "G#", "A#"];
        string[] flats = ["Db", "Eb", "Gb", "Ab", "Bb"];
        int[] blackKeys = [1, 3, 6, 8, 10];
        var spelled = blackKeys.Select((pc, i) => ChordKeyEstimator.SpellsFlat(key, pc) ? flats[i] : sharps[i]);

        Assert.Equal(expected, string.Join(" ", spelled));
    }

    [Fact]
    public void Estimate_FindsAMajorKeyFromItsScale()
    {
        var key = ChordKeyEstimator.Estimate(Scale(tonic: 1), out _);

        Assert.Equal(new ChordKeySignature(-5, false), key);
    }

    /// <summary>
    ///     The owner's tabs: a song typed in Db (Gb, Ab, Bbm7) whose tab says E
    ///     major. A wrong authored key must not win against the notes.
    /// </summary>
    [Fact]
    public void Resolve_OverrulesAnAuthoredKeyTheNotesContradict()
    {
        var key = ChordKeyEstimator.Resolve(Scale(tonic: 1), new ChordKeySignature(4, false));

        Assert.Equal(new ChordKeySignature(-5, false), key);
    }

    [Fact]
    public void Resolve_KeepsAnAuthoredKeyThatFitsTheNotes()
    {
        var authored = new ChordKeySignature(-5, false);

        Assert.Equal(authored, ChordKeyEstimator.Resolve(Scale(tonic: 1), authored));
    }

    [Fact]
    public void Resolve_KeepsTheAuthoredKey_WhenTheNotesAreTooFewToSay()
    {
        var thin = new double[12];
        thin[1] = 3;
        thin[8] = 2;
        var authored = new ChordKeySignature(4, false);

        Assert.Equal(authored, ChordKeyEstimator.Resolve(thin, authored));
    }

    [Fact]
    public void Resolve_WithNoNotesAndNoKey_IsNull()
    {
        Assert.Null(ChordKeyEstimator.Resolve((IReadOnlyList<double>?)null, null));
        Assert.Null(ChordKeyEstimator.Resolve(new Arrangement { Tuning = Standard }, new ChartData()));
    }

    [Fact]
    public void ResolveFromTheProject_TreatsCMajorAsUnset_AndReadsAnyOtherKey()
    {
        var empty = new Arrangement { Tuning = Standard };

        Assert.Null(ChordKeyEstimator.Resolve(empty, new ChartData { KeyAccidentals = 0, KeyIsMinor = false }));
        Assert.Equal(new ChordKeySignature(-3, false),
            ChordKeyEstimator.Resolve(empty, new ChartData { KeyAccidentals = -3 }));
        Assert.Equal(new ChordKeySignature(0, true),
            ChordKeyEstimator.Resolve(empty, new ChartData { KeyIsMinor = true }));
    }

    [Fact]
    public void Histogram_ReadsTheHardestLevel_FoldsTheCapoIn_AndSkipsFullMutes()
    {
        var arrangement = new Arrangement { Tuning = Standard, Capo = 2 };
        var easy = new ChartLevel { DifficultyIndex = 0 };
        easy.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 5 });
        var hard = new ChartLevel { DifficultyIndex = 3 };
        hard.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 0 }); // open E under capo 2 → F#
        hard.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 3, Mute = MuteType.Full });
        hard.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 3, Mute = MuteType.Palm }); // still a C
        var chord = new Chord();
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 4 }); // B
        hard.Chords.Add(chord);
        arrangement.Levels.Add(easy);
        arrangement.Levels.Add(hard);

        var histogram = ChordKeyEstimator.PitchClassHistogram(arrangement);

        Assert.Equal(3, histogram.Sum());
        Assert.Equal(1, histogram[6]);
        Assert.Equal(1, histogram[0]);
        Assert.Equal(1, histogram[11]);
        Assert.Equal(0, histogram[9]); // the easy level's A is not read
    }

    [Fact]
    public void Histogram_FallsBackToTheTemplates_WhenTheLevelIsEmpty()
    {
        var arrangement = new Arrangement { Tuning = Standard };
        var template = new ChordTemplate();
        template.Frets[1] = 3;
        template.Frets[2] = 2;
        arrangement.ChordTemplates.Add(template);

        var histogram = ChordKeyEstimator.PitchClassHistogram(arrangement);

        Assert.Equal(1, histogram[0]);
        Assert.Equal(1, histogram[4]);
    }

    [Fact]
    public void For_EofStyle_NeverCarriesAKey()
    {
        var options = ChordNamingOptions.For(ChordNamingStyle.Eof, new Arrangement { Tuning = Standard },
            new ChartData { KeyAccidentals = -3 });

        Assert.Equal(ChordNamingOptions.Default, options);
    }

    [Fact]
    public void For_MusicalStyle_ResolvesTheKey()
    {
        var options = ChordNamingOptions.For(ChordNamingStyle.Musical, new Arrangement { Tuning = Standard },
            new ChartData { KeyAccidentals = -3 });

        Assert.Equal(new ChordNamingOptions(ChordNamingStyle.Musical, new ChordKeySignature(-3, false)), options);
    }

    // A major scale on a tonic, weighted the way a song leans on it: the tonic
    // and fifth most, the third next.
    private static double[] Scale(int tonic)
    {
        var histogram = new double[12];
        int[] steps = [0, 2, 4, 5, 7, 9, 11];
        double[] weights = [6, 3, 4, 3, 5, 3, 2];
        for (var i = 0; i < steps.Length; i++)
            histogram[(tonic + steps[i]) % 12] = weights[i];
        return histogram;
    }
}
