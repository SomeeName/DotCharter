using DotCharter.Services.Audio.SilentTransport;
using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Session;

public sealed class SilentTransportSessionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter-silentsession-").FullName;
    private readonly FakeAudioPlaybackService _audio = new();
    private readonly ProjectState _state = new();
    private readonly SilentTransportSession _silent;

    public SilentTransportSessionTests()
    {
        var logger = new FakeAppLogger();
        var transport = new TransportViewModel(_audio, _state, new FakeConfigurationService(), FakeLocalization.KeysOnly());
        _silent = new SilentTransportSession(_audio, _state, transport, logger,
            new SilentAudioTrack(logger, Path.Combine(_root, "silence")));
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task Load_RecordsThePlannedLength_AndTouchesOnlyTheEngine()
    {
        _state.CurrentAudioFileName = "missing.ogg";

        await _silent.LoadAsync(20_000);

        Assert.Equal(30_000, _silent.LengthMs);
        Assert.Equal(["silence-30s.wav"], LoadedFileNames());
        Assert.Equal("missing.ogg", _state.CurrentAudioFileName);
    }

    [Fact]
    public async Task AFailedLoad_LeavesTheSessionOffSilence()
    {
        _audio.LoadAudioFailure = new IOException("device lost");

        await _silent.LoadAsync(20_000);

        Assert.Equal(0, _silent.LengthMs);
    }

    [Fact]
    public async Task AProjectWithNoContent_GetsNoSilentTrack()
    {
        await _silent.LoadForProjectAsync(new ChartProject());

        Assert.Empty(_audio.LoadedPaths);
        Assert.Equal(0, _silent.LengthMs);
    }

    [Fact]
    public async Task TheBeatGridDrivesGrowth_UntilStopped()
    {
        _state.ActiveProject = ProjectWithBeatsTo(20_000);
        await _silent.LoadForProjectAsync(_state.ActiveProject);
        Assert.Equal(["silence-30s.wav"], LoadedFileNames());

        ExtendBeatsTo(40_000);
        Assert.Equal("silence-45s.wav", LoadedFileNames()[^1]);

        _silent.Stop();
        var loads = _audio.LoadedPaths.Count;
        ExtendBeatsTo(120_000);

        Assert.Equal(loads, _audio.LoadedPaths.Count);
        Assert.Equal(0, _silent.LengthMs);
    }

    private static ChartProject ProjectWithBeatsTo(double endMs)
    {
        var project = new ChartProject();
        project.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
        for (var ms = 0.0; ms <= endMs; ms += 500)
        {
            project.Beats.Add(new BeatMarker { Time = ms });
        }

        return project;
    }

    private void ExtendBeatsTo(double endMs)
    {
        var last = _state.Beats.Count == 0 ? 0 : _state.Beats[^1].Time;
        for (var ms = last + 500; ms <= endMs; ms += 500)
        {
            _state.Beats.Add(new BeatMarker { Time = ms });
        }
    }

    private List<string> LoadedFileNames() => _audio.LoadedPaths.Select(p => Path.GetFileName(p)).ToList();
}
