using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Infrastructure.Recovery;

/// <summary>Recovery persists available derived fields and never waits for decoding before its write lock.</summary>
public sealed partial class CrashRecoverySnapshotGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_persists_refreshed_automatic_volumes_and_preserves_manual_overrides(bool manual)
    {
        var audio = AutomaticVolumeFixture.WriteMono(_root);
        var h = new Harness(_root);
        h.OpenPsarcSong();
        h.State.CurrentProjectPath = Path.Combine(_root, "legacy.dcproj");
        var project = h.State.ActiveProject!;
        project.AudioFileName = audio;
        h.State.CurrentAudioFileName = audio;
        AutomaticVolumeFixture.Legacy(project.Chart, manual);
        await BoundedWait.ForAsync(DotCharter.Services.Session.AttachmentVolumeMeasurement.MeasureAsync(project, audio, () => true, new FakeAppLogger()),
            "the completed attachment measurement before recovery", TestContext.Current.CancellationToken);
        h.IsDirty = true;
        h.Service.NotifyProjectChanged();

        await BoundedWait.ForAsync(h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(1)),
            "the volume-migrated recovery snapshot", TestContext.Current.CancellationToken);

        var written = h.Store.LoadSnapshot(h.Store.GetSnapshotPath(h.State.CurrentProjectPath));
        Assert.Equal(AutomaticVolumeFixture.Expected(audio, manual), ProjectVolumeFields.From(written.Chart));
        Assert.Equal(ProjectVolumeFields.From(written.Chart), ProjectVolumeFields.From(project.Chart));
        Assert.False(ProjectVolumes.Measure(project.Chart, audio, null, null));
    }

}
