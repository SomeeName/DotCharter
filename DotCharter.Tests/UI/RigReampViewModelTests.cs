using DotCharter.Tone.Audio.Reamp;
using DotCharter.Tests.Audio.Reamp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.UI;

/// <summary>
///     The re-amp panel's view model over a fake engine: the transport state it
///     shows (what is loaded, playing, where), the record/re-amp exclusion, the
///     unavailable state, and the stand-down when the engine loses the take.
///     <see cref="RigReampViewModel.Tick" /> is driven by hand; no dispatcher.
/// </summary>
public sealed class RigReampViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-reampvm-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private RigReampViewModel NewVm(FakeReampEngine engine, int takes = 1)
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < takes; i++)
            WavIo.WriteFloat32(Path.Combine(_dir, $"take {i}.wav"), new AudioBuffer(new float[4800], 48000, 1));
        return new RigReampViewModel(engine, new DiTakeLibrary(_dir), FakeLocalization.English(),
            loader: _ => (new float[48000 * 90], 48000),   // a 90 s take
            probe: _ => TimeSpan.FromSeconds(90))
        {
            ManualTick = true
        };
    }

    [Fact]
    public async Task ReampingATake_LoadsIt_PlaysIt_AndTheTransportFollowsTheEngine()
    {
        var engine = new FakeReampEngine();
        using var vm = NewVm(engine);
        Assert.True(vm.IsAvailable);
        Assert.False(vm.IsReamping);
        var row = Assert.Single(vm.Takes);
        Assert.Equal("1:30", row.DurationText);

        await vm.ReampTakeCommand.ExecuteAsync(row);

        Assert.True(vm.IsReamping);
        Assert.True(vm.IsActive);
        Assert.True(vm.IsPlaying);
        Assert.True(engine.Playing);
        Assert.True(engine.Loop); // loop defaults ON
        Assert.Equal(row.Name, vm.ReampTakeName);
        Assert.Equal(90, vm.Duration, 3);

        engine.Position = 12.4;
        vm.Tick();
        Assert.Equal("0:12 / 1:30", vm.PositionText);
        Assert.Empty(engine.Seeks); // an engine readout is not a seek

        vm.Position = 30; // the slider
        Assert.Equal([30.0], engine.Seeks);

        vm.PlayPauseCommand.Execute(null);
        Assert.False(vm.IsPlaying);
        Assert.False(engine.Playing);

        vm.Loop = false;
        Assert.False(engine.Loop);

        vm.StopCommand.Execute(null);
        Assert.False(vm.IsReamping);
        Assert.False(vm.IsActive);
        Assert.Null(engine.LoadedTake); // the live guitar is back
        Assert.Equal("0:00 / 0:00", vm.PositionText);
    }

    [Fact]
    public async Task ARefusedLoad_SaysSo_AndLeavesNothingLookingLive()
    {
        var engine = new FakeReampEngine { RefuseLoads = true };
        using var vm = NewVm(engine);
        await vm.ReampTakeCommand.ExecuteAsync(vm.Takes[0]);
        Assert.False(vm.IsReamping);
        Assert.Equal(FakeLocalization.English()["Rig_Reamp_LoadFailed"], vm.Status);
    }

    [Fact]
    public async Task ATakeTheEngineDropped_StandsTheTransportDown()
    {
        var engine = new FakeReampEngine();
        using var vm = NewVm(engine);
        await vm.ReampTakeCommand.ExecuteAsync(vm.Takes[0]);

        // Mid-rebuild: no status for a moment, but the take is still remembered.
        var take = engine.LoadedTake;
        engine.ClearTake(0);
        engine.Remembered = true;
        vm.Tick();
        Assert.True(vm.IsReamping);

        // The replay gave it up.
        engine.Remembered = false;
        vm.Tick();
        Assert.False(vm.IsReamping);
        Assert.Equal(FakeLocalization.English()["Rig_Reamp_Lost"], vm.Status);
        Assert.NotNull(take);
    }

    [Fact]
    public async Task RecordingAndReamping_ExcludeEachOther()
    {
        var engine = new FakeReampEngine();
        using var vm = NewVm(engine);

        vm.ToggleRecordCommand.Execute(null);
        Assert.True(vm.IsRecording);
        Assert.True(vm.IsActive);
        Assert.False(vm.ReampTakeCommand.CanExecute(vm.Takes[0]));

        engine.Capture(48000 + 24000);
        vm.Tick();
        Assert.Equal("0:01", vm.RecordTimeText);
        Assert.True(vm.RecordLevelDb > -20);

        vm.ToggleRecordCommand.Execute(null);
        Assert.False(vm.IsRecording);
        Assert.Equal(2, vm.Takes.Count);
        Assert.NotNull(vm.SelectedTake);
        Assert.StartsWith("DI ", vm.SelectedTake!.Name);

        await vm.ReampTakeCommand.ExecuteAsync(vm.SelectedTake);
        Assert.True(vm.IsReamping);
        Assert.False(vm.ToggleRecordCommand.CanExecute(null));
    }

    [Fact]
    public void ClosingTheAppMidRecording_SavesTheTake()
    {
        var engine = new FakeReampEngine();
        var vm = NewVm(engine, takes: 0);
        vm.ToggleRecordCommand.Execute(null);
        engine.Capture(4000);
        vm.Dispose();
        var saved = Assert.Single(Directory.GetFiles(_dir, "*.wav"));
        Assert.Equal(4000, WavIo.Read(saved).FrameCount);
        Assert.Empty(Directory.GetFiles(_dir, "*.recording"));
    }

    [Fact]
    public void WithoutAnEngine_EveryActionIsDisabled_AndTheReasonIsThere()
    {
        using var vm = new RigReampViewModel(null, new DiTakeLibrary(_dir), FakeLocalization.English()) { ManualTick = true };
        Assert.False(vm.IsAvailable);
        Assert.False(vm.ToggleRecordCommand.CanExecute(null));
        Assert.Equal(FakeLocalization.English()["Rig_Reamp_NoEngine"], vm.UnavailableReason);
        Assert.False(vm.HasTakes);
    }

    [Fact]
    public void RenameAndDelete_GoThroughTheLibrary()
    {
        var engine = new FakeReampEngine();
        using var vm = NewVm(engine, takes: 2);
        var row = vm.Takes.Single(t => t.Name == "take 1");

        vm.BeginRenameCommand.Execute(row);
        Assert.True(row.IsRenaming);
        row.EditName = "Chorus";
        vm.CommitRenameCommand.Execute(row);
        Assert.Contains(vm.Takes, t => t.Name == "Chorus");
        Assert.Equal("Chorus", vm.SelectedTake?.Name);

        vm.DeleteTakeCommand.Execute(vm.Takes.Single(t => t.Name == "Chorus"));
        Assert.Equal(["take 0"], vm.Takes.Select(t => t.Name));
    }
}
