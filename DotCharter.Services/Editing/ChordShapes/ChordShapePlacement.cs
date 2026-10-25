using System.Collections.ObjectModel;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>Why a chord-shape placement was not built. <see cref="None" /> means it was.</summary>
public enum ChordShapePlacementRefusal
{
    None,

    /// <summary>No arrangement or level to place into.</summary>
    NoChart,

    /// <summary>The template is not one of this arrangement's (by reference).</summary>
    NotInArrangement,

    /// <summary>The template plays no string.</summary>
    EmptyShape,

    /// <summary>The template plays a string this arrangement has not got.</summary>
    StringOutOfRange,

    /// <summary>
    ///     A fret sits strictly between the nut and the capo — inspector rule <c>L01</c>. Snapping
    ///     it onto the capo would squash the grip into a different chord, so the shape is refused
    ///     whole, the way <c>TransposeShapeTo</c> refuses a grip it would squash.
    /// </summary>
    BelowCapo,

    /// <summary>A fret past the 24th.</summary>
    OffNeck,

    /// <summary>Every target already plays this shape on this template: nothing would change.</summary>
    SameShape,

    /// <summary>No chord before the pen to repeat.</summary>
    NoPreviousChord
}

/// <summary>Where a placement lands and what it may write: the level's sounds and the arrangement.</summary>
public sealed record ChordShapePlacementTarget(
    Arrangement Arrangement,
    ObservableCollection<GuitarNote> Notes,
    ObservableCollection<Chord> Chords,
    IStrictCollisionService Collisions,
    Action? OnPostExecute);

/// <summary>A planned placement: the ONE undoable action to commit, or why there is none.</summary>
public sealed record ChordShapePlacementPlan(
    IChartAction? Action,
    ChordShapePlacementRefusal Refusal,
    ChordTemplate? Shape = null,
    int Created = 0,
    int Reshaped = 0)
{
    public bool IsRefused => Action is null;

    public static ChordShapePlacementPlan Refused(ChordShapePlacementRefusal refusal, ChordTemplate? shape = null) =>
        new(null, refusal, shape);
}

/// <summary>
/// Places a whole chord SHAPE in one gesture: the chord-shape palette's click and
///     "Repeat previous chord". Pure — it builds ONE composite action and commits nothing.
///     <para>
///         At each position it does what a string toggle does there, for every string at once:
///         over nothing it creates the chord; over notes it promotes them into the chord
///         (carrying the techniques of every string the shape keeps); over a chord it reshapes
///         that chord in place. The placed chord POINTS AT the template — it arrives named and
///         fingered, in the template's own arpeggio flavour — so no find-or-create runs for a
///         palette shape. Only "Repeat previous chord" may need one, and it asks
///         <see cref="ResolvedGripSource" /> (the chord's own template when it describes the
///         chord's frets, else <see cref="ChordTemplateResolver" /> in the chord's flavour).
///     </para>
/// </summary>
public static class ChordShapePlacement
{
    public const string PlaceLabelKey = "History_ChordShapes_Place";
    public const int MaxFret = 24;

    /// <summary>
    ///     Places <paramref name="template" /> at every one of <paramref name="positions" /> as one
    ///     undo node. <paramref name="minted" /> are templates a resolve just created for this
    ///     placement; they are added FIRST, before any chord points at them.
    /// </summary>
    public static ChordShapePlacementPlan Plan(
        ChordShapePlacementTarget target,
        ChordTemplate template,
        IReadOnlyCollection<FractionalPosition> positions,
        IReadOnlyList<ChordTemplate>? minted = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(positions);
        minted ??= [];

        var arrangement = target.Arrangement;
        var declared = ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, template)
                       || minted.Any(t => ReferenceEquals(t, template));
        if (!declared) return ChordShapePlacementPlan.Refused(ChordShapePlacementRefusal.NotInArrangement, template);
        if (ShapeRefusal(arrangement, template) is var refusal && refusal != ChordShapePlacementRefusal.None)
            return ChordShapePlacementPlan.Refused(refusal, template);

