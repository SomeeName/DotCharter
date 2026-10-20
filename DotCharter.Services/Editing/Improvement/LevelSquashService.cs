namespace DotCharter.Services.Editing.Improvement;

/// <summary>
/// Phrase-aware level squashing compatible with rscharter's "Squash levels":
/// every phrase segment is copied from the phrase's <see cref="PhraseInfo.MaxDifficulty"/>
/// level into a single combined difficulty level.
/// <para>
///     Two rules, both of which shipped inverted and each of which threw the
///     HARDEST level away — the one thing a squash may never do. Phrase markers
///     come from <see cref="PhraseMarkers"/> (BOTH stores), and a phrase with no
///     <see cref="PhraseInfo"/> reads the hardest level rather than level 0.
/// </para>
/// </summary>
public sealed class LevelSquashService : ILevelSquashService
{
    public bool TrySquash(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (arrangement.Levels.Count <= 1) return false;

        var squished = new ChartLevel { DifficultyIndex = 0 };

        // BOTH phrase stores. Keyed on EventPoint.Phrase alone this found ZERO
        // phrases in every IMPORTED chart (measured over TestData/EOF: 0 markers
        // via EventPoint, 2,596 via PhraseIterations) — the whole song then
        // collapsed to the single leading segment, which resolved to level 0,
        // and squashing an imported multi-level chart threw the hardest level
        // away and kept the EASIEST one.
        var phraseMarkers = PhraseMarkers.All(arrangement);

        // "No phrase metadata" is NOT "difficulty 0" — the same convention
        // LiveExpectedTimeline.BuildPhraseSpans already spells out. Defaulting to 0
        // here silently dropped every un-annotated phrase to the easiest level.
        var hardest = arrangement.Levels.Max(level => level.DifficultyIndex);

        for (var i = -1; i < phraseMarkers.Count; i++)
        {
            // i < 0 is the region BEFORE the first phrase marker — outside every
            // phrase, i.e. RS's COUNT lead-in, which is why it takes level 0.
            // With no markers at all that segment IS the whole song, so it has
            // to come from the hardest level instead.
            var levelId = i < 0
                ? phraseMarkers.Count == 0 ? hardest : 0
                : ResolveSourceLevelId(arrangement, phraseMarkers[i].Name, hardest);
            var sourceLevel = arrangement.Levels
                .OrderBy(level => level.DifficultyIndex)
                .FirstOrDefault(level => level.DifficultyIndex == levelId)
                ?? arrangement.Levels[Math.Clamp(levelId, 0, arrangement.Levels.Count - 1)];

            var from = i < 0 ? (FractionalPosition?)null : phraseMarkers[i].Position;
            var to = i + 1 < phraseMarkers.Count ? phraseMarkers[i + 1].Position : (FractionalPosition?)null;

            AppendRange(squished.Notes, sourceLevel.Notes, from, to, static note => note.Position, static note => note.Clone());
            AppendRange(squished.Chords, sourceLevel.Chords, from, to, static chord => chord.Position, static chord => chord.Clone());
            AppendRange(squished.Anchors, sourceLevel.Anchors, from, to, static anchor => anchor.Position, static anchor => anchor.Clone());
            AppendRange(squished.HandShapes, sourceLevel.HandShapes, from, to, static hs => hs.Position, static hs => hs.Clone());
            AppendRange(squished.FretHandMutes, sourceLevel.FretHandMutes, from, to, static fhm => fhm.Position,
                static fhm => new FretHandMute(fhm.Position));
        }

        SortByPosition(squished.Notes, static note => note.Position);
        SortByPosition(squished.Chords, static chord => chord.Position);
        SortByPosition(squished.Anchors, static anchor => anchor.Position);
        SortByPosition(squished.HandShapes, static hs => hs.Position);
        SortByPosition(squished.FretHandMutes, static fhm => fhm.Position);

        ClearLinksWithNoLanding(squished);

        arrangement.Levels.Clear();
        arrangement.Levels.Add(squished);
        NormalizePhraseInfos(arrangement);
        return true;
    }

    /// <summary>
    ///     Clears a Link-Next the squashed level has nothing to land on. Every
    ///     phrase segment is copied from its OWN <see cref="PhraseInfo.MaxDifficulty" />
    ///     level, so the single level this produces is a MIX — and a sustain
    ///     linked across a phrase boundary points into content that came from a
    ///     DIFFERENT level, where the note it named may have been thinned away or
    ///     folded into a reduced chord. The source note is copied verbatim and
    ///     keeps its flag, so the squash manufactured the
    ///     <c>LinkNextMissingTargetNote</c> that <c>LinkNextRule</c> reports.
    ///     <para>
    ///         <b>Only the "nothing follows on this string AT ALL" case.</b> No
    ///         correctly authored link can be in it. A link whose landing merely
    ///         MOVED is the charter's own to judge and the squash may not
    ///         silently unlink it — the inspector already reports that one.
    ///     </para>
    /// </summary>
    private static void ClearLinksWithNoLanding(ChartLevel level)
    {
        var lastByString = new Dictionary<int, FractionalPosition>();
        foreach (var note in level.Notes) Remember(note.StringIndex, note.Position);
        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
                Remember(chordNote.StringIndex, chord.Position);
        }

        foreach (var note in level.Notes)
        {
            if (note.LinkNext && NothingFollows(note.StringIndex, note.Position))
                note.LinkNext = false;
        }

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if (chordNote.LinkNext && NothingFollows(chordNote.StringIndex, chord.Position))
                    chordNote.LinkNext = false;
            }
        }

        void Remember(int stringIndex, FractionalPosition position)
        {
            if (!lastByString.TryGetValue(stringIndex, out var last) || position > last)
                lastByString[stringIndex] = position;
        }

        bool NothingFollows(int stringIndex, FractionalPosition position) =>
            !lastByString.TryGetValue(stringIndex, out var last) || last <= position;
    }

    /// <summary>
    ///     The level a phrase's content is squashed FROM. A phrase the
    ///     arrangement carries no <see cref="PhraseInfo" /> for — every phrase of
    ///     an RS-XML/psarc import, and any marker whose id could not be resolved
    ///     to a name — is assumed fully levelled and reads the HARDEST level:
    ///     "no metadata" is not "difficulty 0", and answering 0 threw the
    ///     hardest level away, which is the one thing a squash must never do.
    /// </summary>
    private static int ResolveSourceLevelId(Arrangement arrangement, string? phraseName, int hardest)
    {
        if (string.IsNullOrWhiteSpace(phraseName)) return hardest;
        return arrangement.PhraseInfos.TryGetValue(phraseName, out var info)
            ? Math.Max(0, info.MaxDifficulty)
            : hardest;
    }

    private static void NormalizePhraseInfos(Arrangement arrangement)
    {
        foreach (var info in arrangement.PhraseInfos.Values)
            info.MaxDifficulty = 0;
    }

    private static void AppendRange<T>(
        ICollection<T> target,
        IEnumerable<T> source,
        FractionalPosition? from,
        FractionalPosition? to,
        Func<T, FractionalPosition> positionSelector,
        Func<T, T> clone)
    {
        foreach (var item in source)
        {
            var position = positionSelector(item);
            if (from.HasValue && position < from.Value) continue;
            if (to.HasValue && position >= to.Value) continue;
            target.Add(clone(item));
        }
    }

    private static void SortByPosition<T>(IList<T> items, Func<T, FractionalPosition> positionSelector)
    {
        if (items.Count <= 1) return;
        var sorted = items.OrderBy(positionSelector).ToList();
        for (var i = 0; i < sorted.Count; i++)
            items[i] = sorted[i];
    }
}
