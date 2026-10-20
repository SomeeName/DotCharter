using DotCharter.Localization;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.Services.Editing;

/// <summary>
/// Top-level façade for global editor commands invoked from the menu,
/// keybinds, or context menus. Resolves the focused/active track and
/// delegates the heavy lifting to focused sub-coordinators
/// (<see cref="EditingClipboardCoordinator"/>,
/// <see cref="HandShapeFromSelectionGenerator"/>,
/// <see cref="TempoScaleCoordinator"/>) so each concern can be tested in
/// isolation. Trivial commands (Undo/Redo/Delete/Snap/Send) live here
/// because they are short and pull from a different mix of services.
/// </summary>
public class GlobalEditingCoordinator : IGlobalEditingCoordinator
{
    private readonly IHighwayEditingService _highwayEditingService;
    private readonly IHistoryService _historyManager;
    private readonly IAppLogger _logger;
    private readonly IPhraseCatalogService _phraseCatalog;
    private readonly ProjectState _projectState;
    private readonly ISelectionService _selectionManager;
    private readonly ISelectLikeService _selectLikeService;
    private readonly ITempoService _tempoService;
    private readonly ITrackContextRegistry _trackContexts;
    private readonly ILocalizationService _localization;

    private readonly EditingClipboardCoordinator _clipboard;
    private readonly HandShapeFromSelectionGenerator _handShape;
    private readonly HandShapeSplitter _handShapeSplitter;
    private readonly HandShapeSuggestionAcceptor _suggestions;
    private readonly TempoScaleCoordinator _tempoScale;

    public GlobalEditingCoordinator(
        ProjectState projectState,
        ISelectionService selectionManager,
        IHistoryService historyManager,
        IClipboardService clipboardManager,
        ITempoService tempoService,
        IBeatScaleService beatScaleService,
        IHighwayEditingService highwayEditingService,
        IConfigurationService configService,
        ISelectLikeService selectLikeService,
        IFingeringGenerationService fingeringService,
        ITrackContextRegistry trackContexts,
        IPhraseCatalogService phraseCatalog,
        IAppLogger logger,
        ILocalizationService localization)
    {
        _phraseCatalog = phraseCatalog;
        _projectState = projectState;
        _selectionManager = selectionManager;
        _historyManager = historyManager;
        _tempoService = tempoService;
        _highwayEditingService = highwayEditingService;
        _selectLikeService = selectLikeService;
        _trackContexts = trackContexts;
        _logger = logger;
        _localization = localization;

        _clipboard = new EditingClipboardCoordinator(
            clipboardManager, configService, highwayEditingService,
            selectionManager, tempoService, logger);
        _handShape = new HandShapeFromSelectionGenerator(
            fingeringService, highwayEditingService, selectionManager, logger);
        _handShapeSplitter = new HandShapeSplitter(
            fingeringService, highwayEditingService, selectionManager, logger);
        _suggestions = new HandShapeSuggestionAcceptor(historyManager, logger);
        _tempoScale = new TempoScaleCoordinator(
            beatScaleService, historyManager, projectState, selectionManager, logger, localization);
    }

    /// <summary>
    /// Resolves the track that global editing commands act on. Defaults to the
    /// focused track (set by the most recent click in any panel) and falls
    /// back to the active arrangement's context if nothing is focused yet.
    /// </summary>
    private TrackEditorContext? FocusedOrActive()
    {
        if (_trackContexts.FocusedTrack != null) return _trackContexts.FocusedTrack;
        var active = _projectState.ActiveArrangement;
        return active != null ? _trackContexts.GetOrCreate(active) : null;
    }

    public void Undo()
    {
        _logger.Info(AppLogCategory.Input, "Undo command invoked.");
        // GUARDED: an action's inverse may throw, and HistoryService rethrows on
        // purpose (see HistoryStepGuard) — unguarded, one bad undo terminated the
        // editor with the user's chart in it.
        HistoryStepGuard.Run(_historyManager.Undo, "Undo", _projectState, _logger, _localization);
    }

    public void Redo()
    {
        _logger.Info(AppLogCategory.Input, "Redo command invoked.");
        HistoryStepGuard.Run(_historyManager.Redo, "Redo", _projectState, _logger, _localization);
    }

