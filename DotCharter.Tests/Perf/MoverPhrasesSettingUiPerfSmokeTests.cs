using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Settings;
using DotCharter.UI.Views.Settings;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Files tab's "RS XML phrases between beats as moveR" row: built, attached, laid
///     out, measured non-zero, and wired both ways to the screen's property. With
///     DOTCHARTER_UI_SWEEP_DUMP=1 it also writes the tab as <c>files-settings-mover</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class MoverPhrasesSettingUiPerfSmokeTests
{
    private const double TabWidth = 960;
    private const double TabHeight = 1400;

    [Fact]
    public async Task FilesTab_Capture()
    {
        await HeadlessSessionFixture.Dispatch(() =>
            WithLaidOutTab(FakeFilesSettingsScreen.Default(), tab =>
            {
                var window = (Window)TopLevel.GetTopLevel(tab)!;
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "files-settings-mover");
            }), CancellationToken.None);
    }

    [Fact]
    public async Task MoverRow_LaysOut_AndBindsBothWays()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeFilesSettingsScreen.Default();
            WithLaidOutTab(screen, tab =>
            {
                var toggle = tab.GetVisualDescendants().OfType<ToggleSwitch>()
                    .SingleOrDefault(t => t.Name == "RsXmlMoverPhrasesSwitch");
                Assert.NotNull(toggle);
                Assert.True(toggle.IsEffectivelyVisible, "the moveR row is hidden");
                Assert.True(toggle.Bounds.Width > 1 && toggle.Bounds.Height > 1,
                    $"the moveR switch laid out as {toggle.Bounds} — it measured nothing");

                Assert.False(toggle.IsChecked);
                screen.RsXmlMoverPhrases = true;
                Assert.True(toggle.IsChecked);
                toggle.IsChecked = false;
                Assert.False(screen.RsXmlMoverPhrases);
            });
        }, CancellationToken.None);
    }

    private static void WithLaidOutTab(FakeFilesSettingsScreen screen, Action<Control> assert)
    {
        var tab = new FilesSettingsView { DataContext = screen };
        var window = new Window { Width = TabWidth, Height = TabHeight, Content = tab };
        window.Show();
        try
        {
            tab.Measure(new Size(TabWidth, TabHeight));
            tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
            assert(tab);
        }
        finally
        {
            window.Close();
        }
    }
}
