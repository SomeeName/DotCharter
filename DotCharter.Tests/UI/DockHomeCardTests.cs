namespace DotCharter.Tests.UI;

/// <summary>
///     A floated pane leaves a card in the spot it came from — "The inspector is in its own window"
///     and a "Dock it back here" button (DockLayoutController.DockPaneHome). Before, the spot was an
///     empty dark area and dragging the window back onto it was the only way home, which the owner
///     could not find (2026-09-24). Every registered pane is wired to its card, both ways.
/// </summary>
public sealed class DockHomeCardTests
{
    [Theory]
    [InlineData("Inspector", "IsInspectorFloating", "DockInspectorHomeButton", "_dockController")]
    [InlineData("SheetMusic", "IsSheetMusicFloating", "DockSheetMusicHomeButton", "_topDockController")]
    public void EveryDockPane_ReportsFloating_AndItsCardDocksItHome(string id, string flag, string button, string controller)
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var docking = UiSourceTree.ReadAllText(Path.Combine(views, "MainWindow.Docking.cs"));
        var window = MainWindowSource.ReadAllText();

        Assert.Contains($"SetFloating = f => vm.{flag} = f", docking);
        Assert.Contains($"{button}.Click += (_, _) => {controller}?.DockPaneHome(\"{id}\");", docking);
        Assert.Contains($"IsVisible=\"{{Binding {flag}}}\"", window);
        Assert.Contains($"x:Name=\"{button}\"", window);
    }
}
