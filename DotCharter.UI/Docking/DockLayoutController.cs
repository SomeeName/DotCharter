using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using Dock.Model.Mvvm.Controls;

namespace DotCharter.UI.Docking;

/// <summary>
/// Owns one <see cref="DockControl"/>'s layout for a set of registered tool
/// panes: builds the initial layout, adds/removes panes at runtime when their
/// visibility flags flip, and keeps the flags truthful when the user closes a
/// pane from its tab or float window. All operations are incremental where
/// possible so toggling one pane never disturbs another pane's float state.
/// </summary>
public sealed class DockLayoutController
{
    private readonly DockControl _host;
    private readonly IReadOnlyList<DockPaneDefinition> _panes;
    private DotCharterDockFactory? _factory;
    private IRootDock? _layout;
    private ToolDock? _toolDock;
    private bool _syncing;

    public DockLayoutController(DockControl host, IReadOnlyList<DockPaneDefinition> panes)
    {
        _host = host;
        _panes = panes;
    }

    public IRootDock? Layout => _layout;

    /// <summary>The live layout's factory (tests drive its menu verbs directly).</summary>
    internal DotCharterDockFactory? Factory => _factory;

    /// <summary>Builds a fresh layout containing every currently-visible pane.
    /// Any float windows from the previous layout are closed first — see
    /// <see cref="TearDownPreviousLayout" />.</summary>
    public void Rebuild()
    {
        TearDownPreviousLayout();
        _factory = new DotCharterDockFactory();

        _toolDock = new ToolDock
        {
            Id = _host.Name + "Tools",
            VisibleDockables = _factory.CreateList<IDockable>(),
            Alignment = Alignment.Left,
            // Never collapse away when the last tool floats out or closes —
            // an empty-but-alive dock is the drop target for re-docking.
            IsCollapsable = false
        };

        foreach (var pane in _panes)
        {
            if (pane.IsVisible())
                _toolDock.VisibleDockables!.Add(CreateDockable(pane));
        }

        _toolDock.ActiveDockable = _toolDock.VisibleDockables!.FirstOrDefault();

        var root = _factory.CreateRootDock();
        root.IsCollapsable = false;
        root.VisibleDockables = _factory.CreateList<IDockable>(_toolDock);
        root.DefaultDockable = _toolDock;
        root.ActiveDockable = _toolDock;

        // Named handlers, not lambdas — TearDownPreviousLayout has to be able
        // to detach them from the outgoing factory.
        _factory.DockableClosed += OnDockableClosed;
        _factory.WindowClosed += OnWindowClosed;
        // Floating in or out by drag or menu: the shell shows a "dock it back" card in the
        // spot a floated pane left (IsPaneFloating).
        _factory.WindowAdded += OnFloatStateMaybeChanged;
        _factory.WindowRemoved += OnFloatStateMaybeChanged;
        _factory.DockableDocked += OnFloatStateMaybeChanged;
        _factory.DockableMoved += OnFloatStateMaybeChanged;

        _factory.InitLayout(root);
        _host.Layout = root;
        _layout = root;
    }

