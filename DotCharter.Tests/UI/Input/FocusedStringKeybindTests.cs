using Avalonia.Input;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Pen;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.Editing.Highway;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.Input;

public sealed class FocusedStringKeybindTests
{
    [Fact]
    public void EntryAndMaskDigitsRefuseStringsOutsideTheFocusedBass()
    {
        using var h = new Harness();

        Assert.True(h.Keys.HandleKeyPress(Key.D6, false, false, false));
        Assert.True(h.Keys.HandleKeyPress(Key.D6, true, false, false));

        Assert.Empty(h.Focused.Levels[0].Notes);
        Assert.Empty(h.Edit.Arrangement.Levels[0].Notes);
        Assert.True(h.Edit.State.StringEditMask.IsEmpty);
        Assert.False(h.Edit.History.CanUndo);

        var before = FocusedHighwayEditingTests.ChartBytes(h.Edit.State.ActiveProject!);
        Assert.True(h.Keys.HandleKeyPress(Key.D4, false, false, false));
        Assert.Equal(3, Assert.Single(h.Focused.Levels[0].Notes).StringIndex);
        AssertUndoRedo(h, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StringMovesStayInsideTheFocusedBass(bool samePitch)
    {
        using var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 3, Fret = 7 };
        h.Focused.Levels[0].Notes.Add(note);
        h.Edit.Selection.SelectedNotes.Add(note);

        Assert.True(h.Keys.HandleVerb(samePitch
            ? nameof(KeybindConfig.StringDownSamePitch)
            : nameof(KeybindConfig.StringDown)));

        Assert.Equal(3, note.StringIndex);
        Assert.Equal(7, note.Fret);
    }

    [Fact]
    public void SwitchingFocusAfterRegistrationUsesTheNewTracksBounds()
    {
        using var h = new Harness();
        h.Tracks.Focus(h.Edit.Arrangement);
        var before = FocusedHighwayEditingTests.ChartBytes(h.Edit.State.ActiveProject!);

        Assert.True(h.Keys.HandleKeyPress(Key.D6, false, false, false));

        Assert.Equal(5, Assert.Single(h.Edit.Arrangement.Levels[0].Notes).StringIndex);
        Assert.Empty(h.Focused.Levels[0].Notes);
        AssertUndoRedo(h, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StringMovesOnTheFocusedTrackRestoreTheExactChart(bool samePitch)
    {
        using var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 2, Fret = 7 };
        h.Focused.Levels[0].Notes.Add(note);
        h.Edit.Selection.SelectedNotes.Add(note);
        var before = FocusedHighwayEditingTests.ChartBytes(h.Edit.State.ActiveProject!);

        Assert.True(h.Keys.HandleVerb(samePitch
            ? nameof(KeybindConfig.StringDownSamePitch)
            : nameof(KeybindConfig.StringDown)));

        Assert.Equal(3, note.StringIndex);
        AssertUndoRedo(h, before);
    }

    private static void AssertUndoRedo(Harness h, byte[] before)
    {
        var after = FocusedHighwayEditingTests.ChartBytes(h.Edit.State.ActiveProject!);
        h.Edit.History.Undo();
        Assert.Equal(before, FocusedHighwayEditingTests.ChartBytes(h.Edit.State.ActiveProject!));
        h.Edit.History.Redo();
        Assert.Equal(after, FocusedHighwayEditingTests.ChartBytes(h.Edit.State.ActiveProject!));
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var config = new FakeConfigurationService();
            config.AppConfig.UseFastChartingInput = true;
            Edit.State.Keybinds = new KeybindConfig();
            Edit.State.InvertStrings = false;
            Edit.State.ActiveProject!.Arrangements.Add(Focused);
            Focused.Levels.Add(new ChartLevel());
            Tracks = new TrackContextRegistry(Edit.State, Edit.Selection);
            Edit.Scope.SetResolver(() => Tracks.FocusedTrack is { Level: { } level } focused
                ? new EditingTarget(focused.Arrangement, level)
                : null);
            var tempo = new TempoService(logger);
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), Edit.State, config,
                FakeLocalization.KeysOnly());
            var editor = new ChartEditorViewModel(Edit.State, transport, Edit.History, Edit.Selection, tempo,
                Tracks, FakeLocalization.KeysOnly());
            var targets = new EditTargetService(Edit.Selection, Edit.Scope, config);
            var pen = new ChartPenService(Edit.State, Edit.Scope, Edit.History, Edit.Selection,
                new StrictCollisionService(), tempo, Edit.Service, targets, config, logger);
            Keys = new ChartKeybindHandler(() => false, logger);
            // Like RegistrarKeybindReplacementTests, capture unused collaborators.
            // These dispatches reach only the real pen and highway services.
            var registrar = new AppKeybindRegistrar(Keys, Edit.State, editor, transport, null!, Edit.Service, pen,
                config, null!, null!, null!, null!, null!, null!, new MatchPatternStore(config));
            registrar.RegisterAll();
            Keys.RefreshBinds();
            Tracks.Focus(Focused);
        }

        public ChordKeyboardMutationTests.Harness Edit { get; } = new();
        public Arrangement Focused { get; } = new() { Tuning = ["E", "A", "D", "G"] };
        public TrackContextRegistry Tracks { get; }
        public ChartKeybindHandler Keys { get; }

        public void Dispose()
        {
            Tracks.Dispose();
            Edit.Service.Dispose();
        }
    }
}
