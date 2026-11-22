using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Collisions;
using DotCharter.UI.Input.Tools;
using DotCharter.UI.Input.Tools.EventInteractionGestures;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class LyricInlineEditingUiPerfSmokeTests
{
    private static void WithTimeline(Action<Window, ChartRenderer, InlineEditOverlayView, MainWindowViewModel> body)
    {
        var services = DotCharter.UI.App.Services!;
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<ProjectState>();
        var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel,
            state.TimelineFollowsPlayhead, state.TimelineViewAnchorMs, state.PixelsPerMs, vm.ChartEditor.ActiveVocalTrack,
            vm.SelectedMainTabIndex, vm.IsGameplayPresentation);
        foreach (var dialog in vm.Dialogs.AllDialogs)
            if (dialog.IsOpen) dialog.RequestClose();
        vm.CommandPalette.Close();
        vm.SelectedMainTabIndex = 0;
        vm.IsGameplayPresentation = false;
        var inline = vm.ShellDialogs.InlineEdit;
        var previousHost = inline.Host;
        var project = new ChartProject();
        var arrangement = new Arrangement();
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        vm.ChartEditor.ApplyProject(project, true);
        state.TimelineFollowsPlayhead = false;
        state.TimelineViewAnchorMs = 0;
        state.PixelsPerMs = 0.2;
        var renderer = new ChartRenderer { Editor = vm.ChartEditor, PixelsPerMs = state.PixelsPerMs, Focusable = true };
        renderer.CurrentRibbon!.ShowLyrics = true;
        var view = new InlineEditOverlayView { DataContext = inline, Renderer = renderer };
        var panel = new Panel();
        panel.Children.Add(renderer);
        panel.Children.Add(view);
        var window = NarrowWidthLayout.NewWindow(900, 360, panel);
        using var manager = vm.InteractionFactory.CreateManager(vm, renderer);
        window.Show();
        try
        {
            NarrowWidthLayout.Settle(window);
            body(window, renderer, view, vm);
        }
        finally
        {
            inline.Lyric.Cancel();
            vm.InteractionFactory.History.Clear();
            window.Close();
            state.ActiveProject = previous.ActiveProject;
            state.ActiveArrangement = previous.ActiveArrangement;
            state.ActiveLevel = previous.ActiveLevel;
            state.TimelineFollowsPlayhead = previous.TimelineFollowsPlayhead;
            state.TimelineViewAnchorMs = previous.TimelineViewAnchorMs;
            state.PixelsPerMs = previous.PixelsPerMs;
            vm.ChartEditor.ActiveVocalTrack = previous.ActiveVocalTrack;
            vm.SelectedMainTabIndex = previous.SelectedMainTabIndex;
            vm.IsGameplayPresentation = previous.IsGameplayPresentation;
            inline.Host = previousHost;
        }
    }

    [Theory]
    [InlineData(1500, 3)]
    [InlineData(2500, 5)]
    public Task MovingASyllableResizesItsNeighboursInOneUndoStep(double destinationMs, int destinationBeat) =>
        HeadlessSessionFixture.Dispatch(() => WithTimeline((window, renderer, _, vm) =>
        {
            var (previous, moved, next) = AddLine(vm);
            var history = vm.InteractionFactory.History;
            var config = new FakeConfigurationService();
            var gap = ChartSpacingEnforcer.GetMinDistance(moved.Position, vm.ChartEditor.Beats, config.AppConfig);
            var initial = (moved.Position, previous.EndPosition, moved.EndPosition);
            var drag = new LyricDragGesture(history, config, new FakeAppLogger());
            var context = DragContext(vm, renderer, destinationMs);
            vm.ProjectState.TimelineViewAnchorMs = 1800;
            LyricTimelineCapture.Capture(window, renderer, vm, $"733-move-{destinationBeat}-before", expectedPhraseCount: 1, expectedWordCount: 3);

            drag.Start(context.Track, moved, 2000, default);
            drag.OnMoved(context);
            drag.Commit(context);
            LyricTimelineCapture.Capture(window, renderer, vm, $"733-move-{destinationBeat}-after", expectedPhraseCount: 1, expectedWordCount: 3);

            Assert.Equal(new FractionalPosition(destinationBeat), moved.Position);
            Assert.Equal(moved.Position - gap, previous.EndPosition);
            Assert.Equal(next.Position - gap, moved.EndPosition);
            Assert.Single(history.GetUndoSteps());
            history.Undo();
            Assert.Equal(initial, (moved.Position, previous.EndPosition, moved.EndPosition));
            history.Redo();
            Assert.Equal(moved.Position - gap, previous.EndPosition);
            Assert.Equal(next.Position - gap, moved.EndPosition);
        }), CancellationToken.None);

    [Fact]
    public Task DragBoundsKeepThePreviousSyllableVisibleAndCancelRestoresIt() =>
        HeadlessSessionFixture.Dispatch(() => WithTimeline((_, renderer, _, vm) =>
        {
            var (previous, moved, _) = AddLine(vm);
            var history = vm.InteractionFactory.History;
            var config = new FakeConfigurationService();
            var gap = ChartSpacingEnforcer.GetMinDistance(previous.Position, vm.ChartEditor.Beats, config.AppConfig);
            var initial = (moved.Position, previous.EndPosition, moved.EndPosition);
            var drag = new LyricDragGesture(history, config, new FakeAppLogger());
            var context = DragContext(vm, renderer, 1000);

            drag.Start(context.Track, moved, 2000, default);
            drag.OnMoved(context);

            Assert.True(previous.EndPosition > previous.Position);
            Assert.Equal(moved.Position - gap, previous.EndPosition);
            drag.Cancel(context);
            Assert.Equal(initial, (moved.Position, previous.EndPosition, moved.EndPosition));
            Assert.False(history.CanUndo);
        }), CancellationToken.None);

    [Fact]
    public Task WheelRepairsAnOverlapAndCanDeliberatelyShortenBeforeAMove() =>
        HeadlessSessionFixture.Dispatch(() => WithTimeline((_, renderer, _, vm) =>
        {
            var (previous, moved, next) = AddLine(vm);
            moved.EndPosition = next.Position + 1;
            var history = vm.InteractionFactory.History;
            var config = DotCharter.UI.App.Services!.GetRequiredService<IConfigurationService>();
            var gap = ChartSpacingEnforcer.GetMinDistance(moved.Position, vm.ChartEditor.Beats, config.LoadAppConfig());
            var wheel = vm.InteractionFactory.CreateWheelAdjuster();
            vm.ChartEditor.Selection.SelectedLyric = moved;

            Assert.Equal(WheelAdjustmentResult.Changed, wheel.TryAdjustLyricSustain(vm, -1));
            Assert.Equal(next.Position - gap, moved.EndPosition);
            Assert.Equal(WheelAdjustmentResult.Changed, wheel.TryAdjustLyricSustain(vm, -1));
            var shortened = moved.EndPosition - moved.Position;
            Assert.Single(history.GetUndoSteps());

            var drag = new LyricDragGesture(history, config, new FakeAppLogger());
            var context = DragContext(vm, renderer, 1500);
            drag.Start(context.Track, moved, 2000, default);
            drag.OnMoved(context);
            drag.Commit(context);

            Assert.Equal(new FractionalPosition(3), moved.Position);
            Assert.Equal(shortened, moved.EndPosition - moved.Position);
            Assert.Equal(moved.Position - gap, previous.EndPosition);
            Assert.Equal(WheelAdjustmentResult.Changed, wheel.TryAdjustLyricSustain(vm, 100));
            Assert.Equal(next.Position - gap, moved.EndPosition);
        }), CancellationToken.None);

    [Fact]
    public Task ClosingGapsRepairsRenderedLyrics() =>
        HeadlessSessionFixture.Dispatch(() => WithTimeline((window, renderer, _, vm) =>
        {
            var (early, overlaps, next) = AddLine(vm);
            early.EndPosition = new FractionalPosition(2, new Fraction(1, 4));
            overlaps.EndPosition = next.Position + new Fraction(1, 2);
            vm.ProjectState.TimelineViewAnchorMs = 1800;
            var services = DotCharter.UI.App.Services!;
            var config = services.GetRequiredService<IConfigurationService>().LoadAppConfig();
            var gap = ChartSpacingEnforcer.GetMinDistance(early.Position, vm.ChartEditor.Beats, config);
            LyricTimelineCapture.Capture(window, renderer, vm, "733-repair-before", expectedPhraseCount: 1, expectedWordCount: 3);

            services.GetRequiredService<LyricsInspectorViewModel>().CloseGapsCommand.Execute(null);
            LyricTimelineCapture.Capture(window, renderer, vm, "733-repair-after", expectedPhraseCount: 1, expectedWordCount: 3);

            Assert.Equal(overlaps.Position - gap, early.EndPosition);
            Assert.Equal(next.Position - gap, overlaps.EndPosition);
        }), CancellationToken.None);

    private static ToolContext DragContext(MainWindowViewModel vm, ChartRenderer renderer, double destinationMs) =>
        new(vm, renderer, vm.ChartEditor.TrackContexts)
        {
            Track = vm.ChartEditor.TrackContexts.GetOrCreate(vm.ProjectState.ActiveProject!.Arrangements[0]),
            Position = new Point(200, 40),
            ExactTimeMs = destinationMs
        };

    private static (LyricEvent Previous, LyricEvent Moved, LyricEvent Next) AddLine(MainWindowViewModel vm)
    {
        var previous = new LyricEvent { Position = new(2), EndPosition = new(4), Text = "fairy" };
        var moved = new LyricEvent { Position = new(4), EndPosition = new(5), Text = "tale" };
        var next = new LyricEvent { Position = new(6), EndPosition = new(7), Text = "ends", IsPhraseEnd = true };
        var track = new VocalTrack
        {
            Phrases = [new LyricPhrase { Position = new(2), EndPosition = new(7), Lyrics = [previous, moved, next] }]
        };
        vm.ProjectState.ActiveProject!.VocalTracks.Add(track);
        vm.ChartEditor.ActiveVocalTrack = track;
        return (previous, moved, next);
    }

    private static Point At(Window window, ChartRenderer renderer, MainWindowViewModel vm, double timeMs)
    {
        var state = vm.ProjectState;
        var bounds = ChartHitTester.GetViewportBounds(state.TimelineViewAnchorEffectiveMs, state.PixelsPerMs, renderer.Bounds.Width);
        return renderer.TranslatePoint(new Point((timeMs - bounds.StartMs) * state.PixelsPerMs,
            renderer.TopRibbon.LyricY + 4), window)!.Value;
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    [Fact]
    public Task Pointer_placement_and_keyboard_rename_measure_and_undo_as_two_gestures() =>
        HeadlessSessionFixture.Dispatch(() => WithTimeline((window, renderer, view, vm) =>
        {
            Assert.True(renderer.TopRibbon.ShowLyrics);
            var inline = vm.ShellDialogs.InlineEdit.Lyric;
            var click = At(window, renderer, vm, 1000);
            NarrowWidthLayout.Capture(window, "625-before-placement");
            window.MouseDown(click, MouseButton.Right);
            window.MouseUp(click, MouseButton.Right);
            NarrowWidthLayout.Settle(window);
            Assert.True(inline.IsOpen);
            Assert.Empty(vm.ChartEditor.VocalTracks);
            var card = view.FindControl<Border>("LyricEditorCard")!;
            var box = view.FindControl<TextBox>("LyricTextBox")!;
            Assert.Same(box, window.FocusManager!.GetFocusedElement());
            Assert.True(card.Bounds is { Width: > 1, Height: > 1 });
            Assert.True(box.Bounds is { Width: > 1, Height: > 1 });
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)));
            Assert.DoesNotContain(card, view.HiddenSurfaces());
            Assert.Equal(renderer.TopRibbon.LyricY, card.TranslatePoint(default, renderer)!.Value.Y, 1);
            box.Text = "sing-";
            NarrowWidthLayout.Capture(window, "625-after-placement-editor");
            Press(window, PhysicalKey.Enter);
            NarrowWidthLayout.Settle(window);
            Assert.Same(renderer, window.FocusManager!.GetFocusedElement());
            var track = Assert.Single(vm.ChartEditor.VocalTracks);
            var lyric = Assert.Single(Assert.Single(track.Phrases).Lyrics);
            Assert.Equal("sing", lyric.Text);
            // The track's only line ends here: the line end stays and the typed word part is dropped.
            Assert.False(lyric.IsWordPart);
            Assert.True(lyric.IsPhraseEnd);
            Assert.Same(lyric, vm.ChartEditor.Selection.SelectedLyric);

            renderer.Focus();
            Press(window, PhysicalKey.F2);
            NarrowWidthLayout.Settle(window);
            Assert.True(inline.IsOpen);
            Assert.Equal("sing", box.Text);
            NarrowWidthLayout.Capture(window, "625-after-rename-editor");
            box.Text = "song";
            Press(window, PhysicalKey.Enter);
            Assert.Equal("song", lyric.Text);
            Assert.False(lyric.IsWordPart);
            Assert.True(lyric.IsPhraseEnd);
            vm.InteractionFactory.History.Undo();
            Assert.Equal("sing", lyric.Text);
            Assert.False(lyric.IsWordPart);
            vm.InteractionFactory.History.Undo();
            Assert.Empty(vm.ChartEditor.VocalTracks);
            Assert.False(vm.InteractionFactory.History.CanUndo);
            vm.InteractionFactory.History.Redo();
            vm.InteractionFactory.History.Redo();
            Assert.Same(track, Assert.Single(vm.ChartEditor.VocalTracks));
            Assert.Equal("song", lyric.Text);
        }), CancellationToken.None);

    [Fact]
    public Task Focus_loss_cancels_the_draft_and_keeps_typing_in_the_destination() =>
        HeadlessSessionFixture.Dispatch(() => WithTimeline((window, renderer, view, vm) =>
        {
            var inline = vm.ShellDialogs.InlineEdit;
            inline.ShowLyric(1000, null);
            inline.Lyric.Text = "original";
            Assert.True(inline.Lyric.Commit());
            var lyric = vm.ChartEditor.Selection.SelectedLyric!;
            vm.InteractionFactory.History.Clear();
            var destination = new TextBox
            {
                Width = 200, Height = 36,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom
            };
            ((Panel)window.Content!).Children.Add(destination);
            renderer.Focus();
            Press(window, PhysicalKey.F2);
            NarrowWidthLayout.Settle(window);
            var box = view.FindControl<TextBox>("LyricTextBox")!;
            Assert.True(inline.Lyric.IsOpen);
            Assert.Same(box, window.FocusManager!.GetFocusedElement());
            Assert.True(box.Bounds is { Width: > 1, Height: > 1 });
            window.KeyTextInput("discard");
            var click = destination.TranslatePoint(new Point(20, 18), window)!.Value;
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            NarrowWidthLayout.Settle(window);
            window.KeyTextInput("typed here");
            Assert.Equal("typed here", destination.Text);
            Assert.Same(destination, window.FocusManager!.GetFocusedElement());
            Assert.False(renderer.IsFocused);
            Assert.False(inline.Lyric.IsOpen);
            Assert.Equal("original", lyric.Text);
            Assert.Same(lyric, Assert.Single(Assert.Single(vm.ChartEditor.ActiveVocalTrack!.Phrases).Lyrics));
            Assert.False(vm.InteractionFactory.History.CanUndo);
        }), CancellationToken.None);

    [Fact]
    public Task Populated_lyric_row_captures_keep_the_editor_inside_the_timeline() =>
        HeadlessSessionFixture.Dispatch(() => WithTimeline((window, renderer, view, vm) =>
        {
            var inline = vm.ShellDialogs.InlineEdit;
            foreach (var (time, text) in new[] { (0, "extraordinarily-"), (1250, "unforgettable") })
            {
                inline.ShowLyric(time, null);
                inline.Lyric.Text = text;
                Assert.True(inline.Lyric.Commit());
            }

            Assert.Equal(2, vm.ChartEditor.ActiveVocalTrack!.Phrases.Count);
            NarrowWidthLayout.Settle(window);
            LyricTimelineCapture.Capture(window, renderer, vm, "before", expectedPhraseCount: 2, expectedWordCount: 2);
            var click = At(window, renderer, vm, 750);
            window.MouseDown(click, MouseButton.Right);
            window.MouseUp(click, MouseButton.Right);
            NarrowWidthLayout.Settle(window);
            Assert.True(inline.Lyric.IsOpen);
            var box = view.FindControl<TextBox>("LyricTextBox")!;
            var card = view.FindControl<Border>("LyricEditorCard")!;
            Assert.Same(box, window.FocusManager!.GetFocusedElement());
            Assert.True(card.Bounds is { Width: > 1, Height: > 1 });
            Assert.True(box.DesiredSize is { Width: > 1, Height: > 1 });
            Assert.Equal(renderer.TopRibbon.LyricY, card.TranslatePoint(default, renderer)!.Value.Y, 1);
            var span = NarrowWidthLayout.Span(card, renderer);
            Assert.InRange(span.Left, 0, renderer.Bounds.Width - card.Bounds.Width);
            Assert.True(card.Bounds.Bottom <= renderer.Bounds.Height);
            LyricTimelineCapture.Capture(window, renderer, vm, "after-placement-draft", expectedPhraseCount: 2, expectedWordCount: 2);
            Press(window, PhysicalKey.Escape);
            NarrowWidthLayout.Settle(window);
            click = At(window, renderer, vm, 0);
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            NarrowWidthLayout.Settle(window);
            Assert.True(inline.Lyric.IsOpen);
            // A line end carries no word part, so the rename shows the syllable without its dash.
            Assert.Equal("extraordinarily", box.Text);
            Assert.Same(box, window.FocusManager!.GetFocusedElement());
            LyricTimelineCapture.Capture(window, renderer, vm, "after-rename", expectedPhraseCount: 2, expectedWordCount: 2);
        }), CancellationToken.None);

    [Fact]
    public Task Double_click_edits_and_tab_walks_while_escape_drops_the_draft() =>
        HeadlessSessionFixture.Dispatch(() => WithTimeline((window, renderer, view, vm) =>
        {
            var inline = vm.ShellDialogs.InlineEdit;
            foreach (var (time, text) in new[] { (1000, "one"), (2500, "two") })
            {
                inline.ShowLyric(time, null);
                inline.Lyric.Text = text;
                inline.Lyric.Commit();
            }

            vm.InteractionFactory.History.Clear();
            NarrowWidthLayout.Settle(window);
            var click = At(window, renderer, vm, 1000);
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            NarrowWidthLayout.Settle(window);
            Assert.True(inline.Lyric.IsOpen);
            var box = view.FindControl<TextBox>("LyricTextBox")!;
            Assert.Same(box, window.FocusManager!.GetFocusedElement());
            box.Text = "won";
            Press(window, PhysicalKey.Tab);
            NarrowWidthLayout.Settle(window);
            Assert.Equal("two", box.Text);
            Assert.Same(box, window.FocusManager!.GetFocusedElement());
            Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
            NarrowWidthLayout.Settle(window);
            Assert.Equal("won", box.Text);
            Assert.Same(box, window.FocusManager!.GetFocusedElement());
            box.Text = "discard";
            Press(window, PhysicalKey.Escape);
            Assert.False(inline.Lyric.IsOpen);
            NarrowWidthLayout.Settle(window);
            Assert.Same(renderer, window.FocusManager!.GetFocusedElement());
            Assert.Equal("won", vm.ChartEditor.Selection.SelectedLyric!.Text);
            renderer.Focus();
            Press(window, PhysicalKey.Enter);
            NarrowWidthLayout.Settle(window);
            Assert.True(inline.Lyric.IsOpen);
            Assert.Equal("won", box.Text);
            Press(window, PhysicalKey.Escape);
            Assert.False(inline.Lyric.IsOpen);
        }), CancellationToken.None);
}
