using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor;

/// <summary>
///     The AI chat pane. Two view concerns past the constructor: the thread follows the newest
///     message — when its content grows while it was scrolled to the end (a reply arrived, a
///     message was sent), it stays at the end; a reader scrolled up is left where they are — and
///     the history dropdown opens with the keyboard in its search box, so typing narrows it.
/// </summary>
public partial class AiChatPaneView : UserControl
{
    private const double EndSlackPx = 4;

    public AiChatPaneView()
    {
        InitializeComponent();
        AiChatThread.ScrollChanged += OnThreadScrollChanged;
        AiChatHistoryPopup.Opened += (_, _) => AiChatHistorySearch.Focus();
    }

    private void OnThreadScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y <= 0) return;

        var viewport = AiChatThread.Viewport.Height;
        var previousExtent = AiChatThread.Extent.Height - e.ExtentDelta.Y;
        var wasAtEnd = previousExtent <= viewport + EndSlackPx
                       || AiChatThread.Offset.Y + viewport >= previousExtent - EndSlackPx;
        if (wasAtEnd) AiChatThread.ScrollToEnd();
    }
}
