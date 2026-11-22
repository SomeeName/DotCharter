using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.Key;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     #822: Song metadata shows the key estimated from the notes (or from the audio
///     when there are none) with its runner-up and confidence; "Use estimate" adopts it
///     as a locked key, and the lock is saved as part of the one-undo-step Save.
/// </summary>
public sealed class SongMetadataKeyEstimateTests
{
    private static readonly string[] Standard = ["E", "A", "D", "G", "B", "E"];

    private sealed class FakeAudioKey(SongKeyEstimate? answer) : ISongKeyAudioEstimator
    {
        public List<string> Asked { get; } = [];

        public Task<SongKeyEstimate?> EstimateAsync(string audioPath, CancellationToken ct)
        {
            Asked.Add(audioPath);
            return Task.FromResult(answer);
        }
    }

    private static (SongMetadataViewModel Vm, ChartData Chart, HistoryService History) Rig(
        ChartProject project, ISongKeyAudioEstimator? audio = null)
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var state = new ProjectState { ActiveProject = project };
        var vm = new SongMetadataViewModel(state, history, FakeLocalization.English(), logger,
            new InertNewProjectSources(), keyAudio: audio ?? new DotCharter.Tests.Infrastructure.FakeSongKeyAudioEstimator());
        return (vm, project.Chart, history);
    }

    [Fact]
    public void TheNotesEstimate_IsShownWithItsRunnerUpAndConfidence()
    {
        var (vm, _, _) = Rig(ProjectWith(DMajorRiff()));

        Assert.True(vm.HasKeyEstimate);
        Assert.Contains("D major", vm.KeyEstimateLabel, StringComparison.Ordinal);
        Assert.Contains("runner-up", vm.KeyEstimateLabel, StringComparison.Ordinal);
        Assert.Contains("%", vm.KeyEstimateLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void UseEstimate_AdoptsTheKeyAsALockedKey_AndSaveIsOneUndoStep()
    {
        var (vm, chart, history) = Rig(ProjectWith(DMajorRiff()));

        vm.UseKeyEstimateCommand.Execute(null);

        Assert.Equal(2, vm.KeyAccidentals);
        Assert.False(vm.KeyIsMinor);
        Assert.True(vm.KeyLocked);

        vm.SaveCommand.Execute(null);

        Assert.Equal(2, chart.KeyAccidentals);
        Assert.True(chart.KeyLocked);
        history.Undo();
        Assert.Equal(0, chart.KeyAccidentals);
        Assert.False(chart.KeyLocked);
    }

    [Fact]
    public void TheLockOpensAsSaved_AndUnlockingItSaves()
    {
        var project = ProjectWith(DMajorRiff());
        project.Chart.KeyAccidentals = -1;
        project.Chart.KeyLocked = true;
        var (vm, chart, _) = Rig(project);

        Assert.True(vm.KeyLocked);
        vm.KeyLocked = false;
        vm.SaveCommand.Execute(null);

        Assert.False(chart.KeyLocked);
        Assert.Equal(-1, chart.KeyAccidentals);
    }

    [Fact]
    public async Task WithNoNotes_TheAudioIsRead()
    {
        var project = new ChartProject { AudioFileName = "song.ogg" };
        var answer = new SongKeyEstimate(new ChordKeySignature(-3, true), new ChordKeySignature(-6, false), 0.8, 0.7);
        var audio = new FakeAudioKey(answer);
        var (vm, _, _) = Rig(project, audio);

        await vm.KeyEstimation;

        Assert.Single(audio.Asked);
        Assert.EndsWith("song.ogg", audio.Asked[0], StringComparison.Ordinal);
        Assert.True(vm.HasKeyEstimate);
        Assert.Contains("audio", vm.KeyEstimateLabel, StringComparison.Ordinal);
        Assert.Contains("C minor", vm.KeyEstimateLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNeitherNotesNorAudio_TheKeyIsUnknown()
    {
        var audio = new FakeAudioKey(null);
        var (vm, _, _) = Rig(new ChartProject(), audio);

        await vm.KeyEstimation;

        Assert.Empty(audio.Asked);
        Assert.False(vm.HasKeyEstimate);
        Assert.Contains("unknown", vm.KeyEstimateLabel, StringComparison.OrdinalIgnoreCase);
    }

    // D E F# G A B C# D on the A and D strings, the D and A held.
    private static Arrangement DMajorRiff()
    {
        var arrangement = new Arrangement { Tuning = Standard };
        var level = new ChartLevel();
        (int String, int Fret, int Beats)[] notes =
            [(2, 0, 2), (2, 2, 0), (2, 4, 0), (2, 5, 0), (1, 0, 2), (1, 2, 0), (1, 4, 0), (2, 0, 2), (1, 0, 1), (2, 4, 1)];
        var beat = 0;
        foreach (var (stringIndex, fret, beats) in notes)
        {
            level.Notes.Add(new GuitarNote
            {
                StringIndex = stringIndex, Fret = fret, Position = new FractionalPosition(beat++),
                Sustain = new FractionalPosition(beats)
            });
        }

        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static ChartProject ProjectWith(Arrangement arrangement)
    {
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        return project;
    }
}
