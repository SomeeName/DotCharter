using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The speed keys (`-` and `=` by default) belong with the speeds they step through. In the
///     footer, right after "Back to 100 % / 0 st", they read as that link's shortcut (live capture,
///     2026-09-24).
/// </summary>
public sealed class TempoPopoverChordsTests
{
    [Fact]
    public void TheSpeedKeys_SitWithThePresets_NotBesideReset()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Popovers", "TempoPopoverView.axaml")));

        var footer = Regex.Match(markup, @"<controls:FirePopover\.Footer>[\s\S]*?</controls:FirePopover\.Footer>");
        Assert.True(footer.Success, "the Tempo popover has no footer");
        Assert.DoesNotContain("Keybinds.Speed", footer.Value, StringComparison.Ordinal);

        var presets = Regex.Match(markup, @"<Grid[^>]*>\s*<TextBlock[^>]*Classes=""FirePopoverKick""[\s\S]*?Loc Toolbar_Tempo_Presets[\s\S]*?</Grid>");
        Assert.True(presets.Success, "the speed presets caption is no longer a row of its own");
        Assert.Contains("Keybinds.SpeedDown", presets.Value, StringComparison.Ordinal);
        Assert.Contains("Keybinds.SpeedUp", presets.Value, StringComparison.Ordinal);
    }
}
