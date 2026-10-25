using System.Collections.ObjectModel;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

internal sealed class CommitHighwayDragDropCommand : IHighwayEditCommand
{
    private readonly IStrictCollisionService _collisionService;
    private readonly List<IChartAction> _extraActions;
    private readonly Func<Chord, string> _formatChord;
    private readonly Func<FractionalPosition, string> _formatPosition;
    private readonly IAppLogger _logger;
    private readonly List<FretAnchor> _newAnchors;
    private readonly List<HandShape> _newHandShapes;
    private readonly Action? _onPostExecute;
    private readonly IEditingScope _scope;
    private readonly List<FretAnchor> _targetAnchors;
    private readonly List<Chord> _targetChords;
    private readonly List<HandShape> _targetHandShapes;
    private readonly List<GuitarNote> _targetNotes;
    private readonly IReadOnlyDictionary<int, ChordTemplate>? _incomingTemplates;
    private readonly List<ChordTemplate> _createdTemplates = [];
    private List<Chord> _newChords;
    private List<GuitarNote> _newNotes;

    public CommitHighwayDragDropCommand(
        IEditingScope scope,
        IStrictCollisionService collisionService,
        HighwayDragDropChange change,
        IEnumerable<IChartAction>? additionalActions,
        Action? onPostExecute,
        IAppLogger logger,
        Func<FractionalPosition, string> formatPosition,
        Func<Chord, string> formatChord,
        IReadOnlyDictionary<int, ChordTemplate>? incomingTemplates = null,
        IEnumerable<ChordTemplate>? mintedTemplates = null)
    {
        _scope = scope;
        _incomingTemplates = incomingTemplates;
        // Templates the CALLER resolved but did not add: they join the same
        // history node (inserted first, below), so an undo takes them back out.
        if (mintedTemplates != null) _createdTemplates.AddRange(mintedTemplates);
        _collisionService = collisionService;
        _targetNotes = change.TargetNotes.ToList();
        _newNotes = change.NewNotes.ToList();
        _targetChords = change.TargetChords.ToList();
        _newChords = change.NewChords.ToList();
        _targetHandShapes = change.TargetHandShapes.ToList();
        _newHandShapes = change.NewHandShapes.ToList();
        _targetAnchors = change.TargetAnchors.ToList();
        _newAnchors = change.NewAnchors.ToList();
        _extraActions = additionalActions?.Where(a => a.HasEffect).ToList() ?? [];
        _onPostExecute = onPostExecute;
        _logger = logger;
        _formatPosition = formatPosition;
        _formatChord = formatChord;

        RequestedNewNotes = _newNotes.Select(n => n.Clone()).ToList();
        RequestedNewChords = _newChords.Select(c => c.Clone()).ToList();
    }

    /// <summary>
    ///     The level's hand shapes while the chord / hand shape sync is on: a drop that merges notes into a
    ///     chord changes that chord's shape, and the bar over it follows onto the new template. Null leaves
    ///     every bar where it is.
    /// </summary>
    public ObservableCollection<HandShape>? SyncedHandShapes { get; init; }

    public IReadOnlyList<GuitarNote> TargetNotes => _targetNotes;
    public IReadOnlyList<GuitarNote> RequestedNewNotes { get; }
    public IReadOnlyList<GuitarNote> NewNotes => _newNotes;
    public IReadOnlyList<Chord> TargetChords => _targetChords;
    public IReadOnlyList<Chord> RequestedNewChords { get; }
    public IReadOnlyList<Chord> NewChords => _newChords;
    public IReadOnlyList<HandShape> NewHandShapes => _newHandShapes;
    public IReadOnlyList<FretAnchor> NewAnchors => _newAnchors;
    public int ActionCount { get; private set; }
    public int RemovedNotesCount { get; private set; }
    public int RemovedChordsCount { get; private set; }

