// Partial: spillover (ABI v37) — the recorded setting and its replay onto every engine the chain is attached to.
namespace DotCharter.Tone.Audio.Dsp;

/// <summary>What the chain service needs from native for spillover (ABI v37).</summary>
internal interface IDspSpilloverNativeInterop
{
    bool SetSpillover(bool enabled);
    bool TryGetSpillover(out bool enabled);
}

public sealed partial class JuceDspChainService
{
    // The APP default is on (RigConfig.RigSpillover); the ENGINE starts off, which
    // is exactly why the value is recorded here and replayed on every attach.
    private bool _spillover = true;

    /// <inheritdoc />
    public bool Spillover
    {
        get { lock (_gate) return _spillover; }
        set
        {
            lock (_gate)
            {
                _spillover = value;
                ReplaySpillover();
            }
        }
    }

    /// <inheritdoc />
    public bool SupportsSpillover
    {
        get
        {
            lock (_gate)
                return _native.EngineHandle != nint.Zero && _native is IDspSpilloverNativeInterop;
        }
    }

    /// <summary>
    ///     Pushes the recorded value to the attached engine. Called under
    ///     <c>_gate</c> on every set and immediately after an engine handle
    ///     changes, so a device change / NAM 48 kHz request / watchdog recovery —
    ///     each of which hands this chain a FRESH engine with spillover off —
    ///     gets it back. A refusal is logged, never thrown.
    /// </summary>
    private void ReplaySpillover()
    {
        if (_native.EngineHandle == nint.Zero || _native is not IDspSpilloverNativeInterop spill) return;
        var on = _spillover;
        if (!Safe(() => spill.SetSpillover(on), false, nameof(ReplaySpillover)))
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"DSP chain (player {Player + 1}): the engine refused spillover = {on}.");
        }
    }
}
