namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     What <see cref="LevelGenerationService" /> records on
///     <see cref="Arrangement.PhraseInfos" /> — the per-phrase ceiling
///     <c>LiveNoteMatcher</c>, <see cref="LevelSquashService" /> and the RS
///     export all read back.
///     <para>
///         Two faults, both of which made the record disagree with the levels
///         that were just built: it read only ONE of the two phrase stores, and
///         it raised the structural phrases that are deliberately pinned at 0.
///     </para>
/// </summary>
public sealed class LevelGenerationPhraseInfoTests
{
    private const int LevelCount = 4;

    /// <summary>
    ///     The RS-XML and psarc readers fill <see cref="Arrangement.Phrases" /> +
    ///     <c>PhraseIterations</c>, never <c>EventPoint.Phrase</c> — measured over
    ///     <c>TestData/EOF</c> as 0 markers via the editor store against 2,596 via
    ///     iterations. Keyed on the editor store alone, generation recorded a
    ///     ceiling for none of an imported chart's phrases.
    /// </summary>
    [Fact]
    public void TryGenerate_RecordsTheCeiling_ForPhrasesThatLiveOnlyInThePhraseIterationStore()
    {
        var arrangement = ImportedShapeArrangement();

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, LevelCount));

        Assert.Equal(LevelCount - 1, arrangement.PhraseInfos["intro"].MaxDifficulty);
        Assert.Equal(LevelCount - 1, arrangement.PhraseInfos["riff"].MaxDifficulty);
    }

    /// <summary>COUNT / END / noguitar carry no sounds and
    /// <c>PhraseSectionGenerator</c> writes them at 0 on purpose; the blanket
    /// pass over the dictionary raised them to N-1.</summary>
    [Fact]
    public void TryGenerate_LeavesTheStructuralPhrasesAtDifficultyZero()
    {
        var arrangement = ImportedShapeArrangement();
        arrangement.Phrases.Add("COUNT");
        arrangement.PhraseIterations.Insert(0, new PhraseIteration(new FractionalPosition(0), 2));
        arrangement.Phrases.Add("END");
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(12), 3));
        arrangement.PhraseInfos["COUNT"] = new PhraseInfo(0, false);
        arrangement.PhraseInfos["END"] = new PhraseInfo(0, false);

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, LevelCount));

        Assert.Equal(0, arrangement.PhraseInfos["COUNT"].MaxDifficulty);
        Assert.Equal(0, arrangement.PhraseInfos["END"].MaxDifficulty);
        Assert.Equal(LevelCount - 1, arrangement.PhraseInfos["riff"].MaxDifficulty);
    }

    /// <summary>The editor's own store still works — the fix reads BOTH, it does
    /// not swap one blind spot for the other.</summary>
    [Fact]
    public void TryGenerate_StillRecordsTheCeiling_ForEditorAuthoredPhraseMarkers()
    {
        var arrangement = ImportedShapeArrangement();
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "outro" });
        arrangement.Levels[0].Notes.Add(new GuitarNote
        { Position = new FractionalPosition(8), StringIndex = 0, Fret = 7 });

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, LevelCount));

        Assert.Equal(LevelCount - 1, arrangement.PhraseInfos["outro"].MaxDifficulty);
    }

    /// <summary>A chart shaped like an import: phrase NAMES plus iterations, no
    /// <c>EventPoint.Phrase</c> and no <see cref="PhraseInfo" /> at all.</summary>
    private static Arrangement ImportedShapeArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Phrases.Add("intro");
        arrangement.Phrases.Add("riff");
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));

        var level = new ChartLevel { DifficultyIndex = 0 };
        for (var beat = 0; beat < 8; beat++)
        {
            level.Notes.Add(new GuitarNote
            { Position = new FractionalPosition(beat), StringIndex = beat % 3, Fret = 3 + beat % 4 });
        }

        arrangement.Levels.Add(level);
        return arrangement;
    }
}
