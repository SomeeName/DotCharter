using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Pays the FIRST-OPEN cost of a dropdown ahead of time, off the user's click
///     — the menu bar's menus AND the transport/status rails' popovers. The
///     sibling of <see cref="HiddenSubtreePrewarmer" /> for the surfaces that
///     helper cannot reach, because they live inside a closed <c>Popup</c>.
/// </summary>
/// <remarks>
///     <para>
///         A <c>MenuItem</c>'s rows live inside a closed <c>Popup</c>, so they are
///         not in the visual tree and Avalonia has never styled, templated or
///         measured them. The first click therefore pays the whole realization on
///         the UI thread — measured on the real markup under
///         <c>Avalonia.Headless</c> with the JIT already warm: <b>File 67-104 ms,
///         Edit 59-82, View 39-64, Tools 50-60, Tempo Map 28-40, Vocals 30-69,
///         Help 24-29, Arrangements 19-31</b> on FIRST open, against <b>2-6 ms</b>
///         for every open after it. That gap is the owner's "short 50-100 ms lag
///         when opening top toolbar menus", and it is per menu, once per session,
///         forever.
///     </para>
///     <para>
///         <b>It must not open anything to do that.</b> Opening a popup to warm it
///         is exactly the flicker this is meant to remove, it steals focus, and it
///         deadlocks <c>Avalonia.Headless</c>. Realizing the popup's CHILD does not
///         work either — measured detached, the <c>ItemsPresenter</c> builds no
///         containers at all (0 of 20 rows) and the first open still cost 120 ms.
///         What does work is the containers themselves: the <c>MenuItem</c>s are
///         declared in the markup, so they already exist as objects and are logical
///         children of their parent item — applying their template and measuring
///         them directly styles and lays out the whole row (icon geometry, header
///         and gesture text included). Same markup, same run: first open
///         <b>7-27 ms</b> per menu, i.e. close to what a repeat open costs.
///     </para>
///     <para>
///         Nested submenus are realized too, depth-first: File, Edit,
///         Tempo Map, View and Tools all group their verbs one level down, and a
///         submenu that was skipped here would just move the stall to hovering it.
///     </para>
/// </remarks>
public static class MenuFlyoutPrewarmer
{
    /// <summary>
    ///     Realizes one menu's rows — and the rows of every submenu under it.
    ///     Never opens a popup, never writes <c>IsSubMenuOpen</c> or
    ///     <c>IsVisible</c>, and never throws: a menu that cannot measure in
    ///     isolation just stays cold and pays on first open.
    /// </summary>
    /// <returns>How many rows were realized.</returns>
    public static int Realize(MenuItem item, Size available)
    {
        if (!CanRealizeInto(available)) return 0;

        try
        {
            return RealizeCore(item, available);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Menu flyout prewarm of '{item.Header}' failed: {ex}");
            return 0;
        }
    }

    /// <summary>
    ///     Realizes <paramref name="menus" /> ONE PER dispatcher turn at
    ///     <see cref="DispatcherPriority.Background" />, so the total never lands
    ///     as a single freeze and real input always preempts it. Call only once
    ///     the first frame has been presented.
    /// </summary>
    /// <param name="menus">The top-level headers, in the order the user is most
    ///     likely to reach them.</param>
    /// <param name="available">Evaluated per menu — the window may be resized
    ///     between turns.</param>
    /// <param name="onDone">Runs on the UI thread once the queue is drained, with
    ///     (rows, menus).</param>
    public static void ScheduleDrain(
        IReadOnlyList<MenuItem> menus, Func<Size> available, Action<int, int>? onDone = null)
    {
        var total = menus.Count;
        if (total == 0)
        {
            onDone?.Invoke(0, 0);
            return;
        }

        var index = 0;
        var rows = 0;

        void Step()
        {
            if (index >= total)
            {
                onDone?.Invoke(rows, total);
                return;
            }

            rows += Realize(menus[index++], available());
            PrewarmPacer.Post(Step);
        }

        PrewarmPacer.Post(Step);
    }

    private static int RealizeCore(MenuItem item, Size available)
    {
        // The parent's own template holds the Popup the rows will be hosted in.
        // Applying it here keeps that off the click too; the rows below are
        // realized whether it succeeded or not.
        item.ApplyTemplate();

        var rows = 0;
        foreach (var row in item.Items.OfType<MenuItem>())
        {
            // Measure only ever consults an element's OWN IsVisible, and a row
            // inside a closed popup is visible — it simply has no visual parent
            // yet. Styles reach it through the LOGICAL tree, which its parent
            // item already gave it.
            row.ApplyTemplate();
            row.Measure(available);
            row.Arrange(new Rect(row.DesiredSize));
            rows++;

            // Depth-first: a grouped verb is one hover away, not one click.
            rows += RealizeCore(row, available);
        }

        return rows;
    }

