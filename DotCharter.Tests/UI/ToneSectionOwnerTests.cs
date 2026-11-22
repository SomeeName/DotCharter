using DotCharter.UI.ViewModels.PlayMode.ToneSections;

namespace DotCharter.Tests.UI;

public sealed class ToneSectionOwnerTests
{
    [Theory]
    [InlineData("song.wav", "C:/cache/song.wav")]
    [InlineData("C:/project/song.wav", "D:/copy/song.wav")]
    [InlineData("song.wav", "song.flac")]
    public void Same_project_normalization_keeps_the_owner(string before, string after)
    {
        var project = new ChartProject();
        var owner = new ToneSectionOwner(project, before, null, 0);
        Assert.True(owner.SameSong(new ToneSectionOwner(project, after, null, 0)));
    }

    [Fact]
    public void Different_projects_over_the_same_audio_do_not_share_the_owner()
    {
        var owner = new ToneSectionOwner(new ChartProject(), "song.wav", null, 0);
        Assert.False(owner.SameSong(new ToneSectionOwner(new ChartProject(), "song.wav", null, 0)));
    }

    [Theory]
    [InlineData("song.dcproj", "C:/project/song.dcproj")]
    [InlineData("C:/project/song.dcproj", "c:\\PROJECT\\song.dcproj")]
    public void Reloaded_project_identity_does_not_depend_on_project_or_audio_spelling(string before, string after)
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        var state = new ProjectState { ActiveProject = project, CurrentProjectPath = before };
        var owner = new ToneSectionOwner(project, state.SongAudioPath, null, state.SongAudioRevision);
        var reloaded = ChartProjectCloner.Clone(project);
        reloaded.AudioFileName = "D:/copy/song.wav";
        state.ActiveProject = reloaded;
        state.CurrentProjectPath = after;
        Assert.True(owner.SameSong(new ToneSectionOwner(reloaded, state.SongAudioPath, null, state.SongAudioRevision)));
    }
}
