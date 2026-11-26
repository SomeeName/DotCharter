using DotCharter.Services.Import.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Import.GuitarPro;

/// <summary>The post-import checklist (<see cref="GpImportChecklist" />): meter, slides and ties kept as Link-Next.</summary>
public sealed class GpImportChecklistTests
{
    [Fact]
    public void CountsMeterChangesSlidesAndLinkedTies()
    {
        var metadata = new GpImportMetadata();
        foreach (var (beats, unit) in new[] { (4, 4), (4, 4), (3, 4), (3, 4), (6, 8), (4, 4) })
            metadata.Bars.Add(new GpBarInfo { BeatsInMeasure = beats, NoteDenominator = unit });

        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 5, SlideTo = 7 });
        level.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 5, UnpitchedSlide = true, LinkNext = true });
        level.Notes.Add(new GuitarNote { StringIndex = 2, Fret = 3 });
        var chord = new Chord();
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, SlideTo = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, LinkNext = true });
        level.Chords.Add(chord);

        Assert.Equal(new GpImportChecklist(4, 3, 2), GpImportChecklist.Count(metadata, [arr]));
    }

    [Fact]
    public void TheTextNamesAllThreeCounts() =>
        Assert.Equal("Status_GpImport_Checklist(4, 3, 2)",
            new GpImportChecklist(4, 3, 2).Text(FakeLocalization.KeysOnly()));
}
