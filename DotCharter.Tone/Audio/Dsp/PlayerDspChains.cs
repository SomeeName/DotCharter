namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
///     The shipped <see cref="IPlayerDspChains" />: one
///     <see cref="IDspChainService" /> per native seat, created once and held for
///     the process lifetime.
///     <para>
///         <b>The catalogue is loaded ONCE, on seat 0, and shared.</b> It is
///         static native data (<c>dca_dsp_module_*</c> takes no engine), and
///         loading it per seat would walk ~57 module types × params × value
///         labels four times at startup for four identical answers.
///     </para>
/// </summary>
public sealed class PlayerDspChains : IPlayerDspChains
{
    private readonly IDspChainService[] _chains;

    public PlayerDspChains(IReadOnlyList<IDspChainService> chains)
    {
        ArgumentNullException.ThrowIfNull(chains);
        if (chains.Count == 0) throw new ArgumentException("At least one chain is required.", nameof(chains));
        _chains = chains.ToArray();
    }

    public int MaxPlayers => _chains.Length;

    public IReadOnlyList<IDspChainService> All => _chains;

    public IDspChainService For(int player) => _chains[Math.Clamp(player, 0, _chains.Length - 1)];

    /// <summary>The LIVE form: every seat gets the wrapper OBJECT, so each
    /// chain drives JuceDspChain (which pins the handle per call) rather than a
    /// bare dca_engine*. Production always uses this one.</summary>
    public void AttachEngineToAll(JuceSharp.JuceAudioEngine? engine)
    {
        // Same seat order, same reason as below.
        foreach (var chain in _chains) chain.AttachEngine(engine);
    }

    public void AttachEngineToAll(nint engineHandle)
    {
        // Seat order is deliberate and matters on ATTACH, not on detach: seat 0's
        // EngineAttached handler is what restores the single-player rig, and the
        // pedalboard binds to seat 0. Doing it first keeps the visible board
        // repopulated at the earliest possible moment.
        foreach (var chain in _chains) chain.AttachEngine(engineHandle);
    }

    /// <summary>
    ///     Builds the real per-player set over the native ABI: seat 0 loads the
    ///     catalogue and the rest reuse it.
    ///     <para>
    ///         Throws if the native DSP entry points are missing — the DI factory
    ///         catches that and falls back to <see cref="NullDspChainService" />
    ///         for every seat, exactly as the single-chain path always did.
    ///     </para>
    /// </summary>
    public static PlayerDspChains CreateNative(int seats, IAppLogger? logger = null)
    {
        var count = Math.Max(1, seats);
        var list = new List<IDspChainService>(count);
        for (var p = 0; p < count; p++)
        {
            var svc = new JuceDspChainService(logger, p);
            if (p == 0) svc.LoadCatalog(); // static native data — one load serves every seat
            list.Add(svc);
        }

        // Seats 1..N take seat 0's catalogue rather than re-reading it.
        if (list.Count > 1 && list[0] is JuceDspChainService head)
        {
            for (var p = 1; p < list.Count; p++)
            {
                if (list[p] is JuceDspChainService tail)
                    tail.ShareCatalogFrom(head);
            }
        }

        return new PlayerDspChains(list);
    }

    /// <summary>Every seat backed by the throw-free no-op service, for a build
    /// without the native DSP ABI and for headless tests.</summary>
    public static PlayerDspChains CreateNull(int seats)
    {
        var count = Math.Max(1, seats);
        var list = new List<IDspChainService>(count);
        for (var p = 0; p < count; p++) list.Add(new NullDspChainService(p));
        return new PlayerDspChains(list);
    }
}
