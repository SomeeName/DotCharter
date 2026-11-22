using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Core.Domain.Vocals;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.Input.Tools;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views;

namespace DotCharter.Tests.Perf;

/// <summary>Real pointer and keyboard routes preserve heads, release positions and discrete toggles.</summary>
[Collection("HeadlessUI")]
public sealed class TimelineInputRoutingUiPerfSmokeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static Point At(MainWindow window, ChartRenderer renderer, double x, double y) =>
        renderer.TranslatePoint(new Point(x, y), window)!.Value;

    private static void Prepare(ChartRenderer renderer, MainWindowViewModel vm)
    {
        Assert.True(renderer.Bounds is { Width: > 1, Height: > 1 });
        vm.ChartEditor.Notes.Clear();
        vm.ChartEditor.Chords.Clear();
        vm.ChartEditor.HandShapes.Clear();
        vm.ChartEditor.Anchors.Clear();
        vm.ChartEditor.Selection.ClearAll();
        vm.InteractionFactory.History.Clear();
        vm.ChartEditor.Beats.Clear();
        for (var i = 0; i < 32; i++)
            vm.ChartEditor.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        vm.ProjectState.TimelineFollowsPlayhead = false;
        vm.ProjectState.PixelsPerMs = 1;
        vm.ProjectState.TimelineViewAnchorMs = 450;
        renderer.CurrentRibbon!.ShowLyrics = true;
    }

    private static void SetMoveTicks(MainWindow window, long ticks)
    {
        var manager = typeof(MainWindow).GetField("_interactionManager", PrivateInstance)!.GetValue(window)!;
        var coordinator = manager.GetType().GetField("_coordinator", PrivateInstance)!.GetValue(manager)!;
        typeof(ToolCoordinator).GetField("_lastMoveTicks", PrivateInstance)!.SetValue(coordinator, ticks);
    }

    private static void Throttle(MainWindow window) => SetMoveTicks(window, long.MaxValue);

    [Fact]
    public Task ShiftRightClickOnAHandShapeOpensItsHeldGripsMenu() =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            Prepare(renderer, vm);
            var bar = new HandShape { Position = new(0), EndPosition = new(4) };
            vm.ChartEditor.HandShapes.Add(bar);
            var laneBottom = renderer.ViewportHeight - renderer.BottomMargin;
            var point = At(window, renderer, 500, laneBottom + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset + 2);

            window.MouseDown(point, MouseButton.Right, RawInputModifiers.Shift);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.Shift);
            NarrowWidthLayout.Settle(window);

            var header = DotCharter.Localization.LocalizationService.Instance["Menu_GripsFromHeldNotes"];
            var popupRows = window.GetVisualDescendants().OfType<OverlayPopupHost>()
                .SelectMany(host => host.GetVisualDescendants()).OfType<MenuItem>();
            var row = Assert.Single(popupRows, item => Equals(item.Header, header) && item.IsEffectivelyVisible);
            Assert.True(row.Bounds is { Width: > 1, Height: > 1 });
            var letRing = DotCharter.Localization.LocalizationService.Instance["Menu_HandShapesFromLetRing"];
            var create = Assert.Single(popupRows, item => Equals(item.Header, letRing) && item.IsEffectivelyVisible);
            Assert.True(create.Bounds is { Width: > 1, Height: > 1 });
            Assert.False(create.IsEnabled);
            Assert.Same(bar, Assert.Single(vm.ChartEditor.Selection.SelectedHandShapes));
        }), CancellationToken.None);

    [Fact]
    public Task ShiftRightClickOnASustainedNoteOffersAndRunsTheLetRingCommand() =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            Prepare(renderer, vm);
            var note = new GuitarNote { Position = new(1), StringIndex = 2, Fret = 7, Sustain = new(1) };
            vm.ChartEditor.Notes.Add(note);
            NarrowWidthLayout.Settle(window);
            var y = ChartHitTester.GetLaneY(renderer.Bounds.Height, renderer.TopMargin, renderer.BottomMargin, 6, 2,
                vm.ProjectState.InvertStrings);
            var point = At(window, renderer, 500, y);

            window.MouseDown(point, MouseButton.Right, RawInputModifiers.Shift);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.Shift);
            NarrowWidthLayout.Settle(window);

            var header = DotCharter.Localization.LocalizationService.Instance["Menu_HandShapesFromLetRing"];
            var rows = window.GetVisualDescendants().OfType<OverlayPopupHost>()
                .SelectMany(host => host.GetVisualDescendants()).OfType<MenuItem>();
            var row = Assert.Single(rows, item => Equals(item.Header, header) && item.IsEffectivelyVisible);
            Assert.True(row.Bounds is { Width: > 1, Height: > 1 });
            Assert.True(row.IsEnabled);
            row.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var shape = Assert.Single(vm.ChartEditor.HandShapes);
            Assert.Equal(note.Position, shape.Position);
            Assert.Equal(note.EndPosition, shape.EndPosition);
        }), CancellationToken.None);

    [Fact]
    public Task DelegatedEscapeSearchClearDoesNotNavigateOnRepeat() =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            vm.OpenPlayModeCommand.Execute(null);
            vm.PlayMode.CurrentScreen = PlayModeScreen.SongSelect;
            NarrowWidthLayout.Settle(window);
            var box = window.GetVisualDescendants().OfType<TextBox>()
                .First(b => b.IsEffectivelyVisible && SearchBoxKeys.GetClearOnEscape(b));
            box.Text = "synthetic";
            Assert.True(box.Focus());
            vm.KeybindHandler.ResetHeldKeys();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Equal("", box.Text);
            Assert.True(vm.PlayMode.IsSongSelectScreen);
            Assert.True(vm.IsPlayModeVisible);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(vm.PlayMode.IsSongSelectScreen);
            Assert.True(vm.IsPlayModeVisible);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(vm.IsHomeVisible);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        }), CancellationToken.None);

    [Fact]
    public Task DelegatedEscapeInlineCancelDoesNotClearSelectionOnRepeat() =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            var note = vm.ChartEditor.Notes[0];
            vm.ChartEditor.Selection.ClearAll();
            vm.ChartEditor.Selection.SelectedNotes.Add(note);
            vm.ShellDialogs.InlineEdit.ShowFhp(2000, null);
            NarrowWidthLayout.Settle(window);
            var box = window.GetVisualDescendants().OfType<DotCharter.UI.Views.Editor.InlineEditOverlayView>()
                .First(v => v.IsEffectivelyVisible).FindControl<TextBox>("FhpFretBox")!;
            Assert.True(box.Focus());
            vm.KeybindHandler.ResetHeldKeys();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(vm.ShellDialogs.InlineEdit.Fhp.IsOpen);
            Assert.Contains(note, vm.ChartEditor.Selection.SelectedNotes);
            Assert.True(renderer.Focus());
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Contains(note, vm.ChartEditor.Selection.SelectedNotes);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Empty(vm.ChartEditor.Selection.SelectedNotes);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        }), CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnchangedRulerReleaseDoesNotSeekAgain(bool droppedMove) =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            Prepare(renderer, vm);
            var field = vm.Transport.GetType().GetField("_audioService", PrivateInstance)!;
            var originalAudio = field.GetValue(vm.Transport);
            var audio = new FakeAudioPlaybackService();
            field.SetValue(vm.Transport, audio);
            try
            {
                var y = renderer.Bounds.Height - 8;
                window.MouseDown(At(window, renderer, 500, y), MouseButton.Left);
                // Reset the ruler's pan after the press's deliberate navigation.
                vm.ProjectState.TimelineFollowsPlayhead = false;
                vm.ProjectState.TimelineViewAnchorMs = 450;
                // A fresh press permits the first move without waiting on a clock.
                SetMoveTicks(window, 0);
                window.MouseMove(At(window, renderer, 800, y), RawInputModifiers.LeftMouseButton);
                Assert.Equal(2, audio.SeekHistory.Count);
                Throttle(window);
                if (droppedMove)
                    window.MouseMove(At(window, renderer, 850, y), RawInputModifiers.LeftMouseButton);
                window.MouseUp(At(window, renderer, 800, y + 2), MouseButton.Left);
                Assert.Equal(2, audio.SeekHistory.Count);
                Assert.Equal(800, audio.LastSeekToMs);
            }
            finally
            {
                field.SetValue(vm.Transport, originalAudio);
            }
        }), CancellationToken.None);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public Task AHeadWinsOverEveryOverlappingTail(bool chordTail, bool chordHead, bool inverted) =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            Prepare(renderer, vm);
            vm.ProjectState.InvertStrings = inverted;
            var beats = vm.ChartEditor.Beats;
            var tailPosition = TimelineMath.PositionFromMs(300, beats);
            var headPosition = TimelineMath.PositionFromMs(600, beats);
            var sustain = headPosition - tailPosition;
            var tailNote = new GuitarNote { Position = tailPosition, Sustain = sustain, StringIndex = 2, Fret = 5 };
            var tailChord = new Chord { Position = tailPosition };
            tailChord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5, Sustain = sustain, LinkNext = true });
            var note = new GuitarNote { Position = headPosition, StringIndex = 2, Fret = 7 };
            var chord = new Chord { Position = headPosition };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
            if (chordTail) vm.ChartEditor.Chords.Add(tailChord);
            else vm.ChartEditor.Notes.Add(tailNote);
            if (chordHead) vm.ChartEditor.Chords.Add(chord);
            else vm.ChartEditor.Notes.Add(note);
            NarrowWidthLayout.Settle(window);
            var y = ChartHitTester.GetLaneY(renderer.Bounds.Height, renderer.TopMargin, renderer.BottomMargin, 6, 2, inverted);
            var point = At(window, renderer, 595, y);
            SetMoveTicks(window, 0);
            window.MouseMove(point);
            Assert.Same(chordHead ? chord : null, vm.ChartEditor.Selection.HoveredChord);
            Assert.Same(chordHead ? null : note, vm.ChartEditor.Selection.HoveredNote);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            if (chordHead)
            {
                Assert.Same(chord, Assert.Single(vm.ChartEditor.Selection.SelectedChords));
                Assert.Empty(vm.ChartEditor.Selection.SelectedNotes);
            }
            else
            {
                Assert.Same(note, Assert.Single(vm.ChartEditor.Selection.SelectedNotes));
                Assert.Empty(vm.ChartEditor.Selection.SelectedChords);
            }
        }), CancellationToken.None);

    [Theory]
    [InlineData("ruler", 800)]
    [InlineData("beat", 800)]
    [InlineData("lyric", 800)]
    [InlineData("event", 501)]
    public Task ThrottledGestureEndsAtRelease(string gesture, double moveX) =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            Prepare(renderer, vm);
            var beats = vm.ChartEditor.Beats;
            var original = TimelineMath.PositionFromMs(500, beats);
            var lyric = new LyricEvent { Position = original, EndPosition = TimelineMath.PositionFromMs(700, beats), Text = "la" };
            var vocals = new VocalTrack { Phrases = [new LyricPhrase { Lyrics = [lyric] }] };
            vm.ProjectState.ActiveProject!.VocalTracks.Add(vocals);
            vm.ChartEditor.ActiveVocalTrack = vocals;
            var marker = new EventPoint { Position = original, Section = "verse" };
            vm.ProjectState.ActiveArrangement!.EventPoints.Clear();
            vm.ProjectState.ActiveArrangement.EventPoints.Add(marker);
            NarrowWidthLayout.Settle(window);
            var y = gesture switch
            {
                "ruler" => renderer.Bounds.Height - 8,
                "lyric" => renderer.TopRibbon.LyricY + 4,
                "event" => renderer.TopRibbon.SectionY + 4,
                _ => renderer.TopRibbon.MetadataY + 4
            };
            window.MouseDown(At(window, renderer, 500, y), MouseButton.Left);
            var releaseTime = ChartHitTester.GetViewportBounds(vm.ProjectState.TimelineViewAnchorEffectiveMs,
                vm.ProjectState.PixelsPerMs, renderer.Bounds.Width).StartMs + 900;
            Throttle(window);
            window.MouseMove(At(window, renderer, moveX, y), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(window, renderer, 900, y), MouseButton.Left);
            var actual = gesture switch
            {
                "ruler" => vm.ProjectState.CurrentTimeMs,
                "beat" => beats[1].Time,
                "lyric" => lyric.Position.GetPositionMs(beats),
                _ => Assert.Single(vm.ProjectState.ActiveArrangement.EventPoints).Position.GetPositionMs(beats)
            };
            // Grid gestures snap 900 ms to the editor grid; the transport and beat are continuous.
            var expected = gesture is "lyric" or "event"
                ? vm.ChartEditor.SnapLyricStartMs(releaseTime).GetPositionMs(beats)
                : releaseTime;
            Assert.Equal(expected, actual, 0.01);
            if (gesture != "ruler") Assert.Equal(2, vm.InteractionFactory.History.GetTree().Count);
        }), CancellationToken.None);

    [Theory]
    [InlineData("note")]
    [InlineData("chord")]
    [InlineData("anchor")]
    [InlineData("handshape")]
    [InlineData("draw")]
    [InlineData("marquee")]
    public Task OtherThrottledToolsEndAtRelease(string gesture) =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            Prepare(renderer, vm);
            var beats = vm.ChartEditor.Beats;
            var original = TimelineMath.PositionFromMs(500, beats);
            var note = new GuitarNote { Position = original, StringIndex = 2, Fret = 7 };
            var chord = new Chord { Position = original };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
            if (gesture == "note") vm.ChartEditor.Notes.Add(note);
            if (gesture == "chord") vm.ChartEditor.Chords.Add(chord);
            if (gesture == "anchor") vm.ChartEditor.Anchors.Add(new FretAnchor { Position = original, Fret = 5, Width = 4 });
            if (gesture == "handshape") vm.ChartEditor.HandShapes.Add(new HandShape { Position = original, EndPosition = new FractionalPosition(2) });
            if (gesture is "draw" or "marquee")
                vm.ChartEditor.Notes.Add(new GuitarNote { Position = TimelineMath.PositionFromMs(gesture == "draw" ? 600 : 875, beats), StringIndex = 2, Fret = 7 });
            NarrowWidthLayout.Settle(window);
            var laneBottom = renderer.Bounds.Height - renderer.BottomMargin;
            var y = gesture switch
            {
                "anchor" => laneBottom + ChartEditorConstants.RenderMetrics.FhpYOffset + 2,
                "handshape" or "draw" => laneBottom + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset + 2,
                _ => ChartHitTester.GetLaneY(renderer.Bounds.Height, renderer.TopMargin, renderer.BottomMargin, 6, 2, vm.ProjectState.InvertStrings)
            };
            var button = gesture == "draw" ? MouseButton.Right : MouseButton.Left;
            window.MouseDown(At(window, renderer, 500, y), button);
            Throttle(window);
            window.MouseMove(At(window, renderer, 800, y), button == MouseButton.Left ? RawInputModifiers.LeftMouseButton : RawInputModifiers.RightMouseButton);
            window.MouseUp(At(window, renderer, 900, y), button);
            var expected = vm.ChartEditor.SnapToGrid(900).GetPositionMs(beats);
            var actual = gesture switch
            {
                "note" => Assert.Single(vm.ChartEditor.Notes).Position.GetPositionMs(beats),
                "chord" => Assert.Single(vm.ChartEditor.Chords).Position.GetPositionMs(beats),
                "anchor" => Assert.Single(vm.ChartEditor.Anchors).Position.GetPositionMs(beats),
                "handshape" => Assert.Single(vm.ChartEditor.HandShapes).Position.GetPositionMs(beats),
                "draw" => Assert.Single(vm.ChartEditor.HandShapes).EndPosition.GetPositionMs(beats),
                _ => Assert.Single(vm.ChartEditor.Selection.SelectedNotes).Position.GetPositionMs(beats)
            };
            Assert.Equal(expected, actual, 0.01);
            Assert.Equal(gesture == "marquee" ? 1 : 2, vm.InteractionFactory.History.GetTree().Count);
            Assert.Equal(0, renderer.ContinuousRenderRequests);
        }), CancellationToken.None);

    [Theory]
    [InlineData(Key.F11, RawInputModifiers.None)]
    [InlineData(Key.Enter, RawInputModifiers.Alt)]
    public Task HeldFullscreenTogglesOnceAndReleaseRearms(Key key, RawInputModifiers modifiers) =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            renderer.Focus();
            vm.KeybindHandler.ResetHeldKeys();
            var before = window.WindowState;
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            var after = window.WindowState;
            Assert.NotEqual(before, after);
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Assert.Equal(after, window.WindowState);
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Assert.Equal(before, window.WindowState);
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        }), CancellationToken.None);

    [Theory]
    [InlineData("OpenMixPopover")]
    [InlineData("OpenToolsPopover")]
    [InlineData("OpenTracksPopover")]
    public Task HeldPopoverTogglesOnceAndReleaseRearms(string verb) =>
        HeadlessSessionFixture.Dispatch(() => TimelineMiddlePanUiPerfSmokeTests.WithShell((window, renderer, vm) =>
        {
            var property = vm.ProjectState.Keybinds.GetType().GetProperty(verb)!;
            var original = property.GetValue(vm.ProjectState.Keybinds);
            var requests = 0;
            void Requested(TransportPopover _) => requests++;
            vm.PopoverRequested += Requested;
            try
            {
                property.SetValue(vm.ProjectState.Keybinds, new KeyChord(Key.F24));
                vm.KeybindHandler.RefreshBinds();
                vm.KeybindHandler.ResetHeldKeys();
                renderer.Focus();
                window.KeyPress(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
                Assert.True(renderer.Focus());
                window.KeyPress(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
                Assert.Equal(1, requests);
                window.KeyRelease(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
                renderer.Focus();
                window.KeyPress(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
                Assert.Equal(2, requests);
                window.KeyRelease(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
            }
            finally
            {
                vm.PopoverRequested -= Requested;
                property.SetValue(vm.ProjectState.Keybinds, original);
                vm.KeybindHandler.ResetHeldKeys();
                vm.KeybindHandler.RefreshBinds();
            }
        }), CancellationToken.None);
}
