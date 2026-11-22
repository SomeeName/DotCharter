namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>A field prefilled with a derived value (a sort name, the DLC key) follows its source while left alone.</summary>
internal static class SongNamesFollow
{
    /// <summary>The text after the source changed: the new derived value, if the text still equals the last one.</summary>
    public static string Next(string current, ref string lastDerived, string derived)
    {
        var follows = string.Equals(current, lastDerived, StringComparison.Ordinal);
        lastDerived = derived;
        return follows ? derived : current;
    }

    public static string Seed(string? stored, string derived) =>
        string.IsNullOrWhiteSpace(stored) ? derived : stored.Trim();
}
