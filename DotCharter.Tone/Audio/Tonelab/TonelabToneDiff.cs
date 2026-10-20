using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>One knob to move live: the GearList slot, the full RS knob key and its new value.</summary>
public sealed record TonelabKnobChange(string GearSlot, string KnobKey, float Value);

/// <summary>
///     Whether going from one tone to the next is only knob moves (sent as <c>tone knob</c>,
///     no chain rebuild in the game) or anything more (sent as a whole <c>apply-file</c>).
///     Pure.
/// </summary>
public static class TonelabToneDiff
{
    /// <summary>
    ///     The knob moves that turn <paramref name="from" /> into <paramref name="to" />, or
    ///     null when that takes more: different gear in any slot, a knob added or removed, or
    ///     a different Volume (the protocol has no volume command). Empty when nothing the
    ///     game hears changed. Name, key and descriptors are not the game's business here.
    /// </summary>
    public static IReadOnlyList<TonelabKnobChange>? KnobChanges(RsTone from, RsTone to)
    {
        if (BitConverter.SingleToInt32Bits(from.VolumeDb) != BitConverter.SingleToInt32Bits(to.VolumeDb)) return null;
        var before = Slots(from);
        var after = Slots(to);
        if (before is null || after is null || before.Count != after.Count) return null;

        var changes = new List<TonelabKnobChange>();
        foreach (var (slot, gear) in after)
        {
            if (!before.TryGetValue(slot, out var old) || !string.Equals(old.GearKey, gear.GearKey, StringComparison.Ordinal)) return null;
            if (old.KnobValues.Count != gear.KnobValues.Count) return null;
            foreach (var (knob, value) in gear.KnobValues.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!old.KnobValues.TryGetValue(knob, out var was)) return null;
                if (BitConverter.SingleToInt32Bits(was) != BitConverter.SingleToInt32Bits(value)) changes.Add(new TonelabKnobChange(slot, knob, value));
            }
        }

        return changes;
    }

    /// <summary>GearList slot name → gear, as <see cref="RsToneJsonWriter" /> names them; null on a slot index it would renumber.</summary>
    private static Dictionary<string, RsGear>? Slots(RsTone tone)
    {
        var slots = new Dictionary<string, RsGear>(StringComparer.Ordinal) { ["Amp"] = tone.Amp, ["Cabinet"] = tone.Cabinet };
        foreach (var (prefix, rail) in new[] { ("PrePedal", tone.PrePedals), ("Rack", tone.Racks), ("PostPedal", tone.PostPedals) })
        {
            foreach (var pedal in rail)
                if (pedal.SlotIndex is < 1 or > 4 || !slots.TryAdd(prefix + pedal.SlotIndex, pedal)) return null;
        }

        return slots;
    }
}
