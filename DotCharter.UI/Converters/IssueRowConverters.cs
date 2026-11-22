using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>The Issues tab's card captions (inspector design D). The finding rows bind their own row model (<c>IssueListRow</c>).</summary>
public static class IssueRowConverters
{
    /// <summary>
    ///     A card's page caption with what its old rail entry said: <c>(title, count)</c> appends a count above zero
    ///     ("Findings 12"), <c>(title, true)</c> a dot ("Project •"), so a paged-away card still says it needs a look.
    /// </summary>
    public static readonly FuncMultiValueConverter<object?, string?> PageTitle = new(values =>
    {
        var parts = values.Take(3).ToArray();
        if (parts.Length != 2 || parts[0] is not string title) return null;
        return parts[1] switch
        {
            int count and > 0 => $"{title} {count}",
            true => $"{title} •",
            _ => title
        };
    });
}
