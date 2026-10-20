using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;

namespace DotCharter.UI.Docking;

/// <summary>
/// Dock factory for DotCharter. Layout construction lives in
/// <see cref="DockLayoutController"/> (driven by the pane registry); this
/// class supplies host-window creation and works around three verified
/// Dock.Avalonia 12.0.0.2 model bugs (unfixed upstream as of master
/// 30b8ec0a /) in the pin + float paths. The 3D Preview stays
/// outside the dock entirely: its NativeControlHost (Vulkan) cannot be
/// re-parented without breaking the swapchain.
/// </summary>
public class DotCharterDockFactory : Factory
{
    public override void InitLayout(IDockable layout)
    {
        DockableLocator = new Dictionary<string, Func<IDockable?>>();
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        base.InitLayout(layout);
    }

    /// <summary>
    /// The tool chrome close button binds CommandParameter="{Binding
    /// ActiveDockable}", which is null on an emptied ToolDock chrome — the
    /// base implementation dereferences the parameter on its first statement
    /// and NREs (hard app crash from a plain close click).
    /// </summary>
    public override void CloseDockable(IDockable? dockable)
    {
        if (dockable is null) return;
        base.CloseDockable(dockable);
    }

    /// <summary>
    /// Dragging a pinned pane's auto-hide flyout out of the dock hits the
    /// DockControlState float fallback with the *preview ToolDock*
    /// (rootDock.PinnedDock) as the dockable. The base factory then neither
    /// unpins the tool nor detaches the preview dock (it lives in the
    /// PinnedDock property, not in any VisibleDockables list), so the same
    /// pane ends up in a float window AND in the pin strip. Redirect the
    /// float to the previewed tool itself — that path unpins and detaches
    /// correctly.
    /// </summary>
    public override void FloatDockable(IDockable dockable, DockWindowOptions? options)
    {
        if (dockable is IToolDock toolDock
            && FindRoot(toolDock) is { } root
            && ReferenceEquals(root.PinnedDock, toolDock))
        {
            var tool = toolDock.ActiveDockable
                       ?? toolDock.VisibleDockables?.FirstOrDefault(d => d is not ISplitter);
            if (tool is null) return;

            var pinnedHome = tool.OriginalOwner as IDock;
            base.FloatDockable(tool, options);
            RememberHome(tool, pinnedHome);
            return;
        }

        var home = dockable.Owner as IDock;
        base.FloatDockable(dockable, options);
        RememberHome(dockable, home);
    }

    public override void SplitToWindow(IDock dock, IDockable dockable, double x, double y, double width, double height)
    {
        var home = dockable.Owner as IDock;
        base.SplitToWindow(dock, dockable, x, y, width, height);
        RememberHome(dockable, home);
    }

    // ---- the way home ------------------------------------------------------------------------
    //
    // A floated tool's chrome menu has a "Dock" item, but Dock.Avalonia enables it only when the
    // tool has an OriginalOwner — which it sets when a tool is AUTO-HIDDEN, never when it floats —
    // and runs PinDockable. So by default "Dock" is greyed out in a float window and dragging the
    // window back is the only way home. A float therefore records the
    // dock it left as the OriginalOwner (enabling "Dock"), PinDockable on a floated tool takes it
    // home, and the float window is titled with the pane's name (not "IToolDock").

    /// <summary>Tools whose OriginalOwner THIS factory set on floating, so that a tool docked back
    /// by any route gets it cleared — a docked tool with an OriginalOwner has "Auto Hide" disabled.</summary>
    private readonly HashSet<IDockable> _homeSetOnFloat = [];

    /// <summary>After a float: remember where the tool came from, and title its window.</summary>
    internal void RememberHome(IDockable dockable, IDock? home)
    {
        if (FindRoot(dockable) is not { Window: { } window }) return;
        window.Title = dockable.Title ?? window.Title;
        window.Host?.SetTitle(window.Title);
        if (home is null || dockable.OriginalOwner is not null) return;
        dockable.OriginalOwner = home;
        _homeSetOnFloat.Add(dockable);
    }

    /// <summary>"Dock" from a floated tool's menu takes it home; anything else pins as before.</summary>
    public override void PinDockable(IDockable dockable)
    {
        if (_homeSetOnFloat.Contains(dockable)
            && dockable.OriginalOwner is IDock home
            && FindRoot(dockable) is { Window: { } window })
        {
            DockHome(dockable, home, window);
            return;
        }

        base.PinDockable(dockable);
    }

    /// <summary>
    ///     Moves a floated tool out of <paramref name="window" /> into <paramref name="home" /> as the
    ///     active tab, then closes the window and drops it from its owner's list. Shared by the menu's
    ///     "Dock" and the "Dock it back here" card (DockLayoutController.DockPaneHome).
    /// </summary>
    internal void DockHome(IDockable dockable, IDock home, IDockWindow window)
    {
        if (dockable.Owner is IDock floatOwner)
        {
            floatOwner.VisibleDockables?.Remove(dockable);
            if (ReferenceEquals(floatOwner.ActiveDockable, dockable))
                floatOwner.ActiveDockable = floatOwner.VisibleDockables?.FirstOrDefault();
        }

        ForgetHome(dockable);
        AddDockable(home, dockable);
        SetActiveDockable(dockable);

        window.Exit();
        if (window.Owner is IRootDock owner) owner.Windows?.Remove(window);
    }

    public override void OnDockableDocked(IDockable? dockable, DockOperation operation)
    {
        base.OnDockableDocked(dockable, operation);
        if (dockable is not null && FindRoot(dockable) is not { Window: not null }) ForgetHome(dockable);
    }

    private void ForgetHome(IDockable dockable)
    {
        if (!_homeSetOnFloat.Remove(dockable)) return;
        dockable.OriginalOwner = null;
    }

    /// <summary>
    /// Upstream PinDockable removes the tool from VisibleDockables but leaves
    /// ActiveDockable pointing at it ("TODO: Handle ActiveDockable state"),
    /// so an IsCollapsable=false ToolDock keeps rendering the pinned pane (a
    /// visual duplicate) and its chrome keeps a stale close target that later
    /// turns into the CloseDockable(null) crash. Dock 12.1.0.8 routes every pin
    /// toggle through this hook (PinDockable after its capability check, and
    /// RemoveDockable for a pinned pane), so the fix covers both.
    /// </summary>
    protected override void PinDockableCore(IDockable dockable)
    {
        var sourceDock = dockable.Owner as IToolDock;

        base.PinDockableCore(dockable);

        if (sourceDock is not null
            && sourceDock.VisibleDockables?.Contains(dockable) != true
            && ReferenceEquals(sourceDock.ActiveDockable, dockable))
        {
            sourceDock.ActiveDockable =
                sourceDock.VisibleDockables?.FirstOrDefault(d => d is not ISplitter);
        }
    }
}
