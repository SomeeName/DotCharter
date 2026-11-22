using Avalonia;
using Avalonia.Controls;
using DotCharter.Services.Diagnostics;

namespace DotCharter.UI.Diagnostics;

/// <summary>
///     Counts every menu item and button click in the app, from ONE registration.
///     <para>
///         <b>Why a class handler rather than per-control wiring.</b> The menu
///         alone binds 95 commands directly to view-model
///         <c>[RelayCommand]</c>s — it does not go through the command palette —
///         so the palette hook was counting a small minority of what a charter
///         actually does, and the tools reached from the menu (FHP generation,
///         refret, auto-fix) read as completely unused. Instrumenting 95 call
///         sites would have been wrong twice over: it would drift the moment
///         someone adds the 96th, and it would put telemetry into markup.
///         <c>RoutedEvent.AddClassHandler</c> observes the whole class of
///         controls at once, so coverage is complete by construction and a new
///         menu item is counted the day it is added, with nobody remembering
///         anything.
///     </para>
///     <para>
///         <b>It only ever OBSERVES.</b> The handler never marks the event
///         handled, never runs before the control's own logic, and swallows its
///         own failures — a counter must not be able to stop a click from
///         reaching the chart.
///     </para>
/// </summary>
public static class UsageInstrumentation
{
    private static bool _installed;

    /// <summary>
    ///     Installs the global click hook. Idempotent: class handlers are
    ///     process-wide and registering twice would double every count.
    /// </summary>
    public static void Install(IUsageTracker? tracker)
    {
        if (tracker == null || _installed) return;
        _installed = true;

        MenuItem.ClickEvent.AddClassHandler<MenuItem>((item, _) =>
            Record(tracker, item.Command, OwnerName(item), item.Header));

        Button.ClickEvent.AddClassHandler<Button>((button, _) =>
            Record(tracker, button.Command, OwnerName(button), button.Content));
    }

    /// <summary>
    ///     The control's own name, or for a template part (a spinner's
    ///     <c>PART_IncreaseButton</c>) the name of the control whose template it
    ///     lives in; null when that owner has no name, so the click is skipped
    ///     rather than counted under a name every spinner shares.
    /// </summary>
    internal static string? OwnerName(StyledElement element)
    {
        if (!CommandIdResolver.IsTemplatePart(element.Name)) return element.Name;

        for (var owner = element.TemplatedParent as StyledElement; owner != null; owner = owner.TemplatedParent as StyledElement)
        {
            if (!string.IsNullOrWhiteSpace(owner.Name) && !CommandIdResolver.IsTemplatePart(owner.Name)) return owner.Name;
        }

        return element.Name;
    }

    private static void Record(IUsageTracker tracker, System.Windows.Input.ICommand? command,
        string? name, object? header)
    {
        try
        {
            // A submenu parent has no command and no useful name; skipping it is
            // the difference between counting VERBS and counting mouse travel.
            var id = CommandIdResolver.Resolve(command, name, header);
            if (id != null) tracker.Record(UsageKind.Command, id);
        }
        catch (Exception)
        {
            // Never let instrumentation break an edit.
        }
    }
}
