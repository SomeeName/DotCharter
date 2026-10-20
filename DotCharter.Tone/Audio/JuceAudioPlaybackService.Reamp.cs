// Partial: re-amp's seam (ABI v37) — the DI recorder's positional capture read, and the per-seat injected take forwarded to ReampTakes (which also replays it onto a rebuilt engine).
using DotCharter.Tone.Audio.Reamp;
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService : IReampEngine
{
    // The takes and every engine write of them live in ReampTakes; it reads the
    // engine through the same one volatile read of _engine this partial made. The
    // rebuild replays them from AttachPerPlayerStateToEngine (_reampTakes.ReplayTakesTo).
    private readonly ReampTakes _reampTakes;

    /// <inheritdoc />
    public bool IsReampAvailable => _engine != null;

    /// <inheritdoc />
    public int ReampSampleRate => InputSampleRate;

    /// <inheritdoc />
    public bool TryGetCaptureTotal(int player, out ulong total)
        => TryGetPlayerInputCaptureSync(player, out total, out _);

    /// <inheritdoc />
    public int ReadCaptureFrom(int player, ulong from, Span<float> dest, out ulong first)
    {
        first = 0;
        var engine = _engine;
        if (engine == null || dest.IsEmpty || !Dsp.DspPlayers.IsValid(player)) return 0;
        try { return engine.ReadInputCaptureFrom(player, from, dest, out first); }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException) { return 0; }
    }

    /// <inheritdoc />
    public bool LoadTake(int player, float[] mono, int sampleRate, bool loop) => _reampTakes.LoadTake(player, mono, sampleRate, loop);

    /// <inheritdoc />
    public bool HasTake(int player) => _reampTakes.HasTake(player);

    /// <inheritdoc />
    public void ClearTake(int player) => _reampTakes.ClearTake(player);

    /// <inheritdoc />
    public bool SetTakePlaying(int player, bool playing) => _reampTakes.SetTakePlaying(player, playing);

    /// <inheritdoc />
    public bool SetTakeLoop(int player, bool loop) => _reampTakes.SetTakeLoop(player, loop);

    /// <inheritdoc />
    public bool SeekTake(int player, double seconds) => _reampTakes.SeekTake(player, seconds);

    /// <inheritdoc />
    public bool TryGetTakeStatus(int player, out ReampTransportStatus status) => _reampTakes.TryGetTakeStatus(player, out status);
}
