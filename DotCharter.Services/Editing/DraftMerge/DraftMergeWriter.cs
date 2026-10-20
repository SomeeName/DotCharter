using DotCharter.Services.State;

namespace DotCharter.Services.Editing.DraftMerge;

/// <summary>
/// Writes reviewed phrases of a chart-from-audio draft into the live level, as ONE
///     <see cref="CompositeAction" /> — one undo node — per call: the merge card's ✓ passes one
///     phrase, Merge all passes every phrase still listed.
///     <para>
///         A merged phrase holds exactly the draft's notes: its notes, chords and the hand shapes
///         that start in it go (a chord and its hand shape are one decision, so a removed chord never
///         leaves its bar behind), and clones of the draft notes come in. Chord templates are never
///         touched — nothing is minted and nothing shared is edited. Anchors are kept; a phrase that
///         plays and owns none gets the one inspector rule E06 asks for
///         (<see cref="PhraseStartAnchors.Needed" />, floored at <c>capo + 1</c>).
///     </para>
///     <para>
///         The level is re-read at write time. A phrase that was EMPTY when the review was held and
///         has sounds now is refused, never replaced: the charter only chose to replace notes where
///         <see cref="DraftMergePhrase.Replaces" /> says so.
///     </para>
/// </summary>
public static class DraftMergeWriter
{
    /// <summary>The history label of a merge.</summary>
    public const string HistoryLabelKey = "History_DraftMerge";

    /// <summary>
    ///     The action that merges <paramref name="phrases" /> of <paramref name="review" />, or null when
    ///     none of them can be written. <paramref name="refused" /> are the phrases that went stale.
    /// </summary>
    public static CompositeAction? Build(DraftMergeReview review, IReadOnlyList<DraftMergePhrase> phrases,
        IFretAnchorGenerationService generator, out IReadOnlyList<DraftMergePhrase> refused)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(generator);

        var level = review.Level;
        var stale = new List<DraftMergePhrase>();
        var merged = new List<DraftMergePhrase>();
        var notes = new List<GuitarNote>();
        var chords = new List<Chord>();
        var shapes = new List<HandShape>();
        var added = new List<GuitarNote>();
        foreach (var phrase in phrases.OrderBy(p => p.Start))
        {
            var oldNotes = level.Notes.Where(n => phrase.Contains(n.Position)).ToList();
            var oldChords = level.Chords.Where(c => phrase.Contains(c.Position)).ToList();
            if (!phrase.Replaces && oldNotes.Count + oldChords.Count > 0)
            {
                stale.Add(phrase);
                continue;
            }

            merged.Add(phrase);
            notes.AddRange(oldNotes);
            chords.AddRange(oldChords);
            shapes.AddRange(level.HandShapes.Where(h => phrase.Contains(h.Position)));
            added.AddRange(phrase.Draft.Where(n => DraftMergeReview.Playable(review.Arrangement, n)).Select(n => n.Clone()));
        }

        refused = stale;
        if (merged.Count == 0) return null;

        var anchors = AnchorsNeeded(review.Arrangement, level, merged, notes, chords, added, generator);
        return new CompositeAction(
        [
            new RemoveNotesAction(level.Notes, notes),
            new RemoveChordsAction(level.Chords, chords),
            new RemoveHandShapesAction(level.HandShapes, shapes),
            new AddNotesAction(level.Notes, added),
            new AddAnchorsAction(level.Anchors, anchors)
        ])
        {
            LabelKey = HistoryLabelKey
        };
    }

    /// <summary>The anchor each merged phrase needs, read off a scratch level that already holds the merge,
    /// in song order so a phrase sees the anchor the one before it brought.</summary>
    private static List<FretAnchor> AnchorsNeeded(Arrangement arrangement, ChartLevel level, List<DraftMergePhrase> merged,
        List<GuitarNote> removedNotes, List<Chord> removedChords, List<GuitarNote> added, IFretAnchorGenerationService generator)
    {
        var goneNotes = new HashSet<GuitarNote>(removedNotes, ReferenceEqualityComparer.Instance);
        var goneChords = new HashSet<Chord>(removedChords, ReferenceEqualityComparer.Instance);
        var scratch = new ChartLevel { DifficultyIndex = level.DifficultyIndex };
        foreach (var note in level.Notes.Where(n => !goneNotes.Contains(n)).Concat(added).OrderBy(n => n.Position))
            scratch.Notes.Add(note);
        foreach (var chord in level.Chords.Where(c => !goneChords.Contains(c))) scratch.Chords.Add(chord);
        foreach (var anchor in level.Anchors.OrderBy(a => a.Position)) scratch.Anchors.Add(anchor);

        var anchors = new List<FretAnchor>();
        foreach (var phrase in merged)
        {
            List<PhraseMarkers.PhraseMarker> span = [new(phrase.Start, phrase.Name)];
            if (phrase.End is { } end) span.Add(new PhraseMarkers.PhraseMarker(end, ""));
            if (PhraseStartAnchors.Needed(arrangement, scratch, span, 0, generator) is not { } anchor) continue;
            anchors.Add(anchor);
            scratch.Anchors.Add(anchor);
        }

        return anchors;
    }
}
