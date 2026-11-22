using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #497: the Fix chart dialog (one dialog over Auto-fix arrangements and Improve chart), attached to a real window
///     and laid out in every fake variant: both groups, their lists, the total line and Apply MEASURE non-zero, and
///     the lists hold one line per change. The real view model drives the capture half (opt-in, as the sweep's).
/// </summary>
[Collection("HeadlessUI")]
public sealed class FixChartConstructionUiPerfSmokeTests
{
    private static void WithDialog(IFixChartScreen screen, Action<FixChartDialogView, Window> assert)
    {
        var view = new FixChartDialogView { DataContext = screen };
        var window = new Window { Width = 1200, Height = 900, Content = DialogHost.Preview(view) };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            assert(view, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static Control Named(Control view, string name) => view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);

    public static TheoryData<string> VariantNames()
    {
        var data = new TheoryData<string>();
        foreach (var variant in FakeFixChartScreen.Variants) data.Add(variant.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(VariantNames))]
    public async Task EveryVariant_ConstructsAttachesAndMeasures(string variant)
    {
        var screen = FakeFixChartScreen.Variants.Single(v => v.Name == variant).Create();
        await HeadlessSessionFixture.Dispatch(() => WithDialog(screen, (view, _) =>
        {
            foreach (var name in new[] { "ErrorsGroup", "PolishGroup", "ErrorsBox", "PolishBox", "TotalText", "ApplyButton" })
            {
                var control = Named(view, name);
                Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: {name} laid out as {control.Bounds}");
            }

            var apply = (Button)Named(view, "ApplyButton");
            Assert.Equal(screen.CanApply, apply.IsEffectivelyEnabled);
            var errors = Named(view, "ErrorsList");
            Assert.Equal(screen.ErrorsChanges.Count, errors.GetVisualDescendants().OfType<TextBlock>().Count());
            var polish = Named(view, "PolishList");
            Assert.Equal(screen.PolishChanges.Count, polish.GetVisualDescendants().OfType<TextBlock>().Count());
        }), CancellationToken.None);
    }

    /// <summary>The real view model over a fake maintenance service: the product's own strings on the product's view. Capture is opt-in.</summary>
    [Fact]
    public async Task TheRealViewModel_ListsEveryChange_AndTheDialogCaptures()
    {
        var harness = new ChartEditHarness();
        var maintenance = DispatchProxy.Create<IChartMaintenanceService, FixChartViewModelTests.FakeMaintenance>();
        ((FixChartViewModelTests.FakeMaintenance)(object)maintenance).BothAnswer = new FixChartPreview(
            [new FixChartChange("duplicatesRemoved", 12), new FixChartChange("anchorsAddedAtPhrases", 4),
                new FixChartChange("handShapesAddedOverChords", 6), new FixChartChange("templateFingersCleaned", 2)],
            [new FixChartChange("phrase-start anchors", 9), new FixChartChange("end phrase", 1), new FixChartChange("chord sustains", 3)]);
        var vm = new FixChartViewModel(harness.Services, maintenance);
        vm.Configure(new FixChartRequest());
        await vm.Pending;

        await HeadlessSessionFixture.Dispatch(() => WithDialog(vm, (view, window) =>
        {
            Assert.Equal(4, Named(view, "ErrorsList").GetVisualDescendants().OfType<TextBlock>().Count());
            Assert.Equal(3, Named(view, "PolishList").GetVisualDescendants().OfType<TextBlock>().Count());
            Assert.Equal("Apply makes 37 changes as one undo step.", ((TextBlock)Named(view, "TotalText")).Text);
            Assert.True(Named(view, "ApplyButton").Bounds is { Width: > 1, Height: > 1 });
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            NarrowWidthLayout.Capture(window, "fix-chart-dialog-after");
        }), CancellationToken.None);
    }
}
