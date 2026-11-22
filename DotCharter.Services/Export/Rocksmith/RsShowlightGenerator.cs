namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     Generates a show-light set from a project's sections and notes: a fog change at each section start,
///     a beam colour per pitch class, lasers on at the first solo (or 60 % into the song) and off 5 s before
///     the end, plus the lights the game needs to not glitch (a fog, a beam, a closing fog).
///     <para>
///         Ported from Rocksmith2014.NET (<c>ShowLightGenerator.fs</c>, MIT; see THIRD-PARTY-NOTICES.md),
///         working on the chart model instead of an SNG. Differences: fog colours are derived from the section
///         name (no <c>Random</c>), so a re-export gives the same lights; the beams read the hardest
///         difficulty level as a whole, not each phrase iteration's own maximum; solo detection ignores case;
///         the closing fog never repeats the fog before it.
///     </para>
/// </summary>
public static class RsShowlightGenerator
{
    private const double MinBeamChangeMs = 350;
    private const double LasersOnFraction = 0.6;
    private const double LasersOffBeforeEndMs = 5000;

    private static readonly IReadOnlyList<ShowlightDefinition> Fogs = ShowlightCatalog.FogDefinitions;

    // The twelve colour beams in pitch-class order (the catalogue lists "off" first in the lane).
    private static readonly IReadOnlyList<ShowlightDefinition> Beams = ShowlightCatalog.BeamDefinitions
        .Where(d => d.Type != "BEAMS_OFF").OrderBy(d => d.Note).ToArray();

    private static readonly ShowlightDefinition LasersOn = ShowlightCatalog.LaserDefinitions.First(d => d.Type == "LASERS_ON");
    private static readonly ShowlightDefinition LasersOff = ShowlightCatalog.LaserDefinitions.First(d => d.Type == "LASERS_OFF");

    /// <summary>
    ///     The generated lights, sorted by time, or an empty list when the project has no instrumental
    ///     arrangement or no beats (the caller then falls back to the minimal set).
    ///     <paramref name="songLengthMs" /> of zero or less is derived from the last beat and the last note.
    /// </summary>
    public static IReadOnlyList<Showlight> Generate(ChartProject project, double songLengthMs)
    {
        ArgumentNullException.ThrowIfNull(project);
        var source = SourceArrangement(project);
        if (source == null || project.Beats.Count == 0) return [];

        var beats = project.Beats;
        var notes = BeamSources(source, beats);
        var length = songLengthMs > 0 ? songLengthMs : Math.Max(beats[^1].Time, notes.Count > 0 ? notes[^1].Ms : 0);

        var sections = SectionStarts(source, beats);
        var lights = new List<(double Ms, ShowlightDefinition Light)>();
        lights.AddRange(FogLights(sections));
        lights.AddRange(BeamLights(notes));
        lights.AddRange(LaserLights(sections, length));

        if (!lights.Any(l => l.Light.Lane == ShowlightLane.Beams)) lights.Add((0, Beams[0]));
        if (!lights.Any(l => l.Light.Lane == ShowlightLane.Fog)) lights.Add((0, Fogs[0]));
        // The closing fog avoids a known in-game glitch; it must differ from the fog before it.
        var lastFog = lights.Where(l => l.Light.Lane == ShowlightLane.Fog).MaxBy(l => l.Ms).Light;
        lights.Add((length, lastFog == Fogs[^1] ? Fogs[0] : Fogs[^1]));

        return lights.OrderBy(l => l.Ms)
            .Select(l => new Showlight { Position = TimelineMath.PositionFromMs(l.Ms, beats), Type = l.Light.Type, Note = l.Light.Note })
            .ToArray();
    }

    private static Arrangement? SourceArrangement(ChartProject project)
        => project.Arrangements.FirstOrDefault(a => a.ArrangementType == ArrangementType.Lead)
           ?? project.Arrangements.FirstOrDefault(a => a.ArrangementType != ArrangementType.Vocal);

    private static List<(double Ms, string Name)> SectionStarts(Arrangement source, IList<BeatMarker> beats)
        => source.EventPoints.Where(e => !string.IsNullOrWhiteSpace(e.Section))
            .Select(e => (Ms: e.GetTimeMs(beats), Name: e.Section.Trim()))
            .OrderBy(e => e.Ms).ToList();

