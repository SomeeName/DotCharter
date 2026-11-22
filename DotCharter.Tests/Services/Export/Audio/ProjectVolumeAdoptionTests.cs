using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>Adoption cannot overwrite changed live inputs or manual fields and raises no user-edit signal.</summary>
public sealed class ProjectVolumeAdoptionTests
{
    [Theory]
    [InlineData("manual")]
    [InlineData("preview")]
    [InlineData("audio")]
    [InlineData("chart")]
    [InlineData("project")]
    [InlineData("path")]
    [InlineData("revision")]
    public void Changed_live_inputs_reject_adoption(string change)
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        AutomaticVolumeFixture.Legacy(project.Chart, false);
        var state = new ProjectState { ActiveProject = project, CurrentProjectPath = "song.dcproj" };
        var snapshot = ChartProjectCloner.Clone(project);
        var adoption = ProjectVolumeAdoption.Capture(state, state.CurrentProjectPath, snapshot)!;
        snapshot.Chart.AudioVolume = -3;
        switch (change)
        {
            case "manual": project.Chart.AudioVolumeManual = true; break;
            case "preview": project.Chart.PreviewStartMs = 1000; break;
            case "audio": project.AudioFileName = "other.wav"; break;
            case "chart": project.Chart = ChartProjectCloner.Clone(project).Chart; break;
            case "project": state.ActiveProject = ChartProjectCloner.Clone(project); break;
            case "path": state.CurrentProjectPath = "other.dcproj"; break;
            case "revision": state.CommitSongAudio(project, project.AudioFileName); break;
        }
        var before = ProjectVolumeFields.From(state.ActiveProject!.Chart);

        Assert.False(adoption.Apply(state, snapshot));

        Assert.Equal(before, ProjectVolumeFields.From(state.ActiveProject.Chart));
    }

    [Fact]
    public void Automatic_fields_are_adopted_without_edit_signals_and_manual_fields_are_kept()
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        var before = AutomaticVolumeFixture.Legacy(project.Chart, false) with { AudioManual = true };
        before.ApplyTo(project.Chart);
        var state = new ProjectState { ActiveProject = project, CurrentProjectPath = "song.dcproj" };
        var edits = 0;
        state.ProjectChanged += () => edits++;
        var snapshot = ChartProjectCloner.Clone(project);
        var adoption = ProjectVolumeAdoption.Capture(state, state.CurrentProjectPath, snapshot)!;
        snapshot.Chart.AudioVolume = -100;
        snapshot.Chart.AudioVolumeSignature = "wrong-manual-signature";
        snapshot.Chart.PreviewVolume = -3;
        snapshot.Chart.PreviewVolumeSignature = "fresh-preview";

        Assert.True(adoption.Apply(state, snapshot));

        Assert.Equal(before.Audio, project.Chart.AudioVolume);
        Assert.Equal(before.AudioSignature, project.Chart.AudioVolumeSignature);
        Assert.True(project.Chart.AudioVolumeManual);
        Assert.Equal(-3, project.Chart.PreviewVolume);
        Assert.Equal("fresh-preview", project.Chart.PreviewVolumeSignature);
        Assert.Equal(0, edits);
    }
}
