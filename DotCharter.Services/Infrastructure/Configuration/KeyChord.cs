using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;
using DotCharter.Localization;

namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     A user-configurable key combination — a single key plus optional Ctrl/Shift/Alt
///     modifiers. Replaces the previous design where the key was stored as a bare
///     <see cref="Key" /> value and modifiers were hard-coded on each Register call
///     in <c>AppKeybindRegistrar</c>. Now the user owns the modifier choice on every
///     binding.
/// </summary>
[JsonConverter(typeof(KeyChordJsonConverter))]
public readonly record struct KeyChord(Key Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    public static readonly KeyChord None = new(Key.None);

    /// <summary>The chord spelled the way the config stores it (<c>Ctrl+Shift+S</c>); an unknown key parses as <see cref="None" />.</summary>
    public static KeyChord Parse(string? text) => KeyChordJsonConverter.Parse(text);

    public bool IsBound => Key != Key.None;

    public bool Matches(Key key, bool ctrl, bool shift, bool alt) =>
        Key == key
        && ctrl == Modifiers.HasFlag(KeyModifiers.Control)
        && shift == Modifiers.HasFlag(KeyModifiers.Shift)
        && alt == Modifiers.HasFlag(KeyModifiers.Alt);

    /// <summary>
    ///     The chord in its INVARIANT form (e.g. <c>Ctrl+Shift+S</c>) — for logs,
    ///     for a failure message and for a test that pins a default binding.
    ///     <para>
    ///         <b>Not for the screen.</b> A German keyboard has no key marked
    ///         "Ctrl"; the cap says <i>Strg</i>. Call
    ///         <see cref="ToDisplayString" /> for anything a user reads, and
    ///         keep this one stable — making <c>ToString</c> itself follow the
    ///         UI language would put process-global state inside a method the
    ///         default gate asserts on, which is the shape of flake this repo
    ///         already serialized a whole collection to stop.
    ///     </para>
    /// </summary>
    public override string ToString() => Render(null);

    /// <summary>
    ///     The chord as the user's own keyboard spells it: <c>Strg+Umschalt+S</c>
    ///     in German. Only the MODIFIERS are language — the key half is the
    ///     character printed on the cap and
    ///     <see cref="FormatKey" /> already produces that.
    /// </summary>
    public string ToDisplayString(ILocalizationService localization) => Render(localization);

    private string Render(ILocalizationService? localization)
    {
        if (Key == Key.None) return "—";

        string Modifier(string key, string fallback) => localization?[key] is { Length: > 0 } text
            ? text
            : fallback;

        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add(Modifier("Keybind_Mod_Ctrl", "Ctrl"));
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add(Modifier("Keybind_Mod_Alt", "Alt"));
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add(Modifier("Keybind_Mod_Shift", "Shift"));
        parts.Add(FormatKey(Key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Back => "Backspace",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemBackslash => "\\",
        Key.OemPipe => "\\",
        Key.OemQuestion => "/",
        Key.OemTilde => "`",
        Key.Add => "Num+",
        Key.Subtract => "Num-",
        Key.Multiply => "Num*",
        Key.Divide => "Num/",
        _ => key.ToString()
    };
}

/// <summary>
///     Serializes <see cref="KeyChord" /> as a single string ("Ctrl+Shift+S") so the
///     keybind file stays readable and round-trips cleanly. Also accepts the old
///     bare-key format ("S") for backwards compatibility with pre-refactor settings.
/// </summary>
public sealed class KeyChordJsonConverter : JsonConverter<KeyChord>
{
    public override KeyChord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return KeyChord.None;

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            return Parse(raw);
        }

        // Legacy: pre-refactor configs persisted Key as a bare integer.
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var asInt))
            return new KeyChord((Key)asInt);

        return KeyChord.None;
    }

    public override void Write(Utf8JsonWriter writer, KeyChord value, JsonSerializerOptions options) => writer.WriteStringValue(Serialize(value));

    public static string Serialize(KeyChord chord)
    {
        if (!chord.IsBound) return "None";
        var parts = new List<string>(4);
        if (chord.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(chord.Key.ToString());
        return string.Join("+", parts);
    }

    public static KeyChord Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("None", StringComparison.OrdinalIgnoreCase))
            return KeyChord.None;

        var mods = KeyModifiers.None;
        var key = Key.None;
        foreach (var part in raw.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                mods |= KeyModifiers.Control;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                mods |= KeyModifiers.Shift;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                mods |= KeyModifiers.Alt;
            }
            else if (Enum.TryParse<Key>(token, true, out var parsed))
            {
                key = parsed;
            }
        }

        return new KeyChord(key, mods);
    }
}
