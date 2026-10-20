using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Dynamic difficulty needs levels to move between. A chart with NO notes cannot use it: the
///     toggle is disabled with its reason, shows OFF, and the stored preference survives (#323). A
///     ONE-level chart (most CDLC) rides a ladder generated for each run and says so (#295).
/// </summary>
public sealed class LivePlayDynamicDifficultyAvailabilityTests
{
    [Fact]
    public void A_chart_with_no_notes_disables_dynamic_difficulty_and_shows_it_off()
    {
        var (vm, state) = NewViewModel();
        vm.IsDynamicDifficultyEnabled = true;

        state.ActiveArrangement = Arrangement(levels: 1, withNotes: false);

        Assert.False(vm.CanUseDynamicDifficulty);
        Assert.False(vm.IsDynamicDifficultyOn);
        Assert.False(vm.IsDynamicDifficultyGenerated);

        vm.IsDynamicDifficultyOn = false; // a click on the disabled toggle cannot reach this, but must not stick
        Assert.True(vm.IsDynamicDifficultyEnabled, "the stored preference was cleared by a chart that cannot use it");
    }

    [Fact]
    public void A_single_level_chart_rides_a_generated_ladder_and_says_so()
    {
        var (vm, state) = NewViewModel();
        vm.IsDynamicDifficultyEnabled = true;

        state.ActiveArrangement = Arrangement(levels: 1);

        Assert.True(vm.CanUseDynamicDifficulty);
        Assert.True(vm.IsDynamicDifficultyOn);
        Assert.True(vm.IsDynamicDifficultyGenerated);
    }

    [Fact]
    public void A_levelled_chart_uses_its_own_levels_and_the_switch_is_announced()
    {
        var (vm, state) = NewViewModel();
        vm.IsDynamicDifficultyEnabled = true;
        state.ActiveArrangement = Arrangement(levels: 1);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        state.ActiveArrangement = Arrangement(levels: 3);

        Assert.True(vm.CanUseDynamicDifficulty);
        Assert.True(vm.IsDynamicDifficultyOn);
        Assert.False(vm.IsDynamicDifficultyGenerated);
        Assert.Contains(nameof(LivePlayViewModel.IsDynamicDifficultyGenerated), raised);
        Assert.Contains(nameof(LivePlayViewModel.IsDynamicDifficultyOn), raised);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void The_engine_can_level_only_a_chart_with_more_than_one_level(int levels, bool expected) =>
        Assert.Equal(expected, DynamicDifficultyEngine.CanLevel(Arrangement(levels)));

    private static Arrangement Arrangement(int levels, bool withNotes = true)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < levels; i++)
        {
            var level = new ChartLevel { DifficultyIndex = i };
            if (withNotes) level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), Fret = 3 });
            arrangement.Levels.Add(level);
        }

        return arrangement;
    }

    private static (LivePlayViewModel Vm, ProjectState State) NewViewModel()
    {
        var state = new ProjectState();
        var vm = LivePlayTestFactory.Create(new FakeAudioPlaybackService { InputSampleRate = 48_000 },
            state,
            new FakeAppLogger(),
            new FakeConfigurationService(),
            new PracticeProfileService(new FakeAppLogger(),
                Path.Combine(Path.GetTempPath(), "dc-livedd-" + Guid.NewGuid().ToString("N"), "never-written.json")));
        return (vm, state);
    }
}
