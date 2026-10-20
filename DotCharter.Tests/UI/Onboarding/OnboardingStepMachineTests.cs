using DotCharter.UI.ViewModels.Onboarding;

namespace DotCharter.Tests.UI.Onboarding;

/// <summary>The pure navigation half of the first-run wizard: order, the two
/// edges, what Skip remembers and what Back forgets, and that a reset run starts
/// clean. No services, no dispatcher.</summary>
public sealed class OnboardingStepMachineTests
{
    [Fact]
    public void Order_IsTheEightPagesTheRoadmapAsksFor_InThatOrder()
    {
        Assert.Equal(
        [
            OnboardingStep.Welcome, OnboardingStep.Library, OnboardingStep.Audio, OnboardingStep.Input,
            OnboardingStep.Tone, OnboardingStep.Latency, OnboardingStep.Profile, OnboardingStep.Done
        ], OnboardingStepMachine.Order);
        Assert.Equal(Enum.GetValues<OnboardingStep>().Length, OnboardingStepMachine.Order.Count);
    }

    [Fact]
    public void StartsOnWelcome_WhichIsFirstAndCannotGoBack()
    {
        var m = new OnboardingStepMachine();
        Assert.Equal(OnboardingStep.Welcome, m.Current);
        Assert.True(m.IsFirst);
        Assert.False(m.IsLast);
        Assert.Equal(0, m.Index);
        Assert.Equal(8, m.Count);
        Assert.False(m.Back());
        Assert.Equal(OnboardingStep.Welcome, m.Current);
    }

    [Fact]
    public void Next_WalksEveryPageAndStopsOnDone()
    {
        var m = new OnboardingStepMachine();
        var visited = new List<OnboardingStep> { m.Current };
        while (m.Next()) visited.Add(m.Current);

        Assert.Equal(OnboardingStepMachine.Order, visited);
        Assert.True(m.IsLast);
        Assert.False(m.Next(), "Done has nothing after it — Finish is the view model's, not a navigation");
        Assert.Empty(m.Skipped);
    }

    [Fact]
    public void Skip_AdvancesLikeNextButRemembersThePage()
    {
        var m = new OnboardingStepMachine();
        Assert.True(m.Next());                       // Welcome committed → Library
        Assert.True(m.Skip());                       // Library skipped → Audio
        Assert.Equal(OnboardingStep.Audio, m.Current);
        Assert.Equal([OnboardingStep.Library], m.Skipped);

        Assert.True(m.Skip());                       // Audio skipped → Input
        Assert.Contains(OnboardingStep.Audio, m.Skipped);
        Assert.Contains(OnboardingStep.Library, m.Skipped);
    }

    [Fact]
    public void Skip_IsRefusedOnDone()
    {
        var m = new OnboardingStepMachine();
        while (m.Next()) { }
        Assert.False(m.Skip());
        Assert.Equal(OnboardingStep.Done, m.Current);
        Assert.DoesNotContain(OnboardingStep.Done, m.Skipped);
    }

    [Fact]
    public void Back_ReturnsToThePreviousPageAndUnskipsIt()
    {
        var m = new OnboardingStepMachine();
        m.Next();
        m.Skip();                                    // Library skipped
        Assert.Equal(OnboardingStep.Audio, m.Current);

        Assert.True(m.Back());
        Assert.Equal(OnboardingStep.Library, m.Current);
        Assert.Empty(m.Skipped);                     // looking at it again — it will be left through Next or Skip

        Assert.True(m.Next());                       // committed this time
        Assert.Empty(m.Skipped);
    }

    [Fact]
    public void Next_ClearsAnEarlierSkipOfTheSamePage()
    {
        var m = new OnboardingStepMachine();
        m.Next(); m.Skip(); m.Back();                // on Library, un-skipped by Back
        m.Skip();                                    // skipped again
        m.Back();                                    // back on Library
        m.Next();                                    // committed
        Assert.Empty(m.Skipped);
    }

    [Fact]
    public void Reset_StartsASecondRunClean()
    {
        var m = new OnboardingStepMachine();
        m.Next(); m.Skip(); m.Skip(); m.Next();
        Assert.NotEmpty(m.Skipped);
        Assert.NotEqual(OnboardingStep.Welcome, m.Current);

        m.Reset();

        Assert.Equal(OnboardingStep.Welcome, m.Current);
        Assert.Empty(m.Skipped);
        Assert.True(m.IsFirst);
    }
}
