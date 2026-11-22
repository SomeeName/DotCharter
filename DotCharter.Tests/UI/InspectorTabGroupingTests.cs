using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///    : the Inspector's strip names its first tab "Note" (it said "Sound", which reads as audio
///     beside "Mixer") and groups the tabs: the two that are about the SELECTION first (Note, Chord
///     shapes), a divider, then the tabs about the SONG. The rendered strip is held by
///     <c>Perf/InspectorGroupedTabsUiPerfSmokeTests</c>; this is the markup half.
/// </summary>
public sealed class InspectorTabGroupingTests
{
    private static readonly Regex Header =
        new(@"<ToggleButton\b[^>]*?Classes=""ColumnFlowNav\b[^""]*""[^>]*?Tag=""(\d)""", RegexOptions.Compiled);

    [Fact]
    public void TheStrip_ListsSelectionTabsThenTheSongTabs_WithTheDividerBeforeSong()
    {
        var markup = Regex.Replace(
            UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "InspectorView.axaml"))),
            "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        // Note(0) Chord shapes(7) | Song(2) Lyrics(3) Issues(4) Annotations(5) Riff library(6) Mixer(1)
        Assert.Equal(["0", "7", "2", "3", "4", "5", "6", "1"], Header.Matches(markup).Select(m => m.Groups[1].Value));
        Assert.Matches(@"Classes=""ColumnFlowNav""\s+ctl:OverflowTabStrip\.IsGroupStart=""True""\s+Tag=""2""", markup);
        Assert.Single(Regex.Matches(markup, "IsGroupStart="));
        Assert.Contains("DividerBrush=", markup);
    }

    [Fact]
    public void TheFirstTab_IsCalledNote_InEveryLanguage_AndTheOldCaptionKeyIsGone()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "InspectorView.axaml")));
        Assert.Contains("Inspector_Tab_Note}", markup);
        Assert.DoesNotContain("Inspector_Tab_Sound}", markup); // the tip key `Inspector_Tab_Sound_Tip` keeps its code name

        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file));
            Assert.Contains("name=\"Inspector_Tab_Note\"", resx);
            Assert.DoesNotContain("name=\"Inspector_Tab_Sound\"", resx);
        }
    }
}
