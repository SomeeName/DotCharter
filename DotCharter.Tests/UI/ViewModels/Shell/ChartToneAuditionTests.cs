using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tests.Ai.Chat;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>The Tones review's Audition (#614) over the REAL chart tone preview and tone coordinator (fake DSP
/// chain). The coordinator is built without a HUD adapter, so it writes no process-wide state and the class
/// needs no collection (<c>GameplayToneHudCollectionLintTests</c> names the classes that do).</summary>
public sealed class ChartToneAuditionTests
{
    private readonly AiChatToneFixture _f = new();

    /// <summary>
    ///     The shipping audition over the REAL chart tone preview: the proposed body replaces the committed one
    ///     on the rig (the tone at the playhead is rebuilt from it), the preview toggle is switched on for it
    ///     and back off after, and the transport is asked to play the window once.
    /// </summary>
    [Fact]
    public async Task The_real_audition_puts_the_proposed_body_on_the_rig_and_hands_everything_back()
    {
        var chain = new FakeDspChainService(
            new[] { "amp", "cab", "ds1", "chorus", "eq", "reverb", "delay" }
                .Select(id => new DspModuleType(id, id, new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
                .ToList());
        var tones = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), _f.State, new FakeConfigurationService());
        var preview = new ChartTonePreviewController(tones, _f.State, _f.Loc);
        var plays = new List<(double, double)>();
        var audition = new ChartToneAudition(() => preview, (from, to) =>
        {
            plays.Add((from, to));
            return true;
        }, _f.Loc);
        Assert.True(preview.IsAvailable, preview.Tip);
        Assert.False(preview.IsOn);
        _f.State.CurrentTimeMs = 7000; // inside Lead_Dist

        // A distortion pedal in front of the default amp: a body the committed one does not have.
        var json = """{"Key":"Lead_Dist","Name":"Lead Dist","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}},"PrePedal1":{"Type":"Pedals","Key":"Pedal_Distortion","Category":"Distortion","KnobValues":{}}}}""";
        var why = audition.Start("Lead_Dist", json, 4000, 8000);

        Assert.Null(why);
        Assert.Equal([(4000.0, 8000.0)], plays);
        Assert.True(preview.IsOn, "the audition did not put the rig on the chart's tones");
        // The apply runs on the coordinator's worker: a yielding poll, its expiry asserted (a deadlock detector).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!chain.GetNodes().Any(n => n.ModuleTypeId == "ds1") && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(chain.GetNodes().Any(n => n.ModuleTypeId == "ds1"),
            "the proposed body never reached the rig (or the box was starved for 60 s): " +
            string.Join(", ", chain.GetNodes().Select(n => n.ModuleTypeId)));

        audition.Stop();
        Assert.False(preview.IsOn, "the audition left the preview toggle on");
        audition.Stop(); // idempotent
    }
}
