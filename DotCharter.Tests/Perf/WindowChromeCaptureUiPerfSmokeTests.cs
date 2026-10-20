using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using DotCharter.Presentation.Shared;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The window chrome's three rows as pictures at 3x — the title bar, the back bar and the
///     window caption, each restored and maximized — on the real <see cref="MainWindow" />.
///     The before/after evidence for lifting the rows out of <c>MainWindow.axaml</c> (#654): a
///     move that is pixel-identical here changed nothing the user sees.
///     <para>
///         A CAPTURE harness: it does its work only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (Skia
///         drawing). The rows are found by name in the VISUAL tree, which crosses namescopes, so
///         the same harness runs on either side of a move. Each row is shown by local values
///         (the three rows' visibility, the rail's, the title row's height), because switching
///         the shell to a full-bleed surface runs audio work that takes the testhost down
///         (<c>TitleBarDragUiPerfSmokeTests</c>). PNGs go to <c>DOTCHARTER_RIG_CAPTURE_DIR</c>
///         when set, else <c>TestResults/window-chrome/</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class WindowChromeCaptureUiPerfSmokeTests
{
    private const double Scale = 3;

    [Fact]
    public async Task TheThreeChromeRows_AreCaptured_RestoredAndMaximized()
    {
        if (!UiSweepRunner.CaptureEnabled) return;

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = App.Services?.GetRequiredService<MainWindowViewModel>()
                     ?? throw new InvalidOperationException("the headless app session did not boot.");
            foreach (var dialog in vm.Dialogs.AllDialogs)
            {
                if (dialog.IsOpen)
                    dialog.RequestClose();
            }

            vm.CommandPalette.Close();
            vm.ToneRig.IsPedalboardOpen = false;
            vm.IsGameplayPresentation = false;
            vm.SelectedMainTabIndex = 0;

            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var root = NarrowWidthLayout.Named<Grid>(window, "WindowRootGrid");
                var titleGrid = NarrowWidthLayout.Named<Grid>(window, "TitleContentGrid");
                var rail = NarrowWidthLayout.Named<Control>(window, "ActivityRail");
                string[] rows = ["TitleBarRow", "BackBar", "WindowCaption"];

                foreach (var state in new[] { WindowState.Normal, WindowState.Maximized })
                {
                    window.WindowState = state;
                    foreach (var shown in rows)
                    {
                        // Each row as the surface that shows it: the editor's title bar with
                        // the rail, a full-bleed surface's back bar or caption without it, at
                        // that row's height.
                        foreach (var row in rows)
                            NarrowWidthLayout.Named<Grid>(window, row).IsVisible = row == shown;
                        rail.IsVisible = shown == "TitleBarRow";
                        titleGrid.RowDefinitions[0].Height = new GridLength(shown switch
                        {
                            "TitleBarRow" => ShellPresentation.TitleBarHeight,
                            "BackBar" => ShellPresentation.BackBarHeight,
                            _ => ShellPresentation.WindowCaptionHeight
                        });

                        NarrowWidthLayout.Settle(window);
                        var band = NarrowWidthLayout.Named<Grid>(window, shown);
                        Assert.True(band.Bounds is { Width: > 0, Height: > 0 }, $"{shown} laid out as {band.Bounds}");
                        Save(root, $"{shown}-{state}");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void Save(Control root, string name)
    {
        var size = new PixelSize((int)Math.Ceiling(root.Bounds.Width * Scale), (int)Math.Ceiling(root.Bounds.Height * Scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * Scale, 96 * Scale));
        bitmap.Render(root);
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "TestResults", "window-chrome");
        Directory.CreateDirectory(dir);
        bitmap.Save(Path.Combine(dir, name + ".png"), new PngBitmapEncoderOptions());
        NarrowWidthLayout.Report($"window-chrome {name}: {root.Bounds.Width}x{root.Bounds.Height} @ {Scale}x");
    }
}
