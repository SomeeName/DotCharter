using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Spectrum;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The tone lab's view-model halves (#299): the A/B level match learning each
///     slot's level and trimming B onto A, the board's per-node meters and chain
///     latency, and the node spectrum's pre/post selection.
/// </summary>
public sealed class ToneLabViewModelTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
    [
        new("amp", "Amp", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new("gain", "Gain", [new DspParamInfo(CompareLevelTrim.GainParamId, "Gain", -24f, 24f, 0f)]),
        new("splitter", "Splitter", []),
        new("merger", "Merger", []),
        new("delay", "Delay", []),
    ];

    private static void Feed(PedalboardViewModel rig, FakeDspChainService chain, double rmsDb, int readings)
    {
        var last = rig.Nodes[^1].NodeId;
        var rms = (float)Math.Pow(10, rmsDb / 20);
        chain.Meters[last] = new DspNodeMeter(rms, rms, rms * 2, rms);
        for (var i = 0; i < readings; i++)
        {
            rig.OutputMeters.Poll();
            rig.CompareLevel.Sample();
        }
    }

    // ── level-matched A/B ────────────────────────────────────────────────────

    [Fact]
    public void ToneCompareLevelMatch_LearnsRawLevels_AndMatchesBToA()
    {
        var match = new ToneCompareLevelMatch();
        for (var i = 0; i < ToneCompareLevelMatch.MinReadings; i++) match.Record(ToneCompareSlot.A, -20, 0);
        Assert.Null(match.MeasuredDb(ToneCompareSlot.B));
        // B was heard while a +4 dB trim was on: its raw level is 4 dB lower.
        for (var i = 0; i < ToneCompareLevelMatch.MinReadings; i++) match.Record(ToneCompareSlot.B, -22, 4);
        Assert.Equal(-26, match.MeasuredDb(ToneCompareSlot.B)!.Value, 6);
        Assert.Equal(6, match.TrimFor(ToneCompareSlot.B, null, null), 6);
        Assert.Equal(0, match.TrimFor(ToneCompareSlot.A, null, null));

        // silence between phrases is gated out
        match.Record(ToneCompareSlot.B, -90, 0);
        Assert.Equal(-26, match.MeasuredDb(ToneCompareSlot.B)!.Value, 6);

        match.Forget(ToneCompareSlot.B);
        Assert.Null(match.MeasuredDb(ToneCompareSlot.B));
    }

    [Fact]
    public void ToneCompareLevelMatch_FallsBackToTheStaticEstimate_UntilBothSlotsAreHeard()
    {
        var match = new ToneCompareLevelMatch();
        var a = new DcRigPreset([new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = 0f })]);
        var b = new DcRigPreset([new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = -5f })]);
        Assert.Equal(5, match.TrimFor(ToneCompareSlot.B, a, b), 3);
    }

    [Fact]
    public void SwitchingToB_TrimsTheTrailingGainOntoAsLevel_AndTheSlotStoresItUntrimmed()
    {
        var chain = new FakeDspChainService(Catalog()) { MeteringActive = true };
        var presets = new FakeDspTonePresetService();
        using var rig = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English());
        chain.AddNode("amp");
        var gain = chain.AddNode("gain")!.NodeId;
        rig.Sync();
        rig.OutputMeters.SetSurfaceLive(this, true);
        rig.SeedCompareCommand.Execute(null);

        Feed(rig, chain, -20, ToneCompareLevelMatch.MinReadings);         // A heard at -20
        rig.ToggleCompareCommand.Execute(null);                            // fork into B
        Feed(rig, chain, -27, ToneCompareLevelMatch.MinReadings * 4);      // B heard at -27
        rig.SelectCompareSlotCommand.Execute("A");
        Assert.False(rig.CompareLevel.HasTrim);

        rig.SelectCompareSlotCommand.Execute("B");
        Assert.Equal(7f, chain.GetParam(gain, CompareLevelTrim.GainParamId)!.Value, 1);
        Assert.True(rig.CompareLevel.HasTrim);
        Assert.StartsWith("+7", rig.CompareLevel.TrimLabel, StringComparison.Ordinal);

        // Leaving B stores it WITHOUT the trim, so the next switch does not stack a second one.
        rig.SelectCompareSlotCommand.Execute("A");
        rig.SelectCompareSlotCommand.Execute("B");
        var appliedB = presets.AppliedPresetsPerPlayer[^1].Preset.Rig.Modules;
        Assert.Equal(0f, appliedB[^1].Params[CompareLevelTrim.GainParamId], 3);

        // Turning the match off takes the trim straight back off the chain.
        var trimmed = chain.GetParam(gain, CompareLevelTrim.GainParamId)!.Value;
        rig.CompareLevel.IsMatched = false;
        Assert.Equal(trimmed - 7f, chain.GetParam(gain, CompareLevelTrim.GainParamId)!.Value, 1);
        Assert.False(rig.CompareLevel.HasTrim);
        rig.OutputMeters.SetSurfaceLive(this, false);
    }

    // ── board meters + chain latency ─────────────────────────────────────────

    [Fact]
    public void BoardMeters_PutEachUnitsOutputOnItsCard_OnlyWhileLeased()
    {
        var chain = new FakeDspChainService(Catalog()) { MeteringActive = true };
        using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        var amp = chain.AddNode("amp")!.NodeId;
        var delay = chain.AddNode("delay")!.NodeId;
        rig.Sync();
        chain.Meters[amp] = new DspNodeMeter(0.1f, 0.05f, 0.5f, 0.25f);

        rig.BoardMeters.Poll();
        Assert.Equal(0, chain.MeterLeases);
        Assert.All(rig.Nodes, n => Assert.False(n.HasBoardMeter));

        rig.BoardMeters.SetSurfaceLive(this, true);
        Assert.Equal(1, chain.MeterLeases);
        var ampCard = rig.Nodes.Single(n => n.NodeId == amp);
        Assert.True(ampCard.HasBoardMeter);
        Assert.Equal(-6.0206, ampCard.BoardOutputDb, 3);
        // a unit the engine has no reading for draws no meter rather than an empty one
        Assert.False(rig.Nodes.Single(n => n.NodeId == delay).HasBoardMeter);

        rig.BoardMeters.SetSurfaceLive(this, false);
        Assert.Equal(0, chain.MeterLeases);
        Assert.False(ampCard.HasBoardMeter);
    }

    [Fact]
    public void ChainLatency_SumsSerialUnits_AndTakesTheSlowestLaneOfASplit()
    {
        static RigLatencyUnit S(int? samples) => new(false, false, 0, samples);
        Assert.Equal(300, RigChainLatency.TotalSamples([S(100), S(0), S(200)]));
        Assert.Equal(100 + 64 + 512 + 0 + 10, RigChainLatency.TotalSamples(
        [
            S(100),
            new(true, false, 0, 0),
            new(false, false, 1, 64),
            new(false, false, 2, 500),
            new(false, false, 2, 12),
            new(false, false, 0, 0),     // unassigned: lane 1
            new(false, true, 0, 64),
            S(10),
        ]));
        Assert.Null(RigChainLatency.TotalSamples([S(100), S(null)]));
        Assert.Null(RigChainLatency.TotalSamples([]));
    }

    [Fact]
    public void BoardMeters_ReadTheChainLatency_FromTheCards()
    {
        var types = Catalog();
        var chain = new RigCapabilityChain(types);
        using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.KeysOnly(),
            audio: new FakeAudioPlaybackService { ActiveSampleRate = 48_000 });
        var amp = chain.AddNode("amp")!.NodeId;
        var delay = chain.AddNode("delay")!.NodeId;
        chain.Latency[amp] = 256;
        chain.Latency[delay] = 2048;
        rig.Sync();

        rig.BoardMeters.Poll();
        Assert.Equal(2304, rig.BoardMeters.ChainLatencySamples);
        Assert.True(rig.BoardMeters.HasChainLatency);
        Assert.Equal($"Rig_ChainLatencyLabel(2304, {48.0.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)})",
            rig.BoardMeters.ChainLatencyText);

        var unmeasured = new RigCapabilityChain(types) { AnswersLatency = false };
        using var blind = TestRigBuilders.Pedalboard(unmeasured, new FakeDspTonePresetService(), FakeLocalization.KeysOnly());
        unmeasured.AddNode("amp");
        blind.Sync();
        blind.BoardMeters.Poll();
        Assert.Null(blind.BoardMeters.ChainLatencySamples);
        Assert.False(blind.BoardMeters.HasChainLatency);
        Assert.Equal(string.Empty, blind.BoardMeters.ChainLatencyText);
    }

    // ── node spectrum ────────────────────────────────────────────────────────

    private sealed class Renderer : IOfflineToneRenderer
    {
        public readonly List<string[]> Rendered = [];
        public bool Fail;

        public bool TryRender(DcRigPreset rig, float[] left, float[] right, int sampleRate)
        {
            Rendered.Add([.. rig.Modules.Select(m => m.ModuleTypeId)]);
            if (Fail) return false;
            // each unit halves the level, so PRE and POST differ by 6 dB
            var scale = MathF.Pow(0.5f, rig.Modules.Count);
            for (var i = 0; i < left.Length; i++) left[i] *= scale;
            return true;
        }
    }

    [Fact]
    public async Task Spectrum_MeasuresBothTaps_AndTheTapPicksTheBrightCurve()
    {
        var chain = new FakeDspChainService(Catalog());
        chain.AddNode("gain");
        var amp = chain.AddNode("amp")!.NodeId;
        chain.AddNode("delay");
        var renderer = new Renderer();
        int? selected = amp;
        var vm = new RigNodeSpectrumViewModel(() => renderer, () => chain, () => selected);

        Assert.Null(vm.Bands);
        await vm.MeasureCommand.ExecuteAsync(null);
        Assert.Equal(RigSpectrumState.Ready, vm.State);
        Assert.Equal(2, renderer.Rendered.Count);
        Assert.Equal(["gain"], renderer.Rendered[0]);
        Assert.Equal(["gain", "amp"], renderer.Rendered[1]);

        Assert.True(vm.IsPost);
        var post = vm.Bands!;
        var pre = vm.ReferenceBands!;
        Assert.Equal(RigNodeSpectrumViewModel.BandCount, post.Length);
        var loudest = Array.IndexOf(pre, pre.Max());
        Assert.Equal(pre[loudest] - 6.02, post[loudest], 1);

        vm.SelectTapCommand.Execute("Pre");
        Assert.True(vm.IsPre);
        Assert.Same(pre, vm.Bands);
        Assert.Same(post, vm.ReferenceBands);

        // a different unit selected: the old curve is dropped, not shown under the new name
        selected = null;
        vm.SyncNode();
        Assert.Equal(RigSpectrumState.Idle, vm.State);
        Assert.Null(vm.Bands);
    }

    [Fact]
    public async Task Spectrum_SaysWhy_WhenItCannotMeasure()
    {
        var chain = new FakeDspChainService(Catalog());
        chain.AddNode("splitter");
        var inSplit = chain.AddNode("delay")!.NodeId;
        chain.AddNode("merger");
        var renderer = new Renderer();
        var vm = new RigNodeSpectrumViewModel(() => renderer, () => chain, () => inSplit);
        await vm.MeasureCommand.ExecuteAsync(null);
        Assert.True(vm.IsUnsupported);
        Assert.Empty(renderer.Rendered);

        var none = new RigNodeSpectrumViewModel(() => null, () => chain, () => inSplit);
        Assert.True(none.IsUnavailable);
        Assert.False(none.MeasureCommand.CanExecute(null));

        // the splitter itself: its POST would cut the split open
        var splitter = new RigNodeSpectrumViewModel(() => renderer, () => chain, () => chain.GetNodes()[0].NodeId);
        await splitter.MeasureCommand.ExecuteAsync(null);
        Assert.True(splitter.IsUnsupported);
        Assert.Empty(renderer.Rendered);
    }

    [Fact]
    public async Task Spectrum_ReportsAFailedRender()
    {
        var chain = new FakeDspChainService(Catalog());
        var amp = chain.AddNode("amp")!.NodeId;
        var vm = new RigNodeSpectrumViewModel(() => new Renderer { Fail = true }, () => chain, () => amp);
        await vm.MeasureCommand.ExecuteAsync(null);
        Assert.True(vm.IsFailed);
        Assert.Null(vm.Bands);
    }
}
