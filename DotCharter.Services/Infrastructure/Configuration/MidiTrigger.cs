using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Localization;

namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>What on a MIDI controller a <see cref="MidiTrigger" /> listens for.</summary>
public enum MidiTriggerKind
{
    None = 0,

    /// <summary>A note-on with a non-zero velocity: a pad or a key.</summary>
    Note = 1,

    /// <summary>A control change crossing from below 64 to 64 or above: a button
    /// that sends 127 on press and 0 on release, or a knob turned past half.</summary>
    ControlChange = 2
}

/// <summary>
///     One press on a MIDI controller that an editor verb can be bound to — the
///     MIDI twin of <see cref="KeyChord" />. A note or a CC number on ONE channel
///     (0..15): the same pad on another channel is another trigger.
/// </summary>
[JsonConverter(typeof(MidiTriggerJsonConverter))]
public readonly record struct MidiTrigger(MidiTriggerKind Kind, int Channel, int Number)
{
    public static readonly MidiTrigger None = default;

    private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static MidiTrigger Note(int channel, int note) => new(MidiTriggerKind.Note, channel, note);

    public static MidiTrigger ControlChange(int channel, int controller) =>
        new(MidiTriggerKind.ControlChange, channel, controller);

    /// <summary>A real trigger: a known kind, a channel 0..15 and a number 0..127.</summary>
    public bool IsBound => Kind is MidiTriggerKind.Note or MidiTriggerKind.ControlChange
                           && Channel is >= 0 and <= 15 && Number is >= 0 and <= 127;

    /// <summary>The note's name with its octave, middle C (60) as C4: <c>C#3</c>.</summary>
    public static string NoteName(int note) =>
        NoteNames[note % 12] + (note / 12 - 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The invariant form the keybind file stores and a log prints:
    /// <c>note:1:60</c> / <c>cc:10:20</c> (channel 1..16), or <c>None</c>.</summary>
    public override string ToString() => MidiTriggerJsonConverter.Serialize(this);

    /// <summary>The trigger as a user reads it: <c>C4 · ch 1</c> / <c>CC 20 · ch 10</c>.</summary>
    public string ToDisplayString(ILocalizationService localization)
    {
        if (!IsBound) return "—";
        var channel = (Channel + 1).ToString(CultureInfo.CurrentCulture);
        return Kind == MidiTriggerKind.Note
            ? localization.Format("Midi_Trigger_Note", NoteName(Number), channel)
            : localization.Format("Midi_Trigger_Cc", Number.ToString(CultureInfo.CurrentCulture), channel);
    }
}

/// <summary>
///     Serializes <see cref="MidiTrigger" /> as one readable string so
///     <c>keybinds.json</c> stays hand-editable. Anything it cannot read is
///     <see cref="MidiTrigger.None" />, which the loader then drops.
/// </summary>
public sealed class MidiTriggerJsonConverter : JsonConverter<MidiTrigger>
{
    public override MidiTrigger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? Parse(reader.GetString()) : MidiTrigger.None;

    public override void Write(Utf8JsonWriter writer, MidiTrigger value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Serialize(value));

    public static string Serialize(MidiTrigger trigger)
    {
        if (!trigger.IsBound) return "None";
        var kind = trigger.Kind == MidiTriggerKind.Note ? "note" : "cc";
        return string.Create(CultureInfo.InvariantCulture, $"{kind}:{trigger.Channel + 1}:{trigger.Number}");
    }

    public static MidiTrigger Parse(string? raw)
    {
        var parts = raw?.Split(':', StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length != 3) return MidiTrigger.None;
        var kind = parts[0].ToLowerInvariant() switch
        {
            "note" => MidiTriggerKind.Note,
            "cc" => MidiTriggerKind.ControlChange,
            _ => MidiTriggerKind.None
        };
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return MidiTrigger.None;
        }

        var trigger = new MidiTrigger(kind, channel - 1, number);
        return trigger.IsBound ? trigger : MidiTrigger.None;
    }
}
