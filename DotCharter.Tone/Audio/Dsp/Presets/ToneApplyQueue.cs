using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// Runs tone-preset applies OFF the gesture's thread, one at a time per co-op
/// seat, last-writer-wins.
///
/// <para><b>Why this exists.</b> The v0.48 bypass crossfade shipped natively:
/// <c>DspChain::fadeOutAndWait</c> marks a node bypassed, publishes, and then
/// SLEEPS on the calling thread until the audio thread has ramped its weight to
/// zero — bounded at <c>kFadeSeconds + 3 blocks + 5 ms</c>, i.e. ≈47 ms at
/// 512 frames / 48 kHz, and every <c>RemoveNode</c> and <c>MoveNode</c> goes
/// through it. `DcRigApplier` is a minimal-diff planner, so an eight-module
/// preset swap is on the order of sixteen of those. Driven synchronously from a
/// tone-card click that is hundreds of milliseconds of frozen UI against a
/// 150 ms budget.</para>
///
/// <para><b>Why a queue and not just <c>Task.Run</c>.</b> Two clicks in quick
/// succession must not interleave two <see cref="RsToneConversion.DcRigApplier"/>
/// plans over one chain: each plans its whole diff from a single
/// <c>GetNodes()</c> snapshot, so the second would remove nodes the first meant
/// to reuse. One worker per seat, and a request that arrives while one is
/// running REPLACES the pending one — the user asked for the newest tone, not
/// for every tone they skimmed past.</para>
///
/// <para>Throw-free: an apply that throws is logged and the pump continues, so a
/// single bad preset can never wedge the seat's queue.</para>
/// </summary>
public sealed class ToneApplyQueue
{
    private readonly IDspTonePresetService _presets;
    private readonly Action<int, DcRigApplyResult>? _applied;
    private readonly Action<Action> _runOnWorker;
    private readonly Action<Action> _postBack;
    private readonly IAppLogger? _logger;

    private readonly object _gate = new();
    private readonly Dictionary<int, DspTonePreset> _pending = [];
    private readonly HashSet<int> _running = [];

    /// <param name="presets">The preset service the apply goes through.</param>
    /// <param name="applied">Raised once per completed apply, through
    /// <paramref name="postBack"/> — the board refresh.</param>
    /// <param name="runOnWorker">How to get OFF the gesture's thread. The
    /// product passes <c>a =&gt; Task.Run(a)</c>; omitting it runs inline, which
    /// is the deterministic shape tests want and is exactly the pre-0.62
    /// behaviour.</param>
    /// <param name="postBack">How to get back to the UI thread for
    /// <paramref name="applied"/>. The product passes the dispatcher's post;
    /// omitting it invokes inline.</param>
    /// <param name="logger">Optional diagnostics logger; null disables logging.</param>
    public ToneApplyQueue(
        IDspTonePresetService presets,
        Action<int, DcRigApplyResult>? applied = null,
        Action<Action>? runOnWorker = null,
        Action<Action>? postBack = null,
        IAppLogger? logger = null)
    {
        _presets = presets;
        _applied = applied;
        _runOnWorker = runOnWorker ?? (action => action());
        _postBack = postBack ?? (action => action());
        _logger = logger;
    }

    /// <summary>How many seats currently have a worker pumping. Test seam.</summary>
    internal int RunningSeats { get { lock (_gate) return _running.Count; } }

    /// <summary>True while a tone is on its way to <paramref name="player" />'s
    /// chain — the chain is mid-diff, so a snapshot of it is no rig at all.</summary>
    public bool IsBusy(int player)
    {
        lock (_gate) return _running.Contains(player);
    }

    /// <summary>Queues <paramref name="preset"/> for <paramref name="player"/>.
    /// Returns immediately; the apply happens on the worker.</summary>
    public void ApplyToPlayer(int player, DspTonePreset preset)
    {
        if (preset is null) return;

        bool start;
        lock (_gate)
        {
            // Last writer wins: a burst of card clicks costs ONE apply of the
            // final tone, not one apply each.
            _pending[player] = preset;
            start = _running.Add(player);
        }

        // A pump is already running for this seat and will pick the newest
        // pending preset up on its next turn — never a second worker.
        if (!start) return;
        _runOnWorker(() => Pump(player));
    }

    private void Pump(int player)
    {
        while (true)
        {
            DspTonePreset? next;
            lock (_gate)
            {
                if (!_pending.Remove(player, out next))
                {
                    _running.Remove(player);
                    return;
                }
            }

            DcRigApplyResult result;
            try
            {
                result = _presets.ApplyToPlayer(player, next);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Tone apply for player {player + 1} threw: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (_applied is null) continue;
            try { _postBack(() => _applied(player, result)); }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Tone apply completion for player {player + 1} threw: {ex.Message}");
            }
        }
    }
}
