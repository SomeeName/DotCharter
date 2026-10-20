using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
/// Unit tests for the absolute loudness normalizer. The model under test
/// (documented on <see cref="DcRigLoudnessNormalizer"/>): a coarse peak walker
/// from a −18 dBFS reference input — linear level params multiply absolutely,
/// dB level params add, saturating drive stages cap at the −3 dBFS ceiling —
/// and a trailing master gain that pulls the estimate to the −12 dBFS target,
/// clamped to [−24, +18] dB.
/// </summary>
public sealed class DcRigLoudnessNormalizerTests
{
    private const float In = DcRigLoudnessNormalizer.ReferenceInputPeakDb;    // −18
    private const float Target = DcRigLoudnessNormalizer.TargetOutputPeakDb;  // −12
    private const float Ceiling = DcRigLoudnessNormalizer.SaturationCeilingDb; // −3

    private static DcRigModule Module(string type, params (string Id, float Value)[] ps)
        => new(type, ps.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal));

    private static DcRigModule BypassedModule(string type, params (string Id, float Value)[] ps)
        => new(type, ps.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal), Bypassed: true);

    private static float LinDb(float v) => 20f * MathF.Log10(MathF.Max(v, 0.05f));

    // ------------------------------------------------------------ estimator

    [Fact]
    public void EmptyChain_EstimatesTheInputUnchanged()
    {
        Assert.Equal(In, DcRigLoudnessNormalizer.EstimateChainOutputDb(Array.Empty<DcRigModule>()), 0.0001);
        Assert.Equal(-30f, DcRigLoudnessNormalizer.EstimateChainOutputDb(Array.Empty<DcRigModule>(), -30f), 0.0001);
    }

    [Fact]
    public void DbLevelParams_AddDirectly()
    {
        // compressor makeup +6, eq trim −3, trailing gain +2 → input + 5.
        var chain = new[]
        {
            Module("compressor", ("makeupDb", 6f)),
            Module("eq", ("outputTrimDb", -3f)),
            Module("gain", ("gainDb", 2f))
        };
        Assert.Equal(In + 5f, DcRigLoudnessNormalizer.EstimateChainOutputDb(chain), 0.0001);
    }

    [Fact]
    public void UnsetParams_UseCatalogueDefaults()
    {
        // A bare compressor: makeupDb default 0 → no change.
        var chain = new[] { Module("compressor") };
        Assert.Equal(In, DcRigLoudnessNormalizer.EstimateChainOutputDb(chain), 0.0001);
    }

    [Fact]
    public void MildOverdrive_StaysLinear_AndScalesByOutputLevel()
    {
        // drive 0.2 → +6 dB: −18+6 = −12, below the −3 ceiling → no saturation.
        // level 0.5 → ×(0.5·1.2) = −4.44 dB.
        var chain = new[] { Module("overdrive", ("drive", 0.2f), ("level", 0.5f)) };
        var expected = In + 6f + LinDb(0.5f * 1.2f);
        Assert.Equal(expected, DcRigLoudnessNormalizer.EstimateChainOutputDb(chain), 0.001);
    }

    [Fact]
    public void HotDriveStages_CapAtTheSaturationCeiling()
    {
        // drive 1.0 → +30 dB would land at +12; the ceiling caps it at −3.
        // Past the knee MORE drive adds no more estimated level:
        var hot = new[] { Module("overdrive", ("drive", 0.8f), ("level", 1f)) };
        var hotter = new[] { Module("overdrive", ("drive", 1f), ("level", 1f)) };

        var expected = Ceiling + LinDb(1.2f);
        Assert.Equal(expected, DcRigLoudnessNormalizer.EstimateChainOutputDb(hot), 0.001);
        Assert.Equal(
            DcRigLoudnessNormalizer.EstimateChainOutputDb(hot),
            DcRigLoudnessNormalizer.EstimateChainOutputDb(hotter), 0.0001);
    }

    [Fact]
    public void CleanAmp_IsNearlyLinear_LeadAmpSaturates()
    {
        // Clean voicing, gain 0.2 → +2 dB drive; master 0.6 → ×0.72.
        var clean = new[] { Module("amp", ("gain", 0.2f), ("voicing", 0f), ("master", 0.6f)) };
        Assert.Equal(In + 2f + LinDb(0.72f), DcRigLoudnessNormalizer.EstimateChainOutputDb(clean), 0.001);

        // Lead voicing, gain 0.8 → +38 dB drive → pinned at the ceiling.
        var lead = new[] { Module("amp", ("gain", 0.8f), ("voicing", 2f), ("master", 0.6f)) };
        Assert.Equal(Ceiling + LinDb(0.72f), DcRigLoudnessNormalizer.EstimateChainOutputDb(lead), 0.001);
    }

    [Fact]
    public void ZeroLevel_IsFlooredFinite()
    {
        var chain = new[] { Module("distortion", ("gain", 0.9f), ("level", 0f)) };
        var db = DcRigLoudnessNormalizer.EstimateChainOutputDb(chain);
        Assert.True(float.IsFinite(db));
        Assert.Equal(Ceiling + LinDb(0.05f), db, 0.001); // 0·0.9 floors at 0.05
    }

    [Fact]
    public void BypassedModules_AreIgnored()
    {
        var chain = new[] { BypassedModule("compressor", ("makeupDb", 12f)) };
        Assert.Equal(In, DcRigLoudnessNormalizer.EstimateChainOutputDb(chain), 0.0001);
    }

    [Fact]
    public void CharacterModules_AreLevelNeutral()
    {
        var chain = new[]
        {
            Module("cab", ("model", 3f)),
            Module("chorus", ("mix", 1f)),
            Module("delay", ("mix", 1f)),
            Module("reverb", ("mix", 1f)),
            Module("eq", ("lowGainDb", 12f), ("highGainDb", 12f)) // band shelves ignored
        };
        Assert.Equal(In, DcRigLoudnessNormalizer.EstimateChainOutputDb(chain), 0.0001);
    }

    // ------------------------------------------------------- trailing gain

    [Fact]
    public void TrailingGain_PullsTheEstimateToTheTarget()
    {
        // Quiet clean chain (≈ −18.85 dBFS estimate) → boost of the exact gap.
        var chain = new[] { Module("amp", ("gain", 0.2f), ("voicing", 0f), ("master", 0.6f)) };
        var estimate = DcRigLoudnessNormalizer.EstimateChainOutputDb(chain);
        var gain = DcRigLoudnessNormalizer.ComputeTrailingGainDb(chain);

        Assert.Equal(Target - estimate, gain, 0.0001);
        Assert.Equal(Target, estimate + gain, 0.0001);
    }

    [Fact]
    public void TrailingGain_BoostIsClampedConservatively()
    {
        // An absurdly quiet estimate (zeroed distortion level ≈ −29 dBFS after
        // the floor) is only trusted up to +18 dB of boost.
        var chain = new[]
        {
            Module("distortion", ("gain", 0.5f), ("level", 0f)),
            Module("gain", ("gainDb", -24f))
        };
        Assert.Equal(
            DcRigLoudnessNormalizer.MaxBoostDb,
            DcRigLoudnessNormalizer.ComputeTrailingGainDb(chain), 0.0001);
    }

    [Fact]
    public void TrailingGain_CutReachesTheGainModuleFloor()
    {
        // A chain pushed way past the target (stacked makeup + boost) gets the
        // full −24 dB cut — the hot end always has the whole range available.
        var chain = new[]
        {
            Module("compressor", ("makeupDb", 24f)),
            Module("gain", ("gainDb", 24f))
        };
        Assert.Equal(
            -DcRigLoudnessNormalizer.MaxCutDb,
            DcRigLoudnessNormalizer.ComputeTrailingGainDb(chain), 0.0001);
    }

    // ---------------------------------------------------------- Normalize

    [Fact]
    public void Normalize_AppendsATrailingGain_HittingTheTarget()
    {
        var preset = new DcRigPreset(new List<DcRigModule>
        {
            Module("amp", ("gain", 0.8f), ("voicing", 2f), ("master", 0.6f)),
            Module("cab")
        });

        var normalized = DcRigLoudnessNormalizer.Normalize(preset);

        Assert.Equal(new[] { "amp", "cab", "gain" },
            normalized.Modules.Select(m => m.ModuleTypeId).ToArray());
        Assert.Equal(Target,
            DcRigLoudnessNormalizer.EstimateChainOutputDb(normalized.Modules), 0.001);
    }

    [Fact]
    public void Normalize_ReplacesAnExistingTrailingGain_AndIsIdempotent()
    {
        var preset = new DcRigPreset(new List<DcRigModule>
        {
            Module("overdrive", ("drive", 0.6f), ("level", 0.7f)),
            Module("gain", ("gainDb", -20f)) // stale trailing master
        });

        var once = DcRigLoudnessNormalizer.Normalize(preset);
        var twice = DcRigLoudnessNormalizer.Normalize(once);

        Assert.Equal(preset.Modules.Count, once.Modules.Count); // replaced, not stacked
        Assert.Equal(Target, DcRigLoudnessNormalizer.EstimateChainOutputDb(once.Modules), 0.001);
        Assert.Equal(
            once.Modules[^1].Params["gainDb"],
            twice.Modules[^1].Params["gainDb"], 0.0001);
    }

    [Fact]
    public void Normalize_LeavesAMidChainBoostAlone()
    {
        // A gain module that is NOT last is a clean-boost pedal, not the
        // trailing master — it must survive verbatim.
        var preset = new DcRigPreset(new List<DcRigModule>
        {
            Module("gain", ("gainDb", 6f)),
            Module("amp", ("voicing", 0f), ("gain", 0.3f))
        });

        var normalized = DcRigLoudnessNormalizer.Normalize(preset);

        Assert.Equal(new[] { "gain", "amp", "gain" },
            normalized.Modules.Select(m => m.ModuleTypeId).ToArray());
        Assert.Equal(6f, normalized.Modules[0].Params["gainDb"], 0.0001);
    }
}