    /// <summary>
    ///     Sounds destroyed for sitting under a hand shape's newly covered
    ///     ground. Counted separately because the drag/drop commit log reported
    ///     only the CALLER's removals for several releases — which is how a split
    ///     that deleted every note under it could log "removed notes=0".
    /// </summary>
    public int OverriddenSoundCount { get; private set; }

    /// <summary>Anchors destroyed for already sitting on an arriving anchor's beat.</summary>
    public int OverriddenAnchorCount { get; private set; }
    public bool WasStatePreserving { get; private set; }

    public bool Execute(IHistoryService history)
    {
        var action = BuildAction();
        return action != null && history.Do(action);
    }

    private IChartAction? BuildAction()
    {
        if (IsStatePreservingDragDrop())
        {
            WasStatePreserving = true;
            return null;
        }

        _logger.Info(AppLogCategory.Editing,
            $"Drag/drop commit requested. Target notes={_targetNotes.Count}, new notes={_newNotes.Count}, target chords={_targetChords.Count}, new chords={_newChords.Count}, target handshapes={_targetHandShapes.Count}, new handshapes={_newHandShapes.Count}, target anchors={_targetAnchors.Count}, new anchors={_newAnchors.Count}.");

        var actions = new List<IChartAction>();
        AddTargetRemovalActions(actions);

        RemapIncomingTemplates();

        var normalizedSounds = NormalizeDroppedSounds();
        _newNotes = normalizedSounds.NewNotes;
        _newChords = normalizedSounds.NewChords;

        AddNormalizedRemovalActions(actions, normalizedSounds);
        AddHandShapeCollisionActions(actions);
        AddMovedHandShapeOverrideActions(actions, normalizedSounds);
        AddSoundCollisionActions(actions, normalizedSounds);
        AddAnchorCollisionActions(actions);
        AddNewElementActions(actions);
        AddHandShapeFollowActions(actions, normalizedSounds);

        if (_extraActions.Count > 0) actions.AddRange(_extraActions);

        // Minted templates go in FIRST: every add/mutate action's post-execute
        // is the spacing enforcer, which resolves chord template ids.
        if (_createdTemplates.Count > 0 && _scope.Arrangement is { } arrangement)
            actions.Insert(0, new AddChordTemplatesAction(arrangement.ChordTemplates, _createdTemplates));

        ActionCount = actions.Count;
        RemovedNotesCount = _targetNotes.Count + normalizedSounds.NotesToRemove.Count;
        RemovedChordsCount = _targetChords.Count + normalizedSounds.ChordsToRemove.Count;

        if (actions.Count == 0) return null;

        // A template the drop leaves with no chord or hand shape (a replaced bar, a chord run over,
        // a merged chord's old shape) leaves in the same node.
        return StrandedShapes.Wrap(_scope.Arrangement,
            new CompositeAction(actions) { LabelKey = "History_MoveNotes" });
    }

    private void AddTargetRemovalActions(ICollection<IChartAction> actions)
    {
        if (_targetNotes.Count > 0)
            actions.Add(new RemoveNotesAction(_scope.Notes, _targetNotes, _onPostExecute));
        if (_targetChords.Count > 0)
            actions.Add(new RemoveChordsAction(_scope.Chords, _targetChords, _onPostExecute));
        if (_targetHandShapes.Count > 0)
            actions.Add(new RemoveHandShapesAction(_scope.HandShapes, _targetHandShapes, _onPostExecute));
        if (_targetAnchors.Count > 0)
            actions.Add(new RemoveAnchorsAction(_scope.Anchors, _targetAnchors));
    }

    private void AddNormalizedRemovalActions(ICollection<IChartAction> actions, DropSoundNormalization normalizedSounds)
    {
        if (normalizedSounds.NotesToRemove.Count > 0)
        {
            actions.Add(new RemoveNotesAction(_scope.Notes, normalizedSounds.NotesToRemove,
                _onPostExecute));
        }

        if (normalizedSounds.ChordsToRemove.Count > 0)
        {
            actions.Add(new RemoveChordsAction(_scope.Chords, normalizedSounds.ChordsToRemove,
                _onPostExecute));
        }
    }

