using Avalonia.Controls;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     The right-click menu on a tone card in the Tones mode (also the menu key and
///     Shift+F10): Load, Rename / edit details…, Duplicate, Overwrite with current
///     rig, Export to file…, Delete…. Built per open over the browser's per-card
///     commands with the card as the parameter — the same shape as
///     <see cref="RigUnitContextMenu" /> — so an item can never act on a card other
///     than the one it was opened on, and each item's enabled state IS its
///     command's <c>CanExecute</c>.
///     <para>
///         On a built-in tone Rename, Overwrite and Delete stay in the menu,
///         DISABLED, with the reason as their tooltip (a capability the app will
///         not honour must look disabled and say why, never vanish or do
///         nothing).
///     </para>
/// </summary>
public static class RigToneCardContextMenu
{
    /// <summary>Builds the menu for <paramref name="card" />.</summary>
    public static ContextMenu Build(PresetBrowserViewModel browser, PresetCardViewModel card) =>
        Build(browser, card, browser.Localization);

    private static ContextMenu Build(PresetBrowserViewModel browser, PresetCardViewModel card, ILocalizationService loc)
    {
        var locked = browser.LockedReason(card);
        var items = new List<Control>
        {
            Item("RigToneCardLoad", loc["PresetBrowser_Ctx_Load"], browser.ApplyCardCommand, card),
            new Separator(),
            Item("RigToneCardEdit", loc["PresetBrowser_Ctx_Edit"], browser.BeginEditCardCommand, card, locked),
            Item("RigToneCardDuplicate", loc["PresetBrowser_Ctx_Duplicate"], browser.DuplicateCardCommand, card),
            Item("RigToneCardOverwrite", loc["PresetBrowser_Ctx_Overwrite"], browser.OverwriteCardCommand, card, locked),
            Item("RigToneCardExport", loc["PresetBrowser_Ctx_Export"], browser.ExportCardCommand, card),
            new Separator(),
            Item("RigToneCardDelete", loc["PresetBrowser_Ctx_Delete"], browser.RequestDeleteCardCommand, card, locked)
        };
        return new ContextMenu { ItemsSource = items, Tag = card };
    }

    private static MenuItem Item(string name, string header, System.Windows.Input.ICommand command,
        PresetCardViewModel card, string? disabledReason = null)
    {
        var item = new MenuItem { Name = name, Header = header, Command = command, CommandParameter = card };
        Avalonia.Automation.AutomationProperties.SetName(item, header);
        if (disabledReason is not null)
        {
            ToolTip.SetTip(item, disabledReason);
            ToolTip.SetShowOnDisabled(item, true);
            // Beside the item, not over it: the default placement opened the
            // reason on top of the menu and hid the items above the one hovered.
            ToolTip.SetPlacement(item, PlacementMode.Right);
        }

        return item;
    }
}
