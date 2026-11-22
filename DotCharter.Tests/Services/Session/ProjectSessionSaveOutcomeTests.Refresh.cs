using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Session;

/// <summary>Saving never owns a decoder, and refreshes use the same source the companions package.</summary>
public sealed partial class ProjectSessionSaveOutcomeTests
{
    [Fact]
    public async Task Save_during_a_blocked_decode_writes_old_fields_then_the_next_save_persists_the_refresh()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var h = CreateHarness(out var origin, VolumeCompanions(), volumeDecoder: path =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            Assert.True(release.Wait(BoundedWait.DeadlockCap * 2), "The background decoder was never released (deadlock).");
            return PcmAudioLoader.Load(path, []);
        });
        var source = Path.ChangeExtension(origin, ".dcproj");
        var audio = AutomaticVolumeFixture.WriteMono(Path.GetDirectoryName(source)!);
        var legacy = new ChartProject { AudioFileName = Path.GetFileName(audio) };
        legacy.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
        var before = AutomaticVolumeFixture.Legacy(legacy.Chart, false);
        new RscpChartSerializer().Save(source, legacy);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(source)!, "export", "DTRS XML"));
        await BoundedWait.ForAsync(h.Session.LoadFile(source), "loading before a blocked volume refresh", TestContext.Current.CancellationToken);
        var save = h.Session.Saver.SaveProjectCoreAsync(source);
        try
        {
            await BoundedWait.ForAsync(started.Task, "the injected decoder starting", TestContext.Current.CancellationToken);
            Assert.True(await BoundedWait.ForAsync(save, "saving without the held decoder", TestContext.Current.CancellationToken));
            Assert.Equal(before, ProjectVolumeFields.From(new RscpChartSerializer().Load(source).Chart));
            AssertVolumeCompanion(source, before);
            Assert.False(h.Session.IsProjectDirty);
        }
        finally
        {
            release.Set();
            await BoundedWait.ForAsync(save, "the released save", TestContext.Current.CancellationToken);
            await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the released background refresh", TestContext.Current.CancellationToken);
        }

        var completedCalls = calls;
        Assert.True(completedCalls > 0);
        Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(source), "persisting a completed refresh", TestContext.Current.CancellationToken));
        Assert.Equal(AutomaticVolumeFixture.Expected(audio, false), ProjectVolumeFields.From(new RscpChartSerializer().Load(source).Chart));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the refreshed companions", TestContext.Current.CancellationToken);
        AssertVolumeCompanion(source, AutomaticVolumeFixture.Expected(audio, false));
        Assert.Equal(completedCalls, calls);
        Assert.Empty(h.History.GetUndoSteps());
    }

    [Fact]
    public async Task Current_signatures_require_zero_decoder_calls_on_load_and_save()
    {
        var calls = 0;
        var h = CreateHarness(out var origin, volumeDecoder: path =>
        {
            Interlocked.Increment(ref calls);
            return PcmAudioLoader.Load(path, []);
        });
        var source = Path.ChangeExtension(origin, ".dcproj");
        var audio = AutomaticVolumeFixture.WriteMono(Path.GetDirectoryName(source)!);
        var project = new ChartProject { AudioFileName = Path.GetFileName(audio) };
        Assert.True(ProjectVolumes.Measure(project.Chart, audio, null, null));
        new RscpChartSerializer().Save(source, project);
        await BoundedWait.ForAsync(h.Session.LoadFile(source), "loading current signatures", TestContext.Current.CancellationToken);
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "checking current file identities", TestContext.Current.CancellationToken);
        Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(source), "saving current signatures", TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_declared_audio_never_measures_a_same_stem_sibling_for_persisted_volumes(bool saveAs)
    {
        var meter = new MeasuringCompanions(VolumeCompanions());
        var h = CreateHarness(out var origin, meter);
        var source = Path.ChangeExtension(origin, ".dcproj");
        var sibling = AutomaticVolumeFixture.WriteMono(Path.GetDirectoryName(source)!);
        var project = new ChartProject { AudioFileName = Path.ChangeExtension(Path.GetFileName(sibling), ".ogg") };
        project.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
        var before = AutomaticVolumeFixture.Legacy(project.Chart, false);
        new RscpChartSerializer().Save(source, project);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(source)!, "export", "DTRS XML"));
        await BoundedWait.ForAsync(h.Session.LoadFile(source), "loading a missing reference beside a same-stem sibling", TestContext.Current.CancellationToken);
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the companions after loading", TestContext.Current.CancellationToken);
        var target = saveAs ? Path.Combine(Path.GetDirectoryName(source)!, "moved", "song.dcproj") : source;
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(target)!, "export", "DTRS XML"));
        var save = saveAs ? h.Session.Saver.SaveToPickedPathAsync(target) : h.Session.Saver.SaveProjectCoreAsync(target);
        Assert.True(await BoundedWait.ForAsync(save, "saving with the declared audio missing", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the companions after the first save", TestContext.Current.CancellationToken);
        Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(target), "saving again with the declared audio missing", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the companions after the second save", TestContext.Current.CancellationToken);
        var written = new RscpChartSerializer().Load(target);
        Assert.Equal(project.AudioFileName, written.AudioFileName);
        Assert.Null(SaveCompanionService.ResolveAudio(Path.GetDirectoryName(target)!, written.AudioFileName));
        Assert.Equal(before, ProjectVolumeFields.From(written.Chart));
        Assert.Equal(0, meter.Decodes);
        foreach (var companion in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(target)!, "export"), "*.rs2dlc"))
            Assert.DoesNotContain(Path.GetFileName(sibling), File.ReadAllText(companion), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_unreadable_audio_is_attempted_at_most_once_per_save(bool missing)
    {
        var calls = 0;
        var h = CreateHarness(out var origin, volumeDecoder: _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidDataException("Unreadable self-made audio fixture");
        });
        var source = Path.ChangeExtension(origin, ".dcproj");
        var project = new ChartProject { AudioFileName = "bad.wav" };
        var before = AutomaticVolumeFixture.Legacy(project.Chart, false);
        if (!missing) File.WriteAllText(Path.Combine(Path.GetDirectoryName(source)!, project.AudioFileName), "invalid audio");
        new RscpChartSerializer().Save(source, project);
        await BoundedWait.ForAsync(h.Session.LoadFile(source), "loading unavailable audio", TestContext.Current.CancellationToken);
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the failed or unavailable refresh", TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(source), "saving despite unavailable audio", TestContext.Current.CancellationToken));
            await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the unavailable audio companion attempt", TestContext.Current.CancellationToken);
            Assert.Equal(before, ProjectVolumeFields.From(new RscpChartSerializer().Load(source).Chart));
        }
        Assert.Equal(missing ? 0 : 3, calls);
        Assert.False(h.Session.IsProjectDirty);
    }

    private static SaveCompanionService VolumeCompanions() => new(new FakeConfigurationService
    {
        AppConfig = new AppConfig { SaveCompanionEof = false, SaveCompanionEofAudio = false, SaveCompanionPsarc = false, SaveCompanionRs2Dlc = true }
    }, new FakeAppLogger());

    private static void AssertVolumeCompanion(string projectPath, ProjectVolumeFields expected)
    {
        var path = Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(projectPath)!, "export"), "*.rs2dlc"));
        using var dto = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(expected.Audio, dto.RootElement.GetProperty("AudioFile").GetProperty("Volume").GetDouble());
        Assert.Equal(expected.Preview, dto.RootElement.GetProperty("AudioPreviewFile").GetProperty("Volume").GetDouble());
    }
}
