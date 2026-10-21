using DotCharter.Services.Parsing.MoverPhrases;

namespace DotCharter.Tests.Services.Parsing.MoverPhrases;

/// <summary>
///     The two spellings of a moveR phrase (DLC Builder's <c>PhraseMover.getMoveBy</c> and
///     EOF's <c>eof_is_mover_phrase</c>) and the note count both tools share.
/// </summary>
public sealed class MoverPhraseNameTests
{
    [Theory]
    [InlineData("moveR2", 2)]
    [InlineData("MOVER3", 3)]
    [InlineData("mover10", 10)]
    [InlineData("moveR 4", 4)] // NumberStyles.Integer allows the blank
    public void DlcBuilder_ReadsTheNumberAfterTheCaseInsensitivePrefix(string name, int expected)
    {
        Assert.True(MoverPhraseName.TryParseDlcBuilder(name, out var moveBy));
        Assert.Equal(expected, moveBy);
    }

    [Theory]
    [InlineData("riff moveR2")] // EOF reads it; DLC Builder wants the prefix
    [InlineData("moveL2")]
    [InlineData("verse")]
    [InlineData(null)]
    public void DlcBuilder_IsNotAMoverWithoutThePrefix(string? name)
    {
        Assert.False(MoverPhraseName.IsDlcBuilderMover(name));
        Assert.False(MoverPhraseName.TryParseDlcBuilder(name, out _));
    }

    [Theory]
    [InlineData("mover")]
    [InlineData("moveRx")]
    public void DlcBuilder_SeesAMoverWhoseNumberDoesNotParse(string name)
    {
        // DLC Builder fails the build on these ("Unable to parse value for 'moveR' phrase").
        Assert.True(MoverPhraseName.IsDlcBuilderMover(name));
        Assert.False(MoverPhraseName.TryParseDlcBuilder(name, out _));
    }

    [Theory]
    [InlineData("moveR2", 2, "")]
    [InlineData("MoVeR12", 12, "")]
    [InlineData("riff moveR3", 3, "riff")]
    [InlineData("Solo mover1", 1, "Solo")]
    public void Eof_FindsTheTokenAnywhere_AndKeepsWhatPrecedesIt(string text, int expected, string ownName)
    {
        Assert.True(MoverPhraseName.TryParseEof(text, out var moveBy, out var name));
        Assert.Equal(expected, moveBy);
        Assert.Equal(ownName, name);
    }

    [Theory]
    [InlineData("moveR")] // no digits
    [InlineData("moveR 2")] // isdigit(str[0]) fails on the blank
    [InlineData("moveR2a")] // eof_read_macro_number wants nothing but digits
    [InlineData("moveR0")] // 0 is "not a moveR phrase"
    [InlineData("verse")]
    public void Eof_RejectsWhatEofRejects(string text) =>
        Assert.False(MoverPhraseName.TryParseEof(text, out _, out _));

    [Fact]
    public void Format_IsThePlainPrefixFormBothToolsRead()
    {
        var text = MoverPhraseName.Format(2);
        Assert.Equal("moveR2", text);
        Assert.True(MoverPhraseName.TryParseDlcBuilder(text, out _));
        Assert.True(MoverPhraseName.TryParseEof(text, out _, out _));
    }

    [Fact]
    public void FallbackName_IsTheSection_NumberedPastATakenName()
    {
        Assert.Equal("chorus", MoverPhraseName.FallbackName("chorus", ["verse"]));
        Assert.Equal("chorus2", MoverPhraseName.FallbackName("chorus", ["Chorus"]));
        Assert.Equal("phrase", MoverPhraseName.FallbackName(null, []));
    }

    [Fact]
    public void Count_IsAtOrAfter_TheNoteOnTheBeatIsTheFirst()
    {
        // Utils.getTimes keeps x.Time >= startTime; EOF skips only pos < beat.
        int[] notes = [1000, 1250, 1500];
        Assert.Equal(1000, MoverNoteCount.FindTimeOfNthNoteFrom(notes, [], 1000, 1));
        Assert.Equal(1250, MoverNoteCount.FindTimeOfNthNoteFrom(notes, [], 1000, 2));
        Assert.Equal(2, MoverNoteCount.CountTo(notes, [], 1000, 1250));
    }

    [Fact]
    public void Count_MergesNotesAndChords_AndASplitChordCountsOnce()
    {
        int[] notes = [1250, 1250, 1750];
        int[] chords = [1500];
        Assert.Equal(1500, MoverNoteCount.FindTimeOfNthNoteFrom(notes, chords, 1000, 2));
        Assert.Equal(1750, MoverNoteCount.FindTimeOfNthNoteFrom(notes, chords, 1000, 3));
        Assert.Equal(3, MoverNoteCount.CountTo(notes, chords, 1000, 1750));
    }

    [Fact]
    public void Count_IsNullWhereDlcBuilderThrows_AndForATimeWithNoNote()
    {
        Assert.Null(MoverNoteCount.FindTimeOfNthNoteFrom([1250], [], 1000, 2));
        Assert.Null(MoverNoteCount.FindTimeOfNthNoteFrom([1250], [], 1000, 0));
        Assert.Null(MoverNoteCount.CountTo([1250, 1500], [], 1000, 1400));
    }
}