    private static IEnumerable<(double Ms, ShowlightDefinition Light)> FogLights(List<(double Ms, string Name)> sections)
    {
        var colours = FogColours(sections.Select(s => s.Name).ToList());
        var previous = (string?)null;
        foreach (var (ms, name) in sections)
        {
            if (string.Equals(name, previous, StringComparison.OrdinalIgnoreCase)) continue;
            previous = name;
            yield return (ms, Fogs[colours[name.ToLowerInvariant()]]);
        }
    }

    /// <summary>
    ///     One fog colour per distinct section name: it starts at a stable hash of the name and moves on until
    ///     it differs from every colour already given to a name next to it in the song, so equal names share a
    ///     colour and (whenever the twelve colours allow) neighbouring sections do not.
    /// </summary>
    private static Dictionary<string, int> FogColours(List<string> names)
    {
        var order = new List<string>();
        var neighbours = new Dictionary<string, HashSet<string>>();
        for (var i = 0; i < names.Count; i++)
        {
            var key = names[i].ToLowerInvariant();
            if (!neighbours.ContainsKey(key))
            {
                order.Add(key);
                neighbours[key] = [];
            }

            if (i == 0) continue;
            var before = names[i - 1].ToLowerInvariant();
            if (before == key) continue;
            neighbours[key].Add(before);
            neighbours[before].Add(key);
        }

        var colours = new Dictionary<string, int>();
        foreach (var key in order)
        {
            var taken = neighbours[key].Where(colours.ContainsKey).Select(n => colours[n]).ToHashSet();
            var colour = (int)(StableHash(key) % (uint)Fogs.Count);
            for (var tries = 0; tries < Fogs.Count && taken.Contains(colour); tries++) colour = (colour + 1) % Fogs.Count;
            colours[key] = colour;
        }

        return colours;
    }

    // FNV-1a: stable across runs and machines, unlike string.GetHashCode.
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }

    private static IEnumerable<(double Ms, ShowlightDefinition Light)> BeamLights(List<(double Ms, int Midi)> notes)
    {
        (double Ms, ShowlightDefinition Light)? last = null;
        foreach (var (ms, midi) in notes)
        {
            var beam = Beams[((midi % 12) + 12) % 12];
            if (last is { } previous && (ms - previous.Ms < MinBeamChangeMs || beam == previous.Light)) continue;
            last = (ms, beam);
            yield return last.Value;
        }
    }

    private static IEnumerable<(double Ms, ShowlightDefinition Light)> LaserLights(List<(double Ms, string Name)> sections, double length)
    {
        var solo = sections.FirstOrDefault(s => s.Name.StartsWith("solo", StringComparison.OrdinalIgnoreCase));
        var on = solo.Name != null ? solo.Ms : length * LasersOnFraction;
        var off = length - LasersOffBeforeEndMs;
        if (off <= on) yield break;
        yield return (on, LasersOn);
        yield return (off, LasersOff);
    }

    /// <summary>The sounding pitch of every note and chord of the hardest level, in time order.</summary>
    private static List<(double Ms, int Midi)> BeamSources(Arrangement source, IList<BeatMarker> beats)
    {
        var level = source.Levels.Where(l => l.Notes.Count + l.Chords.Count > 0).MaxBy(l => l.DifficultyIndex);
        if (level == null) return [];

        var result = new List<(double Ms, int Midi)>();
        foreach (var note in level.Notes)
        {
            if (SoundingPitch.MidiFor(source, note.StringIndex, note.Fret) is { } midi) result.Add((note.GetTimeMs(beats), midi));
        }

        foreach (var chord in level.Chords)
        {
            if (FirstChordPitch(source, chord) is { } midi) result.Add((chord.GetTimeMs(beats), midi));
        }

        return result.OrderBy(n => n.Ms).ToList();
    }

    // A chord uses its first sounding note (the lowest string), as DLC Builder does.
    private static int? FirstChordPitch(Arrangement source, Chord chord)
    {
        var frets = chord.ChordNotes.Count > 0
            ? chord.ChordNotes.Select(n => (n.StringIndex, n.Fret))
            : chord.TemplateId >= 0 && chord.TemplateId < source.ChordTemplates.Count
                ? source.ChordTemplates[chord.TemplateId].Frets.Select(f => (f.Key, f.Value))
                : [];
        foreach (var (stringIndex, fret) in frets.OrderBy(f => f.Item1))
        {
            if (SoundingPitch.MidiFor(source, stringIndex, fret) is { } midi) return midi;
        }

        return null;
    }
}
