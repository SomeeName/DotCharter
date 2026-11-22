using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DotCharter.UI.Views.Editor;

/// <summary>
///     The AI chat pane. Two view concerns past the constructor: the thread follows the newest
///     message — when its content grows while it was scrolled to the end (a reply arrived, a
///     message was sent), it stays at the end; a reader scrolled up is left where they are — and
///     the history dropdown opens with the keyboard in its search box, so typing narrows it, and
///     Esc anywhere in it closes it and gives the keyboard back to the title that opened it.
/// </summary>
public partial class AiChatPaneView : UserControl
{
    private const double EndSlackPx = 4;

    public AiChatPaneView()
    {
        InitializeComponent();
        AiChatThread.ScrollChanged += OnThreadScrollChanged;
        AiChatHistoryPopup.Opened += (_, _) => AiChatHistorySearch.Focus();
        // The dropdown lives in the overlay layer, so its keys never reach this view: listen on its panel.
        AiChatHistoryPanel.AddHandler(KeyDownEvent, OnHistoryKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !AiChatHistoryPopup.IsOpen) return;
        AiChatHistoryPopup.IsOpen = false;
        AiChatHistoryToggle.Focus(NavigationMethod.Directional);
        e.Handled = true;
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
