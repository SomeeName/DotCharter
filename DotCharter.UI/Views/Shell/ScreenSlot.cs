using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     One screen drawn INSIDE another screen's view, as a section of its page:
///     Settings ▸ AI &amp; models shows the stem-separation and lyric-model tabs
///     this way. The view comes from the enclosing <see cref="ScreenHost" />'s
///     catalogue and its view model from the host's scope, so it is the same
///     instance the settings shell loads and saves. Outside a host (the gallery,
///     a bare test) the slot stays empty.
///     <para>
///         A screen whose view is a page of its own wraps its body in a
///         <see cref="ScrollViewer" />. As a section it scrolls with the page that
///         holds it, so the slot lifts the body out of that scroller (padding and
///         margin with it) rather than nesting one scroller in another. It also
///         drops the style groups the page already includes: a rule included at
///         two levels is attached to every control below both, twice.
///     </para>
/// </summary>
public sealed class ScreenSlot : Decorator
{
    public static readonly StyledProperty<Type?> ScreenProperty =
        AvaloniaProperty.Register<ScreenSlot, Type?>(nameof(Screen));

    public Type? Screen
    {
        get => GetValue(ScreenProperty);
        set => SetValue(ScreenProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Child is not null || Screen is not { } contract) return;
        if (this.FindAncestorOfType<ScreenHost>() is not { } host) return;

        var entry = host.Catalog.Find(contract)
                    ?? throw new InvalidOperationException($"{contract.Name} is not registered in the screen catalog.");
        var view = entry.CreateView();
        view.DataContext = host.Services.GetRequiredService(contract);
        if (view is UserControl { Content: ScrollViewer { Content: Control body } scroller } page)
        {
            scroller.Content = null;
            body.Margin = default;
            page.Content = body;
        }

        DropInheritedStyles(view);
        Child = view;
    }

    private void DropInheritedStyles(Control view)
    {
        var inherited = this.GetVisualAncestors().OfType<StyledElement>()
            .SelectMany(a => a.Styles).Select(Fingerprint).Where(f => f.Length > 0).ToHashSet(StringComparer.Ordinal);
        for (var i = view.Styles.Count - 1; i >= 0; i--)
        {
            if (inherited.Contains(Fingerprint(view.Styles[i])))
                view.Styles.RemoveAt(i);
        }
    }

    /// <summary>A compiled <c>StyleInclude</c> is the included file's own <see cref="Styles" />
    /// instance, one per includer, so a group is known by its selectors and themes.</summary>
    private static string Fingerprint(IStyle style) => style is Styles group
        ? string.Join('|', group.OfType<Style>().Select(s => s.Selector?.ToString()).Take(8)) + '#' + string.Join('|', group.Resources.Keys.Take(8))
        : string.Empty;
}
