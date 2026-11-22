using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.CommandPalette;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The in-place fret-hand position box and section popover (#498) built for real over a real timeline:
///     both cards are hidden and offered to the shell prewarm until a tool opens one, then they MEASURE
///     (a view whose styles failed still arranges at zero size), sit inside the timeline on their rows,
///     every input has an accessible name, a panned view moves them, and Enter / Escape drive the same
///     commit and cancel the view models test. The open time is logged, never asserted.
/// </summary>
[Collection("HeadlessUI")]
public sealed class InlineEditConstructionUiPerfSmokeTests
{
    [Fact]
    public Task ThePaletteFhpEntry_OpensTheTimelineEditorAtThePlayhead() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = DotCharter.UI.App.Services!;
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var state = services.GetRequiredService<ProjectState>();
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel);
            var project = new ChartProject();
            var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
            arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arrangement);
            project.Beats.Add(new BeatMarker { Time = 0 });
            project.Beats.Add(new BeatMarker { Time = 500 });
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = arrangement.Levels[0];
            var inline = vm.ShellDialogs.InlineEdit;
            var previousHost = inline.Host;
            inline.Host = new object();
            try
            {
                var entry = CommandCatalog.Build(vm, FakeLocalization.English()).Single(c => c.Id == "tools.editFhp");
                Assert.Equal(state.Keybinds.EditFhp, entry.Shortcut());
                entry.Execute();
                Assert.True(inline.Fhp.IsOpen);
            }
            finally
            {
                inline.Fhp.Cancel();
                inline.Host = previousHost;
                foreach (var dialog in vm.Dialogs.AllDialogs)
                    if (dialog.IsOpen) dialog.RequestClose();
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
            }
        }, CancellationToken.None);

    private static (Window Window, ChartRenderer Renderer, InlineEditOverlayView View, InlineEditRig Rig) Build()
    {
        var rig = new InlineEditRig();
        var renderer = new ChartRenderer { Editor = rig.Chart.Editor, Focusable = true };
        var view = new InlineEditOverlayView { DataContext = rig.Inline, Renderer = renderer };
        var host = new Panel { Width = 900, Height = 360 };
        host.Children.Add(renderer);
        host.Children.Add(view);
        var window = new Window { Width = 900, Height = 360, Content = host };
        return (window, renderer, view, rig);
    }

    private static void AssertMeasured(Control control) =>
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"{control.Name ?? control.GetType().Name} laid out as {control.Bounds} - it rendered nothing.");

    private static void AssertInside(Control card, ChartRenderer renderer)
    {
        AssertMeasured(card);
        var origin = card.TranslatePoint(default, renderer) ?? default;
        Assert.True(origin.X >= -0.5 && origin.X + card.Bounds.Width <= renderer.Bounds.Width + 0.5 &&
                    origin.Y >= -0.5 && origin.Y + card.Bounds.Height <= renderer.Bounds.Height + 0.5,
            $"{card.Name} at {origin} size {card.Bounds.Size} leaves the {renderer.Bounds.Size} timeline");
    }

    [Fact]
    public Task TheFretEditor_HangsOnTheFhpRow_MeasuresAndCommitsOnEnter() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, renderer, view, rig) = Build();
            window.Show();
            try
            {
                window.UpdateLayout();
                var card = view.FindControl<Border>("FhpEditorCard")!;
                var section = view.FindControl<Border>("SectionEditorCard")!;
                var lyric = view.FindControl<Border>("LyricEditorCard")!;
                Assert.Equal([card, section, lyric], view.HiddenSurfaces());
                Assert.Same(renderer, rig.Inline.Host);

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                rig.Inline.ShowFhp(2000, null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Console.WriteLine($"inline fhp editor first open: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");

                Assert.True(card.IsVisible);
                Assert.Equal([section, lyric], view.HiddenSurfaces());
                AssertInside(card, renderer);
                foreach (var name in new[] { "FhpFretBox", "FhpWidthBox" })
                {
                    var box = view.FindControl<TextBox>(name)!;
                    AssertMeasured(box);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)), $"{name} has no accessible name");
                }

                // The card sits on the FHP row: below the lanes' top margin, at the row's own Y.
                var y = card.TranslatePoint(default, renderer)!.Value.Y;
                Assert.True(y >= renderer.TopMargin, $"the card at y={y} is above the note lanes ({renderer.TopMargin})");

                var fret = view.FindControl<TextBox>("FhpFretBox")!;
                fret.Text = "9";
                fret.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Assert.False(rig.Inline.Fhp.IsOpen);
                Assert.Equal(9, Assert.Single(rig.Chart.Editor.Anchors).Fret);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task TheSectionPopover_HangsUnderTheSectionsRow_ListsSuggestionsAndCancelsOnEscape() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, renderer, view, rig) = Build();
            window.Show();
            try
            {
                window.UpdateLayout();
                rig.Inline.ShowSection(2000, rig.Lead);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var card = view.FindControl<Border>("SectionEditorCard")!;
                Assert.True(card.IsVisible);
                AssertInside(card, renderer);
                var list = view.FindControl<ListBox>("SectionSuggestionList")!;
                AssertMeasured(list);
                Assert.True(list.ItemCount > 20, "the known section names are not offered");
                foreach (var name in new[] { "SectionNameBox", "SectionSuggestionList", "SectionMoreButton" })
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(view.FindControl<Control>(name)!)), $"{name} has no accessible name");
                var y = card.TranslatePoint(default, renderer)!.Value.Y;
                Assert.True(y >= renderer.TopRibbon.SectionY, $"the popover at y={y} is above the Sections row");

                card.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(rig.Inline.Section.IsOpen);
                Assert.Empty(rig.Lead.EventPoints);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task APannedTimeline_MovesTheEditorWithItsTime() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, renderer, view, rig) = Build();
            window.Show();
            try
            {
                window.UpdateLayout();
                rig.Chart.State.TimelineFollowsPlayhead = false;
                rig.Chart.State.TimelineViewAnchorMs = 2000;
                rig.Inline.ShowFhp(2000, null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var card = view.FindControl<Border>("FhpEditorCard")!;
                var before = card.TranslatePoint(default, renderer)!.Value.X;

                rig.Chart.State.TimelineViewAnchorMs = 2200;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var after = card.TranslatePoint(default, renderer)!.Value.X;

                var pixelsPerMs = rig.Chart.State.PixelsPerMs;
                Assert.Equal(-200 * pixelsPerMs, after - before, 0.5);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
}