    private void AddHandShapeCollisionActions(ICollection<IChartAction> actions)
    {
        var collisions = _collisionService.CalculateHandShapeOverride(
            _newHandShapes, _scope.HandShapes, _targetHandShapes);

        if (collisions.NotesToDestroy.Count > 0)
            actions.Add(new RemoveNotesAction(_scope.Notes, collisions.NotesToDestroy, _onPostExecute));
        if (collisions.ChordsToDestroy.Count > 0)
        {
            actions.Add(new RemoveChordsAction(_scope.Chords, collisions.ChordsToDestroy,
                _onPostExecute));
        }

        if (collisions.HandShapesToDestroy.Count > 0)
        {
            actions.Add(new RemoveHandShapesAction(_scope.HandShapes, collisions.HandShapesToDestroy,
                _onPostExecute));
        }
    }

    /// <summary>
    ///     Dropping a hand shape onto occupied ground destroys what is under it.
    ///     The shape carries its own sounds — they arrive as target/new pairs and
    ///     are excluded here — so whatever is left in the landing zone loses.
    ///     <para>
    ///         Only ground the shape did NOT already cover counts. A sound that
    ///         already sat inside one of the shapes being REPLACED is not being
    ///         run over; it is that shape's own content, and a caller replacing a
    ///         span in place has no reason to hand its sounds back. Splitting a
    ///         shape does exactly that — one span becomes two spans inside it —
    ///         so without this test every note under both halves was deleted, in
    ///         silence, with the shapes themselves landing correctly. Both split
    ///         entry points shipped that: the SplitHandShape verb and
    ///         Ctrl+right-click on the highway.
    ///     </para>
    /// </summary>
    private void AddMovedHandShapeOverrideActions(
        ICollection<IChartAction> actions,
        DropSoundNormalization normalizedSounds)
    {
        if (_targetHandShapes.Count == 0) return;

        var newNoteSet = _newNotes.ToHashSet();
        var newChordSet = _newChords.ToHashSet();

        var notesInMovedHandShapes = _scope.Notes.Where(n =>
            !_targetNotes.Contains(n) &&
            !normalizedSounds.NotesToRemove.Contains(n) &&
            !newNoteSet.Contains(n) &&
            IsNewlyCovered(n.Position)).ToList();
        if (notesInMovedHandShapes.Count > 0)
            actions.Add(new RemoveNotesAction(_scope.Notes, notesInMovedHandShapes, _onPostExecute));

        var chordsInMovedHandShapes = _scope.Chords.Where(c =>
            !_targetChords.Contains(c) &&
            !normalizedSounds.ChordsToRemove.Contains(c) &&
            !newChordSet.Contains(c) &&
            IsNewlyCovered(c.Position)).ToList();
        if (chordsInMovedHandShapes.Count > 0)
            actions.Add(new RemoveChordsAction(_scope.Chords, chordsInMovedHandShapes, _onPostExecute));

        OverriddenSoundCount = notesInMovedHandShapes.Count + chordsInMovedHandShapes.Count;
    }

    /// <summary>
    ///     True when <paramref name="position" /> lands under one of the incoming
    ///     hand shapes and under none of the ones they replace. Half-open on both
    ///     sides, so a shape's own end position belongs to whatever follows it.
    /// </summary>
    private bool IsNewlyCovered(FractionalPosition position)
    {
        return Covers(_newHandShapes, position) && !Covers(_targetHandShapes, position);

        static bool Covers(List<HandShape> shapes, FractionalPosition p)
            => shapes.Any(hs => p >= hs.Position && p < hs.EndPosition);
    }

