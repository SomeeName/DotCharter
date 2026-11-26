using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.Views.Shell.Menus;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #950: the owner opened File right after start and saw every row's text drawn on top of
///     each other in the first row, the separators spread down an otherwise empty panel. The
///     menu prewarm (<see cref="MenuFlyoutPrewarmer" />) is paced to land while the user holds
///     still, so its step for a menu can run while that menu is OPEN; it arranged each row at
///     (0, 0), which overrode the open panel's slots, and nothing re-arranged them. Each row of
///     an open dropdown must keep its own row: increasing Y, non-zero height, whichever order
///     the prewarm and the click come in.
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///         <see cref="MainMenuDropdownUiPerfSmokeTests.CaptureDirEnvVar" /> set, each case writes
///         <c>prewarm-open-&lt;menu&gt;.png</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class MenuPrewarmWhileOpenUiPerfSmokeTests(ITestOutputHelper output)
{
    private static readonly Size Slot = new(1280, 900);

    [Theory]
    [InlineData(typeof(FileMenu), true)]
    [InlineData(typeof(FileMenu), false)]
    [InlineData(typeof(EditMenu), true)]
    [InlineData(typeof(TempoMapMenu), true)]
    [InlineData(typeof(ArrangementsMenu), true)]
    [InlineData(typeof(ToolsMenu), true)]
    [InlineData(typeof(ViewMenu), true)]
    [InlineData(typeof(HelpMenu), true)]
    public Task AnOpenMenu_KeepsOneRowPerItem_WhenThePrewarmLands(Type menuType, bool prewarmWhileOpen) =>
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
                var top = view.TopLevelMenus().Single(m => m.GetType() == menuType);
                if (!prewarmWhileOpen) MenuFlyoutPrewarmer.Realize(top, Slot);

                top.Open();
                Settle(window);
                Assert.True(top.IsSubMenuOpen, $"{menuType.Name} did not open");

                // The product's drain step for this menu, landing while it is open.
                if (prewarmWhileOpen) MenuFlyoutPrewarmer.Realize(top, Slot);
                Settle(window);

                // Captured before the checks, so a red run still leaves its picture.
                Capture(window, $"prewarm-open-{menuType.Name}{(prewarmWhileOpen ? "" : "-prewarm-first")}.png");
                var rows = top.Items.OfType<Control>().Where(r => r.IsVisible).ToList();
                Assert.True(rows.Count(r => r is MenuItem) >= 3, $"{menuType.Name} shows only {rows.Count} rows");
                var previousBottom = double.NegativeInfinity;
                foreach (var row in rows)
                {
                    var label = row is MenuItem item ? $"'{item.Header}'" : row.GetType().Name;
                    Assert.True(row.Bounds.Height > 0, $"{menuType.Name} row {label} laid out as {row.Bounds}");
                    Assert.True(row.Bounds.Y >= previousBottom - 0.5,
                        $"{menuType.Name} row {label} sits at y={row.Bounds.Y:F1}, above the previous row's bottom "
                        + $"{previousBottom:F1}: the rows are stacked, not listed");
                    previousBottom = row.Bounds.Bottom;
                }

                output.WriteLine($"{menuType.Name} (prewarm {(prewarmWhileOpen ? "while open" : "first")}): "
                    + $"{rows.Count} rows, last bottom {previousBottom:F0}");
                top.Close();
                Settle(window);
            }
            finally
            {
                window.Close();
            }
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
        var dir = Environment.GetEnvironmentVariable(MainMenuDropdownUiPerfSmokeTests.CaptureDirEnvVar);
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.True(frame is not null, "CaptureRenderedFrame returned null: set DOTCHARTER_UI_SWEEP_DUMP=1 before the session starts");
        frame!.Save(Path.Combine(dir, file), new PngBitmapEncoderOptions());
    }
}
