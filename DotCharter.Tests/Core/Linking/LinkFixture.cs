using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Collisions;

namespace DotCharter.Tests.Core.Linking;

/// <summary>
///     A chart with ONE pattern measure placed three times (measures 1, 4 and 7) between
///     fillers that differ, on a tempo map that can drift per bar and per beat. Each copy
///     meets a different neighbourhood, so the clean-up passes treat them differently:
///     copy 1 rings into a link-next continuation and a note on the next downbeat, copy 2
///     has its first anchor a few ms AFTER the onset (the 5 ms snap moves it), copy 3 has
///     an anchor that restates the one before the run (redundant-anchor removal deletes it)
///     and nothing after it at all.
/// </summary>
internal static class LinkFixture
{
    public static readonly int[] PatternMeasures = [1, 4, 7];

    public static List<BeatMarker> Beats(int measures, int? seed, int beatsPerMeasure = 4)
    {
        var random = seed is { } s ? new Random(s) : null;
        var beats = new List<BeatMarker>();
        var time = 0.0;
        for (var m = 0; m < measures; m++)
        {
            var barBpm = random == null ? 120.0 : 50 + random.NextDouble() * 190;
            for (var b = 0; b < beatsPerMeasure; b++)
            {
                var bpm = random == null ? barBpm : barBpm * (0.92 + random.NextDouble() * 0.16);
                beats.Add(new BeatMarker { Time = time, Bpm = bpm, BeatsInMeasure = beatsPerMeasure, NoteDenominator = 4, IsMeasure = b == 0 });
                time += 60_000.0 / bpm;
            }
        }

        return beats;
    }

    public static ChartProject Project(int? seed)
    {
        var beats = Beats(9, seed);
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Name = "A5",
            Frets = new Dictionary<int, int> { [1] = 7, [2] = 9, [3] = 9 },
            Fingers = new Dictionary<int, int> { [1] = 1, [2] = 3, [3] = 4 }
        });
        var level = new ChartLevel();
        arrangement.Levels.Add(level);

        // Fillers: every one different, so a copy's neighbourhood differs.
        level.Anchors.Add(new FretAnchor(At(0, 0), 5));
        level.Notes.Add(Note(At(0, 1), 0, 3, 0.5));
        level.Notes.Add(Note(At(2, 0), 3, 4, 0.25)); // the link-next continuation of copy 1, on the downbeat
        level.Notes.Add(Note(At(3, 1.5), 4, 2, 1));
        level.Anchors.Add(new FretAnchor(At(3, 1.5), 2));
        level.Anchors.Add(new FretAnchor(At(6, 0), 5)); // copy 3's first anchor restates this one
        level.Notes.Add(Note(At(6, 2), 1, 5, 0.5));

        foreach (var m in PatternMeasures)
            AddPattern(level, m, beats, anchorLateMs: m == 4 ? 3.0 : 0.0);

        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return project;
    }

    public static void AddPattern(ChartLevel level, int measure, IList<BeatMarker> beats, double anchorLateMs = 0)
    {
        var first = At(measure, 0);
        var anchorAt = anchorLateMs > 0
            ? TimelineMath.PositionFromMs(TimelineMath.CalculateTimeMs(first, beats) + anchorLateMs, beats)
            : first;
        level.Anchors.Add(new FretAnchor(anchorAt, 5));
        level.Anchors.Add(new FretAnchor(At(measure, 2), 6));

        var vibrato = Note(first, 0, 5, 1);
        vibrato.Vibrato = true;
        level.Notes.Add(vibrato);

        var bent = Note(At(measure, 1), 1, 7, 0.5);
        bent.BendValues.Add(new BendValue(At(measure, 1.25), 1));
        bent.LeftHandFinger = 3;
        level.Notes.Add(bent);

        var chord = new Chord { Position = At(measure, 2), TemplateId = 0 };
        foreach (var (stringIndex, fret) in new[] { (1, 7), (2, 9), (3, 9) })
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret, Sustain = Beats(0.5) });
        level.Chords.Add(chord);
        level.HandShapes.Add(new HandShape { Position = At(measure, 2), EndPosition = At(measure, 3), TemplateId = 0 });

        // Held across the barline: the enforcer trims it against whatever follows the run.
        level.Notes.Add(Note(At(measure, 3), 2, 9, 2));
        // Rings into the next measure; only copy 1 has a continuation there.
        var link = Note(At(measure, 3.5), 3, 4, 0.5);
        link.LinkNext = true;
        level.Notes.Add(link);
    }

    public static AppConfig Config(MinNoteDistanceMode mode, double ms = 10, int divisor = 64) => new()
    {
        MinNoteDistanceMode = mode,
        MinNoteDistanceMs = ms,
        MinNoteDistanceDivisor = divisor
    };

    public static MeasureContentIndex Index(ChartProject project, MeasureContentOptions? options = null) =>
        MeasureContentIndex.ForHardestLevel(project.Arrangements[0], project.Beats, options);

    /// <summary>The detection rule for a chart cleaned up with <paramref name="config" />.</summary>
    public static MeasureContentIndex Index(ChartProject project, AppConfig config) =>
        Index(project, MeasureContentOptions.Detection with { Spacing = ChartSpacingEnforcer.SpacingGapOf(config) });

    public static FractionalPosition At(int measure, double beat, int beatsPerMeasure = 4) =>
        new FractionalPosition(measure * beatsPerMeasure) + Beats(beat);

    public static FractionalPosition Beats(double beats)
    {
        var whole = (int)Math.Floor(beats);
        return new FractionalPosition(whole, new Fraction((long)Math.Round((beats - whole) * 48), 48));
    }

    public static GuitarNote Note(FractionalPosition at, int stringIndex, int fret, double sustain) =>
        new() { Position = at, StringIndex = stringIndex, Fret = fret, Sustain = Beats(sustain) };
}
