using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #494: the merged Start &amp; end dialog and the new Bookmarks dialog, attached to a real window and laid
///     out in every fake variant (the named parts MEASURE non-zero), and the Song tab's Edit song metadata
///     button, which measures and runs the command the shell wired to it. Capture is opt-in (the sweep's).
/// </summary>
[Collection("HeadlessUI")]
public sealed class SmallMovesConstructionUiPerfSmokeTests
{
    private static void Show(Control view, Action<Window> assert, string? capture = null)
    {
        var window = new Window { Width = 1200, Height = 900, Content = DialogHost.Preview(view) };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            assert(window);
            if (capture is null) return;
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            NarrowWidthLayout.Capture(window, capture);
        }
        finally
        {
            window.Close();
        }
    }

    private static Control Named(Control view, string name) => view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);

    public static TheoryData<string> StartEndVariants()
    {
        var data = new TheoryData<string>();
        foreach (var variant in FakeTempoMapOperationScreen.Variants) data.Add(variant.Name);
        return data;
    }

    public static TheoryData<string> BookmarkVariants()
    {
        var data = new TheoryData<string>();
        foreach (var variant in FakeBookmarksScreen.Variants) data.Add(variant.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(StartEndVariants))]
    public Task StartEndDialog_EveryVariant_ConstructsAttachesAndMeasures(string variant) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            // The fakes are English by design; the playhead variant opens the REAL view model so the capture shows the swept culture.
            object screen = variant == FakeTempoMapOperationScreen.AtPlayheadVariant
                ? OpenAtPlayhead()
                : FakeTempoMapOperationScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new TempoMapOperationDialogView { DataContext = screen };
            Show(view, _ =>
            {
                // The playhead variant serves one verb: its choice row is hidden, not empty.
                var alone = variant == FakeTempoMapOperationScreen.AtPlayheadVariant;
                foreach (var name in alone
                             ? new[] { "DialogGrid", "ValueInput", "BeatsPerMeasureInput", "BeatUnitInput", "BpmInput" }
                             : ["DialogGrid", "OperationInput", "ValueInput"])
                {
                    var control = Named(view, name);
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: {name} laid out as {control.Bounds}");
                }
                Assert.Equal(!alone, Named(view, "OperationInput").IsEffectivelyVisible);
                Assert.Equal(alone, Named(view, "BpmInput").IsEffectivelyVisible);
            }, $"start-end-dialog-{variant}");
        }, CancellationToken.None);

    private static TempoMapOperationViewModel OpenAtPlayhead()
    {
        var (maintenance, _) = RecordingProxy.Create<IChartMaintenanceService>();
        var vm = new TempoMapOperationViewModel(maintenance, new FakeConfigurationService(), LocalizationService.Instance,
            new FakeAppLogger());
        vm.Configure(new TempoMapOperationRequest(TempoMapOperation.InsertMeasuresAtPlayhead));
        return vm;
    }

    [Theory]
    [MemberData(nameof(BookmarkVariants))]
    public Task BookmarksDialog_EveryVariant_ConstructsAttachesAndMeasuresTenRows(string variant) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeBookmarksScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new BookmarksDialogView { DataContext = screen };
            Show(view, _ =>
            {
                var list = Named(view, "SlotsList");
                Assert.True(list.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: the slot list laid out as {list.Bounds}");
                var buttons = list.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Equal(30, buttons.Count);
                Assert.All(buttons, b => Assert.True(b.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: a row button laid out as {b.Bounds}"));
            }, $"bookmarks-dialog-{variant}");
        }, CancellationToken.None);

    [Fact]
    public Task SongTab_EditSongMetadataButton_MeasuresAndRunsTheWiredCommand() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var logger = new FakeAppLogger();
            var state = new ProjectState();
            var vm = new SongOverviewViewModel(state, new HistoryService(logger), logger, FakeLocalization.English(),
                DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy);
            var ran = 0;
            vm.OpenSongMetadataCommand = new RelayCommand(() => ran++);
            var tab = new SongInspectorView { DataContext = vm };
            var scope = new UserControl { Content = tab };
            scope.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });
            var window = new Window { Width = 420, Height = 720, Content = scope, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var button = (Button)Named(tab, "EditSongMetadataButton");
                Assert.True(button.Bounds is { Width: > 1, Height: > 1 }, $"the button laid out as {button.Bounds}");
                var caption = Assert.IsType<TextBlock>(button.Content).Text;
                Assert.False(string.IsNullOrWhiteSpace(caption));
                Assert.NotEqual("Inspector_Song_MoreDetails", caption);
                button.Command!.Execute(null);
                Assert.Equal(1, ran);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
}
