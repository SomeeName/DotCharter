using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class StemMixerOffsetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter-stem-offset-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task PersistedOffsets_AreLoaded_AndChangingOnlyTimingReloadsTheSet()
    {
        var path = Path.Combine(_root, "guitar.wav");
        File.WriteAllBytes(path, [0]);
        var project = new ChartProject();
        project.Stems.Add(new Stem { Name = "guitar", Path = path, Offset = 125 });
        var audio = new RecordingMixer();
        var ui = new GatedInlineDispatcher();
        using var mixer = new StemMixerViewModel(new ProjectState { ActiveProject = project }, audio,
            FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding));

        Assert.Equal(125, Assert.Single(Assert.Single(audio.Loads)).OffsetMs);

        project.Stems[0].Offset = -50;
        mixer.Rebuild();
        await StemMixerSettle.Async(() => ui.Read(() => !mixer.IsRebuilding));

        Assert.Equal(2, audio.Loads.Count);
        Assert.Equal(-50, Assert.Single(audio.Loads[1]).OffsetMs);
    }

    private sealed class RecordingMixer : IStemMixer
    {
        public List<StemLoadRequest[]> Loads { get; } = [];
        public double SongVolume { get; set; } = 1;
        public bool HasStems => Loads.Count > 0;
        public IReadOnlyList<string> LoadedStemNames => [];
        public Task LoadStemsAsync(IReadOnlyList<StemLoadRequest> stems)
        {
            Loads.Add(stems.ToArray());
            return Task.CompletedTask;
        }
        public void ClearStems() { }
        public void SetStemVolume(string name, double linearGain) { }
        public void SetStemMute(string name, bool muted) { }
        public void SetStemSolo(string name, bool soloed) { }
        public void SetMasterMixMuted(bool muted) { }
    }
}
