using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Linking;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     What one <see cref="ChartProjectCloner.Clone" /> of a project costs in managed memory, ESTIMATED
/// from its element counts. An undo step of a whole-project tool holds two such clones, and
///     the history also budgets identity metadata and objects retained for reattachment.
///     <para>
///         The per-element costs were measured two ways that agree within a few percent:
///         cloning 20,000 synthetic elements of each kind, and a least-squares fit over the 70 base-game
///         songs imported with every difficulty level (median clone 10 MiB, the largest 33 MiB).
///         <c>ChartProjectFootprintTests</c> holds the estimate to the measured allocation.
///     </para>
/// </summary>
public static class ChartProjectFootprint
{
    private const long PerNote = 300;
    private const long PerChord = 270;
    private const long PerChordNote = 270;
    private const long PerAnchor = 140;
    private const long PerHandShape = 160;
    private const long PerSmallElement = 150; // beats, lyrics, showlights, annotations, events, mutes
    private const long PerLevel = 1_500;
    private const long PerArrangement = 4_000;
    private const long PerProject = 8_000;

    public static long EstimateBytes(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return PerProject +
               CollectionBytes(project.Beats, _ => PerSmallElement) +
               CollectionBytes(project.Showlights, _ => PerSmallElement) +
               CollectionBytes(project.Annotations, _ => PerSmallElement) +
               CollectionBytes(project.Stems, _ => PerSmallElement) +
               CollectionBytes(project.Bookmarks, _ => PerSmallElement) +
               CollectionBytes(project.ToneDefinitions, _ => PerSmallElement) +
               CollectionBytes(project.LinkGroups, g => PerSmallElement + CollectionBytes(g.Placements, _ => PerSmallElement)) +
               CollectionBytes(project.VocalTracks, t => CollectionBytes(t.Phrases,
                   p => PerSmallElement + CollectionBytes(p.Lyrics, _ => PerSmallElement))) +
               CollectionBytes(project.Arrangements, EstimateBytes) +
               CollectionBytes(project.Chart.SingleNotes, NoteBytes) +
               CollectionBytes(project.Chart.Chords, ChordBytes);
    }

    public static long EstimateBytes(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return PerArrangement +
               CollectionBytes(arrangement.EventPoints, _ => PerSmallElement) +
               CollectionBytes(arrangement.ToneChanges, _ => PerSmallElement) +
               CollectionBytes(arrangement.PhraseIterations, _ => PerSmallElement) +
               CollectionBytes(arrangement.ChordTemplates, _ => PerSmallElement) +
               CollectionBytes(arrangement.FretHandMuteTemplates, _ => PerSmallElement) +
               CollectionBytes(arrangement.TuningPitches, _ => PerSmallElement) +
               CollectionBytes(arrangement.NotationVoices, v => PerLevel + CollectionBytes(v.Notes, NoteBytes) +
                   CollectionBytes(v.Chords, ChordBytes)) +
               CollectionBytes(arrangement.Levels, l => PerLevel +
                   CollectionBytes(l.Notes, NoteBytes) + CollectionBytes(l.Chords, ChordBytes) +
                   CollectionBytes(l.Anchors, _ => PerAnchor) + CollectionBytes(l.HandShapes, _ => PerHandShape) +
                   CollectionBytes(l.FretHandMutes, _ => PerSmallElement));
    }

    /// <summary>
    /// A history snapshot also keeps its original objects available for reattachment. Reserve
    /// a second graph, including identity metadata, for those originals even while attached.
    /// Charging each snapshot independently deliberately overcounts shared originals, so dropping
    /// another history node can never leave the remaining node's retained graph unbudgeted.
    /// </summary>
    public static long EstimateHistoryBytes(ChartProject snapshot)
    {
        using var identities = ChartSnapshotIdentities.BeginCapture();
        return 2 * EstimateBytes(snapshot);
    }

    /// <summary>Arrangement-scoped version of <see cref="EstimateHistoryBytes(ChartProject)" />.</summary>
    public static long EstimateHistoryBytes(Arrangement snapshot)
    {
        using var identities = ChartSnapshotIdentities.BeginCapture();
        return 2 * EstimateBytes(snapshot);
    }

    /// <summary>Link-only snapshots retain groups and placements, but do not copy arrangements.</summary>
    public static long EstimateHistoryBytes(IList<LinkGroup> snapshot)
    {
        using var identities = ChartSnapshotIdentities.BeginCapture();
        return 2 * CollectionBytes(snapshot, g => PerSmallElement + CollectionBytes(g.Placements, _ => PerSmallElement));
    }

    /// <summary>
    ///     What one propagated linked-run copy keeps alive for undo and redo: every element it
    ///     removed from the target run and every element it added, its minted templates and its
    ///     hand-shape trims. A gesture keeps one copy per target per tick, so these add up.
    /// </summary>
    public static long EstimateCopyBytes(LinkedRunCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return PerSmallElement +
               copy.Notes.Removed.Sum(PlainNoteBytes) + copy.Notes.Added.Sum(PlainNoteBytes) +
               copy.Chords.Removed.Sum(PlainChordBytes) + copy.Chords.Added.Sum(PlainChordBytes) +
               (copy.Anchors.Removed.Count + copy.Anchors.Added.Count) * PerAnchor +
               (copy.HandShapes.Removed.Count + copy.HandShapes.Added.Count) * PerHandShape +
               (copy.Mutes.Removed.Count + copy.Mutes.Added.Count + copy.MintedList.Count + copy.TrimmedList.Count) *
               PerSmallElement;
    }

    // The copy's elements are live chart objects, not a history snapshot: they are counted
    // plainly, without registering them in the snapshot identity tables.
    private static long PlainNoteBytes(GuitarNote note) => PerNote + note.BendValues.Count * PerSmallElement;

    private static long PlainChordBytes(Chord chord) =>
        PerChord + chord.ChordNotes.Sum(n => PerChordNote + n.BendValues.Count * PerSmallElement);

    private static long NoteBytes(GuitarNote note) =>
        PerNote + CollectionBytes(note.BendValues, _ => PerSmallElement);

    private static long ChordBytes(Chord chord) =>
        PerChord + CollectionBytes(chord.ChordNotes,
            n => PerChordNote + CollectionBytes(n.BendValues, _ => PerSmallElement));

    private static long CollectionBytes<T>(IList<T> items, Func<T, long> estimate) where T : class =>
        ChartSnapshotIdentities.EstimateBytes(items, estimate);
}
