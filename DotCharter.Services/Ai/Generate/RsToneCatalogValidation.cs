using System.Globalization;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>One reason a generated tone is not something Rocksmith can load.</summary>
/// <param name="Code">A stable machine code, for a test and for a log.</param>
/// <param name="GearKey">The offending gear, or null for a whole-tone fault.</param>
/// <param name="KnobKey">The offending knob's FULL key, or null.</param>
/// <param name="Message">One sentence naming the gear or the value and its range.</param>
public sealed record RsToneFault(string Code, string? GearKey, string? KnobKey, string Message);

/// <summary>The stable <see cref="RsToneFault.Code" /> vocabulary.</summary>
public static class RsToneFaultCodes
{
    public const string BadKey = "bad-tone-key";
    public const string UnknownGear = "unknown-gear";
    public const string WrongGearType = "wrong-gear-type";
    public const string UnknownKnob = "unknown-knob";
    public const string KnobOutOfRange = "knob-out-of-range";
    public const string KnobNotFinite = "knob-not-a-number";
    public const string BadEnumValue = "bad-enum-value";
    public const string TooManySlots = "too-many-slots";
    public const string BadSlotIndex = "bad-slot-index";
    public const string VolumeOutOfRange = "volume-out-of-range";
    public const string UnknownDescriptor = "unknown-descriptor";
}

/// <summary>
///     <b>The closed-catalog gate for a generated Rocksmith tone. Nothing this lane
///     writes gets past it, and it REJECTS rather than repairs.</b>
///     <para>
///         Rocksmith's gear is a fixed list shipped with the game: 63 amps, 450
///         cabinets, 83 pedals, 17 racks, each with declared knob ranges
///         (<see cref="RsGearCatalog" />). A tone naming gear the game does not have,
///         or a knob outside its range, is not a tone that sounds slightly wrong — it
///         is a tone the game or DLC Builder refuses, or silently drops a slot from.
///         A language model asked for gear WILL invent a plausible pedal name.
///     </para>
///     <para>
///         <b><see cref="RsToneAuthoring.ClampKnobValue" /> is deliberately NOT used
///         here.</b> Clamping is right in the tone EDITOR, where a human dragged a
///         knob past its stop and meant "as far as it goes". It is wrong for a
///         generated value: a model that answers <c>Gain 250</c> on a 0–100 knob did
///         not mean 100, it misunderstood the unit — and silently writing 100 turns a
///         detectable fault into a tone nobody can explain. The fault names the value
///         and the range and the caller decides.
///     </para>
///     <para>
///         Pure and throw-free: catalogue in, fault or null out. The catalogue is the
///         one passed in, so a test can hand over <see cref="RsGearCatalog.Empty" />
///         and see the rejection rather than a pass.
///     </para>
/// </summary>
public static class RsToneCatalogValidation
{
    /// <summary>Rocksmith's slot count per rail: PrePedal1–4, Rack1–4,
    /// PostPedal1–4. A fifth has nowhere to serialize to.</summary>
    public const int MaxSlotsPerRail = 4;

    /// <summary>The first fault in <paramref name="tone" />, or null when every
    /// gear key, slot, knob and descriptor is one the game has.</summary>
    /// <param name="tone">The tone about to be written.</param>
    /// <param name="catalog">The gear catalogue to validate against.</param>
    public static RsToneFault? FirstFault(RsTone? tone, RsGearCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (tone is null) return new RsToneFault(RsToneFaultCodes.BadKey, null, null, "the tone is null");

        if (!RsToneAuthoring.IsValidKey(tone.Key))
        {
            return new RsToneFault(RsToneFaultCodes.BadKey, null, null,
                $"'{tone.Key}' is not a valid Rocksmith tone key (a letter followed by letters, digits " +
                $"or underscores, at most {RsToneAuthoring.MaxKeyLength} characters)");
        }

        return FirstLoadFault(tone, catalog);
    }

    /// <summary>
    ///     The first fault the GAME would refuse in <paramref name="tone" /> — volume, descriptors,
    ///     gear, slots and knobs — without the authoring rule for the key. For a tone that already
    ///     exists and is sent as it is (the in-game preview): official packages key their tones
    ///     after the song code, and those keys may start with a digit ('311allm_clean'), which
    ///     <see cref="RsToneAuthoring.IsValidKey" /> refuses for a key DotCharter writes.
    /// </summary>
    /// <param name="tone">The tone about to be sent.</param>
    /// <param name="catalog">The gear catalogue to validate against.</param>
    public static RsToneFault? FirstLoadFault(RsTone? tone, RsGearCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (tone is null) return new RsToneFault(RsToneFaultCodes.BadKey, null, null, "the tone is null");

        if (!float.IsFinite(tone.VolumeDb) ||
            tone.VolumeDb < RsToneAuthoring.MinVolumeDb || tone.VolumeDb > RsToneAuthoring.MaxVolumeDb)
        {
            return new RsToneFault(RsToneFaultCodes.VolumeOutOfRange, null, null,
                $"the tone volume {F(tone.VolumeDb)} dB is outside Rocksmith's range " +
                $"[{F(RsToneAuthoring.MinVolumeDb)}, {F(RsToneAuthoring.MaxVolumeDb)}]");
        }

        foreach (var descriptor in tone.ToneDescriptors)
        {
            if (RsToneAuthoring.FindDescriptor(descriptor) is null)
            {
                return new RsToneFault(RsToneFaultCodes.UnknownDescriptor, null, null,
                    $"'{descriptor}' is not one of Rocksmith's tone descriptors");
            }
        }

        if (CheckGear(tone.Amp, "Amps", catalog) is { } ampFault) return ampFault;
        if (CheckGear(tone.Cabinet, "Cabinets", catalog) is { } cabFault) return cabFault;

        if (CheckRail(tone.PrePedals, "Pedals", catalog) is { } preFault) return preFault;
        if (CheckRail(tone.Racks, "Racks", catalog) is { } rackFault) return rackFault;
        if (CheckRail(tone.PostPedals, "Pedals", catalog) is { } postFault) return postFault;

        return null;
    }