    /// <summary>
    /// Drops the previous layout cleanly before a rebuild. Two things went
    /// wrong without this (View → Reset Layout with a pane floated out):
    ///
    /// 1. **The float window was orphaned.** `_host.Layout = root` swapped the
    ///    root, but the old root's `Windows` stayed on screen — a stranded
    ///    `HostWindow` still presenting a `DockableView` over the SAME view
    ///    model the freshly-rebuilt docked pane now presents. Two live views
    ///    over one VM, and since the orphan is not reachable from
    ///    `_layout`, `CaptureState` recorded the pane as "Docked" and the
    ///    window was silently lost on restart.
    /// 2. **The old factory kept its handlers.** Closing that orphan later
    ///    raised the OLD factory's `WindowClosed` → `OnLayoutMutated()`, which
    ///    reads the NEW `_layout` and could flip the pane's visibility flag off
    ///    even though the docked copy was still there.
    ///
    /// Handlers come off FIRST so closing the windows raises nothing: a
    /// rebuild sets every flag from scratch anyway, and the mutation handler
    /// posts to the dispatcher, so a late callback would land after the new
    /// layout is live and describe a world that has gone.
    /// </summary>
    private void TearDownPreviousLayout()
    {
        if (_factory is { } oldFactory)
        {
            oldFactory.DockableClosed -= OnDockableClosed;
            oldFactory.WindowClosed -= OnWindowClosed;
            oldFactory.WindowAdded -= OnFloatStateMaybeChanged;
            oldFactory.WindowRemoved -= OnFloatStateMaybeChanged;
            oldFactory.DockableDocked -= OnFloatStateMaybeChanged;
            oldFactory.DockableMoved -= OnFloatStateMaybeChanged;

            // Exit() is what actually closes the HostWindow on screen (the
            // model-level CloseWindow only unhooks the dockables and needs a
            // correctly-owned window to remove anything). Snapshot first — a
            // window that closes synchronously mutates the list being walked.
            if (_layout?.Windows is { Count: > 0 } windows)
            {
                foreach (var window in windows.ToList())
                {
                    try { window.Exit(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Dock] Closing orphaned float window failed: {ex.Message}");
                    }
                }

                // Clear unconditionally: a window whose host was never realized
                // has nothing to close, and the old root must not be left
                // holding references either way.
                windows.Clear();
            }
        }

        _factory = null;
        _layout = null;
        _toolDock = null;
    }

    private void OnDockableClosed(object? sender, DockableClosedEventArgs e) => OnLayoutMutated();

    private void OnWindowClosed(object? sender, WindowClosedEventArgs e) => OnLayoutMutated();

    private void OnFloatStateMaybeChanged(object? sender, EventArgs e) => OnLayoutMutated();

    /// <summary>
    /// Shows or hides one pane in the live layout. Showing adds it to the
    /// tool dock (or rebuilds if the layout is gone); hiding closes it.
    /// </summary>
    public void SetPaneVisible(string id, bool visible)
    {
        if (_syncing) return;
        var pane = _panes.FirstOrDefault(p => p.Id == id);
        if (pane == null) return;

        if (_factory == null || _layout == null || _toolDock == null)
        {
            Rebuild();
            return;
        }

        var existing = FindDockable(_layout, id);
        if (visible)
        {
            if (existing != null) return;
            var dockable = CreateDockable(pane);
            _factory.AddDockable(_toolDock, dockable);
            _factory.SetActiveDockable(dockable);
        }
        else if (existing != null)
        {
            _factory.CloseDockable(existing);
        }
    }

    public IDockable? FindPane(string id) => _layout == null ? null : FindDockable(_layout, id);

