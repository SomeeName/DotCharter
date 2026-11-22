using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Editing.Highway;

public sealed class PickSlideMuteTests
{
    [Theory]
    [InlineData(MuteType.None)]
    [InlineData(MuteType.Full)]
    [InlineData(MuteType.Palm)]
    public void ConversionAndLegacyFlipClearEveryStringMute(MuteType mute)
    {
        using var h = new PickSlideCommandTests.Harness();
        var chord = h.Chord(4, 2);
        var note = h.Note(8, 2, 7, new FractionalPosition(2));
        note.Mute = mute;
        foreach (var n in chord.ChordNotes) n.Mute = mute;
        h.Selection.SelectedChords.Add(chord);
        h.Selection.SelectedNotes.Add(note);
        h.Run();
        Assert.Equal(MuteType.None, note.Mute);
        Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.None, n.Mute));
        note.Mute = MuteType.Full;
        foreach (var n in chord.ChordNotes) n.Mute = MuteType.Full;
        Assert.True(GpPickScrape.IsScrape(note));
        h.Run();
        Assert.Equal(MuteType.None, note.Mute);
        Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.None, n.Mute));
    }
}
