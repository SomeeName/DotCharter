using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.Waveform;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>StemWaveformCoordinator</c> keeps the song's own waveform aside so the
///     "waveform follows mix" toggle can go back to it without re-decoding. It
///     captured that with <c>_fileWaveform ??= _state.WaveformData</c>, which is
///     only right on the very first call of a session, and TWO different wrong
///     waveforms came out of it.
///     <para>
///         <b>Its own mix, mistaken for the file.</b> <c>Reset()</c> runs on every
///         mixer rebuild — a Save As, a project-path change, any coalesced second
///         pass — and drops <c>_fileWaveform</c>. The very next pass re-latched
///         whatever sat in <c>ProjectState.WaveformData</c>, and while the toggle
///         is on that is the MIX this coordinator published. Turning the toggle
///         back off then redrew the mix, and the song's waveform was gone for the
///         rest of the session.
///     </para>
///     <para>
///         <b>A stale one, never replaced.</b> <c>??=</c> latches once. The song's
///         real waveform arrives LATER — <c>ProjectSessionService</c> awaits the
///         decode and only then assigns <c>ProjectState.WaveformData</c> — so a
///         rebuild that ran ahead of the audio load pinned the PREVIOUS project's
///         data permanently. That is the second half of the bug
///         <see cref="StemWaveformCacheLifetimeTests" /> only closed the cache side
///         of.
///     </para>
///     <para>
///         The rule both share: the file waveform is whatever the SONG published,
///         never what this coordinator wrote itself, and it is re-read every pass.
///     </para>
/// </summary>
public sealed class StemWaveformFileLatchTests
{
    /// <summary>A recognisable pyramid — the tests compare level-0 content, not
    /// references, because the mix path rebuilds through FromBasePeaks.</summary>
    private static WaveformSamples Wave(params float[] peaks) =>
        WaveformSamples.FromBasePeaks(peaks, WaveformSamples.GenerationBlockMs);

    private static IReadOnlyList<(StemMixChannel, string?)> Channels(bool masterMuted) =>
    [
        (new StemMixChannel("Original", IsMasterMix: true, Gain: 1.0, Muted: masterMuted, Soloed: false), null),
        (new StemMixChannel("drums", IsMasterMix: false, Gain: 1.0, Muted: false, Soloed: false), @"C:\stems\drums.wav")
    ];

    private static (StemWaveformCoordinator Coordinator, ProjectState State, FakeAudioPlaybackService Audio) Rig(
        WaveformSamples file, WaveformSamples stem)
    {
        var audio = new FakeAudioPlaybackService { WaveformResult = stem };
        var state = new ProjectState { WaveformData = file };
        return (new StemWaveformCoordinator(audio, state), state, audio);
    }

    /// <summary>
    ///     Follow the mix, rebuild (which resets the coordinator), then turn the
    ///     toggle off. The song's waveform has to come back — not the mix.
    /// </summary>
    [Fact]
    public async Task A_reset_between_two_mixes_never_relatches_the_mix_as_the_file_waveform()
    {
        var file = Wave(0.10f, 0.10f, 0.10f, 0.10f);
        var (coordinator, state, _) = Rig(file, Wave(0.90f, 0.90f, 0.90f, 0.90f));

        // Toggle on: the mix (the drums stem alone) is published over the file.
        await coordinator.ApplyAsync(followMix: true, Channels(masterMuted: true));
        Assert.NotSame(file, state.WaveformData);

        // A rebuild — the stem set is re-probed and the per-stem cache is dropped.
        // ProjectState still holds the MIX at this moment; that is the whole trap.
        coordinator.Reset();
        await coordinator.ApplyAsync(followMix: true, Channels(masterMuted: true));

        // Toggle off.
        await coordinator.ApplyAsync(followMix: false, Channels(masterMuted: true));

        Assert.Equal(file.Mipmaps[0], state.WaveformData!.Mipmaps[0]);
    }

    /// <summary>
    ///     The mixer rebuild can beat the audio decode, so the coordinator's first
    ///     pass sees the OUTGOING project's waveform. When the new song's own
    ///     waveform lands, that is the one the toggle must go back to.
    /// </summary>
    [Fact]
    public async Task The_songs_own_waveform_replaces_one_latched_before_the_decode_finished()
    {
        var previousSong = Wave(0.20f, 0.20f, 0.20f, 0.20f);
        var (coordinator, state, _) = Rig(previousSong, Wave(0.90f, 0.90f, 0.90f, 0.90f));

        // Project B's mixer rebuilt first; ProjectState still carries project A's.
        await coordinator.ApplyAsync(followMix: true, Channels(masterMuted: true));

        // …and now B's decode finishes and publishes B's waveform.
        var thisSong = Wave(0.40f, 0.40f, 0.40f, 0.40f);
        state.WaveformData = thisSong;

        await coordinator.ApplyAsync(followMix: false, Channels(masterMuted: true));

        Assert.Equal(thisSong.Mipmaps[0], state.WaveformData!.Mipmaps[0]);
    }

    /// <summary>
    ///     A project with no audio publishes <c>WaveformSamples.Empty</c>
    ///     (<c>LoadSilentTransportAsync</c>). Taking that as the file waveform is
    ///     what stops the previous song's being restored over an empty timeline —
    ///     so silence is a value, not a "nothing happened".
    /// </summary>
    [Fact]
    public async Task A_project_with_no_audio_does_not_get_the_previous_songs_waveform_back()
    {
        var previousSong = Wave(0.20f, 0.20f, 0.20f, 0.20f);
        var (coordinator, state, _) = Rig(previousSong, Wave(0.90f, 0.90f, 0.90f, 0.90f));

        await coordinator.ApplyAsync(followMix: false, Channels(masterMuted: true));

        // The next project has no song audio at all.
        coordinator.Reset();
        state.WaveformData = WaveformSamples.Empty;

        await coordinator.ApplyAsync(followMix: false, Channels(masterMuted: true));

        Assert.False(state.WaveformData!.HasData);
    }

    /// <summary>
    ///     The un-muted original mix contributes the FILE waveform to the sum. With
    ///     a mix latched as the file that channel fed the previous mix back into the
    ///     next one, compounding every pass — so this pins the summed case too.
    /// </summary>
    [Fact]
    public async Task The_master_channel_sums_the_file_waveform_not_the_previous_mix()
    {
        var file = Wave(0.10f, 0.10f, 0.10f, 0.10f);
        var (coordinator, state, _) = Rig(file, Wave(0.20f, 0.20f, 0.20f, 0.20f));

        await coordinator.ApplyAsync(followMix: true, Channels(masterMuted: false));
        coordinator.Reset();
        await coordinator.ApplyAsync(followMix: true, Channels(masterMuted: false));

        // file + stem = 0.30 both times. A re-latched mix would make the second
        // pass 0.30 + 0.20 = 0.50.
        Assert.All(state.WaveformData!.Mipmaps[0], v => Assert.Equal(0.30f, v, 3));
    }
}
