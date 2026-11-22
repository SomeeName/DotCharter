using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Session;

/// <summary>Uses the production companion meter and serializers; only the expensive package encoder is stubbed.</summary>
public sealed partial class ProjectSessionSaveOutcomeTests
{
    private sealed class MeasuringCompanions : ISaveCompanionService
    {
        private readonly ISaveCompanionService _inner;
        private readonly SaveCompanionService _meter;
        private readonly bool _failAfterMeasure;
        private int _decodes;

        public MeasuringCompanions(ISaveCompanionService inner, Func<string, PcmAudio>? decode = null, bool failAfterMeasure = false)
        {
            _inner = inner;
            _failAfterMeasure = failAfterMeasure;
            _meter = new SaveCompanionService(new FakeConfigurationService(), new FakeAppLogger(),
                (_, _, _) => throw new InvalidOperationException("The meter boundary never transcodes EOF audio"), path =>
                {
                    Interlocked.Increment(ref _decodes);
                    return decode is null ? PcmAudioLoader.Load(path, []) : decode(path);
                });
        }

        public int Decodes => _decodes;

        public string? WriteCharterProject(string path, ChartProject snapshot) => _inner.WriteCharterProject(path, snapshot);
        public string? WriteEof(string path, ChartProject snapshot) => _inner.WriteEof(path, snapshot);
        public string? WriteDlcBuilderProject(string path, ChartProject snapshot, double? length) => _inner.WriteDlcBuilderProject(path, snapshot, length);

        public async Task<string?> BuildPsarcAsync(string path, ChartProject snapshot, double? length, CancellationToken cancellationToken)
        {
            var audio = SaveCompanionService.ResolveAudio(Path.GetDirectoryName(path)!, snapshot.AudioFileName);
            await _meter.VolumesForAsync(snapshot, audio);
            if (_failAfterMeasure) throw new IOException("Self-made encoder failure after a completed measurement");
            return null;
        }
    }

    [Fact]
    public async Task A_completed_measurement_is_adopted_even_if_the_later_encoder_fails()
    {
        var meter = new MeasuringCompanions(new NoOpSaveCompanionService(), failAfterMeasure: true);
        var h = CreateHarness(out var origin, meter);
        var source = Path.ChangeExtension(origin, ".dcproj");
        var audio = AutomaticVolumeFixture.WriteMono(Path.GetDirectoryName(source)!);
        var project = h.State.ActiveProject!;
        project.AudioFileName = Path.GetFileName(audio);
        h.State.CurrentAudioFileName = project.AudioFileName;
        AutomaticVolumeFixture.Legacy(project.Chart, false);

        Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(source), "saving before an encoder failure", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "adopting before the encoder failure is reported", TestContext.Current.CancellationToken);

        Assert.Equal(AutomaticVolumeFixture.Expected(audio, false), ProjectVolumeFields.From(project.Chart));
        Assert.False(h.Session.IsProjectDirty);
        Assert.Empty(h.History.GetUndoSteps());
    }

    [Fact]
    public async Task A_manual_edit_while_the_companion_measures_prevents_late_adoption()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = CreateHarness(out var origin, volumeDecoder: path =>
        {
            started.TrySetResult();
            Assert.True(release.Wait(BoundedWait.DeadlockCap * 2), "The companion decoder was not released after the manual edit.");
            return PcmAudioLoader.Load(path, []);
        });
        var source = Path.ChangeExtension(origin, ".dcproj");
        var audio = AutomaticVolumeFixture.WriteMono(Path.GetDirectoryName(source)!);
        var project = h.State.ActiveProject!;
        project.AudioFileName = Path.GetFileName(audio);
        h.State.CurrentAudioFileName = project.AudioFileName;
        var before = AutomaticVolumeFixture.Legacy(project.Chart, false);
        var manual = before with { Audio = -4, Preview = -5, AudioManual = true, PreviewManual = true };
        try
        {
            Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(source), "saving before the manual companion edit", TestContext.Current.CancellationToken));
            await BoundedWait.ForAsync(started.Task, "the companion decode before a manual edit", TestContext.Current.CancellationToken);
            h.History.Do(new SetProjectVolumesAction(project.Chart, before, manual));
        }
        finally
        {
            release.Set();
            await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the late companion result", TestContext.Current.CancellationToken);
        }
        Assert.Equal(manual, ProjectVolumeFields.From(project.Chart));
        Assert.Single(h.History.GetUndoSteps());
        Assert.True(h.Session.IsProjectDirty);
    }
}
