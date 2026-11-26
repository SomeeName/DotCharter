using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A tab import never splits one chord into several chords or notes, nor one slide into pieces, at a
///     bar line or a tie: the tied halves merge back into ONE sound (<c>GpPostProcessor.MergeTiedNotes</c>,
///     shared by both importers) that keeps every string and the slide. Written end to end through the
///     binary reader, in both slide encodings (GP4's signed value, GP5's bitmask; 1 is a shift slide in both).
///     A PITCHED slide on the tie's second half used to stay a second strike (only an unpitched tail slide was
///     folded in), so a slide tied across a bar line imported as a plain note plus a sliding note.
/// </summary>
public sealed class GpImportKeepsChordsAndSlidesWholeTests
{
    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void AChordTiedAcrossABarLine_StaysOneChord(string version)
    {
        var bars = new[]
        {
            new FxMeasure().AddVoice(FxBeat.RestBeat(-1), FxBeat.RestBeat(0),
                FxBeat.Chord(0, new FxNote(1, 5), new FxNote(2, 5), new FxNote(3, 6))),
            new FxMeasure().AddVoice(
                FxBeat.Chord(-1, new FxNote(1, 5, Tied: true), new FxNote(2, 5, Tied: true), new FxNote(3, 6, Tied: true)),
                FxBeat.RestBeat(-1))
        };

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile(version, bars));

        var chord = Assert.Single(level.Chords);
        Assert.Empty(level.Notes);
        Assert.Equal(new FractionalPosition(3), chord.Position);
        Assert.Equal(3, chord.ChordNotes.Count);
        Assert.Single(level.HandShapes);
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void ASlideTiedAcrossABarLine_StaysOneSlidingNote(string version)
    {
        var bars = new[]
        {
            new FxMeasure().AddVoice(FxBeat.RestBeat(-1), FxBeat.RestBeat(0), FxBeat.Chord(0, new FxNote(2, 5))),
            new FxMeasure().AddVoice(FxBeat.Chord(0, new FxNote(2, 5, Tied: true, Slide: 1)),
                FxBeat.Chord(0, new FxNote(2, 7)), FxBeat.RestBeat(-1))
        };

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile(version, bars));

        var slide = Assert.Single(level.Notes, n => n.Fret == 5);
        Assert.Equal(new FractionalPosition(3), slide.Position);
        Assert.Equal(7, slide.SlideTo);
        Assert.Equal(new FractionalPosition(2), slide.Sustain);
        Assert.Equal(2, level.Notes.Count);
        Assert.Empty(level.Chords);
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void AChordSlidingAcrossABarLine_StaysOneChord(string version)
    {
        var bars = new[]
        {
            new FxMeasure().AddVoice(FxBeat.RestBeat(-1), FxBeat.RestBeat(0),
                FxBeat.Chord(0, new FxNote(1, 5, Slide: 1), new FxNote(2, 5, Slide: 1))),
            new FxMeasure().AddVoice(FxBeat.Chord(0, new FxNote(1, 7), new FxNote(2, 7)), FxBeat.RestBeat(-1),
                FxBeat.RestBeat(0))
        };

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile(version, bars));

        var sliding = Assert.Single(level.Chords, c => c.Position.Equals(new FractionalPosition(3)));
        Assert.Equal(2, sliding.ChordNotes.Count);
        Assert.All(sliding.ChordNotes, n => Assert.Equal(7, n.SlideTo));
        Assert.DoesNotContain(level.Notes, n => n.Position.Equals(new FractionalPosition(3)));
    }

    private static ChartLevel ImportLevel(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(Assert.Single(result.Arrangements).Levels);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
