using DotCharter.Presentation.Screens.Editor;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

public sealed class TuningDisplaySurfaceTests
{
    [Fact]
    public void GutterAndShapeEditor_UseTheSameEFlatLetters()
    {
        var arrangement = new Arrangement { Tuning = ["Eb", "G#", "C#", "F#", "Bb", "Eb"] };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(-1));
        string[] expected = ["Eb", "Ab", "Db", "Gb", "Bb", "Eb"];
        Assert.Equal(expected, TimelineGutterControl.StringNamesFor(arrangement, 6));
        Assert.Equal(expected, Enumerable.Range(0, 6).Select(i => ShapeEditViewModel.StringName(arrangement, i)));
        Assert.Equal(new[] { "Eb", "G#", "C#", "F#", "Bb", "Eb" }, arrangement.Tuning);
    }

    [Fact]
    public void LibraryDescription_UsesDisplayLetters()
    {
        var item = TuningDisplayNames.Describe("Eb G# C# F# Bb Eb");
        Assert.Equal("Eb Standard (Eb Ab Db Gb Bb Eb)", item);
    }
}
