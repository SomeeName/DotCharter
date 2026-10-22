using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Parsing.Psarc;
using DotCharter.Tests.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     #67: converted tones are levelled by RENDERED loudness — a run starts on the
///     estimate and never waits for the measurement, the live tone GLIDES to its
///     measured level, a glide never displaces a queued tone switch, a measured chain
///     is never measured twice, and the player's saved edits are left alone.
/// </summary>
public sealed class RsTonePlaybackCoordinatorLevellingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static string Tone(string key, string amp) =>
        "{\"Key\":\"" + key + "\",\"Name\":\"" + key + "\",\"Volume\":\"-12.0\",\"GearList\":{\"Amp\":{\"Type\":\"Amps\",\"Key\":\"" + amp +
        "\",\"KnobValues\":{}},\"Cabinet\":{\"Type\":\"Cabinets\",\"Key\":\"Cab_TW112C_57_Cone\",\"KnobValues\":{}}}}";

    private static ProjectState StateWithBeats()
    {
        var state = new ProjectState();
        for (var i = 0; i < 8; i++)
            state.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        return state;
    }

    private static (RsTonePlaybackCoordinator Coord, FakeDspChainService Chain, ProjectState State) NewRig(
        IToneLoudnessMeter meter, params (string Key, string Amp)[] tones)
    {
        var chain = new FakeDspChainService(
            new[] { "amp", "cab", "micpos", "gain" }
                .Select(id => new DspModuleType(id, id, [new DspParamInfo("p", "p", -100f, 100f, 0f)]))
                .ToList());
        var state = StateWithBeats();
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, config, loudnessMeter: meter)
        {
            ToneDefinitionSource = _ => tones.Select(t => new PsarcToneDefinition(t.Key, t.Key, Tone(t.Key, t.Amp))).ToList(),
            SongTonesSource = _ => new Dictionary<string, DcRigPreset>(),
            GlideDuration = TimeSpan.FromMilliseconds(80)
        };
        return (coord, chain, state);
    }

    private static float TrailingGain(FakeDspChainService chain)
        => chain.GetNodes().Last(n => n.ModuleTypeId == "gain").Values["gainDb"];

    private static float Estimate(string amp)
    {
        var preset = new RsToneConverter().ConvertFilled(
            RsToneJsonParserFacade.Parse("x", amp), out _);
        return RsToneLevelling.TrailingGainDb(preset)!.Value;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow - start < Deadline, $"deadlock detector: {what} never happened (box starved?)");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // A loud measurement, so the levelled gain is far from any estimate.
    private static ToneLoudness Loud(IReadOnlyList<DcRigModule> _) => new(-8.0, -1.0);

    [Fact]
    public async Task ARun_StartsOnTheEstimate_ThenGlidesToTheMeasuredLevel()
    {
        var meter = new FakeToneLoudnessMeter { Gated = true, Answer = Loud };
        var (coord, chain, _) = NewRig(meter, ("clean", "Amp_TW22"));
        coord.PrepareForSong("song.psarc", new Arrangement { ToneBase = "clean" });

        // The measurement is out: the run starts anyway, on the estimate.
        Assert.True(coord.BeginRun());
        var estimate = Estimate("Amp_TW22");
        Assert.Equal(estimate, TrailingGain(chain), 3);
        var levelled = RsToneLoudnessPolicy.TrailingGainDb(Loud([]), 0f);
        Assert.True(levelled < estimate - 2f, $"fixture: the levelled gain {levelled} must sit well under the estimate {estimate}");

        var setsBefore = chain.ParamSets.Count;
        meter.Release();
        await coord.LevellingCompletion.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await Until(() => MathF.Abs(TrailingGain(chain) - levelled) < 1e-3f, "the glide reaching the measured level");

        // A GLIDE: several steps, every one between the two levels, in order.
        var steps = chain.ParamSets.Skip(setsBefore).Where(p => p.ParamId == "gainDb").Select(p => p.Value).ToList();
        Assert.True(steps.Count >= 3, $"expected a glide, got {steps.Count} step(s): {string.Join(", ", steps)}");
        for (var i = 1; i < steps.Count; i++)
            Assert.True(steps[i] < steps[i - 1], $"not monotonic: {string.Join(", ", steps)}");
        Assert.All(steps, s => Assert.InRange(s, levelled - 1e-3f, estimate));
        Assert.True(steps.Zip(steps.Skip(1), (a, b) => a - b).All(d => d <= RsTonePlaybackCoordinator.GlideStepDb + 1e-3f),
            $"a step larger than {RsTonePlaybackCoordinator.GlideStepDb} dB: {string.Join(", ", steps)}");

        coord.EndRun();
        await coord.RestoreCompletion;
    }

    [Fact]
    public async Task AChainTooQuietForTheTrailingGain_GlidesTheAmpTrimToo()
    {
        // #88: past the trailing gain's +24 dB the rest goes into the Rocksmith amp
        // chain's output trim, and that glides with it, in steps no larger than the
        // gain's own. The amp's chain is the (synthetic) install's (#755).
        using var game = SyntheticRsGame.Use();
        static ToneLoudness Quiet(IReadOnlyList<DcRigModule> _) => new(-80.0, -60.0);
        var meter = new FakeToneLoudnessMeter { Gated = true, Answer = Quiet };
        var chain = new FakeDspChainService(
            new[] { "rsamp", "cab", "micpos", "gain" }
                .Select(id => new DspModuleType(id, id,
                    [.. new[] { "p", "gainDb", "model", "level" }.Concat(Enumerable.Range(1, 10).Select(k => "k" + k))
                        .Select(p => new DspParamInfo(p, p, -100f, 100f, 0f))]))
                .ToList());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), StateWithBeats(), config, loudnessMeter: meter)
        {
            ToneDefinitionSource = _ => [new PsarcToneDefinition("clean", "clean", Tone("clean", "Amp_MarshallPlexi"))],
            SongTonesSource = _ => new Dictionary<string, DcRigPreset>(),
            GlideDuration = TimeSpan.FromMilliseconds(80)
        };
        coord.PrepareForSong("song.psarc", new Arrangement { ToneBase = "clean" });
        Assert.True(coord.BeginRun());
        var setsBefore = chain.ParamSets.Count;

        meter.Release();
        await coord.LevellingCompletion.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        float Trim() => chain.GetNodes().Single(n => n.ModuleTypeId == "rsamp").Values["level"];
        await Until(() => MathF.Abs(Trim() - 24f) < 1e-3f && MathF.Abs(TrailingGain(chain) - 24f) < 1e-3f,
            "the glide reaching +24 dB of gain and +24 dB of trim");

        var trims = chain.ParamSets.Skip(setsBefore).Where(p => p.ParamId == "level").Select(p => p.Value).ToList();
        Assert.True(trims.Count >= 3, $"expected the trim to glide, got {trims.Count} step(s)");
        Assert.True(trims.Zip(trims.Skip(1), (a, b) => b - a).All(d => d is >= -1e-3f and <= RsTonePlaybackCoordinator.GlideStepDb + 1e-3f),
            $"the trim did not glide in small rising steps: {string.Join(", ", trims)}");

        coord.EndRun();
        await coord.RestoreCompletion;
    }

    [Fact]
    public async Task AMeasuredChain_IsLevelledAtOnce_AndNeverMeasuredTwice()
    {
        var meter = new FakeToneLoudnessMeter { Answer = Loud };
        var (first, _, _) = NewRig(meter, ("clean", "Amp_TW22"));
        first.PrepareForSong("song.psarc", new Arrangement { ToneBase = "clean" });
        await first.LevellingCompletion.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.Equal(1, meter.Measures);

        // Another prepare of the same tone (a new session object, the same meter):
        // levelled before the run starts, from the cache.
        var (second, chain, _) = NewRig(meter, ("clean", "Amp_TW22"));
        second.PrepareForSong("song.psarc", new Arrangement { ToneBase = "clean" });
        Assert.True(second.BeginRun());
        Assert.Equal(RsToneLoudnessPolicy.TrailingGainDb(Loud([]), 0f), TrailingGain(chain), 3);
        Assert.Equal(1, meter.Measures);

        second.EndRun();
        await second.RestoreCompletion;
    }

    [Fact]
    public async Task AGlide_NeverDisplacesAQueuedToneSwitch()
    {
        var meter = new FakeToneLoudnessMeter { Gated = true, Answer = Loud };
        var (coord, chain, state) = NewRig(meter,
            ("clean", "Amp_TW22"), ("heavy", "Amp_MarshallJCM800"), ("lead", "Amp_MarshallPlexi"));
        var arr = new Arrangement { ToneBase = "clean" };
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "heavy" });
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(6), ToneName = "lead" });
        coord.PrepareForSong("song.psarc", arr);
        Assert.True(coord.BeginRun());

        // Park the worker inside the switch to "heavy", then queue "lead" behind it.
        using var parked = new ManualResetEventSlim(false);
        using var inApply = new ManualResetEventSlim(false);
        chain.OnGetNodes = () =>
        {
            inApply.Set();
            parked.Wait(Deadline);
        };
        state.CurrentTimeMs = 4500;
        Assert.True(inApply.Wait(Deadline, TestContext.Current.CancellationToken), "the switch to 'heavy' never reached the chain");
        state.CurrentTimeMs = 6500;

        // The measurement lands while "lead" is queued: its glide must not take the slot.
        meter.Release();
        await coord.LevellingCompletion.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        chain.OnGetNodes = null;
        parked.Set();

        await Until(() => coord.CurrentToneName == "lead", "the queued switch to 'lead'");
        coord.EndRun();
        await coord.RestoreCompletion;
    }

    [Fact]
    public async Task ThePlayersSavedEdit_IsNeverLevelled()
    {
        var meter = new FakeToneLoudnessMeter { Answer = Loud };
        var (coord, chain, _) = NewRig(meter, ("clean", "Amp_TW22"));
        var edit = new DcRigPreset([
            new DcRigModule("amp", new Dictionary<string, float> { ["gain"] = 0.3f }),
            new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = 3.5f })
        ]);
        coord.SongTonesSource = _ => new Dictionary<string, DcRigPreset> { ["clean"] = edit };

        coord.PrepareForSong("song.psarc", new Arrangement { ToneBase = "clean" }, "lib:SONG");
        await coord.LevellingCompletion.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(coord.BeginRun());
        Assert.Equal(3.5f, TrailingGain(chain), 3);

        coord.EndRun();
        await coord.RestoreCompletion;
    }

    [Fact]
    public async Task TheEditorsPreview_OfTheProjectsOwnTones_IsLevelledToo()
    {
        var meter = new FakeToneLoudnessMeter { Gated = true, Answer = Loud };
        var (coord, chain, _) = NewRig(meter);
        var project = new ChartProject();
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = Tone("clean", "Amp_TW22") });
        var arrangement = new Arrangement { ToneBase = "clean" };

        Assert.True(coord.PrepareForProject(project, arrangement).HasTones);
        Assert.True(coord.BeginPreview());
        Assert.Equal(Estimate("Amp_TW22"), TrailingGain(chain), 3);

        meter.Release();
        await coord.LevellingCompletion.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var levelled = RsToneLoudnessPolicy.TrailingGainDb(Loud([]), 0f);
        await Until(() => MathF.Abs(TrailingGain(chain) - levelled) < 1e-3f, "the preview's glide to the measured level");

        coord.EndPreview();
        await coord.RestoreCompletion;
    }

    /// <summary>The real parser over the fixture shape above.</summary>
    private static class RsToneJsonParserFacade
    {
        public static DotCharter.Tone.Audio.Dsp.Tones.RsTone Parse(string key, string amp)
            => DotCharter.Tone.Audio.Dsp.Tones.RsToneJsonParser.Parse(new PsarcToneDefinition(key, key, Tone(key, amp)));
    }
}
