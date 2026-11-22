using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Localization;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class LyricPhraseEndUiPerfSmokeTests
{
    [Fact]
    public Task CloseGapsButtonMeasuresAndRepairsTheTrackWithOneUndo() =>
        HeadlessSessionFixture.Dispatch(() => WithInspector((rig, window, view) =>
        {
            var last = rig.First.Lyrics[0];
            last.Position = new(3);
            last.EndPosition = new(4);
            var first = new LyricEvent { Text = "large", Position = new(1), EndPosition = new(1, new Fraction(1, 4)) };
            var second = new LyricEvent { Text = "overlap", Position = new(2), EndPosition = new(4) };
            rig.First.Lyrics.Insert(0, second);
            rig.First.Lyrics.Insert(0, first);
            var otherPhrase = rig.Track.Phrases[1].Lyrics[0];
            var starts = new[] { first.Position, second.Position, last.Position, otherPhrase.Position };
            var ends = (first.EndPosition, second.EndPosition, last.EndPosition, otherPhrase.EndPosition);
            var before = rig.History.GetUndoSteps().Count;
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, "733-inspector-before-repair");
            var button = NarrowWidthLayout.Named<Button>(view, "LyricsCloseGaps");
            Assert.True(button.Bounds is { Width: > 0, Height: > 0 });

            button.Command!.Execute(button.CommandParameter);

            var gap = ChartSpacingEnforcer.GetMinDistance(first.Position, rig.Editor.Beats,
                App.Services!.GetRequiredService<IConfigurationService>().LoadAppConfig());
            Assert.Equal(starts, rig.Track.Phrases.SelectMany(p => p.Lyrics).Select(l => l.Position));
            Assert.Equal(second.Position - gap, first.EndPosition);
            Assert.Equal(last.Position - gap, second.EndPosition);
            Assert.Equal((ends.Item3, ends.Item4), (last.EndPosition, otherPhrase.EndPosition));
            Assert.Equal(before + 1, rig.History.GetUndoSteps().Count);
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, "733-inspector-after");
            rig.History.Undo();
            Assert.Equal(ends, (first.EndPosition, second.EndPosition, last.EndPosition, otherPhrase.EndPosition));
        }), CancellationToken.None);

    [Fact]
    public async Task TheAttachedCheckbox_JoinsLinesAndUndoRestoresItsValue() =>
        await HeadlessSessionFixture.Dispatch(() => WithInspector((rig, window, view) =>
        {
            var checkbox = view.GetVisualDescendants().OfType<ToggleButton>().Single(c =>
                AutomationProperties.GetName(c) == rig.Localization["Inspector_PhraseEnd"]);
            Assert.True(view.Bounds is { Width: > 0, Height: > 0 });
            Assert.True(checkbox.Bounds is { Width: > 0, Height: > 0 });
            Assert.True(checkbox.IsChecked);
            NarrowWidthLayout.Capture(window, "624-before");

            checkbox.IsChecked = false;
            NarrowWidthLayout.Settle(window);

            Assert.Single(rig.Track.Phrases);
            Assert.Equal(["e", "Sa"], rig.First.Lyrics.Select(l => l.Text));
            Assert.False(checkbox.IsChecked);
            NarrowWidthLayout.Capture(window, "624-after");
            rig.History.Undo();
            NarrowWidthLayout.Settle(window);
            Assert.Equal(2, rig.Track.Phrases.Count);
            Assert.True(checkbox.IsChecked);
        }), CancellationToken.None);

    /// <summary>
    ///     The selected syllable's box is a draft until Enter or lost focus: typing "x-ray"
    ///     one key at a time must not join the lines at "x-", and only a COMMITTED trailing
    ///     hyphen does.
    /// </summary>
    [Fact]
    public async Task TheTextBox_ActsOnATrailingHyphenOnlyWhenTheEditIsCommitted() =>
        await HeadlessSessionFixture.Dispatch(() => WithInspector((rig, window, view) =>
        {
            var box = NarrowWidthLayout.Named<TextBox>(view, "LyricsSelectedTextBox");
            Assert.True(box.Bounds is { Width: > 0, Height: > 0 });
            var lyric = rig.First.Lyrics[0];
            box.Focus();
            box.SelectAll();
            foreach (var key in "x-ray")
            {
                window.KeyTextInput(key.ToString());
                NarrowWidthLayout.Settle(window);
                Assert.Equal(2, rig.Track.Phrases.Count);
                Assert.Equal("e", lyric.Text);
            }
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            NarrowWidthLayout.Settle(window);

            Assert.Equal("x-ray", lyric.Text);
            Assert.False(lyric.IsWordPart);
            Assert.Equal(2, rig.Track.Phrases.Count);
            Assert.Equal("x-ray", box.Text);

            box.SelectAll();
            window.KeyTextInput("hel-");
            NarrowWidthLayout.Settle(window);
            Assert.Equal(2, rig.Track.Phrases.Count);
            NarrowWidthLayout.Named<Button>(view, "LyricsSelectedDelete").Focus();
            NarrowWidthLayout.Settle(window);

            Assert.Equal("hel", lyric.Text);
            Assert.True(lyric.IsWordPart);
            Assert.Single(rig.Track.Phrases);
            Assert.Equal("hel", box.Text);
            rig.History.Undo();
            Assert.Equal("x-ray", lyric.Text);
            Assert.Equal(2, rig.Track.Phrases.Count);
        }), CancellationToken.None);

    /// <summary>
    ///     Ctrl+S, an export shortcut or Alt+F4 move no focus, so the real box raises no LostFocus:
    ///     the editor's <c>CommitPendingEdits</c> (what save, export and close call first) applies
    ///     the typed draft as ONE history entry, and the focus leaving afterwards adds none.
    /// </summary>
    [Fact]
    public async Task ADraftInTheFocusedBox_IsAppliedByTheEditorSeamAsOneEntry() =>
        await HeadlessSessionFixture.Dispatch(() => WithInspector((rig, window, view) =>
        {
            var box = NarrowWidthLayout.Named<TextBox>(view, "LyricsSelectedTextBox");
            var lyric = rig.First.Lyrics[0];
            box.Focus();
            box.SelectAll();
            window.KeyTextInput("hey");
            NarrowWidthLayout.Settle(window);
            Assert.Equal("e", lyric.Text);
            var before = rig.History.GetUndoSteps().Count;

            rig.Editor.CommitPendingEdits();
            NarrowWidthLayout.Settle(window);

            Assert.True(box.IsFocused);
            Assert.Equal("hey", lyric.Text);
            Assert.Equal("hey", box.Text);
            Assert.Equal(before + 1, rig.History.GetUndoSteps().Count);
            var node = rig.History.CurrentNodeId;
            NarrowWidthLayout.Named<Button>(view, "LyricsSelectedDelete").Focus();
            NarrowWidthLayout.Settle(window);
            Assert.Equal(node, rig.History.CurrentNodeId);
            rig.History.Undo();
            Assert.Equal("e", lyric.Text);
        }), CancellationToken.None);

    /// <summary>Switching the inspector tab only HIDES the Lyrics tab (an <c>IsVisible</c> flip), so
    /// leaving it commits the draft from the inspector's view model.</summary>
    [Fact]
    public async Task LeavingTheLyricsTab_AppliesTheDraft() =>
        await HeadlessSessionFixture.Dispatch(() => WithInspector((rig, window, view) =>
        {
            var inspector = App.Services!.GetRequiredService<InspectorViewModel>();
            var previous = inspector.SelectedTabIndex;
            inspector.SelectedTabIndex = 3; // the Lyrics tab
            Assert.True(inspector.IsLyricsTabSelected);
            var lyric = rig.First.Lyrics[0];
            try
            {
                Assert.Same(view.DataContext, inspector.Lyrics);
                inspector.Lyrics.SelectedLyricText = "yo";
                Assert.Equal("e", lyric.Text);

                inspector.SelectedTabIndex = InspectorViewModel.SoundTabIndex;

                Assert.Equal("yo", lyric.Text);
            }
            finally
            {
                inspector.SelectedTabIndex = previous;
            }
        }), CancellationToken.None);

    private static void WithInspector(Action<Rig, Window, LyricsInspectorView> body)
    {
        var services = App.Services!;
        var state = services.GetRequiredService<ProjectState>();
        var editor = services.GetRequiredService<ChartEditorViewModel>();
        var selection = services.GetRequiredService<ISelectionService>();
        var inspector = services.GetRequiredService<LyricsInspectorViewModel>();
        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        var previousChart = editor.CurrentChart;
        var previousTrack = editor.ActiveVocalTrack;
        var project = new ChartProject();
        for (var i = 0; i < 32; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var first = Phrase(1, "e");
        var track = new VocalTrack { Phrases = [first, Phrase(4, "Sa")] };
        project.VocalTracks.Add(track);
        state.ActiveProject = project;
        editor.CurrentChart = project.Chart;
        editor.ActiveVocalTrack = track;
        selection.SelectedLyric = first.Lyrics[0];
        var view = new LyricsInspectorView { DataContext = inspector };
        view.ShowCard("LyricsSyllableCard");
        var window = NarrowWidthLayout.NewWindow(560, 960, view);
        window.Show();
        try
        {
            NarrowWidthLayout.Settle(window);
            body(new Rig(services.GetRequiredService<IHistoryService>(), services.GetRequiredService<ILocalizationService>(),
                editor, track, first), window, view);
        }
        finally
        {
            selection.ClearSelections();
            window.Close();
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
            editor.CurrentChart = previousChart;
            editor.ActiveVocalTrack = previousTrack;
        }
    }

    private sealed record Rig(IHistoryService History, ILocalizationService Localization, ChartEditorViewModel Editor,
        VocalTrack Track, LyricPhrase First);

    private static LyricPhrase Phrase(int beat, string text) => new()
    {
        Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1),
        Lyrics = [new LyricEvent
        {
            Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1),
            Text = text, IsPhraseEnd = true
        }]
    };
}
