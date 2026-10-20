using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Loudness;

/// <summary>
///     The gain math of level-matched A/B switching, pure: how far the
///     incoming slot is trimmed so it plays at the level of the reference slot, and
///     how that trim rides on the rig's TRAILING <c>gain</c> module — the master
///     gain every normalized rig already ends with
///     (<see cref="DcRigLoudnessNormalizer.Normalize" />), so matching adds no new
///     DSP. A louder tone sounds better in a blind A/B; without the match the
///     comparison picks the louder one, not the better one.
/// </summary>
public static class CompareLevelTrim
{
    /// <summary>The native gain module's id and its one param.</summary>
    public const string GainModuleId = DcRigTargetCatalog.Gain;

    public const string GainParamId = "gainDb";

    /// <summary>The most a match ever moves a slot. Past this the two tones are
    /// not "the same level, different colour" any more, and a bigger correction
    /// would mostly be chasing a measurement taken while the player was quiet.</summary>
    public const double MaxTrimDb = 12;

    /// <summary>The gain module's own range (DcRigTargetCatalog: ±24 dB).</summary>
    public const double GainFloorDb = -24;

    public const double GainCeilingDb = 24;

    /// <summary>Smaller trims are not applied: under the just-noticeable
    /// difference, and every applied trim is a param write.</summary>
    public const double MinTrimDb = 0.1;

    /// <summary>The trim that brings <paramref name="incomingDb" /> to
    /// <paramref name="referenceDb" />, clamped to ±<see cref="MaxTrimDb" />;
    /// 0 when either level is unknown or not finite.</summary>
    public static double TrimDb(double? referenceDb, double? incomingDb)
    {
        if (referenceDb is not { } reference || incomingDb is not { } incoming ||
            !double.IsFinite(reference) || !double.IsFinite(incoming))
        {
            return 0;
        }

        var trim = Math.Clamp(reference - incoming, -MaxTrimDb, MaxTrimDb);
        return Math.Abs(trim) < MinTrimDb ? 0 : trim;
    }

    /// <summary>The static estimate of a rig's output level, the fallback when a
    /// slot has not been heard yet (<see cref="DcRigLoudnessNormalizer.EstimateChainOutputDb" />).
    /// A rig's own trailing gain counts — it is part of what the slot sounds like.</summary>
    public static double? EstimateDb(DcRigPreset? rig)
        => rig is null ? null : DcRigLoudnessNormalizer.EstimateChainOutputDb(rig.Modules);

    /// <summary>Moves a gain module at <paramref name="currentDb" /> by
    /// <paramref name="trimDb" />, inside the module's range. Returns the new value
    /// and the trim that actually landed (less than asked for at a range end) —
    /// the caller records the LANDED trim, so stripping it later is exact.</summary>
    public static (double GainDb, double AppliedDb) Apply(double currentDb, double trimDb)
    {
        var next = Math.Clamp(currentDb + trimDb, GainFloorDb, GainCeilingDb);
        return (next, next - currentDb);
    }

    /// <summary>True when the last module is an ENGAGED gain module — the only
    /// place a trim may ride.</summary>
    public static bool HasTrailingGain(IReadOnlyList<DcRigModule> modules)
        => modules.Count > 0 && modules[^1].ModuleTypeId == GainModuleId && !modules[^1].Bypassed;

    /// <summary>The rig with a 0 dB trailing gain module appended when it has
    /// none, so a trim has somewhere to ride; unchanged otherwise. Scenes are
    /// carried (<c>with</c>, never <c>new</c>): a scene matches modules by type
    /// and occurrence, so an appended gain is one no scene has stored.</summary>
    public static DcRigPreset EnsureTrailingGain(DcRigPreset rig)
    {
        if (HasTrailingGain(rig.Modules)) return rig;
        var gain = new DcRigModule(GainModuleId, new Dictionary<string, float>(StringComparer.Ordinal) { [GainParamId] = 0f });
        return rig with { Modules = [.. rig.Modules, gain] };
    }

    /// <summary>The modules with <paramref name="appliedDb" /> taken back off the
    /// trailing gain — what the slot holds, so a trim never compounds across
    /// switches. Unchanged when there is no trim or no trailing gain (the player
    /// removed it: the trim went with it).</summary>
    public static IReadOnlyList<DcRigModule> Strip(IReadOnlyList<DcRigModule> modules, double appliedDb)
    {
        if (appliedDb == 0 || !HasTrailingGain(modules)) return modules;
        var last = modules[^1];
        var current = last.Params.TryGetValue(GainParamId, out var v) ? v : 0f;
        var values = new Dictionary<string, float>(last.Params, StringComparer.Ordinal)
        {
            [GainParamId] = (float)Math.Clamp(current - appliedDb, GainFloorDb, GainCeilingDb)
        };
        return [.. modules.Take(modules.Count - 1), last with { Params = values }];
    }
}
