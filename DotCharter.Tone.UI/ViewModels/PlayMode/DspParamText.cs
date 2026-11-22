using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     A native parameter's caption or value label in the UI language. The engine names its parameters
///     in English; a name with a <c>DspLabel_</c> entry (its letters and digits after the prefix) is shown
///     translated, and any other name, such as a gear model, a note or a number, keeps the engine's text.
/// </summary>
internal static class DspParamText
{
    internal const string Prefix = "DspLabel_";

    /// <summary>The text to show for the engine's <paramref name="english" /> name.</summary>
    public static string Of(ILocalizationService? localization, string english)
    {
        if (localization is null || english.Length == 0) return english;
        var key = KeyOf(english);
        var text = localization[key];
        if (text.Length > 0 && text != key) return text;
        var (caption, unit) = DspControlText.SplitUnit(english);
        if (unit is null) return english;
        var translated = Of(localization, caption);
        return translated + " (" + unit + ")";
    }

    /// <summary>The resource key for an engine name: the prefix and the name's ASCII letters and digits.</summary>
    public static string KeyOf(string english) => Prefix + string.Concat(english.Where(char.IsAsciiLetterOrDigit));
}
