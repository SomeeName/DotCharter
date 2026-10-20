using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using JuceSharp.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The four capabilities the JuceSharp 0.9 gear wave's 43 new modules need
///     and the pedalboard had never rendered: a LOG taper on a frequency dial,
///     a per-node LATENCY readout, the ABI-v34 SIDECHAIN key, and TRANSPORT /
///     MOMENTARY renderings for a named param a picker cannot honestly drive.
///     Plus the fifth thing, which is a hole the wave only made reachable from
///     a second module: a file the engine REFUSED used to look identical to one
///     it loaded.
/// </summary>
public sealed class RigParamCapabilitiesTests
{
    // ══ 1. the log taper ═════════════════════════════════════════════════════

    /// <summary>The reported problem, as arithmetic. On a LINEAR 20–20,000 Hz
    /// dial 1 kHz sits at 4.9 % of travel, which puts a guitar's whole
    /// fundamental range inside the first twentieth of the sweep.</summary>
    [Fact]
    public void AFrequencyDial_PutsOneKilohertzNearTheMiddle_NotAtFivePercent()
    {
        Assert.Equal(KnobTaper.Logarithmic, KnobMath.TaperFor("cutoffHz", 20f, 20000f, 0f));

        var linear = KnobMath.FractionFromValue(1000, 20, 20000);
        Assert.True(linear < 0.06, $"the shipped linear dial put 1 kHz at {linear:P1}");

        var log = KnobMath.FractionFromValue(1000, 20, 20000, KnobTaper.Logarithmic);
        Assert.InRange(log, 0.45, 0.70);

        // The ends still pin, and the mapping is a real inverse — a knob whose
        // round trip drifted would walk the value on every repaint.
        Assert.Equal(0.0, KnobMath.FractionFromValue(20, 20, 20000, KnobTaper.Logarithmic), 9);
        Assert.Equal(1.0, KnobMath.FractionFromValue(20000, 20, 20000, KnobTaper.Logarithmic), 9);
        foreach (var value in new[] { 20.0, 63.0, 440.0, 1000.0, 5000.0, 20000.0 })
        {
            var fraction = KnobMath.FractionFromValue(value, 20, 20000, KnobTaper.Logarithmic);
            Assert.Equal(value, KnobMath.ValueFromFraction(fraction, 20, 20000, KnobTaper.Logarithmic), 6);
        }
    }

    /// <summary>The DSP value never changes — only where the pointer has to be.
    /// The mid-travel value of the 20–20,000 dial is a real frequency in plain
    /// hertz, not a normalized 0..1 the caller would have to un-map.</summary>
    [Fact]
    public void TheTaper_MovesTheKnob_NeverTheValue()
    {
        var mid = KnobMath.ValueFromFraction(0.5, 20, 20000, KnobTaper.Logarithmic);
        Assert.Equal(632.5, mid, 1); // sqrt(20 * 20000)
        Assert.True(mid is > 20 and < 20000);
    }

    /// <summary>Every param that is NOT a frequency or a multi-decade sweep is
    /// bit-identical to what shipped. Asserted as EQUALITY against the 3-arg
    /// overload, not as "looks about the same".</summary>
    [Theory]
    [InlineData("drive", 0f, 1f, 0f)]            // the ordinary unit-range knob
    [InlineData("mix", 0f, 1f, 0f)]
    [InlineData("gainDb", -24f, 24f, 0f)]        // bipolar dB
    [InlineData("shiftHz", -1200f, 1200f, 0f)]   // a FREQUENCY id that is bipolar
    [InlineData("monoBelowHz", 0f, 500f, 0f)]    // a frequency id whose 0 means "off"
    [InlineData("stages", 2f, 12f, 2f)]          // an ORDINAL count
    [InlineData("downsample", 1f, 64f, 1f)]      // ordinal AND 1.8 decades
    [InlineData("size", 0f, 1f, 0f)]
    public void EveryLinearParam_IsBitIdenticalToWhatShipped(
        string paramId, float min, float max, float step)
    {
        Assert.Equal(KnobTaper.Linear, KnobMath.TaperFor(paramId, min, max, step));

        for (var i = 0; i <= 40; i++)
        {
            var value = min + (max - min) * i / 40.0;
            var taper = KnobMath.TaperFor(paramId, min, max, step);
            Assert.Equal(KnobMath.FractionFromValue(value, min, max),
                KnobMath.FractionFromValue(value, min, max, taper));
            Assert.Equal(KnobMath.ValueFromFraction(i / 40.0, min, max),
                KnobMath.ValueFromFraction(i / 40.0, min, max, taper));
            Assert.Equal(KnobMath.AngleDegreesForValue(value, min, max),
                KnobMath.AngleDegreesForValue(value, min, max, taper));
            Assert.Equal(KnobMath.ApplyVerticalDrag(value, 30, min, max, 200),
                KnobMath.ApplyVerticalDrag(value, 30, min, max, 200, taper));
        }
    }

