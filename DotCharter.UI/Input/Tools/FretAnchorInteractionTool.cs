using Avalonia.Input;
using DotCharter.Services.Editing.Anchors;
using DotCharter.UI.Constants;

namespace DotCharter.UI.Input.Tools;

public class FretAnchorInteractionTool : IEditorTool
{
    private readonly DragState _dragState = new();
    private readonly IHighwayEditingService _highwayEditingService;
    private readonly IHistoryService _historyManager;
    private readonly IAppLogger _logger;

    private List<FretAnchor> _dragOriginalAnchors = [];
    private List<Chord> _dragOriginalChords = [];
    private List<GuitarNote> _dragOriginalNotes = [];
    private TrackEditorContext? _dragTrack;
    private FretAnchor? _lastClickedAnchor;

    private FractionalPosition? _lastClickedPos;
    private long _lastClickTicks;
    private FractionalPosition _lastHoverTime;

    public FretAnchorInteractionTool(
        IHighwayEditingService highwayEditingService,
        IHistoryService historyManager,
        IAppLogger logger)
    {
        _highwayEditingService = highwayEditingService;
        _historyManager = historyManager;
        _logger = logger;
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

        var laneAreaHeight = context.Viewport.ViewportHeight - context.Viewport.TopMargin -
                             context.Viewport.BottomMargin;
        var fhpY = context.Viewport.TopMargin + laneAreaHeight +
                   ChartEditorConstants.RenderMetrics.FhpYOffset;

        var anchor = ChartHitTester.HitTestAnchor(context.Position, track.Anchors,
            vm.ProjectState.TimelineViewAnchorEffectiveMs,
            vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth, fhpY, track.Beats);

        if (context.Properties.IsRightButtonPressed)
        {
            var newAnchor = AddAnchorAtSnappedPosition(context, track);
            if (newAnchor != null) vm.ShellDialogs.OpenFhp(context.ExactTimeMs, newAnchor);
            _logger.Info(AppLogCategory.Editing,
                $"Right-click FHP lane at {FormatPosition(context.SnappedPosition, track)}. CreatedAnchor={newAnchor != null}, previewFret={vm.ProjectState.PreviewAnchorFret}.");
            context.Viewport.RequestNextFrameRendering();
            return;
        }

        if (context.Properties.IsLeftButtonPressed)
        {
            if (anchor != null)
            {
                var now = DateTime.UtcNow.Ticks;
                if (now - _lastClickTicks < TimeSpan.FromMilliseconds(500).Ticks && _lastClickedAnchor == anchor)
                {
                    // FIX: Pass the exact mouse time so the dialog snaps to your cursor.
                    // If you clicked the head, it edits it. If you clicked the tail, it makes a new one!
                    vm.ShellDialogs.OpenFhp(context.ExactTimeMs, anchor);
                    _logger.Info(AppLogCategory.Editing,
                        $"Double-clicked FHP anchor; opening dialog for fret={anchor.Fret}, width={anchor.Width}, position={FormatPosition(anchor.Position, track)}.");
                    _dragState.Stop();
                    selection.SelectedAnchors.Clear();
                    context.Viewport.RequestNextFrameRendering();
                    return;
                }

                _lastClickTicks = now;
                _lastClickedAnchor = anchor;

                if (!context.Modifiers.HasFlag(KeyModifiers.Control) && !context.Modifiers.HasFlag(KeyModifiers.Shift))
                {
                    if (!selection.SelectedAnchors.Contains(anchor))
                        selection.SelectedAnchors.Clear();
                }

                if (context.Modifiers.HasFlag(KeyModifiers.Shift) && _lastClickedPos != null)
                {
                    var hitPos = anchor.Position;
                    var minTime = FractionalPosition.Min(_lastClickedPos.Value, hitPos);
                    var maxTime = FractionalPosition.Max(_lastClickedPos.Value, hitPos);

                    SelectionBulkOps.AddRange(selection.SelectedAnchors,
                        track.Anchors.Where(a => a.Position >= minTime && a.Position <= maxTime));
                }
                else
                {
                    SelectionBulkOps.AddRange(selection.SelectedAnchors, [anchor]);
                    _lastClickedPos = anchor.Position;
                }

                _dragOriginalAnchors = selection.SelectedAnchors.Select(a => a.Clone()).ToList();
                _dragOriginalNotes = selection.SelectedNotes.Select(n => n.Clone()).ToList();
                _dragOriginalChords = selection.SelectedChords.Select(c => c.Clone()).ToList();

                _dragTrack = track;
                _dragState.Start(context.ExactTimeMs, anchor.Position);
                _logger.Info(AppLogCategory.Editing,
                    $"Selected FHP anchor for drag. SelectedAnchors={selection.SelectedAnchors.Count}, anchor fret={anchor.Fret}, width={anchor.Width}, position={FormatPosition(anchor.Position, track)}.");
            }
            else
            {
                // Fast placement: a plain left-click drops the anchor
                // silently (fret/width inherited from the previous anchor)
                // and selects it. The dialog stays one right-click or
                // double-click away — forcing a modal on every placement
                // made charting FHP runs painfully slow.
                selection.SelectedAnchors.Clear();
                var newAnchor = AddAnchorAtSnappedPosition(context, track);
                if (newAnchor != null)
                {
                    selection.SelectedAnchors.Add(newAnchor);
                    _lastClickedPos = newAnchor.Position;
                }

                _logger.Info(AppLogCategory.Editing,
                    $"Left-click empty FHP lane at {FormatPosition(context.SnappedPosition, track)}. CreatedAnchor={newAnchor != null} (silent placement).");
            }

            context.Viewport.RequestNextFrameRendering();
        }
    }

