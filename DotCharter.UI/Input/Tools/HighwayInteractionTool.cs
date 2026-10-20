using Avalonia;
using Avalonia.Input;
using DotCharter.UI.Controls;

namespace DotCharter.UI.Input.Tools;

public class HighwayInteractionTool : IEditorTool, IDisposable
{
    // Transient (one instance per ToolCoordinator, so per-gesture
    // state is never shared across viewports); the ctor subscription to
    // HistoryChanged makes disposal load-bearing — TimelineInteractionManager
    // disposes its tool set on detach.
    private IHistoryService? _subscribedHistory;

    public void Dispose()
    {
        _subscribedHistory?.HistoryChanged -= OnHistoryChanged;
        _subscribedHistory = null;
        GC.SuppressFinalize(this);
    }

    private readonly DragState _dragState = new();
    private readonly IHighwayEditingService _highwayEditingService;
    private readonly IHistoryService _history;
    private readonly IAppLogger _logger;
    private FractionalPosition _currentDragDelta = FractionalPosition.Zero;
    private FractionalPosition? _lastAppliedDragDelta;
    private List<FretAnchor> _dragOriginalAnchors = [];

    private List<Chord> _dragOriginalChordStates = [];
    private List<HandShape> _dragOriginalHandShapes = [];
    private List<GuitarNote> _dragOriginalStates = [];

    private Chord? _dragTargetChord;
    private GuitarNote? _dragTargetNote;
    private TrackEditorContext? _dragTrack;
    private FractionalPosition? _lastClickedPosition;
    private int _lastHoverString = -1;
    private FractionalPosition _lastHoverTime;

    // Marquee (rubber-band) selection: armed on left-press over empty lane
    // space, becomes a drag past MarqueeHitTester.DragThresholdPx. The
    // rectangle itself is drawn by the viewport's MarqueeSelectionOverlay —
    // never by the GL scene.
    private Point? _marqueeOrigin;
    private TrackEditorContext? _marqueeTrack;
    private IChartViewport? _marqueeViewport;
    private bool _marqueeDragged;

    // Context menu armed on a bare right-PRESS, shown on the right-RELEASE —
    // opening a flyout while the button is still down lets the release land on
    // the fresh popup and light-dismiss it. Cleared by ResetState.
    private Chord? _pendingMenuChord;
    private GuitarNote? _pendingMenuNote;
    private bool _pendingMenu;

    public HighwayInteractionTool(IHighwayEditingService highwayEditingService,
        IHistoryService historyService, IAppLogger logger)
    {
        _highwayEditingService = highwayEditingService;
        _history = historyService;
        _logger = logger;
        _subscribedHistory = historyService;
        historyService.HistoryChanged += OnHistoryChanged;
    }

    public void OnPointerPressed(PointerPressedEventArgs e, ToolContext context)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var selection = track.Selection;
        track.FocusArea = EditorFocusArea.NoteHighway;
        vm.ChartEditor.FocusArea = EditorFocusArea.NoteHighway;
        selection.SelectedEventPoints.Clear();
        selection.SelectedTones.Clear();

        var stringCount = track.StringCount;

        var hitChord = ChartHitTester.HitTestChord(context.Position, track.Chords,
            vm.ProjectState.TimelineViewAnchorEffectiveMs,
            vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth, context.Viewport.ViewportHeight,
            context.Viewport.TopMargin, context.Viewport.BottomMargin, stringCount,
            track.Beats, context.Viewport.FlipStrings, out var chordExact);

        var hitNote = ChartHitTester.HitTestNote(context.Position, track.Notes,
            vm.ProjectState.TimelineViewAnchorEffectiveMs,
            vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth, context.Viewport.ViewportHeight,
            context.Viewport.TopMargin, context.Viewport.BottomMargin, stringCount,
            track.Beats, context.Viewport.FlipStrings, out var noteExact);

        // A CHORD outranks a note at every branch below, so a chord found only
        // by the near-miss ring would beat a note the pointer is provably
        // INSIDE — see ChartHitTester.ResolveSound.
        (hitChord, hitNote) = ChartHitTester.ResolveSound(hitChord, chordExact, hitNote, noteExact);