    public void CopySelected(EditorFocusArea focusArea)
    {
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Editing, "Copy skipped: no focused or active track.");
            return;
        }
        _clipboard.Copy(track, focusArea);
    }

    public void Paste(double currentTimeMs, int gridDivisor)
    {
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Editing, "Paste skipped: no focused or active track.");
            return;
        }
        ReportPaste(_clipboard.Paste(track, currentTimeMs, gridDivisor));
    }

    public void PasteSpecial(double currentTimeMs, int gridDivisor, PasteSpecialMode mode)
    {
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Editing, $"PasteSpecial({mode}) skipped: no focused or active track.");
            return;
        }
        ReportPaste(_clipboard.PasteSpecial(track, currentTimeMs, gridDivisor, mode));
    }

    /// <summary>
    ///     Puts a refused or warned paste on the status rail, in the same words the
    /// phrase catalog uses for the same check. A clean paste says nothing,
    ///     as before.
    /// </summary>
    private void ReportPaste(PhrasePasteOutcome? outcome)
    {
        if (outcome?.Check is not { Severity: not PhrasePasteSeverity.Ok } check) return;
        var detail = string.Join(" ", check.Issues.Select(i => _localization.Format(i.LocalizationKey, i.Args.ToArray())));
        if (outcome.Result == PhrasePasteResult.Blocked)
            _projectState.SetMessage(_localization.Format("Clipboard_PasteBlocked", detail), StatusSeverity.Error);
        else
            _projectState.SetMessage(_localization.Format("Clipboard_PastedWithWarning", detail), StatusSeverity.Warning);
    }

    public PhraseCatalogEntry? CapturePhrase(EditorFocusArea focusArea, string name)
    {
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Editing, "Phrase capture skipped: no focused or active track.");
            return null;
        }

        return _clipboard.CaptureToCatalog(_phraseCatalog, track, focusArea, name);
    }

    public PhrasePasteOutcome PastePhrase(PhraseCatalogEntry entry, double currentTimeMs, int gridDivisor)
    {
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Editing, "Phrase paste skipped: no focused or active track.");
            return PhrasePasteOutcome.NoDestination;
        }

        return _clipboard.PastePhrase(track, entry, currentTimeMs, gridDivisor);
    }

    public void SelectAllEvents()
    {
        var track = FocusedOrActive();
        if (track == null) return;
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedNotes, []);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedChords, []);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedHandShapes, []);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedAnchors, []);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedEventPoints, track.EventPoints);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedTones, track.Tones);
        _logger.Info(AppLogCategory.Input,
            $"Selected all timeline items in track '{track.Arrangement.Name}'. Events={_selectionManager.SelectedEventPoints.Count}, tones={_selectionManager.SelectedTones.Count}.");
    }

    public void SelectAllNotes()
    {
        var track = FocusedOrActive();
        if (track == null) return;
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedEventPoints, []);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedNotes, track.Notes);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedChords, track.Chords);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedHandShapes, track.HandShapes);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedAnchors, track.Anchors);
        _logger.Info(AppLogCategory.Input,
            $"Selected all highway items in track '{track.Arrangement.Name}'. Notes={_selectionManager.SelectedNotes.Count}, chords={_selectionManager.SelectedChords.Count}, handshapes={_selectionManager.SelectedHandShapes.Count}, anchors={_selectionManager.SelectedAnchors.Count}.");
    }

    /// <summary>
    ///     "Select like" — every sound that plays the same thing as the seed
    ///     selection. For notes that is the existing relative-pattern matcher;
    ///     for CHORDS and HAND SHAPES it is shape equality: the same
    ///     string→fret map with no conflicting finger
    ///     (<see cref="ChordShapeSignature" />), independent of technique and
    ///     independent of which <see cref="ChordTemplate" /> object the sound
    ///     points at.
    ///     <para>
    ///         It must not key chords on template IDENTITY whenever they have a
    ///         template at all, computing a fret signature only for the
    ///         templateless ones. Both halves of that are wrong. Re-fingering
    ///         (or isolating, or reshaping) ONE chord of eight identical ones
    ///         mints it a private template, and from then on select-like finds
    ///         nothing at all — the sibling chords are still on the old id, and
    ///         the seed does not qualify for the signature path because it
    ///         now HAS an id. Undoing the fingering edit makes it work again,
    ///         which is exactly how the owner reported it.
    ///     </para>
    ///     <para>
    ///         Hand shapes are matched too, against the same seed set. They were
    ///         simply omitted — a <see cref="HandShape" /> carries a
    ///         <c>TemplateId</c> like a chord does, and the bar has to come
    ///         along or a bulk fingering edit leaves the 3D fingering pointing
    ///         at the old shape.
    ///     </para>
    /// </summary>
    public void SelectLikePattern()
    {
        var track = FocusedOrActive();
        if (track == null) return;

        var seedNotes = _selectionManager.SelectedNotes.ToList();
        var addedNotes = 0;
        if (seedNotes.Count > 0)
        {
            addedNotes = SelectionBulkOps.AddRange(_selectionManager.SelectedNotes,
                _selectLikeService.FindMatches(seedNotes, track.Notes));
        }

        // Always try shape matching too: even when notes were the seed,
        // selections often span chord notes the user reached by box-selecting,
        // and matching like-shaped chords elsewhere is what they expect.
        var seedChords = _selectionManager.SelectedChords.ToList();
        var seedHandShapes = _selectionManager.SelectedHandShapes.ToList();

        var shapeMatches = _selectLikeService.FindLikeShapes(
            track.Arrangement, seedChords, seedHandShapes, track.Chords, track.HandShapes);

        var addedChords = SelectionBulkOps.AddRange(_selectionManager.SelectedChords, shapeMatches.Chords);
        var addedHandShapes =
            SelectionBulkOps.AddRange(_selectionManager.SelectedHandShapes, shapeMatches.HandShapes);

        // FHP anchors, matched on the hand's JOB — fret + width + the run of
        // sounds underneath — never on the fret alone. "Every anchor on fret 7"
        // is not a meaningful selection: there are many reasons to park the hand
        // there, and re-fretting all of them because one was wrong corrupts
        // passages the user never looked at. See AnchorSpanSignature.
        var seedAnchors = _selectionManager.SelectedAnchors.ToList();
        var addedAnchors = SelectionBulkOps.AddRange(_selectionManager.SelectedAnchors,
            _selectLikeService.FindLikeAnchors(track.Level, seedAnchors));

        if (seedNotes.Count == 0 && seedChords.Count == 0 && seedHandShapes.Count == 0 && seedAnchors.Count == 0)
        {
            _logger.Info(AppLogCategory.Input,
                "Select-like skipped: no notes, chords, hand shapes or anchors selected as template.");
            return;
        }

        _logger.Info(AppLogCategory.Input,
            $"Select-like added {addedNotes} note(s), {addedChords} chord(s), {addedHandShapes} hand shape(s) " +
            $"and {addedAnchors} anchor(s) to selection.");
    }

    // === User-authored match patterns (v0.39) ================================
    // SelectLikePattern above is the shipped, hard-coded answer to "what is
    // alike". These three run a pattern the USER wrote instead. They deliberately
    // do not go through ISelectLikeService: that service encodes three specific
    // arguments (shape not template identity, the anchor's job not its fret,
    // strict-then-loose for notes) and a builder whose output is silently
    // rewritten by those arguments could never evaluate them.

    public int SelectByPattern(MatchPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Input, $"Pattern '{pattern.Name}' skipped: no focused or active track.");
            return 0;
        }

        var seed = BuildSeed();
        if (seed.Count == 0)
        {
            _logger.Info(AppLogCategory.Input,
                $"Pattern '{pattern.Name}' skipped: nothing selected to derive it from.");
            return 0;
        }

        if (!SeedTouches(seed, track))
        {
            _logger.Info(AppLogCategory.Input,
                $"Pattern '{pattern.Name}' skipped: the seed is on another arrangement than " +
                $"'{track.Arrangement.Name}', so its string and fret indices mean something else here.");
            return 0;
        }

        var matches = PatternMatcher.Find(pattern, seed, BuildSource(track));

        var added = SelectionBulkOps.AddRange(_selectionManager.SelectedNotes, matches.Notes)
                    + SelectionBulkOps.AddRange(_selectionManager.SelectedChords, matches.Chords)
                    + SelectionBulkOps.AddRange(_selectionManager.SelectedHandShapes, matches.HandShapes)
                    + SelectionBulkOps.AddRange(_selectionManager.SelectedAnchors, matches.Anchors);

        _logger.Info(AppLogCategory.Input,
            $"Pattern '{pattern.Name}' selected {added} element(s) from {matches.Groups.Count} occurrence(s) " +
            $"(seed={seed.Count}, scope={pattern.Scope}, fret={pattern.Fret}, grouping={pattern.Grouping}).");
        return added;
    }

    public int CountPatternMatches(MatchPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var track = FocusedOrActive();
        if (track == null) return 0;

        var seed = BuildSeed();
        if (seed.Count == 0 || !SeedTouches(seed, track)) return 0;

        return PatternMatcher.Find(pattern, seed, BuildSource(track)).Count;
    }

    public double? MoveToPatternMatch(MatchPattern pattern, double fromMs, int direction)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var track = FocusedOrActive();
        if (track == null) return null;

        var seed = BuildSeed();
        if (seed.Count == 0)
        {
            _logger.Info(AppLogCategory.Input,
                $"Pattern search '{pattern.Name}' skipped: nothing selected to derive it from.");
            return null;
        }

        if (!SeedTouches(seed, track))
        {
            _logger.Info(AppLogCategory.Input,
                $"Pattern search '{pattern.Name}' skipped: the seed is on another arrangement than " +
                $"'{track.Arrangement.Name}'.");
            return null;
        }

        var matches = PatternMatcher.Find(pattern, seed, BuildSource(track));
        if (matches.Groups.Count == 0)
        {
            _logger.Info(AppLogCategory.Input, $"Pattern search '{pattern.Name}' found no further occurrence.");
            return null;
        }

        // A one-millisecond dead band around the playhead: seeking lands the
        // playhead ON the match, so a strict comparison alone would let the next
        // press find the same occurrence again and the search would never move.
        var beats = track.Beats;
        PatternMatchGroup? best = null;
        var bestMs = 0.0;

        foreach (var group in matches.Groups)
        {
            var ms = group.Position.GetPositionMs(beats);
            if (direction > 0)
            {
                if (ms <= fromMs + 1.0) continue;
                if (best == null || ms < bestMs) (best, bestMs) = (group, ms);
            }
            else
            {
                if (ms >= fromMs - 1.0) continue;
                if (best == null || ms > bestMs) (best, bestMs) = (group, ms);
            }
        }

        if (best == null)
        {
            _logger.Info(AppLogCategory.Input,
                $"Pattern search '{pattern.Name}': {matches.Groups.Count} occurrence(s), none " +
                $"{(direction > 0 ? "after" : "before")} {fromMs:F0} ms.");
            return null;
        }

        // The found occurrence BECOMES the selection, so pressing the key again
        // steps on from here — that is what makes it a find-next rather than a
        // one-shot jump. It also means a run pattern stays a run pattern.
        _selectionManager.ClearSelections();
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedNotes, best.Notes);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedChords, best.Chords);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedHandShapes, best.HandShapes);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedAnchors, best.Anchors);

        _logger.Info(AppLogCategory.Input,
            $"Pattern search '{pattern.Name}' moved {(direction > 0 ? "forward" : "back")} to {bestMs:F0} ms " +
            $"({best.Count} element(s), {matches.Groups.Count} occurrence(s) total).");
        return bestMs;
    }

    /// <inheritdoc />
    public bool PatternSeedIsFromAnotherArrangement()
    {
        var track = FocusedOrActive();
        if (track == null) return false;

        var seed = BuildSeed();
        return seed.Count > 0 && !SeedTouches(seed, track);
    }

    /// <summary>
    ///     Does ANY seed element still live in this track's level? Reference
    ///     membership, because that is exactly the question — a selection is a
    ///     list of the domain objects themselves.
    ///     <para>
    ///         ANY rather than ALL on purpose: a selection that legitimately
    ///         spans levels or reaches beyond the current view still has a foot
    ///         in the focused track, and refusing that would break the feature
    ///         to fix a narrower bug. Only a seed with NOTHING here came from
    ///         somewhere else.
    ///     </para>
    /// </summary>
    private static bool SeedTouches(PatternSeed seed, TrackEditorContext track) =>
        seed.Notes.Any(track.Notes.Contains) ||
        seed.Chords.Any(track.Chords.Contains) ||
        seed.HandShapes.Any(track.HandShapes.Contains) ||
        seed.Anchors.Any(track.Anchors.Contains);

    private PatternSeed BuildSeed() => new(
        _selectionManager.SelectedNotes.ToList(),
        _selectionManager.SelectedChords.ToList(),
        _selectionManager.SelectedHandShapes.ToList(),
        _selectionManager.SelectedAnchors.ToList());

    private static PatternSource BuildSource(TrackEditorContext track) => new(
        track.Arrangement, track.Notes, track.Chords, track.HandShapes, track.Anchors,
        // Sections and phrases are song structure and live on the arrangement,
        // not the level — PatternRange is the only facet that reads them.
        track.EventPoints);

    public void GenerateHandShapeFromSelection()
    {
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Editing, "GenerateHandShape skipped: no focused or active track.");
            return;
        }
        _handShape.Generate(track);
    }

    public void GenerateArpeggioFromSelection()
    {
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Editing, "GenerateArpeggio skipped: no focused or active track.");
            return;
        }
        _handShape.Generate(track, arpeggio: true);
    }

    public void AcceptSuggestedHandShapesInSelection(bool arpeggio)
    {
        var track = FocusedOrActive();
        if (track?.Level is not { } level) return;

        var selected = _selectionManager.SelectedNotes.Select(n => n.Position)
            .Concat(_selectionManager.SelectedChords.Select(c => c.Position));
        var picks = HandShapeSuggestionAcceptor.Holding(HandShapeSuggester.Suggest(track.Arrangement, level), selected);
        var accepted = _suggestions.Accept(track.Arrangement, level, picks, arpeggio);
        _projectState.MainDisplayMessage = accepted == 0
            ? _localization.Format("Status_NoSuggestedHandShapesInSelection")
            : _localization.Plural("Status_SuggestedHandShapesAcceptedOne", "Status_SuggestedHandShapesAccepted", accepted);
    }

    public void SplitHandShapeAtSelection()
    {
        var track = FocusedOrActive();
        if (track == null)
        {
            _logger.Info(AppLogCategory.Editing, "SplitHandShape skipped: no focused or active track.");
            return;
        }
        _handShapeSplitter.SplitAtSelection(track);
    }

    public void DeleteSelected(EditorFocusArea focusArea)
    {
        var track = FocusedOrActive();
        if (track == null) return;

        var actions = BuildDeleteActions(track, focusArea);
        if (actions.Count > 0) _historyManager.Do(new CompositeAction(actions) { LabelKey = "History_Delete" });
        _logger.Info(AppLogCategory.Editing,
            $"Delete selected requested in track '{track.Arrangement.Name}'. FocusArea={focusArea}, actions={actions.Count}.");
    }

    /// <summary>
    ///     Builds the removal actions for the current selection and CLEARS it —
    ///     but commits nothing. Split out of <see cref="DeleteSelected" /> so
    ///     <see cref="DeleteSelectedRelated" /> can extend the same list and push
    ///     ONE history node: a gesture is one undo node, and "delete and related"
    ///     is one gesture on one keybind. Calling
    ///     <see cref="DeleteSelected" /> and then committing the orphaned hand shapes
    ///     as a SECOND node would let a single Ctrl+Z put the bars back over sounds that
    ///     were still gone — a chart state that never existed — and a second press
    ///     be needed to finish the undo.
    /// </summary>
    private List<IChartAction> BuildDeleteActions(TrackEditorContext track, EditorFocusArea focusArea)
    {
        var actions = new List<IChartAction>();

        // A lone selected LYRIC is deleted from either focus area: the lyric lane
        // sits in the event rows, but it is neither an event nor a tone, so Delete would otherwise do
        // nothing with one selected. With other content selected too, the focus-area rule below
        // decides and the lyric is left alone.
        if (_selectionManager.SelectedLyric is { } lyric && !HasNonLyricSelection())
        {
            foreach (var vocals in _projectState.ActiveProject?.VocalTracks ?? [])
            {
                var phrase = vocals.Phrases.FirstOrDefault(p => p.Lyrics.Contains(lyric));
                if (phrase == null) continue;
                actions.Add(new RemoveLyricAction(vocals.Phrases, phrase, lyric));
                break;
            }

            _selectionManager.SelectedLyric = null;
            return actions;
        }

        if (focusArea == EditorFocusArea.TimelineEvents &&
            (_selectionManager.SelectedEventPoints.Any() || _selectionManager.SelectedTones.Any()))
        {
            if (_selectionManager.SelectedEventPoints.Any())
            {
                actions.Add(new RemoveEventPointsAction(track.EventPoints,
                    _selectionManager.SelectedEventPoints.ToList()));
                _selectionManager.SelectedEventPoints.Clear();
            }

            if (_selectionManager.SelectedTones.Any())
            {
                actions.Add(new RemoveToneChangesAction(track.Tones,
                    _selectionManager.SelectedTones.ToList()));
                _selectionManager.SelectedTones.Clear();
            }
        }
        else
        {
            if (_selectionManager.SelectedNotes.Any())
            {
                actions.Add(new RemoveNotesAction(track.Notes, _selectionManager.SelectedNotes.ToList(),
                    _highwayEditingService.GetLinkNextUpdater()));
                _selectionManager.SelectedNotes.Clear();
            }

            if (_selectionManager.SelectedChords.Any())
            {
                actions.Add(new RemoveChordsAction(track.Chords,
                    _selectionManager.SelectedChords.ToList(), _highwayEditingService.GetLinkNextUpdater()));
                _selectionManager.SelectedChords.Clear();
            }

            if (_selectionManager.SelectedHandShapes.Any())
            {
                actions.Add(new RemoveHandShapesAction(track.HandShapes,
                    _selectionManager.SelectedHandShapes.ToList(), _highwayEditingService.GetLinkNextUpdater()));
                _selectionManager.SelectedHandShapes.Clear();
            }

            if (_selectionManager.SelectedAnchors.Any())
            {
                actions.Add(new RemoveAnchorsAction(track.Anchors, _selectionManager.SelectedAnchors.ToList()));
                _selectionManager.SelectedAnchors.Clear();
            }
        }

        return actions;
    }

    private bool HasNonLyricSelection() =>
        _selectionManager.SelectedNotes.Count > 0 || _selectionManager.SelectedChords.Count > 0 ||
        _selectionManager.SelectedHandShapes.Count > 0 || _selectionManager.SelectedAnchors.Count > 0 ||
        _selectionManager.SelectedEventPoints.Count > 0 || _selectionManager.SelectedTones.Count > 0;

    public void DeleteSelectedRelated(EditorFocusArea focusArea)
    {
        var track = FocusedOrActive();
        if (track == null) return;

        // Snapshot the selection before BuildDeleteActions clears it, so we can
        // find the handshapes this delete will orphan (no remaining notes/chords
        // inside them).
        var deletedNotes = _selectionManager.SelectedNotes.ToList();
        var deletedChords = _selectionManager.SelectedChords.ToList();
        var deletedHandShapes = _selectionManager.SelectedHandShapes.ToList();

        var actions = BuildDeleteActions(track, focusArea);

        // The orphan scan runs on the PRE-delete chart and discounts what the
        // actions above are about to remove — they have not executed yet, which
        // is the whole point: they and the orphan removal go into ONE composite
        // and therefore into one undo node. Committing the orphans separately
        // meant one Ctrl+Z restored the hand-shape bars over sounds that were
        // still gone.
        var orphanedHandShapes = focusArea == EditorFocusArea.TimelineEvents
            ? []
            : FindOrphanedHandShapes(track, deletedNotes, deletedChords, deletedHandShapes);

        if (orphanedHandShapes.Count > 0)
        {
            actions.Add(new RemoveHandShapesAction(track.HandShapes, orphanedHandShapes,
                _highwayEditingService.GetLinkNextUpdater()));
        }

        if (actions.Count > 0) _historyManager.Do(new CompositeAction(actions) { LabelKey = "History_DeleteRelated" });

        _logger.Info(AppLogCategory.Editing,
            $"DeleteSelectedRelated committed {actions.Count} action(s) as ONE undo node in track '{track.Arrangement.Name}', removing {orphanedHandShapes.Count} orphaned handshape(s).");
    }

    /// <summary>
    ///     Which hand shapes does the pending delete leave holding nothing?
    ///     <para>
    ///         Containment is <see cref="HandShape.Covers" /> — start inclusive, end
    ///         EXCLUSIVE — the same span rule the anchor generator, the refret sites
    ///         and the companion resolver use. A CLOSED span would be wrong,
    ///         and shapes ABUT: every tab importer emits A=[0,4) B=[4,8), so B's
    ///         opening sound would count as content of A. Emptying A would therefore leave it
    ///         looking occupied, and "delete + related" would never collect it.
    ///     </para>
    ///     <para>
    ///         The doomed elements are discounted BY REFERENCE (the rule the merge
    ///         contract uses for its targets): two equal-looking sounds at one
    ///         position are ordinary chart state, and a value comparison would
    ///         discount the survivor instead of the one being deleted.
    ///     </para>
    /// </summary>
    private static List<HandShape> FindOrphanedHandShapes(
        TrackEditorContext track,
        IReadOnlyList<GuitarNote> deletedNotes,
        IReadOnlyList<Chord> deletedChords,
        IReadOnlyList<HandShape> deletedHandShapes)
    {
        var orphaned = new List<HandShape>();
        if (deletedNotes.Count == 0 && deletedChords.Count == 0) return orphaned;

        var goingAway = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var n in deletedNotes) goingAway.Add(n);
        foreach (var c in deletedChords) goingAway.Add(c);
        foreach (var shape in deletedHandShapes) goingAway.Add(shape);

        foreach (var hs in track.HandShapes)
        {
            // A shape the selection itself removes is not an orphan to collect a
            // second time.
            if (goingAway.Contains(hs)) continue;

            var hasContent = false;
            foreach (var note in track.Notes)
            {
                if (goingAway.Contains(note)) continue;
                if (hs.Covers(note.Position))
                {
                    hasContent = true;
                    break;
                }
            }
            if (!hasContent)
            {
                foreach (var chord in track.Chords)
                {
                    if (goingAway.Contains(chord)) continue;
                    if (hs.Covers(chord.Position))
                    {
                        hasContent = true;
                        break;
                    }
                }
            }
            // Only target handshapes whose range overlapped a deleted item, to
            // avoid wiping handshapes that just happen to be empty for
            // unrelated reasons.
            if (!hasContent && CoversAnyDeleted(hs, deletedNotes, deletedChords))
                orphaned.Add(hs);
        }

        return orphaned;
    }

    /// <summary>
    ///     Did this shape hold one of the sounds we are deleting? Same
    ///     <see cref="HandShape.Covers" /> span as the emptiness test above — with
    ///     a closed span, deleting the FIRST sound of the next abutting shape
    ///     made this one a candidate for removal as well.
    /// </summary>
    private static bool CoversAnyDeleted(
        HandShape shape,
        IReadOnlyList<GuitarNote> deletedNotes, IReadOnlyList<Chord> deletedChords)
    {
        foreach (var n in deletedNotes)
            if (shape.Covers(n.Position)) return true;
        foreach (var c in deletedChords)
            if (shape.Covers(c.Position)) return true;
        return false;
    }

    public void SendSelectionToArrangement(Arrangement target)
    {
        var sourceTrack = FocusedOrActive();
        if (sourceTrack == null || target == sourceTrack.Arrangement)
        {
            _logger.Info(AppLogCategory.Editing,
                "SendSelectionToArrangement skipped: no source track or target equals source.");
            return;
        }

        // The target's whole-song level, not its easiest (ChartLevels).
        var targetLevel = ChartLevels.Hardest(target);
        if (targetLevel == null)
        {
            _logger.Warning(AppLogCategory.Editing,
                $"SendSelectionToArrangement skipped: target arrangement '{target.Name}' has no levels.");
            return;
        }

        var notes = _selectionManager.SelectedNotes.ToList();
        var chords = _selectionManager.SelectedChords.ToList();
        var hs = _selectionManager.SelectedHandShapes.ToList();
        var anchors = _selectionManager.SelectedAnchors.ToList();

        if (notes.Count + chords.Count + hs.Count + anchors.Count == 0)
        {
            _logger.Info(AppLogCategory.Editing, "SendSelectionToArrangement skipped: nothing selected.");
            return;
        }

        var actions = new List<IChartAction>();
        if (notes.Count > 0)
        {
            actions.Add(new RemoveNotesAction(sourceTrack.Notes, notes));
            actions.Add(new AddNotesAction(targetLevel.Notes, notes));
        }
        if (chords.Count > 0)
        {
            actions.Add(new RemoveChordsAction(sourceTrack.Chords, chords));
            actions.Add(new AddChordsAction(targetLevel.Chords, chords));
        }
        if (hs.Count > 0)
        {
            actions.Add(new RemoveHandShapesAction(sourceTrack.HandShapes, hs));
            actions.Add(new AddHandShapesAction(targetLevel.HandShapes, hs));
        }
        if (anchors.Count > 0)
        {
            actions.Add(new RemoveAnchorsAction(sourceTrack.Anchors, anchors));
            actions.Add(new AddAnchorsAction(targetLevel.Anchors, anchors));
        }

        _selectionManager.ClearSelections();
        _historyManager.Do(new CompositeAction(actions) { LabelKey = "History_SendToArrangement" });

        // Focus (not promote) the target so the user's selection visually
        // moves there. ActiveArrangement stays as it was.
        _trackContexts.Focus(target);

        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedNotes, notes);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedChords, chords);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedHandShapes, hs);
        SelectionBulkOps.ReplaceWith(_selectionManager.SelectedAnchors, anchors);

        _logger.Info(AppLogCategory.Editing,
            $"Sent selection from '{sourceTrack.Arrangement.Name}' to '{target.Name}': notes={notes.Count}, chords={chords.Count}, handshapes={hs.Count}, anchors={anchors.Count}.");
    }

    public void SnapSelectedNotes(int gridDivisor)
    {
        var track = FocusedOrActive();
        if (track == null) return;
        var actions = BuildSnapActions(track,
            _selectionManager.SelectedNotes.ToList(),
            _selectionManager.SelectedChords.ToList(),
            gridDivisor);
        if (actions.Count == 0) return;
        if (_historyManager.Do(actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_SnapNotes" }))
        {
            _logger.Info(AppLogCategory.Editing,
                $"Snapped selected highway items to grid divisor {gridDivisor}. Notes={_selectionManager.SelectedNotes.Count}, chords={_selectionManager.SelectedChords.Count}.");
        }
    }

    public void SnapAllNotes(int gridDivisor)
    {
        var track = FocusedOrActive();
        if (track == null) return;
        var actions = BuildSnapActions(track,
            track.Notes.ToList(),
            track.Chords.ToList(),
            gridDivisor);
        if (actions.Count == 0) return;
        if (_historyManager.Do(actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_SnapNotes" }))
        {
            _logger.Info(AppLogCategory.Editing,
                $"Snapped all highway items to grid divisor {gridDivisor}. Notes={track.Notes.Count}, chords={track.Chords.Count}.");
        }
    }

    public void ApplyDoubleTimeToSelection() => _tempoScale.ApplyDoubleTime();
    public void ApplyHalfTimeToSelection() => _tempoScale.ApplyHalfTime();

    private List<IChartAction> BuildSnapActions(
        TrackEditorContext track,
        IReadOnlyList<GuitarNote> notes,
        IReadOnlyList<Chord> chords,
        int gridDivisor)
    {
        var actions = new List<IChartAction>();
        if (notes.Count > 0)
        {
            actions.Add(new MutateNotesAction(track.Notes, notes,
                n => n.Position = _tempoService.SnapToGrid(n.Position.GetPositionMs(track.Beats), track.Beats,
                    gridDivisor),
                _highwayEditingService.GetLinkNextUpdater()));
        }

        if (chords.Count > 0)
        {
            var oldStates = chords.Select(c => c.Clone()).ToList();
            var newStates = chords.Select(c =>
            {
                var clone = c.Clone();
                clone.Position = _tempoService.SnapToGrid(c.Position.GetPositionMs(track.Beats), track.Beats,
                    gridDivisor);
                return clone;
            }).ToList();
            actions.Add(new MutateChordsAction(track.Chords, chords, oldStates, newStates,
                _highwayEditingService.GetLinkNextUpdater()));
        }

        return actions;
    }
}
