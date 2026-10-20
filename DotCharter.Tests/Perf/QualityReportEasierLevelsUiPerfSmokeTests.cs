using Avalonia.Controls;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #397: the Chart quality report's "Show easier difficulty levels" row, in a real window over
///     the fake screen. With findings set aside it lays out with a size; with none it is hidden.
/// </summary>
[Collection("HeadlessUI")]
public sealed class QualityReportEasierLevelsUiPerfSmokeTests
{
    [Fact]
    public Task TheEasierLevelsRow_LaysOutWhenFindingsAreSetAside_AndHidesOtherwise() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new ChartQualityReportDialogView { DataContext = FakeChartQualityReportScreen.Default() };
            var window = new Window { Width = 900, Height = 1200, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                var row = view.FindControl<Grid>("EasierLevelsRow")!;
                var box = view.FindControl<CheckBox>("ShowEasierLevelsBox")!;
                Assert.True(row.IsEffectivelyVisible, "the row is hidden while findings are set aside");
                Assert.True(box.Bounds.Width > 0 && box.Bounds.Height > 0, $"the toggle measured {box.Bounds}");

                view.DataContext = FakeChartQualityReportScreen.Empty();
                window.UpdateLayout();
                Assert.False(row.IsVisible, "the row shows with nothing set aside");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
}
