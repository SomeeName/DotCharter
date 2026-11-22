using DotCharter.Core.Domain.Vocals;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using Avalonia.Input;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.CommandPalette;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

public sealed class EditingAvailabilityTests
{
    private sealed class Rig
    {
        public readonly SelectionService Selection = new();
        public readonly ProjectState State = new();
        public readonly EditingAvailability Availability;
        public readonly TimeNudgeViewModel Nudge;

        public Rig(bool withArrangement = true)
        {
            if (withArrangement) State.ActiveArrangement = new Arrangement { Name = "Lead" };
            var loc = FakeLocalization.KeysOnly();
            Availability = new EditingAvailability(Selection, State, loc);
            var logger = new FakeAppLogger();
            var scope = new EditingScope(State);
            var config = new FakeConfigurationService();
            var service = new HighwayEditingService(State, Selection, new HistoryService(logger),
                new StrictCollisionService(), config, scope, new EditTargetService(Selection, scope, config), logger);
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, loc);
            Nudge = new TimeNudgeViewModel(service, Selection, transport, State, loc);
        }
    }

    public static TheoryData<EditingVerb, string> NeedsSelection() => new()
    {
        { EditingVerb.SnapSelected, EditingAvailability.KeyNoSoundSelected },
        { EditingVerb.GenerateFhpForSelection, EditingAvailability.KeyNoSoundSelected },
        { EditingVerb.GenerateHandShape, EditingAvailability.KeyNoSoundSelected },
        { EditingVerb.GenerateArpeggio, EditingAvailability.KeyNoSoundSelected },
        { EditingVerb.SplitHandShape, EditingAvailability.KeyNoSoundSelected },
        { EditingVerb.AcceptSuggestedHandShapes, EditingAvailability.KeyNoSoundSelected },
        { EditingVerb.AcceptSuggestedArpeggios, EditingAvailability.KeyNoSoundSelected },
        { EditingVerb.SelectLikePattern, EditingAvailability.KeyNoTemplateSelected },
        { EditingVerb.IncreaseLength, EditingAvailability.KeyNoLengthTarget },
        { EditingVerb.DecreaseLength, EditingAvailability.KeyNoLengthTarget },
    };

    [Theory]
    [MemberData(nameof(NeedsSelection))]
    public void ASelectionVerb_SaysWhyWithNothingSelected_AndNothingOnceANoteIsSelected(EditingVerb verb, string key)
    {
        var rig = new Rig();

        Assert.Equal(key, rig.Availability.ReasonFor(verb));
        Assert.False(rig.Availability.IsAvailable(verb));

        rig.Selection.SelectedNotes.Add(new GuitarNote());

        Assert.Null(rig.Availability.ReasonFor(verb));
        Assert.True(rig.Availability.IsAvailable(verb));
    }

    [Theory]
    [InlineData(EditingVerb.Refret)]
    [InlineData(EditingVerb.SnapAll)]
    public void AnArrangementVerb_SaysWhyWithNoArrangement_AndNothingWithOne(EditingVerb verb)
    {
        Assert.Equal(EditingAvailability.KeyNoArrangement, new Rig(withArrangement: false).Availability.ReasonFor(verb));
        Assert.Null(new Rig().Availability.ReasonFor(verb));
    }

    [Fact]
    public void FitTempoToRecording_SaysWhatItNeedsOnAChartWithoutNotes_AndNothingOnceItHasOne()
    {
        const string verb = nameof(EditingVerb.OpenAlignToRecordingDialog);
        var rig = new Rig();
        Assert.Equal(EditingAvailability.KeyNoArrangement, rig.Availability.ReasonFor(EditingVerb.OpenAlignToRecordingDialog));

        var lead = new Arrangement { Name = "Lead" };
        lead.Levels.Add(new ChartLevel());
        var project = new ChartProject();
        project.Arrangements.Add(lead);
        rig.State.ActiveProject = project;

        Assert.Equal(EditingAvailability.KeyAlignNeedsNotes, rig.Availability.ReasonFor(EditingVerb.OpenAlignToRecordingDialog));
        Assert.Equal(EditingAvailability.KeyAlignNeedsNotes, rig.Availability[verb]);
        Assert.False(rig.Availability.Enabled[verb]);

        // A note placed on the chart raises no selection or arrangement event: the menu asks again as it opens.
        var raised = 0;
        rig.Availability.PropertyChanged += (_, _) => raised++;
        rig.Availability.Enabled.PropertyChanged += (_, _) => raised++;
        lead.Levels[0].Notes.Add(new GuitarNote());
        rig.Availability.Refresh();

        // The indexer, the Generate FHP row label (re-read by the same refresh) and the enabled map.
        Assert.Equal(3, raised);
        Assert.Null(rig.Availability[verb]);
        Assert.True(rig.Availability.Enabled[verb]);
    }

    /// <summary>The row is drawn disabled with its reason: Tempo map's markup binds both to the availability
    ///     and asks again as the menu opens (DotCharter.UI/Views/Shell/Menus/TempoMapMenu.axaml and its code-behind).</summary>
    [Fact]
    public void TheTempoMapRow_IsBoundToItsAvailability_AndTheMenuAsksAgainAsItOpens()
    {
        var folder = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Menus");
        var markup = File.ReadAllText(Path.Combine(folder, "TempoMapMenu.axaml"));
        var start = markup.IndexOf("Loc Menu_AlignToRecording", StringComparison.Ordinal);
        Assert.True(start >= 0, "the row is gone from the Tempo map menu");
        var row = markup[start..markup.IndexOf("/>", start, StringComparison.Ordinal)];

        Assert.Contains("IsEnabled=\"{Binding EditingAvailability.Enabled[OpenAlignToRecordingDialog]}\"", row);
        Assert.Contains("ToolTip.Tip=\"{Binding EditingAvailability[OpenAlignToRecordingDialog]}\"", row);
        Assert.Contains("ToolTip.ShowOnDisabled=\"True\"", row);
        Assert.Contains("EditingAvailability.Refresh()", File.ReadAllText(Path.Combine(folder, "TempoMapMenu.axaml.cs")));
    }

    [Fact]
    public void AnAnchorIsATemplate_AndALyricIsALengthTarget()
    {
        var rig = new Rig();
        rig.Selection.SelectedAnchors.Add(new FretAnchor());
        Assert.Null(rig.Availability.ReasonFor(EditingVerb.SelectLikePattern));
        Assert.NotNull(rig.Availability.ReasonFor(EditingVerb.IncreaseLength));

        rig.Selection.SelectedLyric = new LyricEvent();
        Assert.Null(rig.Availability.ReasonFor(EditingVerb.IncreaseLength));
    }

    [Fact]
    public void TheMenuTooltipIndexer_ReturnsTheReason_OrTheStaticTipWhenAvailable()
    {
        var rig = new Rig();
        Assert.Equal(EditingAvailability.KeyNoSoundSelected, rig.Availability["SnapSelected"]);
        Assert.Equal(EditingAvailability.KeyNoSoundSelected, rig.Availability["GenerateHandShape|Some_Tip"]);

        rig.Selection.SelectedNotes.Add(new GuitarNote());

        Assert.Null(rig.Availability["SnapSelected"]);
        Assert.Equal("Some_Tip", rig.Availability["GenerateHandShape|Some_Tip"]);
        Assert.Null(rig.Availability["NoSuchVerb"]);
    }

    [Fact]
    public void AvailabilityChangesAreAnnounced_OnSelectionAndOnArrangementChange()
    {
        var rig = new Rig(withArrangement: false);
        var raised = 0;
        rig.Availability.PropertyChanged += (_, _) => raised++;
        rig.Availability.Enabled.PropertyChanged += (_, _) => raised++;

        rig.Selection.SelectedNotes.Add(new GuitarNote());
        Assert.True(raised > 0);

        var before = raised;
        rig.State.ActiveArrangement = new Arrangement();
        Assert.True(raised > before);
    }

    [Fact]
    public void ABlockedKeybind_LeavesTheReasonOnTheStatusRail_AndDoesNotRun()
    {
        var rig = new Rig();
        var ran = false;
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger()) { Gate = rig.Availability.Blocks };
        handler.Register(new KeyChord(Key.F20), () => ran = true, "SnapSelected");
        handler.RefreshBinds();

        Assert.True(handler.HandleKeyPress(Key.F20, false, false, false));

        Assert.False(ran);
        Assert.Equal(EditingAvailability.KeyNoSoundSelected, rig.State.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, rig.State.MainDisplaySeverity);
    }

    [Fact]
    public void AnAvailableKeybind_RunsAndLeavesTheStatusRailAlone()
    {
        var rig = new Rig();
        rig.Selection.SelectedNotes.Add(new GuitarNote());
        var before = rig.State.MainDisplayMessage;
        var ran = false;
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger()) { Gate = rig.Availability.Blocks };
        handler.Register(new KeyChord(Key.F20), () => ran = true, "SnapSelected");
        handler.RefreshBinds();

        Assert.True(handler.HandleKeyPress(Key.F20, false, false, false));

        Assert.True(ran);
        Assert.Equal(before, rig.State.MainDisplayMessage);
    }

    [Fact]
    public void AMenuItem_ReadsItsEnabledStateAndTooltipFromTheSameVerb()
    {
        var rig = new Rig();
        Assert.False(rig.Availability.Enabled["SnapSelected"]);
        Assert.True(rig.Availability.Enabled["NotAnEditingVerb"]);

        rig.Selection.SelectedNotes.Add(new GuitarNote());

        Assert.True(rig.Availability.Enabled["SnapSelected"]);
    }

    [Fact]
    public void TheNudges_SayWhyWithNothingSelected_AndNothingOnceSomethingIs()
    {
        var rig = new Rig();

        Assert.Equal(EditingAvailability.KeyNoMovableSelection, rig.Nudge.DisabledReason);
        Assert.False(rig.Nudge.EarlierCommand.CanExecute(null));
        rig.Nudge.Nudge(1, 10);
        Assert.Equal(EditingAvailability.KeyNoMovableSelection, rig.State.MainDisplayMessage);

        rig.Selection.SelectedAnchors.Add(new FretAnchor());

        Assert.Null(rig.Nudge.DisabledReason);
        Assert.True(rig.Nudge.LaterCommand.CanExecute(null));
    }

    [Fact]
    public void ThePaletteRow_ShowsTheReasonOnlyWhileDisabled()
    {
        var rig = new Rig();
        var enabled = false;
        var command = new AppCommand("edit.snapSelected", "Snap", "Edit", () => { }, canExecute: () => enabled,
            disabledReason: () => rig.Availability.ReasonFor(EditingVerb.SnapSelected));

        var disabledRow = new CommandPaletteItem(command, FakeLocalization.KeysOnly());
        Assert.False(disabledRow.IsEnabled);
        Assert.Equal(EditingAvailability.KeyNoSoundSelected, disabledRow.DisabledReason);
        Assert.True(disabledRow.HasDisabledReason);

        enabled = true;
        rig.Selection.SelectedNotes.Add(new GuitarNote());
        var enabledRow = new CommandPaletteItem(command, FakeLocalization.KeysOnly());
        Assert.Null(enabledRow.DisabledReason);
        Assert.False(enabledRow.HasDisabledReason);
    }

    [Fact]
    public void TheOneGenerateFhpRow_NamesTheArrangement_AndRunsItsGenerator_WithNothingSelected()
    {
        var rig = new Rig();
        var ran = new List<string>();

        Assert.False(rig.Availability.GenerateFhpActsOnSelection);
        Assert.Equal(EditingAvailability.KeyGenerateFhpArrangement, rig.Availability.GenerateFhpLabel);
        rig.Availability.GenerateFhp(() => ran.Add("selection"), () => ran.Add("arrangement"));

        Assert.Equal(["arrangement"], ran);
    }

    [Fact]
    public void TheOneGenerateFhpRow_NamesTheSelection_AndRunsItsGenerator_OnceASoundIsSelected()
    {
        var rig = new Rig();
        var ran = new List<string>();
        var changed = new List<string?>();
        rig.Availability.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Selection.SelectedChords.Add(new Chord());

        Assert.Contains(nameof(EditingAvailability.GenerateFhpLabel), changed);
        Assert.True(rig.Availability.GenerateFhpActsOnSelection);
        Assert.Equal(EditingAvailability.KeyGenerateFhpSelection, rig.Availability.GenerateFhpLabel);
        rig.Availability.GenerateFhp(() => ran.Add("selection"), () => ran.Add("arrangement"));
        Assert.Equal(["selection"], ran);

        // A hand shape or an anchor alone is not a range of sounds: the arrangement again.
        rig.Selection.SelectedChords.Clear();
        rig.Selection.SelectedAnchors.Add(new FretAnchor());
        Assert.Equal(EditingAvailability.KeyGenerateFhpArrangement, rig.Availability.GenerateFhpLabel);
    }

    [Fact]
    public void EveryReasonKey_ExistsInAllThreeResourceFiles()
    {
        string[] keys =
        [
            EditingAvailability.KeyNoArrangement, EditingAvailability.KeyNoSoundSelected,
            EditingAvailability.KeyNoTemplateSelected, EditingAvailability.KeyNoLengthTarget,
            EditingAvailability.KeyNoMovableSelection,
        ];
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "DotCharter.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var text = File.ReadAllText(Path.Combine(root, "DotCharter.Localization", file));
            foreach (var key in keys) Assert.Contains($"name=\"{key}\"", text);
        }
    }
}
