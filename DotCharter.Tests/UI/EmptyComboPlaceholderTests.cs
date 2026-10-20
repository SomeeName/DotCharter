using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     A ComboBox whose value can be LEGITIMATELY empty must say so
///     (2026-08-11): with no <c>PlaceholderText</c> an empty device / key /
///     vocal-track combo renders as a blank box and reads as broken. And a
///     168 px device combo fed "Focusrite Thunderbolt …" must TRIM with the
///     full name on a tooltip instead of hard-clipping mid-word (owner
///     report). Site-anchored on purpose — these are the combos the live
///     audit found blank or clipping; a new empty-capable combo should follow
///     the same pattern.
/// </summary>
public sealed class EmptyComboPlaceholderTests
{
    /// <summary>The four Play Mode device combos + the two single sites.</summary>
    [Theory]
    [InlineData(@"Views\PlayMode\Screens\GameSettingsDevicesView.axaml", "GameSettingsOutputDevice")]
    [InlineData(@"Views\PlayMode\Screens\GameSettingsDevicesView.axaml", "GameSettingsOutputDeviceAsio")]
    [InlineData(@"Views\PlayMode\Screens\GameSettingsDevicesView.axaml", "GameSettingsInputDevice")]
    [InlineData(@"Views\PlayMode\Screens\GameSettingsDevicesView.axaml", "GameSettingsInputDeviceAsio")]
    public void EveryDeviceCombo_CarriesAPlaceholder_AndTrimsItsItems(string relativePath, string name)
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", relativePath)));
        var tag = Regex.Match(markup, $"<ComboBox x:Name=\"{name}\".*?</ComboBox>", RegexOptions.Singleline);
        Assert.True(tag.Success, $"{name} is gone from {relativePath}");

        Assert.Contains("PlaceholderText=", tag.Value, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", tag.Value, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=", tag.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyCombo_AndTheVocalTrackCombo_SayWhenTheyAreEmpty()
    {
        var song = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Views", "Editor", "Inspector", "SongInspectorView.axaml")));
        Assert.Contains("PlaceholderText=\"{localization:Loc Combo_NotSet}\"", song, StringComparison.Ordinal);

        var tracks = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Views", "Shell", "Popovers", "TracksPopoverView.axaml")));
        Assert.Contains("PlaceholderText=\"{localization:Loc Toolbar_Vocals_NoTrack}\"", tracks,
            StringComparison.Ordinal);
    }

    /// <summary>Both resx carry every placeholder key — a key in one language
    /// renders raw in the other.</summary>
    [Theory]
    [InlineData("Combo_SystemDefault")]
    [InlineData("Combo_SelectDevice")]
    [InlineData("Combo_NotSet")]
    [InlineData("Toolbar_Vocals_NoTrack")]
    public void EveryPlaceholderKey_ExistsInBothLanguages(string key)
    {
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            Assert.Contains($"<data name=\"{key}\"",
                UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file)),
                StringComparison.Ordinal);
        }
    }
}
