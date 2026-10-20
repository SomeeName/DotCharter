using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     With "waveform follows mix" on, the timeline draws the sum of the audible
///     channels at their gains. The master "Original" strip's fader is NOT one of
///     those gains.
///     <para>
///         It writes <c>IAudioPlaybackService.SongVolume</c>, and the engine applies
///         that to the original mix AND to every stem —
///         <c>ApplyStemGainsLocked</c>'s <c>UserVolume * master</c>. So it is a
///         common factor across every channel, it cancels out of the RELATIVE
///         balance a drawing shows, and feeding it in as the master channel's own
///         gain shrank the original against stems the same slider had just made
///         equally quiet. Pull the Original fader to half and the drawn mix lost
///         half the original while losing none of the stems, which is the opposite
///         of what you hear.
///     </para>
///     <para>
///         It went unnoticed because the strip's fader was seeded to a hard-coded
///         1.0 and only a deliberate drag moved it. Seeding it from the engine (so a
///         rebuild stops resetting the song level) makes the wrong gain arrive on
///         load instead.
///     </para>
/// </summary>
public sealed class StemMixerWaveformBalanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-wavebalance-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>
    ///     Original + one stem, both audible, with the song level at half. The drawn
    ///     sum must be file + stem, exactly as it is at unity.
    /// </summary>
    [Fact]
    public async Task The_song_master_does_not_shrink_the_originals_share_of_the_drawn_mix()
    {
        var mixed = await DrawnMixAsync(songVolume: 0.5);

        // file 0.4 + stem 0.2. Scaled by the song master it would be 0.2 + 0.2.
        Assert.All(mixed, v => Assert.Equal(0.6f, v, 3));
    }

    /// <summary>The same drawing at unity — the control case, so a change that
    /// simply stopped summing the original would not pass the first one.</summary>
    [Fact]
    public async Task The_drawn_mix_at_unity_is_the_same_balance()
    {
        var mixed = await DrawnMixAsync(songVolume: 1.0);

        Assert.All(mixed, v => Assert.Equal(0.6f, v, 3));
    }

    // ---- fixtures -----------------------------------------------------------

    private async Task<float[]> DrawnMixAsync(double songVolume)
    {
        var dir = Path.Combine(_root, songVolume.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(dir, "stems"));
        File.WriteAllBytes(Path.Combine(dir, "stems", "drums.wav"), [0]);

        // Both audible at once. Nothing re-applies the cross-track exclusion at
        // load, so this is a state a project really can be saved in.
        var project = new ChartProject { OriginalMixMuted = false };
        project.Stems.Add(new Stem
        {
            Name = "drums",
            Path = Path.Combine("stems", "drums.wav"),
            Local = true,
            Volume = 1.0,
            Muted = false
        });

        var file = WaveformSamples.FromBasePeaks([0.4f, 0.4f, 0.4f, 0.4f],
            WaveformSamples.GenerationBlockMs);
        var stem = WaveformSamples.FromBasePeaks([0.2f, 0.2f, 0.2f, 0.2f],
            WaveformSamples.GenerationBlockMs);

        var audio = new FakeAudioPlaybackService { SongVolume = songVolume, WaveformResult = stem };
        var ui = new GatedInlineDispatcher();
        var state = new ProjectState
        {
            CurrentProjectPath = Path.Combine(dir, "song.rscp"),
            ActiveProject = project,
            WaveformData = file
        };

        var coordinator = new StemWaveformCoordinator(audio, state);
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui, waveform: coordinator);

        await StemMixerSettle.Async(() =>
        {
            var tracks = ui.Snapshot(vm);
            return tracks.Any(t => t.IsMasterMix) && tracks.Any(t => t.Name == "drums");
        });

        ui.Read(() => vm.WaveformFollowsMix = true);
        await StemMixerSettle.Async(() => !ReferenceEquals(state.WaveformData, file));

        return state.WaveformData!.Mipmaps[0];
    }

}
