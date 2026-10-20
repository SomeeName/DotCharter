using System.Security.Cryptography;
using System.Text.Json;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;

namespace DotCharter.Tests.Ai.Model;

/// <summary>
///     The guard that keeps the C# estimator and the python trainer describing
///     the same model.
///     <para>
///         Drift here is SILENT: a C# side that windowed differently, normalised
///         differently, or read the knob vector at the wrong offsets still
///         returns a well-formed <c>DcRigPreset</c> full of plausible numbers.
///         Nothing downstream can tell, because there is no correct answer to
///         compare a guess against. So the export writes a fixture clip's exact
///         outputs into the sidecar, and this runs the SHIPPED ONNX on a fixture
///         it regenerates itself and compares.
///     </para>
///     <para>
///         The model is not bundled, so these skip when
///         <c>%LOCALAPPDATA%\DotCharter\ai-match\rig-estimator\</c> is empty —
///         but the pure-mapping tests below do not, and they are the half that
///         catches an offset or normalisation change.
///     </para>
/// </summary>
public sealed class RigEstimatorParityTests
{
    private const double OutputTolerance = 2e-3;

    /// <summary>The parity fixture: the closed-form twin of
    /// <c>build_fixture</c> in tools/rig-model/export_onnx.py. No RNG, so both
    /// sides can produce it exactly.</summary>
    internal static float[] BuildFixture(int n, int sampleRate)
    {
        var x = new double[n];
        double[] partials = [110.0, 220.0, 330.0, 440.0, 660.0];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)sampleRate;
            double v = 0;
            for (var h = 0; h < partials.Length; h++)
                v += 1.0 / (h + 1) * Math.Sin(2 * Math.PI * partials[h] * t + 0.3 * (h + 1));
            v *= 0.6 + 0.4 * Math.Sin(2 * Math.PI * 4.0 * t);
            v *= Math.Exp(-(t % 1.25) * 1.6);
            x[i] = v;
        }

        var peak = 0.0;
        foreach (var v in x) peak = Math.Max(peak, Math.Abs(v));
        peak = Math.Max(peak, 1e-9);

        var outBuf = new float[n];
        for (var i = 0; i < n; i++) outBuf[i] = (float)(x[i] / peak * 0.7);
        return outBuf;
    }

    [Fact]
    public void Fixture_RegeneratesByteForByte_AgainstTheSidecarHash()
    {
        var sidecar = TryLoadSidecar(out var path);
        if (sidecar is null) return; // covered by the skip test below

        using var doc = JsonDocument.Parse(File.ReadAllText(path!));
        if (!doc.RootElement.TryGetProperty("parity", out var parity)) return;

        var samples = parity.GetProperty("fixtureSamples").GetInt32();
        var raw = BuildFixture(samples, sidecar.SampleRate);
        var prepared = RigEstimatorOnnx.NormaliseRms(raw, RigEstimatorOnnx.TargetRmsDbfs);

        var bytes = new byte[prepared.Length * 4];
        Buffer.BlockCopy(prepared, 0, bytes, 0, bytes.Length);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var expected = parity.GetProperty("fixtureSha256").GetString();
        Assert.True(hash == expected,
            $"The C# parity fixture is not the python one.\n"
            + $"  expected sha256 {expected}\n  actual   sha256 {hash}\n"
            + "Either BuildFixture here and build_fixture in tools/rig-model/export_onnx.py have "
            + "diverged, or NormaliseRms and prepare_input have. Fix the C# side to match the "
            + "export -- the model was TRAINED against the python path.");
    }

    [Fact]
    public async Task ShippedModel_ReproducesThePythonOutputs()
    {
        var estimator = new RigEstimatorOnnx();
        using var _ = estimator;
        if (!estimator.IsAvailable)
        {
            Assert.Skip($"No rig estimator model at {estimator.ModelPath}; "
                        + "export one with tools/rig-model/export_onnx.py.");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(estimator.SidecarPath));
        if (!doc.RootElement.TryGetProperty("parity", out var parity)
            || !parity.TryGetProperty("outputs", out var outputs))
        {
            Assert.Skip("The sidecar carries no parity block; re-export with --check.");
            return;
        }

        var sidecar = JsonSerializer.Deserialize<RigLabelVocabularySnapshot>(
            File.ReadAllText(estimator.SidecarPath))!;
        var samples = parity.GetProperty("fixtureSamples").GetInt32();
        var fixture = BuildFixture(samples, sidecar.SampleRate);

        var estimate = await estimator
            .EstimateAsync(new MonoAudio(fixture, sidecar.SampleRate), CancellationToken.None);

        Assert.NotNull(estimate);

        // family_presence is the head whose numbers the decode path does not
        // reshape, so it compares one-to-one against what python recorded.
        var expectedFamily = outputs.GetProperty("family_presence")
            .EnumerateArray().Select(e => e.GetDouble()).ToArray();

        for (var i = 0; i < sidecar.Families.Count && i < expectedFamily.Length; i++)
        {
            var family = sidecar.Families[i];
            var actual = estimate.FamilyPresence[family];
            Assert.True(Math.Abs(actual - expectedFamily[i]) <= OutputTolerance,
                $"family '{family}' (index {i}): python wrote {expectedFamily[i]:F6}, C# produced "
                + $"{actual:F6} (tolerance {OutputTolerance}). The two halves have drifted: check "
                + "PrepareInput/NormaliseRms against prepare_input, and the sidecar's schemaVersion "
                + "against the model that was exported.");
        }

        var expectedConfidence = parity.GetProperty("outputs").GetProperty("confidence")
            .EnumerateArray().First().GetDouble();
        Assert.True(Math.Abs(estimate.Confidence - expectedConfidence) <= OutputTolerance,
            $"confidence: python wrote {expectedConfidence:F6}, C# produced {estimate.Confidence:F6}");
    }

    [Fact]
    public async Task IsAvailable_IsFalseWithNoModel_AndNeverThrows()
    {
        var empty = Path.Combine(Path.GetTempPath(), "dotcharter-rig-estimator-absent-" + Guid.NewGuid());
        using var estimator = new RigEstimatorOnnx(empty);

        Assert.False(estimator.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            estimator.EstimateAsync(new MonoAudio(new float[240000], 48000), CancellationToken.None));
    }

    [Fact]
    public void NormaliseRms_HitsTheTarget_AndLeavesSilenceAlone()
    {
        var tone = new float[48000];
        for (var i = 0; i < tone.Length; i++) tone[i] = 0.01f * MathF.Sin(2 * MathF.PI * 220 * i / 48000f);

        RigEstimatorOnnx.NormaliseRms(tone, RigEstimatorOnnx.TargetRmsDbfs);
        var rms = Math.Sqrt(tone.Sum(v => (double)v * v) / tone.Length);
        var wanted = Math.Pow(10, RigEstimatorOnnx.TargetRmsDbfs / 20.0);
        Assert.True(Math.Abs(rms - wanted) < 1e-3, $"rms {rms:F6}, wanted {wanted:F6}");

        // Digital silence must stay silent: normalising it would amplify
        // nothing into the model's whole dynamic range.
        var silence = new float[1000];
        RigEstimatorOnnx.NormaliseRms(silence, RigEstimatorOnnx.TargetRmsDbfs);
        Assert.All(silence, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void NormaliseRms_ScrubsNonFiniteSamples()
    {
        // A NaN anywhere in the input is a NaN out of every head, and the
        // estimate then reads as "no modules at all" rather than as an error.
        float[] dirty = [0.5f, float.NaN, -0.5f, float.PositiveInfinity, 0.5f];
        RigEstimatorOnnx.NormaliseRms(dirty, RigEstimatorOnnx.TargetRmsDbfs);
        Assert.All(dirty, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void PrepareInput_TakesTheCentreWindow_AndPadsAShortClip()
    {
        const int rate = 48000;
        const int want = 240000;

        var longClip = new float[want * 2];
        for (var i = 0; i < longClip.Length; i++) longClip[i] = i is < (want / 2) or > (want * 3 / 2) ? 0f : 0.2f;
        var centred = RigEstimatorOnnx.PrepareInput(new MonoAudio(longClip, rate), rate, want);
        Assert.Equal(want, centred.Length);
        Assert.All(centred, v => Assert.True(Math.Abs(v) > 0));

        var shortClip = new float[want / 4];
        for (var i = 0; i < shortClip.Length; i++) shortClip[i] = 0.3f;
        var padded = RigEstimatorOnnx.PrepareInput(new MonoAudio(shortClip, rate), rate, want);
        Assert.Equal(want, padded.Length);
        Assert.Equal(0f, padded[^1]);
    }

    [Fact]
    public void Decode_MapsSlotsAndKnobsThroughTheCatalogueRanges()
    {
        var vocab = RigLabelVocabulary.Snapshot();
        var classes = vocab.Modules.Count + 1;

        // Put 'overdrive' in slot 0 and leave every other slot empty.
        var overdrive = vocab.Modules.Single(m => m.TypeId == "overdrive");
        var overdriveClass = vocab.Modules.ToList().IndexOf(overdrive) + 1;

        var slots = new float[vocab.Slots * classes];
        for (var s = 0; s < vocab.Slots; s++) slots[s * classes] = 1f;  // none wins everywhere
        slots[0 * classes] = 0f;
        slots[0 * classes + overdriveClass] = 1f;

        // Every knob at 0.25 of its range.
        var knobs = new float[vocab.KnobDimension];
        Array.Fill(knobs, 0.25f);

        var family = new float[vocab.Families.Count];
        family[vocab.Families.ToList().IndexOf("drive")] = 0.91f;

        var estimate = RigEstimatorOnnx.Decode(vocab, family, slots, knobs, [0.77f]);

        var module = Assert.Single(estimate.Rig.Modules);
        Assert.Equal("overdrive", module.ModuleTypeId);
        Assert.Equal(0.77f, estimate.Confidence, 5);
        Assert.Equal(0.91f, estimate.FamilyPresence["drive"], 5);

        foreach (var spec in overdrive.Params)
        {
            var expected = spec.Min + 0.25f * (spec.Max - spec.Min);
            Assert.True(Math.Abs(module.Params[spec.Id] - expected) < 1e-4,
                $"overdrive.{spec.Id}: expected {expected}, got {module.Params[spec.Id]}");
        }
    }

    [Fact]
    public void Decode_DropsRoutingModules_BecauseV1IsSeriesOnly()
    {
        var vocab = RigLabelVocabulary.Snapshot();
        var classes = vocab.Modules.Count + 1;
        var splitter = vocab.Modules.Single(m => m.TypeId == "splitter");
        var splitterClass = vocab.Modules.ToList().IndexOf(splitter) + 1;

        var slots = new float[vocab.Slots * classes];
        for (var s = 0; s < vocab.Slots; s++) slots[s * classes] = 1f;
        slots[0] = 0f;
        slots[splitterClass] = 1f;

        var estimate = RigEstimatorOnnx.Decode(
            vocab, new float[vocab.Families.Count], slots, new float[vocab.KnobDimension], [0.5f]);

        // A per-slot argmax cannot guarantee a splitter gets its merger, and a
        // splitter without one is not a rig.
        Assert.Empty(estimate.Rig.Modules);
    }

    private static RigLabelVocabularySnapshot? TryLoadSidecar(out string? path)
    {
        path = Path.Combine(RigEstimatorOnnx.DefaultModelDirectory, "model.sidecar.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<RigLabelVocabularySnapshot>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
