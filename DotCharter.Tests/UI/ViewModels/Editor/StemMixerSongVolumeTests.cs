using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The mixer's master "Original" strip has no backing <c>Stem</c>, so its fader
///     writes <c>IAudioPlaybackService.SongVolume</c> directly — and that is not a
///     private value. It is the SAME engine property the transport's Song fader, the
///     Mix popover's Song slider and Play Mode's game settings all write
///     (<c>TransportViewModel.ApplyVolumes</c>), and the one
///     <c>ApplyStemGainsLocked</c> multiplies every stem by.
///     <para>
///         The strip seeded it to a hard-coded <c>1.0</c> and
///         <c>StemTrackViewModel.ApplyToEngine</c> pushes that back on EVERY
///         rebuild, so opening a project, saving it under a new path, or any
///         coalesced second rebuild silently reset the song level to unity. Nothing
///         said so: <c>TransportViewModel._songVolume</c> is read from the engine
///         once, at construction, so the popover's slider and its read-out went on
///         printing the number the user had set while the engine played at 1.0.
///         That is the two-surfaces-one-value failure, arriving through the mixer
///         rather than through either fader.
///     </para>
/// </summary>
public sealed class StemMixerSongVolumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-songvol-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>A rebuild must leave the engine's song level exactly where the user
    /// put it.</summary>
    [Fact]
    public async Task A_rebuild_does_not_reset_the_song_volume_to_unity()
    {
        var (project, path) = Project("keep", "drums");
        var audio = new FakeAudioPlaybackService { SongVolume = 0.35 };
        var ui = new GatedInlineDispatcher();

        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => ui.Snapshot(vm).Any(t => t.IsMasterMix));

        Assert.Equal(0.35, audio.SongVolume, 3);
    }

    /// <summary>…and the strip has to SHOW that level, or the two mixer surfaces
    /// disagree about the same engine value the moment either is opened.</summary>
    [Fact]
    public async Task The_original_strip_shows_the_song_level_the_engine_is_playing_at()
    {
        var (project, path) = Project("show", "drums");
        var audio = new FakeAudioPlaybackService { SongVolume = 0.5 };
        var ui = new GatedInlineDispatcher();

        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => ui.Snapshot(vm).Any(t => t.IsMasterMix));

        var master = ui.Snapshot(vm).Single(t => t.IsMasterMix);
        Assert.Equal(0.5, master.VolumeLinear, 3);
    }

    /// <summary>
    ///     A SECOND rebuild is the one that actually bit: the first can be excused as
    ///     "the mixer opened", but project load fires <c>ActiveProject</c> and then
    ///     <c>CurrentProjectPath</c>, so every load rebuilds twice and a level set
    ///     between the two passes was thrown away.
    /// </summary>
    [Fact]
    public async Task A_level_set_between_two_rebuilds_survives_the_second()
    {
        var (project, path) = Project("second", "drums");
        var audio = new FakeAudioPlaybackService { SongVolume = 1.0 };
        var ui = new GatedInlineDispatcher();

        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => ui.Snapshot(vm).Any(t => t.IsMasterMix));

        // The user drags the Mix popover's Song fader down.
        audio.SongVolume = 0.2;

        vm.Rebuild();
        await StemMixerSettle.Async();

        Assert.Equal(0.2, audio.SongVolume, 3);
    }

    // ---- fixtures -----------------------------------------------------------

    private (ChartProject Project, string ProjectPath) Project(string folder, params string[] stemNames)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "stems"));
        var project = new ChartProject();
        foreach (var name in stemNames)
        {
            File.WriteAllBytes(Path.Combine(dir, "stems", name + ".wav"), [0]);
            project.Stems.Add(new Stem
            {
                Name = name,
                Path = Path.Combine("stems", name + ".wav"),
                Local = true,
                Volume = 1.0
            });
        }

        return (project, Path.Combine(dir, "song.rscp"));
    }

}
