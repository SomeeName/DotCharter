using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>
/// Serializes an <see cref="RsTone"/> back into a manifest-compatible tone JSON
/// body — the exact shape Rocksmith 2014 and the toolkit read from a manifest's
/// <c>Tones</c> array (Rocksmith2014.NET <c>ToneDto</c>/<c>GearDto</c>/<c>PedalDto</c>
/// with null fields omitted):
/// <code>
/// { "GearList": { "Rack1": {...}, "Amp": {...}, "Cabinet": {...},
///                 "PrePedal1": {...}, "PostPedal1": {...} },
///   "ToneDescriptors": [...], "NameSeparator": " - ", "IsCustom": true,
///   "Volume": "-12.000", "Key": "...", "Name": "...", "SortOrder": 0 }
/// </code>
/// Inverse of <see cref="RsToneJsonParser"/>: parse → write → parse is
/// structurally lossless for everything the <see cref="RsTone"/> model captures
/// (per-gear <c>Skin</c>/<c>SkinIndex</c> cosmetics are not modelled and are
/// therefore dropped — the game treats them as optional). Pure, deterministic,
/// invariant-culture, throw-free for any well-formed <see cref="RsTone"/>.
/// </summary>
public static class RsToneJsonWriter
{
    /// <summary>The default name separator Rocksmith2014.NET writes
    /// (<c>Tone.DefaultNameSeparator</c>). The parser ignores the field, so the
    /// writer always emits this canonical value.</summary>
    public const string DefaultNameSeparator = " - ";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Manifest tone bodies embed straight into the package manifest; keep
        // them compact like the game's own bodies. Relaxed escaping matches the
        // manifest generator's serializer options.
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Writes the manifest tone JSON body for <paramref name="tone"/>.</summary>
    public static string Write(RsTone tone)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            WriteGearList(writer, tone);

            writer.WriteStartArray("ToneDescriptors");
            foreach (var descriptor in tone.ToneDescriptors)
                writer.WriteStringValue(descriptor);
            writer.WriteEndArray();

            writer.WriteString("NameSeparator", DefaultNameSeparator);
            writer.WriteBoolean("IsCustom", true);
            writer.WriteString("Volume", FormatVolume(tone.VolumeDb));
            writer.WriteString("Key", tone.Key);
            writer.WriteString("Name", tone.Name);
            if (tone.SortOrder is { } sortOrder)
                writer.WriteNumber("SortOrder", sortOrder);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Rocksmith2014.NET's <c>volumeToString</c>: fixed three decimals,
    /// invariant culture (e.g. <c>-12.000</c>). The manifest carries Volume as a
    /// STRING.</summary>
    public static string FormatVolume(float volumeDb)
        => volumeDb.ToString("0.000", NumberFormatInfo.InvariantInfo);

    private static void WriteGearList(Utf8JsonWriter writer, RsTone tone)
    {
        writer.WriteStartObject("GearList");

        // Slot order mirrors Rocksmith2014.NET's GearDto declaration order:
        // Rack1..4, Amp, Cabinet, PrePedal1..4, PostPedal1..4. Empty slots are
        // omitted entirely (ignore-null DTO serialization).
        WritePedalSlots(writer, "Rack", tone.Racks);
        WriteGear(writer, "Amp", "Amps", tone.Amp);
        WriteGear(writer, "Cabinet", "Cabinets", tone.Cabinet);
        WritePedalSlots(writer, "PrePedal", tone.PrePedals);
        WritePedalSlots(writer, "PostPedal", tone.PostPedals);

        writer.WriteEndObject();
    }

    private static void WritePedalSlots(Utf8JsonWriter writer, string slotPrefix, IReadOnlyList<RsPedal> pedals)
    {
        // Preserve the parsed 1..4 slot numbers so a round-trip keeps a sparse
        // layout (e.g. only PrePedal2 occupied) intact. Duplicate/invalid slot
        // indices degrade to sequential placement rather than illegal JSON.
        var seen = new HashSet<int>();
        foreach (var pedal in pedals)
        {
            var slot = pedal.SlotIndex is >= 1 and <= 4 && !seen.Contains(pedal.SlotIndex)
                ? pedal.SlotIndex
                : NextFree(seen);
            if (slot > 4) continue; // more than four occupied slots cannot exist in RS

            seen.Add(slot);
            WriteGear(writer, slotPrefix + slot.ToString(CultureInfo.InvariantCulture),
                TypeString(pedal), pedal);
        }

        static int NextFree(HashSet<int> seen)
        {
            var i = 1;
            while (seen.Contains(i)) i++;
            return i;
        }
    }

    private static void WriteGear(Utf8JsonWriter writer, string slotName, string typeString, RsGear gear)
    {
        writer.WriteStartObject(slotName);

        // Property order mirrors PedalDto: Type, KnobValues, Key, Category.
        writer.WriteString("Type", typeString);

        writer.WriteStartObject("KnobValues");
        foreach (var knob in gear.KnobValues)
            writer.WriteNumber(knob.Key, knob.Value);
        writer.WriteEndObject();

        writer.WriteString("Key", gear.GearKey);
        if (!string.IsNullOrEmpty(gear.Category))
            writer.WriteString("Category", gear.Category);

        writer.WriteEndObject();
    }

    /// <summary>The manifest <c>Type</c> string for a pedal/rack slot. An
    /// <see cref="RsGearType.Unknown"/> (junk source data) degrades to the
    /// slot-role-appropriate family instead of emitting an invalid type.</summary>
    private static string TypeString(RsPedal pedal) => pedal.Type switch
    {
        RsGearType.Amp => "Amps",
        RsGearType.Cabinet => "Cabinets",
        RsGearType.Rack => "Racks",
        RsGearType.Pedal => "Pedals",
        _ => pedal.Role == RsGearRole.Rack ? "Racks" : "Pedals"
    };
}
