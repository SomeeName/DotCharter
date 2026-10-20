using DotCharter.Services.Gameplay.Stats;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     Localizes a <see cref="RelativeTime" /> bucket ("4 days ago"). The
///     read-model keeps the mapping pure; the wording lives here in the UI.
///     The song library's rows, its detail rail and the status bar's profile
///     line all print the SAME string, so there is one formatter for the concept.
/// </summary>
internal static class RelativeTimeText
{
    public static string Format(RelativeTime age, ILocalizationService loc)
    {
        return age.Unit switch
        {
            RelativeTimeUnit.JustNow => loc["PlayMode_History_JustNow"],
            RelativeTimeUnit.Minutes => loc.Format("PlayMode_History_MinutesAgo", age.Value),
            RelativeTimeUnit.Hours => loc.Format("PlayMode_History_HoursAgo", age.Value),
            RelativeTimeUnit.Days => loc.Format("PlayMode_History_DaysAgo", age.Value),
            RelativeTimeUnit.Weeks => loc.Format("PlayMode_History_WeeksAgo", age.Value),
            RelativeTimeUnit.Months => loc.Format("PlayMode_History_MonthsAgo", age.Value),
            RelativeTimeUnit.Years => loc.Format("PlayMode_History_YearsAgo", age.Value),
            _ => ""
        };
    }
}
