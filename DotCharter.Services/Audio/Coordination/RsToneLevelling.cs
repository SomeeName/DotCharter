using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>
/// Levels a song's converted Rocksmith tones by their RENDERED loudness:
///     each tone's trailing master gain becomes
///     <see cref="RsToneLoudnessPolicy.TrailingGainDb" /> of its measured chain, plus the
///     song-relative intent the tones' own Rocksmith <c>Volume</c>s carry
///     (<see cref="RsToneLoudnessPolicy.IntentOffsetsDb" />).
///     <para>
///         <b>Never blocks, never guesses twice.</b> <see cref="Level" /> only reads the
///         meter's CACHE: a tone measured earlier in the session is levelled at once,
///         one that is not keeps the converter's estimate and is listed as pending for
///         <see cref="MeasureAsync" /> — off every thread that matters — after which
///         the caller levels again and publishes. Levelling always starts from the
///         CONVERTED preset, never from a levelled one, so doing it twice is the same as
///         doing it once.
///     </para>
/// </summary>
public sealed class RsToneLevelling
{
    private readonly IToneLoudnessMeter? _meter;

    public RsToneLevelling(IToneLoudnessMeter? meter) => _meter = meter;

    /// <summary>False when no meter is wired (a test, a build with no offline rig):
    /// every tone keeps its estimate.</summary>
    public bool IsEnabled => _meter is not null;

    /// <summary>
    ///     The levelled copy of <paramref name="converted" /> (every alias of one tone
    ///     keeps pointing at ONE preset object). <paramref name="volumesDb" /> holds the
    ///     Rocksmith <c>Volume</c> per key; the tones of the map are the set the intent
    ///     is relative to. <paramref name="pending" /> lists the chains with no cached
    ///     measurement yet.
    /// </summary>
    public Dictionary<string, DcRigPreset> Level(
        IReadOnlyDictionary<string, DcRigPreset> converted,
        IReadOnlyDictionary<string, float> volumesDb,
        out List<IReadOnlyList<DcRigModule>> pending)
    {
        pending = [];
        var result = new Dictionary<string, DcRigPreset>(StringComparer.OrdinalIgnoreCase);
        if (_meter is null || converted.Count == 0)
        {
            foreach (var (k, v) in converted) result[k] = v;
            return result;
        }

        // The DISTINCT tones (a tone is indexed by its key and its name).
        var distinct = new List<DcRigPreset>();
        var volumes = new List<float>();
        var seen = new HashSet<DcRigPreset>(ReferenceEqualityComparer.Instance);
        foreach (var (key, preset) in converted)
        {
            if (!seen.Add(preset)) continue;
            distinct.Add(preset);
            volumes.Add(volumesDb.TryGetValue(key, out var v) ? v : float.NaN);
        }

        var intents = RsToneLoudnessPolicy.IntentOffsetsDb(volumes);
        var levelled = new Dictionary<DcRigPreset, DcRigPreset>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < distinct.Count; i++)
        {
            var preset = distinct[i];
            if (Body(preset) is not { } body)
            {
                levelled[preset] = preset;
                continue;
            }

            if (_meter.TryGetCached(body) is { } loudness)
            {
                levelled[preset] = loudness.IsMeasured
                    ? WithLevel(preset, RsToneLoudnessPolicy.NeededGainDb(loudness, intents[i]))
                    : preset;
            }
            else
            {
                levelled[preset] = preset;
                pending.Add(body);
            }
        }

        foreach (var (k, v) in converted) result[k] = levelled[v];
        return result;
    }

    /// <summary>Measures <paramref name="bodies" /> (the meter caches each). Never throws.</summary>
    public async Task MeasureAsync(IReadOnlyList<IReadOnlyList<DcRigModule>> bodies, CancellationToken ct)
    {
        if (_meter is null) return;
        foreach (var body in bodies)
        {
            if (ct.IsCancellationRequested) return;
            await _meter.MeasureAsync(body, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The chain without its trailing master <c>gain</c>, or null for a preset
    /// that has none (not a conversion: left alone).</summary>
    public static IReadOnlyList<DcRigModule>? Body(DcRigPreset preset)
        => TrailingGainDb(preset) is null ? null : preset.Modules.Take(preset.Modules.Count - 1).ToList();

    /// <summary>The trailing master gain (dB), or null when the last module is not an
    /// active <c>gain</c>.</summary>
    public static float? TrailingGainDb(DcRigPreset preset)
    {
        if (preset.Modules.Count == 0) return null;
        var last = preset.Modules[^1];
        if (last.ModuleTypeId != DcRigTargetCatalog.Gain || last.Bypassed) return null;
        return last.Params.TryGetValue("gainDb", out var g) ? g : 0f;
    }

    /// <summary>
    ///     <paramref name="preset" /> levelled by <paramref name="neededDb" />: the
    ///     trailing gain takes what its ±24 dB can, and the rest goes into the output
    /// trim of the tone's Rocksmith amp chain (`rsamp`), whose clean settings
    ///     can sit 30+ dB under the reference. A tone without one keeps the clamp.
    /// </summary>
    public static DcRigPreset WithLevel(DcRigPreset preset, float neededDb)
    {
        var trailing = Math.Clamp(neededDb, -RsToneLoudnessPolicy.MaxGainDb, RsToneLoudnessPolicy.MaxGainDb);
        var levelled = WithTrailingGain(preset, trailing);
        var spill = neededDb - trailing;
        return spill == 0f || AmpLevelDb(levelled) is not { } level ? levelled : WithAmpLevel(levelled, level + spill);
    }

    /// <summary>The output trim (dB) of the tone's Rocksmith amp chain (`rsamp`), or
    /// null when the tone has none.</summary>
    public static float? AmpLevelDb(DcRigPreset preset)
    {
        var amp = preset.Modules.FirstOrDefault(m => m.ModuleTypeId == DcRigTargetCatalog.RsAmpChain && !m.Bypassed);
        return amp?.Params.GetValueOrDefault("level");
    }

    /// <summary><paramref name="preset" /> with its Rocksmith amp chain's output trim
    /// set to <paramref name="levelDb" /> (within ±24 dB); unchanged without one.</summary>
    public static DcRigPreset WithAmpLevel(DcRigPreset preset, float levelDb)
    {
        var modules = preset.Modules.ToList();
        var i = modules.FindIndex(m => m.ModuleTypeId == DcRigTargetCatalog.RsAmpChain && !m.Bypassed);
        if (i < 0) return preset;
        modules[i] = modules[i] with
        {
            Params = new Dictionary<string, float>(modules[i].Params, StringComparer.Ordinal)
            {
                ["level"] = Math.Clamp(levelDb, -RsToneLoudnessPolicy.MaxGainDb, RsToneLoudnessPolicy.MaxGainDb)
            }
        };
        return preset with { Modules = modules };
    }

    /// <summary><paramref name="preset" /> with its trailing gain set to
    /// <paramref name="gainDb" />; every other module, and the rig's scenes, kept.</summary>
    public static DcRigPreset WithTrailingGain(DcRigPreset preset, float gainDb)
    {
        var modules = preset.Modules.ToList();
        var last = modules[^1];
        modules[^1] = last with
        {
            Params = new Dictionary<string, float>(last.Params, StringComparer.Ordinal) { ["gainDb"] = gainDb }
        };
        return preset with { Modules = modules };
    }
}
