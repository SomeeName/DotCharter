using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Tone.Parsing.Psarc;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The edit bar over the rig: it begins an edit only when the coordinator
///     takes the rig, saves what the rig holds under the PACKAGE's tone key, says
///     why a save failed, resets to the conversion, and always gives the player's
///     board back when it ends.
/// </summary>
public sealed class SongToneEditViewModelTests
{
    private const string CleanJson =
        """{"Key":"clean","Name":"Clean Tone","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    private sealed class Rig
    {
        public required RsTonePlaybackCoordinator Coordinator { get; init; }
        public required FakeDspChainService Chain { get; init; }
        public required SongToneEditViewModel Vm { get; init; }
        public List<(string Path, string Key, string Name, DcRigPreset Rig)> Saves { get; } = [];
        public List<(string Path, string Key)> Removes { get; } = [];
        public int Refreshes { get; set; }
        public int Closes { get; set; }
        public DcRigPreset? Captured { get; set; } =
            new([new DcRigModule("chorus", new Dictionary<string, float> { ["p"] = 1f })]);
        public SongToneEditWriteResult NextResult { get; set; } = new(SongToneEditWriteOutcome.Written);
    }

    private static Rig NewRig(string? song = "lib:CallMe")
    {
        var chain = new FakeDspChainService(
            new[] { "amp", "cab", "gain", "chorus", "reverb" }
                .Select(id => new DspModuleType(id, id, new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
                .ToList());
        var coordinator = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), new ProjectState(),
            new FakeConfigurationService())
        {
            ToneDefinitionSource = _ => [new PsarcToneDefinition("clean", "Clean Tone", CleanJson)],
            SongTonesSource = _ => new Dictionary<string, DcRigPreset>()
        };
        coordinator.PrepareForSong("song.psarc", new Arrangement { ToneBase = "Clean Tone" }, song);

        Rig? rig = null;
        var vm = new SongToneEditViewModel(
            coordinator,
            () => rig!.Captured,
            () => song,
            () => { rig!.Refreshes++; return Task.CompletedTask; },
            () => rig!.Closes++, FakeLocalization.English());
        rig = new Rig { Coordinator = coordinator, Chain = chain, Vm = vm };
        vm.SaveEdit = (path, key, name, preset) => { rig.Saves.Add((path, key, name, preset)); return rig.NextResult; };
        vm.RemoveEdit = (path, key) => { rig.Removes.Add((path, key)); return rig.NextResult; };
        return rig;
    }

    private static string[] Order(IDspChainService chain) => chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();

    [Fact]
    public async Task Begin_ShowsTheBar_AndDiscard_GivesTheBoardBackAndClosesTheRig()
    {
        var rig = NewRig();
        rig.Chain.AddNode("reverb");

        Assert.True(rig.Vm.Begin("Clean Tone", isEdited: false));
        Assert.True(rig.Vm.IsActive);
        Assert.Equal("Clean Tone", rig.Vm.ToneName);
        Assert.True(rig.Coordinator.IsEditingTone);

        rig.Vm.DiscardCommand.Execute(null);
        await rig.Coordinator.RestoreCompletion;

        Assert.False(rig.Vm.IsActive);
        Assert.Equal(1, rig.Closes);
        Assert.Equal(["reverb"], Order(rig.Chain));
        Assert.Empty(rig.Saves);
    }

    [Fact]
    public void Begin_StaysHidden_WhenTheCoordinatorRefuses()
    {
        var rig = NewRig();

        Assert.False(rig.Vm.Begin("no-such-tone", isEdited: false));
        Assert.False(rig.Vm.IsActive);
    }

    [Fact]
    public async Task Save_WritesTheCapturedRig_UnderThePackagesToneKey_AndReloadsTheTones()
    {
        var rig = NewRig();
        rig.Vm.Begin("Clean Tone", isEdited: false);

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        var save = Assert.Single(rig.Saves);
        Assert.Equal("lib:CallMe", save.Path);
        Assert.Equal("clean", save.Key);           // the manifest key, not the chart's name
        Assert.Equal("Clean Tone", save.Name);
        Assert.Same(rig.Captured, save.Rig);
        Assert.True(rig.Vm.HasEdit);
        Assert.False(rig.Vm.IsStatusError);
        Assert.NotEmpty(rig.Vm.StatusText);
        Assert.Equal(1, rig.Refreshes);
        Assert.True(rig.Vm.IsActive);              // saving does not end the edit
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact]
    public async Task AFailedSave_SaysWhy_AndMarksNothingEdited()
    {
        var rig = NewRig();
        rig.NextResult = new SongToneEditWriteResult(SongToneEditWriteOutcome.Failed, "the file is locked");
        rig.Vm.Begin("Clean Tone", isEdited: false);

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.IsStatusError);
        Assert.Contains("the file is locked", rig.Vm.StatusText);
        Assert.False(rig.Vm.HasEdit);
        Assert.Equal(0, rig.Refreshes);
    }

    [Fact]
    public async Task Save_OfAnEmptyRig_WritesNothing()
    {
        var rig = NewRig();
        rig.Captured = new DcRigPreset([]);
        rig.Vm.Begin("Clean Tone", isEdited: false);

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(rig.Saves);
        Assert.True(rig.Vm.IsStatusError);
    }

    [Fact]
    public async Task Save_ForAnOwnChart_WritesNothing()
    {
        var rig = NewRig(song: null);
        rig.Vm.Begin("Clean Tone", isEdited: false);

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(rig.Saves);
        Assert.True(rig.Vm.IsStatusError);
    }

    [Fact]
    public async Task Reset_RemovesTheEdit_ReloadsTheTones_AndKeepsEditing()
    {
        var rig = NewRig();
        rig.Vm.Begin("Clean Tone", isEdited: true);
        Assert.True(rig.Vm.ResetCommand.CanExecute(null));

        await rig.Vm.ResetCommand.ExecuteAsync(null);

        Assert.Equal(("lib:CallMe", "clean"), Assert.Single(rig.Removes));
        Assert.False(rig.Vm.HasEdit);
        Assert.False(rig.Vm.ResetCommand.CanExecute(null));
        Assert.Equal(1, rig.Refreshes);
        Assert.True(rig.Vm.IsActive);
        rig.Vm.End();
    }

    [Fact]
    public async Task End_IsIdempotent_AndAlwaysReleasesTheRig()
    {
        var rig = NewRig();
        rig.Vm.Begin("Clean Tone", isEdited: false);

        rig.Vm.End();
        rig.Vm.End();
        await rig.Coordinator.RestoreCompletion;

        Assert.False(rig.Vm.IsActive);
        Assert.False(rig.Coordinator.IsEditingTone);
        Assert.Equal(0, rig.Closes); // End is what a CLOSING rig calls; it does not close it again
    }
}
