namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     What <see cref="LevelGenerationService" />'s chord → root-note reduction
///     is allowed to throw away. It is a DIFFICULTY reduction, so it may only
///     drop what makes a sound harder — and the generator's technique stripping
///     already spells that rule out for single notes, keeping mute and harmonic
///     because they change the SOUND rather than the difficulty. The reduction
///     path built its note from scratch and kept neither.
/// </summary>
public sealed class LevelGenerationChordReductionTests
{
    /// <summary>Three levels, so level 0 (ratio 1/3) is a SIMPLIFY level.</summary>
    private const int LevelCount = 3;

    /// <summary>
    ///     <see cref="HarmonicType" /> changes the sounding pitch (the same reason
    ///     the generator leaves harmonics on stripped notes alone), <c>Ignore</c>
    ///     decides whether the sound is judged at all, and bass slap/pop is how
    ///     the note is produced. None of the three is difficulty.
    /// </summary>
    [Fact]
    public void TryGenerate_ChordReduction_KeepsTheFlagsThatChangeWhatTheSoundIs()
    {
        var arrangement = new Arrangement { Name = "Bass" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(1), Phrase = "verse" });
        arrangement.Levels[0].Chords.Add(new Chord
        {
            Position = new FractionalPosition(0), TemplateId = 0, Ignore = true,
            ChordNotes =
            {
                new ChordNote
                {
                    StringIndex = 0, Fret = 12, Harmonic = HarmonicType.Normal,
                    BassPicking = BassPickingType.Slap
                },
                new ChordNote { StringIndex = 1, Fret = 12 }
            }
        });
        arrangement.Levels[0].Notes.Add(new GuitarNote
        { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, LevelCount));

        var root = Assert.Single(arrangement.Levels[0].Notes, n => n.Position == new FractionalPosition(0));
        Assert.Equal(HarmonicType.Normal, root.Harmonic);
        Assert.Equal(BassPickingType.Slap, root.BassPicking);
        Assert.True(root.Ignore);
    }

    /// <summary>The chord's own <c>Ignore</c> reaches the reduced note too — a
    /// strum the charter told the game not to judge must not become a judged
    /// note two difficulties down.</summary>
    [Fact]
    public void TryGenerate_ChordReduction_CarriesTheChordsOwnIgnoreFlag()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(1), Phrase = "verse" });
        arrangement.Levels[0].Chords.Add(new Chord
        {
            Position = new FractionalPosition(0), TemplateId = 0, Ignore = true,
            ChordNotes = { new ChordNote { StringIndex = 0, Fret = 5 }, new ChordNote { StringIndex = 1, Fret = 5 } }
        });
        arrangement.Levels[0].Notes.Add(new GuitarNote
        { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, LevelCount));

        Assert.True(Assert.Single(arrangement.Levels[0].Notes, n => n.Position == new FractionalPosition(0)).Ignore);
    }
}
