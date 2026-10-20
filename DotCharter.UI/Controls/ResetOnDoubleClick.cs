using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DotCharter.UI.Controls;

/// <summary>
///     Double-click a slider, get its default back.
///     <para>
///         An attached behaviour rather than a handler per view, because the
///         gesture is the same everywhere and the wiring is what was missing:
///         the mixer's seven faders had it (through a code-behind
///         <c>DoubleTapped</c> handler that keyed off the slider's NAME), the
///         EQ module's four did not, and the owner reported the EQ's as broken.
///         One declaration on the slider is now the whole wiring, and a slider
///         that declares neither sink is visibly a slider with no reset rather
///         than one whose handler quietly went missing.
///     </para>
///     <para>
///         <b>It listens to <see cref="InputElement.DoubleTappedEvent" />, and
///         that was measured rather than assumed.</b> Avalonia's
///         <c>Thumb.OnPointerPressed</c> marks the press handled, so the obvious
///         worry is that the gesture never survives to the slider — it does:
///         <c>Gestures</c> is driven off <c>RouteFinished</c>, which runs whether
///         or not anything handled the event, and the tap is raised on the thumb
///         and bubbles. Verified headless on a bare <c>Slider</c> and on one
///         wearing <c>MixerSlider</c>, pressed on the THUMB and on the TRACK: the
///         event fires in all four, with click counts 1 then 2.
///     </para>
///     <para>
///         Two sinks, because the two callers legitimately keep their default in
///         different places and duplicating it is how the reset and the control
///         come to disagree:
///         <list type="bullet">
///             <item><see cref="DefaultValueProperty" /> — write this value. For
///                 a slider whose neutral is a fact about the DOMAIN (0 dB of
///                 gain, Q 0.707), which is a number the view model can hand over
///                 and nothing else needs to know.</item>
///             <item><see cref="CommandProperty" /> — execute this, with
///                 <see cref="CommandParameterProperty" /> or, failing that, the
///                 control's <c>Name</c>. For the mixer, whose thirteen defaults
///                 already live in one switch on the transport view model.</item>
///         </list>
///         Both may be set; the value is written first, then the command runs.
///     </para>
/// </summary>
public static class ResetOnDoubleClick
{
    /// <summary>
    ///     The value a double-click writes. <see cref="double.NaN" /> — the
    ///     default — arms nothing.
    ///     <para>
    ///         NaN rather than a nullable, so the property is a plain
    ///         <c>double</c> in markup and a binding that resolves to nothing
    ///         cannot silently become "reset to zero".
    ///     </para>
    /// </summary>
    public static readonly AttachedProperty<double> DefaultValueProperty =
        AvaloniaProperty.RegisterAttached<RangeBase, double>(
            "DefaultValue", typeof(ResetOnDoubleClick), double.NaN);

    /// <summary>Executed on a double-click. See the class remarks for when to use
    /// this instead of <see cref="DefaultValueProperty" />.</summary>
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<RangeBase, ICommand?>("Command", typeof(ResetOnDoubleClick));

    /// <summary>What <see cref="CommandProperty" /> is executed with. Unset means
    /// the control's <c>Name</c>, which is what the mixer's switch keys on.</summary>
    public static readonly AttachedProperty<object?> CommandParameterProperty =
        AvaloniaProperty.RegisterAttached<RangeBase, object?>("CommandParameter", typeof(ResetOnDoubleClick));

    static ResetOnDoubleClick()
    {
        DefaultValueProperty.Changed.AddClassHandler<RangeBase>((target, _) => Rearm(target));
        CommandProperty.Changed.AddClassHandler<RangeBase>((target, _) => Rearm(target));
    }

    public static double GetDefaultValue(RangeBase target) => target.GetValue(DefaultValueProperty);

    public static void SetDefaultValue(RangeBase target, double value) =>
        target.SetValue(DefaultValueProperty, value);

    public static ICommand? GetCommand(RangeBase target) => target.GetValue(CommandProperty);

    public static void SetCommand(RangeBase target, ICommand? value) =>
        target.SetValue(CommandProperty, value);

    public static object? GetCommandParameter(RangeBase target) => target.GetValue(CommandParameterProperty);

    public static void SetCommandParameter(RangeBase target, object? value) =>
        target.SetValue(CommandParameterProperty, value);

    /// <summary>Whether this control has anything to reset TO. The lint over the
    /// EQ and mixer markup asks this rather than reading the two properties, so a
    /// third sink could not be added without it being covered.</summary>
    public static bool IsArmed(RangeBase target) =>
        !double.IsNaN(GetDefaultValue(target)) || GetCommand(target) is not null;

    /// <summary>
    ///     Subscribe once, whichever sink arrived first, and never twice.
    ///     <para>
    ///         Both properties can change over the control's life (the command
    ///         arrives with the DataContext; the default value is bound to the
    ///         SELECTED band and moves as the user picks a different one), so the
    ///         removal is unconditional and the add is guarded on
    ///         <see cref="IsArmed" />. Adding per property change is how a handler
    ///         ends up running three times and a reset writes the value, runs the
    ///         command, and then writes it again.
    ///     </para>
    /// </summary>
    private static void Rearm(RangeBase target)
    {
        target.RemoveHandler(InputElement.DoubleTappedEvent, OnDoubleTapped);
        if (IsArmed(target))
            target.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not RangeBase range) return;

        var handled = false;

        var value = GetDefaultValue(range);
        if (!double.IsNaN(value))
        {
            // Clamped to the slider's OWN range: the default travels from a view
            // model that does not know what the track was configured with, and a
            // value outside it would be silently coerced by the binding into
            // something the user never asked for.
            range.Value = Math.Clamp(value, range.Minimum, range.Maximum);
            handled = true;
        }

        if (GetCommand(range) is { } command)
        {
            var parameter = GetCommandParameter(range) ?? range.Name;
            if (command.CanExecute(parameter))
            {
                command.Execute(parameter);
                handled = true;
            }
        }

        e.Handled = handled;
    }
}