    // ── The TRANSPORT bar's popovers ────────────────────────────────────────

    /// <summary>
    ///     One popover a prewarm can reach: the flyout, and the control it hangs
    ///     off (which is what gives it a style scope and a place in the tree).
    /// </summary>
    public readonly record struct FlyoutTarget(Control Target, PrewarmableFlyout Flyout);

    /// <summary>
    ///     Every <see cref="PrewarmableFlyout" /> declared under
    ///     <paramref name="root" />, whether it hangs off a <c>Button.Flyout</c>,
    ///     off <c>FlyoutBase.AttachedFlyout</c> or off a <c>ContextFlyout</c>.
    /// </summary>
    /// <remarks>
    ///     DERIVED from the markup rather than hand-listed, so a popover added to
    ///     the tool rail is prewarmed the day it compiles. The walk is LOGICAL:
    ///     these are all declared elements, and the logical tree is populated
    ///     whether or not the host has been laid out yet (the shell's chart editor
    ///     is <c>IsVisible=false</c> for the whole boot).
    /// </remarks>
    public static IReadOnlyList<FlyoutTarget> FlyoutsUnder(Control root)
    {
        var found = new List<FlyoutTarget>();

        foreach (var control in root.GetSelfAndLogicalDescendants().OfType<Control>())
        {
            if (control is Button { Flyout: PrewarmableFlyout onButton })
                found.Add(new FlyoutTarget(control, onButton));

            if (FlyoutBase.GetAttachedFlyout(control) is PrewarmableFlyout attached)
                found.Add(new FlyoutTarget(control, attached));

            // A right-click popover (the timeline's lane menu).
            if (control.ContextFlyout is PrewarmableFlyout context)
                found.Add(new FlyoutTarget(control, context));
        }

        return found;
    }

    /// <summary>
    ///     Builds one popover's popup, presenter and content — and styles,
    ///     templates and measures the content where it now stands — without
    ///     opening anything.
    /// </summary>
    /// <remarks>
    ///     The realize itself is <see cref="PrewarmableFlyout.RealizeBuiltContent" />
    ///     — safe here and ONLY here, because the cancelled open left the popup host
    ///     attached and therefore left the content a style scope to resolve against.
    ///     The presenter is measured first so its own chrome (the scroll viewer, the
    ///     border, the shadow) is realized, then the CONTENT is measured directly:
    ///     <c>ContentPresenter</c> only builds its child from inside a layout pass
    ///     it is itself part of, and a closed popup never runs one, so measuring
    ///     the presenter alone realizes 57 visuals of scrollbar chrome and none of
    ///     the popover.
    /// </remarks>
    /// <returns>1 if the popover was realized, 0 if it was skipped.</returns>
    public static int Realize(FlyoutTarget popover, Size available)
    {
        if (!CanRealizeInto(available)) return 0;

        try
        {
            if (popover.Flyout.BuildWithoutOpening(popover.Target) is null) return 0;
            return popover.Flyout.RealizeBuiltContent(available) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Flyout prewarm of '{popover.Target.Name ?? popover.Target.GetType().Name}' failed: {ex}");
            return 0;
        }
    }

    /// <summary>
    ///     Realizes <paramref name="popovers" /> one per <c>Background</c>
    ///     dispatcher turn, exactly like the menu drain above.
    /// </summary>
    public static void ScheduleDrain(
        IReadOnlyList<FlyoutTarget> popovers, Func<Size> available, Action<int, int>? onDone = null)
    {
        var total = popovers.Count;
        if (total == 0)
        {
            onDone?.Invoke(0, 0);
            return;
        }

        var index = 0;
        var realized = 0;

        void Step()
        {
            if (index >= total)
            {
                onDone?.Invoke(realized, total);
                return;
            }

            realized += Realize(popovers[index++], available());
            PrewarmPacer.Post(Step);
        }

        PrewarmPacer.Post(Step);
    }

    /// <summary>
    ///     Whether <paramref name="size" /> is a constraint worth measuring
    ///     against. Public and pure because it is the one part of this helper a
    ///     DEFAULT-GATE test can reach: building a <c>MenuItem</c> tree needs the
    ///     dispatcher thread (<c>ItemCollection.Add</c> verifies access), so the
    ///     behavioural half lives in the smoke tests.
    ///     <para>
    ///         The window has no size until it is shown, and an infinite
    ///         constraint would measure every row against infinity — which is not
    ///         the layout the popup will really run.
    ///     </para>
    /// </summary>
    public static bool CanRealizeInto(Size size) =>
        size is { Width: > 0, Height: > 0 } &&
        !double.IsNaN(size.Width) && !double.IsNaN(size.Height) &&
        !double.IsInfinity(size.Width) && !double.IsInfinity(size.Height);
}
