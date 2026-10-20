using DotCharter.Services.Editing.Selection;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     <see cref="PhraseInfo.Solo" /> reaches <c>RsPhrase.Solo</c>, the psarc
///     manifest's solo phrases and the `.eof` solo-phrase flag, and until 0.35 it
///     was import-only — an authored chart could never mark one.
/// </summary>
public sealed class SetPhraseSoloActionTests
{
    private static Arrangement NewArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Phrases.Add("solo1");
        return arrangement;
    }

    [Fact]
    public void SettingSolo_CreatesTheEntry_AndUndoRemovesItAgain()
    {
        var arrangement = NewArrangement();
        var action = new SetPhraseSoloAction(arrangement, "solo1", true);

        Assert.True(action.HasEffect);
        action.Execute();
        Assert.True(arrangement.PhraseInfos["solo1"].Solo);

        action.Undo();
        // Not merely Solo=false: an entry the user never had must not survive the
        // undo, or the RS-XML writer emits a phrase property nobody authored.
        Assert.False(arrangement.PhraseInfos.ContainsKey("solo1"));
    }

    [Fact]
    public void ClearingSolo_KeepsAnExistingEntry_AndUndoRestoresTheFlag()
    {
        var arrangement = NewArrangement();
        arrangement.PhraseInfos["solo1"] = new PhraseInfo(3, true);

        var action = new SetPhraseSoloAction(arrangement, "solo1", false);
        action.Execute();

        Assert.False(arrangement.PhraseInfos["solo1"].Solo);
        Assert.Equal(3, arrangement.PhraseInfos["solo1"].MaxDifficulty); // untouched

        action.Undo();
        Assert.True(arrangement.PhraseInfos["solo1"].Solo);
        Assert.Equal(3, arrangement.PhraseInfos["solo1"].MaxDifficulty);
    }

    /// <summary>A no-op must not reach the history — `CompositeAction` filters on
    /// exactly this, so a false here would push an empty undo node on every save
    /// of the event dialog.</summary>
    [Fact]
    public void ANoOp_HasNoEffect()
    {
        var arrangement = NewArrangement();
        Assert.False(new SetPhraseSoloAction(arrangement, "solo1", false).HasEffect);

        arrangement.PhraseInfos["solo1"] = new PhraseInfo(0, true);
        Assert.False(new SetPhraseSoloAction(arrangement, "solo1", true).HasEffect);

        Assert.False(new SetPhraseSoloAction(arrangement, "", true).HasEffect);
        Assert.False(new SetPhraseSoloAction(arrangement, "   ", true).HasEffect);
    }

    /// <summary>
    ///     The reachability half: the Event dialog is the surface that authors it, and
    ///     it goes through the history like every other edit that dialog makes.
    /// </summary>
    [Fact]
    public void TheEventDialog_AuthorsASoloPhrase_Undoably()
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var state = new ProjectState();
        var selection = new SelectionService();
        var config = new FakeConfigurationService();

        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = NewArrangement();
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger),
            new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart,
            ActiveArrangement = arrangement
        };

        var eventEditing = new DotCharter.Services.Editing.Events.EventEditingService(state, selection, history,
            new DotCharter.Services.Editing.EditingScope(state), logger);
        var services = new ChartEditDialogServices(editor, history, state, config,
            FakeLocalization.English(), logger);
        var dialog = new EventViewModel(services, eventEditing);
        dialog.Configure(new EventRequest(1000, arrangement));
        dialog.PhraseName = "solo1";
        dialog.SoloPhrase = true;
        dialog.SaveCommand.Execute(null);

        Assert.True(arrangement.PhraseInfos["solo1"].Solo);

        // Reopening on the same marker shows what was stored, not a fresh default.
        var reopened = new EventViewModel(services, eventEditing);
        reopened.Configure(new EventRequest(1000, arrangement));
        Assert.True(reopened.SoloPhrase);

        history.Undo();
        Assert.False(arrangement.PhraseInfos.TryGetValue("solo1", out var info) && info.Solo);
    }
}
