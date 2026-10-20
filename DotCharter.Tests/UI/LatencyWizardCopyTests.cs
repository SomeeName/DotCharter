namespace DotCharter.Tests.UI;

/// <summary>
///     The wizard's subtitle is the always-on reminder that only the machine's latency is
///     removed — your reaction time cancels out between the two taps. The results page said
///     it a second time under the result ("Only the machine's display latency is applied…
///     Your reaction time canceled out between the two taps"), a few lines below the first.
/// </summary>
public sealed class LatencyWizardCopyTests
{
    [Fact]
    public void TheReactionTimeReminder_IsSaidOnce()
    {
        var view = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs",
            "LatencyCalibrationWizardView.axaml")));

        Assert.Contains("LatencyWizard_NotCompensated", view);
        Assert.DoesNotContain("LatencyWizard_Results_Note", view);
    }
}