    public void OnPointerMoved(PointerEventArgs e, ToolContext context)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var selection = track.Selection;
        selection.PreviewEventPoints.Clear();

        if (context.Properties.IsLeftButtonPressed && _dragState.IsDragging && _dragOriginalAnchors.Any())
        {
            _dragState.HasDragged = true;
            RefreshDragPreviews(context, track);
        }
        else
        {
            var laneAreaHeight = context.Viewport.ViewportHeight - context.Viewport.TopMargin -
                                 context.Viewport.BottomMargin;
            var fhpY = context.Viewport.TopMargin + laneAreaHeight +
                       ChartEditorConstants.RenderMetrics.FhpYOffset;

            var hoveredAnchor = ChartHitTester.HitTestAnchor(context.Position, track.Anchors,
                vm.ProjectState.TimelineViewAnchorEffectiveMs, vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth, fhpY,
                track.Beats);

            selection.HoveredAnchor = hoveredAnchor;

            if (_lastHoverTime != context.SnappedPosition || !selection.PreviewAnchors.Any())
            {
                selection.PreviewAnchors.Clear();

                var prevAnchor = track.Anchors.LastOrDefault(a => a.Position <= context.SnappedPosition);
                var defaultFret = vm.ProjectState.PreviewAnchorFret > 0
                    ? vm.ProjectState.PreviewAnchorFret
                    : prevAnchor?.Fret ?? 1;
                var defaultWidth = prevAnchor?.Width ?? 4;

                selection.PreviewAnchors.Add(new FretAnchor
                {
                    Position = context.SnappedPosition,
                    Fret = defaultFret,
                    Width = defaultWidth
                });

                _lastHoverTime = context.SnappedPosition;
            }
        }

