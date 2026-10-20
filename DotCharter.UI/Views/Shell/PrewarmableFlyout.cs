using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     A <see cref="Flyout" /> whose popup, presenter and content can be BUILT
///     without ever being shown — the one thing a plain <c>Flyout</c> cannot do,
///     and the whole reason the transport bar's popovers still lagged on first
///     open after <see cref="MenuFlyoutPrewarmer" /> fixed the menu bar.
/// </summary>
/// <remarks>
///     <para>
///         A menu's rows are declared <c>MenuItem</c>s that are already logical
///         children of their header, so the prewarm can style and measure them
///         where they stand. A flyout's content is not: until the first open
///         there is no <c>Popup</c> and no <c>FlyoutPresenter</c> at all, so the
///         content sits in the object graph with <b>no parent</b>. Measured
///         detached it gets no styles, no control themes and therefore no
///         templates — exactly the "0 of 20 rows" dead end
///         <see cref="MenuFlyoutPrewarmer" /> records for the popup's child — and
///         the first open still pays for everything.
///     </para>
///     <para>
///         Avalonia builds that structure inside <c>ShowAtCore</c>, and it builds
///         it <b>before</b> it asks whether the open may proceed. So a subclass
///         that cancels its own <see cref="OnOpening" /> gets the popup, the
///         presenter and a logically-parented content tree — inside the host's
///         real style scope — while <c>IsOpen</c> stays <c>false</c> and no popup
///         window is ever created. The structure is kept (the popup is created
///         once per flyout and reused), so the styling and layout the prewarm
///         then does STICKS.
///     </para>
///     <para>
///         <b>Nothing opens.</b> That is asserted behaviourally rather than by a
///         source lint, because the whole trick is a call to <c>ShowAt</c>:
///         <c>UI/PrewarmableFlyoutTests</c> pins the cancel override and
///         <c>Perf/FlyoutPrewarmUiPerfSmokeTests</c> pins that a realized flyout
///         reports <c>IsOpen == false</c>, has no popup host, and still opens
///         normally afterwards.
///     </para>
/// </remarks>
public class PrewarmableFlyout : Flyout
{
    private bool _suppressOpen;
    private long _openingTicks;
    private Control? _openTarget;

    /// <summary>
    ///     Creates the popup + presenter and gives <see cref="Flyout.Content" /> a
    ///     logical parent, without opening anything. Returns the presenter, or
    ///     <c>null</c> if the structure could not be built (an unattached target,
    ///     a flyout that is already open).
    /// </summary>
    /// <remarks>
    ///     Idempotent: once the popup exists this is a property read. It is also
    ///     a no-op while the flyout is OPEN — re-entering <c>ShowAt</c> there
    ///     would close it, which is the one user-visible thing this must never do.
    /// </remarks>
    public Control? BuildWithoutOpening(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (IsOpen) return null;
        if (Popup?.Child is { } already) return already;

        _suppressOpen = true;
        try
        {
            ShowAt(target);
        }
        finally
        {
            _suppressOpen = false;
        }

        // Belt and braces: if a future Avalonia moves the cancel check ahead of
        // the popup construction, the open would go through and this is the only
        // place that could notice.
        if (IsOpen) Hide();

        return Popup?.Child;
    }

    /// <summary>
    ///     Styles, templates and MEASURES the already-built presenter and content
    ///     where they stand. Does not build anything — call
    ///     <see cref="BuildWithoutOpening" /> first — and never opens anything.
    /// </summary>
    /// <returns><c>true</c> when there was a built popup to realize.</returns>
    public bool RealizeBuiltContent(Size available)
    {
        if (Popup?.Child is not { } presenter) return false;

        presenter.ApplyTemplate();
        presenter.Measure(available);
        presenter.Arrange(new Rect(presenter.DesiredSize));

        if (Content is Control content)
        {
            content.ApplyTemplate();
            content.Measure(available);
            content.Arrange(new Rect(content.DesiredSize));
        }

        return true;
    }

