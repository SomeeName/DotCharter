using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Owner decision for 1.0 (2026-08-29): the CSVD drop
///     tuner SHIPS, fully functional, labelled EXPERIMENTAL everywhere a user
///     meets it — no native DSP work before 1.0; finishing it (dual-band CSVD)
///     is a post-1.0 item. This pins the label at every site, in all three
///     languages: strip it from one string in one resx and the gate goes red.
///     <para>
///         The site list is the set of places the feature is offered: the rig
///         Amp/FX panel card + its tooltip and per-seat pick (`AmpFxPanel`), the
///         tools popover (`ToolsPopoverView`), the settings row
///         (`AudioSettingsView`), and Get Ready's per-song drop-tuner switch
///         (#635: the `PlayMode_DropTuner_Label*` pair and its title, built by
///         `DropTunerViewModel`).
///     </para>
/// </summary>
public sealed class DropTunerExperimentalLabelTests
{
    /// <summary>Each culture's marker, matched case-insensitively.</summary>
    private static readonly (string File, string Marker)[] Cultures =
    [
        ("Resources.resx", "experimental"),
        ("Resources.de-DE.resx", "experimentell"),
        ("Resources.ja-JP.resx", "実験的"),
    ];

    /// <summary>Key → the product file(s) that must still reference it, so the
    /// label cannot be "kept" on a key nothing renders any more.</summary>
    private static readonly (string Key, string[] Sites)[] Keys =
    [
        ("Toolbar_DropTune", ["DotCharter.UI/Views/Shell/AmpFxPanel.axaml", "DotCharter.UI/Views/Shell/Popovers/ToolsPopoverView.axaml"]),
        ("Toolbar_DropTune_Tip", ["DotCharter.UI/Views/Shell/AmpFxPanel.axaml", "DotCharter.UI/Views/Shell/Popovers/ToolsPopoverView.axaml"]),
        ("Toolbar_DropTune_Seat", ["DotCharter.UI/Views/Shell/AmpFxPanel.axaml"]),
        ("DropTuner_Engine", ["DotCharter.UI/Views/Settings/AudioSettingsView.axaml"]),
        ("DropTuner_Engine_Tip", ["DotCharter.UI/Views/Settings/AudioSettingsView.axaml"]),
        ("PlayMode_DropTuner_Label", ["DotCharter.UI/ViewModels/PlayMode/DropTunerViewModel.cs"]),
        ("PlayMode_DropTuner_LabelOne", ["DotCharter.UI/ViewModels/PlayMode/DropTunerViewModel.cs"]),
        ("PlayMode_DropTuner_Title", ["DotCharter.UI/ViewModels/PlayMode/DropTunerViewModel.cs", "DotCharter.UI/Views/PlayMode/Screens/GetReadyView.axaml"]),
        ("Toolbar_TempoFlyout_Tip", []),
    ];

    public static IEnumerable<object[]> KeyByCulture() =>
        from k in Keys from c in Cultures select new object[] { k.Key, c.File, c.Marker };

    [Theory]
    [MemberData(nameof(KeyByCulture))]
    public void Every_drop_tuner_string_says_experimental(string key, string resx, string marker)
    {
        var value = ResxValue(resx, key);
        Assert.True(value.Contains(marker, StringComparison.OrdinalIgnoreCase),
            $"{resx} {key} = '{value}' does not carry the '{marker}' label");
    }

    [Fact]
    public void Every_site_still_renders_its_labelled_key()
    {
        foreach (var (key, sites) in Keys)
        {
            foreach (var site in sites)
            {
                var source = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), site));
                Assert.True(Regex.IsMatch(source, @"\b" + Regex.Escape(key) + @"\b"),
                    $"{site} no longer references {key} — the experimental label is on a string nothing renders");
            }
        }
    }

    [Fact]
    public void The_pedal_is_still_offered_not_hidden()
    {
        // "Experimental" means labelled, not removed: the rig section, the
        // settings row and the Play Mode option all still bind their controls.
        Assert.Contains("SelectedValue=\"{Binding InputPitchShiftSemis}\"",
            UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI/Views/Shell/AmpFxPanel.axaml")));
        Assert.Contains("SelectedIndex=\"{Binding SelectedDropTunerEngineIndex}\"",
            UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI/Views/Settings/AudioSettingsView.axaml")));
        Assert.Contains("IsChecked=\"{Binding DropTuner.IsOn, Mode=TwoWay}\"",
            UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI/Views/PlayMode/Screens/GetReadyView.axaml")));
    }

    private static string ResxValue(string resx, string key)
    {
        // Grep the key — the resx trio is 450–514 KB each; never parse whole.
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", resx);
        foreach (var line in File.ReadLines(path))
        {
            var m = Regex.Match(line, "<data name=\"" + Regex.Escape(key) + "\"[^>]*><value>(.*?)</value>");
            if (m.Success) return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        }
        throw new InvalidOperationException($"{resx} has no single-line entry for {key}");
    }
}
