using Avalonia.Controls;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell.Popovers;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #395: the Tracks popover's Difficulty level row, in a real window through a real
///     <see cref="FlyoutPresenter" />, on a three-level arrangement the editor shows at its easiest
///     level, as a quality-report Go to leaves it. The row lays out with a size, and Hardest
///     switches back to the level with every note.
/// </summary>
[Collection("HeadlessUI")]
public sealed class TracksPopoverLevelRowUiPerfSmokeTests
{
    [Fact]
    public Task TheLevelRow_LaysOut_AndHardestSwitchesBack() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var editor = services.GetRequiredService<ChartEditorViewModel>();
            var state = services.GetRequiredService<ProjectState>();

            var project = new ChartProject();
            for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            var arrangement = new Arrangement { Name = "Lead" };
            for (var d = 0; d < 3; d++) arrangement.Levels.Add(new ChartLevel { DifficultyIndex = d });
            project.Arrangements.Add(arrangement);
            editor.ApplyProject(project, resetHistory: true);
            state.ActiveLevel = arrangement.Levels[0];

            var view = new TracksPopoverView { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            var window = new Window { Width = 420, Height = 900, Content = new FlyoutPresenter { Content = view } };
            window.Show();
            try
            {
                window.UpdateLayout();
                var row = view.FindControl<StackPanel>("LevelRow")!;
                var value = view.FindControl<TextBlock>("LevelValue")!;
                var hardest = view.FindControl<Button>("LevelHardestButton")!;

                Assert.True(row.IsEffectivelyVisible, "the row is hidden on a three-level arrangement");
                Assert.True(row.Bounds.Width > 0 && row.Bounds.Height > 0, $"the row measured {row.Bounds}");
                Assert.True(hardest.Bounds.Width > 0 && hardest.Bounds.Height > 0, $"Hardest measured {hardest.Bounds}");
                Assert.Equal("1 / 3", value.Text);
                Assert.True(hardest.IsEffectivelyEnabled, "Hardest is disabled below the hardest level");

                hardest.Command!.Execute(hardest.CommandParameter);
                window.UpdateLayout();

                Assert.Same(arrangement.Levels[2], state.ActiveLevel);
                Assert.Equal("3 / 3", value.Text);
                Assert.False(hardest.IsEffectivelyEnabled, "Hardest stays enabled on the hardest level");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
}
