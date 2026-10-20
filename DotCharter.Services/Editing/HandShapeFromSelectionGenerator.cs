using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing;

/// <summary>
/// Builds chord templates and hand-shapes from the user's selection.
/// Walks the timeline and splits the selection into runs of contiguous
/// selected notes/chords (any unselected element breaks the run, so a
/// pattern repeated across the chart yields one handshape per occurrence
/// rather than a single span swallowing the gaps).
/// </summary>
public sealed class HandShapeFromSelectionGenerator
{
    /// <summary>The canonical spelling first: it is the one a mint is stamped with.</summary>
    private static readonly ArpeggioFlavour[] ArpeggioSpellings =
    [
        new(Arpeggio: true, ForceArpeggioInRs: false),
        new(Arpeggio: false, ForceArpeggioInRs: true),
        new(Arpeggio: true, ForceArpeggioInRs: true)
    ];

    private readonly IFingeringGenerationService _fingeringService;
    private readonly IHighwayEditingService _highwayEditingService;
    private readonly ISelectionService _selectionManager;
    private readonly IAppLogger _logger;

    public HandShapeFromSelectionGenerator(
        IFingeringGenerationService fingeringService,
        IHighwayEditingService highwayEditingService,
        ISelectionService selectionManager,
        IAppLogger logger)
    {
        _fingeringService = fingeringService;
        _highwayEditingService = highwayEditingService;
        _selectionManager = selectionManager;
        _logger = logger;
    }

    /// <summary>
    ///     One bar per contiguous run of the selection. <paramref name="arpeggio" />
    /// resolves each run's grip in the ARPEGGIO flavour — the Rocksmith
    ///     bracketed shape most lead parts are charted with — instead of generating a
    ///     plain bar and ticking Arpeggio in the chord-template panel afterwards.
    /// </summary>
    public void Generate(TrackEditorContext track, bool arpeggio = false)
    {
        var selectedNotes = _selectionManager.SelectedNotes.ToHashSet();
        var selectedChords = _selectionManager.SelectedChords.ToHashSet();
        if (selectedNotes.Count == 0 && selectedChords.Count == 0)
        {
            _logger.Info(AppLogCategory.Editing, "GenerateHandShape skipped: no notes or chords selected.");
            return;
        }

        var runs = ComputeContiguousSelectedRuns(track, selectedNotes, selectedChords);
        if (runs.Count == 0)
        {
            _logger.Info(AppLogCategory.Editing, "GenerateHandShape skipped: selection produced no runs.");
            return;
        }

        var newTemplates = new List<ChordTemplate>();
        var newHandShapes = new List<HandShape>();
        var nextTemplateId = _fingeringService.NextTemplateId(track.Arrangement);

        foreach (var run in runs)
        {
            var start = run.Start;
            var end = run.End;
            if (end <= start) end = start + new FractionalPosition(1);

            var seed = _fingeringService.BuildTemplateForRange(track.Arrangement,
                track.Notes, track.Chords, start, end);

            ChordTemplate? grip = null;
            if (seed != null && arpeggio)
            {
                grip = ResolveArpeggio(track.Arrangement, seed.Frets, newTemplates);
            }
            else if (seed != null)
            {
                // Re-check equivalence against templates already queued in
                // this batch so two identical runs share the same template.
                grip = _fingeringService.FindEquivalentTemplate(track.Arrangement, seed)
                       ?? newTemplates.FirstOrDefault(t => t.Equals(seed));
                if (grip == null)
                {
                    seed.Id = nextTemplateId++;
                    newTemplates.Add(seed);
                    grip = seed;
                }
            }

            // Named from the grip — never the literal "Chord" every generated bar
            // would otherwise carry, whatever it covered.
            newHandShapes.Add(new HandShape
            {
                Position = start,
                EndPosition = end,
                Name = grip?.Name ?? string.Empty,
                TemplateId = grip?.Id ?? -1
            });
        }

        var additional = new List<IChartAction>();
        if (newTemplates.Count > 0)
            additional.Add(new AddChordTemplatesAction(track.Arrangement.ChordTemplates, newTemplates));

        var trimmed = AddTailTrimActions(track, newHandShapes, additional);

        _highwayEditingService.CommitHandShapeDragAndDrop(
            [], [],
            [], [],
            [], newHandShapes,
            [], [],
            additional);

        _logger.Info(AppLogCategory.Editing,
            $"GenerateHandShape{(arpeggio ? " (arpeggio)" : "")} created {newHandShapes.Count} handshape(s) (new templates={newTemplates.Count}), " +
            $"trimmed {trimmed} tail(s), on track '{track.Arrangement.Name}'.");
    }