    private void AddSoundCollisionActions(ICollection<IChartAction> actions, DropSoundNormalization normalizedSounds)
    {
        var ignoredNotes = _targetNotes.Concat(normalizedSounds.NotesToRemove).ToList();
        var noteCollisions =
            _collisionService.CalculateNoteCollisions(_newNotes, _scope.Notes, ignoredNotes);
        if (noteCollisions.NotesToDestroy.Count > 0)
        {
            actions.Add(new RemoveNotesAction(_scope.Notes, noteCollisions.NotesToDestroy,
                _onPostExecute));
        }

        var ignoredChords = _targetChords.Concat(normalizedSounds.ChordsToRemove).ToList();
        var chordCollisions =
            _collisionService.CalculateChordCollisions(_newChords, _scope.Chords, ignoredChords);
        if (chordCollisions.ChordsToDestroy.Count > 0)
        {
            actions.Add(new RemoveChordsAction(_scope.Chords, chordCollisions.ChordsToDestroy,
                _onPostExecute));
        }
    }

    /// <summary>
    ///     An arriving FHP anchor destroys any anchor already on its beat.
    ///     <para>
    ///         One beat holds ONE fret-hand position. Notes, chords and hand
    ///         shapes each had a collision rule here from the start; anchors did
    ///         not, so every drop that carried them — a paste above all, which
    ///         brings a whole run at once — simply stacked them on whatever the
    ///         destination already had. Nothing failed at the time: the duplicate
    ///         surfaced later as inspector "Two anchors share the same position",
    ///         once per pasted anchor, and the only fix was deleting each by
    ///         hand.
    ///     </para>
    /// </summary>
    private void AddAnchorCollisionActions(ICollection<IChartAction> actions)
    {
        if (_newAnchors.Count == 0) return;

        var collisions = _collisionService.CalculateAnchorCollisions(_newAnchors, _scope.Anchors, _targetAnchors);
        if (collisions.AnchorsToDestroy.Count == 0) return;

        OverriddenAnchorCount = collisions.AnchorsToDestroy.Count;
        actions.Add(new RemoveAnchorsAction(_scope.Anchors, collisions.AnchorsToDestroy));
    }

    private void AddNewElementActions(ICollection<IChartAction> actions)
    {
        if (_newNotes.Count > 0)
            actions.Add(new AddNotesAction(_scope.Notes, _newNotes, _onPostExecute));
        if (_newChords.Count > 0)
            actions.Add(new AddChordsAction(_scope.Chords, _newChords, _onPostExecute));
        if (_newHandShapes.Count > 0)
            actions.Add(new AddHandShapesAction(_scope.HandShapes, _newHandShapes, _onPostExecute));
        if (_newAnchors.Count > 0)
            actions.Add(new AddAnchorsAction(_scope.Anchors, _newAnchors));
    }

    /// <summary>
    ///     A chord that a drop merged notes into sits on a new template; the hand shapes that were in step
    ///     with it move there too. A bar the same drop replaces is the caller's own, and is left to it.
    /// </summary>
    private void AddHandShapeFollowActions(ICollection<IChartAction> actions, DropSoundNormalization normalizedSounds)
    {
        if (SyncedHandShapes is not { } handShapes || normalizedSounds.TemplateMoves.Count == 0) return;

        var removed = _targetChords.ToHashSet();
        var bars = handShapes.Where(h => !_targetHandShapes.Contains(h)).ToList();
        var follow = ChordHandShapeSync.BuildAction(handShapes,
            ChordHandShapeSync.Follow(bars, _scope.Chords.Where(c => !removed.Contains(c)), normalizedSounds.TemplateMoves),
            _onPostExecute);
        if (follow != null) actions.Add(follow);
    }

