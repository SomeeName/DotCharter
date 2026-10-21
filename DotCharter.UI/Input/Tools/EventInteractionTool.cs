using Avalonia.Input;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Editing.Linking;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.UI.Input.Tools;

public enum EventRowType
{
    Tone,
    Section,
    Phrase,
    Lyric,
    Link,
    Other
}

/// <summary>
/// Dispatcher for the event-ribbon pointer surface. Owns three mutually
/// exclusive gestures (beat-drag, lyric-drag, event-point-drag) and the
/// double-click bookkeeping for opening dialogs. All actual mutation is
/// delegated to the gesture classes; this file is intentionally a router.
/// </summary>
public class EventInteractionTool : IEditorTool
{
    private const long DoubleClickTicks = 5_000_000; // 500 ms

    private readonly IConfigurationService _configService;
    private readonly IEventEditingService _eventEditingService;
    private readonly IHistoryService _historyManager;
    private readonly IAppLogger _logger;
    private readonly PinnedLabelHitRegistry _pinnedRegistry;
    private readonly ILocalizationService _localization;

    private readonly BeatDragGesture _beatDrag;
    private readonly LyricDragGesture _lyricDrag;
    private readonly EventPointDragGesture _eventPointDrag;

    private BeatMarker? _lastClickedBeat;
    private long _lastBeatClickTicks;
    private EventPoint? _lastClickedEventPoint;
    private ToneChange? _lastClickedTone;
    private long _lastClickTicks;

    public EventInteractionTool(IEventEditingService eventEditingService,
        IHistoryService historyManager, IConfigurationService configService, IAppLogger logger,
        PinnedLabelHitRegistry pinnedRegistry, ILocalizationService localization)
    {
        _eventEditingService = eventEditingService;
        _localization = localization;
        _historyManager = historyManager;
        _configService = configService;
        _logger = logger;
        _pinnedRegistry = pinnedRegistry;

        _beatDrag = new BeatDragGesture(historyManager, configService, logger);
        _lyricDrag = new LyricDragGesture(historyManager, configService, logger);
        _eventPointDrag = new EventPointDragGesture(eventEditingService, logger);
    }

    private IEventGesture? ActiveGesture
    {
        get
        {
            if (_beatDrag.IsActive) return _beatDrag;
            if (_lyricDrag.IsActive) return _lyricDrag;
            if (_eventPointDrag.IsActive) return _eventPointDrag;
            return null;
        }
    }

    public void OnPointerPressed(PointerPressedEventArgs e, ToolContext context)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var selection = track.Selection;
        track.FocusArea = EditorFocusArea.TimelineEvents;
        vm.ChartEditor.FocusArea = EditorFocusArea.TimelineEvents;

        selection.ClearPreviews();
        selection.ClearHovers();

        if (context.Properties.IsLeftButtonPressed &&
            _pinnedRegistry.TryHit(context.Viewport, context.Position, out var pinned))
        {
            _logger.Info(AppLogCategory.Events,
                $"Clicked pinned {pinned.Kind} label; opening event dialog at timeMs={pinned.TimeMs:0.###}.");
            vm.ShellDialogs.OpenEvent(pinned.TimeMs, track.Arrangement);
            context.Viewport.RequestNextFrameRendering();
            return;
        }

        var rowType = GetRowType(context.Position.Y, context);
        _logger.Info(AppLogCategory.Input,
            $"Pointer pressed in event area. Row={rowType}, snapped={FormatPosition(context.SnappedPosition, track)}, exactMs={context.ExactTimeMs:0.###}, left={context.Properties.IsLeftButtonPressed}, right={context.Properties.IsRightButtonPressed}, modifiers={context.Modifiers}.");

        if (rowType == EventRowType.Lyric)
        {
            HandleLyricPress(context);
            context.Viewport.RequestNextFrameRendering();
            return;
        }

        if (rowType == EventRowType.Link)
        {
            if (LinkAt(context) is { } span)
            {
                _logger.Info(AppLogCategory.Events, $"Clicked link chip {span.Letter}; opening the link panel.");
                vm.ChartEditor.LinkHighlight.RequestPanel(new LinkPanelRequest(span.Group.Id, context.Viewport,
                    context.Position, context.Track.Arrangement, span.Placement.Position));
            }

            return;
        }

