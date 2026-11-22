using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.UI.ViewModels.PlayMode.ToneSections;

namespace DotCharter.Tests.UI;

public sealed partial class ToneDesignMatchTests
{
    private static void SectionPrior(Harness h, ProjectState state)
    {
        h.GiveItAReferenceDi();
        h.Vm.ConfigureSections(static () => null, static () => null,
            project: () => state.ActiveProject, ownerChanges: state);
        ((IToneSectionHost)h.Vm).PublishResult(new ToneDesignResult(
            new DspTonePreset("section", "Lead", DspToneCategory.Lead, "", Rig("amp"), DspToneOrigin.User),
            "", 0.8, [], "{}", new AiUsage("m", 1, 1)), "Amp");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_match_consuming_a_section_cannot_publish_after_its_owner_expires(bool duringIsolation)
    {
        var h = new Harness();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        SectionPrior(h, state);
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (duringIsolation) h.Isolator.Block = block;
        else h.Matcher.Block = block;
        var match = h.Vm.MatchCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsMatching);
        state.ActiveProject = new ChartProject();
        block.SetResult(true);
        await match;
        Assert.Empty(h.Vm.MatchCandidates);
        Assert.False(h.Vm.HasResult);
    }

    [Fact]
    public async Task Match_candidates_derived_from_a_section_keep_its_owner_after_success()
    {
        var h = new Harness();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        SectionPrior(h, state);
        await h.Vm.MatchCommand.ExecuteAsync(null);
        Assert.Equal("design", Assert.Single(h.Matcher.Requests).Prior!.Source);
        var retained = h.Vm.MatchCandidates[1];
        Assert.True(h.Vm.HasResult);
        state.ActiveProject = new ChartProject();
        h.Vm.SelectedMatchCandidate = retained;
        h.Vm.HearItCommand.Execute(null);
        h.Vm.SaveToneCommand.Execute(null);
        Assert.Empty(h.Vm.MatchCandidates);
        Assert.False(h.Vm.HasResult);
        Assert.Empty(h.Applied);
        Assert.Empty(h.Saved);
    }
}