    /// <summary>True when the tone is one Rocksmith can load.</summary>
    /// <param name="tone">The tone.</param>
    /// <param name="catalog">The gear catalogue.</param>
    public static bool IsValid(RsTone? tone, RsGearCatalog catalog) => FirstFault(tone, catalog) is null;

    /// <summary>
    ///     Throws when <paramref name="tone" /> is not loadable — the shape a WRITER
    ///     uses, so "validated before it is written" is a statement about control
    ///     flow rather than about whoever remembered to call the predicate.
    /// </summary>
    /// <param name="tone">The tone about to be written.</param>
    /// <param name="catalog">The gear catalogue.</param>
    /// <param name="what">What is being written, for the message ("the tone for
    /// 'Chorus'").</param>
    /// <exception cref="RsToneCatalogException">The tone is not loadable.</exception>
    public static RsTone Require(RsTone? tone, RsGearCatalog catalog, string what)
    {
        var fault = FirstFault(tone, catalog);
        if (fault is not null) throw new RsToneCatalogException(fault, what);
        return tone!;
    }

    private static RsToneFault? CheckRail(IReadOnlyList<RsPedal> rail, string expectedType, RsGearCatalog catalog)
    {
        if (rail.Count > MaxSlotsPerRail)
        {
            return new RsToneFault(RsToneFaultCodes.TooManySlots, null, null,
                $"{rail.Count} {expectedType.ToLowerInvariant()} slots were produced; Rocksmith has " +
                $"{MaxSlotsPerRail}");
        }

        foreach (var pedal in rail)
        {
            if (pedal.SlotIndex is < 1 or > MaxSlotsPerRail)
            {
                return new RsToneFault(RsToneFaultCodes.BadSlotIndex, pedal.GearKey, null,
                    $"'{pedal.GearKey}' is in slot {pedal.SlotIndex}; Rocksmith's slots are 1–{MaxSlotsPerRail}");
            }

            if (CheckGear(pedal, expectedType, catalog) is { } fault) return fault;
        }

        return null;
    }

    private static RsToneFault? CheckGear(RsGear gear, string expectedType, RsGearCatalog catalog)
    {
        if (!catalog.TryGetGear(gear.GearKey, out var data))
        {
            return new RsToneFault(RsToneFaultCodes.UnknownGear, gear.GearKey, null,
                $"'{gear.GearKey}' is not a piece of gear Rocksmith has");
        }

        if (!string.Equals(data.Type, expectedType, StringComparison.Ordinal))
        {
            return new RsToneFault(RsToneFaultCodes.WrongGearType, gear.GearKey, null,
                $"'{gear.GearKey}' is a {data.Type} entry and cannot go in a {expectedType} slot");
        }

        foreach (var (knobKey, value) in gear.KnobValues)
        {
            if (!catalog.TryGetKnobByFullKey(knobKey, out var knob))
            {
                return new RsToneFault(RsToneFaultCodes.UnknownKnob, gear.GearKey, knobKey,
                    $"'{knobKey}' is not a knob '{gear.GearKey}' has");
            }

            if (!float.IsFinite(value))
            {
                return new RsToneFault(RsToneFaultCodes.KnobNotFinite, gear.GearKey, knobKey,
                    $"'{knobKey}' is {value}, which is not a number");
            }

            var min = Math.Min(knob.MinValue, knob.MaxValue);
            var max = Math.Max(knob.MinValue, knob.MaxValue);
            if (value < min || value > max)
            {
                return new RsToneFault(RsToneFaultCodes.KnobOutOfRange, gear.GearKey, knobKey,
                    $"'{knobKey}' is {F(value)}, outside Rocksmith's range [{F(min)}, {F(max)}]");
            }

            // An ENUM knob is an index, and a fractional or out-of-list one is a
            // selection the game cannot make. Checked separately from the range
            // because the range alone accepts 1.5 of 0..3.
            if (knob.UnitType == RsKnobUnit.Enum && knob.EnumValues is { Count: > 0 } options)
            {
                if (Math.Abs(value - MathF.Round(value)) > 1e-4f || (int)MathF.Round(value) >= options.Count)
                {
                    return new RsToneFault(RsToneFaultCodes.BadEnumValue, gear.GearKey, knobKey,
                        $"'{knobKey}' is {F(value)}; it selects one of {options.Count} settings " +
                        $"(0–{options.Count - 1})");
                }
            }
        }

        return null;
    }

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
///     A generated tone that Rocksmith could not load. Carries the
///     <see cref="RsToneFault" /> so a caller can report the gear or the value
///     rather than "generation failed".
/// </summary>
public sealed class RsToneCatalogException : Exception
{
    /// <summary>Builds the exception for a fault.</summary>
    /// <param name="fault">What was wrong.</param>
    /// <param name="what">What was being written.</param>
    public RsToneCatalogException(RsToneFault fault, string what)
        : base($"{what}: {fault.Message}.") => Fault = fault;

    /// <summary>The fault, with its code and the gear/knob it names.</summary>
    public RsToneFault Fault { get; }
}
