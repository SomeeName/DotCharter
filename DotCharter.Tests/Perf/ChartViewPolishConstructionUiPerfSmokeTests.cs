using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.Localization;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.Services;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The chart view polish on the real status rail: the audio readout is one health dot (measured, and
///     the detail text not drawn) while audio is fine, and the dot plus the full text while it is not;
///     the rail no longer carries the grid snap readout. Width-shaped, so it is in the de-DE / ja-JP
///     layout filter. Capture is the sweep's (<c>DOTCHARTER_UI_SWEEP_DUMP=1</c>).
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChartViewPolishConstructionUiPerfSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheAudioReadout_IsOneDotWhenFine_AndTheFullTextWhenNot(bool problem) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            // The rail over a fake engine, so the audio state is the one the case sets, not the headless
            // session's own (its 1 s refresh would overwrite a hand-set property).
            var dispatcher = services.GetRequiredService<IUiDispatcher>();
            var audio = new FakeAudioPlaybackService
            {
                ActiveSampleRate = problem ? 0 : 48000,
                ActualBufferFrames = problem ? 0 : 128
            };
            using var vm = new StatusBarViewModel(new StatusBarService(dispatcher), state, audio, dispatcher,
                services.GetRequiredService<IPlayerProfileService>(), services.GetRequiredService<IFileStorageService>(),
                services.GetRequiredService<ILocalizationService>(), new FakeConfigurationService(),
                services.GetRequiredService<TransportViewModel>(), services.GetRequiredService<ISelectionService>());
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;
            Window? window = null;
            try
            {
                NarrowWidthLayout.SeedSong(state);
                vm.IsEditorSurface = true;
                Assert.Equal(problem, vm.HasAudioProblem);

                var view = new StatusBar { DataContext = vm };
                window = NarrowWidthLayout.NewWindow(1280, 120, view);
                UiBreakpointController.Apply(window, UiBreakpointController.For(1280));
                window.Show();
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, problem ? "statusbar-audio-problem" : "statusbar-audio-ok");

                var group = NarrowWidthLayout.Named<StackPanel>(view, "StatusAudioHealth");
                var dot = group.GetVisualDescendants().OfType<Ellipse>().Single();
                Assert.True(dot.Bounds is { Width: > 1, Height: > 1 }, $"the health dot laid out as {dot.Bounds}");
                Assert.Equal(vm.AudioStatus, ToolTip.GetTip(group));

                var text = group.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.Equal(problem, text.IsEffectivelyVisible);
                if (problem) Assert.True(text.Bounds.Width > 1, $"the problem text laid out as {text.Bounds}");
                Assert.Equal(problem, dot.Classes.Contains("Problem"));

                var cluster = NarrowWidthLayout.Named<StackPanel>(view, "StatusMetricCluster");
                var (clusterLeft, _) = NarrowWidthLayout.Span(cluster, view);
                var (_, groupRight) = NarrowWidthLayout.Span(group, view);
                Assert.True(groupRight <= NarrowWidthLayout.Span(cluster, view).Right + 0.5 && group.Bounds.Left >= -0.5,
                    $"the audio group ({group.Bounds}) left the metric cluster starting at {clusterLeft:0.#}");

                // The grid snap is the toolbar's: nothing on the rail reads "Snap …" any more.
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.IsEffectivelyVisible && t.Text is { } s && (s.StartsWith("Snap ", StringComparison.Ordinal)
                        || s.StartsWith("Raster ", StringComparison.Ordinal) || s.StartsWith("スナップ ", StringComparison.Ordinal)));
            }
            finally
            {
                window?.Close();
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
            }
        }, CancellationToken.None);
}
