using System.Text.Json.Serialization;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
///     A named, ordered list of tones for a gig (<c>RigConfig.RigSetlists</c>).
///     Entries name presets by id — factory or the player's own — so a tone
///     renamed or overwritten stays in the list, and a deleted one reads as
///     missing rather than silently skipping a song.
/// </summary>
public sealed class RigSetlistDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<RigSetlistEntryDto> Entries { get; set; } = [];

    public RigSetlistDto Clone() => new()
    {
        Id = Id,
        Name = Name,
        Entries = (Entries ?? []).Where(e => e is not null).Select(e => e.Clone()).ToList()
    };
}

/// <summary>One song of a setlist: the tone, and the scene it starts on (null =
/// the scene the tone was saved on).</summary>
public sealed class RigSetlistEntryDto
{
    public string PresetId { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Scene { get; set; }

    public RigSetlistEntryDto Clone() => new() { PresetId = PresetId, Scene = Scene };
}
