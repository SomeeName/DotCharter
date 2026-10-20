using System.Globalization;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     How the inspector NAMES a string, wherever it names one: the Placement
///     card's String picker, the chord-bend string picker, the keyboard readout
/// of the string-edit mask and a folded issue row's "strings …".
///     <para>
///         It speaks the way the 2D highway's lane gutter does
///         (<c>TimelineGutterControl.DrawStringRows</c>): the string's NAME from
///         the arrangement's tuning, and the guitarist's string NUMBER, which
///         counts from the HIGH string (on six strings the low E is string 6).
///         Before this, the String box printed the model's 0-based index
///         (<c>String 0</c> for the low E), the mask readout printed it 1-based
///         from the LOW string, and the fretboard right above both labelled the
///         same strings E A D G B e — three conventions on one tab.
///     </para>
///     <para>
///         The name alone is not enough: in drop D the low string and the
///         fourth are both "D". The number alone is not either, because the
///         keyboard's <c>Ctrl+1..9</c> counts the other way; the name is what
///         both sides agree on.
///     </para>
/// </summary>
public static class GuitarStringLabel
{
    /// <summary>What a string with no usable tuning name is called — never an
    /// invented "E", the same rule as the gutter's.</summary>
    public const string UnknownName = "?";

    /// <summary>
    ///     One string: <paramref name="format" /> (the resx
    ///     <c>Inspector_StringLabelFmt</c>, "{0} ({1})") filled with the tuning
    ///     name and the gutter's number.
    /// </summary>
    /// <param name="stringIndex">The MODEL index, 0 = the lowest string.</param>
    /// <param name="stringCount">How many strings the arrangement has; the
    /// tuning's length wins when it is known.</param>
    /// <param name="tuning">The arrangement's tuning names (<c>Arrangement.Tuning</c>).</param>
    /// <param name="format">The resx format, "{0} ({1})".</param>
    public static string Format(int stringIndex, int stringCount, IReadOnlyList<string>? tuning, string format)
    {
        var count = tuning is { Count: > 0 } ? tuning.Count : Math.Max(stringCount, stringIndex + 1);
        var name = tuning != null && stringIndex >= 0 && stringIndex < tuning.Count &&
                   !string.IsNullOrWhiteSpace(tuning[stringIndex])
            ? tuning[stringIndex].Trim()
            : UnknownName;
        return string.Format(CultureInfo.InvariantCulture, format, name, Number(stringIndex, count));
    }

    /// <summary>The guitarist's number for a model index: the highest string
    /// is 1, the lowest is <paramref name="stringCount" />.</summary>
    public static int Number(int stringIndex, int stringCount) => stringCount - stringIndex;

    /// <summary>Several strings, lowest first, comma separated.</summary>
    public static string List(IEnumerable<int> stringIndices, int stringCount, IReadOnlyList<string>? tuning,
        string format) =>
        string.Join(", ", stringIndices.Distinct().Order().Select(i => Format(i, stringCount, tuning, format)));

    /// <summary>One label per string, in model order — the String picker's
    /// items, so the picker's <c>SelectedIndex</c> IS the model index.</summary>
    public static IReadOnlyList<string> All(int stringCount, IReadOnlyList<string>? tuning, string format)
    {
        var count = tuning is { Count: > 0 } ? tuning.Count : stringCount;
        var labels = new string[Math.Max(0, count)];
        for (var i = 0; i < labels.Length; i++) labels[i] = Format(i, count, tuning, format);
        return labels;
    }

    /// <summary>
    ///     The keyboard readout of the string-edit mask on the Sound tab's queue
    ///     strip: "all strings" for the empty mask (which edits every string),
    ///     otherwise "strings A (5), D (4)". Every word comes from the resx —
    ///     the readout never prints <c>StringEditMask.ToString()</c>'s hard-coded
    ///     English "all" in another language.
    /// </summary>
    public static string MaskReadout(StringEditMask mask, int stringCount, IReadOnlyList<string>? tuning,
        ILocalizationService localization)
    {
        if (mask.IsEmpty) return localization["Inspector_Queue_MaskAll"];
        var indices = Enumerable.Range(0, StringEditMask.MaxStrings).Where(mask.IsExplicit);
        return localization.Format("Inspector_Queue_MaskFmt",
            List(indices, stringCount, tuning, localization["Inspector_StringLabelFmt"]));
    }
}
