using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     The Amp &amp; FX rig screen (§6.8) — Play Mode's rig overlay and the chart
///     editor's are the SAME control over the SAME
///     <see cref="AmpFxRigViewModel" />, which is over the one
///     <see cref="PedalboardViewModel" /> singleton.
///
///     <para>
///         <b>The screen owns its own clock.</b> While this markup lived inside
///         <c>PlayModeView</c>, the rig's meter was one branch of that screen's
///         timer — which is exactly the coupling that made a second host
///         impossible. The clock lives here now and is gated on attach +
///         <see cref="Visual.IsEffectivelyVisible" />, so it runs when the rig is
///         on screen in EITHER host and at no other time; a closed overlay is
///         `IsVisible=false`, which is not effectively visible, so nothing polls.
///     </para>
/// </summary>
public partial class AmpFxRigView : UserControl
{
    /// <summary>
    ///     50 ms — the same cadence Play Mode's settings modal and Get Ready use
    ///     for the same capture tap. Faster would poll the engine for numbers a
    ///     20 fps meter cannot draw; slower makes the CLIP lamp miss transients.
    /// </summary>
    private readonly DispatcherTimer _meterTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };

    /// <summary>Drag-to-reorder over the board's signal chain. Owned for the
    /// view's lifetime — the board is one <c>ItemsControl</c> that is never
    /// re-created, and the controller resolves the rig per drop rather than
    /// holding one.</summary>
    private readonly RigBoardDragController _boardDrag;

    /// <summary>The rig whose <c>Zoom</c> this view mirrors into
    /// <c>RigBoardZoomHost</c>. The VM is a singleton shared with the other
    /// host, so the subscription is dropped on detach and re-taken on attach —
    /// never left dangling on a closed overlay.</summary>
    private PedalboardViewModel? _zoomRig;
    private RigWorkspaceViewModel? _workspace;
    private AmpFxRigViewModel? _screenModel;

    /// <summary>The tuner this view currently holds a liveness token with. The VM
    /// is the ONE singleton rig's, shared with the other host, so a DataContext
    /// swap has to hand the token back before taking a new one — a stale token on
    /// a retired VM keeps that VM's poll running forever.</summary>
    private RigTunerViewModel? _liveTuner;

    /// <summary>The last effective visibility the watcher reported. Kept because
    /// the token is (re)written on DataContext changes too, which the watcher
    /// knows nothing about.</summary>
    private bool _surfaceLive;
    private RigNodeMetersViewModel? _liveModuleMeters;
    private RigNodeMetersViewModel? _liveOutputMeters;
    private RigBoardMetersViewModel? _liveBoardMeters;

    public AmpFxRigView()
    {
        // The meter clock follows EFFECTIVE visibility (an overlay host toggles
        // IsVisible on an ancestor) — via the watcher, because an
        // OnPropertyChanged branch keyed on a name Avalonia 12 never raises would
        // leave the rig's input meter never started.
        //
        // The same flip is the ONLY thing that reports this host's liveness to
        // the rig tuner. Without it RigTunerViewModel.SetSurfaceLive has no product caller,
        // so its live-surface set would be permanently empty, its 66 ms poll could never start,
        // and the footer's FireTunerMeter needle would never move — while the gating contract test over that very
        // method stayed green, because it called the method itself.
        _ = new EffectiveVisibilityWatcher(this, visible =>
        {
            _surfaceLive = visible;
            SyncMeterClock();
            SyncTunerSurface();
            SyncModuleMeters();
        });

        InitializeComponent();

        // The whole view is the move/release surface and the gear list is a
        // second drag SOURCE: a gear row can be dragged straight onto a run —
        // including a lane, whose branch it then lands on.
        _boardDrag = new RigBoardDragController(
            RigBoardItems, this, RigGearGroups, () => DataContext as AmpFxRigViewModel);

        // Wheel over the board: Ctrl+wheel zooms (tunnel — it must win over the
        // card bodies' own vertical ScrollViewers), a plain leftover wheel
        // travels the chain horizontally (bubble — an inner viewer that really
        // scrolled has already handled it).
        RigBoard.AddHandler(PointerWheelChangedEvent, OnBoardWheelTunnel, RoutingStrategies.Tunnel);
        RigBoard.AddHandler(PointerWheelChangedEvent, OnBoardWheelBubble, RoutingStrategies.Bubble);

        // Drag-to-pan on EMPTY board space (design: "drag-to-pan on empty
        // canvas"): a press that no card, knob or button took scrolls the board.
        RigBoard.AddHandler(PointerPressedEvent, OnBoardPanPressed, RoutingStrategies.Bubble);
        RigBoard.AddHandler(PointerMovedEvent, OnBoardPanMoved, RoutingStrategies.Bubble);
        RigBoard.AddHandler(PointerReleasedEvent, OnBoardPanReleased, RoutingStrategies.Bubble);

        _meterTimer.Tick += (_, _) => (DataContext as AmpFxRigViewModel)?.PollMeters();

        // The save-tone modal opens from the header OR the drawer; either way
        // the user's next act is typing a name, so the caret goes there. Posted:
        // the subtree has not laid out yet on the flag's own change.
        RigBrowserSaveDialog.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.GetNewValue<bool>())
                Dispatcher.UIThread.Post(() => RigSaveNameBox.Focus(), DispatcherPriority.Background);
        };
        // The delete confirmation puts the keys on Cancel: a stray Enter or Space
        // must keep the tone, never delete it.
        RigBrowserDeleteDialog.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.GetNewValue<bool>())
                Dispatcher.UIThread.Post(() => RigDeleteDialogCancel.Focus(), DispatcherPriority.Background);
        };

        // The tone library's resize. `DragCompleted` (GridSplitter is a Thumb)
        // is the commit point on purpose: the splitter writes the ROW while the
        // pointer moves, and the rig — which is what persists — only needs the
        // height the user let go at.
        RigBrowserSplitter.DragCompleted += (_, _) => CommitWorkspaceHeight();
        RigBrowserSplitter.DoubleTapped += (_, _) => ResetWorkspaceHeight();
        // A shrinking host may not leave the canvas less than its MinHeight; the
        // workspace is what gives way, exactly as the shell's sheet strip does.
        // SyncWorkspaceRow, not just the clamp: the default height is a
        // breakpointed token, so a window that crosses the wide breakpoint has
        // to re-read it. Writing the same GridLength back is a no-op.
        RigFlatBody.SizeChanged += (_, _) => { SyncWorkspaceRow(); CapDockBody(); };
        // The chain is FITTED to the canvas rather than scrolled sideways: six
        // blocks must not produce a horizontal scrollbar. Both boxes the fit
        // reads can change without the other moving — the canvas with the
        // window, the path with the chain — so both call back.
        RigBoard.SizeChanged += (_, _) => { SyncBoardFaceHeight(); SyncBoardZoom(); };
        RigBoardPath.SizeChanged += (_, _) => SyncBoardZoom();
        // the map's scroller is shorter than the board by its caption row, and the
        // height half of the fit reads the scroller
        RigBoardScroll.SizeChanged += (_, _) => SyncBoardZoom();

        // The routing map's right-click menu, and the rig's keyboard shortcuts.
        RigBoard.AddHandler(ContextRequestedEvent, OnBoardContextRequested, RoutingStrategies.Bubble);
        Focusable = true;
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            // a click anywhere in the rig puts the keyboard here (unless a field or a
            // control inside already has it), so Ctrl+Z / Del reach the rig
            if (!IsKeyboardFocusWithin) Focus();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // the performance keys (1–8 = scene A–H, PageUp / PageDown = setlist), on a
        // key the focused control did not take and never while a field is typing
        AddHandler(KeyDownEvent, OnPerformanceKey, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) => SyncKeyBindings();
        DataContextChanged += (_, _) => { SyncMeterClock(); AttachZoom(); SyncTunerSurface(); SyncModuleMeters(); SyncWorkspaceRow(); SyncBoardFaceHeight(); };
        AttachedToVisualTree += (_, _) => { SyncMeterClock(); AttachZoom(); SyncWorkspaceRow(); };
        DetachedFromVisualTree += (_, _) =>
        {
            _meterTimer.Stop();
            _liveModuleMeters?.SetSurfaceLive(this, false);
            _liveOutputMeters?.SetSurfaceLive(this, false);
            _liveBoardMeters?.SetSurfaceLive(this, false);
            _boardDrag.Dispose();
            DetachZoom();
        };
    }

    /// <summary>
    ///     Reports THIS host's surface liveness to the rig tuner, which gates its
    ///     poll on it (open panel AND at least one live host — two hosts share the
    ///     one singleton VM, hence a set keyed by the view instance rather than a
    ///     flag). Detach reports not-live: the watcher treats a detached control
    ///     as invisible, so nothing reads the engine's input capture ring behind a
    ///     closed overlay.
    ///     <para>
    ///         Safe before the DataContext arrives (there is simply no tuner to
    ///         tell yet) and safe across a VM swap: the old tuner's token is handed
    ///         back first, so a retired rig cannot be left polling on this view's
    ///         behalf.
    ///     </para>
    /// </summary>
    private void SyncModuleMeters()
    {
        var meters = (DataContext as AmpFxRigViewModel)?.Rig.ModuleMeters;
        if (!ReferenceEquals(_liveModuleMeters, meters))
        {
            _liveModuleMeters?.SetSurfaceLive(this, false);
            _liveModuleMeters = meters;
        }
        _liveModuleMeters?.SetSurfaceLive(this, _surfaceLive);

        var output = (DataContext as AmpFxRigViewModel)?.Rig.OutputMeters;
        if (!ReferenceEquals(_liveOutputMeters, output))
        {
            _liveOutputMeters?.SetSurfaceLive(this, false);
            _liveOutputMeters = output;
        }
        _liveOutputMeters?.SetSurfaceLive(this, _surfaceLive);

        var board = (DataContext as AmpFxRigViewModel)?.Rig.BoardMeters;
        if (!ReferenceEquals(_liveBoardMeters, board))
        {
            _liveBoardMeters?.SetSurfaceLive(this, false);
            _liveBoardMeters = board;
        }
        _liveBoardMeters?.SetSurfaceLive(this, _surfaceLive);
    }

    private void SyncTunerSurface()
    {
        var tuner = (DataContext as AmpFxRigViewModel)?.Rig.RigTuner;
        if (!ReferenceEquals(_liveTuner, tuner))
        {
            _liveTuner?.SetSurfaceLive(this, false);
            _liveTuner = tuner;
        }

        if (_liveTuner is null) return;

        // The poll needs the panel OPEN as well as a live host, and the only
        // other opener is selecting a Pitch-family pedal — so with liveness
        // alone the footer needle still stood still on every rig with no such
        // pedal. This screen draws the tuner PERMANENTLY in its footer: a live
        // rig surface IS an open tuner, and there is no panel toggle any more.
        if (_surfaceLive) _liveTuner.Open();
        _liveTuner.SetSurfaceLive(this, _surfaceLive);
    }

    private void AttachZoom()
    {
        var model = DataContext as AmpFxRigViewModel;
        var rig = model?.Rig;
        if (!ReferenceEquals(_screenModel, model))
        {
            DetachZoom();
            _screenModel = model;
            _screenModel?.PropertyChanged += OnScreenLayoutChanged;
            _zoomRig = rig;
            _zoomRig?.PropertyChanged += OnRigZoomChanged;
            _workspace = rig?.Workspace;
            _workspace?.PropertyChanged += OnWorkspaceHeightChanged;
        }

        SyncBoardZoom();
    }

    private void DetachZoom()
    {
        _zoomRig?.PropertyChanged -= OnRigZoomChanged;
        _zoomRig = null;
        _workspace?.PropertyChanged -= OnWorkspaceHeightChanged;

        _workspace = null;
        _screenModel?.PropertyChanged -= OnScreenLayoutChanged;
        _screenModel = null;
    }

    private void OnScreenLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AmpFxRigViewModel.IsDockBodyVisible) or null) SyncWorkspaceRow();
    }

    private void OnRigZoomChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PedalboardViewModel.Zoom) or null) SyncBoardZoom();
        if (e.PropertyName is nameof(PedalboardViewModel.RoutingBranches) or null) SyncBoardFaceHeight();
        if (e.PropertyName is nameof(PedalboardViewModel.IsBrowserOpen) or null) SyncWorkspaceRow();
    }

    private void OnWorkspaceHeightChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RigWorkspaceViewModel.Height) or null)
        {
            SyncWorkspaceRow();
        }
    }

    // ---- The workspace: the stage and its gear bar, above the routing map -----

    /// <summary>Row 0 of <c>RigFlatBody</c>: <c>RigWorkspace</c>, the STAGE (the
    /// selected gear, big) with its gear bar; row 1 is the divider and row 2 the
    /// routing map. Indexed rather than <c>x:Name</c>d because a
    /// <c>RowDefinition</c> is not a <see cref="Control" /> — <c>FindControl</c>
    /// would never see it.</summary>
    internal const int WorkspaceRowIndex = 0;

    /// <summary>The routing map's floor, mirroring the <c>MinHeight</c> on
    /// <c>RigFlatBody</c>'s last row. Duplicated as a constant only so
    /// <see cref="ClampWorkspaceRow" /> cannot drift from it silently.</summary>
    internal const double BoardRowMinHeight = 170;

    /// <summary>The stage's share of the body before the divider has ever been
    /// dragged — AmpliTube's proportions: the gear takes the larger part, the map
    /// the foot. A SHARE, not a fixed height, because the stage is the thing that
    /// should grow with the window; it is still content-independent, so the map
    /// never moves when a unit is selected.</summary>
    internal const double StageShare = 0.62;

    /// <summary>How far the height half of the map's fit may shrink the chain
    /// (<see cref="BoardFitScale" />): below <see cref="PedalboardViewModel.MinZoom" />,
    /// which floors the WIDTH half, because three lanes on a 1280×800 map need it.</summary>
    internal const double MapHeightFitFloor = 0.35;

    /// <summary>The divider's own height in the markup.</summary>
    private const double DividerHeight = 6;

    /// <summary>
    ///     Writes the workspace's row height, and it is an ABSOLUTE pixel value
    ///     whatever is inside it. This is the whole of W1.
    ///     <para>
    ///         The row must not be <c>Auto</c> for the module dock and a pixel
    ///         height for the drawer, or the star-sized routing canvas above
    ///         would gain or lose 244+ px every time a block was selected or
    ///         deselected — the canvas you read the signal path off would resize
    ///         whenever the editor opened. One content-independent height is
    ///         what makes row 1 invariant: the user's dragged divider (restored
    ///         from <c>AppConfig.RigWorkspaceHeight</c> through
    ///         <see cref="RigWorkspaceViewModel.Height" />), or, until it
    ///         has been dragged, <see cref="DefaultWorkspaceRowHeight" />.
    ///     </para>
    ///     <para>
    ///         The row's own Min/MaxHeight is what bounds the DRAG: a
    ///         <c>GridSplitter</c> honours them, so the clamp does not have to be
    ///         re-implemented in the drag handler.
    ///     </para>
    /// </summary>
    private void SyncWorkspaceRow()
    {
        if (RigFlatBody.RowDefinitions.Count <= WorkspaceRowIndex) return;
        var row = RigFlatBody.RowDefinitions[WorkspaceRowIndex];

        var workspace = (DataContext as AmpFxRigViewModel)?.Rig.Workspace;
        var px = workspace is { HasUserHeight: true }
            ? workspace.Height
            : DefaultWorkspaceRowHeight();

        row.MinHeight = RigWorkspaceViewModel.MinHeight;
        row.MaxHeight = RigWorkspaceViewModel.MaxHeight;
        row.Height = new GridLength(Math.Clamp(px,
            RigWorkspaceViewModel.MinHeight, RigWorkspaceViewModel.MaxHeight));
        ClampWorkspaceRow();
    }

    /// <summary>
    ///     The workspace's height before anyone has dragged the divider:
    ///     the breakpointed <c>FireRigWorkspaceHeight</c> token (480, 760 above
    ///     the wide breakpoint), exactly as the gear rail takes its width from
    ///     <c>FireRigGearPanelWidth</c>.
    ///     <para>
    ///         A LADDER and not a star row, and the difference is the whole
    ///         point: a `2.2*` row is a share of its parent and a content-sized
    ///         row moves the canvas, while a step at a breakpoint is neither. A
    ///         flat 480 px would have been the third wrong answer — measured at
    ///         3840x2160 with one, `RigAmpFaceView` scaled 0.96 against a 2.0
    ///         ceiling, i.e. the module editor sat at its design size in the
    ///         corner of a row that never grew (the lane-E audit's own finding,
    ///         reintroduced).
    ///     </para>
    /// </summary>
    private double DefaultWorkspaceRowHeight()
    {
        // The stage's share of the body, once the body has a height.
        var body = RigFlatBody.Bounds.Height;
        if (body > 0) return Math.Round((body - DividerHeight) * StageShare);
        return this.TryFindResource("FireRigWorkspaceHeight", out var value) && value is double px && px > 0
            ? px
            : RigWorkspaceViewModel.DefaultHeight;
    }

    /// <summary>Double-click on the divider: back to the content-independent
    /// default (design: "double-click restores automatic sizing"). There is no
    /// automatic size to restore any more — the workspace never sizes to its
    /// content, because that is what moved the canvas.</summary>
    private void ResetWorkspaceHeight()
    {
        if (DataContext is not AmpFxRigViewModel vm) return;
        vm.Rig.Workspace.Height = RigFlatBody.Bounds.Height > 0
            ? DefaultWorkspaceRowHeight()
            : RigWorkspaceViewModel.DefaultHeight;
    }

    /// <summary>
    ///     The editor reserve (design section 3.2): the dock body may take the
    ///     screen down to the canvas's minimum and no further, so the editor
    ///     family views know their real ceiling and scale to it
    ///     (<see cref="Controls.Fire.RigScaleToFit" />) instead of growing the
    ///     dock until the canvas is squeezed to nothing. Since W1 the workspace
    ///     row is a fixed height and the body cannot push past it anyway; this
    ///     stays as the SECOND half of the guarantee, for a host smaller than the
    ///     persisted divider.
    /// </summary>
    private void CapDockBody()
    {
        var room = RigFlatBody.Bounds.Height - RigDockBar.Bounds.Height - DividerHeight - BoardRowMinHeight;
        RigDockBody.MaxHeight = room > MinDockBodyHeight ? room : MinDockBodyHeight;
    }

    private const double MinDockBodyHeight = 180;

    /// <summary>Shrinks (never grows) the workspace row so the canvas keeps at
    /// least <see cref="BoardRowMinHeight" /> — a persisted height from a taller
    /// window, or a host that shrank under the user, must not push the canvas off
    /// the bottom. Never writes back to the rig: the user's CHOSEN height
    /// survives a small window and comes back when there is room again.</summary>
    private void ClampWorkspaceRow()
    {
        if (RigFlatBody.RowDefinitions.Count <= WorkspaceRowIndex) return;
        var row = RigFlatBody.RowDefinitions[WorkspaceRowIndex];
        if (!row.Height.IsAbsolute) return;

        var available = RigFlatBody.Bounds.Height;
        if (available <= 0) return; // not laid out yet — the first arrange will call back

        var max = available - DividerHeight - BoardRowMinHeight;
        if (max < RigWorkspaceViewModel.MinHeight) return; // too small: let the MinHeights fight it out
        if (row.Height.Value > max) row.Height = new GridLength(max);
    }

    /// <summary>Hands the dragged row height back to the rig, which clamps and
    /// persists it. Reads the ROW rather than a child Border: the Border
    /// stretches, so during the frame the drag ends its bounds may still be the
    /// pre-drag ones.</summary>
    private void CommitWorkspaceHeight()
    {
        if (DataContext is not AmpFxRigViewModel vm) return;
        if (RigFlatBody.RowDefinitions.Count <= WorkspaceRowIndex) return;

        var row = RigFlatBody.RowDefinitions[WorkspaceRowIndex];
        var px = row.Height.IsAbsolute ? row.Height.Value : row.ActualHeight;
        if (px <= 1) return;
        vm.Rig.Workspace.Height = px;
    }

    /// <summary>Every unit on the board is its faceplate, as tall as the board
    /// allows (<see cref="RigBoardFaceSize" />): the board's height shared between
    /// the lanes of a split region. The width then follows each face's aspect, and
    /// <see cref="BoardFitScale" /> shrinks a chain too long for the canvas.</summary>
    private void SyncBoardFaceHeight()
    {
        if (DataContext is not AmpFxRigViewModel vm) return;
        var height = Math.Max(RigBoardFaceSize.Min,
            Math.Min(RigBoardFaceSize.For(RigBoard.Bounds.Height, vm.Rig.RoutingBranches), _faceWidthLimit));
        if (Math.Abs(vm.BoardFaceHeight - height) >= 1) vm.BoardFaceHeight = height;
    }

    /// <summary>The face height the board's WIDTH allows (<see cref="RigBoardFaceSize.NextWidthLimit" />),
    /// +∞ until a chain has been wider than the canvas.</summary>
    private double _faceWidthLimit = double.PositiveInfinity;

    /// <summary>True when the board's faces were laid out at <paramref name="height" />
    /// (or there are none to lay out).</summary>
    private bool PathLaidOutAt(double height)
    {
        var face = RigBoardPath.GetVisualDescendants().OfType<Controls.Fire.RigFaceplateView>().FirstOrDefault();
        return face is null || Math.Abs(face.Bounds.Height - height) < 0.5;
    }

    /// <summary>Pushed from code rather than bound in markup: a <c>{Binding}</c>
    /// inside a <c>Transform</c> has no DataContext to inherit (transforms are
    /// not styled elements), so it would silently resolve nothing.</summary>
    private void SyncBoardZoom()
    {
        // First let a chain that does not fit take shorter faces; the transform
        // below then only absorbs what is left (a chain at the faces' floor).
        // Only from a layout that already reflects the current face height: a
        // size change raised before the faces re-measured still carries the old
        // width, and shrinking again on it ratchets the faces to the floor.
        if (DataContext is AmpFxRigViewModel faces && RigBoardPath.Bounds.Width > 0
            && PathLaidOutAt(faces.BoardFaceHeight))
        {
            var pad = RigBoardScroll.Padding;
            var fit = (RigBoard.Bounds.Width - pad.Left - pad.Right) / RigBoardPath.Bounds.Width;
            var limit = RigBoardFaceSize.NextWidthLimit(faces.BoardFaceHeight, fit, _faceWidthLimit);
            if (limit != _faceWidthLimit)
            {
                _faceWidthLimit = limit;
                SyncBoardFaceHeight();
            }
        }

        var scale = (_zoomRig?.Zoom ?? 1.0) * BoardFitScale();
        if (RigBoardZoomHost.LayoutTransform is ScaleTransform current
            && Math.Abs(current.ScaleX - scale) < 0.001)
        {
            return; // no re-layout for nothing
        }

        RigBoardZoomHost.LayoutTransform = new ScaleTransform(scale, scale);
    }

    /// <summary>
    ///     How far the chain has to shrink to FIT the canvas, 1.0 when it
    ///     already does. Six blocks must not produce a horizontal scrollbar
    ///     (W1), and the mechanism is the same transform the Fit button drives —
    ///     applied continuously instead of on a press.
    ///     <para>
    ///         It reads <c>RigBoardPath.Bounds.Width</c>, which is the path's
    ///         LOGICAL width: a <c>LayoutTransformControl</c> keeps its child's
    ///         bounds in logical pixels, and the path is content-sized
    ///         horizontally, so the number this divides does not move when the
    ///         transform changes. Reading the ScrollViewer's EXTENT instead
    ///         would be a feedback loop — the extent is the transformed size.
    ///     </para>
    ///     <para>
    ///         Floored at <see cref="PedalboardViewModel.MinZoom" />: a chain
    ///         long enough to need more than that is better scrolled than
    ///         rendered at a size nobody can read, and that is what the
    ///         horizontal bar is still there for.
    ///     </para>
    /// </summary>
    private double BoardFitScale()
    {
        var natural = RigBoardPath.Bounds.Width;
        if (natural <= 0) return 1.0;

        var pad = RigBoardScroll.Padding;
        // two DIPs of slack: a chain fitted EXACTLY to the room rounds a pixel
        // over it and grows a scrollbar with nothing to scroll
        var room = RigBoard.Bounds.Width - pad.Left - pad.Right - 2;
        if (room <= 0) return 1.0;
        var fit = Math.Clamp(room / natural, PedalboardViewModel.MinZoom, 1.0);

        // The map is SHORT (a share of the body, not a stage), so a split region
        // whose lanes are already at the faces' floor fits by HEIGHT too, rather
        // than growing a vertical scrollbar under three lanes — down to its own,
        // lower floor: the map is schematic (the stage is where a unit is edited,
        // and the Split node's routing console carries every lane mixer big).
        var tall = RigBoardPath.Bounds.Height;
        var roomHeight = RigBoardScroll.Bounds.Height - pad.Top - pad.Bottom - 4;
        if (tall > 0 && roomHeight > 0) fit = Math.Min(fit, Math.Clamp(roomHeight / tall, MapHeightFitFloor, 1.0));

        return fit;
    }

    /// <summary>
    ///     The rig's shortcuts: Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z) undo and redo,
    ///     Ctrl+C / Ctrl+X / Ctrl+V copy, cut and paste a unit, Ctrl+D duplicates it,
    ///     Ctrl+B switches it on or off, Del removes it. Bound on this view, so a text
    ///     field inside it keeps its own Ctrl+Z / Del (it handles the key first).
    /// </summary>
    private void SyncKeyBindings()
    {
        KeyBindings.Clear();
        if (DataContext is not AmpFxRigViewModel vm) return;
        var rig = vm.Rig;
        void Bind(Avalonia.Input.KeyGesture gesture, System.Windows.Input.ICommand command) =>
            KeyBindings.Add(new Avalonia.Input.KeyBinding { Gesture = gesture, Command = command });
        Bind(RigUnitContextMenu.Undo, rig.UndoCommand);
        Bind(RigUnitContextMenu.Redo, rig.RedoCommand);
        Bind(RigUnitContextMenu.RedoAlt, rig.RedoCommand);
        Bind(RigUnitContextMenu.Copy, rig.CopyCommand);
        Bind(RigUnitContextMenu.Cut, rig.CutCommand);
        Bind(RigUnitContextMenu.Paste, rig.PasteCommand);
        Bind(RigUnitContextMenu.Duplicate, rig.DuplicateSelectedCommand);
        Bind(RigUnitContextMenu.Bypass, rig.ToggleSelectedBypassCommand);
        Bind(RigUnitContextMenu.Delete, rig.DeleteSelectedCommand);
    }

    private void OnPerformanceKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not AmpFxRigViewModel vm) return;
        var typing = e.Source is TextBox || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
        var (action, scene) = RigPerformanceKeys.Map(e.Key, e.KeyModifiers, typing);
        switch (action)
        {
            case RigPerformanceKeyAction.Scene:
                vm.Rig.SelectSceneAt(scene);
                break;
            // with no live setlist the page keys stay the scrollers'
            case RigPerformanceKeyAction.PreviousSong when vm.Rig.Setlists.HasActive:
                vm.Rig.Setlists.PreviousCommand.Execute(null);
                break;
            case RigPerformanceKeyAction.NextSong when vm.Rig.Setlists.HasActive:
                vm.Rig.Setlists.NextCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>A right-click on a unit in the routing map selects it and opens its menu.</summary>
    private void OnBoardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not AmpFxRigViewModel vm || e.Source is not Visual source) return;
        if (RigBoardDragController.CardNodeOf(source) is not { } node) return;
        vm.Rig.SelectNodeCommand.Execute(node);
        UnitMenu?.Close();
        UnitMenu = RigUnitContextMenu.Build(vm.Rig, node);
        UnitMenu.Open(source as Control ?? RigBoard);
        e.Handled = true;
    }

    /// <summary>The unit menu last opened by a right-click (one at a time).</summary>
    internal ContextMenu? UnitMenu { get; private set; }

    private bool _panning;
    private Point _panOrigin;
    private Vector _panStartOffset;

    /// <summary>True when a press at <paramref name="source" /> is on the board
    /// itself rather than on something that owns its own gesture (a card, a
    /// knob, a slider, a button, the region's mixers).</summary>
    internal static bool IsPanSpot(Visual? source, Visual board)
    {
        for (var v = source; v is not null && !ReferenceEquals(v, board); v = v.GetVisualParent())
        {
            if (v is Button or ToggleButton or Slider or TextBox or ComboBox or Controls.Fire.FireKnob or ScrollBar) return false;
            if (v is Border border && (border.Classes.Contains("FireUnitCard") || border.Classes.Contains("FireLaneRow"))) return false;
        }
        return true;
    }

    private void OnBoardPanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(RigBoard).Properties.IsLeftButtonPressed) return;
        if (!IsPanSpot(e.Source as Visual, RigBoard)) return;
        if (RigBoardScroll.Extent.Width <= RigBoardScroll.Viewport.Width + 1
            && RigBoardScroll.Extent.Height <= RigBoardScroll.Viewport.Height + 1)
        {
            return; // nothing to pan
        }

        _panning = true;
        _panOrigin = e.GetPosition(RigBoard);
        _panStartOffset = RigBoardScroll.Offset;
        e.Pointer.Capture(RigBoard);
        e.Handled = true;
    }

    private void OnBoardPanMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;
        var delta = e.GetPosition(RigBoard) - _panOrigin;
        RigBoardScroll.Offset = new Vector(
            Math.Max(0, _panStartOffset.X - delta.X),
            Math.Max(0, _panStartOffset.Y - delta.Y));
        e.Handled = true;
    }

    private void OnBoardPanReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnBoardWheelTunnel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || _zoomRig == null || e.Delta.Y == 0)
            return;

        if (e.Delta.Y > 0) _zoomRig.ZoomInCommand.Execute(null);
        else _zoomRig.ZoomOutCommand.Execute(null);
        e.Handled = true;
    }

    private void OnBoardWheelBubble(object? sender, PointerWheelEventArgs e)
    {
        // Leftovers only: a card body that could still scroll vertically has
        // handled the event before it bubbles here. Shift+wheel stays native.
        if (e.KeyModifiers != KeyModifiers.None || e.Delta.Y == 0) return;

        var max = RigBoardScroll.Extent.Width - RigBoardScroll.Viewport.Width;
        if (max <= 0) return;

        RigBoardScroll.Offset = RigBoardScroll.Offset.WithX(
            RigBoardWheelMath.NextOffsetX(RigBoardScroll.Offset.X, e.Delta.Y, max));
        e.Handled = true;
    }


    private void SyncMeterClock()
    {
        if (IsEffectivelyVisible && DataContext is AmpFxRigViewModel vm)
        {
            vm.EnsureEditorSelection();
            _meterTimer.Start();
        }
        else
        {
            _meterTimer.Stop();
        }
    }

    /// <summary>
    ///     The rig's "+ Add" button: the gear panel is where a unit comes from,
    ///     so the button puts the caret in its search field rather than opening a
    ///     second, smaller picker for the same catalogue.
    /// </summary>
    private void OnRigFit(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AmpFxRigViewModel vm) return;
        var padding = RigBoardScroll.Padding;
        var horizontal = padding.Left + padding.Right;
        var vertical = padding.Top + padding.Bottom;
        vm.Rig.Zoom = RigBoardWheelMath.FitZoom(vm.Rig.Zoom,
            RigBoardScroll.Viewport.Width - horizontal, RigBoardScroll.Viewport.Height - vertical,
            RigBoardScroll.Extent.Width - horizontal, RigBoardScroll.Extent.Height - vertical);
    }

    private void OnRigAddUnit(object? sender, RoutedEventArgs e) => RigGearSearch.Focus();
}
