using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     One home per mix control. The Mixer tab's Levels and Playback FX sections went; every
///     control they carried must now be bound in a toolbar popover (Mix for the levels, their mute /
///     solo and the pan, Tempo for speed and pitch), and the tab must not bind any of them again.
///     <para>
///         The table below IS the migration: one row per control the two removed sections held. A
///         control missing from the popovers is a function removed, which the owner ruled out.
///     </para>
/// </summary>
public sealed class MixerLevelsInPopoversTests
{
    private static readonly (string Control, string Property, string Popover)[] Controls =
    [
        ("Master level", "Volume", "Mix"),
        ("Master mute", "IsMasterMuted", "Mix"),
        ("Song level", "SongVolume", "Mix"),
        ("Song mute", "IsSongMuted", "Mix"),
        ("Song solo", "IsSongSoloed", "Mix"),
        ("Guitar input level", "InputVolume", "Mix"),
        ("Guitar input mute", "IsInputMuted", "Mix"),
        ("Guitar input solo", "IsInputSoloed", "Mix"),
        ("MIDI level", "MidiVolume", "Mix"),
        ("MIDI mute", "IsMidiMuted", "Mix"),
        ("MIDI solo", "IsMidiSoloed", "Mix"),
        ("Track pan", "Pan", "Mix"),
        ("Playback speed", "PlaybackSpeed", "Tempo"),
        ("Pitch shift", "PitchShift", "Tempo")
    ];

    [Fact]
    public void EveryControlOfTheRemovedMixerSections_IsBoundInItsPopover()
    {
        Assert.Equal(14, Controls.Length); // non-vacuity: the table is the migration list
        var offenders = new List<string>();
        foreach (var (control, property, popover) in Controls)
        {
            if (!Binds(Markup("Views", "Shell", "Popovers", popover + "PopoverView.axaml"), property))
                offenders.Add($"{control}: Transport.{property} is not bound in {popover}PopoverView.axaml");
        }

        Assert.True(offenders.Count == 0, "controls the Mixer tab lost without a home:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheMixerTab_NoLongerBindsThem_SoEachControlHasOneHome()
    {
        var mixer = Markup("Views", "Editor", "Inspector", "MixerInspectorView.axaml");
        var offenders = Controls.Where(c => Binds(mixer, c.Property)).Select(c => $"{c.Control} (Transport.{c.Property})").ToList();

        Assert.True(offenders.Count == 0, "still bound in the Mixer tab AND a popover:\n  " + string.Join("\n  ", offenders));
        foreach (var gone in new[] { "MixerLevelsCard", "MixerPlaybackFxBody", "MixerRailLevels", "MixerRailPlaybackFx" })
            Assert.DoesNotContain(gone, mixer);
    }

    [Fact]
    public void TheMixerTabKeepsTheDeepControls()
    {
        var mixer = Markup("Views", "Editor", "Inspector", "MixerInspectorView.axaml");
        foreach (var kept in new[] { "MixerStemsBody", "MixerEqBody", "MixerMidiBody", "ChartSynthInstrument", "ChartSynthPlayStyle", "Transport.Eq" })
            Assert.Contains(kept, mixer);
    }

    [Fact]
    public void PracticeTools_HasNoSecondTunerDoor_NoSecondChartSoundSwitch_AndOneVstPicker()
    {
        var tools = Markup("Views", "Shell", "Popovers", "ToolsPopoverView.axaml");
        var mixer = Markup("Views", "Editor", "Inspector", "MixerInspectorView.axaml");

        Assert.DoesNotContain("OpenTunerCommand", tools);       // the toolbar's tuner button is the one door
        Assert.DoesNotContain("Transport.MidiEnabled", tools);  // the toolbar's piano button and the Mixer checkbox write it
        Assert.Contains("Transport.SelectedInstrumentVst", tools);
        Assert.DoesNotContain("InstrumentVstPlugins", mixer);   // the picker lives in ONE place ...
        Assert.Contains("Mixer_ChartSynth_VstLink", mixer);     // ... and the Mixer tab links to it
    }

    /// <summary>A binding to <c>Transport.&lt;property&gt;</c> exactly (not a longer name that starts with it).</summary>
    private static bool Binds(string markup, string property) =>
        Regex.IsMatch(markup, @"Binding\s+Transport\." + property + @"(?![A-Za-z0-9_])");

    private static string Markup(params string[] parts) =>
        Regex.Replace(
            UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine([TestPaths.RepoRoot(), "DotCharter.UI", .. parts]))),
            "<!--.*?-->", string.Empty, RegexOptions.Singleline);
}