        if (context.Properties.IsRightButtonPressed)
        {
            // IsEnabled gates keep the interpolated strings from being built
            // on every click when the Input category is filtered out.
            if (_logger.IsEnabled(AppLogCategory.Input))
            {
                _logger.Info(AppLogCategory.Input,
                    $"Right-click highway at string={context.StringIndex}, snapped={FormatPosition(context.SnappedPosition, track)}, exactMs={context.ExactTimeMs:0.###}. HitChord={hitChord != null}, HitNote={hitNote != null}, modifiers={context.Modifiers}.");
            }

            // A plain (or Alt+) right-click is the INSTANT charting verb: toggle
            // the clicked chord string, remove the note, or place one.
            var verb = HighwayRightClick.Resolve(hitChord, hitNote, context.StringIndex, context.Modifiers);
            if (!HighwayRightClick.Apply(_highwayEditingService, verb, hitChord, hitNote,
                    context.SnappedPosition, context.StringIndex))
            {
                // Shift+right-click: arm the context menu for the RELEASE. The
                // hit is the near-miss-tolerant pass above, so the menu targets
                // what the user visibly clicked; a target outside the current
                // selection replaces it (a menu on A acting on B loses work).
                if (hitChord != null || hitNote != null)
                    EnsureTargetSelected(track, hitChord, hitNote);
                _pendingMenu = true;
                _pendingMenuChord = hitChord;
                _pendingMenuNote = hitNote;
            }
        }
        else if (context.Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2 && (hitChord != null || hitNote != null))
            {
                // Double-click on a sound: focus it in the Notes inspector
                // (tab 0, the Sound tab). The first click of the pair already
                // selected it and armed a drag; this press supersedes both.
                EnsureTargetSelected(track, hitChord, hitNote);
                vm.ShowInspectorTab(0);
                if (_logger.IsEnabled(AppLogCategory.Input))
                {
                    _logger.Info(AppLogCategory.Input,
                        $"Double-clicked highway sound at {FormatPosition(context.SnappedPosition, track)}; opening the Sound inspector.");
                }

                context.Viewport.RequestNextFrameRendering();
                return;
            }

            if (_logger.IsEnabled(AppLogCategory.Input))
            {
                _logger.Info(AppLogCategory.Input,
                    $"Left-click highway at string={context.StringIndex}, snapped={FormatPosition(context.SnappedPosition, track)}, exactMs={context.ExactTimeMs:0.###}. HitChord={hitChord != null}, HitNote={hitNote != null}, modifiers={context.Modifiers}.");
            }

