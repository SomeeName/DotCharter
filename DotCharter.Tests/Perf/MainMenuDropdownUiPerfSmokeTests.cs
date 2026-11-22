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

                // Each group under it opens onto its own rows (static groups only: a
                // group filled from data, like Open recent, holds no MenuItem here).
                var groups = rows.Where(r => r.IsEffectivelyEnabled && r.Items.OfType<MenuItem>().Any()).ToList();
                if (headerKey == "Menu_Edit") AssertHeldGripRow(groups);
                var boundHeaders = 0;
                for (var index = 0; index < groups.Count; index++)
                {
                    var group = groups[index];
                    group.Open();
                    Settle(window);
                    Assert.True(group.IsSubMenuOpen, $"the {headerKey} group '{group.Header}' did not open");
                    var groupRows = group.Items.OfType<MenuItem>().Where(r => r.IsVisible).ToList();
                    Assert.True(groupRows.Count >= 3, $"the {headerKey} group '{group.Header}' shows only {groupRows.Count} rows");
                    foreach (var row in groupRows)
                    {
                        Assert.True(row.IsEffectivelyVisible && row.Bounds is { Width: > 1, Height: > 1 },
                            $"the row '{row.Header}' of the {headerKey} group '{group.Header}' laid out as {row.Bounds}");
                    }

                    // The one bound header in the bar: Tools ▸ Generate's first row names its scope,
                    // and this shell has nothing selected.
                    if (groupRows[0].Command == Shell().FhpGeneration.GenerateFhpForScopeCommand)
                    {
                        Assert.Equal(LocalizationService.Instance[EditingAvailability.KeyGenerateFhpArrangement],
                            groupRows[0].Header?.ToString());
                        boundHeaders++;
                    }

                    output.WriteLine($"{headerKey} > {group.Header}: {groupRows.Count} rows");
                    Capture(window, $"menu-{headerKey}-group{index + 1}.png");
                    group.Close();
                    Settle(window);
                }

                Assert.Equal(headerKey == "Menu_Tools" ? 1 : 0, boundHeaders);

                top.Close();
                Settle(window);
                Assert.False(top.IsSubMenuOpen, $"{headerKey} did not close");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     Tempo map ▸ Fit the tab's tempo to the recording matches the chart's notes against the audio, so on
    ///     a chart without notes its row is DRAWN disabled and carries the reason as a tooltip shown while
    ///     disabled; a note placed since raises no event, so the row is asked again each time the menu opens.
    /// </summary>
    [Fact]
    public Task TheFitTempoRow_IsDisabledWithItsReasonOnAChartWithoutNotes_AndEnabledOnTheNextOpenOnceItHasOne() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var lead = new Arrangement { Name = "Lead" };
            lead.Levels.Add(new ChartLevel());
            var project = new ChartProject();
            for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
            project.Arrangements.Add(lead);
            shell.ProjectState.ActiveProject = project;
            shell.ProjectState.ActiveArrangement = lead;

            var view = new MainMenuView
            {
                DataContext = shell,
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
                var top = view.TopLevelMenus().OfType<TempoMapMenu>().Single();
                MenuFlyoutPrewarmer.Realize(top, Slot);
                var title = LocalizationService.Instance["Menu_AlignToRecording"];
                var row = top.Items.OfType<MenuItem>().Single(r => r.Header?.ToString() == title);

                top.Open();
                Settle(window);
                Assert.False(row.IsEffectivelyEnabled, "the row is enabled on a chart without notes");
                Assert.Equal(LocalizationService.Instance["Dialog_AlignToRecording_NeedsNotes"], ToolTip.GetTip(row));
                Assert.True(ToolTip.GetShowOnDisabled(row), "a disabled row hides its tooltip, so the reason would never show");
                Assert.True(row.Bounds is { Width: > 1, Height: > 1 }, $"the row laid out as {row.Bounds}");
                Capture(window, "menu-fit-tempo-no-notes.png");
                top.Close();
                Settle(window);

                lead.Levels[0].Notes.Add(new GuitarNote());
                top.Open();
                Settle(window);
                Assert.True(row.IsEffectivelyEnabled, "the row stayed disabled after the chart got a note and the menu reopened");
                Assert.Null(ToolTip.GetTip(row));
                Capture(window, "menu-fit-tempo-with-notes.png");
                top.Close();
                Settle(window);
            }
            finally
            {
                window.Close();
                shell.ProjectState.ActiveArrangement = null;
                shell.ProjectState.ActiveProject = null;
            }
        }, CancellationToken.None);

    /// <summary>The bar holds the seven menu classes, in order, and nothing else —
    /// what <see cref="MenuFlyoutPrewarmer" /> drains after the first frame.</summary>
    [Fact]
    public Task TheBar_IsTheSevenMenuFiles_InOrder() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new MainMenuView();
            Assert.Equal(
                [typeof(FileMenu), typeof(EditMenu), typeof(TempoMapMenu), typeof(ArrangementsMenu),
                    typeof(ToolsMenu), typeof(ViewMenu), typeof(HelpMenu)],
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

    private static void AssertHeldGripRow(IEnumerable<MenuItem> groups)
    {
        var chord = groups.Single(g => g.Header?.ToString() == LocalizationService.Instance["Menu_GroupChords"]);
        var row = Assert.Single(chord.Items.OfType<MenuItem>(), r => r.Header?.ToString() == LocalizationService.Instance["Menu_GripsFromHeldNotes"]);
        Assert.NotNull(row.Command);
        Assert.False(row.IsEnabled);
        var shapes = Assert.Single(chord.Items.OfType<MenuItem>(),
            r => Equals(r.Header, LocalizationService.Instance["Menu_HandShapesFromLetRing"]));
        Assert.NotNull(shapes.Command);
        Assert.False(shapes.IsEnabled);
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
