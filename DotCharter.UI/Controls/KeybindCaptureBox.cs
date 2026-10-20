using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DotCharter.UI.Controls;

/// <summary>
///     A keybind capture field. Renders the bound <see cref="KeyChord" /> as a
///     formatted label ("Ctrl+Shift+S"). When focused, the next non-modifier
///     key press (with whatever modifiers are held) is captured and written
///     back through the two-way <see cref="ChordProperty" /> binding.
///     Backspace clears the binding, Escape cancels and unfocuses.
/// </summary>
public class KeybindCaptureBox : TemplatedControl
{
    public static readonly StyledProperty<KeyChord> ChordProperty =
        AvaloniaProperty.Register<KeybindCaptureBox, KeyChord>(
            nameof(Chord),
            defaultValue: KeyChord.None,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<KeybindCaptureBox, string>(
            nameof(Placeholder), "Press a key…");

    public static readonly StyledProperty<bool> IsCapturingProperty =
        AvaloniaProperty.Register<KeybindCaptureBox, bool>(nameof(IsCapturing));

    public static readonly StyledProperty<string> DisplayTextProperty =
        AvaloniaProperty.Register<KeybindCaptureBox, string>(nameof(DisplayText), "—");

    public KeyChord Chord
    {
        get => GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public bool IsCapturing
    {
        get => GetValue(IsCapturingProperty);
        private set => SetValue(IsCapturingProperty, value);
    }

    public string DisplayText
    {
        get => GetValue(DisplayTextProperty);
        private set => SetValue(DisplayTextProperty, value);
    }

    static KeybindCaptureBox()
    {
        FocusableProperty.OverrideDefaultValue<KeybindCaptureBox>(true);
        // Transparent background guarantees the entire control surface is
        // hit-testable, even if the templated Border's Background resource
        // ever fails to resolve. Without this, only painted child elements
        // (the text glyphs) would receive pointer events.
        BackgroundProperty.OverrideDefaultValue<KeybindCaptureBox>(Brushes.Transparent);
        ChordProperty.Changed.AddClassHandler<KeybindCaptureBox>((o, _) => o.UpdateDisplayText());
    }

    public KeybindCaptureBox()
    {
        UpdateDisplayText();
        GotFocus += OnGotFocusedHandler;
        LostFocus += OnLostFocusedHandler;
        // Subscribe with Tunnel routing so we intercept Ctrl/Alt/Shift+key
        // chords *before* parent KeyBindings or HotKeys (e.g. Ctrl+F filter,
        // Ctrl+S save) consume them. Without this, modifier-combos never
        // reach OnKeyDown when the parent dialog has any matching shortcut.
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnTunnelKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsCapturing) return;
        HandleKeyDown(e);
    }

    private void OnTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        // Swallow key-ups for the captured chord so they don't bubble to
        // global handlers / KeyBindings while we're armed.
        if (IsCapturing) e.Handled = true;
    }

    private void OnGotFocusedHandler(object? sender, RoutedEventArgs e) => Arm();

    private void OnLostFocusedHandler(object? sender, RoutedEventArgs e) => Disarm();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsFocused)
        {
            // Already focused — clicking again re-arms for another capture.
            Arm();
        }
        else
        {
            Focus();
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Bubble-phase fallback in case the tunnel handler didn't run
        // (e.g. event re-raised internally by Avalonia).
        if (IsCapturing) HandleKeyDown(e);
    }

    private void Arm()
    {
        IsCapturing = true;
        PseudoClasses.Set(":capturing", true);
    }

    private void Disarm()
    {
        IsCapturing = false;
        PseudoClasses.Set(":capturing", false);
    }

    private void HandleKeyDown(KeyEventArgs e)
    {
        if (e.Handled) return;

        // Always swallow keys while armed so global keybinds (e.g. Space =
        // PlayPause) don't fire while the user is rebinding.
        e.Handled = true;

        if (e.Key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin
            or Key.System)
        {
            // Wait for the actual key — modifier-only press doesn't bind.
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Esc fully clears the binding (was: cancel-without-change). Users
            // overwhelmingly hit Esc expecting "remove this shortcut", so we
            // route to the same path as Backspace/Delete for consistency.
            Chord = KeyChord.None;
            Disarm();
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            Chord = KeyChord.None;
            Disarm();
            return;
        }

        if (e.Key == Key.Tab)
        {
            // Allow Tab to move focus normally so users can keyboard-navigate.
            e.Handled = false;
            Disarm();
            return;
        }

        var mods = KeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) mods |= KeyModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) mods |= KeyModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) mods |= KeyModifiers.Alt;

        Chord = new KeyChord(e.Key, mods);
        // Capture complete — disarm but keep focus. Click again to rebind.
        Disarm();
    }

    private void UpdateDisplayText() => DisplayText = Chord.IsBound ? Chord.ToDisplayString(LocalizationService.Instance) : "—";
}
