using DotCharter.Tone.UI;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
/// Locks in the view-model ↔ view naming convention that the whole docking /
/// content-template system relies on. Tests the pure type resolver so no
/// Avalonia app instance is needed (headless-safe, stays in the default gate).
/// </summary>
public class ViewLocatorTests
{
    [Fact]
    public void ResolveViewType_MapsViewModelNamespaceAndLeafToView()
    {
        var resolved = ViewLocator.ResolveViewType(typeof(SheetMusicPaneViewModel));

        Assert.Same(typeof(SheetMusicPaneView), resolved);
    }

    [Fact]
    public void ResolveViewType_ReturnsNull_WhenNoMatchingView()
    {
        // A VM-suffixed type with no mirrored View type resolves to null so
        // Build() can fall back to a visible placeholder instead of throwing.
        var resolved = ViewLocator.ResolveViewType(typeof(NoViewViewModel));

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveViewType_ReturnsNull_ForTypeWithoutViewModelSuffix()
    {
        var resolved = ViewLocator.ResolveViewType(typeof(string));

        Assert.Null(resolved);
    }

    [Fact]
    public void Match_OnlyMatchesViewModelBase()
    {
        var locator = new ViewLocator();

        Assert.False(locator.Match(new object()));
        Assert.False(locator.Match(null));
        Assert.True(locator.Match(new NoViewViewModel()));
    }

    // A ViewModelBase with deliberately no corresponding View type.
    private sealed class NoViewViewModel : ViewModelBase;
}
