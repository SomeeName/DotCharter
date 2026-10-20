namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/DataExtractor.fs; MIT,
///     Copyright (c) 2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): what one
///     phrase iteration holds in its hardest level: what the phrase search compares and what
///     both level counts (Simple and the ML model) read. <c>UniqueLevels</c> and the CSV
///     export only fed the model's training and are not ported.
/// </summary>
internal sealed class DdPhraseData
{
    public required int StartTime { get; init; }
    public required int EndTime { get; init; }
    public required IReadOnlyList<DdBeat> Beats { get; init; }
    public required IReadOnlyList<DdNote> Notes { get; init; }
    public required IReadOnlyList<DdChord> Chords { get; init; }
    public required IReadOnlyList<DdHandShape> HandShapes { get; init; }
    public required IReadOnlyList<DdAnchor> Anchors { get; init; }
    public required int RepeatedNotes { get; init; }
    public required int RepeatedChords { get; init; }
    public required int MaxChordStrings { get; init; }
    public required int LengthMs { get; init; }
    public required int TempoEstimate { get; init; }
    public required int TechCount { get; init; }
    public required int BendCount { get; init; }
    public required int TapCount { get; init; }
    public required int TremoloCount { get; init; }
    public required int PinchHarmonicCount { get; init; }
    public required int HarmonicCount { get; init; }
    public required int VibratoCount { get; init; }
    public required int PalmMuteCount { get; init; }
    public required int SlideCount { get; init; }
    public required int UnpitchedSlideCount { get; init; }
    public required bool SoloPhrase { get; init; }

    public int NoteCount => Notes.Count;
    public int ChordCount => Chords.Count;

    /// <summary><c>getPhraseIterationData</c>.</summary>
    public static DdPhraseData For(DdArrangement arr, int iterationIndex)
    {
        var iteration = arr.PhraseIterations[iterationIndex];
        var phrase = arr.Phrases[iteration.PhraseId];
        // The F# reads Levels[maxDifficulty] and fails past the last level; a phrase
        // that claims more levels than the file has reads the hardest one here.
        var level = arr.Levels[Math.Min(phrase.MaxDifficulty, arr.Levels.Count - 1)];
        var startTime = iteration.Time;
        var endTime = iterationIndex + 1 == arr.PhraseIterations.Count
            ? arr.SongLength
            : arr.PhraseIterations[iterationIndex + 1].Time;

        var notes = Range(level.Notes, n => n.Time, startTime, endTime);
        var chords = Range(level.Chords, c => c.Time, startTime, endTime);

        var maxChordStrings = chords.Count == 0
            ? 0
            : chords.DistinctBy(c => c.ChordId).Max(c => arr.ChordTemplates[c.ChordId].Frets.Count(f => f != -1));

        var beats = Range(arr.Beats, b => b.Time, startTime, endTime);
        var lengthMs = endTime - startTime;
        int Count(Func<DdNote, bool> pred) =>
            notes.Count(pred) + chords.Count(c => c.HasChordNotes && c.ChordNotes!.Exists(n => pred(n)));
        var isSolo = phrase.IsSolo
                     || arr.Sections.Find(s => s.Time == startTime) is { } section && (section.Name == "solo" || section.Name == "tapping");

        return new DdPhraseData
        {
            StartTime = startTime,
            EndTime = endTime,
            Beats = beats,
            Notes = notes,
            Chords = chords,
            Anchors = Range(level.Anchors, a => a.Time, startTime, endTime),
            HandShapes = Range(level.HandShapes, h => h.StartTime, startTime, endTime),
            RepeatedNotes = CountPairs(notes, (n1, n2) => n1.String == n2.String && n1.Fret == n2.Fret && n1.Mask == n2.Mask),
            RepeatedChords = CountPairs(chords, (c1, c2) => c1.ChordId == c2.ChordId),
            MaxChordStrings = maxChordStrings,
            LengthMs = lengthMs,
            // Fix for files that have multiple phrase iterations at the same time stamp
            TempoEstimate = lengthMs != 0 ? 1000 * 60 * beats.Count / lengthMs : 0,
            TechCount = notes.Count(n => n.Mask != DdNoteMask.None || n.IsSlide || n.IsUnpitchedSlide || n.IsVibrato || n.IsBend)
                        + chords.Count(c => c.Mask != DdChordMask.None),
            TapCount = notes.Count(n => n.IsTap),
            TremoloCount = Count(n => n.IsTremolo),
            BendCount = Count(n => n.IsBend),
            PinchHarmonicCount = Count(n => n.IsPinchHarmonic),
            HarmonicCount = Count(n => n.IsHarmonic),
            VibratoCount = Count(n => n.IsVibrato),
            SlideCount = Count(n => n.IsSlide),
            UnpitchedSlideCount = Count(n => n.IsUnpitchedSlide),
            PalmMuteCount = Count(n => n.IsPalmMute),
            SoloPhrase = isSolo,
        };
    }

    /// <summary><c>Utils.getRange</c>: the elements in [start, end), in list order.</summary>
    private static List<T> Range<T>(List<T> items, Func<T, int> time, int startTime, int endTime) =>
        items.FindAll(e => time(e) >= startTime && time(e) < endTime);

    private static int CountPairs<T>(IReadOnlyList<T> items, Func<T, T, bool> repeated)
    {
        var count = 0;
        for (var i = 1; i < items.Count; i++)
        {
            if (repeated(items[i - 1], items[i])) count++;
        }

        return count;
    }
}
