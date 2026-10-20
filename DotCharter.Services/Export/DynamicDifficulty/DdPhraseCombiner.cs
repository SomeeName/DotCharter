namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/PhraseCombiner.fs; MIT,
///     Copyright (c) 2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): one phrase per
///     phrase iteration, then the phrase search — iterations whose content is at least
///     <see cref="DdGeneratorConfig.PhraseSearchThreshold" /> percent alike share one
///     phrase when they got the same level count, or are linked (a
///     <c>newLinkedDiff</c>) when they did not. Names become COUNT, END, NOGUITAR and
///     p0, p1, … as DLC Builder writes them.
/// </summary>
internal static class DdPhraseCombiner
{
    private sealed record Combination(int MainId, int[] Ids, bool IsSameDifficulty);

    public static (List<DdPhrase> Phrases, List<DdPhraseIteration> Iterations, List<DdNewLinkedDiff> LinkedDiffs)
        Combine(DdGeneratorConfig config, IReadOnlyList<DdPhraseData> iterationData,
            IReadOnlyList<DdPhraseIteration> iterations, IReadOnlyList<int> levelCounts)
    {
        List<Combination> sameDifficulties;
        List<Combination> differentDifficulties;
        if (config.PhraseSearchThreshold is { } threshold)
        {
            var found = FindSamePhrases(threshold, levelCounts, iterationData);
            sameDifficulties = [.. found.Where(x => x.IsSameDifficulty)];
            differentDifficulties = [.. found.Where(x => !x.IsSameDifficulty)];
        }
        else
        {
            sameDifficulties = FindEmptyPhrases(iterationData);
            differentDifficulties = [];
        }

        // Create a mapping to the new phrase IDs after phrases with the same content have been combined
        var newPhraseIds = new int[iterations.Count];
        var phraseId = -1;
        var idMap = new Dictionary<int, int>();
        for (var id = 0; id < iterations.Count; id++)
        {
            var data = sameDifficulties.Find(x => id == x.MainId || x.Ids.Contains(id));
            if (data == null)
            {
                newPhraseIds[id] = ++phraseId;
            }
            else if (idMap.TryGetValue(data.MainId, out var value))
            {
                newPhraseIds[id] = value;
            }
            else
            {
                idMap.Add(data.MainId, ++phraseId);
                newPhraseIds[id] = phraseId;
            }
        }

        // Create phrase iterations
        var newIterations = new List<DdPhraseIteration>(iterations.Count);
        for (var i = 0; i < iterations.Count; i++)
        {
            var maxDiff = levelCounts[i] - 1;
            var hero = DdHeroLevels.None;
            if (maxDiff > 0)
            {
                // If there are only two levels, use the same difficulty,
                // otherwise score attack will crash on easy/medium
                hero = maxDiff == 1
                    ? new DdHeroLevels(1, 1, 1)
                    : new DdHeroLevels((int)Math.Round(maxDiff / 4.0), (int)Math.Round(maxDiff / 2.0), maxDiff);
            }

            newIterations.Add(new DdPhraseIteration(iterations[i].Time, newPhraseIds[i], hero));
        }

        // Create linked difficulties
        var linkedDiffs = differentDifficulties
            .Select(d => new DdNewLinkedDiff(-1, [newPhraseIds[d.MainId], .. d.Ids.Select(x => newPhraseIds[x])]))
            .ToList();

        // Create phrases
        var firstId = newIterations[0].PhraseId;
        var lastId = newIterations[^1].PhraseId;
        var created = new HashSet<int>();
        var counter = -1;
        var phrases = new List<DdPhrase>();
        foreach (var pi in newIterations)
        {
            if (!created.Add(pi.PhraseId)) continue;
            var maxDiff = pi.HeroLevels.Hard;
            string name;
            if (pi.PhraseId == firstId) name = "COUNT";
            else if (pi.PhraseId == lastId) name = "END";
            else if (maxDiff == 0) name = "NOGUITAR";
            else name = $"p{++counter}";
            phrases.Add(new DdPhrase(name, maxDiff, false));
        }

        return (phrases, newIterations, linkedDiffs);
    }

    private static bool IsEmpty(DdPhraseData phrase) => phrase.NoteCount == 0 && phrase.ChordCount == 0;

    private static int CalculateSimilarity(Func<IReadOnlyList<DdNote>, IReadOnlyList<DdNote>, double> fn,
        Func<IReadOnlyList<DdChord>, IReadOnlyList<DdChord>, double> fc, DdPhraseData phrase1, DdPhraseData phrase2)
    {
        var noteSimilarity = fn(phrase1.Notes, phrase2.Notes);
        var chordSimilarity = fc(phrase1.Chords, phrase2.Chords);
        double notesTotal = phrase1.NoteCount + phrase2.NoteCount;
        double chordsTotal = phrase1.ChordCount + phrase2.ChordCount;
        // F#'s round: Math.Round, to even.
        return (int)Math.Round(((noteSimilarity * notesTotal) + (chordSimilarity * chordsTotal)) / (notesTotal + chordsTotal));
    }

    private static int GetSimilarity(int threshold, DdPhraseData phrase1, DdPhraseData phrase2)
    {
        if (IsEmpty(phrase1) && IsEmpty(phrase2)) return 100;

        var simFastest = CalculateSimilarity(
            (a, b) => DdComparers.GetMaxSimilarityFastest(a.Count, b.Count),
            (a, b) => DdComparers.GetMaxSimilarityFastest(a.Count, b.Count), phrase1, phrase2);
        var simFast = simFastest < threshold
            ? 0
            : CalculateSimilarity(
                (a, b) => DdComparers.GetMaxSimilarityFast(DdComparers.NoteProjection, a, b),
                (a, b) => DdComparers.GetMaxSimilarityFast(DdComparers.ChordProjection, a, b), phrase1, phrase2);
        if (simFast < threshold) return 0;

        return CalculateSimilarity(
            (a, b) => DdComparers.GetSimilarityPercent(DdComparers.SameNote, a, b),
            (a, b) => DdComparers.GetSimilarityPercent(DdComparers.SameChord, a, b), phrase1, phrase2);
    }

    private static List<Combination> FindSamePhrases(int threshold, IReadOnlyList<int> levelCounts,
        IReadOnlyList<DdPhraseData> iterationData)
    {
        // Ignore the first and last phrases (COUNT, END)
        var lastId = iterationData.Count - 1;
        var matched = new HashSet<int> { 0, lastId };
        var result = new List<Combination>();
        for (var mainId = 0; mainId < iterationData.Count; mainId++)
        {
            if (matched.Contains(mainId)) continue;
            var main = iterationData[mainId];
            // The F# runs this search in parallel and reads `matched` only; the order of the
            // hits is the order of the iterations either way.
            var ids = Enumerable.Range(mainId + 1, iterationData.Count - mainId - 1)
                .Where(id => !matched.Contains(id) && GetSimilarity(threshold, main, iterationData[id]) >= threshold)
                .ToArray();
            if (ids.Length == 0) continue;

            matched.UnionWith(ids);
            var isSameDifficulty = ids.All(id => levelCounts[id] == levelCounts[mainId]);
            result.Add(new Combination(mainId, ids, isSameDifficulty));
        }

        return result;
    }

    private static List<Combination> FindEmptyPhrases(IReadOnlyList<DdPhraseData> iterationData)
    {
        var emptyIds = Enumerable.Range(1, Math.Max(0, iterationData.Count - 2))
            .Where(i => IsEmpty(iterationData[i]))
            .ToArray();
        return emptyIds.Length > 1 ? [new Combination(emptyIds[0], emptyIds[1..], true)] : [];
    }
}