            HandleLeftClick(context, hitChord, hitNote);
        }

        context.Viewport.RequestNextFrameRendering();
    }

    public void OnPointerMoved(PointerEventArgs e, ToolContext context)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var selection = track.Selection;
        var stringCount = track.StringCount;

        if (selection.PreviewEventPoints.Count > 0) selection.PreviewEventPoints.Clear();

        if (context.Properties.IsLeftButtonPressed && _marqueeOrigin is { } origin)
        {
            // Marquee drag: update the rubber-band overlay only. The GL scene
            // is untouched — the overlay control invalidates itself.
            if (!_marqueeDragged && MarqueeHitTester.IsDrag(origin, context.Position))
                _marqueeDragged = true;

            if (_marqueeDragged)
            {
                var rect = MarqueeHitTester.Clamp(
                    MarqueeHitTester.Normalize(origin, context.Position),
                    context.Viewport.ViewportWidth, context.Viewport.ViewportHeight);
                (_marqueeViewport ?? context.Viewport).SetMarquee(rect);

                // Suppress the hover placement preview while rubber-banding.
                if (selection.PreviewNotes.Count > 0) selection.PreviewNotes.Clear();
                if (selection.PreviewChords.Count > 0) selection.PreviewChords.Clear();
                if (selection.HoveredNote != null) selection.HoveredNote = null;
                if (selection.HoveredChord != null) selection.HoveredChord = null;
            }

            return;
        }

        if (context.Properties.IsLeftButtonPressed && _dragState.IsDragging)
        {
            if (!_dragState.HasDragged)
            {
                // First real movement of this drag: NOW the original states
                // are worth cloning. The selection cannot have changed since
                // the press — the button has been held the whole time.
                _dragState.HasDragged = true;
                CaptureDragOriginals(selection);
            }

            _currentDragDelta = _dragState.GetDelta(context);
            // The delta is grid-snapped, so most throttled pointer moves land
            // on the same value. Only rebuild the preview clones when the
            // snapped delta actually changed — the rebuild Clone()s the whole
            // selection and was the dominant GC source during drags.
            if (_lastAppliedDragDelta != _currentDragDelta)
            {
                RefreshDragPreviews(_currentDragDelta);
                _lastAppliedDragDelta = _currentDragDelta;
            }
        }
        else
        {
            var hoveredNote = ChartHitTester.HitTestNote(context.Position, track.Notes,
                vm.ProjectState.TimelineViewAnchorEffectiveMs,
                vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth, context.Viewport.ViewportHeight,
                context.Viewport.TopMargin, context.Viewport.BottomMargin, stringCount,
                track.Beats, context.Viewport.FlipStrings, out var hoverNoteExact);

            var hoveredChord = ChartHitTester.HitTestChord(context.Position, track.Chords,
                vm.ProjectState.TimelineViewAnchorEffectiveMs, vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth,
                context.Viewport.ViewportHeight,
                context.Viewport.TopMargin, context.Viewport.BottomMargin, stringCount,
                track.Beats, context.Viewport.FlipStrings, out var hoverChordExact);

            // Same precedence as the press path — the hover highlight has to
            // point at whatever the click would take, or the user aims at a lit
            // gem and edits a different one.
            (hoveredChord, hoveredNote) =
                ChartHitTester.ResolveSound(hoveredChord, hoverChordExact, hoveredNote, hoverNoteExact);

            if (selection.HoveredNote != hoveredNote) selection.HoveredNote = hoveredNote;
            if (selection.HoveredChord != hoveredChord) selection.HoveredChord = hoveredChord;

            if (_lastHoverTime != context.SnappedPosition || _lastHoverString != context.StringIndex ||
                !selection.PreviewNotes.Any())
            {
                if (selection.PreviewNotes.Count > 0) selection.PreviewNotes.Clear();
                if (selection.PreviewChords.Count > 0) selection.PreviewChords.Clear();

                if (hoveredChord == null && hoveredNote == null)
                {
                    selection.PreviewNotes.Add(new GuitarNote
                    {
                        Position = context.SnappedPosition, StringIndex = context.StringIndex,
                        Fret = vm.ProjectState.PreviewNoteFret,
                        Sustain = FractionalPosition.Zero
                    });
                }

                _lastHoverTime = context.SnappedPosition;
                _lastHoverString = context.StringIndex;
            }

            selection.HoveredTimeMs = context.SnappedPosition.GetPositionMs(track.Beats);
            selection.HoveredStringIndex = context.StringIndex;
        }

        context.Viewport.RequestNextFrameRendering();
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context)
    {
        if (_marqueeOrigin is { } marqueeOrigin)
        {
            CommitMarquee(marqueeOrigin, context);
            ResetState(context);
            return;
        }

        if (_pendingMenu && e.InitialPressMouseButton == MouseButton.Right)
        {
            var chord = _pendingMenuChord;
            var note = _pendingMenuNote;
            ResetState(context);
            OpenContextMenu(context, chord, note);
            return;
        }

        var selection = (_dragTrack ?? context.Track).Selection;
        if (_dragState.IsDragging && _dragState.HasDragged)
        {
            // A drag ends where the pointer was RELEASED, not one throttle window
            // behind it. ToolCoordinator drops every pointer move inside 16 ms, so
            // the previews this commit writes were built from a position up to a
            // frame old — and because the delta is grid-snapped, a flick whose last
            // frame crossed a grid line dropped the whole selection on the previous
            // one, silently and undoably. HandShapeInteractionTool already
            // recomputed at release; this is the same fix the minimap drag took.
            _currentDragDelta = _dragState.GetDelta(context);
            RefreshDragPreviews(_currentDragDelta);
            _lastAppliedDragDelta = _currentDragDelta;

            var targetNotes = _dragTargetNote != null
                ? selection.SelectedNotes.ToList()
                : [];
            var previewNotes = _dragTargetNote != null
                ? selection.PreviewNotes.ToList()
                : [];
            var targetChords = _dragTargetChord != null ? selection.SelectedChords.ToList() : [];
            var previewChords = _dragTargetChord != null ? selection.PreviewChords.ToList() : [];

            var targetHandShapes = selection.SelectedHandShapes.ToList();
            var previewHandShapes = selection.PreviewHandShapes.ToList();
            var targetAnchors = selection.SelectedAnchors.ToList();
            var previewAnchors = selection.PreviewAnchors.ToList();

            var committed = _highwayEditingService.CommitHandShapeDragAndDrop(
                targetNotes, previewNotes,
                targetChords, previewChords,
                targetHandShapes, previewHandShapes,
                targetAnchors, previewAnchors);
            if (committed)
            {
                _logger.Info(AppLogCategory.Editing,
                    $"Released highway drag. TargetNotes={targetNotes.Count}, previewNotes={previewNotes.Count}, targetChords={targetChords.Count}, previewChords={previewChords.Count}, targetHandShapes={targetHandShapes.Count}, previewHandShapes={previewHandShapes.Count}, targetAnchors={targetAnchors.Count}, previewAnchors={previewAnchors.Count}.");
            }
        }

        ResetState(context);
    }

    public void OnPointerExited(PointerEventArgs e, ToolContext context)
    {
        // A marquee drag keeps pointer capture; don't tear it down on the
        // exit events that can fire while the button is still held.
        if (!_dragState.IsDragging && _marqueeOrigin == null) ResetState(context);
    }

    /// <summary>
    ///     Finish a marquee interaction: a real drag selects every note/chord
    ///     intersecting the rectangle (additive with Shift, replacing
    ///     otherwise); a plain click keeps the old click-to-clear behaviour
    ///     (Shift-click on empty space leaves the selection alone).
    /// </summary>
    private void CommitMarquee(Point origin, ToolContext context)
    {
        var track = _marqueeTrack ?? context.Track;
        var viewport = _marqueeViewport ?? context.Viewport;
        var selection = track.Selection;
        var additive = context.Modifiers.HasFlag(KeyModifiers.Shift);

        if (_marqueeDragged)
        {
            var rect = MarqueeHitTester.Clamp(
                MarqueeHitTester.Normalize(origin, context.Position),
                viewport.ViewportWidth, viewport.ViewportHeight);

            var vm = context.ViewModel;
            var hitNotes = new List<GuitarNote>();
            var hitChords = new List<Chord>();
            MarqueeHitTester.CollectNotes(rect, track.Notes, vm.ProjectState.TimelineViewAnchorEffectiveMs,
                vm.ProjectState.PixelsPerMs, viewport.ViewportWidth, viewport.ViewportHeight,
                viewport.TopMargin, viewport.BottomMargin, track.StringCount, track.Beats, hitNotes,
                viewport.FlipStrings);
            MarqueeHitTester.CollectChords(rect, track.Chords, vm.ProjectState.TimelineViewAnchorEffectiveMs,
                vm.ProjectState.PixelsPerMs, viewport.ViewportWidth, viewport.ViewportHeight,
                viewport.TopMargin, viewport.BottomMargin, track.StringCount, track.Beats, hitChords,
                viewport.FlipStrings);

            // One notification per collection, never one per hit — see
            // SelectionBulkOps. The hit testers read the TRACK, not the
            // selection, so replacing wholesale is the same answer the old
            // clear-then-add-each produced.
            if (additive)
            {
                SelectionBulkOps.AddRange(selection.SelectedNotes, hitNotes);
                SelectionBulkOps.AddRange(selection.SelectedChords, hitChords);
            }
            else
            {
                SelectionBulkOps.ReplaceWith(selection.SelectedNotes, hitNotes);
                SelectionBulkOps.ReplaceWith(selection.SelectedChords, hitChords);
                SelectionBulkOps.ReplaceWith(selection.SelectedHandShapes, Array.Empty<HandShape>());
                SelectionBulkOps.ReplaceWith(selection.SelectedAnchors, Array.Empty<FretAnchor>());
            }

            if (_logger.IsEnabled(AppLogCategory.Input))
            {
                _logger.Info(AppLogCategory.Input,
                    $"Marquee-selected {rect} (additive={additive}). Hit notes={hitNotes.Count}, chords={hitChords.Count}. Selected notes={selection.SelectedNotes.Count}, chords={selection.SelectedChords.Count}.");
            }
        }
        else if (!additive)
        {
            selection.ClearSelections();
            if (_logger.IsEnabled(AppLogCategory.Input))
            {
                _logger.Info(AppLogCategory.Input,
                    $"Left-click empty highway at {FormatPosition(context.SnappedPosition, track)}; cleared selections.");
            }
        }

        viewport.SetMarquee(null);
        _marqueeOrigin = null;
        _marqueeTrack = null;
        _marqueeViewport = null;
        _marqueeDragged = false;
    }

    private void HandleLeftClick(ToolContext context, Chord? hitChord, GuitarNote? hitNote)
    {
        var track = context.Track;
        var selection = track.Selection;

        if (hitChord != null || hitNote != null)
        {
            selection.ClearPreviews();

            if (!context.Modifiers.HasFlag(KeyModifiers.Control) && !context.Modifiers.HasFlag(KeyModifiers.Shift))
            {
                var isAlreadySelected = (hitChord != null && selection.SelectedChords.Contains(hitChord)) ||
                                        (hitNote != null && selection.SelectedNotes.Contains(hitNote));
                if (!isAlreadySelected)
                {
                    selection.SelectedNotes.Clear();
                    selection.SelectedChords.Clear();
                    selection.SelectedHandShapes.Clear();
                    selection.SelectedAnchors.Clear();
                }
            }

            if (context.Modifiers.HasFlag(KeyModifiers.Shift) && _lastClickedPosition != null)
            {
                // Range selection selects everything in the range. It does not
                // honour the Ctrl+V PasteInclude* config flags, which would silently
                // dropped e.g. handshapes from a shift-selection because of a
                // PASTE preference — the flags still filter at paste time
                // (EditingClipboardCoordinator), selection stays complete.
                var hitPos = hitChord?.Position ?? hitNote!.Position;
                var minTime = FractionalPosition.Min(_lastClickedPosition.Value, hitPos);
                var maxTime = FractionalPosition.Max(_lastClickedPosition.Value, hitPos);

                // Four bulk appends, not one Add per element in the range: this
                // is the gesture the O(N²) selection rebuild was measured on.
                SelectionBulkOps.AddRange(selection.SelectedNotes,
                    track.Notes.Where(n => n.Position >= minTime && n.Position <= maxTime));
                SelectionBulkOps.AddRange(selection.SelectedChords,
                    track.Chords.Where(c => c.Position >= minTime && c.Position <= maxTime));
                SelectionBulkOps.AddRange(selection.SelectedHandShapes,
                    track.HandShapes.Where(hs => hs.Position >= minTime && hs.Position <= maxTime));
                SelectionBulkOps.AddRange(selection.SelectedAnchors,
                    track.Anchors.Where(a => a.Position >= minTime && a.Position <= maxTime));
                if (_logger.IsEnabled(AppLogCategory.Input))
                {
                    _logger.Info(AppLogCategory.Input,
                        $"Shift-selected highway range {FormatPosition(minTime, track)} to {FormatPosition(maxTime, track)}. Selected notes={selection.SelectedNotes.Count}, chords={selection.SelectedChords.Count}, handshapes={selection.SelectedHandShapes.Count}, anchors={selection.SelectedAnchors.Count}.");
                }
            }
            else
            {
                if (hitChord != null)
                {
                    SelectionBulkOps.AddRange(selection.SelectedChords, [hitChord]);
                    _lastClickedPosition = hitChord.Position;
                }
                else if (hitNote != null)
                {
                    SelectionBulkOps.AddRange(selection.SelectedNotes, [hitNote]);
                    _lastClickedPosition = hitNote.Position;
                }

                if (context.Modifiers.HasFlag(KeyModifiers.Control))
                    SelectOverlappingTimelineItems(track);
            }

            if (_logger.IsEnabled(AppLogCategory.Input))
            {
                _logger.Info(AppLogCategory.Input,
                    $"Highway selection updated. Selected notes={selection.SelectedNotes.Count}, chords={selection.SelectedChords.Count}, handshapes={selection.SelectedHandShapes.Count}, anchors={selection.SelectedAnchors.Count}.");
            }

            _dragTargetChord = hitChord;
            _dragTargetNote = hitNote;
            _dragTrack = track;

            // The original-state clones are captured LAZILY, on the first
            // actual drag movement (CaptureDragOriginals) — not here on the
            // press. Cloning the whole selection ×4 on every plain click was
            // measurable on large selections, and a click that never drags
            // needs none of it.
            _currentDragDelta = FractionalPosition.Zero;
            _dragState.Start(context.ExactTimeMs, hitChord?.Position ?? hitNote!.Position);
        }
        else
        {
            // Empty lane space: arm a marquee selection. The selection is only
            // touched on release — a plain click (no drag) keeps the old
            // click-to-clear behaviour, a drag selects the rubber-banded
            // notes/chords (additive with Shift).
            _marqueeOrigin = context.Position;
            _marqueeTrack = track;
            _marqueeViewport = context.Viewport;
            _marqueeDragged = false;
            if (_logger.IsEnabled(AppLogCategory.Input))
            {
                _logger.Info(AppLogCategory.Input,
                    $"Left-press empty highway at {FormatPosition(context.SnappedPosition, track)}; marquee armed.");
            }
        }
    }

    /// <summary>A context menu must act on what the user visibly clicked: a
    /// target outside the current selection REPLACES it (matching the
    /// left-click convention above); a target already inside it keeps the
    /// whole selection so the menu verbs act on all of it.</summary>
    private static void EnsureTargetSelected(TrackEditorContext track, Chord? chord, GuitarNote? note)
    {
        var selection = track.Selection;
        var alreadySelected = (chord != null && selection.SelectedChords.Contains(chord)) ||
                              (note != null && selection.SelectedNotes.Contains(note));
        if (alreadySelected) return;

        SelectionBulkOps.ReplaceWith(selection.SelectedNotes,
            note != null ? [note] : Array.Empty<GuitarNote>());
        SelectionBulkOps.ReplaceWith(selection.SelectedChords,
            chord != null ? [chord] : Array.Empty<Chord>());
        SelectionBulkOps.ReplaceWith(selection.SelectedHandShapes, Array.Empty<HandShape>());
        SelectionBulkOps.ReplaceWith(selection.SelectedAnchors, Array.Empty<FretAnchor>());
    }

    /// <summary>
    ///     Builds the context-menu actions — every one an EXISTING command or
    ///     service verb (the same undo actions the keybinds ride) — and hands
    ///     the pure item list to the viewport to present. Times are captured
    ///     into the closures at click time: <c>LastPointerTimeMs</c> may move
    ///     while the menu is open.
    /// </summary>
    private void OpenContextMenu(ToolContext context, Chord? hitChord, GuitarNote? hitNote)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var viewport = context.Viewport;
        var clickedMs = context.ExactTimeMs;
        var snapped = context.SnappedPosition;
        var stringIndex = context.StringIndex;

        var actions = new TimelineContextMenuActions(
            DeleteSelection: () => vm.DeleteSelectionCommand.Execute(null),
            DuplicateSelection: () => vm.DuplicateSelectionAt(clickedMs),
            OpenInInspector: () => vm.ShowInspectorTab(0),
            SetFret: fret => _highwayEditingService.SetSelectedFret(fret),
            AddHandShape: () => vm.GenerateHandShapeCommand.Execute(null),
            AddFhpAnchor: () =>
            {
                var anchor = FretAnchorInteractionTool.AddAnchorAt(
                    snapped, vm.ProjectState.PreviewAnchorFret, track, _history, _logger);
                if (anchor != null) vm.ShellDialogs.OpenFhp(clickedMs, anchor);
                viewport.RequestNextFrameRendering();
            },
            AddNoteHere: () =>
            {
                _highwayEditingService.ApplyPlacement(snapped, stringIndex);
                viewport.RequestNextFrameRendering();
            },
            Paste: () => vm.PasteAt(clickedMs),
            SelectAllInView: () =>
            {
                SelectAllInView(vm, viewport, track);
                viewport.RequestNextFrameRendering();
            },
            AddEventAtTime: () => vm.ShellDialogs.OpenEvent(clickedMs, track.Arrangement),
            ApplyDlcBuilderFixes: () =>
            {
                vm.ApplyDlcBuilderFixesCommand.Execute(null);
                viewport.RequestNextFrameRendering();
            });

        var items = hitChord != null || hitNote != null
            ? TimelineContextMenuBuilder.ForSound(actions)
            : TimelineContextMenuBuilder.ForEmptyHighway(actions);
        viewport.ShowContextMenu(items);
    }

    /// <summary>"Select all in view": the marquee's own collectors over the
    /// whole viewport rectangle — selection primitives, not a new edit verb.
    /// Takes vm/viewport as VALUES (not the shared mutable ToolContext), so
    /// the menu closure sees the state at invoke time, on the right viewport.</summary>
    private static void SelectAllInView(
        ViewModels.Shell.MainWindowViewModel vm, IChartViewport viewport, TrackEditorContext track)
    {
        var selection = track.Selection;
        var rect = new Rect(0, 0, viewport.ViewportWidth, viewport.ViewportHeight);

        var hitNotes = new List<GuitarNote>();
        var hitChords = new List<Chord>();
        MarqueeHitTester.CollectNotes(rect, track.Notes, vm.ProjectState.TimelineViewAnchorEffectiveMs,
            vm.ProjectState.PixelsPerMs, viewport.ViewportWidth, viewport.ViewportHeight,
            viewport.TopMargin, viewport.BottomMargin, track.StringCount, track.Beats, hitNotes,
            viewport.FlipStrings);
        MarqueeHitTester.CollectChords(rect, track.Chords, vm.ProjectState.TimelineViewAnchorEffectiveMs,
            vm.ProjectState.PixelsPerMs, viewport.ViewportWidth, viewport.ViewportHeight,
            viewport.TopMargin, viewport.BottomMargin, track.StringCount, track.Beats, hitChords,
            viewport.FlipStrings);

        SelectionBulkOps.ReplaceWith(selection.SelectedNotes, hitNotes);
        SelectionBulkOps.ReplaceWith(selection.SelectedChords, hitChords);
        SelectionBulkOps.ReplaceWith(selection.SelectedHandShapes, Array.Empty<HandShape>());
        SelectionBulkOps.ReplaceWith(selection.SelectedAnchors, Array.Empty<FretAnchor>());
    }

    private void SelectOverlappingTimelineItems(TrackEditorContext track)
    {
        var selection = track.Selection;
        if (!selection.SelectedNotes.Any() && !selection.SelectedChords.Any()) return;

        // The guard above already refused an empty selection, so this is never
        // null — and it is the shared walk rather than a sentinel, because a
        // sound really sitting at beat 999,999 would win the Min and collapse
        // the range to nothing.
        var minTime = DragBounds.Earliest(
            selection.SelectedNotes.Select(n => n.Position),
            selection.SelectedChords.Select(c => c.Position)) ?? FractionalPosition.Zero;
        var maxTime = FractionalPosition.Max(
            selection.SelectedNotes.Any()
                ? selection.SelectedNotes.Max(n => n.EndPosition)
                : FractionalPosition.Zero,
            selection.SelectedChords.Any()
                ? selection.SelectedChords.Max(c => c.EndPosition)
                : FractionalPosition.Zero);

        SelectionBulkOps.AddRange(selection.SelectedHandShapes,
            track.HandShapes.Where(hs => hs.EndPosition > minTime && hs.Position <= maxTime));
        SelectionBulkOps.AddRange(selection.SelectedAnchors,
            track.Anchors.Where(anchor => anchor.Position >= minTime && anchor.Position <= maxTime));
        if (_logger.IsEnabled(AppLogCategory.Input))
        {
            _logger.Info(AppLogCategory.Input,
                $"Ctrl-selection expanded overlapping timeline items. Range {FormatPosition(minTime, track)} to {FormatPosition(maxTime, track)}. Selected handshapes={selection.SelectedHandShapes.Count}, anchors={selection.SelectedAnchors.Count}.");
        }
    }

    private static string FormatPosition(FractionalPosition position, TrackEditorContext track)
        => position.FormatWithMs(track.Beats);

    /// <summary>Clone the drag targets' original states — the undo baseline
    /// the previews and the commit are computed against. Deferred to the first
    /// drag movement (P2-3): on the press it was 4 whole-selection clone
    /// passes a plain click never used.</summary>
    private void CaptureDragOriginals(ISelectionService selection)
    {
        _dragOriginalStates = selection.SelectedNotes.Select(n => n.Clone()).ToList();
        _dragOriginalChordStates = selection.SelectedChords.Select(c => c.Clone()).ToList();
        _dragOriginalHandShapes = selection.SelectedHandShapes.Select(hs => hs.Clone()).ToList();
        _dragOriginalAnchors = selection.SelectedAnchors.Select(a => a.Clone()).ToList();
    }

    private void OnHistoryChanged()
    {
        // Unconditional: the selection contents may have changed even though
        // the delta did not.
        if (_dragState.IsDragging)
        {
            RefreshDragPreviews(_currentDragDelta);
            _lastAppliedDragDelta = _currentDragDelta;
        }
    }

    private void RefreshDragPreviews(FractionalPosition delta)
    {
        if (_dragTrack == null) return;
        var selection = _dragTrack.Selection;

        selection.PreviewNotes.Clear();
        selection.PreviewChords.Clear();
        selection.PreviewHandShapes.Clear();
        selection.PreviewAnchors.Clear();

        var noteCount = _dragTargetNote != null
            ? Math.Min(selection.SelectedNotes.Count, _dragOriginalStates.Count)
            : 0;
        var chordCount = _dragTargetNote == null && _dragTargetChord != null
            ? Math.Min(selection.SelectedChords.Count, _dragOriginalChordStates.Count)
            : 0;
        var handShapeCount = Math.Min(selection.SelectedHandShapes.Count, _dragOriginalHandShapes.Count);
        var anchorCount = Math.Min(selection.SelectedAnchors.Count, _dragOriginalAnchors.Count);

        // Shorten the DELTA for the whole payload rather than flooring each item
        // at zero: a per-item floor stacks everything that went negative onto
        // beat 0 and leaves the rest where the drag put them, so a selection
        // dragged off the front of the song comes back with its spacing gone —
        // and the preview is what the release commits. See DragBounds.
        delta = DragBounds.ClampDeltaToStart(delta, DragBounds.Earliest(
            _dragOriginalStates.Take(noteCount).Select(n => n.Position),
            _dragOriginalChordStates.Take(chordCount).Select(c => c.Position),
            _dragOriginalHandShapes.Take(handShapeCount).Select(h => h.Position),
            _dragOriginalAnchors.Take(anchorCount).Select(a => a.Position)));

        for (var i = 0; i < noteCount; i++)
        {
            var previewNote = selection.SelectedNotes[i].Clone();
            MoveNotePreview(previewNote, _dragOriginalStates[i].Position + delta);
            selection.PreviewNotes.Add(previewNote);
        }

        for (var i = 0; i < chordCount; i++)
        {
            var previewChord = selection.SelectedChords[i].Clone();
            MoveChordPreview(previewChord, _dragOriginalChordStates[i].Position + delta);
            selection.PreviewChords.Add(previewChord);
        }

        for (var i = 0; i < handShapeCount; i++)
        {
            var current = selection.SelectedHandShapes[i];
            var previewHs = current.Clone();
            var duration = FractionalPosition.Max(FractionalPosition.Zero, current.EndPosition - current.Position);
            previewHs.Position = _dragOriginalHandShapes[i].Position + delta;
            previewHs.EndPosition = previewHs.Position + duration;
            selection.PreviewHandShapes.Add(previewHs);
        }

        for (var i = 0; i < anchorCount; i++)
        {
            var previewA = selection.SelectedAnchors[i].Clone();
            previewA.Position = _dragOriginalAnchors[i].Position + delta;
            selection.PreviewAnchors.Add(previewA);
        }
    }

    private static void MoveNotePreview(GuitarNote previewNote, FractionalPosition targetPosition)
    {
        var bendDelta = targetPosition - previewNote.Position;
        previewNote.Position = targetPosition;
        foreach (var bend in previewNote.BendValues) bend.Position += bendDelta;
    }

    private static void MoveChordPreview(Chord previewChord, FractionalPosition targetPosition)
    {
        var bendDelta = targetPosition - previewChord.Position;
        previewChord.Position = targetPosition;
        foreach (var chordNote in previewChord.ChordNotes)
        {
            foreach (var bend in chordNote.BendValues)
                bend.Position += bendDelta;
        }
    }

    private void ResetState(ToolContext context)
    {
        // Defensive marquee teardown for paths that bypass CommitMarquee
        // (e.g. a lost pointer capture): never leave a stale rubber band.
        if (_marqueeOrigin != null || _marqueeViewport != null)
        {
            _marqueeViewport?.SetMarquee(null);
            _marqueeOrigin = null;
            _marqueeTrack = null;
            _marqueeViewport = null;
            _marqueeDragged = false;
        }

        _pendingMenu = false;
        _pendingMenuChord = null;
        _pendingMenuNote = null;

        var selection = (_dragTrack ?? context.Track).Selection;
        _dragState.Stop();
        _dragTargetNote = null;
        _dragTargetChord = null;
        _dragTrack = null;
        _currentDragDelta = FractionalPosition.Zero;
        _lastAppliedDragDelta = null;
        // Drop the drag-original clones — this tool outlives every drag (one
        // instance per coordinator), so keeping them rooted the last drag's
        // whole cloned selection for the lifetime of the editor.
        _dragOriginalStates = [];
        _dragOriginalChordStates = [];
        _dragOriginalHandShapes = [];
        _dragOriginalAnchors = [];
        if (selection.PreviewNotes.Count > 0) selection.PreviewNotes.Clear();
        if (selection.PreviewChords.Count > 0) selection.PreviewChords.Clear();
        selection.PreviewHandShapes.Clear();
        selection.PreviewAnchors.Clear();

        // FIX: Globally wipe the hover states
        selection.ClearHovers();

        context.Viewport.RequestNextFrameRendering();
    }
}
