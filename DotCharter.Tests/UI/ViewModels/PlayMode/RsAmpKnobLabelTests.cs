using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tests.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     <c>rsamp</c>'s ten generic knobs on the rig (#88, #755): with the game tables they
///     take the picked amp's knob names and the rest go inactive; without a Rocksmith
///     install every knob goes inactive and says why, neutrally.
/// </summary>
public sealed class RsAmpKnobLabelTests
{
    private const string Hint = "Rocksmith's own amp chains need a Rocksmith 2014 installation.";

    private static List<PedalParamViewModel> Knobs()
    {
        var dsp = new FakeDspChainService();
        return RsAmpKnobs.ParamIds.Select(id => new PedalParamViewModel(dsp, 1, new DspParamInfo(id, id, 0f, 1f, 0.5f), 0.5f)).ToList();
    }

    [Fact]
    public void WithTheTables_TheAmpsKnobsAreNamed_AndTheRestInactive()
    {
        using var game = SyntheticRsGame.Use();
        var knobs = Knobs();
        PedalNodeViewModel.RelabelRsAmpKnobs(knobs, SyntheticRsGame.Model("Amp_TestA"), Hint);

        Assert.Equal(["Gain", "Bass", "Mode"], knobs.Take(3).Select(k => k.DisplayName));
        Assert.All(knobs.Take(3), k => Assert.True(k.IsControlActive));
        Assert.All(knobs.Skip(3), k => Assert.False(k.IsControlActive));
        Assert.All(knobs, k => Assert.Null(k.InactiveHint));
    }

    [Fact]
    public void WithoutAnInstall_EveryKnobIsInactive_AndSaysWhy()
    {
        using var none = SyntheticRsGame.UseNone();
        var knobs = Knobs();
        PedalNodeViewModel.RelabelRsAmpKnobs(knobs, 0f, Hint);

        Assert.All(knobs, k => Assert.False(k.IsControlActive));
        Assert.All(knobs, k => Assert.Equal(Hint, k.InactiveHint));
        Assert.Equal("Knob 1", knobs[0].DisplayName);
    }

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void TheReason_IsInEveryLanguage_AndNeverNamesTheKeys(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file);
        var line = File.ReadLines(path).Single(l => l.Contains("name=\"Rig_RsAmpNeedsInstall\"", StringComparison.Ordinal));
        Assert.Contains("Rocksmith", line, StringComparison.Ordinal);
        Assert.Contains("2014", line, StringComparison.Ordinal);
        Assert.DoesNotContain("key", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Schlüssel", line, StringComparison.Ordinal);
        Assert.DoesNotContain("キー", line, StringComparison.Ordinal);
    }
}