    /// <summary>
    ///     <b>Never realize a popover while it is DETACHED.</b> A close-time
    ///     re-realize was tried that way, it shipped, and the owner caught it:
    ///     every dropdown came back unstyled. <see cref="OnClosed" /> re-parents first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The reasoning was sound and the measurement was real — closing
    ///         disposes the popup host, which detaches the presenter, and Avalonia
    ///         invalidates measure and arrange on re-attach, so the prewarm buys
    ///         the FIRST open only and a repeat costs 10-15 ms instead of 3.
    ///         Re-measuring the closed body at <c>Background</c> took that back to
    ///         6-7 ms.
    ///     </para>
    ///     <para>
    ///         What it also did: a DETACHED subtree has no style scope, so
    ///         <c>ApplyTemplate</c> and the measure that follows resolve every
    ///         <c>DynamicResource</c> against nothing and cache the FALLBACK. The
    ///         next open then drew unstyled headings, plain rows and sliders with
    ///         no gradient — four of six brushes wrong on the toolbar's popovers.
    ///     </para>
    ///     <para>
    ///         The guard it shipped with asserted <c>IsMeasureValid</c> and
    ///         <c>IsArrangeValid</c> and was green throughout, because a view whose
    ///         styles failed still arranges — this repo's own rule, from the other
    ///         direction. <c>Perf/PopoverRestyleUiPerfSmokeTests</c> compares the
    ///         RESOLVED BRUSHES across a close and is the guard that would have
    ///         caught it.
    ///     </para>
    ///     <para>
    ///         Anything that revives this idea has to re-parent into a live style
    ///         scope FIRST. Realizing where the content stands is only safe while
    ///         the popup host is still attached — which is exactly why the initial
    ///         prewarm goes through <see cref="BuildWithoutOpening" />'s cancelled
    ///         open rather than measuring the content detached.
    ///     </para>
    /// </remarks>
    protected override void OnOpening(CancelEventArgs args)
    {
        base.OnOpening(args);
        if (_suppressOpen) { args.Cancel = true; return; }
        _openingTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Opening → Opened is the popup host's whole cost on this box —
    /// the per-open HWND and surface included — so it is what gets logged
    /// (<see cref="PopoverOpenTiming" />).</summary>
    protected override void OnOpened()
    {
        base.OnOpened();
        if (_openingTicks == 0) return;
        var openingTicks = _openingTicks;
        var ms = PopoverOpenTiming.MsSince(openingTicks);
        _openingTicks = 0;
        _openTarget = Target;
        var name = Target is Control { Name.Length: > 0 } target
            ? target.Name
            : Content?.GetType().Name ?? "flyout";
        PopoverOpenTiming.Record(name, ms);
        // Opening → Opened read 11-17 ms on the owner's machine while the
        // owner still felt a lag: the content's first layout and render come
        // AFTER Opened. This times Opening → the first idle tick, which is what
        // the user waits for, through the same log line as Inspector tabs.
        SurfaceShowTiming.BeginAt($"Popover {name}", openingTicks);
    }

    /// <summary>
    /// put the closed popup BACK into its target's logical tree, then
    ///     re-realize it, so the next open costs what the first prewarmed one did.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Avalonia's close takes the <c>Popup</c> OUT of the logical tree, and
    ///         the next <c>ShowAt</c> puts it back BEFORE <c>OnOpening</c>: the whole
    ///         popover subtree is re-styled and re-templated inside the click,
    ///         outside the Opening → Opened window the log line measures. On the
    ///         real window that was 25-90 ms per repeat open of every toolbar
    ///         popover against 2-5 ms logged (perf autopilot,
    ///         <c>DOTCHARTER_AUTOPILOT_POPOVERS=1</c>).
    ///     </para>
    ///     <para>
    ///         This is the re-realize the remarks above forbid — done the way they
    ///         say it has to be: the popup is re-parented into the target's LIVE
    ///         style scope first, so every <c>DynamicResource</c> resolves against
    ///         the real theme, then the content is measured where it stands — the
    ///         state the startup prewarm leaves it in. At <c>Background</c>, after
    ///         the close has painted. Guarded by
    ///         <c>Perf/PopoverReopenUiPerfSmokeTests</c> (parented, realized) and
    ///         <c>Perf/PopoverRestyleUiPerfSmokeTests</c> (the same brushes).
    ///     </para>
    /// </remarks>
    protected override void OnClosed()
    {
        base.OnClosed();
        if (_openTarget is not { } target) return;
        Dispatcher.UIThread.Post(() => RestoreClosedStructure(target), DispatcherPriority.Background);
    }

    /// <summary>The re-parent + re-realize <see cref="OnClosed" /> schedules;
    /// a no-op once the flyout has opened again or the target left the tree.</summary>
    internal void RestoreClosedStructure(Control target)
    {
        if (IsOpen || Popup is not { } popup) return;
        if (!((ILogical)target).IsAttachedToLogicalTree) return;
        if (((ILogical)popup).LogicalParent is null)
        {
            ((ISetLogicalParent)popup).SetParent(target);
            popup.PlacementTarget = target;
        }

        if (TopLevel.GetTopLevel(target) is { } top)
            RealizeBuiltContent(top.ClientSize);
    }
}
