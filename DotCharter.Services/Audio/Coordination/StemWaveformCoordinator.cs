using System.Collections.Concurrent;
using DotCharter.Services.Audio.Waveform;
using DotCharter.Services.State;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>
///     Keeps the timeline waveform showing what the MIXER is playing.
///     <para>
///         Owner request: "a toggle in the mixer to make the waveform reflect
///         the actual mixed waveform … when stems and only vocals not muted it
///         should only show that vocals, or when mixed vocals and bass it should
///         show that mixed waveform." Charting to a stem you cannot see is the
///         problem — separating out the guitar and then still drawing the full
///         band's transients tells you nothing about where the guitar is.
///     </para>
///     <para>
///         Off, the drawn waveform is the audio file's, exactly as before. On,
///         it is the sum of the audible channels
///         (<see cref="StemWaveformMix" />). The toggle is remembered per
///         session, not per project — it is a way of looking, not chart data.
///     </para>
///     <para>
///         Per-stem waveforms are generated ONCE per path and kept for the
///         session: generating six of them costs a decode each, and the mix
///         itself is then a walk over level 0. Toggling a mute must not pay that
///         again, which is the whole reason this holds a cache rather than
///         asking the engine each time.
///     </para>
/// </summary>
public sealed class StemWaveformCoordinator
{
    private readonly IAudioPlaybackService _audio;
    private readonly ProjectState _state;
    private readonly IAppLogger? _logger;

    private readonly ConcurrentDictionary<string, WaveformSamples> _perStem = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The file waveform, kept so the toggle can go back to it without
    /// re-decoding the song.</summary>
    private WaveformSamples? _fileWaveform;

    /// <summary>
    ///     The instance this coordinator last WROTE into
    ///     <see cref="ProjectState.WaveformData" />, so it can tell its own output
    ///     apart from the song's.
    ///     <para>
    ///         Without it the file waveform was captured as
    ///         <c>_fileWaveform ??= _state.WaveformData</c>, which is only ever
    ///         right on the very first call of a session. Two failures came out of
    ///         that. A MIX gets latched as the file: <see cref="Reset" /> runs on
    ///         every rebuild — a Save As, a project-path change, any coalesced
    ///         second pass — and the value sitting in <c>ProjectState</c> at that
    ///         moment is the mix this coordinator published, so turning "waveform
    ///         follows mix" back OFF redrew the mix and the song's own waveform was
    ///         gone for the session. And a STALE one is never replaced: <c>??=</c>
    ///         latches once and the song's real waveform arrives later
    ///         (<c>ProjectSessionService</c> awaits the decode), so a rebuild that
    ///         beat the audio load pinned the PREVIOUS project's data forever.
    ///     </para>
    ///     <para>
    ///         It deliberately survives <see cref="Reset" />: what is in
    ///         <c>ProjectState</c> right now does not stop being ours because the
    ///         stem set changed, and clearing it is exactly how the mix gets
    ///         re-latched.
    ///     </para>
    /// </summary>
    private WaveformSamples? _published;

    public StemWaveformCoordinator(IAudioPlaybackService audio, ProjectState state, IAppLogger? logger = null)
    {
        _audio = audio;
        _state = state;
        _logger = logger;
    }

    /// <summary>
    ///     Drops the per-stem cache — a different song's stems are not this song's,
    ///     and the paths can repeat across projects.
    ///     <para>
    ///         <b>It deliberately does NOT drop the file waveform.</b> Dropping it is wrong,
    ///         and that is unrecoverable rather than conservative: this runs on
    ///         EVERY mixer rebuild, including one inside the same song, and what
    ///         sits in <see cref="ProjectState.WaveformData" /> at that moment is
    ///         usually the MIX this coordinator published. With the only copy of
    ///         the song's own waveform thrown away there is nothing left to put
    ///         back, so turning "waveform follows mix" off after a rebuild left the
    ///         mix on the timeline for the rest of the session. The staleness that
    ///         drop was aiming at is handled where it belongs instead — see
    ///         <see cref="_published" />: the next song replaces the file waveform
    ///         the instant it publishes one, silence included.
    ///     </para>
    /// </summary>
    public void Reset() => _perStem.Clear();

    /// <summary>
    ///     Recomputes and publishes the waveform for the given mixer state.
    ///     <paramref name="channels" /> carries a path for every real stem;
    ///     the original-mix channel has none and uses the file waveform.
    /// </summary>
    public async Task ApplyAsync(
        bool followMix, IReadOnlyList<(StemMixChannel Channel, string? Path)> channels)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-read the song's waveform every pass, and take it ONLY when it is
            // not the one we published — see the remarks on _published.
            // Silence counts: LoadSilentTransportAsync publishes
            // WaveformSamples.Empty for a project with no audio, and taking that
            // is what stops the PREVIOUS song's waveform being restored over it.
            var current = _state.WaveformData;
            if (!ReferenceEquals(current, _published)) _fileWaveform = current;

            if (!followMix || channels.Count == 0)
            {
                if (_fileWaveform is { HasData: true }) Publish(_fileWaveform);
                return;
            }

            var audible = StemWaveformMix.Audible(channels.Select(c => c.Channel).ToList());
            var byName = channels.ToDictionary(c => c.Channel.Name, c => c.Path, StringComparer.OrdinalIgnoreCase);

            var parts = new List<(WaveformSamples, double)>(audible.Count);
            foreach (var channel in audible)
            {
                // The original-mix channel IS the file, so it needs no
                // generation — and it is the one channel that always has a
                // waveform already.
                if (channel.IsMasterMix)
                {
                    if (_fileWaveform is { HasData: true }) parts.Add((_fileWaveform, channel.Gain));
                    continue;
                }

                if (!byName.TryGetValue(channel.Name, out var path) || string.IsNullOrWhiteSpace(path)) continue;
                var waveform = await WaveformForAsync(path).ConfigureAwait(false);
                if (waveform.HasData) parts.Add((waveform, channel.Gain));
            }

            var mixed = StemWaveformMix.Mix(parts);
            // Everything muted really does draw as nothing; a source that failed
            // to generate must NOT, or one unreadable stem blanks the timeline.
            if (mixed.HasData || parts.Count == 0) Publish(mixed);

            _logger?.Info(AppLogCategory.Audio,
                $"Waveform follows the mix: {parts.Count} audible channel(s) of {channels.Count}.");
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, "Mixed-waveform update failed; leaving the file waveform.", ex);
            if (_fileWaveform is { HasData: true }) Publish(_fileWaveform);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The ONE place that writes <see cref="ProjectState.WaveformData" />
    /// from here, so <see cref="_published" /> can never fall out of step with what
    /// this coordinator actually put there.</summary>
    private void Publish(WaveformSamples samples)
    {
        _published = samples;
        _state.WaveformData = samples;
    }

    private async Task<WaveformSamples> WaveformForAsync(string path)
    {
        if (_perStem.TryGetValue(path, out var cached)) return cached;

        var generated = await _audio.GenerateWaveformAsync(path).ConfigureAwait(false);
        _perStem[path] = generated;
        return generated;
    }
}
