// RsTonePlaybackCoordinator: converted tones levelled by RENDERED loudness — the per-arrangement levelling pass, the off-thread measurement, and the glide when a measurement lands under a live tone.

using System.Threading.Channels;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>
/// <b>Levelling.</b> A converted tone's trailing master gain starts as the
///     converter's ESTIMATE (the static walker plus the per-voicing trim). After a
///     prepare, the tones the arrangement plays are levelled by their RENDERED
///     loudness (<see cref="RsToneLevelling" />): a tone the session has measured
///     before is levelled at once; the rest are measured off every thread that
///     matters, and the levelled maps are published when they land.
///     <para>
///         <b>A song never waits for it.</b> The measurement starts after the prepare
///         has published (so <see cref="BeginRun" /> is never held back by it), and a
///         tone that is already on the rig when its measurement lands GLIDES to the
///         new level (<see cref="GlideDuration" />, small steps through the gain
///         module's own per-block ramp) instead of jumping. A tone switch queued
///         meanwhile always wins: a glide is only queued onto an EMPTY apply channel,
///         and a glide in progress stops at the first newer request.
///     </para>
///     <para>
///         The player's saved song-tone edits are never levelled: they are laid over
///         the levelled conversions exactly as before (<see cref="OverlaySongEdits" />).
///     </para>
/// </summary>
public sealed partial class RsTonePlaybackCoordinator
{
    /// <summary>One apply request for the worker: a tone switch, or a glide of the
    /// live tone's trailing gain to its freshly measured level.</summary>
    private readonly record struct ApplyRequest(string Key, bool Glide);

    private readonly RsToneLevelling _levelling;
    private readonly object _requestGate = new();

    // The package's UNLEVELLED conversions and each tone's Rocksmith Volume, by
    // trimmed key and name; written under _convertGate by EnsureConverted.
    private Dictionary<string, DcRigPreset> _packageConverted = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, float> _packageVolumes = new(StringComparer.OrdinalIgnoreCase);

    // The same for the editor preview's project tones.
    private Dictionary<string, DcRigPreset> _previewConverted = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, float> _previewVolumes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The trailing gain (dB) of the preset the worker last applied — where a
    /// glide starts from. Null before the first apply or for a preset with none.</summary>
    private float? _appliedGainDb;

    // ...and the output trim of its Rocksmith amp chain, which takes what the
    // trailing gain's ±24 dB cannot; glided with it.
    private float? _appliedAmpLevelDb;

    private Task _levellingTask = Task.CompletedTask;