    /// <summary>
    ///     Repoints incoming chords / hand shapes at a template of the TARGET
    ///     arrangement with the same shape, minting one when it has none.
    ///     <para>
    ///         The clipboard carries raw <c>TemplateId</c>s, and template ids are
    ///         only meaningful inside their own arrangement: copying a Dmin barre
    ///         from Lead (id 7) and pasting it into Rhythm, where id 7 is an open
    ///         E5, made the pasted chord render and export as E5 with Dmin frets
    ///         — and if Rhythm had fewer than 8 templates the id dangled instead.
    ///         A same-arrangement paste resolves back onto the very template it
    ///         came from, so it stays a no-op.
    ///     </para>
    /// </summary>
    private void RemapIncomingTemplates()
    {
        if (_incomingTemplates is not { Count: > 0 } incoming) return;
        if (_scope.Arrangement is not { } arrangement) return;

        foreach (var chord in _newChords)
        {
            if (!incoming.TryGetValue(chord.TemplateId, out var source)) continue;
            chord.TemplateId = ChordTemplateResolver
                .Resolve(arrangement, source.Frets, ArpeggioFlavour.Of(source), source.Name, _createdTemplates).Id;
        }

        foreach (var handShape in _newHandShapes)
        {
            if (!incoming.TryGetValue(handShape.TemplateId, out var source)) continue;
            handShape.TemplateId = ChordTemplateResolver
                .Resolve(arrangement, source.Frets, ArpeggioFlavour.Of(source), source.Name, _createdTemplates).Id;
        }
    }

    private bool IsStatePreservingDragDrop()
    {
        return _extraActions.Count == 0 &&
               SameStates(_targetNotes, _newNotes, ChartActionStateComparer.GuitarNotesEqual) &&
               SameStates(_targetChords, _newChords, ChartActionStateComparer.ChordsEqual) &&
               SameStates(_targetHandShapes, _newHandShapes, ChartActionStateComparer.HandShapesEqual) &&
               SameStates(_targetAnchors, _newAnchors, ChartActionStateComparer.AnchorsEqual);
    }

    private DropSoundNormalization NormalizeDroppedSounds()
    {
        var result = new DropSoundNormalization();
        var targetNoteSet = _targetNotes.ToHashSet();
        var targetChordSet = _targetChords.ToHashSet();
        var positions = _newNotes.Select(n => n.Position)
            .Concat(_newChords.Select(c => c.Position))
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        foreach (var position in positions)
        {
            var incomingNotes = _newNotes.Where(n => n.Position == position).ToList();
            var incomingChords = _newChords.Where(c => c.Position == position).ToList();
            var existingNotes = _scope.Notes
                .Where(n => !targetNoteSet.Contains(n) && n.Position == position)
                .ToList();
            var existingChords = _scope.Chords
                .Where(c => !targetChordSet.Contains(c) && c.Position == position)
                .ToList();

            var stringsAtPosition = new HashSet<int>();
            foreach (var note in incomingNotes.Concat(existingNotes)) stringsAtPosition.Add(note.StringIndex);
            foreach (var chord in incomingChords.Concat(existingChords))
            {
                foreach (var chordNote in chord.ChordNotes)
                    stringsAtPosition.Add(chordNote.StringIndex);
            }

            var hasChordAtPosition = incomingChords.Count > 0 || existingChords.Count > 0;
            var shouldCreateChord = hasChordAtPosition ||
                                    (stringsAtPosition.Count > 1 && incomingNotes.Count + existingNotes.Count > 1);
            if (!shouldCreateChord)
            {
                foreach (var note in incomingNotes.GroupBy(n => n.StringIndex).Select(g => g.Last()))
                    result.NewNotes.Add(note);
                continue;
            }

            var mergedChord = incomingChords.FirstOrDefault()?.Clone()
                              ?? existingChords.FirstOrDefault()?.Clone()
                              ?? new Chord { Position = position, TemplateId = -1 };
            mergedChord.Position = position;
            mergedChord.ChordNotes.Clear();

            var chordNotesByString = new Dictionary<int, ChordNote>();
            foreach (var chord in existingChords)
            {
                foreach (var chordNote in chord.ChordNotes)
                    chordNotesByString[chordNote.StringIndex] = chordNote.Clone();
                result.ChordsToRemove.Add(chord);
            }

            foreach (var note in existingNotes)
            {
                chordNotesByString[note.StringIndex] = CreateChordNote(note);
                result.NotesToRemove.Add(note);
            }

            foreach (var chord in incomingChords)
            {
                foreach (var chordNote in chord.ChordNotes)
                    chordNotesByString[chordNote.StringIndex] = chordNote.Clone();
            }

            foreach (var note in incomingNotes)
                chordNotesByString[note.StringIndex] = CreateChordNote(note);

            foreach (var chordNote in chordNotesByString.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value))
                mergedChord.ChordNotes.Add(chordNote);

            if (incomingChords.Count == 0 && existingChords.Count == 0)
            {
                mergedChord.Accent = incomingNotes.Concat(existingNotes).Any(n => n.Accent);
                mergedChord.Ignore = incomingNotes.Concat(existingNotes).Any(n => n.Ignore);
                mergedChord.PassOtherNotes = incomingNotes.Concat(existingNotes).Any(n => n.PassOtherNotes);
            }
            else if (incomingNotes.Concat(existingNotes).Any(n => n.PassOtherNotes))
            {
                mergedChord.PassOtherNotes = true;
            }

            // The merge REBUILT the chord's note set from the survivors plus the
            // dropped notes, but kept the cloned TemplateId — so the chord and
            // its SHARED template described different shapes, and every other
            // chord on that template still drew (and exported) the old one.
            // Resolve-or-create for the shape it now plays; an untemplated chord
            // stays untemplated, it has nothing to disagree with.
            ResolveMergedTemplate(mergedChord, existingChords, result);

            result.NewChords.Add(mergedChord);
            _logger.Info(AppLogCategory.Editing,
                $"Normalized dropped sounds into chord at {_formatPosition(position)}. IncomingNotes={incomingNotes.Count}, incomingChords={incomingChords.Count}, existingNotes={existingNotes.Count}, existingChords={existingChords.Count}, result={_formatChord(mergedChord)}.");
        }

