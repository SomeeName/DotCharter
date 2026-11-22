using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class AnnotationResolutionUiPerfSmokeTests
{
    [Fact]
    public async Task The_resolved_checkbox_measures_and_changes_the_annotation_through_history() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("The headless app did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var history = services.GetRequiredService<IHistoryService>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var previous = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;
            var previousLevel = state.ActiveLevel;
            var project = new ChartProject();
            var arrangement = new Arrangement { Name = "Lead", Levels = { new ChartLevel() } };
            project.Arrangements.Add(arrangement);
            var annotation = new ChartAnnotation { Text = "Check this bend", TimeMs = 1000 };
            project.Annotations.Add(annotation);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = arrangement.Levels[0];
            history.Clear();
            using var dirty = new ProjectDirtyTracker(history);
            var view = new NotesInspectorView { DataContext = inspector };
            var window = new Window { Width = 420, Height = 600, Content = view };
            if (Application.Current is { } app && app.TryGetResource("AppBackground", app.ActualThemeVariant, out var background)
                && background is IBrush brush)
            {
                window.Background = brush;
            }

            window.Show();
            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var checkbox = Assert.Single(view.GetVisualDescendants().OfType<CheckBox>());
                Assert.True(checkbox.Bounds is { Width: > 1, Height: > 1 });
                Capture(window, "before");
                var at = checkbox.TranslatePoint(new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Capture(window, "after");

                Assert.True(annotation.Resolved);
                Assert.True(dirty.IsDirty);
                Assert.Single(history.GetUndoSteps());
                history.Undo();
                Dispatcher.UIThread.RunJobs();
                Assert.False(annotation.Resolved);
                Assert.False(checkbox.IsChecked);
                Assert.False(dirty.IsDirty);
                history.Redo();
                Dispatcher.UIThread.RunJobs();
                Assert.True(checkbox.IsChecked);
            }
            finally
            {
                window.Close();
                history.Clear();
                state.ActiveProject = previous;
                state.ActiveArrangement = previousArrangement;
                state.ActiveLevel = previousLevel;
            }
        }, CancellationToken.None);

    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("DOTCHARTER_ISSUE548_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }
}
