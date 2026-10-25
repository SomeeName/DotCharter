namespace DotCharter.Tests.UI.Onboarding;

/// <summary>#229: first-run setup asks for the String order and writes the editor's InvertStrings.</summary>
/// <remarks>A Next off the Welcome page commits the language through the process-wide LocalizationService.</remarks>
[Collection("LocalizationSingleton")]
public sealed class OnboardingStringOrderTests
{
    [Fact]
    public void PickingTab_WritesTheEditorsStringOrder_AndTurnsTheTimeline()
    {
        var rig = new OnboardingWizardRig();

        rig.Vm.Open();
        Assert.True(rig.Vm.IsRocksmithStringOrder);
        rig.Vm.IsTabStringOrder = true;
        rig.Vm.Next();

        Assert.True(rig.Config.AppConfig.InvertStrings);
        Assert.False(rig.Config.AppConfig.PlayModeInvertStrings);
        Assert.True(rig.State.InvertStrings);
    }

    [Fact]
    public void AnUntouchedPage_KeepsTheConfiguredOrder()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.InvertStrings = true;

        rig.Vm.Open();
        Assert.True(rig.Vm.IsTabStringOrder);
        rig.Vm.Next();

        Assert.True(rig.Config.AppConfig.InvertStrings);
    }

    [Fact]
    public void PickingRocksmith_RaisesBothCards()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        var raised = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Vm.IsTabStringOrder = true;

        Assert.Contains(nameof(rig.Vm.IsTabStringOrder), raised);
        Assert.Contains(nameof(rig.Vm.IsRocksmithStringOrder), raised);
    }
}
