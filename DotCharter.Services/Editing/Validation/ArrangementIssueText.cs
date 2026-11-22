using System.Globalization;
using System.Resources;
using DotCharter.Localization;

namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     The two renderings of a finding's sentence. <see cref="English" /> is what the rule
///     stores in <see cref="ArrangementIssue.Message" /> (logs, reports, AI prompts), read from
///     the SAME neutral resource the UI language is read from, so the wording lives once.
///     <see cref="Of" /> is what a surface shows the user.
/// </summary>
public static class ArrangementIssueText
{
    private static readonly ResourceManager Resources = new(
        "DotCharter.Localization.Resources",
        typeof(LocalizationService).Assembly);

    /// <summary>The sentence in the UI language of <paramref name="localization" />; a finding
    /// built without a key (a tool-made one) shows its stored <see cref="ArrangementIssue.Message" />.</summary>
    public static string Of(ArrangementIssue issue, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(localization);
        return issue.MessageKey is null
            ? issue.Message
            : localization.Format(issue.MessageKey, Resolve(issue.MessageArgs, localization.Format));
    }

    /// <summary>The English (neutral resource) sentence for <paramref name="key" />.</summary>
    public static string English(string key, IReadOnlyList<object>? args = null) =>
        string.Format(CultureInfo.InvariantCulture, Neutral(key), Resolve(args, EnglishFormat));

    private static string EnglishFormat(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Neutral(key), args);

    private static string Neutral(string key) => Resources.GetString(key, CultureInfo.InvariantCulture) ?? key;

    private static object[] Resolve(IReadOnlyList<object>? args, Func<string, object[], string> format)
    {
        if (args is null || args.Count == 0) return [];
        var resolved = new object[args.Count];
        for (var i = 0; i < resolved.Length; i++)
            resolved[i] = args[i] is IssueTextRef nested ? format(nested.Key, Resolve(nested.Args, format)) : args[i];
        return resolved;
    }
}
