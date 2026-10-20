using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     Measures the ceiling — the score a PERFECT model could earn once the truth
///     has been through the draft schema and <see cref="ToneDraftValidator" />.
///     <para>
///         These cases are the difference between an eval and a number. Without
///         them "the model scored 78" is unreadable: it could be a good answer
///         against a reachable 94, or a flawless one against a pipeline that only
///         ever allows 78.
///     </para>
///     Pure and offline — the default gate runs all of it.
/// </summary>
public sealed class ToneEvalCeilingTests
{
    /// <summary>
    ///     The mean ceiling over the committed fixture corpus. Pinned, because it is
    ///     a property of the DESIGN PIPELINE (the draft schema, the validator's
    ///     12-module cap, the dropped string params) and not of any model — so it
    ///     must only move when someone changes that pipeline, and then on purpose.
    /// </summary>
    private const double FixtureCeiling = 100.0;

    [Fact]
    public void EveryFixtureTruth_SurvivesTheDraftRoundTripIntact()
    {
        // A truth a model is structurally forbidden from expressing would cap that
        // row's score forever, and the eval would report the CAP as the model's
        // failure. Nothing in this corpus is in that position today.
        foreach (var entry in ToneEvalCorpus.Fixtures)
        {
            var ceiling = ToneEvalCeiling.Of(entry.Truth);
            Assert.True(ceiling.Total >= 99.99,
                $"{entry.SongTitle}/{entry.ToneName} can only ever score " +
                $"{ceiling.Total:F1}: the draft round trip turned " +
                $"`{string.Join(" → ", ceiling.TruthModules)}` into " +
                $"`{string.Join(" → ", ceiling.DesignedModules)}`. Either the fixture " +
                "uses something a v1 draft cannot express, or the validator changed.");
        }
    }

    [Fact]
    public void TheFixtureCorpusCeiling_IsThePinnedNumber()
    {
        var mean = ToneEvalCorpus.Fixtures.Average(e => ToneEvalCeiling.TotalOf(e.Truth));
        Assert.Equal(FixtureCeiling, mean, 2);
    }

    [Fact]
    public void TheCeilingIsNotVacuous_AChainPastTheValidatorsCapIsClipped()
    {
        // Prove the measurement really goes through ToneDraftValidator: thirteen
        // DISTINCT modules against a cap of twelve must come back short, lose the
        // family the dropped module carried, and so score below 100.
        //
        // Distinct on purpose. The module component is a Jaccard over distinct type
        // ids, so thirteen copies of one overdrive truncate to twelve and still
        // score 100 — a real limitation, written down in
        // docs/agents/testing/ai-eval.md rather than hidden behind a test that
        // happens not to hit it.
        var ids = new[]
        {
            DcRigTargetCatalog.Gate, DcRigTargetCatalog.Compressor, DcRigTargetCatalog.Eq,
            DcRigTargetCatalog.Overdrive, DcRigTargetCatalog.Distortion, DcRigTargetCatalog.Amp,
            DcRigTargetCatalog.Cab, DcRigTargetCatalog.Chorus, DcRigTargetCatalog.Flanger,
            DcRigTargetCatalog.Phaser, DcRigTargetCatalog.Delay, DcRigTargetCatalog.Reverb,
            DcRigTargetCatalog.Octaver
        };
        Assert.Equal(ToneDraftValidator.MaxModules + 1, ids.Length);

        var tooLong = new DcRigPreset(ids
            .Select(id => new DcRigModule(id, new Dictionary<string, float>(StringComparer.Ordinal)))
            .ToList());

        var ceiling = ToneEvalCeiling.Of(tooLong);

        Assert.Equal(ToneDraftValidator.MaxModules, ceiling.DesignedModules.Count);
        Assert.Equal(ids.Length, ceiling.TruthModules.Count);
        Assert.DoesNotContain(DcRigTargetCatalog.Octaver, ceiling.DesignedModules);
        Assert.Contains(ToneFamily.Pitch, ceiling.TruthFamilies);
        Assert.DoesNotContain(ToneFamily.Pitch, ceiling.DesignedFamilies);
        Assert.True(ceiling.Total < 100.0,
            $"a chain the validator truncates scored {ceiling.Total:F2} — the ceiling " +
            "is not going through ToneDraftValidator at all.");
    }

    [Fact]
    public void TheLocalCorpusCeiling_IsReportedAndIsNotBelowTheFloor()
    {
        Assert.SkipWhen(ToneEvalCorpus.ResolveCorpusRoot() is null, ToneEvalCorpus.MissingCorpus);

        var entries = ToneEvalCorpus.LoadLocal();
        Assert.SkipWhen(entries.Count == 0, ToneEvalCorpus.MissingCorpus);

        var ceilings = entries
            .Select(e => (e, Total: ToneEvalCeiling.TotalOf(e.Truth)))
            .OrderBy(x => x.Total)
            .ToList();

        var mean = ceilings.Average(x => x.Total);
        var worst = ceilings[0];
        Console.WriteLine(
            $"[ai-eval] local ceiling over {entries.Count} songs: mean {mean:F2}, " +
            $"worst {worst.Total:F2} ({worst.e.Artist} — {worst.e.SongTitle} / {worst.e.ToneName})");

        // Not a wall-clock or a quality assertion — a STRUCTURAL one. If the real
        // corpus's ceiling ever falls far below the fixture corpus's, the eval has
        // started grading models against chains they cannot produce, and the fix is
        // in the draft schema or the converter, never here.
        Assert.True(mean >= 95.0,
            $"the local corpus can only ever score {mean:F2} on average — the design " +
            "pipeline cannot express these truths, so every model score is capped. " +
            $"Worst row: {worst.e.Artist} — {worst.e.SongTitle} / {worst.e.ToneName} " +
            $"at {worst.Total:F2}.");
    }
}