        if (rowType == EventRowType.Other)
        {
            HandleBeatRowPress(context);
            return;
        }

        HandleEventRowPress(context, rowType);
    }

    private void HandleBeatRowPress(ToolContext context)
    {
        var vm = context.ViewModel;
        var track = context.Track;

        var hitBeat = track.Beats.FirstOrDefault(b =>
            Math.Abs(b.Time - context.ExactTimeMs) < 15 / vm.ProjectState.PixelsPerMs);
        if (hitBeat == null) return;

        var isLocked = vm.ProjectState.ActiveProject?.IsBeatMapLocked == true;
        if (context.Properties.IsLeftButtonPressed)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _lastBeatClickTicks < DoubleClickTicks &&
                ReferenceEquals(_lastClickedBeat, hitBeat))
            {
                var beatIndex = track.Beats.IndexOf(hitBeat);
                _logger.Info(AppLogCategory.Events,
                    $"Double-clicked beat; opening dialog. BeatIndex={beatIndex}, timeMs={hitBeat.Time:0.###}, bpm={hitBeat.Bpm:0.###}.");
                vm.ShellDialogs.OpenBeat(hitBeat, beatIndex);
                _lastBeatClickTicks = 0;
                _lastClickedBeat = null;
                context.Viewport.RequestNextFrameRendering();
                return;
            }

            _lastBeatClickTicks = nowTicks;
            _lastClickedBeat = hitBeat;

            if (isLocked)
            {
                _logger.Info(AppLogCategory.Events, "Beat left-click drag blocked; tempo/beat map is locked.");
                vm.ProjectState.SetMessage(_localization["Status_BeatMapLocked"]);
                return;
            }

            _beatDrag.Start(track, hitBeat, track.Beats.IndexOf(hitBeat), context.ExactTimeMs, context.Position);
            context.Viewport.RequestNextFrameRendering();
        }
        else if (context.Properties.IsRightButtonPressed)
        {
            var beatIndex = track.Beats.IndexOf(hitBeat);
            if (hitBeat.IsAnchor && !isLocked)
            {
                _logger.Info(AppLogCategory.Events,
                    $"Right-click delete anchor. BeatIndex={beatIndex}, timeMs={hitBeat.Time:0.###}.");
                DeleteAnchor(context, beatIndex);
                context.Viewport.RequestNextFrameRendering();
                return;
            }

            _logger.Info(AppLogCategory.Events,
                $"Opening beat dialog from right-click. BeatIndex={beatIndex}, timeMs={hitBeat.Time:0.###}, bpm={hitBeat.Bpm:0.###}.");
            vm.ShellDialogs.OpenBeat(hitBeat, beatIndex);
            context.Viewport.RequestNextFrameRendering();
        }
    }

    private void HandleEventRowPress(ToolContext context, EventRowType rowType)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var selection = track.Selection;

        var hitEp = EventInteractionHitTester.HitTestEvent(
            track.EventPoints, track.Beats, rowType, context.ExactTimeMs, vm.ProjectState.PixelsPerMs);
        var hitTone = EventInteractionHitTester.HitTestTone(
            track.Tones, track.Beats, rowType, context.ExactTimeMs, vm.ProjectState.PixelsPerMs);

        if (hitEp != null)
        {
            HandleEventPointPress(context, hitEp);
            return;
        }

        if (hitTone != null)
        {
            HandleTonePress(context, hitTone);
            return;
        }

        if (context.Properties.IsLeftButtonPressed)
        {
            _logger.Info(AppLogCategory.Events,
                $"Left-click empty event row {rowType}; selection unchanged, no event created.");
            context.Viewport.RequestNextFrameRendering();
            return;
        }

        if (context.Properties.IsRightButtonPressed)
        {
            selection.SelectedEventPoints.Clear();
            selection.SelectedTones.Clear();
            _logger.Info(AppLogCategory.Events,
                $"Right-click empty event row {rowType}; opening event dialog at exactMs={context.ExactTimeMs:0.###}, snapped={FormatPosition(context.SnappedPosition, track)}.");
            vm.ShellDialogs.OpenEvent(context.ExactTimeMs, track.Arrangement);
            context.Viewport.RequestNextFrameRendering();
        }
    }

    private void HandleEventPointPress(ToolContext context, EventPoint hitEp)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var selection = track.Selection;

        if (context.Properties.IsLeftButtonPressed)
        {
            var now = DateTime.UtcNow.Ticks;
            if (now - _lastClickTicks < DoubleClickTicks && _lastClickedEventPoint == hitEp)
            {
                vm.ShellDialogs.OpenEvent(hitEp.Position.GetPositionMs(track.Beats), track.Arrangement);
                _logger.Info(AppLogCategory.Events,
                    $"Double-clicked event point; opening dialog at {FormatPosition(hitEp.Position, track)}. Section='{hitEp.Section}', phrase='{hitEp.Phrase}'.");
                _eventPointDrag.Cancel(context);
                context.Viewport.RequestNextFrameRendering();
                return;
            }

            _lastClickTicks = now;
            _lastClickedEventPoint = hitEp;
            _lastClickedTone = null;

            if (!context.Modifiers.HasFlag(KeyModifiers.Control) &&
                !context.Modifiers.HasFlag(KeyModifiers.Shift))
            {
                if (!selection.SelectedEventPoints.Contains(hitEp))
                {
                    selection.SelectedEventPoints.Clear();
                    selection.SelectedTones.Clear();
                }
            }

            if (!selection.SelectedEventPoints.Contains(hitEp))
                selection.SelectedEventPoints.Add(hitEp);
            _logger.Info(AppLogCategory.Events,
                $"Selected event point at {FormatPosition(hitEp.Position, track)}. Section='{hitEp.Section}', phrase='{hitEp.Phrase}', selectedCount={selection.SelectedEventPoints.Count}.");

            _eventPointDrag.Start(track, hitEp, context.ExactTimeMs, context.Position);
        }
        else if (context.Properties.IsRightButtonPressed)
        {
            // Right-click on an EMPTY row opens the dialog, so right-click on a
            // marker opening it too is the only consistent reading. Deleting on a
            // plain right-click would lose markers silently, with nothing on screen
            // saying what happened. Deleting is still one
            // gesture, it just has to be deliberate: Shift+right-click, or the
            // Delete key on the selected marker.
            if (context.Modifiers.HasFlag(KeyModifiers.Shift))
            {
                _logger.Info(AppLogCategory.Events,
                    $"Shift+right-click delete event point at {FormatPosition(hitEp.Position, track)}. " +
                    $"Section='{hitEp.Section}', phrase='{hitEp.Phrase}'.");
                _eventEditingService.RemoveEventPoints(new[] { hitEp });
            }
            else
            {
                if (!selection.SelectedEventPoints.Contains(hitEp))
                {
                    selection.SelectedEventPoints.Clear();
                    selection.SelectedTones.Clear();
                    selection.SelectedEventPoints.Add(hitEp);
                }

                vm.ShellDialogs.OpenEvent(hitEp.Position.GetPositionMs(track.Beats), track.Arrangement);
                _logger.Info(AppLogCategory.Events,
                    $"Right-click event point; opening dialog at {FormatPosition(hitEp.Position, track)}. " +
                    $"Section='{hitEp.Section}', phrase='{hitEp.Phrase}'.");
            }
        }

        context.Viewport.RequestNextFrameRendering();
    }

    private void HandleTonePress(ToolContext context, ToneChange hitTone)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var selection = track.Selection;

        if (context.Properties.IsLeftButtonPressed)
        {
            var now = DateTime.UtcNow.Ticks;
            if (now - _lastClickTicks < DoubleClickTicks && _lastClickedTone == hitTone)
            {
                vm.ShellDialogs.OpenEvent(hitTone.Position.GetPositionMs(track.Beats), track.Arrangement);
                _logger.Info(AppLogCategory.Events,
                    $"Double-clicked tone change; opening dialog at {FormatPosition(hitTone.Position, track)}. Tone='{hitTone.ToneName}'.");
                context.Viewport.RequestNextFrameRendering();
                return;
            }

            _lastClickTicks = now;
            _lastClickedTone = hitTone;
            _lastClickedEventPoint = null;

            if (!context.Modifiers.HasFlag(KeyModifiers.Control) &&
                !context.Modifiers.HasFlag(KeyModifiers.Shift))
            {
                if (!selection.SelectedTones.Contains(hitTone))
                {
                    selection.SelectedEventPoints.Clear();
                    selection.SelectedTones.Clear();
                }
            }

            if (!selection.SelectedTones.Contains(hitTone))
                selection.SelectedTones.Add(hitTone);

            _logger.Info(AppLogCategory.Events,
                $"Selected tone change at {FormatPosition(hitTone.Position, track)}. Tone='{hitTone.ToneName}', selectedCount={selection.SelectedTones.Count}.");
        }
        else if (context.Properties.IsRightButtonPressed)
        {
            _logger.Info(AppLogCategory.Events,
                $"Right-click delete tone change at {FormatPosition(hitTone.Position, track)}. Tone='{hitTone.ToneName}'.");
            // Out of the selection first — the left-click above put it there.
            selection.SelectedTones.Remove(hitTone);
            _historyManager.Do(new RemoveToneChangesAction(track.Tones, new[] { hitTone }));
            // Prune, exactly as the lyric branch does and as
            // EventEditingService.RemoveEventPoints does for markers: a stale
            // selection entry makes the next Delete push a node that removes
            // nothing and whose undo re-inserts the tone change.
            selection.SelectedTones.Remove(hitTone);
            if (ReferenceEquals(selection.HoveredTone, hitTone)) selection.HoveredTone = null;
            vm.ChartEditor.RequestRedraw();
        }

        context.Viewport.RequestNextFrameRendering();
    }

    private void HandleLyricPress(ToolContext context)
    {
        var vm = context.ViewModel;
        var selection = vm.ChartEditor.Selection;
        var (lyric, phrase) = EventInteractionHitTester.HitTestLyric(
            vm.ChartEditor.ActiveVocalTrack?.Phrases, vm.ChartEditor.Beats,
            context.ExactTimeMs, vm.ProjectState.PixelsPerMs);

        if (context.Properties.IsRightButtonPressed)
        {
            if (lyric != null && phrase != null)
            {
                _logger.Info(AppLogCategory.Events,
                    $"Right-click delete lyric '{lyric.Text}' at {lyric.Position.GetPositionMs(context.Track.Beats):0.###}ms.");
                var track = vm.ChartEditor.ActiveVocalTrack;
                if (track != null)
                    _historyManager.Do(new RemoveLyricAction(track.Phrases, phrase, lyric));
                if (ReferenceEquals(selection.SelectedLyric, lyric)) selection.SelectedLyric = null;
            }
            return;
        }

        if (!context.Properties.IsLeftButtonPressed) return;

        if (lyric == null)
        {
            selection.SelectedLyric = null;
            _logger.Info(AppLogCategory.Events, "Left-click empty lyric row; selection cleared.");
            return;
        }

        SelectLyricAlone(selection, context.Track.Selection, lyric);
        _logger.Info(AppLogCategory.Events,
            $"Selected lyric '{lyric.Text}' at {lyric.Position.GetPositionMs(context.Track.Beats):0.###}ms.");

        _lyricDrag.Start(context.Track, lyric, context.ExactTimeMs, context.Position);
    }

    /// <summary>
    ///     A lyric click REPLACES the selection, as a click on the note lane does. Setting
    ///     only <see cref="ISelectionService.SelectedLyric" /> would leave a note picked a moment earlier
    ///     still selected — and <c>TimelineWheelAdjuster</c> tries selected sounds before the
    ///     lyric, so wheeling to lengthen the lyric would lengthen the NOTE.
    ///     Clears the clicked track's selection too when it is a stacked row with its own.
    /// </summary>
    public static void SelectLyricAlone(ISelectionService editorSelection, ISelectionService trackSelection,
        LyricEvent lyric)
    {
        editorSelection.ClearSelections();
        if (!ReferenceEquals(trackSelection, editorSelection)) trackSelection.ClearSelections();
        editorSelection.SelectedLyric = lyric;
    }

    public void OnPointerMoved(PointerEventArgs e, ToolContext context)
    {
        var track = context.Track;
        var active = ActiveGesture;
        var dragTrack = (active as BeatDragGesture)?.Track
                        ?? (active as LyricDragGesture)?.Track
                        ?? (active as EventPointDragGesture)?.Track
                        ?? track;
        dragTrack.Selection.ClearPreviews();
        if (!ReferenceEquals(dragTrack, track)) track.Selection.ClearPreviews();

        if (context.Properties.IsLeftButtonPressed && active != null)
        {
            active.OnMoved(context);
            context.Viewport.RequestNextFrameRendering();
            return;
        }

        var rowType = GetRowType(context.Position.Y, context);
        var selection = track.Selection;
        context.ViewModel.ChartEditor.LinkHighlight.SetHovered(
            rowType == EventRowType.Link ? LinkAt(context)?.Group.Id : null, context.Viewport, context.Position);

        selection.HoveredEventPoint = EventInteractionHitTester.HitTestEvent(
            track.EventPoints, track.Beats, rowType, context.ExactTimeMs, context.ViewModel.ProjectState.PixelsPerMs);
        selection.HoveredTone = EventInteractionHitTester.HitTestTone(
            track.Tones, track.Beats, rowType, context.ExactTimeMs, context.ViewModel.ProjectState.PixelsPerMs);
        if (rowType == EventRowType.Lyric)
        {
            var (lyric, _) = EventInteractionHitTester.HitTestLyric(
                context.ViewModel.ChartEditor.ActiveVocalTrack?.Phrases, context.ViewModel.ChartEditor.Beats,
                context.ExactTimeMs, context.ViewModel.ProjectState.PixelsPerMs);
            selection.HoveredLyric = lyric;
        }
        else
        {
            selection.HoveredLyric = null;
        }

        if (selection.HoveredEventPoint == null && selection.HoveredTone == null &&
            rowType is not (EventRowType.Other or EventRowType.Lyric or EventRowType.Link))
        {
            selection.PreviewEventPoints.Add(new EventPoint
            {
                Position = context.SnappedPosition,
                Section = rowType == EventRowType.Tone ? "+ Add Tone" : "+ Add Event"
            });
        }

        context.Viewport.RequestNextFrameRendering();
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context)
    {
        var active = ActiveGesture;
        if (active != null && active.HasDragged)
            active.Commit(context);
        else
            active?.Cancel(context);

        ResetVisuals(context);
    }

    public void OnPointerExited(PointerEventArgs e, ToolContext context)
    {
        if (ActiveGesture == null) ResetVisuals(context);
    }

    private void ResetVisuals(ToolContext context)
    {
        var track = context.Track;
        track.Selection.PreviewEventPoints.Clear();
        track.Selection.ClearHovers();
        context.ViewModel.ChartEditor.LinkHighlight.SetHovered(null);
        context.Viewport.RequestNextFrameRendering();
    }

    private void DeleteAnchor(ToolContext context, int beatIndex)
    {
        var vm = context.ViewModel;
        var track = context.Track;
        var beats = track.Beats;

        var result = AnchorRemovalMath.Compute(beats, beatIndex);
        if (result == null) return;

        var (oldBeats, newBeats) = result.Value;
        var action = new MutateBeatsAction(beats, oldBeats, newBeats,
            () =>
            {
                ChartSpacingEnforcer.EnforceProject(vm.ProjectState.ActiveProject,
                    _configService.LoadAppConfig());
                vm.ChartEditor.RequestRedraw();
            });
        _historyManager.Do(action);
    }

    private static EventRowType GetRowType(double y, ToolContext context)
    {
        var ribbon = context.Viewport.TopRibbon;
        return ribbon.HitTestRow(y) switch
        {
            EventRow.Tone => EventRowType.Tone,
            EventRow.Section => EventRowType.Section,
            EventRow.Phrase => EventRowType.Phrase,
            EventRow.Lyric => EventRowType.Lyric,
            EventRow.Link => EventRowType.Link,
            _ => EventRowType.Other
        };
    }

    /// <summary>The LINKS-row chip span under the pointer, in this track's arrangement.</summary>
    private static LinkSpan? LinkAt(ToolContext context) =>
        LinkLaneModel.HitTest(
            LinkLaneModel.Spans(context.ViewModel.ProjectState.ActiveProject, context.Track.Arrangement), context.ExactTimeMs);

    private static string FormatPosition(FractionalPosition position, TrackEditorContext track)
        => position.FormatWithMs(track.Beats);
}
