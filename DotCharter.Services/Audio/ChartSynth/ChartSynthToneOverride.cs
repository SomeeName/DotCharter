using System.Text.Json;
using DotCharter.Tone.Audio.ChartSynth;

namespace DotCharter.Services.Audio.ChartSynth;

/// <summary>
/// A song tone's own chart-synth instrument: while that tone plays it
///     wins over the player profile's, family by family. A null family follows
///     the profile — so an override made to put the guitar on its neck pickup
///     leaves the player's bass alone, and an override with both null (just
///     created, nothing changed yet) sounds exactly like the profile.
///     Saved beside the song's tone edits (<c>SongToneEditStore</c>).
/// </summary>
/// <param name="Guitar">The guitar this tone plays, or null for the profile's.</param>
/// <param name="Bass">The bass this tone plays, or null for the profile's.</param>
public sealed record ChartSynthToneOverride(ChartSynthInstrument? Guitar, ChartSynthInstrument? Bass)
{
    /// <summary>An override that changes nothing yet.</summary>
    public static readonly ChartSynthToneOverride Empty = new(null, null);

    /// <summary>The family's override, or null when it follows the profile.</summary>
    public ChartSynthInstrument? For(bool bass) => bass ? Bass : Guitar;

    /// <summary>This override with <paramref name="instrument" /> as its family's.</summary>
    public ChartSynthToneOverride With(ChartSynthInstrument instrument)
        => instrument.IsBass ? this with { Bass = instrument } : this with { Guitar = instrument };
}

/// <summary>
///     The ONE JSON shape of an instrument selection, shared by the profile's
///     instrument file and the song tones' overrides: <c>{"model": "StratHss",
///     "pickup": "Bridge", "coilSplit": false}</c>, by NAME so a renumbered enum
///     can never silently swap a guitar (the rule the instrument config fields follow).
/// </summary>
public static class ChartSynthInstrumentJson
{
    /// <summary>Writes <paramref name="instrument" /> as the property <paramref name="name" />.</summary>
    public static void Write(Utf8JsonWriter writer, string name, ChartSynthInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject(name);
        writer.WriteString("model", instrument.Model.ToString());
        writer.WriteString("pickup", instrument.Pickup.ToString());
        writer.WriteBoolean("coilSplit", instrument.CoilSplit);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     Reads the property <paramref name="name" /> of <paramref name="parent" />.
    ///     False when it is missing, not an object, or names something that is not
    ///     an instrument of the family — a bad value costs that family, never the file.
    /// </summary>
    public static bool TryRead(JsonElement parent, string name, bool bass, out ChartSynthInstrument instrument)
    {
        instrument = ChartSynthInstrumentSelection.Default(bass);
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var body) ||
            body.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var split = body.TryGetProperty("coilSplit", out var s) && s.ValueKind == JsonValueKind.True;
        return ChartSynthInstrumentSelection.TryParse(StringOf(body, "model"), StringOf(body, "pickup"), split, bass,
            out instrument);
    }

    /// <summary>Writes an override's families (absent = follows the profile).</summary>
    public static void WriteOverride(Utf8JsonWriter writer, string name, ChartSynthToneOverride value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject(name);
        if (value.Guitar is { } g) Write(writer, "guitar", g);
        if (value.Bass is { } b) Write(writer, "bass", b);
        writer.WriteEndObject();
    }

    /// <summary>Reads an override object; null when <paramref name="body" /> is not one.</summary>
    public static ChartSynthToneOverride? ReadOverride(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        return new ChartSynthToneOverride(
            TryRead(body, "guitar", bass: false, out var g) ? g : null,
            TryRead(body, "bass", bass: true, out var b) ? b : null);
    }

    private static string? StringOf(JsonElement body, string name)
        => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
