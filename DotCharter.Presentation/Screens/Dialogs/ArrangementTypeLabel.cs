namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>An arrangement's type beside its name, or nothing when the name already says it ("Lead Lead").</summary>
public static class ArrangementTypeLabel
{
    public static string For(string name, string type) =>
        string.Equals(name.Trim(), type, StringComparison.OrdinalIgnoreCase) ? "" : type;
}
