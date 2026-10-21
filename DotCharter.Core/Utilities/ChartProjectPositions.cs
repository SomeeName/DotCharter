using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     The ONE exhaustive walk over every <see cref="FractionalPosition" /> a
///     <see cref="ChartProject" /> stores, for the tools that move the whole
///     chart along the beat grid (insert beat, remove beat, add beats at start).
///     <para>
///         <b>A new position-bearing element class MUST be added here.</b> This
///         type exists because the two traversals in
///         <c>ChartMaintenanceService.TempoMap</c> were hand-written twice and
///         BOTH forgot <see cref="ChartLevel.FretHandMutes" /> and
///         <see cref="Arrangement.NotationVoices" />: Insert Beat / Remove Beat /
///         Add Beats At Start silently desynchronised every fret-hand mute and
///         every imported Guitar Pro voice 2-4 from the rest of the chart, and
///         nothing failed. It is the same rule (and the same failure mode) as
///         <see cref="ChartProjectRestorer" />'s "a new project collection must
///         be added there or it vanishes from undo history" and
///         <c>ChartDiffEngine.DiffStructure</c>'s "or a chart edited only in it
///         diffs as unchanged". Those two answer different questions — restore a
///         snapshot, and compare two arrangements — so they stay separate types;
///         what is shared is the obligation, not the code.
///     </para>
///     <para>
///         Two exclusions are deliberate:
///         <list type="bullet">
///             <item>
///                 <b>Relative</b> spans — <c>GuitarNote.Sustain</c>,
///                 <c>ChordNote.Sustain</c> — are DURATIONS measured from their
///                 own sound's onset. Moving the onset already moves them; adding
///                 the delta again would stretch every sustain by the shift.
///                 <c>GuitarNote.EndPosition</c> is derived from the two and has
///                 no setter for the same reason. <c>HandShape.EndPosition</c> and
///                 the vocal phrase/lyric end positions ARE absolute and are
///                 mapped.
///             </item>
///             <item>
///                 <b>Millisecond-keyed</b> markers — <c>Bookmark.Time</c> and
///                 <c>ChartAnnotation.TimeMs</c> — carry no
///                 <see cref="FractionalPosition" /> at all. Whether they move is
///                 a per-tool decision about the AUDIO (Add Silence shifts
///                 bookmarks because the audio moves; Insert Beat does not), so
///                 they stay with their callers.
///             </item>
///         </list>
///         <c>ChartData.SingleNotes</c>/<c>ChartData.Chords</c> — the legacy flat
///         store — are excluded too: nothing in the app writes them any more
///         (<see cref="ChartProjectRestorer" /> is their only remaining consumer),
///         so walking them would only add a traversal no chart can exercise.
///     </para>
/// </summary>
public static class ChartProjectPositions
{
    /// <summary>
    ///     Applies <paramref name="map" /> to every absolute position slot in
    ///     <paramref name="project" />. <paramref name="map" /> must be pure: it
    ///     is called once per slot, in unspecified order, and its result is
    ///     written straight back.
    /// </summary>
    public static void Remap(ChartProject project, Func<FractionalPosition, FractionalPosition> map)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(map);

        foreach (var showlight in project.Showlights)
            showlight.Position = map(showlight.Position);

        foreach (var arrangement in project.Arrangements)
        {
            foreach (var level in arrangement.Levels)
            {
                RemapSounds(level.Notes, level.Chords, map);

                foreach (var anchor in level.Anchors)
                    anchor.Position = map(anchor.Position);

                foreach (var handShape in level.HandShapes)
                {
                    handShape.Position = map(handShape.Position);
                    handShape.EndPosition = map(handShape.EndPosition);
                }

                foreach (var mute in level.FretHandMutes)
                    mute.Position = map(mute.Position);
            }

            // Notation-only sidecar voices (GP voices 2-4). They are never
            // played, but they ARE drawn on the same beat grid as the level
            // they sit beside, so a shift that skips them tears the sheet.
            foreach (var voice in arrangement.NotationVoices)
                RemapSounds(voice.Notes, voice.Chords, map);

            foreach (var eventPoint in arrangement.EventPoints)
                eventPoint.Position = map(eventPoint.Position);

            foreach (var toneChange in arrangement.ToneChanges)
                toneChange.Position = map(toneChange.Position);

            foreach (var iteration in arrangement.PhraseIterations)
                iteration.Position = map(iteration.Position);
        }

        // Linked runs: both barlines move with the grid, so a beat inserted INSIDE a run
        // widens it (and the history unlinks it), while one inserted before it shifts it whole.
        foreach (var group in project.LinkGroups)
        {
            foreach (var placement in group.Placements)
            {
                placement.Position = map(placement.Position);
                placement.EndPosition = map(placement.EndPosition);
            }
        }

        foreach (var vocalTrack in project.VocalTracks)
        {
            foreach (var phrase in vocalTrack.Phrases)
            {
                phrase.Position = map(phrase.Position);
                phrase.EndPosition = map(phrase.EndPosition);
                foreach (var lyric in phrase.Lyrics)
                {
                    lyric.Position = map(lyric.Position);
                    lyric.EndPosition = map(lyric.EndPosition);
                }
            }
        }
    }

    /// <summary>
    ///     Shifts every position at or after <paramref name="pivotBeatId" /> by
    ///     <paramref name="delta" /> whole beats, keeping the fraction — what
    ///     insert/remove beat needs so chart content stays anchored to the same
    ///     temporal beat once the beat list grows or shrinks.
    /// </summary>
    public static void ShiftFromBeat(ChartProject project, int pivotBeatId, int delta) =>
        Remap(project, p => p.BeatId >= pivotBeatId
            ? new FractionalPosition(p.BeatId + delta, p.Fraction)
            : p);

    /// <summary>Shifts every position by <paramref name="delta" />.</summary>
    public static void ShiftAll(ChartProject project, FractionalPosition delta) =>
        Remap(project, p => p + delta);

    /// <summary>
    ///     A sound carries its own bend curve, and <c>BendValue.Position</c> is
    ///     ABSOLUTE (not relative to the note) — so a shift that moves the note
    ///     and not its bends leaves the curve outside its own sustain, which the
    ///     next spacing pass then collapses.
    /// </summary>
    private static void RemapSounds(
        IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords,
        Func<FractionalPosition, FractionalPosition> map)
    {
        foreach (var note in notes)
        {
            note.Position = map(note.Position);
            foreach (var bend in note.BendValues)
                bend.Position = map(bend.Position);
        }

        foreach (var chord in chords)
        {
            chord.Position = map(chord.Position);
            foreach (var chordNote in chord.ChordNotes)
            {
                foreach (var bend in chordNote.BendValues)
                    bend.Position = map(bend.Position);
            }
        }
    }
}