    /// <summary>Floats a docked pane into a window at the given bounds.</summary>
    public void FloatPane(string id, double x, double y, double width, double height)
    {
        if (_factory == null || _layout == null) return;
        if (FindDockable(_layout, id) is { Owner: IDock owner } dockable)
        {
            try
            {
                _factory.SplitToWindow(owner, dockable, x, y, width, height);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Dock] Floating pane '{id}' failed: {ex.Message}");
            }
        }
    }

    /// <summary>The float window currently hosting a pane, or null if docked/closed.</summary>
    public IDockWindow? GetFloatWindow(string id)
    {
        if (_layout?.Windows == null) return null;
        foreach (var window in _layout.Windows)
        {
            if (window.Layout != null && FindDockable(window.Layout, id) != null)
                return window;
        }

        return null;
    }

    /// <summary>True while the pane lives in a float window.</summary>
    public bool IsPaneFloating(string id) => GetFloatWindow(id) != null;

    /// <summary>
    ///     Brings a floated pane home: out of its float window, back into this host's tool dock
    ///     as the active tab, and the emptied window closed and removed from the model. The only
    ///     way back is dragging the window onto the dock, which is hard to find.
    ///     False when the pane is not floating.
    /// </summary>
    public bool DockPaneHome(string id)
    {
        if (_factory == null || _layout == null || _toolDock == null) return false;
        var window = GetFloatWindow(id);
        if (window?.Layout == null || FindDockable(window.Layout, id) is not { } dockable) return false;

        try
        {
            _factory.DockHome(dockable, _toolDock, window);
            _layout.Windows?.Remove(window);
        }
        catch (Exception ex)
        {
            Log($"Docking pane '{id}' home failed: {ex.Message}");
            return false;
        }

        OnLayoutMutated();
        return true;
    }

    /// <summary>Pins (auto-hides) a docked pane to its dock edge.</summary>
    public void PinPane(string id)
    {
        if (_factory == null || _layout == null) return;
        if (FindDockable(_layout, id) is { } dockable)
        {
            try { _factory.PinDockable(dockable); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Dock] Pinning pane '{id}' failed: {ex.Message}");
            }
        }
    }

    /// <summary>Makes a docked pane the active tab in its dock.</summary>
    public void ActivatePane(string id)
    {
        if (_factory == null || _layout == null) return;
        if (FindDockable(_layout, id) is { } dockable)
            _factory.SetActiveDockable(dockable);
    }

    /// <summary>Writes each registered pane's current mode into the snapshot.</summary>
    public void CaptureState(DockLayoutState state)
    {
        if (_layout == null) return;
        foreach (var pane in _panes)
        {
            var s = new DockPaneState();
            var floatWindow = GetFloatWindow(pane.Id);
            if (floatWindow != null)
            {
                s.Mode = "Floating";
                // X/Y/Width/Height are plain MODEL fields — they only learn
                // where the window actually is when Save() copies them off the
                // platform host. Dock.Avalonia calls it at a drag-END and from
                // the host window's own close; this snapshot runs from
                // MainWindow.OnClosing, i.e. BEFORE any host window closes, and
                // a RESIZE raises no drag at all. Without this the persisted
                // bounds are the ones the window was CREATED at, so a float
                // dragged to the second monitor came home to the middle of the
                // first one at its opening size.
                try { floatWindow.Save(); }
                catch (Exception ex)
                {
                    Log($"Reading the float bounds for pane '{pane.Id}' failed: {ex.Message}");
                }

                s.FloatX = floatWindow.X;
                s.FloatY = floatWindow.Y;
                s.FloatWidth = floatWindow.Width;
                s.FloatHeight = floatWindow.Height;
            }
            else if (EnumeratePinned(_layout).Any(d => d.Id == pane.Id))
            {
                s.Mode = "Pinned";
            }
            else if (FindDockable(_layout, pane.Id) is { } docked)
            {
                s.Mode = "Docked";
                s.IsActive = ReferenceEquals(docked.Owner is IDock owner ? owner.ActiveDockable : null, docked);
            }
            else
            {
                s.Mode = "Closed";
            }

            state.Panes[pane.Id] = s;
        }
    }

    /// <summary>The four modes <see cref="CaptureState" /> ever writes. Anything
    /// else in a snapshot is hand-edited, corrupt, or from a build that is not
    /// this one, and is IGNORED — the pane keeps the default layout. Reading it as
    /// `!= "Closed"` would make any garbage string mean "show it".</summary>
    private static readonly string[] KnownModes = ["Docked", "Floating", "Pinned", "Closed"];

    /// <summary>
    /// Re-applies a snapshot to this controller's panes: visibility flags
    /// first (which adds/closes dockables), then float/pin/active state.
    ///
    /// <para><b>A snapshot is UNTRUSTED INPUT.</b> It round-trips through JSON
    /// in <c>AppConfig</c>, so it can arrive with a null pane entry, a mode
    /// string this build has never written, a pane id that does not exist,
    /// or float bounds that are NaN / infinite / far outside every monitor.
    /// This runs from <c>MainWindow</c>'s <c>Loaded</c> handler, where an
    /// escaping exception takes the rest of first-paint wiring (prewarm, focus
    /// anchoring, the autopilot hook) down with it — so every pane is applied
    /// independently and a pane that cannot be restored is LEFT AT THE DEFAULT
    /// with a logged reason rather than aborting the whole restore.</para>
    /// </summary>
    public void RestoreState(DockLayoutState state)
    {
        foreach (var pane in _panes)
        {
            if (!TryReadPaneState(state, pane.Id, out var s)) continue;
            var visible = s.Mode != "Closed";
            pane.SetVisible(visible);
            try
            {
                SetPaneVisible(pane.Id, visible);
            }
            catch (Exception ex)
            {
                Log($"Restoring visibility for pane '{pane.Id}' failed: {ex.Message}");
            }
        }

        foreach (var pane in _panes)
        {
            if (!TryReadPaneState(state, pane.Id, out var s)) continue;
            switch (s.Mode)
            {
                case "Floating":
                    var bounds = RestoredFloatBounds(s, pane.Id, ScreenBounds());
                    FloatPane(pane.Id, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    break;
                case "Pinned":
                    PinPane(pane.Id);
                    break;
                case "Docked" when s.IsActive:
                    ActivatePane(pane.Id);
                    break;
            }
        }
    }

    /// <summary>Where a float window goes when its persisted origin is
    /// unusable — near the top-left of the primary screen, which every platform
    /// has.</summary>
    private const double DefaultFloatX = 200;

    private const double DefaultFloatY = 200;

    /// <summary>
    ///     The bounds to restore one floating pane at: the persisted values,
    ///     sanitized, and then checked against the monitors that actually EXIST.
    ///     <para>A valid coordinate on a monitor that has since been unplugged is
    ///     indistinguishable to the user from the NaN the sanitizer already
    ///     rejects — the pane is open, the dock is empty and the window is
    ///     nowhere. The SIZE is kept either way (it is not a placement), only the
    ///     ORIGIN falls back.</para>
    /// </summary>
    internal static (double X, double Y, double Width, double Height) RestoredFloatBounds(
        DockPaneState s, string paneId, IReadOnlyList<PixelRect> screens)
    {
        var x = SanitizeBound(s.FloatX, DefaultFloatX, -MaxFloatOffset, MaxFloatOffset);
        var y = SanitizeBound(s.FloatY, DefaultFloatY, -MaxFloatOffset, MaxFloatOffset);
        var width = SanitizeBound(s.FloatWidth, 480, MinFloatWidth, MaxFloatExtent);
        var height = SanitizeBound(s.FloatHeight, 400, MinFloatHeight, MaxFloatExtent);

        if (FloatWindowPlacement.IsReachable(x, y, screens)) return (x, y, width, height);

        Log($"Float bounds for pane '{paneId}' ({x}, {y}) are on no connected monitor — " +
            "restoring it on the primary screen instead.");
        return (DefaultFloatX, DefaultFloatY, width, height);
    }

    /// <summary>The connected monitors, or an empty list when the platform has
    /// not said (headless, or before the top-level is up) — which
    /// <see cref="FloatWindowPlacement.IsReachable" /> reads as "do not
    /// interfere".</summary>
    private IReadOnlyList<PixelRect> ScreenBounds()
    {
        if (ScreenBoundsProvider is { } provider)
        {
            try { return provider(); }
            catch (Exception ex)
            {
                Log($"Reading the connected monitors failed: {ex.Message}");
                return [];
            }
        }

        try
        {
            var screens = TopLevel.GetTopLevel(_host)?.Screens;
            if (screens?.All is not { Count: > 0 } all) return [];
            return all.Select(s => s.Bounds).ToList();
        }
        catch (Exception ex)
        {
            Log($"Reading the connected monitors failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Overrides the monitor list (tests). Null means ask the
    /// platform.</summary>
    internal Func<IReadOnlyList<PixelRect>>? ScreenBoundsProvider { get; set; }

    private const double MinFloatWidth = 320;
    private const double MinFloatHeight = 240;

    /// <summary>Wider than any plausible monitor wall, small enough that a
    /// window placed here is still on-screen-ish rather than at 1e300.</summary>
    private const double MaxFloatExtent = 20000;

    private const double MaxFloatOffset = 20000;

    /// <summary>
    /// Reads one pane's snapshot entry, rejecting the shapes JSON can produce
    /// but <see cref="CaptureState" /> never writes: a null value (a
    /// <c>"Inspector": null</c> in the config dereferenced straight to an NRE
    /// inside <c>Loaded</c>) and an unrecognized mode string.
    /// </summary>
    private static bool TryReadPaneState(DockLayoutState state, string id, out DockPaneState pane)
    {
        pane = null!;
        if (state.Panes == null) return false;
        if (!state.Panes.TryGetValue(id, out var s) || s is null)
        {
            if (state.Panes.ContainsKey(id)) Log($"Snapshot entry for pane '{id}' is null — using the default.");
            return false;
        }

        if (Array.IndexOf(KnownModes, s.Mode) < 0)
        {
            Log($"Snapshot mode '{s.Mode}' for pane '{id}' is not one this build writes — using the default.");
            return false;
        }

        pane = s;
        return true;
    }

    /// <summary>Clamps a persisted float bound into a usable range. A NaN or
    /// infinite value (or a null, from a snapshot written before the field
    /// existed) falls back to the default — restoring a window at NaN puts it
    /// nowhere and the pane reads as lost.</summary>
    internal static double SanitizeBound(double? value, double fallback, double min, double max)
    {
        if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v)) return fallback;
        return Math.Clamp(v, min, max);
    }

    private static void Log(string message) => System.Diagnostics.Debug.WriteLine($"[Dock] {message}");

    private DockableView CreateDockable(DockPaneDefinition pane) => new()
    {
        Id = pane.Id,
        Title = pane.Title,
        Context = pane.Context,
        CanClose = true,
        CanFloat = true,
        // VS-style auto-hide: the pane collapses to an edge tab and flies
        // out on demand. The flyout renders inside this DockControl's
        // bounds, so it never fights the 3D preview's native airspace.
        CanPin = true
    };

    // After any close (tab X, float-window X, or programmatic), sync the
    // view-model flags to what actually survives in the layout so the View
    // menu and sidebar stay truthful — and re-enabling a flag restores the pane.
    private void OnLayoutMutated()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_layout == null) return;
            _syncing = true;
            try
            {
                foreach (var pane in _panes)
                {
                    var present = FindDockable(_layout, pane.Id) != null;
                    if (pane.IsVisible() != present) pane.SetVisible(present);
                    pane.SetFloating?.Invoke(present && IsPaneFloating(pane.Id));
                }
            }
            finally
            {
                _syncing = false;
            }
        });
    }

    private static IDockable? FindDockable(IDockable root, string id)
    {
        if (root.Id == id) return root;
        if (root is IDock dock && dock.VisibleDockables != null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var found = FindDockable(child, id);
                if (found != null) return found;
            }
        }

        if (root is IRootDock rootDock)
        {
            // Floated panes live under the root's windows...
            if (rootDock.Windows != null)
            {
                foreach (var window in rootDock.Windows)
                {
                    if (window.Layout == null) continue;
                    var found = FindDockable(window.Layout, id);
                    if (found != null) return found;
                }
            }

            // ...and pinned (auto-hidden) panes under the pinned collections.
            // Without this the close-watcher would misread "pinned" as
            // "closed" and flip the visibility flag off.
            foreach (var pinned in EnumeratePinned(rootDock))
            {
                var found = FindDockable(pinned, id);
                if (found != null) return found;
            }
        }

        return null;
    }

    private static IEnumerable<IDockable> EnumeratePinned(IRootDock root)
    {
        if (root.LeftPinnedDockables != null)
            foreach (var d in root.LeftPinnedDockables) yield return d;
        if (root.RightPinnedDockables != null)
            foreach (var d in root.RightPinnedDockables) yield return d;
        if (root.TopPinnedDockables != null)
            foreach (var d in root.TopPinnedDockables) yield return d;
        if (root.BottomPinnedDockables != null)
            foreach (var d in root.BottomPinnedDockables) yield return d;
    }
}
