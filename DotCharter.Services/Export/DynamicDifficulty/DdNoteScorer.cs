namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>A note or a chord: the generator's <c>XmlEntity</c>.</summary>
internal readonly record struct DdEntity(DdNote? Note, DdChord? Chord)
{
    public int Time => Note?.Time ?? Chord!.Time;

    /// <summary><c>getSustain</c>: a chord's is its first chord note's.</summary>
    public int Sustain => Note?.Sustain ?? (Chord!.HasChordNotes ? Chord.ChordNotes![0].Sustain : 0);

    public static DdEntity Of(DdNote note) => new(note, null);

    public static DdEntity Of(DdChord chord) => new(null, chord);

    /// <summary><c>createXmlEntityArrayFromLists</c>: notes then chords, sorted by time
    /// with the same UNSTABLE sort F#'s <c>Array.sortInPlaceBy</c> runs
    /// (<see cref="Array.Sort{TKey,TValue}(TKey[],TValue[])" /> on the keys).</summary>
    public static DdEntity[] FromLists(IReadOnlyList<DdNote> notes, IReadOnlyList<DdChord> chords)
    {
        var entities = new DdEntity[notes.Count + chords.Count];
        for (var i = 0; i < notes.Count; i++) entities[i] = Of(notes[i]);
        for (var i = 0; i < chords.Count; i++) entities[notes.Count + i] = Of(chords[i]);
        if (notes.Count == 0 || chords.Count == 0 || entities.Length < 2) return entities;
        var keys = entities.Select(e => e.Time).ToArray();
        Array.Sort(keys, entities);
        return entities;
    }
}

/// <summary>A score's share of the phrase: [Low, High) in 0..1.</summary>
internal readonly record struct DdDifficultyRange(double Low, double High);

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/NoteScorer.fs; MIT, Copyright (c)
///     2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): how hard a note is to keep,
///     from where it sits in the bar and the phrase. A LOW score is kept first.
/// </summary>
internal static class DdNoteScorer
{
    private static readonly int[] PhraseDivisions = [0, 3, 2, 3, 1, 3, 2, 3, 4];

    public static int GetScore(DdPhraseData phrase, int time, DdEntity entity)
    {
        var beats = phrase.Beats;
        DdBeat? beat1 = null;
        for (var i = beats.Count - 1; i >= 0; i--)
        {
            if (beats[i].Time <= time)
            {
                beat1 = beats[i];
                break;
            }
        }

        var beat2 = beats.FirstOrDefault(b => b.Time >= time);

        var score = TechniquePenalty(entity) + DivisionInPhrase(phrase.StartTime, phrase.EndTime, time);
        if (beat1 == null)
        {
            // The note comes before any beat
            return score + 20;
        }

        if (beat2 != null)
        {
            if (time != beat1.Time) return score + (10 * Subdivision(beat1.Time, beat2.Time, time));
            // On the first beat of the measure
            return score + (beat1.Measure >= 0 ? 0 : SubdivisionInsideMeasure(phrase.EndTime, beats, time));
        }

        // The note comes after the last beat in the phrase
        return score + (10 * Subdivision(beat1.Time, phrase.EndTime, time));
    }

    /// <summary><c>createScoreMap</c>: each score's range, lowest score first.</summary>
    public static Dictionary<int, DdDifficultyRange> CreateScoreMap(IReadOnlyList<(int Time, int Score)> scores, int totalNotes)
    {
        var map = new Dictionary<int, DdDifficultyRange>();
        var low = 0.0;
        foreach (var group in scores.GroupBy(s => s.Score).Select(g => (Score: g.Key, Notes: g.Count())).OrderBy(g => g.Score))
        {
            var high = low + ((double)group.Notes / totalNotes);
            map[group.Score] = new DdDifficultyRange(low, high);
            low = high;
        }

        return map;
    }

    private static int TechniquePenalty(DdEntity entity)
    {
        var isFretHandMute = entity.Note?.IsFretHandMute ?? entity.Chord!.IsFretHandMute;
        var isBend = entity.Note is { } n && n.MaxBend > 0.0f;
        if (isFretHandMute) return 20;
        return isBend ? 10 : 0;
    }

    private static int Subdivision(int startTime, int endTime, int time)
    {
        double dist = endTime - startTime;
        double pos = time - startTime;
        var mid = dist / 2.0;
        var div = dist / (pos - mid > 10.0 ? pos - mid : pos);
        return (int)Math.Round(div, MidpointRounding.AwayFromZero);
    }

    private static int SubdivisionInsideMeasure(int phraseEndTime, IReadOnlyList<DdBeat> beats, int time)
    {
        DdBeat? first = null;
        for (var i = beats.Count - 1; i >= 0; i--)
        {
            if (beats[i].Time < time && beats[i].Measure != -1)
            {
                first = beats[i];
                break;
            }
        }

        if (first == null) return 2;
        var following = beats.FirstOrDefault(b => b.Time > time && b.Measure != -1);
        return Subdivision(first.Time, following?.Time ?? phraseEndTime, time);
    }

    private static int DivisionInPhrase(int startTime, int endTime, int time)
    {
        double distance = endTime - startTime;
        var divisionLength = distance / PhraseDivisions.Length;
        double position = time - startTime;
        var index = 0;
        while (!(position >= divisionLength * index && position < divisionLength * (index + 1)))
        {
            // The F# recursion never ends here; a note is always inside its phrase.
            if (++index >= PhraseDivisions.Length)
                throw new InvalidOperationException($"A note at {time} ms lies outside its phrase {startTime}-{endTime} ms.");
        }

        return PhraseDivisions[index];
    }
}

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/LevelCounter.fs; MIT, Copyright
///     (c) 2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): the Simple level count,
///     DLC Builder's default. The ML.NET model is not ported.
/// </summary>
internal static class DdLevelCounter
{
    public const int MinimumNumberOfLevelsToGenerate = 2;
    public const int MaximumPossibleLevels = 30;

    public static int GetSimpleLevelCount(DdPhraseData phrase, IReadOnlyDictionary<int, DdDifficultyRange> scoreMap)
    {
        var baseCount = scoreMap.Count;
        // Try to prevent inflated level count for phrases that are mostly repeated notes
        var repeated = (double)(phrase.RepeatedNotes + phrase.RepeatedChords);
        var total = (double)(phrase.NoteCount + phrase.ChordCount);
        var levelCount = baseCount > 15 && repeated / total > 0.8 ? baseCount / 2 : baseCount;
        var minLevels = Math.Max(MinimumNumberOfLevelsToGenerate, phrase.MaxChordStrings);
        return Math.Clamp(levelCount, minLevels, MaximumPossibleLevels);
    }
}