    /// <summary>
    ///     The arpeggio grip for <paramref name="frets" />: an arpeggio template the
    ///     arrangement already declares in EITHER spelling (an RS-XML import sets only
    ///     <c>ForceArpeggioInRs</c>, a psarc import only <c>Arpeggio</c>), else
    ///     <see cref="ChordTemplateResolver" />'s find-or-create in the canonical
    ///     spelling — the flag the chord-template panel's Arpeggio box writes. A mint
    ///     lands in <paramref name="minted" />, where the next run with the same frets
    ///     finds it, so two runs share one template.
    /// </summary>
    public static ChordTemplate ResolveArpeggio(
        Arrangement arrangement, IReadOnlyDictionary<int, int> frets, List<ChordTemplate> minted)
    {
        foreach (var spelling in ArpeggioSpellings)
        {
            if (ChordTemplateResolver.FindByShape(arrangement, frets, spelling) is { } declared)
                return declared;
        }

        return ChordTemplateResolver.Resolve(arrangement, frets, ArpeggioSpellings[0], null, minted);
    }

    /// <summary>
    ///     Takes the TAILS off the sounds the new bars cover — except the ones
    ///     whose tail carries the technique.
    ///     <para>
    ///         The tail is how a charter SETS the bar's length: stretch the last
    ///         note until the bar comes out right, generate, then go back and
    ///         shrink every tail again. The owner did that loop a dozen times in
    ///         one session. The length is already captured in the shape by the
    ///         time this runs, so the tails have done their job and the second
    ///         half of the loop is dead work.
    ///     </para>
    ///     <para>
    ///         A bend, slide, vibrato, tremolo or link-next is drawn ALONG the
    ///         tail — shorten it and the technique becomes unreadable — so those
    ///         sounds keep their length. Everything a note head carries on its
    ///         own (mute, HOPO, harmonic, accent) says as much with no tail.
    ///         <see cref="TailTechniques" /> is the rule.
    ///     </para>
    /// </summary>
    private static int AddTailTrimActions(
        TrackEditorContext track, List<HandShape> shapes, List<IChartAction> actions)
    {
        var noteTargets = new List<GuitarNote>();
        var noteOld = new List<GuitarNote>();
        var noteNew = new List<GuitarNote>();

        foreach (var note in track.Notes)
        {
            if (note.Sustain.Equals(FractionalPosition.Zero)) continue;
            if (!Covers(shapes, note.Position)) continue;
            if (TailTechniques.AreDrawnOnTheTail(note)) continue;

            var trimmedNote = note.Clone();
            trimmedNote.Sustain = FractionalPosition.Zero;
            noteTargets.Add(note);
            noteOld.Add(note.Clone());
            noteNew.Add(trimmedNote);
        }

        var chordTargets = new List<Chord>();
        var chordOld = new List<Chord>();
        var chordNew = new List<Chord>();

        foreach (var chord in track.Chords)
        {
            if (!Covers(shapes, chord.Position)) continue;
            if (TailTechniques.AreDrawnOnTheTail(chord)) continue;
            if (chord.ChordNotes.All(n => n.Sustain.Equals(FractionalPosition.Zero))) continue;

            var trimmedChord = chord.Clone();
            foreach (var chordNote in trimmedChord.ChordNotes) chordNote.Sustain = FractionalPosition.Zero;
            chordTargets.Add(chord);
            chordOld.Add(chord.Clone());
            chordNew.Add(trimmedChord);
        }

        if (noteTargets.Count > 0)
            actions.Add(new MutateNotesAction(track.Notes, noteTargets, noteOld, noteNew));
        if (chordTargets.Count > 0)
            actions.Add(new MutateChordsAction(track.Chords, chordTargets, chordOld, chordNew));

        return noteTargets.Count + chordTargets.Count;
    }

