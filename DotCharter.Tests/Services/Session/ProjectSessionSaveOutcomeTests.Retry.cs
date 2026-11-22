using DotCharter.Services.Export.Audio;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Session;

/// <summary>A rejected measurement is not a settled input: the next save may measure its changed source.</summary>
public sealed partial class ProjectSessionSaveOutcomeTests
{
    [Fact]
    public async Task A_source_changed_during_measurement_is_refreshed_on_a_following_save()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var h = CreateHarness(out var origin, volumeDecoder: path =>
        {
            var pcm = PcmAudioLoader.Load(path, []);
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                Assert.True(release.Wait(BoundedWait.DeadlockCap * 2), "The changed-source decoder was not released.");
            }
            return pcm;
        });
        var source = Path.ChangeExtension(origin, ".dcproj");
        var audio = AutomaticVolumeFixture.WriteMono(Path.GetDirectoryName(source)!);
        var project = new ChartProject { AudioFileName = Path.GetFileName(audio) };
        var before = AutomaticVolumeFixture.Legacy(project.Chart, false);
        new RscpChartSerializer().Save(source, project);
        await BoundedWait.ForAsync(h.Session.LoadFile(source), "loading before changing the source", TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(source), "saving while the old source decode is held", TestContext.Current.CancellationToken));
            await BoundedWait.ForAsync(started.Task, "the old source's decode", TestContext.Current.CancellationToken);
            var pcm = PcmAudioLoader.Load(audio, []);
            PcmAudioLoader.WriteWav(audio, pcm with { Samples = pcm.Samples.Select(s => (short)(s / 10)).ToArray() });
        }
        finally
        {
            release.Set();
            await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "rejecting the changed source's old result", TestContext.Current.CancellationToken);
        }
        Assert.Equal(before, ProjectVolumeFields.From(h.State.ActiveProject!.Chart));
        Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(source), "saving the changed source again", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the changed source's next companion", TestContext.Current.CancellationToken);
        Assert.Equal(AutomaticVolumeFixture.Expected(audio, false), ProjectVolumeFields.From(h.State.ActiveProject.Chart));
        Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(source), "persisting the changed-source measurement", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the persisted changed-source companion", TestContext.Current.CancellationToken);
        Assert.Equal(AutomaticVolumeFixture.Expected(audio, false), ProjectVolumeFields.From(new RscpChartSerializer().Load(source).Chart));
        Assert.Equal(2, calls);
    }
}
