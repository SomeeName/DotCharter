namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>How many levels each phrase gets (Rocksmith2014.DD's <c>LevelCountGeneration</c>).</summary>
public enum DdLevelCountMode
{
    /// <summary>One level per distinct note score, clamped (DLC Builder's default).</summary>
    Simple,

    /// <summary>DLC Builder's trained model (<see cref="DdLevelCountModel" />).</summary>
    MLModel,

    /// <summary>The same count for every phrase (<see cref="DdGeneratorConfig.ConstantLevelCount" />).</summary>
    Constant,
}

/// <summary>Rocksmith2014.DD's <c>GeneratorConfig</c>. <see cref="Default" /> is DLC
/// Builder's (<c>DLCBuilder.Domain/Configuration.fs</c>): phrase search at 80 % and
/// Simple level counts.</summary>
internal sealed record DdGeneratorConfig(int? PhraseSearchThreshold, DdLevelCountMode LevelCountMode, int ConstantLevelCount = 0)
{
    public static readonly DdGeneratorConfig Default = new(80, DdLevelCountMode.Simple);

    /// <summary>DLC Builder's phrase search with its trained level-count model.</summary>
    public static readonly DdGeneratorConfig MLModel = new(80, DdLevelCountMode.MLModel);

    public static DdGeneratorConfig For(DdLevelCountMode mode) => mode == DdLevelCountMode.MLModel ? MLModel : Default;
}

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/Generator.fs; MIT, Copyright (c)
///     2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): DLC Builder's dynamic
///     difficulty generator. Given a single-level arrangement it writes the levels, the
///     phrases, the phrase iterations and the linked difficulties.
///     <para>
///         <b>One difference on purpose: it runs the phrases in order.</b> The F# runs them
///         in parallel, and every phrase adds the narrower chord templates it needs to the
///         ONE template list as it goes, so the IDs of new templates depend on thread
///         timing (two runs of DLC Builder on one file can number them differently). Run
///         in order, the templates are those of some F# run; the golden tests compare chord
///         IDs by the template they point at.
///     </para>
/// </summary>
internal static class DdGenerator
{
    /// <summary><c>generateForArrangement</c>: mutates <paramref name="arr" /> and returns it.</summary>
    public static DdArrangement GenerateForArrangement(DdGeneratorConfig config, DdArrangement arr)
    {
        var iterations = arr.PhraseIterations.ToList();
        var phraseIterationData = Enumerable.Range(0, iterations.Count).Select(i => DdPhraseData.For(arr, i)).ToList();

        // Create the difficulty levels
        var levelDataForPhrases = phraseIterationData.Select(data => GenerateLevels(config, arr, data)).ToList();
        var generatedLevelCounts = levelDataForPhrases.Select(l => l.Count).ToList();
        var maxDiff = generatedLevelCounts.Max();

        // Combine the data in the levels
        var combinedLevels = new List<DdLevel>(maxDiff);
        for (var diff = 0; diff < maxDiff; diff++)
        {
            var level = new DdLevel(diff);
            foreach (var phraseLevels in levelDataForPhrases)
            {
                if (diff >= phraseLevels.Count) continue;
                level.Anchors.AddRange(phraseLevels[diff].Anchors);
                level.Notes.AddRange(phraseLevels[diff].Notes);
                level.HandShapes.AddRange(phraseLevels[diff].HandShapes);
                level.Chords.AddRange(phraseLevels[diff].Chords);
            }

            combinedLevels.Add(level);
        }

        var (phrases, newIterations, linkedDiffs) =
            DdPhraseCombiner.Combine(config, phraseIterationData, iterations, generatedLevelCounts);

        arr.Levels = combinedLevels;
        arr.Phrases = phrases;
        arr.PhraseIterations = newIterations;
        arr.NewLinkedDiffs = linkedDiffs;
        return arr;
    }

    private static List<DdLevel> GenerateLevels(DdGeneratorConfig config, DdArrangement arr, DdPhraseData phraseData)
    {
        // Generate one level for empty phrases with only anchors copied
        if (phraseData.NoteCount + phraseData.ChordCount == 0)
            return [new DdLevel(0) { Anchors = [.. phraseData.Anchors] }];

        var entities = DdEntity.FromLists(phraseData.Notes, phraseData.Chords);
        var scores = entities.Select(e => (e.Time, Score: DdNoteScorer.GetScore(phraseData, e.Time, e))).ToList();
        var notesWithScore = scores.GroupBy(s => s.Score).ToDictionary(g => g.Key, g => g.Count());
        // F#'s readOnlyDict keeps the LAST score given for a time (a note and a chord can share one).
        var noteTimeToScore = new Dictionary<int, int>();
        foreach (var (time, score) in scores) noteTimeToScore[time] = score;
        var scoreMap = DdNoteScorer.CreateScoreMap(scores, entities.Length);

        // Determine the number of levels to generate for this phrase
        var levelCount = config.LevelCountMode switch
        {
            DdLevelCountMode.Constant => config.ConstantLevelCount,
            DdLevelCountMode.MLModel => DdLevelCountModel.Instance.LevelCount(arr.Path, phraseData),
            _ => DdLevelCounter.GetSimpleLevelCount(phraseData, scoreMap),
        };

        var applier = new DdTemplateRequestApplier(arr.ChordTemplates);
        var levels = new List<DdLevel>(levelCount);
        for (var diff = 0; diff < levelCount; diff++)
        {
            // Copy everything for the hardest level
            if (diff == levelCount - 1)
            {
                levels.Add(new DdLevel(diff)
                {
                    Notes = [.. phraseData.Notes],
                    Chords = [.. phraseData.Chords],
                    Anchors = [.. phraseData.Anchors],
                    HandShapes = [.. phraseData.HandShapes],
                });
                continue;
            }

            var diffPercent = (double)(diff + 1) / levelCount;
            var chosen = DdEntityChooser.Choose(diffPercent, scoreMap, noteTimeToScore, notesWithScore,
                arr.ChordTemplates, phraseData.HandShapes, phraseData.MaxChordStrings, arr.StringSlots, entities);
            var levelEntities = chosen.Select(c => c.Entity).ToList();
            var notes = levelEntities.Where(e => e.Note != null).Select(e => e.Note!).ToList();
            var chords = levelEntities.Where(e => e.Chord != null).Select(e => e.Chord!).ToList();

            // Ensure that the link next attributes for chords are correct
            // A chord that has a link next attribute, but does not have any notes with link next will crash the game
            foreach (var chord in chords)
            {
                // The F# throws on a linked chord without chord notes; such a chord is not linked to anything.
                if (chord.IsLinkNext && (chord.ChordNotes == null || chord.ChordNotes.TrueForAll(x => !x.IsLinkNext)))
                    chord.IsLinkNext = false;
            }

            var handShapes = DdHandShapeChooser.Choose(diffPercent, levelEntities, entities,
                phraseData.MaxChordStrings, arr.ChordTemplates, phraseData.HandShapes);
            var anchors = DdAnchorChooser.Choose(levelEntities, phraseData.Anchors, phraseData.StartTime, phraseData.EndTime);

            // `templateRequests1 |> Seq.append templateRequests2`: the hand shapes' first.
            foreach (var request in handShapes.Select(h => h.Request).Concat(chosen.Select(c => c.Request)))
            {
                if (request != null) applier.Apply(request);
            }

            levels.Add(new DdLevel(diff)
            {
                Notes = notes,
                Chords = chords,
                Anchors = anchors,
                HandShapes = [.. handShapes.Select(h => h.Shape)],
            });
        }

        return levels;
    }
}
