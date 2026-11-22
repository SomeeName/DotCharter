// ChartRenderer: the timeline context-menu presenter (IChartViewport.ShowContextMenu) —
// realizes the pure TimelineContextMenuBuilder items into a MenuFlyout at the pointer.

using Avalonia.Controls;
using DotCharter.UI.Input;

namespace DotCharter.UI.Controls;

public partial class ChartRenderer
{
    /// <summary>
    ///     Opens the timeline context menu at the pointer. The MODEL is built by
    ///     the pure <see cref="TimelineContextMenuBuilder" /> (unit-tested,
    ///     including that every LocKey exists in all three resx files); this method
    ///     only realizes it. The in-window popup overlays the composed 3D panel.
    ///     Rebuilt per open on purpose — the item set
    ///     depends on what was clicked, and a dozen <c>MenuItem</c>s are far
    ///     inside the interaction budget.
    /// </summary>
    public void ShowContextMenu(IReadOnlyList<TimelineMenuItem> items)
    {
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.Pointer
        };
        foreach (var item in items)
            flyout.Items.Add(Realize(item));
        flyout.ShowAt(this, showAtPointer: true);
    }

    internal static Control Realize(TimelineMenuItem item) =>
        Realize(item, DotCharter.Localization.LocalizationService.Instance);

    private static Control Realize(TimelineMenuItem item, ILocalizationService localization)
    {
        if (item.IsSeparator) return new Separator();

        var menuItem = new MenuItem
        {
            Header = item.LiteralHeader ??
                     localization[item.LocKey!],
            IsEnabled = item.IsEnabled
        };
        var reason = item.DisabledReason ?? (item.DisabledReasonLocKey is { } key
            ? localization[key] : null);
        if (reason != null)
        {
            ToolTip.SetTip(menuItem, reason);
            ToolTip.SetShowOnDisabled(menuItem, true);
            Avalonia.Automation.AutomationProperties.SetHelpText(menuItem, reason);
        }
        if (item.Chord is { } chord)
            menuItem.InputGesture = new Avalonia.Input.KeyGesture(chord.Key, chord.Modifiers);
        if (item.Children != null)
        {
            foreach (var child in item.Children)
                menuItem.Items.Add(Realize(child, localization));
        }
        else if (item.Invoke is { } invoke)
        {
            menuItem.Click += (_, _) => invoke();
        }

        return menuItem;
    }
}
