using DotCharter.Core.Domain.Vocals;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     The launch card's content rule (P1-6). Its predecessor tracked AUDIO
///     only, so after a GP import — a 1005-note chart with no audio — the
///     "open a project or drop an audio file here" hint sat over a full chart.
///     <see cref="MainWindowViewModel.ProjectHasChartContent" /> is the pure
///     half of the fix: the card shows only when there is neither chart
///     content nor audio.
/// </summary>
public sealed class EditorLaunchCardTests
{
    [Fact]
    public void AnEmptyDefaultProject_HasNoChartContent()
    {
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead Guitar" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        Assert.False(MainWindowViewModel.ProjectHasChartContent(project));
    }

    [Fact]
    public void NoProjectAtAll_HasNoChartContent() => Assert.False(MainWindowViewModel.ProjectHasChartContent(null));

    [Fact]
    public void AChartWithNotesButNoAudio_CountsAsContent_TheGpImportCase()
    {
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead Guitar" };
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote());
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        Assert.True(MainWindowViewModel.ProjectHasChartContent(project));
    }

    [Fact]
    public void AChordOnlyLevel_CountsAsContent()
    {
        var project = new ChartProject();
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        level.Chords.Add(new Chord());
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        Assert.True(MainWindowViewModel.ProjectHasChartContent(project));
    }

    /// <summary>The card shows over the untitled default project only: no
    /// audio, no chart content, and no project path.</summary>
    [Fact]
    public void TheCard_ShowsOverTheUntitledDefault_Only()
    {
        Assert.True(MainWindowViewModel.ShowsLaunchCard(0, new ChartProject(), ""));
        Assert.True(MainWindowViewModel.ShowsLaunchCard(0, null, null));
    }

    /// <summary>File > New Project has neither audio nor notes yet, but it has
    /// the path it will be saved to — and that dismisses the card. It sat over
    /// the freshly created project until 2026-09-08.</summary>
    [Fact]
    public void ANewProject_DismissesTheCard_BeforeItHasAudioOrNotes() => Assert.False(MainWindowViewModel.ShowsLaunchCard(0, new ChartProject(), @"C:\songs\ewf\project.rscp"));

    [Fact]
    public void AudioOrContent_DismissTheCard_WithoutAPath()
    {
        Assert.False(MainWindowViewModel.ShowsLaunchCard(1000, new ChartProject(), ""));

        var project = new ChartProject();
        var track = new VocalTrack();
        track.Phrases.Add(new LyricPhrase());
        project.VocalTracks.Add(track);
        Assert.False(MainWindowViewModel.ShowsLaunchCard(0, project, ""));
    }

    [Fact]
    public void AVocalsOnlyProject_CountsAsContent()
    {
        var project = new ChartProject();
        var track = new VocalTrack();
        track.Phrases.Add(new LyricPhrase());
        project.VocalTracks.Add(track);

        Assert.True(MainWindowViewModel.ProjectHasChartContent(project));
    }
}