    /// <summary>Both triggers, and the two exclusions that keep them honest.</summary>
    [Theory]
    [InlineData("cutoffHz", 20f, 20000f, 0f, true)]    // svf — asked for by name
    [InlineData("freqHz", 20f, 5000f, 0f, true)]       // comb
    [InlineData("notchHz", 200f, 5000f, 0f, true)]     // acpre
    [InlineData("toneHz", 500f, 8000f, 0f, true)]      // a frequency under 2 decades
    [InlineData("timeMs", 1f, 2000f, 0f, true)]        // 3.3 decades, not a frequency
    [InlineData("attackMs", 1f, 100f, 0f, true)]       // exactly 2 decades
    [InlineData("releaseMs", 10f, 500f, 0f, false)]    // 1.7 decades, not a frequency
    [InlineData("cutoffHz", 20f, 20000f, 1f, false)]   // ORDINAL wins: a count stays linear
    public void TheTaper_IsDecidedFromCatalogueMetadata_NotFromAModuleTable(
        string paramId, float min, float max, float step, bool logarithmic)
    {
        Assert.Equal(logarithmic ? KnobTaper.Logarithmic : KnobTaper.Linear,
            KnobMath.TaperFor(paramId, min, max, step));
    }

    /// <summary>…and the param view-model carries the decision, so the knob in
    /// the rig editor binds it rather than re-deriving it.</summary>
    [Fact]
    public void TheParamViewModel_CarriesTheTaper()
    {
        var type = new DspModuleType("svf", "State Variable Filter",
        [
            new DspParamInfo("cutoffHz", "Cutoff (Hz)", 20f, 20000f, 1000f),
            new DspParamInfo("resonance", "Resonance", 0f, 1f, 0.3f)
        ]);
        var node = Node(new RigCapabilityChain([type]), type);

        Assert.Equal(KnobTaper.Logarithmic, node.Params.Single(p => p.Id == "cutoffHz").Taper);
        Assert.Equal(KnobTaper.Linear, node.Params.Single(p => p.Id == "resonance").Taper);
    }

    // ══ 2. per-node latency ══════════════════════════════════════════════════

    [Fact]
    public void ANodeWithLatency_ShowsIt_AndAZeroLatencyNodeShowsNothing()
    {
        var type = new DspModuleType("convreverb", "Convolution Reverb",
            [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]);

        var chain = new RigCapabilityChain([type]);
        var loud = Node(chain, type, id => chain.Latency[id] = 2048, withAudio: true);
        Assert.True(loud.HasLatency);
        Assert.Equal(2048, loud.LatencySamples);
        Assert.Contains("2048", loud.LatencyText);
        // Both units, because samples is what the engine compensates in and
        // milliseconds is what the player feels: 2048 / 44,100 s. Asserted in
        // the CURRENT culture's spelling, because that is what the readout is
        // formatted in - a hard-coded "46.4" passes on an en box and fails on
        // the owner's de-DE one, which is a false failure and then a false fix.
        Assert.Contains((2048 * 1000.0 / 44100).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture),
            loud.LatencyText);

