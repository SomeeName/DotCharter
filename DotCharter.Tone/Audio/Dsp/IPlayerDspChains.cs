namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
///     The set of per-player tone rigs (ABI v28). Each live-input seat is its
///     own guitar, so each seat needs its own amp.
///     <para>
///         The native engine hosts <see cref="MaxPlayers" /> independent DSP
///         chains, and this is how managed code reaches one that is not player 1.
///         <see cref="IDspChainService" /> stays registered in DI as
///         <c>For(0)</c>, so the ~500 existing call sites (pedalboard, RS in-game
///         tones, tone presets, the take re-amp) are untouched and still drive
///         exactly the chain they always did.
///     </para>
///     <para>
///         <b>Resolve a seat here; never construct a second
///         <see cref="JuceDspChainService" /> for a seat that already has one.</b>
///         Each service keeps a managed MIRROR of its native chain and never reads
///         the composition back from native, so two services on one seat would each
///         believe they knew the composition and one would be wrong from the first
///         edit made through the other.
///     </para>
/// </summary>
public interface IPlayerDspChains
{
    /// <summary>How many seats the native engine can host — the ceiling
    /// <see cref="For" /> clamps to.</summary>
    int MaxPlayers { get; }

    /// <summary>The chain for one seat. Out-of-range indices clamp into range
    /// rather than throwing: this is reached from gameplay code driven by a
    /// roster, and a stray index must not take the audio path down.</summary>
    IDspChainService For(int player);

    /// <summary>Every seat's chain, seat 0 first.</summary>
    IReadOnlyList<IDspChainService> All { get; }

    /// <summary>
    ///     Points EVERY seat's chain at a live native engine, or detaches them all
    ///     with <see cref="nint.Zero" />.
    ///     <para>
    ///         This is the multi-seat form of
    ///         <see cref="IDspChainService.AttachEngine(nint)" /> and the audio service
    ///         calls it on every engine (re)create. It matters that all seats are
    ///         detached BEFORE an engine is destroyed: <c>AttachEngine</c> writes
    ///         the handle under each chain's own gate, which is what makes the
    ///         detach a real barrier against an in-flight <c>SetParam</c> — and a
    ///         seat left attached to a freed engine is a use-after-free the moment
    ///         its pedalboard moves a knob.
    ///     </para>
    /// </summary>
    void AttachEngineToAll(nint engineHandle);

    /// <summary>The live form, mirroring
    /// <see cref="IDspChainService.AttachEngine(JuceSharp.JuceAudioEngine?)" />:
    /// every seat gets the wrapper object, not just the handle.</summary>
    void AttachEngineToAll(JuceSharp.JuceAudioEngine? engine)
    {
        foreach (var chain in All) chain.AttachEngine(engine);
    }
}
