using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordTemplate = DotCharter.Core.Domain.Guitar.ChordTemplate;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     One keyboard mutation verb applied to the whole selection — notes AND
///     chords — as ONE history entry.
///     <para>
///         Chords carry three extra obligations a note does not. (1) Their per-
///         string state lives on <c>ChordNote</c>s, so a per-string verb is
///         filtered by the <see cref="StringEditMask" /> while a whole-chord verb
///         writes the <c>Chord</c> itself. (2) A shape change (fret or string)
///         must resolve-or-create a <c>ChordTemplate</c> and repoint
///         <c>TemplateId</c> — templates are SHARED, and an rscp
///         <c>&lt;chordNote&gt;</c> has no <c>fret</c> attribute, so a fret
///         written without repointing is silently discarded on save. (3) The hand shape over a
///         repointed chord follows it onto the new template in the same history entry
///         (<see cref="ChordHandShapeSync" />) while the sync is on.
///     </para>
/// </summary>
internal sealed class SelectedNoteMutationCommand : IHighwayEditCommand
{
    private readonly string _mergeVerb;
    private readonly Action<Chord>? _mutateChord;
    private readonly Action<GuitarNote>? _mutateNote;
    private readonly SelectedSoundMutationTargets _targets;

    /// <summary>
    ///     <c>mergeVerb</c> names the gesture when this verb REPEATS under a held
    ///     key — see <see cref="MergeVerbs" />. Empty (the default) means "never
    ///     merge", which is right for every one-shot toggle: cycling HOPO three
    ///     times is three decisions, not one gesture.
    /// </summary>
    public SelectedNoteMutationCommand(
        SelectedSoundMutationTargets targets,
        Action<GuitarNote>? mutateNote,
        Action<Chord>? mutateChord,
        string mergeVerb = "")
    {
        _targets = targets;
        _mutateNote = mutateNote;
        _mutateChord = mutateChord;
        _mergeVerb = mergeVerb;
    }

    public bool Execute(IHistoryService history)
    {
        var notes = _targets.MaskedNotes();
        var chords = _targets.TargetChords();
        if (notes.Count == 0 && chords.Count == 0) return false;

        var actions = new List<IChartAction>();
        var createdTemplates = new List<ChordTemplate>();

        if (notes.Count > 0 && _mutateNote != null)
            actions.Add(BuildNoteMutation(notes));

        // The "Apply to" ladder took a technique verb past the selection: the inspector's
        // own per-level write, so the same chords change and one undo puts every level back.
        if (chords.Count > 0 && _mutateChord != null && _targets.Ladder is { } ladder)
        {
            actions.AddRange(ChordTechniqueEdit.BuildPerLevel(ladder.Levels, _targets.Chords, chords, _mutateChord,
                _targets.OnPostExecute, ladder.LevelPostPass));
        }
        else if (chords.Count > 0 && _mutateChord != null)
        {
            AddChordMutation(actions, chords, createdTemplates);
        }

        // Minted templates go in FIRST: MutateChordsAction's post-execute is the
        // spacing enforcer, which resolves chord template ids — the same
        // ordering EditChordStringCommand and CommitHighwayDragDropCommand use.
        //
        // It also, deliberately, makes the composite UNMERGEABLE (CompositeAction
        // only reports a verb when EVERY child carries the same one, and
        // AddChordTemplatesAction carries none). That is correct, not a gap: the
        // head of a merged run would still hold only the FIRST tick's minted
        // template while its stored new-states point at the LAST tick's, so redo
        // would repoint a chord at a template that was never added — precisely
        // the "a half-merged composite describes a state that never existed"
        // failure CanAbsorb/Absorb are split to prevent. A held fret run over a
        // TEMPLATED chord therefore still records one node per repeat; making it
        // one needs an accumulating template action, which is a separate change.
        if (createdTemplates.Count > 0 && _targets.Arrangement is { } arrangement)
            actions.Insert(0, new AddChordTemplatesAction(arrangement.ChordTemplates, createdTemplates));

        if (actions.Count == 0) return false;
        // A fret or string change can leave the chord's old template with no user: it leaves in the same node.
        return history.Do(StrandedShapes.Wrap(_targets.Arrangement,
            actions.Count == 1 ? actions[0] : new CompositeAction(actions)));
    }

    /// <summary>
    ///     Builds the note half with explicit old/new clones rather than through
    ///     the mutate-fn overload, because only the explicit one takes a merge
    ///     verb. The clones it produces are what that overload builds internally
    ///     anyway, so this is the same action either way.
    /// </summary>
    private MutateNotesAction BuildNoteMutation(IReadOnlyList<GuitarNote> notes)
    {
        var oldStates = new List<GuitarNote>(notes.Count);
        var newStates = new List<GuitarNote>(notes.Count);

        foreach (var note in notes)
        {
            oldStates.Add(note.Clone());
            var newState = note.Clone();
            _mutateNote!(newState);
            newStates.Add(newState);
        }

        return new MutateNotesAction(_targets.Notes, notes, oldStates, newStates,
            _targets.OnPostExecute, _mergeVerb);
    }

    private void AddChordMutation(ICollection<IChartAction> actions, List<Chord> chords,
        IList<ChordTemplate> createdTemplates)
    {
        var oldStates = new List<Chord>(chords.Count);
        var newStates = new List<Chord>(chords.Count);
        var moves = new List<ChordHandShapeSync.ChordMove>();

        for (var i = 0; i < chords.Count; i++)
        {
            var oldState = chords[i].Clone();
            var newState = chords[i].Clone();
            _mutateChord!(newState);

            // Resolve-or-create for the shape it now plays, never a mutation of
            // the shared template. An untemplated chord (TemplateId -1) stays
            // untemplated; a null arrangement (headless/paste harness) is left
            // alone rather than minting into nothing.
            if (_targets.Arrangement is { } arrangement && chords[i].TemplateId >= 0 &&
                ShapeChanged(oldState, newState))
            {
                newState.TemplateId = ChordTemplateResolver
                    .ResolveForChord(arrangement, newState, createdTemplates).Id;
                moves.Add(new ChordHandShapeSync.ChordMove(chords[i], chords[i].TemplateId, newState.TemplateId));
            }

            oldStates.Add(oldState);
            newStates.Add(newState);
        }

        actions.Add(new MutateChordsAction(_targets.Chords, chords, oldStates, newStates,
            _targets.OnPostExecute, _mergeVerb));

        // The hand shapes over the chords follow them onto the new templates, in the same undo node.
        if (_targets.SyncedHandShapes is { } handShapes && moves.Count > 0)
        {
            var follow = ChordHandShapeSync.BuildAction(handShapes,
                ChordHandShapeSync.Follow(handShapes, _targets.Chords, moves), _targets.OnPostExecute, _mergeVerb);
            if (follow != null) actions.Add(follow);
        }
    }

    /// <summary>Did the (string → fret) set change? That — not any technique
    /// flag — is what makes the chord's template wrong.</summary>
    private static bool ShapeChanged(Chord before, Chord after)
    {
        if (before.ChordNotes.Count != after.ChordNotes.Count) return true;

        var beforeFrets = ChordTemplateResolver.FretsOf(before);
        var afterFrets = ChordTemplateResolver.FretsOf(after);
        if (beforeFrets.Count != afterFrets.Count) return true;

        foreach (var (stringIndex, fret) in afterFrets)
        {
            if (!beforeFrets.TryGetValue(stringIndex, out var previous) || previous != fret)
                return true;
        }

        return false;
    }
}
