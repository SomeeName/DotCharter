using DotCharter.Localization;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The engine names its knobs and picker entries in English. A rig built in German shows the
///     words it has a translation for in German, keeps the gear model and note names as the engine
///     wrote them, and still writes the same values to the chain.
/// </summary>
public sealed class PedalParamLocalizationTests
{
    [Fact]
    public void AStringParameterCaption_RelabelsLive_AndKeepsTheStoredValue()
    {
        var localization = FakeLocalization.English();
        var param = new PedalStringParamViewModel(new FakeDspChainService(), 1,
            new DspStringParamInfo("phrases", "Phrase 1 Lyrics", ""), "la la", null, localization);
        var changed = new List<string?>();
        param.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        localization.SwitchTo("ja-JP");

        Assert.Equal(localization["DspLabel_Phrase1Lyrics"], param.DisplayName);
        Assert.Equal("la la", param.Value);
        Assert.Contains(nameof(param.DisplayName), changed);
    }

    [Theory]
    [InlineData("Gain (dB)", "Gain", "dB")]
    [InlineData("Attack (ms)", "Attack", "ms")]
    [InlineData("Mix", "Mix", null)]
    public void CommonCaptions_HaveJapaneseTranslations_AndPreserveUnits(string english, string caption, string? unit)
    {
        var japanese = FakeLocalization.For("ja-JP");
        var param = Param(new DspParamInfo("p", english, 0f, 1f, 0f), japanese);

        Assert.NotEqual(caption, param.Caption);
        Assert.Equal(unit, param.Unit);
    }

    [Fact]
    public void ALoopersTransportAndUndo_RelabelLive_WithoutWritingToTheEngine()
    {
        var localization = FakeLocalization.English();
        var dsp = new FakeDspChainService();
        var transport = new DspParamInfo("state", "Transport", 0f, 3f, 0f)
        {
            ValueLabels = ["Stop", "Record", "Play", "Overdub"]
        };
        var undo = new DspParamInfo("undo", "Undo", 0f, 1f, 0f) { ValueLabels = ["-", "Undo"] };
        var type = new DspModuleType("looper", "Looper", [transport, undo]);
        var node = new PedalNodeViewModel(dsp, new DspNode(1, "looper", false, new Dictionary<string, float>()), type, localization);
        var states = node.TransportParams[0].States.ToArray();
        var sets = dsp.ParamSets.Count;

        localization.SwitchTo("ja-JP");

        Assert.Equal(localization["DspLabel_Transport"], node.TransportParams[0].DisplayName);
        Assert.Equal(localization["DspLabel_Record"], states[1].Label);
        Assert.Equal(localization["DspLabel_Undo"], node.MomentaryParams[0].ActionLabel);
        Assert.Equal(states, node.TransportParams[0].States);
        Assert.Equal(sets, dsp.ParamSets.Count);
    }

    [Fact]
    public void ALanguageSwitch_RelabelsAnExistingParam_WithoutChangingItsValue()
    {
        var localization = FakeLocalization.English();
        var param = Param(new DspParamInfo("shape", "Waveform", 0f, 2f, 1f)
        {
            ValueLabels = ["Sine", "Square", "Plexi 100"]
        }, localization);
        var changed = new List<string?>();
        param.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        localization.SwitchTo("ja-JP");

        Assert.Equal(localization["DspLabel_Waveform"], param.DisplayName);
        Assert.Equal(localization["DspLabel_Square"], param.CurrentLabel);
        Assert.Equal("Plexi 100", param.ValueLabels![2]);
        Assert.Equal(1, param.SelectedIndex);
        Assert.Equal(1f, param.Value);
        Assert.Contains(nameof(param.DisplayName), changed);
        Assert.Contains(nameof(param.Caption), changed);
        Assert.Contains(nameof(param.ValueLabels), changed);
        Assert.Contains(nameof(param.CurrentLabel), changed);
    }

    private static PedalParamViewModel Param(DspParamInfo info, ILocalizationService localization) =>
        new(new FakeDspChainService(), 1, info, info.Default, localization);

    [Fact]
    public void AGermanRig_ShowsTheTranslatedCaptionAndChoices_AndKeepsModelNames()
    {
        var german = FakeLocalization.For("de-DE");
        var waveform = Param(new DspParamInfo("shape", "Waveform", 0f, 2f, 0f) { ValueLabels = ["Sine", "Square", "Plexi 100"] },
            german);
        var direction = Param(new DspParamInfo("reverse", "Direction", 0f, 1f, 0f) { ValueLabels = ["Forward", "Reverse"] }, german);

        Assert.Equal(german["DspLabel_Waveform"], waveform.DisplayName);
        Assert.NotEqual("Waveform", waveform.DisplayName);
        Assert.Equal([german["DspLabel_Sine"], german["DspLabel_Square"], "Plexi 100"], waveform.ValueLabels!);
        Assert.NotEqual("Sine", waveform.ValueLabels![0]);
        Assert.Equal([german["DspLabel_Forward"], german["DspLabel_Reverse"]], direction.ValueLabels!);
        Assert.NotEqual("Forward", direction.ValueLabels![0]);
        Assert.Equal(german["DspLabel_Direction"], direction.DisplayName);
    }

    [Fact]
    public void AUnitSuffix_SurvivesTheTranslation_SoTheFaceStillSplitsIt()
    {
        var german = FakeLocalization.For("de-DE");
        var level = Param(new DspParamInfo("levelDb", "Level (dB)", -24f, 24f, 0f), german);

        Assert.Equal("dB", level.Unit);
        Assert.NotEqual("Level", level.Caption);
    }

    [Fact]
    public void InEnglish_OrWithoutAnEntry_TheEngineTextIsShown()
    {
        var info = new DspParamInfo("shape", "Waveform", 0f, 1f, 0f) { ValueLabels = ["Sine", "Plexi 100"] };

        Assert.Equal("Waveform", Param(info, FakeLocalization.KeysOnly()).DisplayName);
        Assert.Equal(["Sine", "Plexi 100"], Param(info, FakeLocalization.English()).ValueLabels!);
        Assert.Equal(["Sine", "Plexi 100"], Param(info, FakeLocalization.KeysOnly()).ValueLabels!);
    }
}
