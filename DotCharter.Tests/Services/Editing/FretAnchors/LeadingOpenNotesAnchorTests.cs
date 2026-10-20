using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     #799: a level that opens on open strings must still get an anchor at its first sound, otherwise the export
///     validator reports notes before the first anchor.
/// </summary>
public sealed class LeadingOpenNotesAnchorTests
{
    private static GuitarNote Note(int beat, int fret) =>
        new() { Position = new FractionalPosition(beat), StringIndex = 0, Fret = fret };

    private static ChartLevel Level(params (int Beat, int Fret)[] notes)
    {
        var level = new ChartLevel();
        foreach (var (beat, fret) in notes) level.Notes.Add(Note(beat, fret));
        return level;
    }

    private static string Describe(IEnumerable<FretAnchor> anchors) =>
        string.Join(" ", anchors.Select(a => $"{a.Position.BeatId}:{a.Fret}:{a.Width}"));

    [Fact]
    public void LevelOpeningOnOpenStrings_AnchorsAtItsFirstNote()
    {
        var level = Level((4, 0), (5, 0), (6, 5), (7, 6));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        Assert.Equal(4, anchors[0].Position.BeatId);
        Assert.Equal(5, anchors[0].Fret);
        Assert.Equal(anchors[1].Width, anchors[0].Width);
        Assert.Equal(6, anchors[1].Position.BeatId);
    }

    [Fact]
    public void LevelOpeningOnAFrettedNote_IsUnchanged()
    {
        var level = Level((4, 5), (5, 0), (6, 6));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        Assert.Equal(4, anchors[0].Position.BeatId);
        Assert.Equal(5, anchors[0].Fret);
        Assert.Single(anchors);
    }

    [Fact]
    public void LeadingChordOfOpenStrings_AnchorsAtTheChord()
    {
        var level = Level((6, 7));
        level.Chords.Add(new Chord { Position = new FractionalPosition(4) });

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        Assert.Equal(4, anchors[0].Position.BeatId);
        Assert.Equal(7, anchors[0].Fret);
    }

    [Fact]
    public void RangeStart_ClampsTheLeadingAnchorToTheRange()
    {
        var level = Level((2, 0), (4, 0), (6, 7));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, new FractionalPosition(4));

        Assert.Equal(4, anchors[0].Position.BeatId);
    }

    [Fact]
    public void Capo_KeepsTheLeadingAnchorAboveTheCapo()
    {
        var level = Level((4, 0), (6, 3));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, capo: 2);

        Assert.Equal(4, anchors[0].Position.BeatId);
        Assert.True(anchors[0].Fret >= 3, Describe(anchors));
    }

    [Fact]
    public void ExportValidator_ReportsNoFhpCoverageForTheGeneratedAnchors()
    {
        var project = new ChartProject();
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = Level((4, 0), (5, 0), (6, 5), (7, 6));
        foreach (var a in new FretAnchorGenerationService().GenerateAnchors(level)) level.Anchors.Add(a);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        Assert.DoesNotContain(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyMissingFhpCoverage);
    }
}
