using Avalonia.Controls;
using DotCharter.UI.Gallery.Pages.Screens;
using DotCharter.UI.Gallery.Pages.Tokens;

namespace DotCharter.UI.Gallery.Pages;

/// <summary>
///     One named state of a page. <see cref="Create" /> builds a FRESH control
///     every time it is called — a variant is never a cached instance, because
///     the same control cannot be attached to two windows (the capture run and
///     the window both build their own).
/// </summary>
/// <param name="Name">Shown on the variant strip and used in the PNG file name.</param>
/// <param name="Create">Builds the control, fake view model and all.</param>
public sealed record GalleryVariant(string Name, Func<Control> Create);

/// <summary>
///     One screen in the gallery, in one section of the navigation list.
/// </summary>
/// <param name="Section">Navigation group, e.g. "Design system" or "Play Mode".</param>
/// <param name="Name">Page name, unique within its section.</param>
/// <param name="Variants">At least one. The first is shown when the page is selected.</param>
public sealed record GalleryPage(string Section, string Name, IReadOnlyList<GalleryVariant> Variants);

/// <summary>
///     Every page the gallery hosts: the hand-written design-system pages, then
///     one page per screen DERIVED from <c>ScreenCatalog</c> × each fake's
///     <c>Variants</c> (<see cref="ScreenPages" />).
///     <para>
///         <b>A new screen needs no line here.</b> Register it in
///         <c>DotCharter.UI/Views/Shell/ScreenCatalog.cs</c> and give its
///         contract a fake with <c>Variants</c>; the page follows. A page that
///         is NOT a catalog screen (a token sheet, a control study) is one
///         entry in its area's own list, <c>Pages/&lt;Area&gt;/&lt;Area&gt;Pages.cs</c>,
///         spread in below.
///     </para>
///     <para>
///         <c>Perf/GalleryPagesUiPerfSmokeTests</c> enumerates this list, so a
///         new page gets its construction + attach + layout smoke test and its
///         capture PNG for free — there is no second place to register it.
///     </para>
/// </summary>
public static class GalleryCatalog
{
    public static IReadOnlyList<GalleryPage> All { get; } =
    [
        ..DesignSystemPages.All,
        ..ScreenPages.All
    ];
}
