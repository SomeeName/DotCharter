using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity;

/// <summary>
///     Builds Rocksmith tones out of REAL catalogue gear for the fidelity targets:
///     every knob a gear declares is present at its catalogue default (as a tone
///     the game writes would carry it), then the caller's overrides apply. Pure.
/// </summary>
public static class RsToneFidelityTones
{
    private static readonly RsToneConverter Converter = new();

    /// <summary>A tone with one amp, one cab and optional pre-pedals / racks /
    /// loop (post) pedals, as the product converts it
    /// (<see cref="RsToneConverter.ConvertFilled" />). Signal order is
    /// <see cref="RsTone.InSignalOrder" />'s: pre-pedals, amp, cab, racks, loop pedals.</summary>
    public static DcRigPreset Convert(
        string ampKey,
        string cabKey,
        IReadOnlyDictionary<string, float>? ampKnobs = null,
        IReadOnlyList<(string Key, IReadOnlyDictionary<string, float>? Knobs)>? prePedals = null,
        IReadOnlyList<(string Key, IReadOnlyDictionary<string, float>? Knobs)>? racks = null,
        IReadOnlyList<(string Key, IReadOnlyDictionary<string, float>? Knobs)>? postPedals = null)
    {
        var pre = (prePedals ?? []).Select((p, i) => Pedal(RsGearRole.PrePedal, i + 1, p.Key, p.Knobs)).ToList();
        var rk = (racks ?? []).Select((p, i) => Pedal(RsGearRole.Rack, i + 1, p.Key, p.Knobs)).ToList();
        var post = (postPedals ?? []).Select((p, i) => Pedal(RsGearRole.PostPedal, i + 1, p.Key, p.Knobs)).ToList();
        var tone = new RsTone(
            "fidelity", "fidelity", 0f, [],
            new RsAmp(ampKey, "Amp", Knobs(ampKey, ampKnobs)),
            new RsCab(cabKey, null, new Dictionary<string, float>()),
            pre, rk, post);
        return Converter.ConvertFilled(tone, out _);
    }

    /// <summary>Knob overrides by POT suffix, e.g. <c>("Gain", 50)</c>.</summary>
    public static IReadOnlyDictionary<string, float> Pots(params (string Pot, float Value)[] pots)
        => pots.ToDictionary(p => p.Pot, p => p.Value, StringComparer.Ordinal);

    private static RsPedal Pedal(RsGearRole role, int slot, string key, IReadOnlyDictionary<string, float>? knobs)
    {
        var type = role == RsGearRole.Rack ? RsGearType.Rack : RsGearType.Pedal;
        var category = RsGearCatalog.Shared.GetCategory(key);
        return new RsPedal(role, slot, key, type, category, Knobs(key, knobs));
    }

    private static Dictionary<string, float> Knobs(string gearKey, IReadOnlyDictionary<string, float>? overrides)
    {
        if (!RsGearCatalog.Shared.TryGetGear(gearKey, out var gear))
            throw new ArgumentException($"'{gearKey}' is not catalogue gear", nameof(gearKey));

        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var knob in gear.Knobs ?? [])
            values[knob.Key] = overrides is not null && overrides.TryGetValue(knob.Pot, out var v) ? v : knob.DefaultValue;
        return values;
    }
}
