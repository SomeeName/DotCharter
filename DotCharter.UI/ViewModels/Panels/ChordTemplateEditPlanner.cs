using System.Collections.ObjectModel;
using DotCharter.Services.Editing.Anchors;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     The write half of the chord template panel: it executes the target set
///     <see cref="ChordApplyScope.Resolve" /> answers for the rung the "Apply to"
/// ladder shows — REBIND the listed sounds onto a find-or-created
///     template, or rewrite the loaded template IN PLACE for every user of it —
///     and commits the whole gesture, minted templates and the "same grip"
///     templates included, as ONE undo node. A <c>ChordTemplate</c> is shared and
///     arrangement-scoped, so every path here resolves-or-creates instead of
///     mutating in place, except the sanctioned in-place edits: every use of the
///     shape, and a fingering or name copied onto a template of the same grip,
///     which moves no fret (editing-invariants.md).
/// </summary>
internal sealed class ChordTemplateEditPlanner
{
    private readonly ChartEditorViewModel _editor;
    private readonly IHistoryService _historyService;
    private readonly IFingeringGenerationService _fingeringService;
    private readonly IConfigurationService? _configService;
    private readonly IAppLogger? _logger;

    public ChordTemplateEditPlanner(ChartEditorViewModel editor, IHistoryService historyService,
        IFingeringGenerationService fingeringService, IConfigurationService? configService, IAppLogger? logger)
    {
        _editor = editor;
        _historyService = historyService;
        _fingeringService = fingeringService;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>What kind of edit turns <paramref name="before" /> into
    /// <paramref name="after" />. A fret move wins: it is a different shape,
    /// whatever else changed with it.</summary>
    internal static ChordTemplateEditKind KindOf(ChordTemplate before, ChordTemplate after)
    {
        if (!FingersDictHelper.Equal(before.Frets, after.Frets)) return ChordTemplateEditKind.Shape;
        if (before.Arpeggio != after.Arpeggio || before.ForceArpeggioInRs != after.ForceArpeggioInRs)
            return ChordTemplateEditKind.Arpeggio;
        if (before.NopInRs != after.NopInRs) return ChordTemplateEditKind.Nop;
        // Before the fingers: turning Fingerless on clears them too.
        if (before.Fingerless != after.Fingerless) return ChordTemplateEditKind.Fingerless;
        return FingersDictHelper.Equal(before.Fingers, after.Fingers)
            ? ChordTemplateEditKind.Name
            : ChordTemplateEditKind.Fingering;
    }

    /// <summary>
    ///     What an edit of <paramref name="loaded" /> reaches: the scope the
    ///     ladder prints and <see cref="ApplyEdit" /> writes — one object, so the
    /// words and the write cannot drift apart. Null without an
    ///     arrangement.
    /// </summary>
    internal ChordApplyScope? ResolveReach(ChordTemplate loaded, ChordApplySounds thisSounds, ChordApplySounds selected)
    {
        var arr = _editor.ActiveArrangement;
        return arr == null
            ? null
            : ChordApplyScope.Prepare(arr, loaded, thisSounds, selected, _editor.PlacedChords.ToList());
    }

    /// <summary>
    ///     Applies <paramref name="newState" /> to the loaded template on
    ///     <paramref name="rung" /> and answers the template the panel shows next
    ///     with the targets it reached, or null when the edit was refused (nothing
    ///     written, no history node). <paramref name="kind" /> overrides the kind
    ///     read off the two states: a Save that re-applies an unchanged fingering
    ///     to the same grip elsewhere is still a fingering edit.
    /// </summary>
    internal ChordApplyResult? ApplyEdit(ChordTemplate loaded, ChordTemplate newState, ChordApplyScope scope,
        ChordApplyRung rung, ChordTemplateEditKind? kind = null)
    {
        var arr = _editor.ActiveArrangement;
        if (arr == null) return null;

        var oldState = loaded.Clone();
        var editKind = kind ?? KindOf(oldState, newState);
        // A fingering, Fingerless or fret edit is the charter's grip: the FHP
        // generator may not move it. A name, arpeggio or Nop
        // edit keeps whoever made the fingering.
        if (editKind is ChordTemplateEditKind.Fingering or ChordTemplateEditKind.Fingerless or ChordTemplateEditKind.Shape)
        {
            newState.FollowedFingering = false;
            // ...and AUTHORED: no automatic flow re-fingers or replaces it (IsPinned).
            newState.Pinned = true;
        }
        // A grip with a finger on it is not deliberately fingerless.
        if (newState.Fingers.Count > 0) newState.Fingerless = false;
        var targets = scope.Resolve(rung, editKind, newState);
        // The grips are written from newState: a reused template is identical
        // to it, and a minted one is it. Only the ones that CHANGE are reported.
        var gripActions = GripActions(newState, targets, out var changedGrips);
        targets = scope.WithGrips(targets, changedGrips);
        var shown = targets.Path switch
        {
            ChordApplyPath.Rebind => ReconcileSelectionToTemplate(loaded, newState, targets, arr, gripActions),
            ChordApplyPath.InPlace => EditInPlace(loaded, oldState, newState, targets, gripActions),
            _ => null
        };
        return shown == null ? null : new ChordApplyResult(shown, targets);
    }

    /// <summary>
    ///     The in-place edit: the loaded template becomes <paramref name="newState" />
    ///     and every chord bound to it, on EVERY level, is re-projected onto it.
    ///     <para>
    ///         Every level, not <c>_editor.PlacedChords</c> — that is the ACTIVE
    ///         level's collection, and a ChordTemplate is ARRANGEMENT-scoped.
    ///         Rewriting the shared template in place while re-projecting notes
    ///         onto ONE level's chords would leave every other level's chords with old
    ///         notes under a template that does not describe them.
    ///     </para>
    /// </summary>
    private ChordTemplate EditInPlace(ChordTemplate loaded, ChordTemplate oldState, ChordTemplate newState,
        ChordApplyTargets targets, List<IChartAction> gripActions)
    {
        if (JoinTarget(loaded, newState, targets) is { } joined) return JoinExisting(loaded, joined, targets);
        var targetChords = targets.BoundChords.ToList();
        var globalChords = targetChords.Select(c =>
        {
            var nc = c.Clone();
            nc.ChordNotes = ProjectNotesOntoTemplate(nc, newState);
            return nc;
        }).ToList();

        // A NAME-only edit can arrive one keystroke at a time, so it names the
        // merge verb and the history folds the run into one undo node. A
        // shape/fingering/arpeggio edit is a discrete gesture and stays its own.
        var nameOnly = targets.Kind == ChordTemplateEditKind.Name && targets.GripTemplates.Count == 0
                       && oldState.Unnamed == newState.Unnamed
                       && !string.Equals(oldState.Name, newState.Name, StringComparison.Ordinal);
        var actions = new List<IChartAction>
        {
            new EditTemplateGlobalAction(loaded, oldState, newState, targetChords, globalChords, _editor,
                nameOnly ? EditTemplateGlobalAction.NameMergeVerb : "")
        };

        // Sounds that carry the shape but are not bound to it — selected
        // chords at TemplateId -1, the unbound strums under a selected hand
        // shape, sounds on another template with these frets — join it.
        var joinChords = targets.Sounds.Chords.ToList();
        var joinHandShapes = targets.Sounds.HandShapes.ToList();
        if (joinChords.Count > 0 || joinHandShapes.Count > 0)
        {
            var boundChords = joinChords.Select(c =>
            {
                var nc = c.Clone();
                nc.TemplateId = loaded.Id;
                nc.ChordNotes = ProjectNotesOntoTemplate(nc, newState);
                return nc;
            }).ToList();
            var boundHandShapes = joinHandShapes.Select(h =>
            {
                var nh = h.Clone();
                nh.TemplateId = loaded.Id;
                return nh;
            }).ToList();
            actions.Add(new SwitchTemplateAction(joinChords, boundChords, joinHandShapes, boundHandShapes, _editor));
        }

        // The ANCHOR sync stays on the active level: `_editor.Anchors` is that
        // level's list, and moving it for a chord on level 3 would move a hand
        // the charter is not looking at.
        var activeLevelChords = _editor.PlacedChords.Where(c => c.TemplateId == loaded.Id).ToList();
        activeLevelChords.AddRange(joinChords.Where(c => _editor.PlacedChords.Contains(c)));
        actions.AddRange(AnchorSyncActions(newState, activeLevelChords));
        actions.AddRange(gripActions);

        _logger?.Info(AppLogCategory.Editing,
            $"Chord shape '{Show(oldState.Name)}' edited in place ({targets.Rung}) for {targets.ChordCount} chord(s) " +
            $"on {targets.ShapeCount} template(s): {ChordTemplateDiff.Describe(oldState, newState)}.");
        // Reload the REAL template, which Execute rewrites in place — never the
        // clone, or the next keystroke of a rename fails `templateIsReal` and
        // mints a template per character.
        _historyService.Do(StrandedShapes.Wrap(_editor.ActiveArrangement,
            actions.Count == 1 ? actions[0] : new CompositeAction(actions), _editor.RequestRedraw));
        return loaded;
    }

    /// <summary>
    ///     The existing shape an in-place fret or fingering edit would duplicate: the edit gives
    ///     <paramref name="loaded" /> exactly the frets and fingers another shape already has, so the
    ///     uses JOIN that shape instead of leaving two copies behind. Null for every other edit,
    ///     and when the edit also writes other grips.
    /// </summary>
    private ChordTemplate? JoinTarget(ChordTemplate loaded, ChordTemplate newState, ChordApplyTargets targets)
    {
        if (targets.Kind is not (ChordTemplateEditKind.Shape or ChordTemplateEditKind.Fingering)) return null;
        if (targets.GripTemplates.Count > 0 || _editor.ActiveArrangement is not { } arrangement) return null;
        return ShapeEditPlanner.FindMatch(arrangement, loaded, newState);
    }

    /// <summary>
    ///     Points every use of <paramref name="loaded" /> — the chords and hand shapes on every level,
    ///     and the sounds that join it — at <paramref name="target" />, which keeps its own name. The
    ///     emptied shape goes in the same undo node.
    /// </summary>
    private ChordTemplate JoinExisting(ChordTemplate loaded, ChordTemplate target, ChordApplyTargets targets)
    {
        var arrangement = _editor.ActiveArrangement!;
        var (boundChords, boundHandShapes) = ChordShapeEdits.UsesOf(arrangement, loaded);
        var chords = boundChords.Concat(targets.Sounds.Chords).Distinct(ReferenceEqualityComparer.Instance).Cast<Chord>().ToList();
        var handShapes = boundHandShapes.Concat(targets.Sounds.HandShapes).Distinct(ReferenceEqualityComparer.Instance).Cast<HandShape>().ToList();
        var moved = chords.Select(c =>
        {
            var clone = c.Clone();
            clone.TemplateId = target.Id;
            clone.ChordNotes = ProjectNotesOntoTemplate(clone, target);
            return clone;
        }).ToList();
        var movedHandShapes = handShapes.Select(h =>
        {
            var clone = h.Clone();
            clone.TemplateId = target.Id;
            return clone;
        }).ToList();
        var actions = new List<IChartAction> { new SwitchTemplateAction(chords, moved, handShapes, movedHandShapes, _editor) };
        actions.AddRange(AnchorSyncActions(target, chords.Where(c => _editor.PlacedChords.Contains(c)).ToList()));
        _logger?.Info(AppLogCategory.Editing,
            $"Chord shape '{Show(loaded.Name)}' edited into '{Show(target.Name)}': its {chords.Count} chord(s) and " +
            $"{handShapes.Count} hand shape(s) joined the existing shape.");
        _historyService.Do(StrandedShapes.Wrap(arrangement,
            new CompositeAction(actions) { LabelKey = ShapeEditPlanner.EditLabelKey }, _editor.RequestRedraw));
        return target;
    }

    /// <summary>
    ///     The "same grip" half of an edit: the fingering (or name) copied onto
    ///     every other template holding this grip, IN PLACE. That moves no fret,
    ///     so no chord's shape changes, and every chord bound to one of those
    ///     templates plays this very grip — which is why it is the sanctioned
    ///     exception to "never mutate a shared template". It OVERWRITES what is
    ///     there: the old propagation only filled templates still missing a
    ///     fingering, and only under "all"; this rung is chosen in plain sight.
    /// </summary>
    private List<IChartAction> GripActions(ChordTemplate newState, ChordApplyTargets targets,
        out List<ChordTemplate> changed)
    {
        var actions = new List<IChartAction>();
        changed = [];
        foreach (var grip in targets.GripTemplates)
        {
            if (targets.Kind == ChordTemplateEditKind.Name)
            {
                var rename = new SetTemplateNameAction(grip, grip.Name, newState.Name, _editor, newState.Unnamed);
                if (!rename.HasEffect) continue;
                actions.Add(rename);
                changed.Add(grip);
                continue;
            }

            var fingers = new Dictionary<int, int>();
            foreach (var (stringIndex, finger) in newState.Fingers)
            {
                if (grip.Frets.ContainsKey(stringIndex)) fingers[stringIndex] = finger;
            }

            var refinger = new SetTemplateFingersAction(grip, new Dictionary<int, int>(grip.Fingers), fingers, _editor);
            if (!refinger.HasEffect) continue;
            actions.Add(refinger);
            changed.Add(grip);
        }

        return actions;
    }

    /// <summary>
    /// Binds ONLY the target sounds to a template that captures
    /// <paramref name="newState"/> — reusing an existing template with the same
    /// shape (so a chord and the handshape enclosing it keep sharing ONE
    /// template) or minting a new one, in the same undo node. Used for the
    /// "this chord" and "selected" rungs on a shape someone else also uses, and
    /// whenever the targets are not cleanly bound to the loaded template (an
    /// untemplated freshly-imported chord, or a chord whose notes drifted from
    /// its template), so the edit never rewrites unrelated chords.
    /// Reconciliation is deferred to the first edit (not selection) so browsing
    /// untemplated chords stays side-effect-free.
    /// </summary>
    private ChordTemplate? ReconcileSelectionToTemplate(ChordTemplate loaded, ChordTemplate newState,
        ChordApplyTargets targets, Arrangement arr, List<IChartAction> gripActions)
    {
        var selectedChords = targets.Sounds.Chords.ToList();
        var selectedHandShapes = targets.Sounds.HandShapes.ToList();
        if (selectedChords.Count == 0 && selectedHandShapes.Count == 0)
        {
            return null;
        }

        // WHICH KEY. A SHAPE change resolves on FRETS: the destination shape
        // already exists as a template more often than not, and its authored
        // name + fingering are more trustworthy than the ones dragged along
        // from the shape being left behind. That is the documented
        // find-or-create key and it is what keeps a chart from growing a
        // template per edit.
        //
        // A FINGERING / NAME / ARPEGGIO edit cannot use it. The frets did not
        // move, so a frets-only lookup returns the very template the edit came
        // from — `isNew` goes false, the "edit" degrades to a SwitchTemplate
        // back onto the same shape, and the new fingering is silently dropped.
        // That is the owner's "I have to click save fingering twice" verbatim.
        // Those edits resolve on the WHOLE authored state instead: reuse a
        // template that already carries exactly this fingering/name (so
        // re-fingering N siblings one at a time converges on one template),
        // otherwise mint. Never re-finger a shared template behind the user's
        // back — that is what the "every use" rung is for.
        var shapeUnchanged = targets.Kind != ChordTemplateEditKind.Shape;
        var equiv = shapeUnchanged
            ? _fingeringService.FindIdenticalTemplate(arr, newState)
            : newState.IsArpeggio
                ? null
                : _fingeringService.FindEquivalentTemplate(arr, newState);

        if (!shapeUnchanged && equiv != null) equiv = PreferGripTheAnchorAgreesWith(arr, equiv, selectedChords, selectedHandShapes);

        var isNew = equiv == null;
        var target = equiv ?? newState;
        if (isNew) target.Id = _fingeringService.NextTemplateId(arr);
        // A MINTED reshape is a different chord: name it for its new frets. The
        // old name describes the shape being left behind, and the isolate branch
        // must not append "(Isolated)" to it — a name that would then show on the
        // highway and in the export. A reused template keeps its own.
        if (isNew && !shapeUnchanged)
        {
            target.Name = NewChordNames.For(arr, target.Frets);
            target.Unnamed = false;
        }

        var newChords = selectedChords.Select(c =>
        {
            var nc = c.Clone();
            nc.TemplateId = target.Id;
            nc.ChordNotes = ProjectNotesOntoTemplate(nc, target);
            return nc;
        }).ToList();

        var newHandShapes = selectedHandShapes.Select(hs =>
        {
            var nhs = hs.Clone();
            nhs.TemplateId = target.Id;
            return nhs;
        }).ToList();

        IChartAction bind = isNew
            ? new CreateAndSwitchTemplateAction(arr.ChordTemplates, target, selectedChords, newChords,
                selectedHandShapes, newHandShapes, _editor)
            : new SwitchTemplateAction(selectedChords, newChords,
                selectedHandShapes, newHandShapes, _editor);

        _logger?.Info(AppLogCategory.Editing,
            $"Chord shape '{Show(target.Name)}' {(isNew ? "minted" : "reused")} ({targets.Rung}) for " +
            $"{selectedChords.Count} chord(s) + {selectedHandShapes.Count} hand shape(s): " +
            $"{ChordTemplateDiff.Describe(loaded, newState)}.");

        // ONE node for the whole gesture: the mint, the anchor sync and the
        // "same grip" templates ride inside it.
        var followUps = new List<IChartAction>(AnchorSyncActions(target, selectedChords));
        // A fingering typed by hand that lands on a grip the FHP generator minted
        // makes that grip authored: a hand-set fingering is never re-fingered.
        if (targets.Kind == ChordTemplateEditKind.Fingering && equiv is { FollowedFingering: true })
            followUps.Add(SetTemplateFingersAction.MarkAuthored(equiv, _editor));
        followUps.AddRange(gripActions);
        // The shape the targets leave behind goes in the same node when they were its last users.
        _historyService.Do(StrandedShapes.Wrap(arr,
            followUps.Count == 0 ? bind : new CompositeAction([bind, .. followUps]), _editor.RequestRedraw));
        return target;
    }

    /// <summary>
    ///     A RESHAPE lands on whichever existing template has the new frets, and its
    ///     fingering is that template's, not one the user typed. When the anchor
    ///     sync is on and that grip disagrees with the anchor while another grip of
    ///     the same frets agrees and is played under an anchor on that fret
    ///     (<see cref="IndexAnchorRepair.AgreeingGrip" />), the reshape takes that
    /// one and the anchor stays: the arrangement says the anchor is right.
    ///     A fingering the user TYPED is never swapped — the corrector moves the
    ///     anchor to it instead (<see cref="AnchorSyncActions" />).
    /// </summary>
    private ChordTemplate PreferGripTheAnchorAgreesWith(Arrangement arr, ChordTemplate equiv, List<Chord> chords,
        List<HandShape> handShapes)
    {
        if (_configService?.LoadAppConfig().SyncAnchorToChordFingering != true) return equiv;
        var position = chords.Count > 0 ? chords[0].Position
            : handShapes.Count > 0 ? handShapes[0].Position
            : (FractionalPosition?)null;
        if (position is not { } at || SoundCompanions.GoverningAnchor(at, _editor.Anchors) is not { } anchor) return equiv;
        if (IndexFingerPosition.Find(equiv, arr.Capo) is not { } index || index.Allows(anchor.Fret)) return equiv;
        return IndexAnchorRepair.AgreeingGrip(arr, equiv, anchor.Fret).Grip ?? equiv;
    }

    /// <summary>Binds the given chords and hand shapes to <paramref name="target" />
    /// (an existing arrangement template), projecting each chord's notes onto its
    /// frets, as one undo node.</summary>
    internal void SwitchSelection(ChordTemplate target, List<Chord> chords, List<HandShape> handShapes)
    {
        var newChords = chords.Select(c =>
        {
            var nc = c.Clone();
            nc.TemplateId = target.Id;
            nc.ChordNotes = ProjectNotesOntoTemplate(nc, target);
            return nc;
        }).ToList();

        var newHandShapes = handShapes.Select(hs =>
        {
            var nhs = hs.Clone();
            nhs.TemplateId = target.Id;
            return nhs;
        }).ToList();

        _historyService.Do(StrandedShapes.Wrap(_editor.ActiveArrangement,
            new SwitchTemplateAction(chords, newChords, handShapes, newHandShapes, _editor), _editor.RequestRedraw));
    }

    /// <summary>
    ///     Moves each governing FHP anchor onto the fret the new fingering puts
    ///     the index finger on — <see cref="AppConfig.SyncAnchorToChordFingering" />,
    ///     OFF by default.
    ///     <para>
    ///         <b>Why this is opt-in while the hand-shape sync is not.</b> A hand
    ///         shape describes the same decision as its chord, so leaving it behind
    ///         is simply wrong. An anchor says where the player's HAND goes — a
    ///         much bigger claim, and one usually shared with neighbouring sounds
    ///         this edit never touched. Moving it can silently re-finger those.
    ///         Inspector rule E04 reports the disagreement either way, so off costs
    ///         a warning rather than a defect.
    ///     </para>
    ///     <para>
    ///         <b>It moves the anchor, never the fingering:</b> the user just typed
    ///         it. Re-fingering to the grip the arrangement plays under this anchor
    ///         is the default FIX for a finding that is already there (the Issues
    ///         tab, <see cref="IndexAnchorRepair" />), and what a reshape picks when
    ///         the fingering came from an existing template
    /// (<see cref="PreferGripTheAnchorAgreesWith" />).
    ///     </para>
    ///     <para>
    ///         Returned as actions to fold into the caller's existing list, so the
    ///         anchor moves inside the SAME undo node as the fingering that caused
    ///         it. Two nodes would let a Ctrl+Z revert the fingering and strand the
    ///         anchor — the exact disagreement this is meant to remove.
    ///     </para>
    /// </summary>
    private List<IChartAction> AnchorSyncActions(ChordTemplate resolved, List<Chord> chords)
    {
        var actions = new List<IChartAction>();
        if (_configService?.LoadAppConfig().SyncAnchorToChordFingering != true) return actions;

        var arr = _editor.ActiveArrangement;
        if (arr == null || chords.Count == 0) return actions;

        // The SAME question rule E04 asks, inferred index included: an
        // anchor the range allows is left alone, one outside moves to the
        // nearest fret it allows.
        if (IndexFingerPosition.Find(resolved, arr.Capo) is not { } index) return actions;

        var targets = new List<FretAnchor>();
        var oldStates = new List<FretAnchor>();
        var newStates = new List<FretAnchor>();

        foreach (var chord in chords)
        {
            var anchor = SoundCompanions.GoverningAnchor(chord.Position, _editor.Anchors);
            if (anchor == null || index.Allows(anchor.Fret)) continue;
            // One anchor can govern several of the edited chords; move it once.
            if (targets.Contains(anchor)) continue;

            var newState = anchor.Clone();
            newState.Fret = index.Nearest(anchor.Fret);
            targets.Add(anchor);
            oldStates.Add(anchor.Clone());
            newStates.Add(newState);
        }

        if (targets.Count > 0)
            actions.Add(new MutateAnchorsAction(_editor.Anchors, targets, oldStates, newStates));
        return actions;
    }

    /// <summary>
    ///     The selected chords that currently play the shape the panel is
    ///     SHOWING — the only chords any inspector edit may touch, because every
    ///     write path projects its targets onto the loaded shape.
    /// </summary>
    internal static List<Chord> ChordsCarryingDisplayedShape(List<Chord> selectedChords, ChordTemplate displayed,
        Arrangement arr)
    {
        var result = new List<Chord>();
        foreach (var c in selectedChords)
        {
            if (!TemplateMatchesChord(displayed, c)) continue;
            // Arpeggio is part of the find-or-create key everywhere else
            // (`ChordMask.Arpeggio` is load-bearing on export), so a bulk edit
            // must not fold an arpeggio chord into a plain chord's template.
            var current = arr.ChordTemplates.FirstOrDefault(t => t.Id == c.TemplateId);
            if ((current?.IsArpeggio ?? false) != displayed.IsArpeggio) continue;
            result.Add(c);
        }

        return result;
    }

    /// <summary>
    ///     The selected hand shapes whose template declares the shape the panel
    ///     is SHOWING. A hand shape has no notes of its own, so its template is
    ///     its shape; one with a dangling <c>TemplateId</c> has nothing that can
    ///     agree with the display and is left alone.
    /// </summary>
    internal static List<HandShape> HandShapesCarryingDisplayedShape(
        List<HandShape> selectedHandShapes, ChordTemplate displayed, Arrangement arr)
    {
        var result = new List<HandShape>();
        foreach (var hs in selectedHandShapes)
        {
            var template = arr.ChordTemplates.FirstOrDefault(t => t.Id == hs.TemplateId);
            if (template != null && template.IsArpeggio == displayed.IsArpeggio &&
                FingersDictHelper.Equal(template.Frets, displayed.Frets))
            {
                result.Add(hs);
            }
        }

        return result;
    }

    /// <summary>
    /// Projects <paramref name="template"/>'s fret pattern onto a (cloned) chord:
    /// every template string becomes a chord note at that fret, KEEPING the
    /// existing note's per-string attributes (mute/hopo/bends/…) where the string
    /// already existed and adding a bare note where it did not. The chord passed
    /// in must already be a clone — its notes are mutated in place.
    /// </summary>
    private static ObservableCollection<ChordNote> ProjectNotesOntoTemplate(Chord clone, ChordTemplate template) =>
        ChordNoteProjection.OnTo(clone, template);

    /// <summary>True when <paramref name="template"/>'s fret pattern is exactly
    /// the chord's set of (string, fret) pairs — same shape, nothing added or
    /// dropped.</summary>
    internal static bool TemplateMatchesChord(ChordTemplate template, Chord chord) =>
        template.DescribesChordNotes(chord.ChordNotes);

    internal static string Show(string? value) => string.IsNullOrEmpty(value) ? "(unnamed)" : value;
}
