using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     View ▸ Inspector tab (#512) against the app's own shell view model: a row opens the closed
///     Inspector on its tab, only the open Inspector's active tab reads as ticked, and the Riff library
///     keybind still shows the tab in one press.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ViewMenuInspectorTabUiPerfSmokeTests
{
    [Theory]
    [InlineData(InspectorViewModel.SoundTabIndex)]
    [InlineData(InspectorViewModel.MixerTabIndex)]
    [InlineData(InspectorViewModel.SongTabIndex)]
    [InlineData(InspectorViewModel.RiffLibraryTabIndex)]
    [InlineData(InspectorViewModel.ChordShapesTabIndex)]
    public Task ChoosingATabRow_WithTheInspectorClosed_OpensItOnThatTab(int tab) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            shell.IsInspectorVisible = false;
            Assert.Equal(-1, shell.TabMenu.ActiveIndex);

            shell.TabMenu.ShowRowCommand.Execute(tab.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.True(shell.IsInspectorVisible);
            Assert.Equal(tab, shell.Inspector.SelectedTabIndex);
            Assert.Equal(tab, shell.TabMenu.ActiveIndex);

            shell.IsInspectorVisible = false;
            Assert.Equal(-1, shell.TabMenu.ActiveIndex);
        }, CancellationToken.None);

    [Fact]
    public Task TheRiffLibraryKeybind_StillAsksForTheTab_InOnePress() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            shell.IsInspectorVisible = false;
            Assert.False(shell.IsPhraseCatalogVisible);

            shell.TogglePhraseCatalogCommand.Execute(null);

            Assert.True(shell.IsPhraseCatalogVisible);
        }, CancellationToken.None);

    private static MainWindowViewModel Shell() =>
        (App.Services ?? throw new InvalidOperationException("the headless app session did not boot"))
        .GetRequiredService<MainWindowViewModel>();
}
