using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.UI;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.PlayMode.Screens;
using DotCharter.UI.Views.Settings;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     A batch of layout and style faults the 2026-11-06 capture review found (#330, #331, #332, #333,
///     #334, #335), each pinned by what the user sees: the Tracks chip fits the rail, the search
///     placeholder stops before its badge, a radio's label and unchecked ring are readable on the dark
///     surfaces, the onboarding role pair holds exactly one choice, and the Count-in segments sit inside
///     their frame. Width-shaped, so this class is in the release's de-DE / ja-JP layout filter.
/// </summary>
[Collection("HeadlessUI")]
public sealed class LayoutShellBatchUiPerfSmokeTests
{
    [Fact]
    public async Task TheTracksChip_FitsTheRail_At1280() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new ToolbarView();
            var window = NarrowWidthLayout.NewWindow(1280, 120, view);
            try
            {
                window.Show();
                // The bound name of a long arrangement, as the chip shows it.
                var name = NarrowWidthLayout.Named<TextBlock>(view, "TracksArrangementName");
                name.Text = "Lead Guitar (Extended Arrangement)";
                NarrowWidthLayout.Settle(window);

                var scroller = NarrowWidthLayout.Named<ScrollViewer>(view, "CoreRailScroller");
                var chip = NarrowWidthLayout.Named<Button>(view, "TracksFlyoutButton");
                var (_, chipRight) = NarrowWidthLayout.Span(chip, scroller);
                NarrowWidthLayout.Report($"toolbar @1280: chip right {chipRight:0.#} of viewport {scroller.Viewport.Width:0.#}, name visible {name.IsVisible} ({name.Bounds.Width:0.#} px)");
                Assert.True(chipRight <= scroller.Viewport.Width + 0.5,
                    $"the Tracks chip ends at {chipRight:0.#} px, past the rail's {scroller.Viewport.Width:0.#} px viewport — it is cut mid-word (#330).");
                if (name.IsVisible)
                {
                    Assert.True(name.TextLayout.TextLines.Any(l => l.HasCollapsed)
                                || name.TextLayout.WidthIncludingTrailingWhitespace <= name.Bounds.Width + 0.5,
                        "the arrangement name is neither whole nor ellipsized.");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task TheToolbarWords_ComeBack_WhenTheWindowGrowsAgain() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new ToolbarView();
            var window = NarrowWidthLayout.NewWindow(1600, 120, view);
            try
            {
                window.Show();
                NarrowWidthLayout.Settle(window);
                var labels = new[] { "MixLabel", "TempoLabel", "ToolsLabel", "TracksLabel" }
                    .Select(n => NarrowWidthLayout.Named<TextBlock>(view, n)).ToArray();
                Assert.All(labels, l => Assert.True(l.IsVisible, $"{l.Name} is hidden at 1600 px before any resize."));

                // Session health lives in Help, not the toolbar's right group.
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                    b => b.Command is not null && b.Classes.Count == 0 && b.Content is Avalonia.Controls.PathIcon pi
                         && pi.Data is { } d && d == (Avalonia.Application.Current!.TryGetResource("IconPulse", null, out var r) ? r as Avalonia.Media.Geometry : null));
                Assert.True(view.Bounds.Width > 0 && view.Bounds.Height > 0, "the toolbar measured empty.");

                window.Width = 560;
                NarrowWidthLayout.Settle(window);
                Assert.Contains(labels, l => !l.IsVisible);

                window.Width = 1600;
                NarrowWidthLayout.Settle(window);
                Assert.All(labels, l => Assert.True(l.IsVisible, $"{l.Name} stayed hidden after the window grew back (#474)."));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task TheSettingsSearchPlaceholder_StopsBeforeTheShortcutBadge() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            using var host = SettingsSurface.Create(services);
            var window = new Window { Width = 1600, Height = 900, Content = host };
            try
            {
                window.Show();
                NarrowWidthLayout.Settle(window);
                var shell = (SettingsShellView)host.ViewOf(typeof(ISettingsShellScreen))!;
                var placeholder = NarrowWidthLayout.Named<TextBlock>(shell, "SettingsSearchPlaceholder");
                var badge = NarrowWidthLayout.Named<Border>(shell, "SettingsSearchShortcut");
                var (_, placeholderRight) = NarrowWidthLayout.Span(placeholder, shell);
                var (badgeLeft, _) = NarrowWidthLayout.Span(badge, shell);
                NarrowWidthLayout.Report($"settings search: placeholder ends {placeholderRight:0.#}, badge starts {badgeLeft:0.#}");
                Assert.True(placeholderRight <= badgeLeft + 0.5,
                    $"the search placeholder ends at {placeholderRight:0.#} px, under the shortcut badge that starts at {badgeLeft:0.#} px (#331).");
                Assert.True(placeholder.TextLayout.TextLines.Any(l => l.HasCollapsed)
                            || placeholder.TextLayout.WidthIncludingTrailingWhitespace <= placeholder.Bounds.Width + 0.5,
                    "the placeholder is neither whole nor ellipsized.");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task APlainRadioButton_ReadsOnTheDarkSurface() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var panel = new TonePreviewTargetPanel();
            var window = NarrowWidthLayout.NewWindow(600, 200, panel);
            try
            {
                window.Show();
                NarrowWidthLayout.Settle(window);
                var app = Application.Current!;
                var clone = NarrowWidthLayout.Named<RadioButton>(panel, "PreviewTargetClone");
                Assert.False(clone.IsChecked == true, "the unchecked case is the one that vanishes");

                // #332: the label wears the theme's secondary text colour (the check box's), not Fluent's near-black.
                var label = Assert.IsType<SolidColorBrush>(clone.Foreground).Color;
                Assert.Equal(Colour(app, "TextSecondaryColor"), label);

                // #333: the unchecked ring is the BorderDefault outline the check boxes use.
                var ring = clone.GetVisualDescendants().OfType<Ellipse>().First(e => e.Name == "OuterEllipse");
                var stroke = Assert.IsType<SolidColorBrush>(ring.Stroke).Color;
                Assert.Equal(Colour(app, "BorderDefaultColor"), stroke);
                Assert.NotEqual(Colors.Black, stroke);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task TheCountInSegments_SitInsideTheirFrame_AndThePanel() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            const double panelWidth = 520;
            var view = new GameSettingsAudioView();
            var host = new Border { Width = panelWidth, Child = view };
            var window = NarrowWidthLayout.NewWindow(panelWidth + 40, 900, host);
            foreach (var grammar in new[] { "avares://DotCharter/Styles/SongListHeaderStyles.axaml", "avares://DotCharter/Views/PlayMode/PlayModeStyles.axaml" })
                window.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/")) { Source = new Uri(grammar) });
            try
            {
                window.Show();
                NarrowWidthLayout.Settle(window);
                var frame = NarrowWidthLayout.Named<Border>(view, "GameSettingsCountIn");
                var (frameLeft, frameRight) = NarrowWidthLayout.Span(frame, view);
                var items = frame.GetVisualDescendants().OfType<RadioButton>().ToList();
                Assert.Equal(4, items.Count);
                var (_, lastRight) = NarrowWidthLayout.Span(items[^1], view);
                NarrowWidthLayout.Report($"count-in: frame {frameLeft:0.#}..{frameRight:0.#}, last segment ends {lastRight:0.#}, panel {view.Bounds.Width:0.#}");
                Assert.True(lastRight <= frameRight + 0.5, $"the last Count-in segment ends at {lastRight:0.#} px, outside its frame that ends at {frameRight:0.#} px (#335).");
                Assert.True(frameRight <= view.Bounds.Width + 0.5, $"the Count-in frame ends at {frameRight:0.#} px, past the panel edge at {view.Bounds.Width:0.#} px (#335).");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task TheOnboardingRolePair_HoldsExactlyOneChoice() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            var view = new OnboardingWizardView { DataContext = rig.Vm };
            var window = NarrowWidthLayout.NewWindow(1100, 800, view);
            try
            {
                window.Show();
                rig.Vm.Open();
                rig.Vm.IsCharter = true;
                NarrowWidthLayout.Settle(window);
                var roles = view.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "OnboardingRole").ToList();
                Assert.Equal(2, roles.Count);
                Assert.Equal([true, false], roles.Select(r => r.IsChecked == true).ToArray());

                rig.Vm.IsCharter = false;
                NarrowWidthLayout.Settle(window);
                Assert.Equal([false, true], roles.Select(r => r.IsChecked == true).ToArray());
                Assert.True(rig.Vm.IsPlayer);

                rig.Vm.IsCharter = true;
                NarrowWidthLayout.Settle(window);
                Assert.Equal([true, false], roles.Select(r => r.IsChecked == true).ToArray());
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task TheOnboardingRolePair_HoldsExactlyOneChoice_FromEveryStart(bool charter, bool dataContextAfterAttach) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            rig.Vm.Open();
            rig.Vm.IsCharter = charter;
            var view = new OnboardingWizardView();
            if (!dataContextAfterAttach) view.DataContext = rig.Vm;
            var window = NarrowWidthLayout.NewWindow(1100, 800, view);
            try
            {
                window.Show();
                NarrowWidthLayout.Settle(window);
                if (dataContextAfterAttach)
                {
                    view.DataContext = rig.Vm;
                    NarrowWidthLayout.Settle(window);
                }

                var roles = view.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "OnboardingRole").ToList();
                Assert.Equal(2, roles.Count);
                Assert.Equal([charter, !charter], roles.Select(r => r.IsChecked == true).ToArray());

                // The user picks the other card: the view model follows and exactly one stays checked.
                roles[charter ? 1 : 0].IsChecked = true;
                NarrowWidthLayout.Settle(window);
                Assert.Equal(!charter, rig.Vm.IsCharter);
                Assert.Equal([!charter, charter], roles.Select(r => r.IsChecked == true).ToArray());

                // ...and back, then the view model flips it from its side.
                roles[charter ? 0 : 1].IsChecked = true;
                NarrowWidthLayout.Settle(window);
                Assert.Equal(charter, rig.Vm.IsCharter);
                Assert.Equal([charter, !charter], roles.Select(r => r.IsChecked == true).ToArray());

                rig.Vm.IsCharter = !charter;
                NarrowWidthLayout.Settle(window);
                Assert.Equal([!charter, charter], roles.Select(r => r.IsChecked == true).ToArray());
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private static Color Colour(Application app, string key) =>
        app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is Color c
            ? c
            : throw new Xunit.Sdk.XunitException($"no colour resource '{key}'.");
}
