using System.Net;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Localization;

/// <summary>
///     #496: the chart view has ONE plain name per thing. The words below were the second, third or
///     fourth name of something ("MIDI in" / "Synth tone" / "Play the chart" were all the chart sound;
///     "Multi-track" / "Stacked tracks" / "Tracks" were all the arrangements) or an Editor-on-Fire or
///     programmer's word ("gems", "lint"). Toggles are named for the thing ("Metronome"), never "Toggle
///     metronome". The English values of the user-facing chart-view keys must not bring them back.
/// </summary>
public sealed class ChartViewNamingLintTests
{
    /// <summary>The user-facing chart-view surfaces: menus, toolbar, popovers, Inspector, tooltips, keybind titles, palette, undo labels.</summary>
    private static readonly string[] Prefixes =
    [
        "Menu_", "Toolbar_", "Keybind_", "Inspector_", "Mixer_", "Main_", "Palette_", "History_", "Status_",
        "StackedTracks_", "TimelineLanes_", "Diagnostics_", "LivePlay_", "Preview3D_", "SheetMusic_",
        "Dialog_FixChart_", "Dialog_BatchLint_", "Dialog_GenerateLevels_", "BatchLint_", "Picker_SelectLintFolder",
        "ArrangementSync_"
    ];

    /// <summary>Keys owned by the other open #489 PRs, which rewrite or remove them; touching them here would conflict when the PRs are combined.</summary>
    private static readonly HashSet<string> OwnedByOtherPrs = new(StringComparer.Ordinal)
    {
        // feat/493-one-mixer-clear-inspector removes these (Levels / Playback FX / Song / Sound tab / MIDI inst), covered there.
        "Inspector_Levels", "Inspector_MuteInput", "Inspector_SoloInput", "Inspector_PitchShift", "Inspector_PlaybackFx",
        "Inspector_Song", "Inspector_Tab_Sound", "Toolbar_MidiInst"
    };

    /// <summary>The retired names, as regular expressions over the DECODED English value (case-insensitive).</summary>
    private static readonly (string Word, string Pattern)[] Retired =
    [
        ("MIDI in", @"\bMIDI in\b"),
        ("MIDI inst", @"\bMIDI inst"),
        ("MIDI synth", @"\bMIDI synth"),
        ("MIDI instrument", @"\bMIDI instrument"),
        ("Virtual instrument for chart MIDI", @"Virtual instrument for chart"),
        ("Synth tone", @"\bsynth tone"),
        ("Play the chart", @"\bPlay the chart"),
        ("Guitar in", @"\bGuitar in\b"),
        ("the guitar input", @"\bguitar input"),
        ("Input gain", @"\bInput gain"),
        ("Input level", @"\bInput level"),
        ("Multi-track", @"\bMulti-?track"),
        ("Stacked tracks", @"\bStacked (tracks|arrangements)"),
        ("Squash", @"\bSquash"),
        ("Timeline lanes", @"\bTimeline lanes"),
        ("empty lanes", @"\b(empty|all) lanes"),
        ("Session health", @"\bSession health"),
        ("Toggle ", @"\bToggle "),
        ("lint", @"\blint"),
        ("gems", @"\bgems?\b"),
        ("FHP", @"\bFHP\b"),
        ("Fret hand position", @"\bFret hand position"),
        ("Improve chart", @"\bImprove chart"),
        ("Auto-fix", @"\bAuto-?fix"),
        ("Generate structure", @"\bGenerate structure"),
        ("Propagate phrase", @"\bPropagate phrase"),
        ("refret", @"\bRefret"),
        ("Convert note to power chord", @"\bConvert note to power chord"),
        ("Muted strums →", @"Muted strums\s*→"),
        ("Apply edits to another", @"\bApply edits to another"),
        ("Compare with project file", @"\bCompare with project file"),
        ("VST3 chain", @"\bVST3 chain"),
        ("Playback FX", @"\bPlayback FX")
    ];

    [Fact]
    public void ChartViewStrings_UseNoRetiredName()
    {
        var values = EnglishValues();
        var keys = values.Keys.Where(IsChartViewKey).ToList();
        Assert.True(keys.Count > 500, $"only {keys.Count} chart-view keys matched; the prefixes no longer match the table");
        var offenders = Offenders(values);
        Assert.True(offenders.Count == 0, "chart-view strings use a retired name (see #496):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Not vacuous: each retired word, put back into a chart-view value, is reported, and an excluded key is skipped.</summary>
    [Theory]
    [InlineData("Menu_X", "Chart sound MIDI in", "MIDI in")]
    [InlineData("Toolbar_X", "Multi-track", "Multi-track")]
    [InlineData("Toolbar_X", "Toggle metronome", "Toggle ")]
    [InlineData("Menu_X", "Lint chart library…", "lint")]
    [InlineData("Menu_X", "Toggle ghost gems", "gems")]
    [InlineData("Menu_X", "Generate FHP", "FHP")]
    [InlineData("Mixer_X", "Play the chart", "Play the chart")]
    [InlineData("Menu_X", "Muted strums → muted chord", "Muted strums →")]
    public void TheLint_ReportsARetiredWord(string key, string value, string word)
    {
        var offenders = Offenders(new Dictionary<string, string> { [key] = value });
        Assert.Contains(offenders, o => o.Contains(key, StringComparison.Ordinal) && o.Contains($"\"{word}\"", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLint_SkipsOtherSurfacesAndTheOtherPrsKeys()
    {
        var offenders = Offenders(new Dictionary<string, string>
        {
            ["Settings_Graphics_Bloom_Desc"] = "Soft glow around bright note gems.",
            ["Inspector_Levels"] = "Levels with MIDI in",
            ["Menu_Fine"] = "Chart sound on/off"
        });
        Assert.Empty(offenders);
    }

    private static bool IsChartViewKey(string key) =>
        !OwnedByOtherPrs.Contains(key) && Prefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    private static List<string> Offenders(IReadOnlyDictionary<string, string> values)
    {
        var offenders = new List<string>();
        foreach (var (key, value) in values.Where(kv => IsChartViewKey(kv.Key)))
        {
            offenders.AddRange(Retired
                .Where(r => Regex.IsMatch(value, r.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(r => $"{key} = \"{value}\" uses \"{r.Word}\""));
        }

        return offenders;
    }

    private static Dictionary<string, string> EnglishValues()
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        return Regex.Matches(resx, @"<data name=""(?<k>[^""]+)""[^>]*>\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups["k"].Value, m => WebUtility.HtmlDecode(m.Groups["v"].Value), StringComparer.Ordinal);
    }
}
