using System.Globalization;

namespace DotCharter.UI.Services;

/// <summary>
///     The interface-scale row's fixed choice list, and the pure map between a
///     selected INDEX and the (mode, factor) pair it means.
///
///     <para>
///         The row is an index rather than a factor because the first entry is
///         not a factor at all: <c>Auto</c> derives one from the window
///         (<see cref="AutoUiScale" />). Keeping the map here — rather than as
///         two <c>switch</c>es in the settings view model and a third in Play
///         Mode's gameplay modal — is what stops the two doors onto this
///         setting from disagreeing about what "125 %" is.
///     </para>
/// </summary>
public static class InterfaceScaleChoices
{
    /// <summary>The index of the <c>Auto</c> entry. Always first: it is the
    /// default, and a list whose default is in the middle reads as an
    /// afterthought.</summary>
    public const int AutoIndex = 0;

    /// <summary>The fixed factors offered after <c>Auto</c>, in order. Every
    /// one is inside <c>UiScaleController</c>'s manual range.</summary>
    public static IReadOnlyList<double> Factors { get; } = [1.00, 1.25, 1.50, 1.75, 2.00];

    /// <summary>
    ///     The display labels, <c>Auto</c> first. The percentages are formatted
    ///     through <see cref="CultureInfo.CurrentCulture" /> rather than written
    ///     into the resx as five literal strings: a percent sign's placement and
    ///     spacing are a culture's business, and five more keys in three files
    ///     would be five more things to keep in sync for no translatable words.
    /// </summary>
    public static IReadOnlyList<string> Labels(ILocalizationService localization)
    {
        var labels = new List<string>(Factors.Count + 1)
        {
            localization["Settings_UiScale_Auto"]
        };
        labels.AddRange(Factors.Select(f => f.ToString("P0", CultureInfo.CurrentCulture)));
        return labels;
    }

    /// <summary>True when <paramref name="index" /> selects the Auto arm. Any
    /// out-of-range index reads as Auto, because Auto is the default and a
    /// corrupt config must not land the user on an arbitrary fixed
    /// factor.</summary>
    public static bool IsAuto(int index) => index < 0 || index > Factors.Count || index == AutoIndex;

    /// <summary>The manual factor a non-Auto index selects. Answers
    /// <c>null</c> for Auto (and for anything out of range), so a caller cannot
    /// silently get 1.0 where it meant "derive one".</summary>
    public static double? FactorFor(int index) =>
        IsAuto(index) ? null : Factors[index - 1];

    /// <summary>
    ///     The index to show for a persisted (auto, factor) pair — the inverse
    ///     of <see cref="FactorFor" />, used by both load paths.
    ///     <para>
    ///         A manual factor that is not one of the five (0.75, or anything
    ///         an older build's slider produced — that slider shipped with
    ///         <c>TickFrequency="0.05"</c>) snaps to the NEAREST offered one
    ///         rather than falling back to Auto: silently turning a user's
    ///         deliberate 90 % into Auto would look like the setting being
    ///         ignored.
    ///     </para>
    /// </summary>
    public static int IndexFor(bool auto, double factor)
    {
        if (auto || !double.IsFinite(factor)) return AutoIndex;

        var best = 0;
        for (var i = 1; i < Factors.Count; i++)
        {
            if (Math.Abs(Factors[i] - factor) < Math.Abs(Factors[best] - factor))
                best = i;
        }

        return best + 1;
    }
}