        var actions = new List<IChartAction>();
        if (minted.Count > 0) actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, minted));

        var created = 0;
        var reshaped = 0;
        foreach (var position in positions.Distinct())
        {
            var existing = target.Chords.FirstOrDefault(c => c.Position == position);
            var notes = target.Notes.Where(n => n.Position == position).ToList();
            if (existing != null)
            {
                if (notes.Count > 0) actions.Add(new RemoveNotesAction(target.Notes, notes, target.OnPostExecute));
                if (notes.Count == 0 && existing.TemplateId == template.Id && existing.PlaysExactly(template)) continue;

                actions.Add(new MutateChordsAction(target.Chords, [existing], [existing.Clone()],
                    [Reshaped(existing, template)], target.OnPostExecute));
                reshaped++;
                continue;
            }

            var chord = Promoted(position, notes, template);
            if (notes.Count > 0) actions.Add(new RemoveNotesAction(target.Notes, notes, target.OnPostExecute));
            var collisions = target.Collisions.CalculateChordCollisions([chord], target.Chords, []);
            if (collisions.ChordsToDestroy.Count > 0)
                actions.Add(new RemoveChordsAction(target.Chords, collisions.ChordsToDestroy, target.OnPostExecute));
            actions.Add(new AddChordsAction(target.Chords, [chord], target.OnPostExecute));
            created++;
        }

        if (created + reshaped == 0) return ChordShapePlacementPlan.Refused(ChordShapePlacementRefusal.SameShape, template);
        // A chord re-shaped or run over by the placement can leave its old shape with no user.
        return new ChordShapePlacementPlan(
            StrandedShapes.Wrap(arrangement, new CompositeAction(actions) { LabelKey = PlaceLabelKey }),
            ChordShapePlacementRefusal.None, template, created, reshaped);
    }

    /// <summary>
    ///     "Repeat previous chord": the shape of the nearest chord strictly before
    ///     <paramref name="position" />, placed AT it. The chord's own template when it describes
    ///     the chord's frets; otherwise the resolver's find-or-create in the chord's own arpeggio
    ///     flavour, minted inside the same undo node.
    /// </summary>
    public static ChordShapePlacementPlan PlanRepeatPrevious(ChordShapePlacementTarget target, FractionalPosition position)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (PreviousChord(target.Chords, position) is not { } previous || previous.ChordNotes.Count == 0)
            return ChordShapePlacementPlan.Refused(ChordShapePlacementRefusal.NoPreviousChord);

        var minted = new List<ChordTemplate>();
        var grip = new ResolvedGripSource(target.Arrangement).GripFor(previous, minted);
        return Plan(target, grip, [position], minted);
    }

    /// <summary>The chord with the greatest position strictly before <paramref name="position" />, or null.</summary>
    public static Chord? PreviousChord(IEnumerable<Chord> chords, FractionalPosition position)
    {
        ArgumentNullException.ThrowIfNull(chords);
        Chord? best = null;
        foreach (var chord in chords)
        {
            if (chord.Position < position && (best == null || chord.Position > best.Position))
                best = chord;
        }

        return best;
    }

    /// <summary>Why <paramref name="template" /> cannot be played on this arrangement's neck, or <see cref="ChordShapePlacementRefusal.None" />.</summary>
    public static ChordShapePlacementRefusal ShapeRefusal(Arrangement arrangement, ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(template);

        if (template.Frets.Count == 0) return ChordShapePlacementRefusal.EmptyShape;
        var capo = Math.Max(0, arrangement.Capo);
        foreach (var (stringIndex, fret) in template.Frets)
        {
            if (stringIndex < 0 || stringIndex >= arrangement.StringCount) return ChordShapePlacementRefusal.StringOutOfRange;
            if (fret is < 0 or > MaxFret) return ChordShapePlacementRefusal.OffNeck;
            if (SoundingPitch.SnapToNeck(fret, capo, 0, MaxFret) != fret) return ChordShapePlacementRefusal.BelowCapo;
        }

        return ChordShapePlacementRefusal.None;
    }

    /// <summary>A new chord at <paramref name="position" />: each string the shape plays, carrying the
    /// note that was already on that string (its techniques and sustain) at the shape's fret.</summary>
    private static Chord Promoted(FractionalPosition position, IReadOnlyList<GuitarNote> notes, ChordTemplate template)
    {
        var sustain = notes.Count == 0 ? FractionalPosition.Zero : notes.Max(n => n.Sustain);
        var chord = new Chord { Position = position, TemplateId = template.Id };
        if (notes.Count > 0)
        {
            chord.Accent = notes.Any(n => n.Accent);
            chord.Ignore = notes.All(n => n.Ignore);
        }

        foreach (var (stringIndex, fret) in template.Frets.OrderBy(f => f.Key))
        {
            var carried = notes.FirstOrDefault(n => n.StringIndex == stringIndex);
            var chordNote = carried != null
                ? SoundConversion.ToChordNote(carried)
                : new ChordNote { StringIndex = stringIndex, Sustain = sustain };
            chordNote.Fret = fret;
            chord.ChordNotes.Add(chordNote);
        }

        return chord;
    }

    /// <summary>The chord's new state: the shape's strings at the shape's frets, each string the chord
    /// already played keeping its techniques, and the chord pointing at the template.</summary>
    private static Chord Reshaped(Chord existing, ChordTemplate template)
    {
        var state = existing.Clone();
        var sustain = existing.ChordNotes.Count == 0 ? FractionalPosition.Zero : existing.ChordNotes.Max(n => n.Sustain);
        var kept = state.ChordNotes.ToList();
        state.ChordNotes.Clear();
        foreach (var (stringIndex, fret) in template.Frets.OrderBy(f => f.Key))
        {
            var chordNote = kept.FirstOrDefault(n => n.StringIndex == stringIndex)
                            ?? new ChordNote { StringIndex = stringIndex, Sustain = sustain };
            chordNote.Fret = fret;
            state.ChordNotes.Add(chordNote);
        }

        state.TemplateId = template.Id;
        return state;
    }

    private static bool PlaysExactly(this Chord chord, ChordTemplate template) =>
        template.HasFrets(ChordTemplateResolver.FretsOf(chord));
}
