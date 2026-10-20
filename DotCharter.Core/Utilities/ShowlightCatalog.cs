namespace DotCharter.Core.Utilities;

public enum ShowlightLane
{
    Fog,
    Beams,
    Lasers
}

public sealed record ShowlightDefinition(
    ShowlightLane Lane,
    string Type,
    int Note,
    string DisplayName,
    int SortOrder);

public static class ShowlightCatalog
{
    private static readonly ShowlightDefinition[] Definitions =
    [
        new(ShowlightLane.Fog, "FOG_GREEN", 24, "Green", 0),
        new(ShowlightLane.Fog, "FOG_PINK", 25, "Pink", 1),
        new(ShowlightLane.Fog, "FOG_TEAL", 26, "Teal", 2),
        new(ShowlightLane.Fog, "FOG_ORANGE", 27, "Orange", 3),
        new(ShowlightLane.Fog, "FOG_BLUE", 28, "Blue", 4),
        new(ShowlightLane.Fog, "FOG_LIME", 29, "Lime", 5),
        new(ShowlightLane.Fog, "FOG_MAGENTA", 30, "Magenta", 6),
        new(ShowlightLane.Fog, "FOG_LIGHT_GREEN", 31, "Light Green", 7),
        new(ShowlightLane.Fog, "FOG_RED", 32, "Red", 8),
        new(ShowlightLane.Fog, "FOG_LIGHT_BLUE", 33, "Light Blue", 9),
        new(ShowlightLane.Fog, "FOG_YELLOW", 34, "Yellow", 10),
        new(ShowlightLane.Fog, "FOG_PURPLE", 35, "Purple", 11),
        new(ShowlightLane.Beams, "BEAMS_OFF", 42, "Off", 100),
        new(ShowlightLane.Beams, "BEAMS_GREEN", 48, "Green", 101),
        new(ShowlightLane.Beams, "BEAMS_PINK", 49, "Pink", 102),
        new(ShowlightLane.Beams, "BEAMS_LIGHT_BLUE", 50, "Light Blue", 103),
        new(ShowlightLane.Beams, "BEAMS_ORANGE", 51, "Orange", 104),
        new(ShowlightLane.Beams, "BEAMS_BLUE", 52, "Blue", 105),
        new(ShowlightLane.Beams, "BEAMS_YELLOW", 53, "Yellow", 106),
        new(ShowlightLane.Beams, "BEAMS_MAGENTA", 54, "Magenta", 107),
        new(ShowlightLane.Beams, "BEAMS_TEAL", 55, "Teal", 108),
        new(ShowlightLane.Beams, "BEAMS_RED_PLUS", 56, "Red+", 109),
        new(ShowlightLane.Beams, "BEAMS_BLUE_PLUS", 57, "Blue+", 110),
        new(ShowlightLane.Beams, "BEAMS_YELLOW_PLUS", 58, "Yellow+", 111),
        new(ShowlightLane.Beams, "BEAMS_PURPLE_PLUS", 59, "Purple+", 112),
        new(ShowlightLane.Lasers, "LASERS_ON", 66, "On", 200),
        new(ShowlightLane.Lasers, "LASERS_OFF", 67, "Off", 201)
    ];

    private static readonly Dictionary<string, ShowlightDefinition> DefinitionsByType =
        Definitions.ToDictionary(d => d.Type, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, ShowlightDefinition> DefinitionsByNote =
        Definitions.ToDictionary(d => d.Note);

    public static ShowlightDefinition DefaultDefinition { get; } =
        DefinitionsByType["FOG_LIGHT_GREEN"];

    public static IReadOnlyList<ShowlightDefinition> AllDefinitions { get; } = Definitions;

    public static IReadOnlyList<ShowlightDefinition> FogDefinitions { get; } =
        Definitions.Where(d => d.Lane == ShowlightLane.Fog).ToArray();

    public static IReadOnlyList<ShowlightDefinition> BeamDefinitions { get; } =
        Definitions.Where(d => d.Lane == ShowlightLane.Beams).ToArray();

    public static IReadOnlyList<ShowlightDefinition> LaserDefinitions { get; } =
        Definitions.Where(d => d.Lane == ShowlightLane.Lasers).ToArray();

    public static IReadOnlyList<ShowlightDefinition> GetDefinitions(ShowlightLane lane)
    {
        return lane switch
        {
            ShowlightLane.Fog => FogDefinitions,
            ShowlightLane.Beams => BeamDefinitions,
            ShowlightLane.Lasers => LaserDefinitions,
            _ => AllDefinitions
        };
    }

    public static bool TryGetDefinition(string? type, int note, out ShowlightDefinition definition)
    {
        var resolved = ResolveDefinition(type, note);
        if (resolved == null)
        {
            definition = DefaultDefinition;
            return false;
        }

        definition = resolved;
        return true;
    }

    public static bool TryGetLane(string? type, int note, out ShowlightLane lane)
    {
        if (TryGetDefinition(type, note, out var definition))
        {
            lane = definition.Lane;
            return true;
        }

        lane = default;
        return false;
    }

    public static string NormalizeType(string? type, int note = 0)
    {
        var resolved = ResolveDefinition(type, note);
        if (resolved != null) return resolved.Type;
        return string.IsNullOrWhiteSpace(type) ? string.Empty : type.Trim();
    }

    public static int NormalizeNote(string? type, int note = 0)
    {
        var resolved = ResolveDefinition(type, note);
        if (resolved != null) return resolved.Note;
        return note > 0 ? note : 0;
    }

    public static int GetSortOrder(string? type, int note = 0) => ResolveDefinition(type, note)?.SortOrder ?? int.MaxValue;

    public static string GetDisplayName(string? type, int note = 0)
    {
        var resolved = ResolveDefinition(type, note);
        if (resolved != null) return resolved.DisplayName;
        if (!string.IsNullOrWhiteSpace(type)) return type.Trim().Replace('_', ' ');
        return note > 0 ? $"Note {note}" : string.Empty;
    }

    private static ShowlightDefinition? ResolveDefinition(string? type, int note)
    {
        ShowlightDefinition? typeDefinition = null;
        var hasType = !string.IsNullOrWhiteSpace(type) &&
                      DefinitionsByType.TryGetValue(type.Trim(), out typeDefinition);
        ShowlightDefinition? noteDefinition = null;
        var hasNote = note > 0 && DefinitionsByNote.TryGetValue(note, out noteDefinition);

        if (hasType && hasNote)
        {
            if (typeDefinition!.Type.Equals(noteDefinition!.Type, StringComparison.OrdinalIgnoreCase))
                return typeDefinition;

            if (typeDefinition.Type.Equals(DefaultDefinition.Type, StringComparison.OrdinalIgnoreCase) &&
                noteDefinition.Note != DefaultDefinition.Note)
            {
                return noteDefinition;
            }

            return typeDefinition;
        }

        if (hasType) return typeDefinition;
        return hasNote ? noteDefinition : null;
    }
}
