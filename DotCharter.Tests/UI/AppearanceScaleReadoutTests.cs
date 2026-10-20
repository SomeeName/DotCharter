using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The UI-scale readout shows only under Auto (choice 0), where it says what Auto chose. Beside
///     a fixed pick it printed the same "200 %" as the picker next to it (gallery capture, 2026-09-24).
/// </summary>
public sealed class AppearanceScaleReadoutTests
{
    [Fact]
    public void TheReadout_ShowsOnlyUnderAuto()
    {
        var view = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "AppearanceSettingsView.axaml")));
        var readout = Regex.Match(view, @"<TextBlock[^>]*Text=""\{Binding UiScaleReadout\}""[^>]*>");
        Assert.True(readout.Success, "the UI-scale readout is gone");
        Assert.Contains("IsVisible=\"{Binding UiScaleChoiceIndex, Converter={x:Static converters:IntEqualsConverter.Instance}, ConverterParameter=0}\"",
            readout.Value, StringComparison.Ordinal);
    }
}
