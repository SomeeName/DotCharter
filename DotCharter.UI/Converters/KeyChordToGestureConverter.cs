using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;

namespace DotCharter.UI.Converters;

/// <summary>
/// Renders a user-remappable <see cref="KeyChord"/> as the menu accelerator
/// label. Static InputGesture literals go stale the moment the user remaps a
/// shortcut in Settings → Keybinds; binding through this converter keeps the
/// menus truthful.
/// <para>
/// A digit FAMILY (<c>BookmarkBase</c>: its modifiers + 0..9) takes the digit as the
/// <c>ConverterParameter</c> and gets the chord the registrar derives for that row —
/// <c>(Key)((int)Key.D0 + digit)</c> with the base's modifiers.
/// </para>
/// </summary>
public sealed class KeyChordToGestureConverter : IValueConverter
{
    public static readonly KeyChordToGestureConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not KeyChord chord || !chord.IsBound) return null;
        if (Digit(parameter) is { } digit) chord = new KeyChord((Key)((int)Key.D0 + digit), chord.Modifiers);

        // A TEXT target gets the chord's OWN spelling, not the gesture's.
        // `KeyGesture.ToString()` prints Avalonia's enum name — the Tempo
        // popover's footer read "OemMinus OemPlus" where the user's keyboard
        // says "-" and "=" — while `KeyChord.ToString()` already carries the
        // table that turns every Oem key, digit and numpad key into the
        // character printed on the cap. A menu accelerator still needs the real
        // `KeyGesture`, so the target type is what decides.
        if (targetType == typeof(string)) return chord.ToDisplayString(LocalizationService.Instance);

        return new KeyGesture(chord.Key, chord.Modifiers);
    }

    private static int? Digit(object? parameter) => parameter switch
    {
        int i and >= 0 and <= 9 => i,
        string text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 9 => d,
        _ => null
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