    /// <summary>
    ///     Containment goes through <see cref="HandShape.Covers" /> — the span is
    ///     HALF-OPEN and shapes ABUT, so a hand-rolled closed reading would count
    ///     the next bar's opening sound as this one's content.
    /// </summary>
    private static bool Covers(List<HandShape> shapes, FractionalPosition position)
    {
        foreach (var shape in shapes)
        {
            if (shape.Covers(position))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Walks the merged note + chord timeline in time order and returns the
    /// span of each maximal run of contiguous selected items. Visible for testing.
    /// </summary>
    public static List<(FractionalPosition Start, FractionalPosition End)> ComputeContiguousSelectedRuns(
        TrackEditorContext track,
        HashSet<GuitarNote> selectedNotes,
        HashSet<Chord> selectedChords)
    {
        var timeline = track.Notes
            .Select(n => new Sound(n.Position, n.EndPosition, selectedNotes.Contains(n)))
            .Concat(track.Chords.Select(c => new Sound(c.Position, c.EndPosition, selectedChords.Contains(c))))
            .OrderBy(x => x.Position)
            .ToList();

        var runs = new List<(FractionalPosition Start, FractionalPosition End)>();
        var runStart = FractionalPosition.Zero;
        var runEnd = FractionalPosition.Zero;
        var lastOnset = FractionalPosition.Zero;
        var inRun = false;

        for (var i = 0; i < timeline.Count; i++)
        {
            var entry = timeline[i];
            if (entry.IsSelected)
            {
                if (!inRun)
                {
                    runStart = entry.Position;
                    runEnd = entry.EndPosition;
                    inRun = true;
                }
                else if (entry.EndPosition > runEnd)
                {
                    runEnd = entry.EndPosition;
                }

                lastOnset = entry.Position;
                continue;
            }

            if (!inRun) continue;
            runs.Add((runStart, CoverLastSound(runEnd, lastOnset, timeline, i)));
            inRun = false;
        }

        if (inRun) runs.Add((runStart, CoverLastSound(runEnd, lastOnset, timeline, timeline.Count)));
        return runs;
    }

    /// <summary>
    ///     A hand shape's span is HALF-OPEN, so a run ending ON its last sound's
    ///     onset does not contain that sound.
    ///     <para>
    ///         That is the tail-less case, and it was the reported bug: select
    ///         four notes with no sustain, generate, and the bar covers three.
    ///         The fourth is outside it — and so is the GRIP built for it, since
    ///         <c>BuildTemplateForRange</c> reads the same half-open range. The
    ///         workaround people found was to give the notes tails first.
    ///     </para>
    ///     <para>
    ///         The extension reaches the NEXT sound rather than adding an invented
    ///         step: <c>ChartSpacingEnforcer</c> then applies the same minimum gap
    ///         every other shape gets, which lands the bar's end just before that
    ///         sound while covering the last selected one. With nothing after the
    ///         run there is nothing to crowd, so a beat is plenty.
    ///     </para>
    /// </summary>
    private static FractionalPosition CoverLastSound(
        FractionalPosition runEnd, FractionalPosition lastOnset, List<Sound> timeline, int nextIndex)
    {
        if (runEnd > lastOnset) return runEnd;

        for (var i = nextIndex; i < timeline.Count; i++)
        {
            if (timeline[i].Position > lastOnset)
                return timeline[i].Position;
        }

        return lastOnset + new FractionalPosition(1);
    }

    /// <summary>One sound on the merged timeline — a note or a chord, flattened to
    /// the three facts a run needs.</summary>
    private readonly record struct Sound(FractionalPosition Position, FractionalPosition EndPosition, bool IsSelected);
}
