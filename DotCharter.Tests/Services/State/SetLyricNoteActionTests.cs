using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.State;

public sealed class SetLyricNoteActionTests
{
    [Fact]
    public void ExecuteAndUndo_SetsAndRestoresNote()
    {
        var lyric = new LyricEvent { Note = 255 };
        var action = new SetLyricNoteAction(lyric, 255, 64);

        Assert.True(action.HasEffect);
        action.Execute();
        Assert.Equal(64, lyric.Note);
        action.Undo();
        Assert.Equal(255, lyric.Note);
    }

    [Fact]
    public void HasEffect_IsFalseWhenNoteUnchanged()
    {
        var lyric = new LyricEvent { Note = 60 };
        Assert.False(new SetLyricNoteAction(lyric, 60, 60).HasEffect);
    }
}
