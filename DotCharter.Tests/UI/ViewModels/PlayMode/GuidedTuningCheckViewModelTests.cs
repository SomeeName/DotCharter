using DotCharter.Tone.Audio.Tuner;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     0.21 guided tuning-check VM wiring: it builds its per-string rows from the
///     tested <see cref="TuningCheckTargets"/> table and drives the pure
///     <see cref="GuidedTuningCheck"/> state machine (skip / restart / complete).
///     Constructed with driveWithTimer: false so no dispatcher is needed; the pitch
///     detection itself is covered by the tuner-suite tests.
/// </summary>
public sealed class GuidedTuningCheckViewModelTests
{
    private static Arrangement Guitar()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        return arrangement;
    }

    private static GuidedTuningCheckViewModel NewVm()
        => new(audio: null, FakeLocalization.English(), driveWithTimer: false);

    [Fact]
    public void Begin_BuildsRowsFromTheTargetTable()
    {
        var vm = NewVm();

        vm.Begin(Guitar());

        Assert.True(vm.HasCheck);
        Assert.Equal(6, vm.Strings.Count);
        // Standard tuning note names, lowest string first — the check's own table.
        Assert.Equal(new[] { "E2", "A2", "D3", "G3", "B3", "E4" },
            vm.Strings.Select(s => s.NoteName));
        Assert.Equal(0, vm.CurrentStringIndex);
        Assert.True(vm.Strings[0].IsCurrent);
        Assert.False(vm.IsComplete);
    }

    [Fact]
    public void Begin_Null_ClearsTheCheck()
    {
        var vm = NewVm();
        vm.Begin(Guitar());

        vm.Begin(null);

        Assert.False(vm.HasCheck);
        Assert.Empty(vm.Strings);
    }

    [Fact]
    public void Skip_AdvancesToTheNextString()
    {
        var vm = NewVm();
        vm.Begin(Guitar());

        vm.SkipCurrentCommand.Execute(null);

        Assert.Equal(TuningCheckStringState.Skipped, vm.Strings[0].State);
        Assert.Equal(1, vm.CurrentStringIndex);
        Assert.True(vm.Strings[1].IsCurrent);
        Assert.False(vm.Strings[0].IsCurrent);
    }

    [Fact]
    public void SkippingEveryString_CompletesTheCheck()
    {
        var vm = NewVm();
        vm.Begin(Guitar());

        for (var i = 0; i < 6; i++) vm.SkipCurrentCommand.Execute(null);

        Assert.True(vm.IsComplete);
        Assert.All(vm.Strings, s => Assert.False(s.IsCurrent));
    }

    [Fact]
    public void SelectString_ReChecksASpecificString()
    {
        var vm = NewVm();
        vm.Begin(Guitar());
        vm.SkipCurrentCommand.Execute(null); // skip low E, now on A

        vm.SelectStringCommand.Execute(vm.Strings[0]); // revisit the low E

        Assert.Equal(0, vm.CurrentStringIndex);
        Assert.True(vm.Strings[0].IsCurrent);
        Assert.False(vm.IsComplete);
    }

    [Fact]
    public void Restart_ResetsEveryStringToTheLowest()
    {
        var vm = NewVm();
        vm.Begin(Guitar());
        vm.SkipCurrentCommand.Execute(null);
        vm.SkipCurrentCommand.Execute(null);

        vm.RestartCommand.Execute(null);

        Assert.Equal(0, vm.CurrentStringIndex);
        Assert.False(vm.IsComplete);
        Assert.All(vm.Strings, s => Assert.NotEqual(TuningCheckStringState.Skipped, s.State));
    }

    [Fact]
    public void Stop_ParksPolling()
    {
        var vm = NewVm();
        vm.Begin(Guitar());

        vm.Stop();

        Assert.False(vm.IsPolling);
    }
}
