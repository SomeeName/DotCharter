using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     #140: every slider in the toolbar popovers (Mix, Tempo, …) resets to its
///     default on a double-click, the way the Mixer inspector's faders do — through
///     <c>ResetOnDoubleClick</c>. The lint finds a slider with no reset wiring; the
///     behaviour fact runs each key the popovers name through the transport's one
///     reset switch, so a key that switch does not know is a reset that does nothing.
/// </summary>
public sealed class PopoverSliderResetTests
{
    private static readonly Regex SliderTag = new(@"<Slider\b[^>]*>", RegexOptions.Singleline);
    private static readonly Regex ResetKey = new(@"ResetOnDoubleClick\.CommandParameter=""(\w+)""");

    private static IEnumerable<string> PopoverViews() =>
        UiSourceTree.EnumerateFiles(
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Popovers")),
            "*.axaml", SearchOption.AllDirectories);

    [Fact]
    public void EveryPopoverSlider_ResetsOnDoubleClick()
    {
        var sliders = 0;
        var missing = new List<string>();
        foreach (var file in PopoverViews())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in SliderTag.Matches(text))
            {
                sliders++;
                if (!m.Value.Contains("ResetOnDoubleClick.", StringComparison.Ordinal))
                    missing.Add($"{Path.GetFileName(file)}:{text[..m.Index].Count(c => c == '\n') + 1}");
            }
        }

        // Mix (3) + Tempo (2) when this was written; more popovers may add more.
        Assert.True(sliders >= 5, $"only {sliders} popover sliders found — the walk no longer finds the views");
        Assert.True(missing.Count == 0, "popover sliders with no double-click reset:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryResetKeyThePopoversName_PutsItsSliderBack()
    {
        var keys = PopoverViews()
            .SelectMany(f => ResetKey.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        Assert.NotEmpty(keys);

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var readers = new Dictionary<string, (Action Move, Func<bool> AtDefault)>
        {
            ["SongGainSlider"] = (() => transport.SongVolume = 0.3, () => transport.SongVolume == 1.0),
            ["InputGainSlider"] = (() => transport.InputVolume = 0.3, () => transport.InputVolume == 1.0),
            ["MidiInstSlider"] = (() => transport.MidiVolume = 0.3, () => transport.MidiVolume == 1.0),
            ["SpeedSlider"] = (() => transport.PlaybackSpeed = 0.5, () => transport.PlaybackSpeed == 1.0),
            ["PitchShiftSlider"] = (() => transport.PitchShift = -3, () => transport.PitchShift == 0),
        };

        foreach (var key in keys)
        {
            Assert.True(readers.TryGetValue(key, out var probe), $"no probe for popover reset key '{key}' — add one here");
            probe.Move();
            Assert.False(probe.AtDefault(), $"'{key}': the probe did not move the value off its default");
            transport.ResetMixerParameterCommand.Execute(key);
            Assert.True(probe.AtDefault(), $"'{key}' does not reset its slider");
        }
    }
}
