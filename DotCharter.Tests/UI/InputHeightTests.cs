using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The shared input chrome sets <c>MinHeight</c>, never a fixed
///     <c>Height</c> (2026-08-11). <c>Styles/Inputs.axaml</c> pinned every
///     TextBox / ComboBox / NumericUpDown at exactly 32 px, which silently
///     killed every multi-line TextBox in the app — the lyrics inspector's
///     textarea declared <c>MinHeight=160 MaxHeight=320</c> and both were dead,
///     because a style's fixed Height on the control wins over its own range.
///     A single-line input still sits at 32: nothing makes it taller.
/// </summary>
public sealed class InputHeightTests
{
    [Fact]
    public void TheSharedInputChrome_UsesMinHeight_NeverAFixedHeight()
    {
        var inputs = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "Inputs.axaml"));

        // The shared chrome block for the three input controls.
        var block = Regex.Match(inputs,
            "<Style Selector=\"TextBox, ComboBox, NumericUpDown\">.*?</Style>", RegexOptions.Singleline);
        Assert.True(block.Success, "the shared TextBox/ComboBox/NumericUpDown style is gone from Inputs.axaml");

        Assert.Contains("Property=\"MinHeight\"", block.Value, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(block.Value, "Property=\"Height\""),
            "the shared input chrome sets a fixed Height again — every multi-line TextBox " +
            "in the app is pinned to one line by it (the lyrics textarea shipped that way)");
    }
}