        result.NotesToRemove = result.NotesToRemove.Distinct().ToList();
        result.ChordsToRemove = result.ChordsToRemove.Distinct().ToList();
        return result;
    }

    /// <summary>
    ///     Repoints the merged chord at a template of the shape it now plays, and notes which chord it replaced
    ///     so the hand shape over that chord can follow (<see cref="AddHandShapeFollowActions" />).
    /// </summary>
    private void ResolveMergedTemplate(Chord mergedChord, List<Chord> existingChords, DropSoundNormalization result)
    {
        if (mergedChord.TemplateId < 0 || _scope.Arrangement is not { } arrangement) return;

        mergedChord.TemplateId = ChordTemplateResolver.ResolveForChord(arrangement, mergedChord, _createdTemplates).Id;
        if (existingChords.FirstOrDefault(c => c.TemplateId >= 0) is not { } replaced) return;

        result.TemplateMoves.Add(new ChordHandShapeSync.ChordMove(replaced, replaced.TemplateId, mergedChord.TemplateId));
    }

    /// <summary>One converter for all three note-to-chord-note sites - this one
    /// additionally carries Accent, Ignore and GraceFret, which a hand-written copy would drop.</summary>
    private static ChordNote CreateChordNote(GuitarNote note) => SoundConversion.ToChordNote(note);

    private static bool SameStates<T>(IReadOnlyList<T> targets, IReadOnlyList<T> newStates, Func<T, T, bool> equals)
    {
        if (targets.Count != newStates.Count) return false;
        for (var i = 0; i < targets.Count; i++)
        {
            if (!equals(targets[i], newStates[i]))
                return false;
        }

        return true;
    }

    private sealed class DropSoundNormalization
    {
        public List<GuitarNote> NewNotes { get; } = [];
        public List<Chord> NewChords { get; } = [];
        public List<GuitarNote> NotesToRemove { get; set; } = [];
        public List<Chord> ChordsToRemove { get; set; } = [];

        /// <summary>The chords a merge replaced, with the template each one's replacement sits on.</summary>
        public List<ChordHandShapeSync.ChordMove> TemplateMoves { get; } = [];
    }
}
