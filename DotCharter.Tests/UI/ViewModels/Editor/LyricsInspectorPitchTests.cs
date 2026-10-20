using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class LyricsInspectorPitchTests
{
    [Theory]
    [InlineData(60, "C4")]
    [InlineData(69, "A4")]
    [InlineData(61, "C#4")]
    [InlineData(48, "C3")]
    [InlineData(0, "C-1")]
    [InlineData(127, "G9")]
    public void MidiToNoteName_FormatsScientificPitch(int midi, string expected)
        => Assert.Equal(expected, LyricsInspectorViewModel.MidiToNoteName(midi));
}
