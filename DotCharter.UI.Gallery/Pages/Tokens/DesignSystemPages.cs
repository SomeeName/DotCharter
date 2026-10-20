namespace DotCharter.UI.Gallery.Pages.Tokens;

/// <summary>The "Design system" area: tokens, type, spacing, control classes.</summary>
public static class DesignSystemPages
{
    public static IReadOnlyList<GalleryPage> All { get; } =
    [
        new GalleryPage("Design system", "Design tokens",
        [
            new GalleryVariant("All", () => new DesignTokensPage())
        ])
    ];
}
