using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     What one <see cref="ChartProjectCloner.Clone" /> of a project costs in managed memory, ESTIMATED
/// from its element counts. An undo step of a whole-project tool holds two such clones, and
///     the history needs their size to keep a byte budget without walking object graphs.
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
        var bytes = PerProject + (project.Beats.Count + project.Showlights.Count + project.Annotations.Count) * PerSmallElement;
        foreach (var track in project.VocalTracks)
        {
            foreach (var phrase in track.Phrases) bytes += (phrase.Lyrics.Count + 1) * PerSmallElement;
        }

        foreach (var arrangement in project.Arrangements) bytes += EstimateBytes(arrangement);
        return bytes;
    }

    public static long EstimateBytes(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var bytes = PerArrangement +
                    (arrangement.EventPoints.Count + arrangement.ToneChanges.Count + arrangement.PhraseIterations.Count +
                     arrangement.ChordTemplates.Count) * PerSmallElement;
        foreach (var level in arrangement.Levels)
        {
            bytes += PerLevel + level.Notes.Count * PerNote + level.Chords.Count * PerChord +
                     level.Anchors.Count * PerAnchor + level.HandShapes.Count * PerHandShape +
                     level.FretHandMutes.Count * PerSmallElement;
            foreach (var chord in level.Chords) bytes += chord.ChordNotes.Count * PerChordNote;
        }

        return bytes;
    }
}
