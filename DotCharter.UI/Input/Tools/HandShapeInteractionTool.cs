using Avalonia.Input;
using DotCharter.Services.Editing.HandShapes;

namespace DotCharter.UI.Input.Tools;

public class HandShapeInteractionTool : IEditorTool
{
    private readonly DragState _dragState = new();
    private readonly HandShapeDrawGesture _draw;
    private readonly IHighwayEditingService _highwayEditingService;
    private readonly IAppLogger _logger;
    private List<Chord> _dragOriginalHandShapeChords = [];
    private List<GuitarNote> _dragOriginalHandShapeNotes = [];
    private List<HandShape> _dragOriginalHandShapes = [];
    private TrackEditorContext? _dragTrack;

    private FractionalPosition? _lastClickedPos;

    // The suggested bars as the timeline draws them — the same latch, so a click
    // accepts exactly the dashed bar under the pointer.
    private readonly HandShapeSuggestionLatch _suggested = new();
    private readonly HandShapeSuggestionAcceptor _acceptor;
    private readonly ILocalizationService _localization;
    private HandShape? _hintedSuggestion;

    public HandShapeInteractionTool(IHighwayEditingService highwayEditingService,
        IFingeringGenerationService fingeringService, IHistoryService history, ILocalizationService localization,
        IAppLogger logger)
    {
        _highwayEditingService = highwayEditingService;
        _localization = localization;
        _logger = logger;
        _draw = new HandShapeDrawGesture(fingeringService, highwayEditingService);
        _acceptor = new HandShapeSuggestionAcceptor(history, logger);
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

        var topMargin = context.Viewport.TopMargin;
        var bottomMargin = context.Viewport.BottomMargin;

        var hs = ChartHitTester.HitTestHandShape(context.Position, track.HandShapes,
            vm.ProjectState.TimelineViewAnchorEffectiveMs,
            vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth, context.Viewport.ViewportHeight,
            topMargin, bottomMargin, track.Beats);

        if (context.Properties.IsRightButtonPressed)
        {
            if (hs != null && context.Modifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl+Right-click on an existing handshape splits it at the
                // (snapped) cursor position into two contiguous handshapes
                // sharing the original template.
                var splitPos = context.SnappedPosition;
                if (splitPos > hs.Position && splitPos < hs.EndPosition)
                {
                    var left = new HandShape
                    {
                        Position = hs.Position, EndPosition = splitPos,
                        Name = hs.Name, TemplateId = hs.TemplateId
                    };
                    var right = new HandShape
                    {
                        Position = splitPos, EndPosition = hs.EndPosition,
                        Name = hs.Name, TemplateId = hs.TemplateId
                    };
                    _logger.Info(AppLogCategory.Editing,
                        $"Ctrl+Right-click split handshape at {FormatPosition(splitPos, track)}; original=[{FormatPosition(hs.Position, track)}-{FormatPosition(hs.EndPosition, track)}].");
                    _highwayEditingService.CommitHandShapeDragAndDrop(
                        [], [],
                        [], [],
                        [hs], [left, right],
                        [], []);
                    context.Viewport.RequestNextFrameRendering();
                    return;
                }
                _logger.Info(AppLogCategory.Editing,
                    $"Ctrl+Right-click split ignored; cursor outside handshape bounds. cursor={FormatPosition(splitPos, track)}, hs=[{FormatPosition(hs.Position, track)}-{FormatPosition(hs.EndPosition, track)}].");
            }
            else if (hs != null)
            {
                _logger.Info(AppLogCategory.Editing,
                    $"Right-click existing handshape; removing name='{hs.Name}', position={FormatPosition(hs.Position, track)}, end={FormatPosition(hs.EndPosition, track)}.");
                _highwayEditingService.RemoveHandShape(hs);
            }
            else
            {
                // Over empty lane the press only ARMS: a drag draws the span, a plain
                // click spans to the next fret change or chord — both decided on the
                // release (HandShapeDrawGesture).
                _draw.Arm(track, context.Position, context.SnappedPosition);
                _dragTrack = track;
            }

            context.Viewport.RequestNextFrameRendering();
            return;
        }

        if (context.Properties.IsLeftButtonPressed)
        {
            if (hs != null)
            {
                if (!context.Modifiers.HasFlag(KeyModifiers.Control) && !context.Modifiers.HasFlag(KeyModifiers.Shift))
                {
                    if (!selection.SelectedHandShapes.Contains(hs))
                        selection.SelectedHandShapes.Clear();
                }

                if (context.Modifiers.HasFlag(KeyModifiers.Shift) && _lastClickedPos != null)
                {
                    var hitPos = hs.Position;
                    var minTime = FractionalPosition.Min(_lastClickedPos.Value, hitPos);
                    var maxTime = FractionalPosition.Max(_lastClickedPos.Value, hitPos);

                    SelectionBulkOps.AddRange(selection.SelectedHandShapes,
                        track.HandShapes.Where(h => h.Position >= minTime && h.Position <= maxTime));
                }
                else
                {
                    SelectionBulkOps.AddRange(selection.SelectedHandShapes, [hs]);
                    _lastClickedPos = hs.Position;
                }

                _dragOriginalHandShapes = selection.SelectedHandShapes.Select(h => h.Clone()).ToList();

                _dragOriginalHandShapeNotes = track.Notes
                    .Where(n => _dragOriginalHandShapes.Any(h =>
                        n.Position >= h.Position && n.Position < h.EndPosition)).ToList();
                _dragOriginalHandShapeChords = track.Chords
                    .Where(c => _dragOriginalHandShapes.Any(h =>
                        c.Position >= h.Position && c.Position < h.EndPosition)).ToList();

                _dragTrack = track;
                _dragState.Start(context.ExactTimeMs, hs.Position);
                _logger.Info(AppLogCategory.Editing,
                    $"Selected handshape for drag. SelectedHandShapes={selection.SelectedHandShapes.Count}, enclosedNotes={_dragOriginalHandShapeNotes.Count}, enclosedChords={_dragOriginalHandShapeChords.Count}, target name='{hs.Name}', position={FormatPosition(hs.Position, track)}, end={FormatPosition(hs.EndPosition, track)}.");
            }
            else if (HitSuggestion(context) is { } hit && track.Level is { } level)
            {
                // A click on a dashed suggested bar accepts it: plain, or an
                // arpeggio with Alt — one undo node either way.
                _acceptor.Accept(track.Arrangement, level, [hit.Suggestion], context.Modifiers.HasFlag(KeyModifiers.Alt));
                _hintedSuggestion = null;
            }
            else
            {
                selection.SelectedHandShapes.Clear();
                _logger.Info(AppLogCategory.Editing,
                    $"Left-click empty handshape bar at {FormatPosition(context.SnappedPosition, track)}; cleared selected handshapes.");
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

        if (_draw.IsArmed)
        {
            selection.PreviewHandShapes.Clear();
            if (_draw.Preview(context.Position, context.SnappedPosition) is { } preview)
                selection.PreviewHandShapes.Add(preview);
            context.Viewport.RequestNextFrameRendering();
            return;
        }

        if (context.Properties.IsLeftButtonPressed && _dragState.IsDragging && _dragOriginalHandShapes.Any())
        {
            _dragState.HasDragged = true;
            var dragSelection = (_dragTrack ?? track).Selection;
            dragSelection.PreviewNotes.Clear();
            dragSelection.PreviewChords.Clear();
            dragSelection.PreviewHandShapes.Clear();

            var delta = GetClampedDelta(context);

            foreach (var origHs in _dragOriginalHandShapes)
            {
                var previewHs = origHs.Clone();
                previewHs.Position += delta;
                previewHs.EndPosition += delta;
                dragSelection.PreviewHandShapes.Add(previewHs);
            }

            foreach (var origN in _dragOriginalHandShapeNotes)
            {
                var pn = origN.Clone();
                pn.Position += delta;
                foreach (var b in pn.BendValues) b.Position += delta;
                dragSelection.PreviewNotes.Add(pn);
            }

            foreach (var origC in _dragOriginalHandShapeChords)
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
        }
        else
        {
            var topMargin = context.Viewport.TopMargin;
            var bottomMargin = context.Viewport.BottomMargin;

            var hoveredHandShape = ChartHitTester.HitTestHandShape(context.Position, track.HandShapes,
                vm.ProjectState.TimelineViewAnchorEffectiveMs, vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth,
                context.Viewport.ViewportHeight,
                topMargin, bottomMargin, track.Beats);

            selection.HoveredHandShape = hoveredHandShape;
            HintSuggestion(context, hoveredHandShape == null);
        }

        context.Viewport.RequestNextFrameRendering();
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context)
    {
        var dragTrack = _dragTrack ?? context.Track;
        var dragSelection = dragTrack.Selection;

        if (_draw.IsArmed)
        {
            dragSelection.PreviewHandShapes.Clear();
            if (_draw.Release(context.Position, context.SnappedPosition) is { } drawn)
            {
                _logger.Info(AppLogCategory.Editing,
                    $"Right-click handshape lane; created name='{drawn.Name}', position={FormatPosition(drawn.Position, dragTrack)}, end={FormatPosition(drawn.EndPosition, dragTrack)}, templateId={drawn.TemplateId}.");
            }
        }
        else if (_dragState.IsDragging && _dragState.HasDragged && _dragOriginalHandShapes.Any())
        {
            var targetNotes = new List<GuitarNote>();
            var finalNewNotes = new List<GuitarNote>();
            var targetChords = new List<Chord>();
            var finalNewChords = new List<Chord>();
            var targetHs = dragSelection.SelectedHandShapes.ToList();
            var delta = GetClampedDelta(context);
            var newHs = _dragOriginalHandShapes.Select(h =>
            {
                var clone = h.Clone();
                clone.Position += delta;
                clone.EndPosition += delta;
                return clone;
            }).ToList();

            if (_dragOriginalHandShapeNotes.Count > 0)
            {
                targetNotes.AddRange(_dragOriginalHandShapeNotes);

                var previewNotesFromHs = _dragOriginalHandShapeNotes.Select(n =>
                {
                    var clone = n.Clone();
                    clone.Position += delta;
                    foreach (var b in clone.BendValues) b.Position += delta;
                    return clone;
                }).ToList();
                finalNewNotes.AddRange(previewNotesFromHs);
            }

            if (_dragOriginalHandShapeChords.Count > 0)
            {
                targetChords.AddRange(_dragOriginalHandShapeChords);

                var previewChordsFromHs = _dragOriginalHandShapeChords.Select(c =>
                {
                    var clone = c.Clone();
                    clone.Position += delta;
                    foreach (var cn in clone.ChordNotes)
                    {
                        foreach (var b in cn.BendValues)
                            b.Position += delta;
                    }

                    return clone;
                }).ToList();
                finalNewChords.AddRange(previewChordsFromHs);
            }

            var committed = _highwayEditingService.CommitHandShapeDragAndDrop(
                targetNotes, finalNewNotes,
                targetChords, finalNewChords,
                targetHs, newHs,
                [], []
            );
            if (committed)
            {
                for (var i = 0; i < targetHs.Count && i < newHs.Count; i++)
                {
                    _logger.Info(AppLogCategory.Editing,
                        $"Moved handshape from name='{targetHs[i].Name}', position={FormatPosition(targetHs[i].Position, dragTrack)}, end={FormatPosition(targetHs[i].EndPosition, dragTrack)} to position={FormatPosition(newHs[i].Position, dragTrack)}, end={FormatPosition(newHs[i].EndPosition, dragTrack)}.");
                }
            }
        }

        ResetState(context);
    }

    public void OnPointerExited(PointerEventArgs e, ToolContext context)
    {
        if (!_dragState.IsDragging && !_draw.IsArmed) ResetState(context);
    }

    private void ResetState(ToolContext context)
    {
        var selection = (_dragTrack ?? context.Track).Selection;
        _draw.Cancel();
        _dragState.Stop();
        _dragOriginalHandShapes.Clear();
        _dragOriginalHandShapeNotes.Clear();
        _dragOriginalHandShapeChords.Clear();
        selection.PreviewHandShapes.Clear();
        selection.PreviewNotes.Clear();
        selection.PreviewChords.Clear();
        _dragTrack = null;

        // FIX: Globally wipe the hover states
        selection.ClearHovers();

        context.Viewport.RequestNextFrameRendering();
    }

    /// <summary>The suggested bar under the pointer and the suggestion behind it, through the
    /// committed bars' own hit test.</summary>
    private (HandShape Bar, SuggestedHandShape Suggestion)? HitSuggestion(ToolContext context)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var bars = _suggested.Get(track.Arrangement, track.Level, vm.ProjectState.ShowSuggestedHandShapes,
            vm.ChartEditor.HistoryRevision, Environment.TickCount64);
        var hit = ChartHitTester.HitTestHandShape(context.Position, bars, vm.ProjectState.TimelineViewAnchorEffectiveMs,
            vm.ProjectState.PixelsPerMs, context.Viewport.ViewportWidth, context.Viewport.ViewportHeight,
            context.Viewport.TopMargin, context.Viewport.BottomMargin, track.Beats);
        return hit == null ? null : (hit, _suggested.Suggestions[bars.IndexOf(hit)]);
    }

    /// <summary>Says how to accept the suggestion under the pointer, once per suggestion.</summary>
    private void HintSuggestion(ToolContext context, bool overEmptyLane)
    {
        var bar = overEmptyLane ? HitSuggestion(context)?.Bar : null;
        if (ReferenceEquals(bar, _hintedSuggestion)) return;
        _hintedSuggestion = bar;
        if (bar != null)
            context.ViewModel.ProjectState.SetMessage(_localization.Format("Status_SuggestedHandShapeHint", bar.Name));
    }

    private FractionalPosition GetClampedDelta(ToolContext context)
    {
        return DragBounds.ClampDeltaToStart(_dragState.GetDelta(context), DragBounds.Earliest(
            _dragOriginalHandShapeNotes.Select(n => n.Position),
            _dragOriginalHandShapeChords.Select(c => c.Position),
            _dragOriginalHandShapes.Select(h => h.Position)));
    }

    private static string FormatPosition(FractionalPosition position, TrackEditorContext track)
        => position.FormatWithMs(track.Beats);
}
