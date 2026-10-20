using System.Globalization;
using DotCharter.Services.Import.Audio;
using DotCharter.Tests.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     #313, the owner's option A: Add silence writes padded copies of the song and its stems, and the
///     project switches to them in ONE undo step. At the start the chart moves with the music; at the
///     end nothing moves. Undo points the project back at the untouched originals and reloads them.
/// </summary>
public sealed class AddSilenceSpliceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "addsilence-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLocalization _text = FakeLocalization.English();

    public AddSilenceSpliceTests() => Directory.CreateDirectory(Path.Combine(_dir, "stems"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>A saved project in <see cref="_dir" /> with a WAVE song, one stem, a bookmark and a note.</summary>
    private ChartPowerToolsTests.Harness SavedProject(IAudioSilenceSplicer? splicer = null)
    {
        var h = new ChartPowerToolsTests.Harness(splicer: splicer ?? new AudioSilenceSplicer(null, null, null, () => null));
        var wav = WavSilencePadderTests.Wav(100, 1, 16, [.. Enumerable.Range(0, 200).Select(i => (byte)(i + 1))]);
        File.WriteAllBytes(Path.Combine(_dir, "song.wav"), wav);
        File.WriteAllBytes(Path.Combine(_dir, "stems", "drums.wav"), wav);
        var project = h.State.ActiveProject!;
        project.AudioFileName = "song.wav";
        project.Stems.Add(new Stem { Name = "drums", Path = Path.Combine("stems", "drums.wav") });
        project.Bookmarks.Add(new Bookmark { Time = 1500 });
        project.Annotations.Add(new ChartAnnotation { TimeMs = 2500 });
        h.State.CurrentProjectPath = Path.Combine(_dir, "project.rscp");
        h.State.CurrentAudioFileName = "song.wav";
        return h;
    }

    private static string Amount(double seconds) => seconds.ToString("0.###", CultureInfo.CurrentCulture);

    [Fact]
    public async Task At_the_start_the_song_and_stems_are_padded_and_the_chart_moves_with_the_music()
    {
        var h = SavedProject();
        var project = h.State.ActiveProject!;
        var beats = project.Beats.Select(b => b.Time).ToList();
        var notes = project.Arrangements[0].Levels[0].Notes.Select(n => n.Position).ToList();

        await h.Maintenance.AddSilenceAtBeginningAsync(2);

        Assert.Equal(beats.Select(t => t + 2000), project.Beats.Select(b => b.Time));
        Assert.Equal(notes, project.Arrangements[0].Levels[0].Notes.Select(n => n.Position));
        Assert.Equal(3500, project.Bookmarks[0].Time);
        Assert.Equal(4500, project.Annotations[0].TimeMs);
        Assert.Equal(2000, project.Chart.MainAudioOffsetAdded);
        Assert.Equal("song-silence1.wav", project.AudioFileName);
        Assert.Equal("song-silence1.wav", h.State.CurrentAudioFileName);
        Assert.Equal(Path.Combine("stems", "silence-1", "drums.wav"), project.Stems[0].Path);
        Assert.True(File.Exists(Path.Combine(_dir, "stems", "silence-1", "drums.wav")));
        Assert.Equal([Path.Combine(_dir, "song-silence1.wav")], h.Session.LoadedAudio);
        Assert.Equal(_text.Format("Status_SilenceAddedAtStart", Amount(2)), h.State.MainDisplayMessage);
    }

    [Fact]
    public async Task One_undo_puts_back_the_original_audio_stems_and_grid_and_redo_reapplies_them()
    {
        var h = SavedProject();
        var project = h.State.ActiveProject!;
        var beats = project.Beats.Select(b => b.Time).ToList();

        await h.Maintenance.AddSilenceAtBeginningAsync(1.5);
        h.History.Undo();

        Assert.Equal(beats, project.Beats.Select(b => b.Time));
        Assert.Equal(1500, project.Bookmarks[0].Time);
        Assert.Equal(0, project.Chart.MainAudioOffsetAdded);
        Assert.Equal("song.wav", project.AudioFileName);
        Assert.Equal("song.wav", h.State.CurrentAudioFileName);
        Assert.Equal(Path.Combine("stems", "drums.wav"), project.Stems[0].Path);
        Assert.Equal(Path.Combine(_dir, "song.wav"), h.Session.LoadedAudio[^1]);
        Assert.False(h.History.CanUndo);

        h.History.Redo();

        Assert.Equal(beats.Select(t => t + 1500), project.Beats.Select(b => b.Time));
        Assert.Equal("song-silence1.wav", h.State.CurrentAudioFileName);
        Assert.Equal(Path.Combine(_dir, "song-silence1.wav"), h.Session.LoadedAudio[^1]);
    }

    [Fact]
    public async Task At_the_end_the_audio_is_padded_and_nothing_moves()
    {
        var h = SavedProject();
        var project = h.State.ActiveProject!;
        var beats = project.Beats.Select(b => b.Time).ToList();

        await h.Maintenance.AddSilenceAtEndAsync(3);

        Assert.Equal(beats, project.Beats.Select(b => b.Time));
        Assert.Equal(1500, project.Bookmarks[0].Time);
        Assert.Equal(0, project.Chart.MainAudioOffsetAdded);
        Assert.Equal("song-silence1.wav", project.AudioFileName);
        Assert.Equal(Path.Combine("stems", "silence-1", "drums.wav"), project.Stems[0].Path);
        Assert.True(h.History.CanUndo);
        Assert.Equal(_text.Format("Status_SilenceAddedAtEnd", Amount(3)), h.State.MainDisplayMessage);
    }

    [Fact]
    public async Task An_unsaved_session_has_nowhere_to_write_and_changes_nothing()
    {
        var h = SavedProject();
        h.State.CurrentAudioFileName = Path.Combine(_dir, "song.wav");
        h.State.CurrentProjectPath = "";
        var beats = h.State.ActiveProject!.Beats.Select(b => b.Time).ToList();

        await h.Maintenance.AddSilenceAtBeginningAsync(2);

        Assert.Equal(_text["Status_AddSilenceNeedsSavedProject"], h.State.MainDisplayMessage);
        Assert.Equal(beats, h.State.ActiveProject!.Beats.Select(b => b.Time));
        Assert.False(h.History.CanUndo);
        Assert.False(File.Exists(Path.Combine(_dir, "song-silence1.wav")));
    }

    [Fact]
    public async Task Missing_audio_is_reported_before_anything_is_written()
    {
        var h = SavedProject();
        h.State.CurrentAudioFileName = "";

        await h.Maintenance.AddSilenceAtBeginningAsync(2);

        Assert.Equal(_text["Status_ProjectAudioMissing"], h.State.MainDisplayMessage);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public async Task A_failed_splice_says_why_and_records_no_step()
    {
        File.WriteAllBytes(Path.Combine(_dir, "song.mp3"), [1, 2, 3]);
        var h = SavedProject();
        h.State.ActiveProject!.AudioFileName = "song.mp3";
        h.State.CurrentAudioFileName = "song.mp3";
        var beats = h.State.ActiveProject.Beats.Select(b => b.Time).ToList();

        await h.Maintenance.AddSilenceAtBeginningAsync(2);

        Assert.Equal(_text["Status_AddSilenceFailed_NoFfmpeg"], h.State.MainDisplayMessage);
        Assert.Equal(beats, h.State.ActiveProject.Beats.Select(b => b.Time));
        Assert.Equal("song.mp3", h.State.CurrentAudioFileName);
        Assert.False(h.History.CanUndo);
        Assert.Empty(h.Session.LoadedAudio);
    }
}
