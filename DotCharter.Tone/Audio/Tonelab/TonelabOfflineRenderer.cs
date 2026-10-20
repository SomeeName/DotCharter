using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>
///     The shipping <see cref="IGameToneRenderer" />: each tone goes to the game (<c>tone apply-file</c>), then the DI is
///     pushed through tonelab's exchange OFFLINE (<see cref="TonelabAudioRing" />) and the game's output is the render.
///     The game's own tone goes back at the end (<c>tone restore</c>).
///     <list type="bullet">
///         <item>Refused, with the code, while a live preview routes audio (<see cref="ITonelabSession.TryLeaseAudio" />):
///         never two users of the game's one tone and one exchange.</item>
///         <item>Refused for a game started with its own device (RS ASIO): its input is that device, not the exchange.</item>
///         <item>A route claimed DURING the batch marks the rest <see cref="GameRenderRefusals.Interrupted" />, and the
///         claim opens the insert as soon as the batch ends.</item>
///     </list>
///     Each render starts with <see cref="LeadIn" /> of silence (the previous tone's tails die out there, and the game's
///     own block offset falls inside it) and ends with <see cref="Tail" />; the lead-in is cut from the result.
/// </summary>
public sealed class TonelabOfflineRenderer : IGameToneRenderer
{
    private readonly ITonelabSession _session;
    private readonly IAppLogger? _logger;
    private readonly string? _fileDirectory;

    /// <summary>The app's renderer over its one session.</summary>
    public TonelabOfflineRenderer(ITonelabSession session, IAppLogger? logger)
        : this(session, logger, null)
    {
    }

    internal TonelabOfflineRenderer(ITonelabSession session, IAppLogger? logger, string? fileDirectory)
    {
        _session = session;
        _logger = logger;
        _fileDirectory = fileDirectory;
    }

    /// <summary>Silence before the take: the previous tone's reverb and delay die out and the game's latency lands here.</summary>
    public static TimeSpan LeadIn { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Silence after the take, so the take's own tail comes back.</summary>
    public static TimeSpan Tail { get; } = TimeSpan.FromMilliseconds(250);

    public int SampleRate => TonelabProtocol.SampleRate;

    public GameRenderAvailability Availability() =>
        _session.State != TonelabSessionState.Ready || _session.ProcessId is null ? new GameRenderAvailability(GameRenderRefusals.NotReady)
        : _session.IsRoutingAudio ? new GameRenderAvailability(GameRenderRefusals.LivePreview)
        : _session.GameHoldsAudioDevice ? new GameRenderAvailability(GameRenderRefusals.GameOwnDevice)
        : GameRenderAvailability.Ready;

    public async Task<IReadOnlyList<GameRenderResult>> RenderAsync(float[] di, IReadOnlyList<RsTone> tones, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(di);
        ArgumentNullException.ThrowIfNull(tones);
        if (Availability().Refusal is { } refusal) return All(tones.Count, GameRenderResult.Refused(refusal));
        using var lease = _session.TryLeaseAudio();
        if (lease is null) return All(tones.Count, GameRenderResult.Refused(GameRenderRefusals.LivePreview));
        using var ring = TonelabAudioRing.TryOpen(TonelabProtocol.SharedMemoryNameFor(_session.ProcessId!.Value), out var fault);
        if (ring is null) return All(tones.Count, GameRenderResult.Refused(GameRenderRefusals.NoAudioRing, fault));

        var input = Padded(di);
        var results = new List<GameRenderResult>(tones.Count);
        using var files = _fileDirectory is null ? new TonelabToneFiles() : new TonelabToneFiles(_fileDirectory);
        var applied = false;
        try
        {
            foreach (var tone in tones)
            {
                ct.ThrowIfCancellationRequested();
                if (lease.Interrupted)
                {
                    results.Add(GameRenderResult.Refused(GameRenderRefusals.Interrupted));
                    continue;
                }

                var answer = await _session.ApplyToneFileAsync(files.Write(RsToneJsonWriter.Write(tone)), ct).ConfigureAwait(false);
                if (!answer.IsOk)
                {
                    results.Add(GameRenderResult.Refused(GameRenderRefusals.ToneRefused, $"{answer.Outcome} {answer.Detail}".Trim()));
                    continue;
                }

                applied = true;
                // A blocking loop for as long as the game takes: its own thread, never a pool thread.
                var rendered = await Task.Factory.StartNew(() => ring.Render(input, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                    .ConfigureAwait(false);
                results.Add(rendered is null ? GameRenderResult.Refused(GameRenderRefusals.Stalled)
                    : lease.Interrupted ? GameRenderResult.Refused(GameRenderRefusals.Interrupted)
                    : new GameRenderResult(rendered[Frames(LeadIn)..], null));
            }
        }
        finally
        {
            if (applied) await _session.RestoreToneAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _logger?.Info(AppLogCategory.Audio,
            $"tonelab: rendered {results.Count(r => r.Samples is not null)}/{tones.Count} tones offline through the game.");
        return results;
    }

    private static float[] Padded(float[] di)
    {
        var lead = Frames(LeadIn);
        var padded = new float[lead + di.Length + Frames(Tail)];
        Array.Copy(di, 0, padded, lead, di.Length);
        return padded;
    }

    private static int Frames(TimeSpan span) => (int)(span.TotalSeconds * TonelabProtocol.SampleRate);

    private static List<GameRenderResult> All(int count, GameRenderResult result) => Enumerable.Repeat(result, count).ToList();
}
