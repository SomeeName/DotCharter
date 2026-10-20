using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     #138: the built-in chart synth ALWAYS plays through the chart's own tones —
///     the headphones toggle is no longer needed for that — and falls back to the
///     charter's own rig only when the project has no tone it can convert. Every
///     assertion reads the DSP CHAIN (what the audio runs through), never a flag.
/// </summary>
[Collection("GameplayToneHud")]
public sealed class ChartTonePreviewSynthTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "gain", "overdrive", "distortion", "ds1", "eq", "reverb", "delay" }
            .Select(id => new DspModuleType(id, id, new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();

    private const string CleanJson =
        """{"Key":"clean","Name":"clean","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    private static readonly string[] CleanTone = ["amp", "cab", "gain"];
    private static readonly string[] OwnBoard = ["reverb", "delay"];

    private sealed class FakeMixer : IGuitarMonitorMixer
    {
        public bool IsInputMuted { get; set; }
        public bool IsInputSoloed { get; set; }
        public bool IsAnythingSoloed => IsInputSoloed;
        public double InputVolume { get; set; } = 1.0;
        public double MonitorVolume { get; set; } = 1.0;
    }

    private sealed record Rig(
        ChartTonePreviewController Preview, RsTonePlaybackCoordinator Tones, FakeDspChainService Chain,
        ChartProject Project);

    /// <summary>A project whose base tone is "clean" — authored, or not — over the
    /// charter's own two-pedal board.</summary>
    private static Rig NewRig(bool authorTones = true)
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        if (authorTones)
            project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = CleanJson });
        var arrangement = new Arrangement { Name = "Lead", ToneBase = "clean" };
        project.Arrangements.Add(arrangement);

        var state = new ProjectState { ActiveProject = project, ActiveArrangement = arrangement };
        var chain = new FakeDspChainService(Catalog());
        chain.AddNode("reverb");
        chain.AddNode("delay");
        var tones = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        return new Rig(new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer()), tones, chain, project);
    }

    private static string[] Order(IDspChainService chain) => chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();

    /// <summary>A live edit re-applies through the coordinator's apply WORKER, so a
    /// test waits for that apply to complete (ActiveToneChanged fires after it).</summary>
    private static Func<int> CountApplies(RsTonePlaybackCoordinator tones)
    {
        var count = 0;
        tones.ActiveToneChanged += _ => Interlocked.Increment(ref count);
        return () => Volatile.Read(ref count);
    }

    private static void WaitForApplies(Func<int> applies, int atLeast) =>
        Assert.True(SpinWait.SpinUntil(() => applies() >= atLeast, TimeSpan.FromSeconds(60)),
            $"the live edit was never applied (saw {applies()} of {atLeast}; a 60 s deadlock detector, not a budget)");

    [Fact]
    public void TheSynth_PlaysThroughTheChartsTones_WithoutTheToggle()
    {
        var rig = NewRig();
        using var preview = rig.Preview;

        preview.SetChartSynthPlaying(true);

        Assert.False(preview.IsOn);          // the toggle was never touched
        Assert.True(preview.IsDriving);
        Assert.Equal(CleanTone, Order(rig.Chain));
    }

    // ----- #164: the synth on the player's own rig -------------------------------------

    [Fact]
    public void WithMyRigChosen_TheSynthLeavesThePlayersBoardOnTheRig()
    {
        var rig = NewRig();
        using var preview = rig.Preview;
        preview.SetChartSynthUsesOwnRig(true);

        preview.SetChartSynthPlaying(true);

        Assert.False(preview.IsDriving);
        Assert.False(rig.Tones.IsPlayersBoardDisplaced);
        Assert.Equal(OwnBoard, Order(rig.Chain));
    }

    [Fact]
    public async Task SwitchingTheSynthTone_WhileItPlays_HandsTheBoardBack_AndTakesTheSongTonesAgain()
    {
        var rig = NewRig();
        using var preview = rig.Preview;
        preview.SetChartSynthPlaying(true);
        Assert.True(preview.IsDriving);

        preview.SetChartSynthUsesOwnRig(true);
        Assert.False(preview.IsDriving);
        await rig.Tones.RestoreCompletion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(OwnBoard, Order(rig.Chain));

        preview.SetChartSynthUsesOwnRig(false);
        Assert.True(preview.IsDriving);
        Assert.Equal(CleanTone, Order(rig.Chain));
    }

    [Fact]
    public async Task WithMyRigChosen_ThePreviewToggle_StillPutsTheSongTonesOn_AndTheSynthDoesNotHoldThem()
    {
        var rig = NewRig();
        using var preview = rig.Preview;
        preview.SetChartSynthUsesOwnRig(true);
        preview.SetChartSynthPlaying(true);

        Assert.True(preview.TurnOn());
        Assert.Equal(CleanTone, Order(rig.Chain));

        preview.TurnOff();
        Assert.False(preview.IsDriving);
        await rig.Tones.RestoreCompletion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(OwnBoard, Order(rig.Chain));
    }

    [Fact]
    public void TheSynth_WithNoConvertibleTone_LeavesTheChartersRigAlone()
    {
        var rig = NewRig(authorTones: false);
        using var preview = rig.Preview;

        preview.SetChartSynthPlaying(true);

        Assert.False(preview.IsDriving);
        Assert.Equal(OwnBoard, Order(rig.Chain));
    }

    [Fact]
    public async Task TheToggleOff_KeepsTheChartsTones_UntilTheSynthStopsToo()
    {
        var rig = NewRig();
        using var preview = rig.Preview;
        preview.SetChartSynthPlaying(true);
        Assert.True(preview.TurnOn());

        preview.TurnOff();
        Assert.True(preview.IsDriving);
        Assert.Equal(CleanTone, Order(rig.Chain));

        preview.SetChartSynthPlaying(false);
        Assert.False(preview.IsDriving);
        await rig.Tones.RestoreCompletion;
        Assert.Equal(OwnBoard, Order(rig.Chain));
    }

    [Fact]
    public async Task TheSynthOff_KeepsTheChartsTones_WhileTheToggleIsOn()
    {
        var rig = NewRig();
        using var preview = rig.Preview;
        Assert.True(preview.TurnOn());
        preview.SetChartSynthPlaying(true);

        preview.SetChartSynthPlaying(false);
        Assert.True(preview.IsDriving);
        Assert.Equal(CleanTone, Order(rig.Chain));

        preview.TurnOff();
        await rig.Tones.RestoreCompletion;
        Assert.Equal(OwnBoard, Order(rig.Chain));
    }

    [Fact]
    public async Task LeavingTheEditor_GivesTheRigBack_AndComingBackResumesTheSynthsTones()
    {
        var rig = NewRig();
        using var preview = rig.Preview;
        preview.SetChartSynthPlaying(true);

        preview.StopOutsideTheEditor();
        Assert.False(preview.IsDriving);
        await rig.Tones.RestoreCompletion;
        Assert.Equal(OwnBoard, Order(rig.Chain));

        // A synth switched on while away does not take the rig from Play Mode.
        preview.SetChartSynthPlaying(false);
        preview.SetChartSynthPlaying(true);
        Assert.False(preview.IsDriving);

        preview.ResumeInTheEditor();
        Assert.True(preview.IsDriving);
        Assert.Equal(CleanTone, Order(rig.Chain));
    }

    [Fact]
    public void TonesAuthoredWhileTheSynthPlays_AreUsedWithoutAnOffOn()
    {
        var rig = NewRig(authorTones: false);
        using var preview = rig.Preview;
        preview.SetChartSynthPlaying(true);
        Assert.False(preview.IsDriving);

        rig.Project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = CleanJson });

        Assert.True(preview.IsDriving);
        Assert.Equal(CleanTone, Order(rig.Chain));
    }

    // ---- #139: the Tone designer's edits are heard live -----------------------------

    private const string DistortedCleanJson =
        """{"Key":"clean","Name":"clean","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}},"PrePedal1":{"Type":"Pedals","Key":"Pedal_Distortion","Category":"Distortion","KnobValues":{}}}}""";

    [Fact]
    public void ADesignerEdit_OfTheToneUnderThePlayhead_IsHeardWithoutCommitting()
    {
        var rig = NewRig();
        using var preview = rig.Preview;
        preview.SetChartSynthPlaying(true);
        Assert.Equal(CleanTone, Order(rig.Chain));
        var applies = CountApplies(rig.Tones);

        // A distortion pedal added in the designer, not committed: the project still
        // holds the clean body.
        preview.PreviewToneEdit("clean", DistortedCleanJson);
        WaitForApplies(applies, 1);

        Assert.Contains("ds1", Order(rig.Chain));
        Assert.Equal(CleanJson, rig.Project.ToneDefinitions[0].Json);
    }

    [Fact]
    public async Task ClosingTheDesigner_PlaysTheCommittedBodyAgain()
    {
        var rig = NewRig();
        using var preview = rig.Preview;
        preview.SetChartSynthPlaying(true);
        var applies = CountApplies(rig.Tones);
        preview.PreviewToneEdit("clean", DistortedCleanJson);
        WaitForApplies(applies, 1);

        preview.EndToneEditPreview();
        WaitForApplies(applies, 2);
        await rig.Tones.RestoreCompletion;

        Assert.Equal(CleanTone, Order(rig.Chain));
    }

    [Fact]
    public void AnEditMadeWhileNothingDrives_IsUsedWhenTheSynthStarts()
    {
        var rig = NewRig();
        using var preview = rig.Preview;

        preview.PreviewToneEdit("clean", DistortedCleanJson);
        Assert.Equal(OwnBoard, Order(rig.Chain));   // nothing drives yet: the charter's board

        preview.SetChartSynthPlaying(true);
        Assert.Contains("ds1", Order(rig.Chain));
    }
}
