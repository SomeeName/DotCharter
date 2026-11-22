namespace DotCharter.Presentation.Shared;

/// <summary>The way from a dialog to the Inspector's Issues tab: the window sets Open, a dialog closes itself and calls TryOpen.</summary>
public sealed class ChartIssuesDoor
{
    public Action? Open { get; set; }

    public bool CanOpen => Open != null;

    public bool TryOpen()
    {
        if (Open is not { } open) return false;
        open();
        return true;
    }
}
