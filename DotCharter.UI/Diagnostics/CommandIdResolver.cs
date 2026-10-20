using System.Reflection;
using System.Windows.Input;

namespace DotCharter.UI.Diagnostics;

/// <summary>
///     Derives a STABLE id for a clicked menu item or button, so usage counts
///     survive a rename of the visible label and a change of UI language.
///     <para>
///         <b>The command's own method name is the identity.</b> A
///         <c>[RelayCommand]</c> wraps the method it was generated from, so
///         <c>SaveProjectCommand</c> reports <c>SaveProject</c> — the same
///         convention <c>ChartKeybindHandler</c> already uses for shortcut labels,
///         which means the same verb reached by a key and by a menu lands in the
///         same bucket rather than looking like two half-used features.
///     </para>
///     <para>
///         Header text is the LAST resort, never the first: it is localized, so
///         counts keyed on it would split across languages and reset whenever a
///         label is reworded — exactly when you most want the history.
///     </para>
/// </summary>
public static class CommandIdResolver
{
    /// <summary>Ids we refuse rather than record, because they carry no meaning.</summary>
    private const int MaxIdLength = 96;

    /// <summary>
    ///     Resolves an id, or null when nothing stable can be derived (a menu item
    ///     that only opens a submenu, a button with no command and no name).
    /// </summary>
    /// <param name="command">The bound command, if any.</param>
    /// <param name="controlName">The control's <c>x:Name</c>, if any.</param>
    /// <param name="header">The visible label — the fallback of last resort.</param>
    public static string? Resolve(ICommand? command, string? controlName, object? header)
    {
        var fromCommand = FromCommand(command);
        if (fromCommand != null) return fromCommand;

        if (!string.IsNullOrWhiteSpace(controlName)) return Clean(controlName);

        // A plain string header only. A non-string header is a whole control
        // subtree (icon + text panels), whose ToString is a type name.
        return header is string text && !string.IsNullOrWhiteSpace(text) ? Clean("label:" + text) : null;
    }

    /// <summary>
    ///     The method a <c>[RelayCommand]</c> was generated from.
    ///     <para>
    ///         Read by reflecting the delegate the command holds. That is a private
    ///         detail of CommunityToolkit's <c>RelayCommand</c>, so it is treated as
    ///         best-effort: if the shape ever changes this returns null and the
    ///         caller falls back to the control name, rather than throwing on a
    ///         click.
    ///     </para>
    /// </summary>
    private static string? FromCommand(ICommand? command)
    {
        if (command == null) return null;

        try
        {
            foreach (var field in command.GetType()
                         .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                if (field.GetValue(command) is not Delegate handler) continue;

                var name = handler.Method.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // A lambda compiles to "<Ctor>b__12_3" — no more meaningful than
                // no answer at all, and it would churn whenever the file is edited.
                if (name.StartsWith('<')) continue;

                return Clean(name);
            }
        }
        catch (Exception)
        {
            // Reflection is a convenience here; a click must never fail for it.
        }

        return null;
    }

    private static string Clean(string value)
    {
        var trimmed = value.Trim().Replace('\n', ' ').Replace('\r', ' ');

        // Underscores are Avalonia's access-key marker, not part of the label.
        trimmed = trimmed.Replace("_", string.Empty);

        return trimmed.Length <= MaxIdLength ? trimmed : trimmed[..MaxIdLength];
    }
}