        var quiet = Node(new RigCapabilityChain([type]), type);
        Assert.Equal(0, quiet.LatencySamples);
        Assert.False(quiet.HasLatency);
        Assert.Equal(string.Empty, quiet.LatencyText);
    }

    /// <summary>"Measured 0" and "not measured" are different answers, and a
    /// readout that folded them would print a latency for a chain it never
    /// asked. Both HIDE the row; only one of them has a number.</summary>
    [Fact]
    public void NoReading_IsNotZero()
    {
        var type = new DspModuleType("limiter", "Limiter", [new DspParamInfo("ceilingDb", "Ceiling", -24f, 0f, -1f)]);
        var chain = new RigCapabilityChain([type]) { AnswersLatency = false };
        var node = Node(chain, type);

        Assert.Null(node.LatencySamples);
        Assert.False(node.HasLatency);
    }

    /// <summary>The refresh is driven by the same beats the meters and the NAM
    /// badge use — a param move and the board's sync — never by a timer.</summary>
    [Fact]
    public void Latency_IsRefreshedWhenAParamMoves()
    {
        var type = new DspModuleType("whammy", "Whammy",
            [new DspParamInfo("lowLatency", "Low Latency", 0f, 1f, 0f) { Step = 1f }]);
        var chain = new RigCapabilityChain([type]);
        var node = Node(chain, type, id => chain.Latency[id] = 1272);
        Assert.Equal(1272, node.LatencySamples);

        chain.Latency[node.NodeId] = 318;
        node.Params.Single().Value = 1f;
        Assert.Equal(318, node.LatencySamples);
    }

    // ══ 3. the sidechain key ═════════════════════════════════════════════════

    [Fact]
    public void ThePicker_IsOfferedOnlyWhereTheCatalogueSaysTheEngineAcceptsAKey()
    {
        var ducker = new DspModuleType("ducker", "Ducker",
            [new DspParamInfo("depthDb", "Depth", -40f, 0f, -12f)])
        { SupportsSidechain = true };
        var rat = new DspModuleType("rat", "Rat",
            [new DspParamInfo("distortion", "Distortion", 0f, 1f, 0.5f)]);

        var chain = new RigCapabilityChain([ducker, rat]);
        var keyed = Node(chain, ducker);
        Assert.True(keyed.SupportsSidechain);
        Assert.Equal(3, keyed.SidechainOptions.Count);

        var drive = Node(chain, rat);
        Assert.False(drive.SupportsSidechain);
        Assert.Empty(drive.SidechainOptions);
    }

    [Fact]
    public void ChoosingSongMix_CallsTheServiceWithTheWrappersOwnEnumValue()
    {
        var ducker = new DspModuleType("ducker", "Ducker",
            [new DspParamInfo("depthDb", "Depth", -40f, 0f, -12f)])
        { SupportsSidechain = true };
        var chain = new RigCapabilityChain([ducker]);
        var node = Node(chain, ducker);

        Assert.Equal(JuceDspSidechainSource.None, node.SidechainSource);

        node.SelectedSidechainOption = node.SidechainOptions.Single(o => o.Source == JuceDspSidechainSource.SongMix);

        Assert.Equal((node.NodeId, JuceDspSidechainSource.SongMix), Assert.Single(chain.SidechainSets));
        Assert.Equal(JuceDspSidechainSource.SongMix, node.SidechainSource);
        // The numbers are ABI and are persisted — a managed re-typing that
        // renumbered them would send the engine the wrong source silently.
        Assert.Equal(2, (int)JuceDspSidechainSource.SongMix);
        Assert.Equal(1, (int)JuceDspSidechainSource.SeatInput);
        Assert.Equal(0, (int)JuceDspSidechainSource.None);
    }

    /// <summary>A REJECTED key leaves the picker on what is really running. The
    /// engine refuses one on every module that does not advertise the
    /// capability, so this is the ordinary case, not the exotic one.</summary>
    [Fact]
    public void ARejectedKey_LeavesThePickerOnTheSourceThatIsRunning()
    {
        var ducker = new DspModuleType("ducker", "Ducker",
            [new DspParamInfo("depthDb", "Depth", -40f, 0f, -12f)])
        { SupportsSidechain = true };
        var chain = new RigCapabilityChain([ducker]) { FailSidechain = true };
        var node = Node(chain, ducker);

        node.SidechainSource = JuceDspSidechainSource.SeatInput;

        Assert.Equal(JuceDspSidechainSource.None, node.SidechainSource);
        Assert.Equal(JuceDspSidechainSource.None, node.SelectedSidechainOption!.Source);
    }

    // ══ 4. transport and momentary ═══════════════════════════════════════════

    private static DspModuleType Looper() => new("looper", "Looper",
    [
        new DspParamInfo("state", "State", 0f, 3f, 0f)
            { ValueLabels = ["Stop", "Record", "Play", "Overdub"] },
        new DspParamInfo("undo", "Undo", 0f, 1f, 0f) { ValueLabels = ["-", "Undo"] },
        new DspParamInfo("maxLoopS", "Max Loop (s)", 1f, 60f, 30f),
        new DspParamInfo("mix", "Mix", 0f, 1f, 1f)
    ]);

    [Fact]
    public void TheLooper_RendersATransportAndAMomentary_NotTwoPickers()
    {
        var type = Looper();
        var node = Node(new RigCapabilityChain([type]), type);

        Assert.Equal(RigParamRole.Transport, RigParamRoles.RoleOf("looper", "state"));
        Assert.Equal(RigParamRole.Momentary, RigParamRoles.RoleOf("looper", "undo"));
        Assert.Equal(RigParamRole.Choice, RigParamRoles.RoleOf("tremolo", "shape"));

        var transport = Assert.Single(node.TransportParams);
        Assert.Equal("state", transport.Id);
        Assert.Equal(["Stop", "Record", "Play", "Overdub"], transport.States.Select(s => s.Label));

        var momentary = Assert.Single(node.MomentaryParams);
        Assert.Equal("undo", momentary.Id);

        // …and NEITHER is still a picker: ModelSelectors is the only surface a
        // picker appears on, so leaving them there would draw both renderings.
        Assert.Empty(node.ModelSelectors);
        Assert.DoesNotContain(node.KnobParams, p => p.Id is "state" or "undo");
        // The knobs are untouched.
        Assert.Equal(["maxLoopS", "mix"], node.KnobParams.Select(p => p.Id));
    }

    /// <summary>The reason the transport is polled at all: the ENGINE closes the
    /// take itself at <c>maxLoopS</c>, so a control showing its last WRITE would
    /// sit on Record over a pedal that had already gone to Play.</summary>
    [Fact]
    public void TheTransportState_FollowsAnEngineDrivenChange_AfterARefresh()
    {
        var type = Looper();
        var chain = new RigCapabilityChain([type]);
        var node = Node(chain, type);
        var transport = Assert.Single(node.TransportParams);

        transport.States[1].SelectCommand.Execute(null);      // the user hits Record
        Assert.Equal((node.NodeId, "state", 1f), Assert.Single(chain.ParamSets));
        Assert.Equal(1, transport.SelectedIndex);
        Assert.True(transport.States[1].IsActive);

        chain.SetParamFromEngine(node.NodeId, "state", 2f);    // the engine auto-closes
        Assert.Equal(1, transport.SelectedIndex);              // …until something asks
        node.RefreshPolledState();

        Assert.Equal(2, transport.SelectedIndex);
        Assert.Equal("Play", transport.CurrentLabel);
        Assert.False(transport.States[1].IsActive);
        Assert.True(transport.States[2].IsActive);
    }

    /// <summary>The trigger is WRITE-ONLY and shows no state at all: it reads
    /// back 0, so anything that mirrored the value would flash "Undo" and snap
    /// straight back — and a two-way binding would read that snap-back as an
    /// edit and re-fire.</summary>
    [Fact]
    public void TheMomentaryTrigger_WritesItsActionAndNeverDisplaysAState()
    {
        var type = Looper();
        var chain = new RigCapabilityChain([type]);
        var node = Node(chain, type);
        var undo = Assert.Single(node.MomentaryParams);

        Assert.Equal("Undo", undo.ActionLabel);   // the ACTION's name, never "-"
        Assert.DoesNotContain("On", undo.ActionLabel, StringComparison.Ordinal);

        undo.TriggerCommand.Execute(null);
        Assert.Equal((node.NodeId, "undo", 1f), Assert.Single(chain.ParamSets));

        // The engine performed it and answered its resting value. Nothing on the
        // control moved, because there is nothing on it that could.
        chain.SetParamFromEngine(node.NodeId, "undo", 0f);
        node.RefreshPolledState();
        Assert.Equal("Undo", undo.ActionLabel);
        // Structural, not incidental: the type exposes NO value surface at all,
        // so there is nothing a face could bind that would show "On".
        Assert.DoesNotContain(typeof(PedalMomentaryParamViewModel).GetProperties().Select(prop => prop.Name),
            name => name is "Value" or "SelectedIndex" or "IsActive" or "CurrentLabel");
    }

    // ══ 5. a refused file ════════════════════════════════════════════════════

    /// <summary>convreverb's <c>irFile</c> rides the file-picker path that
    /// already existed — the hole it exposes did not. A synchronous IR load that
    /// fails returns false and the engine KEEPS the previous IR, so the strip
    /// used to just not change: indistinguishable from "it loaded and sounds the
    /// same".</summary>
    [Fact]
    public void ARefusedFile_IsSurfaced_AndTheNextAcceptedOneClearsIt()
    {
        var type = new DspModuleType("convreverb", "Convolution Reverb",
            [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)])
        {
            StringParams = [new DspStringParamInfo("irFile", "IR File", "*.wav")]
        };

        var chain = new RigCapabilityChain([type]) { FailSetString = true };
        var node = Node(chain, type);
        var file = Assert.Single(node.StringParams);
        Assert.True(file.IsFilePicker);
        Assert.False(file.HasLoadError);

        file.LoadFile(@"C:\irs\broken-32bit.wav");

        Assert.Equal(string.Empty, file.Value); // the engine kept the previous IR
        Assert.True(file.HasLoadError);
        Assert.Contains("broken-32bit.wav", file.LoadError, StringComparison.Ordinal);

        chain.FailSetString = false;
        file.LoadFile(@"C:\irs\good.wav");

        Assert.Equal(@"C:\irs\good.wav", file.Value);
        Assert.False(file.HasLoadError);
    }

    /// <summary>…and the DOCK puts its unit panel up for it, so the refusal has
    /// somewhere to be seen on a face whose file strip is elsewhere.</summary>
    [Fact]
    public void TheDocksUnitPanel_AppearsOnlyWhenTheUnitHasSomethingToSay()
    {
        var plain = new DspModuleType("rat", "Rat",
            [new DspParamInfo("distortion", "Distortion", 0f, 1f, 0.5f)]);
        var chain = new RigCapabilityChain([plain]);
        Assert.False(new RigDockUnit(Node(chain, plain)).HasUnitPanel);

        var looper = Looper();
        var withTransport = new RigCapabilityChain([looper]);
        Assert.True(new RigDockUnit(Node(withTransport, looper)).HasUnitPanel);

        var keyed = new DspModuleType("expander", "Expander",
            [new DspParamInfo("ratio", "Ratio", 1f, 10f, 2f)])
        { SupportsSidechain = true };
        var withKey = new RigCapabilityChain([keyed]);
        Assert.True(new RigDockUnit(Node(withKey, keyed)).HasUnitPanel);

        var slow = new DspModuleType("pitchcorrect", "Pitch Correct",
            [new DspParamInfo("amount", "Amount", 0f, 1f, 0.5f)]);
        var withLatency = new RigCapabilityChain([slow]);
        Assert.True(new RigDockUnit(Node(withLatency, slow, id => withLatency.Latency[id] = 1392)).HasUnitPanel);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Adds the module to the chain for real and builds the card the
    /// board would build for it, so nothing here tests a view model over a node
    /// the chain does not have.</summary>
    private static PedalNodeViewModel Node(RigCapabilityChain chain, DspModuleType type,
        Action<int>? beforeCard = null, bool withAudio = false)
    {
        // The id is the CHAIN's to assign, never the caller's to guess: a test
        // that hard-codes one and then adds a second node asserts against a
        // number that moved. `beforeCard` is for the state the card reads at
        // CONSTRUCTION (its latency), which has to be in place first.
        var node = chain.AddNode(type.Id)!;
        beforeCard?.Invoke(node.NodeId);
        return new PedalNodeViewModel(chain, node, type, FakeLocalization.English(),
            audio: withAudio ? new FakeAudioPlaybackService() : null);
    }
}
