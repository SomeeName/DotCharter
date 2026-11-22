using DotCharter.Services.Export.Audio;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>Backups persist available derived fields without waiting for measurement or adding an edit.</summary>
public sealed partial class ProjectAutoBackupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_persists_refreshed_automatic_volumes_and_preserves_manual_overrides(bool manual)
    {
        var audio = AutomaticVolumeFixture.WriteMono(_root);
        var project = _state.ActiveProject!;
        project.AudioFileName = Path.GetFileName(audio);
        _state.CurrentAudioFileName = project.AudioFileName;
        AutomaticVolumeFixture.Legacy(project.Chart, manual);

        await BoundedWait.ForAsync(AttachmentVolumeMeasurement.MeasureAsync(project, audio, () => true, new FakeAppLogger()),
            "the completed attachment measurement before backup", TestContext.Current.CancellationToken);
        await BoundedWait.ForAsync(_backup.CreateBackupAsync(), "the volume-migrated backup", TestContext.Current.CancellationToken);

        var written = Assert.Single(_documents.Saved).Project;
        Assert.Equal(AutomaticVolumeFixture.Expected(audio, manual), ProjectVolumeFields.From(written.Chart));
        Assert.Equal(AutomaticVolumeFixture.Expected(audio, manual), Assert.Single(_documents.WrittenVolumeFields));
        Assert.Equal(ProjectVolumeFields.From(written.Chart), ProjectVolumeFields.From(project.Chart));
        Assert.Empty(_history.GetUndoSteps());
        Assert.False(_dirty.IsDirty);
        Assert.False(ProjectVolumes.Measure(project.Chart, audio, null, null));
    }

}
