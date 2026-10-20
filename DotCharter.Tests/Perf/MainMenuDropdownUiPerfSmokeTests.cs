using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.Views.Shell.Menus;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Every top-level menu of the editor's bar OPENS, through the real
///     <see cref="MainMenuView" /> on the real shell view model: after the
///     product's own prewarm (<see cref="MenuFlyoutPrewarmer" />) the dropdown's
///     popup is open, its panel measured non-zero, and every visible row in it
///     laid out with a size.
///     <para>
///         <c>ShellConstructionUiPerfSmokeTests</c> realizes the rows WITHOUT
///         opening anything; this is the other half — the panel the user sees on
///         a click. Since #653 each menu is its own file
///         (<c>Views/Shell/Menus/*Menu.axaml</c>, a <c>MenuItem</c> subclass), and a
///         subclass whose style key did not resolve to <c>MenuItem</c> would get no
///         template and open onto nothing, which no source lint can see.
///     </para>
///     <para>
///         The capture half is inert unless <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (Skia
///         drawing) and <see cref="CaptureDirEnvVar" /> name a folder: then each
///         open menu is written as <c>menu-&lt;key&gt;.png</c>, which is how the
///         split was compared before/after.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class MainMenuDropdownUiPerfSmokeTests(ITestOutputHelper output)
{
    public const string CaptureDirEnvVar = "DOTCHARTER_MENU_CAPTURE_DIR";

    private static readonly Size Slot = new(1280, 900);

    [Theory]
    [InlineData("Menu_File", typeof(FileMenu))]
    [InlineData("Menu_Edit", typeof(EditMenu))]
    [InlineData("Menu_TempoMap", typeof(TempoMapMenu))]
    [InlineData("Menu_Arrangements", typeof(ArrangementsMenu))]
    [InlineData("Menu_Vocals", typeof(VocalsMenu))]
    [InlineData("Menu_Tools", typeof(ToolsMenu))]
    [InlineData("Menu_View", typeof(ViewMenu))]
    [InlineData("Menu_Help", typeof(HelpMenu))]
    public Task EveryTopLevelMenu_OpensWithEveryRowMeasured(string headerKey, Type menuType) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new MainMenuView
            {
                DataContext = Shell(),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var window = new Window
            {
                Width = Slot.Width, Height = Slot.Height, Content = view,
                Background = UiSweepRunner.ShellBackground(),
            };
            window.Show();
            try
            {
                Settle(window);
                var header = LocalizationService.Instance[headerKey];
                var top = view.TopLevelMenus().Single(m => m.Header?.ToString() == header);
                Assert.IsType(menuType, top);
                Assert.True(top.Bounds is { Width: > 1, Height: > 1 }, $"the {headerKey} header laid out as {top.Bounds}");
                Assert.True(top.Classes.Contains("FireMenuTop"), $"the {headerKey} header lost the FireMenuTop class");

                // The header is STYLED as one: `MenuItem.FireMenuTop` (Fire) matches on
                // the style key, so a menu class keyed as itself keeps its template but
                // silently loses this padding and type size (measured at #653).
                Assert.True(top.TryFindResource("FirePadMenuTop", top.ActualThemeVariant, out var pad) && pad is Thickness,
                    "FirePadMenuTop does not resolve from the menu bar");
                Assert.True(top.Padding == (Thickness)pad!,
                    $"the {headerKey} header's padding is {top.Padding}, not FireMenuTop's {pad} — its style key no longer matches MenuItem");

                Assert.True(MenuFlyoutPrewarmer.Realize(top, Slot) > 0,
                    $"the prewarm realized nothing under {headerKey} — its first open would pay it all");

                top.Open();
                Settle(window);

                Assert.True(top.IsSubMenuOpen, $"{headerKey} did not open");
                var popup = top.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
                Assert.True(popup is { IsOpen: true, Child: Control },
                    $"{headerKey} has no open popup — its template did not apply (style key?)");
                var panel = popup!.Child!;
                Assert.True(panel.Bounds is { Width: > 1, Height: > 1 },
                    $"the {headerKey} dropdown laid out as {panel.Bounds} — it opens onto nothing");

                var rows = top.Items.OfType<MenuItem>().Where(r => r.IsVisible).ToList();
                Assert.True(rows.Count >= 3, $"{headerKey} shows only {rows.Count} rows");
                foreach (var row in rows)
                {
                    Assert.True(row.IsEffectivelyVisible && row.Bounds is { Width: > 1, Height: > 1 },
                        $"the {headerKey} row '{row.Header}' laid out as {row.Bounds} in the open dropdown");
                }

                output.WriteLine($"{headerKey}: {rows.Count} rows, dropdown {panel.Bounds.Width:F0}x{panel.Bounds.Height:F0}");
                Capture(window, $"menu-{headerKey}.png");

                top.Close();
                Settle(window);
                Assert.False(top.IsSubMenuOpen, $"{headerKey} did not close");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>The bar holds the eight menu classes, in order, and nothing else —
    /// what <see cref="MenuFlyoutPrewarmer" /> drains after the first frame.</summary>
    [Fact]
    public Task TheBar_IsTheEightMenuFiles_InOrder() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new MainMenuView();
            Assert.Equal(
                [typeof(FileMenu), typeof(EditMenu), typeof(TempoMapMenu), typeof(ArrangementsMenu),
                    typeof(VocalsMenu), typeof(ToolsMenu), typeof(ViewMenu), typeof(HelpMenu)],
                view.TopLevelMenus().Select(m => m.GetType()));
        }, CancellationToken.None);

    private static MainWindowViewModel Shell() =>
        (App.Services ?? throw new InvalidOperationException("the headless app session did not boot"))
        .GetRequiredService<MainWindowViewModel>();

    private static void Settle(Window window)
    {
        for (var round = 0; round < 3; round++)
        {
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable(CaptureDirEnvVar);
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.True(frame is not null,
            "CaptureRenderedFrame returned null — DOTCHARTER_UI_SWEEP_DUMP=1 has to be set "
            + "BEFORE the session starts, or UseHeadlessDrawing is still true");
        frame!.Save(Path.Combine(dir, file), new PngBitmapEncoderOptions());
    }
}
