using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DotCharter.UI.Platform;

/// <summary>
///     A wheel-adjustable control — a <see cref="Slider" />, or anything that
///     implements <see cref="IWheelAdjustable" /> — may not eat the mouse wheel
///     of the panel it sits in.
///     <para>
///         Scrolling a settings or mixer pane must never edit whichever value
///         happens to pass under the pointer — reported on the mixer tab, where
///         scrolling down moved the filter's Q. That is a silent, undoable-only
///         edit to a value the user never aimed at, and it is the same fault the
///         chord editor's finger ComboBox had: a control that claims the wheel
///         while the wheel belongs to the surface behind it.
///     </para>
///     <para>
///         Avalonia 12's own <see cref="Slider" /> has no wheel handler at all
///         (checked against 12.1.3's source; the headless
///         <c>MixerScrollHostUiPerfSmokeTests</c> pass with the Slider dropped
///         from this policy). It stays covered because a slider template or a
///         later Avalonia that adds one would reopen the bug silently.
///     </para>
///     <para>
///         The rule is absolute: a wheel-adjustable control inside a scroll host
///         NEVER takes a bare notch. Drag to adjust, double-click to reset, wheel
///         to scroll.
///     </para>
///     <para>
///         It was not absolute at first — it exempted a slider that had
///         keyboard focus, on the platform convention "click it, then wheel
///         it". That exemption put the bug straight back, because a mixer is a
///         wall of sliders you click constantly: one click on a fader leaves
///         it focused, and every later scroll whose pointer crosses it moves
///         it. The owner reported the same Q slider twice, the second time as
///         moving "without even being touched" — accurate, because the touch
///         that armed it was minutes earlier and on a different control. A
///         convention that needs the user to track where focus is is not
///         worth a silent unintended edit.
///     </para>
///     <para>
///         Implemented on the SCROLL VIEWER in TUNNEL, which is the only place it
///         can work: routed events tunnel top-down, so the host sees the notch
///         before the slider does — a bubble handler would arrive after the
///         slider had already moved. Marking it handled there stops the slider,
///         and the host is scrolled explicitly in its place.
///     </para>
///     <para>
///         The SLIDER is not the only control that turns a notch into an edit.
///         The EQ module's plot (<c>EqPlotControl</c>) mapped the wheel to the
///         selected band's Q, and the plot is 100 px tall in the middle of the
///         Mixer tab: scroll the tab and, a few notches in, the plot slides under
///         the pointer, takes every notch from then on (the panel stops dead) and
/// walks Band 1's Q — which the Q slider below it dutifully shows.
///         The guard never saw it, because it only looked for a Slider. So the
///         rule is about any WHEEL-ADJUSTABLE control: a <see cref="Slider" />,
///         or anything that implements <see cref="IWheelAdjustable" />. A control
///         that wants the wheel for itself must ask for a MODIFIER the user
///         cannot press by accident while scrolling (the plot: Ctrl + wheel).
///     </para>
///     <para>
///         Nesting cannot bypass it either: the OUTERMOST scroll host is the
///         first to see the notch on the tunnel, it scrolls the innermost host
///         between the control and itself that can still move in that
///         direction (or itself), and the notch is handled before any inner host
///         or the control is reached.
///     </para>
/// </summary>
public static class SliderWheelPolicy
{
    /// <summary>Lines per notch, matching Avalonia's own ScrollViewer step.</summary>
    private const double WheelStep = 50.0;

    private static readonly object Gate = new();
    private static bool _installed;

    /// <summary>
    ///     Pure half: does this notch belong to the scroll host rather than to
    ///     the control under the pointer? Over a wheel-adjustable control it
    ///     does, unless that control asked for this notch's modifiers
    ///     (<see cref="IWheelAdjustable.TakesWheel" />; a Slider never does).
    ///     Focus deliberately does NOT enter into it; see the type doc for what
    ///     happened when it did.
    /// </summary>
    public static bool BelongsToTheScrollHost(bool overAWheelAdjustable, bool controlTakesThisNotch = false) =>
        overAWheelAdjustable && !controlTakesThisNotch;

    /// <summary>
    ///     Installs the policy for the process. Idempotent; call it before any
    ///     view is built (<c>App.Initialize</c>).
    /// </summary>
    public static void Apply()
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;
            InputElement.PointerWheelChangedEvent.AddClassHandler<ScrollViewer>(
                static (host, e) => Guard(host, e),
                RoutingStrategies.Tunnel);
        }
    }

    /// <summary>
    ///     The nearest wheel-adjustable control at or above <paramref name="source" />
    ///     — a <see cref="Slider" /> (its Thumb, Track and RepeatButtons are
    ///     template children, so the hit is usually one of those) or an
    ///     <see cref="IWheelAdjustable" />.
    /// </summary>
    internal static Visual? FindWheelAdjustable(Visual source)
    {
        if (source is Slider or IWheelAdjustable) return source;
        return source.GetVisualAncestors().FirstOrDefault(static v => v is Slider or IWheelAdjustable);
    }

    private static void Guard(ScrollViewer host, PointerWheelEventArgs e)
    {
        if (e.Handled) return;
        if (e.Source is not Visual source) return;

        var control = FindWheelAdjustable(source);
        if (control == null) return;
        // …and only when the control really sits inside THIS host. A control in
        // a popup reaches the host's tunnel through the popup's logical parent,
        // but the host scrolling would not move it.
        if (!control.GetVisualAncestors().Contains(host)) return;

        var takes = control is IWheelAdjustable adjustable && adjustable.TakesWheel(e.KeyModifiers);
        if (!BelongsToTheScrollHost(true, takes)) return;

        // Scroll in the control's place — the innermost host between it and
        // this one that can still move this way, else this one — then stop the
        // notch before it reaches any inner host or the control's own handler.
        var target = host;
        foreach (var inner in control.GetVisualAncestors().OfType<ScrollViewer>())
        {
            if (inner == host) break;
            if (CanScroll(inner, e.Delta))
            {
                target = inner;
                break;
            }
        }

        var offset = target.Offset;
        var next = Next(target, e.Delta);
        if (!next.NearlyEquals(offset)) target.Offset = next;
        e.Handled = true;
    }

    private static Vector Next(ScrollViewer host, Vector delta)
    {
        var offset = host.Offset;
        return new Vector(
            Math.Clamp(offset.X - delta.X * WheelStep, 0, Math.Max(0, host.Extent.Width - host.Viewport.Width)),
            Math.Clamp(offset.Y - delta.Y * WheelStep, 0, Math.Max(0, host.Extent.Height - host.Viewport.Height)));
    }

    private static bool CanScroll(ScrollViewer host, Vector delta) => !Next(host, delta).NearlyEquals(host.Offset);

    private static bool NearlyEquals(this Vector a, Vector b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;
}
