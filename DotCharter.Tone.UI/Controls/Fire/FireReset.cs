using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The reset gesture for a SLIDER, the same two the knob has: a
///     double-click or a right-click puts the value back to
///     <see cref="DefaultProperty" /> (owner: "there should be a
///     way to reset to default state knobs sliders etc like right click or
///     double click"). Attached rather than a subclass so the rig's faders
///     stay plain <c>Slider</c>s under every existing style and test.
///     <code>&lt;Slider fire:FireReset.Default="{Binding Default}" … /&gt;</code>
///     Setting the property is what arms the gesture; a slider that never
///     binds it keeps its stock behaviour.
/// </summary>
public static class FireReset
{
    public static readonly AttachedProperty<double> DefaultProperty =
        AvaloniaProperty.RegisterAttached<Slider, double>("Default", typeof(FireReset), double.NaN);

    public static double GetDefault(Slider slider) => slider.GetValue(DefaultProperty);
    public static void SetDefault(Slider slider, double value) => slider.SetValue(DefaultProperty, value);

    static FireReset() => DefaultProperty.Changed.AddClassHandler<Slider>((slider, _) => Arm(slider));

    private static readonly AttachedProperty<bool> ArmedProperty =
        AvaloniaProperty.RegisterAttached<Slider, bool>("Armed", typeof(FireReset));

    private static void Arm(Slider slider)
    {
        if (slider.GetValue(ArmedProperty)) return;
        slider.SetValue(ArmedProperty, true);
        // Tunnelling, so the reset wins over the Track's own press handling
        // (which would otherwise start a jump-to-click on the same press).
        slider.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEffectivelyEnabled) return;
        var point = e.GetCurrentPoint(slider).Properties;
        var isReset = point.IsRightButtonPressed || (point.IsLeftButtonPressed && e.ClickCount == 2);
        if (!isReset) return;
        if (!TryReset(slider)) return;
        e.Handled = true;
    }

    /// <summary>Resets when a default is bound; false when there is none. The
    /// test seam for the gesture.</summary>
    public static bool TryReset(Slider slider)
    {
        var value = GetDefault(slider);
        if (double.IsNaN(value)) return false;
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        return true;
    }
}
