using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>Why a tone could not be rendered through the game: stable codes, printed in the AI tone check's provenance.</summary>
public static class GameRenderRefusals
{
    /// <summary>This build or container has no game renderer at all.</summary>
    public const string NoRenderer = "no-game-renderer";

    /// <summary>No tonelab session is ready (no game, still starting, wrong protocol version).</summary>
    public const string NotReady = "game-not-ready";

    /// <summary>A live in-game preview routes the guitar through the game: the game has one tone and one exchange.</summary>
    public const string LivePreview = "live-preview-routing";

    /// <summary>The game was started with its own audio device (RS ASIO), so its input is that device, not the exchange.</summary>
    public const string GameOwnDevice = "game-own-device";

    /// <summary>The game's shared-memory exchange is missing, malformed or has not rendered a block yet.</summary>
    public const string NoAudioRing = "no-audio-ring";

    /// <summary>The game refused the tone (<c>err …</c> to <c>tone apply-file</c>).</summary>
    public const string ToneRefused = "tone-refused";

    /// <summary>A live preview claimed the route while the render ran, so the render may have heard its tone.</summary>
    public const string Interrupted = "interrupted-by-live-preview";

    /// <summary>The game stopped answering input blocks mid-render.</summary>
    public const string Stalled = "game-stalled";
}

/// <summary>Whether the game can render now, and the refusal code when it cannot.</summary>
public sealed record GameRenderAvailability(string? Refusal)
{
    public static GameRenderAvailability Ready { get; } = new((string?)null);

    public bool CanRender => Refusal is null;
}

/// <summary>One tone's render through the game: the samples at <see cref="IGameToneRenderer.SampleRate" />, or why there are none.</summary>
public sealed record GameRenderResult(float[]? Samples, string? Refusal, string Detail = "")
{
    public static GameRenderResult Refused(string refusal, string detail = "") => new(null, refusal, detail);
}

/// <summary>
///     Renders a dry DI through the real game, one Rocksmith tone after another: the AI tone check's "real check". The
///     shipping implementation is <see cref="TonelabOfflineRenderer" /> (tonelab's shared-memory exchange, driven
///     offline); tests hand in a fake. Never runs while a live preview routes audio through the game.
/// </summary>
public interface IGameToneRenderer
{
    /// <summary>The rate the DI goes in and the render comes back at.</summary>
    int SampleRate { get; }

    /// <summary>Whether a render could run now; cheap, no I/O.</summary>
    GameRenderAvailability Availability();

    /// <summary>
    ///     Renders <paramref name="di" /> through each of <paramref name="tones" /> in turn, then puts the game's own tone
    ///     back. One result per tone, in order; a refusal for the whole batch answers that refusal for every tone.
    /// </summary>
    Task<IReadOnlyList<GameRenderResult>> RenderAsync(float[] di, IReadOnlyList<RsTone> tones, CancellationToken ct);
}