        context.Viewport.RequestNextFrameRendering();
    }

    /// <summary>
    ///     Rebuild the drag previews from <paramref name="context" />'s CURRENT
    ///     pointer position. Called from the throttled move AND from the release,
    ///     because a drag ends where the pointer was released rather than up to one
    ///     16 ms throttle window behind it (<c>ToolCoordinator</c>) — and the
    ///     previews are what the commit writes.
    /// </summary>
    private void RefreshDragPreviews(ToolContext context, TrackEditorContext track)
    {
        var dragSelection = (_dragTrack ?? track).Selection;
        dragSelection.PreviewNotes.Clear();
        dragSelection.PreviewChords.Clear();
        dragSelection.PreviewAnchors.Clear();

        var delta = DragBounds.ClampDeltaToStart(_dragState.GetDelta(context), DragBounds.Earliest(
            _dragOriginalNotes.Select(n => n.Position),
            _dragOriginalChords.Select(c => c.Position),
            _dragOriginalAnchors.Select(a => a.Position)));

        foreach (var origN in _dragOriginalNotes)
        {
            var pn = origN.Clone();
            pn.Position += delta;
            foreach (var b in pn.BendValues) b.Position += delta;
            dragSelection.PreviewNotes.Add(pn);
        }

        foreach (var origC in _dragOriginalChords)
        {
            var pc = origC.Clone();
            pc.Position += delta;
            foreach (var cn in pc.ChordNotes)
            {
                foreach (var b in cn.BendValues)
                    b.Position += delta;
            }

            dragSelection.PreviewChords.Add(pc);
        }

        foreach (var origA in _dragOriginalAnchors)
        {
            var pa = origA.Clone();
            pa.Position += delta;
            dragSelection.PreviewAnchors.Add(pa);
        }
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context)
    {
        var dragTrack = _dragTrack ?? context.Track;
        var dragSelection = dragTrack.Selection;
        if (_dragState.IsDragging && _dragState.HasDragged && _dragOriginalAnchors.Any())
        {
            // See RefreshDragPreviews: the last throttled move may be a frame
            // behind the release, and the delta is grid-snapped, so a flick that
            // crossed a grid line inside that window dropped the anchor a line
            // short of where the user let go.
            RefreshDragPreviews(context, dragTrack);
        }

        if (_dragState.IsDragging && _dragState.HasDragged && dragSelection.PreviewAnchors.Any())
        {
            var targetNotes = dragSelection.SelectedNotes.ToList();
            var finalNewNotes = dragSelection.PreviewNotes.ToList();
            var targetChords = dragSelection.SelectedChords.ToList();
            var finalNewChords = dragSelection.PreviewChords.ToList();

            var targetAnchors = dragSelection.SelectedAnchors.ToList();
            var newAnchors = dragSelection.PreviewAnchors.ToList();

            var committed = _highwayEditingService.CommitHandShapeDragAndDrop(
                targetNotes, finalNewNotes,
                targetChords, finalNewChords,
                [], [],
                targetAnchors, newAnchors
            );
            if (committed)
            {
                for (var i = 0; i < targetAnchors.Count && i < newAnchors.Count; i++)
                {
                    _logger.Info(AppLogCategory.Editing,
                        $"Moved FHP anchor from position={FormatPosition(targetAnchors[i].Position, dragTrack)}, fret={targetAnchors[i].Fret}, width={targetAnchors[i].Width} to position={FormatPosition(newAnchors[i].Position, dragTrack)}, fret={newAnchors[i].Fret}, width={newAnchors[i].Width}.");
                }
            }
        }

        ResetState(context);
    }

    public void OnPointerExited(PointerEventArgs e, ToolContext context)
    {
        if (!_dragState.IsDragging) ResetState(context);
    }

    private FretAnchor? AddAnchorAtSnappedPosition(ToolContext context, TrackEditorContext track) =>
        AddAnchorAt(context.SnappedPosition, context.ViewModel.ProjectState.PreviewAnchorFret,
            track, _historyManager, _logger);

    /// <summary>
    ///     THE anchor-placement verb — one undoable <see cref="AddAnchorsAction" />
    ///     with the fret derived by <see cref="AnchorFretSuggester" />. Internal
    ///     static, taking VALUES rather than the shared mutable
    ///     <see cref="ToolContext" />, so the timeline context menu ("Add FHP
    ///     anchor here", <c>HighwayInteractionTool</c>) can capture the CLICKED
    ///     position into its closure and still place through exactly this code
    ///     path rather than a second copy.
    /// </summary>
    internal static FretAnchor? AddAnchorAt(FractionalPosition snappedPosition, int previewAnchorFret,
        TrackEditorContext track, IHistoryService history, IAppLogger logger)
    {
        var existingExact = track.Anchors.FirstOrDefault(a => a.Position == snappedPosition);
        if (existingExact != null) return null;

        var prevAnchor = track.Anchors.LastOrDefault(a => a.Position <= snappedPosition);

        // An explicit preview fret is the user having already said which fret
        // they want (they wheeled it before clicking), so it always wins.
        // Otherwise DERIVE it from the notes this anchor will govern rather than
        // copying the previous anchor's — see AnchorFretSuggester for why: every
        // anchor in a sample of real charting was corrected within 3.4 seconds,
        // five of six on the fret.
        var defaultFret = previewAnchorFret > 0
            ? previewAnchorFret
            : AnchorFretSuggester.Suggest(
                snappedPosition, track.Notes, track.Chords, track.Anchors,
                track.Arrangement.Capo, prevAnchor?.Fret);
        var defaultWidth = prevAnchor?.Width ?? 4;

        var newAnchor = new FretAnchor
        {
            Position = snappedPosition,
            Fret = defaultFret,
            Width = defaultWidth
        };

        history.Do(new AddAnchorsAction(track.Anchors, new[] { newAnchor }));
        logger.Info(AppLogCategory.Editing,
            $"Created FHP anchor position={FormatPosition(newAnchor.Position, track)}, fret={newAnchor.Fret}, width={newAnchor.Width}, previousAnchorFret={prevAnchor?.Fret.ToString() ?? "none"}.");
        return newAnchor;
    }

    private void ResetState(ToolContext context)
    {
        var selection = (_dragTrack ?? context.Track).Selection;
        _dragState.Stop();
        _dragOriginalAnchors.Clear();
        _dragOriginalNotes.Clear();
        _dragOriginalChords.Clear();
        selection.PreviewAnchors.Clear();
        selection.PreviewNotes.Clear();
        selection.PreviewChords.Clear();
        selection.ClearHovers();
        _dragTrack = null;

        context.Viewport.RequestNextFrameRendering();
    }

    private static string FormatPosition(FractionalPosition position, TrackEditorContext track)
        => position.FormatWithMs(track.Beats);
}
