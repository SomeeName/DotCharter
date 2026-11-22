using System.Text.Json;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #300: the golden charts and rs7chart's decoded output for them. The four charts
///     (<c>Fixtures/Rs7/golden/*.yaml</c>, rs7chart's input format, package string order) were built with
///     rs7chart (<c>feat/eight</c> for the guitars, <c>feat/bass</c> for the basses) and distilled by
///     <c>extract.py</c> into <c>Fixtures/rs7chart-golden.json</c>; <see cref="Chart" /> builds the SAME chart
///     in DotCharter's model (chart order, lowest string first).
/// </summary>
public sealed partial class Rs7ExtendedRangeGoldenTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static readonly Case[] Cases =
    [
        new("g7", ArrangementType.Lead, 7, [-2, 0, 0, 0, 0, 0, 0],
            [(4, 6, 3), (5, 0, 5), (6, 6, 0)],
            [
                (8, [(6, 2, 1), (0, 4, 3)]), (12, [(6, 0, -1), (0, 2, 1)]), (16, [(6, 2, 1), (0, 4, 3)]),
                (20, [(6, 2, 1), (0, 2, 1), (1, 4, 3)])
            ]),
        new("g8", ArrangementType.Lead, 8, [-1, -2, 0, 0, 0, 0, 0, 0],
            [(4, 7, 0), (5, 7, 3), (6, 6, 2), (7, 0, 5)],
            [
                (10, [(7, 0, -1), (6, 2, 1), (0, 2, 3)]), (14, [(7, 2, 1), (0, 2, 3)]),
                (18, [(7, 0, -1), (6, 2, 1), (0, 2, 3)]), (22, [(7, 3, 2), (6, 2, 1), (0, 2, 3)])
            ]),
        // Bass offsets are measured from the chart's reference row (E1 A1 D2 G2 B2 E3): a standard
        // B E A D G is -5 -5 -5 -5 -4, so the yaml's package tuning [0, 0, 0, 0, -2, -1] (B0 -2,
        // E A D G standard, C3 -1) reads -7 -5 -5 -5 -4 -5 here.
        new("b5", ArrangementType.Bass, 5, [-7, -5, -5, -5, -4],
            [(4, 4, 0), (5, 4, 3), (6, 0, 2), (7, 3, 5)],
            [(10, [(4, 2, 1), (0, 2, 1), (1, 4, 3)]), (14, [(4, 0, -1), (3, 5, 3)])]),
        new("b6", ArrangementType.Bass, 6, [-7, -5, -5, -5, -4, -5],
            [(4, 4, 0), (5, 5, 3), (6, 0, 2), (7, 5, 12), (8, 4, 12)],
            [
                (10, [(0, 3, 1), (1, 3, 1), (5, 5, 3)]), (14, [(4, 2, 1), (5, 3, 2)]),
                (18, [(1, 2, 1), (2, 2, 1), (3, 4, 3), (5, 7, 4)])
            ])
    ];

    public static TheoryData<string> Names => [.. Cases.Select(c => c.Name)];

    internal static Case CaseOf(string name) => Cases.Single(c => c.Name == name);

    /// <summary>The case as a DotCharter chart (chart order); the package indices of the yaml go
    /// through <see cref="Rs7StringLayout.ToDomain(int, int, ArrangementType)" />.</summary>
    internal static (Arrangement Arrangement, List<BeatMarker> Beats) Chart(Case c)
    {
        var beats = Rs7TestSupport.Beats(40);
        var arrangement = new Arrangement
        {
            ArrangementType = c.Type,
            Name = c.Type == ArrangementType.Bass ? "Bass" : "Lead",
            StringCount = c.Strings
        };
        foreach (var offset in c.DomainTuning) arrangement.TuningPitches.Add(new TuningOffset(offset));
        var level = new ChartLevel { DifficultyIndex = 0 };
        FractionalPosition At(int beat) => TimelineMath.PositionFromMs(beat * Rs7TestSupport.BeatMs, beats);
        level.Anchors.Add(new FretAnchor(At(4), 1, 4));
        foreach (var (beat, s, fret) in c.Notes)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = At(beat), StringIndex = Rs7StringLayout.ToDomain(s, c.Strings, c.Type), Fret = fret
            });
        }

        var templates = new Dictionary<string, ChordTemplate>();
        foreach (var (beat, columns) in c.Chords)
        {
            var key = string.Join(',', columns.Select(x => $"{x.String}:{x.Fret}:{x.Finger}"));
            if (!templates.TryGetValue(key, out var template))
            {
                template = new ChordTemplate { Id = templates.Count, Name = $"T{templates.Count}" };
                foreach (var (s, fret, finger) in columns)
                {
                    var d = Rs7StringLayout.ToDomain(s, c.Strings, c.Type);
                    template.Frets[d] = fret;
                    if (finger >= 0) template.Fingers[d] = finger;
                }

                templates[key] = template;
                arrangement.ChordTemplates.Add(template);
            }

            var chord = new Chord { Position = At(beat), TemplateId = template.Id };
            foreach (var (s, fret, _) in columns)
                chord.ChordNotes.Add(new ChordNote { StringIndex = Rs7StringLayout.ToDomain(s, c.Strings, c.Type), Fret = fret });
            level.Chords.Add(chord);
        }

        arrangement.Levels.Add(level);
        return (arrangement, beats);
    }

    internal static Golden LoadGolden(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "Export", "Fixtures", "rs7chart-golden.json");
        var all = JsonSerializer.Deserialize<Dictionary<string, Golden>>(File.ReadAllText(path), Json)!;
        return all[name];
    }

    /// <summary>A chart in package string order, exactly as the yaml spells it.</summary>
    internal sealed record Case(string Name, ArrangementType Type, int Strings, int[] DomainTuning,
        (int Beat, int String, int Fret)[] Notes, (int Beat, (int String, int Fret, int Finger)[] Columns)[] Chords);

    /// <summary>rs7chart's decoded output for one chart (<c>Fixtures/rs7chart-golden.json</c>).</summary>
    internal sealed record Golden(
        string Package, int[] Tuning, int StringMask, string BlockHex, List<GoldenChord> Chords, int ChordNotes,
        List<GoldenNote> Notes, Dictionary<string, int> ManifestTuning, Dictionary<string, int> HsanTuning);

    internal sealed record GoldenChord(int[] Frets, int[] Fingers, int[] Notes);

    internal sealed record GoldenNote(int String, int Fret, int ChordId, int ChordNotesId, long Mask);
}
