using System.Text.RegularExpressions;
using DotCharter.Localization;

namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The driver's own channel names on the output-pair and input-jack rows:
///     "Out 3 + 4 · Headphones", "Input 2 · Hi-Z".
///     <para>
///         The names come from the OPEN device (<c>IAudioDeviceControl.GetOutputChannelNames</c> /
///         <c>GetInputChannelNames</c>), entry i naming channel i. A name that says
///         nothing the number does not ("Output channel 3", JUCE's WASAPI name for
///         every endpoint; "Out 3", "Input 2", "Ch 5") is dropped, and so is an empty
///         one: the row then reads as the bare number.
///     </para>
/// </summary>
public static partial class ChannelNames
{
    /// <summary>The driver's name for <paramref name="channel" /> when it adds
    /// something to the number; null when there is no list, the channel is past
    /// it, or the name is empty or generic.</summary>
    public static string? Meaningful(IReadOnlyList<string>? names, int channel)
    {
        if (names == null || channel < 0 || channel >= names.Count) return null;
        var name = names[channel]?.Trim();
        return string.IsNullOrEmpty(name) || IsGeneric(name) ? null : name;
    }

    /// <summary>True for a name made of nothing but a channel word and a number:
    /// "Output channel 3", "Out 3", "Input 2", "Ch. 5", "7".</summary>
    public static bool IsGeneric(string name) => GenericName().IsMatch(name);

    /// <summary>
    ///     One name for an output pair. Two names that differ only in a trailing
    ///     side ("Headphones L" / "Headphones R") read as their common part,
    ///     "Headphones"; two that share a stem and differ in a short tail
    ///     ("Analog 3" / "Analog 4") read as "Analog 3/4"; anything else is both,
    ///     "Main L / Phones R". Null when either side has no meaningful name.
    /// </summary>
    public static string? PairName(IReadOnlyList<string>? names, int left, int right)
    {
        var l = Meaningful(names, left);
        if (left == right) return l;
        var r = Meaningful(names, right);
        if (l == null || r == null) return null;
        if (string.Equals(l, r, StringComparison.OrdinalIgnoreCase)) return l;

        var common = CommonPrefixLength(l, r);
        // Back up to the last separator, so "Analog 13" / "Analog 14" splits after "Analog ", not after "Analog 1".
        while (common > 0 && !IsSeparator(l[common - 1])) common--;
        var stem = l[..common].TrimEnd(Separators);
        if (stem.Length == 0) return $"{l} / {r}";

        var lTail = l[common..].Trim();
        var rTail = r[common..].Trim();
        if (SideMarker().IsMatch(lTail) && SideMarker().IsMatch(rTail)) return stem;
        return lTail.Length <= ShortTail && rTail.Length <= ShortTail ? $"{stem} {lTail}/{rTail}" : $"{l} / {r}";
    }

    /// <summary>"Out 3 + 4" with a name becomes "Out 3 + 4 · Headphones"; a null name keeps the number alone.</summary>
    public static string WithName(ILocalizationService localization, string numberLabel, string? name) =>
        name == null ? numberLabel : localization.Format("ChannelLabel_NamedFmt", numberLabel, name);

    private const int ShortTail = 6;

    private static readonly char[] Separators = [' ', '-', '_', '/', '.', ':', '(', ')'];

    private static bool IsSeparator(char c) => Array.IndexOf(Separators, c) >= 0;

    private static int CommonPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[i])) i++;
        return i;
    }

    [GeneratedRegex(@"^\s*(?:(?:output|input|out|in|channel|chan|ch)\.?\s*)*#?\s*\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericName();

    [GeneratedRegex(@"^\(?(?:l|r|left|right)\)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SideMarker();
}
