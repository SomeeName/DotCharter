using Avalonia.Controls;
using DotCharter.Presentation.Fakes;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Gallery.Pages.Screens;

/// <summary>
///     One page per <see cref="ScreenCatalog" /> entry, one variant per entry in
///     its fake's <c>Variants</c> — DERIVED, never listed by hand
///     (target-architecture.md § 2). A screen registered in the catalog with a
///     fake in <c>DotCharter.Presentation.Fakes</c> is in the gallery, the
///     gallery smoke test and the goldens with no line here.
///     <para>
///         A dialog is shown the way <see cref="DialogHost" /> shows it: over the
///         ground and the shared scrim (<see cref="DialogHost.Preview" />).
///     </para>
/// </summary>
public static class ScreenPages
{
    public static IReadOnlyList<GalleryPage> All { get; } = Derive(ScreenCatalog.Default);

    public static IReadOnlyList<GalleryPage> Derive(ScreenCatalog catalog) =>
    [
        .. catalog.Entries.Select(entry => new GalleryPage(entry.Area, entry.Name,
        [
            .. FakeScreens.VariantsOf(entry.Contract)
                .Select(variant => new GalleryVariant(variant.Name, () => Build(entry, variant)))
        ]))
    ];

    /// <summary>The view on a fresh fake, framed as its host frames it. Shared with the golden harness.</summary>
    public static Control Build(ScreenEntry entry, FakeScreenVariant variant)
    {
        var view = entry.CreateView();
        view.DataContext = variant.Create();
        return entry.Kind == ScreenKind.Dialog ? DialogHost.Preview(view) : view;
    }
}
