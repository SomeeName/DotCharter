// JuceExternalProcessInsert: IExternalProcessInsert over the native external process insert (ABI v47) of one seat, through JuceAudioPlaybackService (which replays it on every engine rebuild).
using JuceSharp;

namespace DotCharter.Tone.Audio.ExternalInsert;

/// <summary>
///     The managed face of the native external process insert for ONE seat. Generic:
///     the caller names the shared-memory block and, through
///     <c>headerMagic</c>, the 8-byte magic its header must start with
///     (null = not checked). Opening requests the ring's device rate when the device
///     runs at another one (in place, never an engine teardown); the reason a refused
///     open gives is <see cref="LastOpenResult" />.
///     <para>
///         Registered once for seat 0 (<c>App.AudioServices</c>); the tonelab session
///         opens it only while an in-game tone preview (the designer's or the editor's) routes audio
///         (<c>ITonelabSession.RouteAudio</c>).
///     </para>
/// </summary>
public sealed class JuceExternalProcessInsert : IExternalProcessInsert
{
    private readonly JuceAudioPlaybackService _service;
    private readonly string? _headerMagic;
    private readonly int _player;

    public JuceExternalProcessInsert(JuceAudioPlaybackService service, string? headerMagic = null, int player = 0)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentOutOfRangeException.ThrowIfNegative(player);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(player, Dsp.DspPlayers.MaxPlayers);
        _service = service;
        _headerMagic = headerMagic;
        _player = player;
    }

    /// <summary>
    ///     The app's insert: seat 0 exchanged with the tonelab game side (its header magic), or
    ///     <see cref="UnsupportedExternalProcessInsert" /> without the JUCE engine.
    /// </summary>
    public static IExternalProcessInsert ForTonelab(IAudioPlaybackService service) =>
        service is JuceAudioPlaybackService juce
            ? new JuceExternalProcessInsert(juce, Tonelab.TonelabProtocol.HeaderMagic)
            : UnsupportedExternalProcessInsert.Instance;

    /// <inheritdoc />
    public bool IsSupported => _service.IsExternalInsertAvailable;

    /// <inheritdoc />
    public bool IsOpen => _service.ExternalInserts.IsOpen(_player);

    /// <summary>What the last open (or the rebuild's re-open) answered: the reason to show when it was refused.</summary>
    public ExternalProcessInsertOpenResult LastOpenResult => _service.ExternalInserts.LastOpenResult(_player);

    /// <inheritdoc />
    public bool Open(ExternalInsertEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return _service.ExternalInserts.Open(_player, endpoint.SharedMemoryName, _headerMagic, endpoint.SampleRate)
               == ExternalProcessInsertOpenResult.Opened;
    }

    /// <inheritdoc />
    public void Close() => _service.ExternalInserts.Close(_player);

    /// <inheritdoc />
    public ExternalInsertStats? ReadStats()
        => ReadNativeStats() is { } s ? new ExternalInsertStats(s.LatencySamples, s.SampleRate, s.Underruns, s.Overruns) : null;

    /// <summary>Every native counter (state, connected, late blocks, …) while open, else null.</summary>
    public ExternalProcessInsertStats? ReadNativeStats() => _service.ExternalInserts.ReadStats(_player);
}