    /// <summary>How long a live tone takes to glide to its measured level. Test seam.</summary>
    internal TimeSpan GlideDuration { get; set; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>Largest single step of a glide (dB).</summary>
    internal const float GlideStepDb = 0.5f;

    /// <summary>Test seam: completes when the measurement started by the last prepare
    /// has landed and its levelled maps are published.</summary>
    internal Task LevellingCompletion => _levellingTask;

    /// <summary>
    ///     Levels the package tones <paramref name="arrangement" /> plays from the
    ///     meter's cache, publishes them (with the song's edits laid over), and starts
    ///     measuring whatever was not cached. Runs inside the prepare, on whichever
    ///     thread that is.
    /// </summary>
    private void LevelPackageTones(Arrangement arrangement, string? songIdentity)
    {
        Dictionary<string, DcRigPreset> converted;
        Dictionary<string, float> volumes;
        List<IReadOnlyList<DcRigModule>> pending;
        lock (_convertGate)
        {
            converted = _packageConverted;
            volumes = _packageVolumes;
            _basePresets = LevelIn(converted, volumes, arrangement, out pending);
        }

        OverlaySongEdits(songIdentity);
        if (pending.Count == 0) return;

        _levellingTask = Task.Run(async () =>
        {
            await _levelling.MeasureAsync(pending, CancellationToken.None).ConfigureAwait(false);
            lock (_convertGate)
            {
                // A newer package, or a newer arrangement, owns the maps now.
                if (!ReferenceEquals(_packageConverted, converted) ||
                    !ReferenceEquals(PreparedArrangement, arrangement))
                {
                    return;
                }

                _basePresets = LevelIn(converted, volumes, arrangement, out _);
            }

            OverlaySongEdits(_preparedSongIdentity);
            _logger?.Info(AppLogCategory.Audio,
                $"RsTone: {pending.Count} tone(s) measured and levelled by rendered loudness.");
            if (_driver == RsToneDriver.SongToneEdit || (_driver == RsToneDriver.PlayModeRun && !_preparedFromProject))
            {
                _activePresets = _presets;
                _activeReports = _reports;
                RequestGlide();
            }
        });
    }

    /// <summary>The editor preview's half of <see cref="LevelPackageTones" />: the
    /// project's own tones, published as the preview maps. Called under
    /// <c>_convertGate</c>, which is also what makes the late publish below one step
    /// with its staleness check.</summary>
    private Dictionary<string, DcRigPreset> LevelPreviewTones(
        Dictionary<string, DcRigPreset> converted, Dictionary<string, float> volumes, Arrangement arrangement)
    {
        _previewConverted = converted;
        _previewVolumes = volumes;
        var levelled = LevelIn(converted, volumes, arrangement, out var pending);
        if (pending.Count == 0) return levelled;

        _levellingTask = Task.Run(async () =>
        {
            await _levelling.MeasureAsync(pending, CancellationToken.None).ConfigureAwait(false);
            lock (_convertGate)
            {
                // A newer prepare of the project's tones owns the preview maps now.
                if (!ReferenceEquals(_previewConverted, converted)) return;
                _previewPresets = LevelIn(converted, volumes, arrangement, out _);
            }

            _logger?.Info(AppLogCategory.Audio,
                $"RsTone: {pending.Count} project tone(s) measured and levelled by rendered loudness.");
            if (_driver == RsToneDriver.EditorPreview || (_driver == RsToneDriver.PlayModeRun && _preparedFromProject))
            {
                _activePresets = _previewPresets;
                _activeReports = _previewReports;
                RequestGlide();
            }
        });
        return levelled;
    }

    private Dictionary<string, DcRigPreset> LevelIn(
        Dictionary<string, DcRigPreset> converted, Dictionary<string, float> volumes, Arrangement arrangement,
        out List<IReadOnlyList<DcRigModule>> pending)
    {
        // Only the tones the arrangement plays are levelled (and rendered); every
        // other tone of the source keeps its converted estimate.
        var levelled = _levelling.Level(Scope(converted, arrangement), volumes, out pending);
        foreach (var (key, preset) in converted) levelled.TryAdd(key, preset);
        return levelled;
    }

    /// <summary>
    ///     The tones the arrangement plays — every alias of each — which is the set a
    ///     tone's intended level is RELATIVE to (a lead part's tones against each other,
    ///     never against the bass part's), and the only tones worth a render.
    /// </summary>
    private static Dictionary<string, DcRigPreset> Scope(Dictionary<string, DcRigPreset> converted, Arrangement arrangement)
    {
        var played = new HashSet<DcRigPreset>(ReferenceEqualityComparer.Instance);
        foreach (var key in ReferencedToneKeys(arrangement))
        {
            if (converted.TryGetValue(NormalizeKey(key), out var p)) played.Add(p);
        }

        return converted.Where(kv => played.Contains(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Queues a glide of the LIVE tone to its level in the current maps — but
    /// only onto an empty channel, so it can never displace a queued tone switch.</summary>
    private void RequestGlide()
    {
        var key = _activeToneKey;
        if (string.IsNullOrEmpty(key)) return;
        lock (_requestGate)
        {
            if (_applyChannel is not { } channel || channel.Reader.TryPeek(out _)) return;
            channel.Writer.TryWrite(new ApplyRequest(key, Glide: true));
        }
    }

    /// <summary>A tone switch (latest wins; may displace a queued glide).</summary>
    private void RequestApply(string key)
    {
        lock (_requestGate) _applyChannel?.Writer.TryWrite(new ApplyRequest(key, Glide: false));
    }

    /// <summary>
    ///     Steps the live tone's trailing gain from where the last apply left it to its
    ///     level in the current maps, over <see cref="GlideDuration" />, in steps of at
    ///     most <see cref="GlideStepDb" />. Each step is an ordinary apply of the same
    ///     chain with one gain value changed (one native param set), and the gain module
    ///     ramps each across a block. Stops at the first newer request.
    /// </summary>
    private async Task GlideAsync(string key, ChannelReader<ApplyRequest> reader, CancellationToken token)
    {
        if (!string.Equals(key, _activeToneKey, StringComparison.Ordinal)) return;
        if (!_activePresets.TryGetValue(NormalizeKey(key), out var target)) return;

        var to = RsToneLevelling.TrailingGainDb(target);
        var from = _appliedGainDb;
        var ampTo = RsToneLevelling.AmpLevelDb(target);
        var ampFrom = _appliedAmpLevelDb;
        var ampMove = ampTo is { } at && ampFrom is { } af ? at - af : 0f;
        if (to is not { } end || from is not { } start ||
            (MathF.Abs(end - start) < 0.05f && MathF.Abs(ampMove) < 0.05f))
        {
            ApplyToneKey(key);
            return;
        }

        var steps = Math.Clamp((int)MathF.Ceiling(MathF.Max(MathF.Abs(end - start), MathF.Abs(ampMove)) / GlideStepDb), 1, 96);
        var pause = GlideDuration / steps;
        for (var i = 1; i <= steps; i++)
        {
            if (reader.TryPeek(out _)) return; // a newer request owns the rig now
            var g = i == steps ? end : start + (end - start) * i / steps;
            var step = RsToneLevelling.WithTrailingGain(target, g);
            if (ampMove != 0f && i < steps) step = RsToneLevelling.WithAmpLevel(step, ampFrom!.Value + ampMove * i / steps);
            lock (_applyGate) _applier.Apply(i == steps ? target : step);
            _appliedGainDb = g;
            if (ampMove != 0f) _appliedAmpLevelDb = ampFrom!.Value + ampMove * i / steps;
            if (i < steps) await Task.Delay(pause, token).ConfigureAwait(false);
        }

        _logger?.Info(AppLogCategory.Audio,
            $"RsTone: '{key}' glided to its measured level ({start:+0.0;-0.0} → {end:+0.0;-0.0} dB).");
    }
}
