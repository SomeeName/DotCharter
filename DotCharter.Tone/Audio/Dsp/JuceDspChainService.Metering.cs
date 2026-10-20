// Meter leases and reads share the chain's engine lifetime gate.
namespace DotCharter.Tone.Audio.Dsp;

public sealed partial class JuceDspChainService : IDspMeterSource
{
    private int _meteringOwners;
    private bool _meteringActive;

    public IDisposable AcquireMetering()
    {
        lock (_gate)
        {
            _meteringOwners++;
            if (!_meteringActive) ReplayMetering();
        }
        return new MeterLease(this);
    }
    public bool IsMeteringActive { get { lock (_gate) return _meteringActive; } }
    // Called only under _gate, including immediately after an engine handle changes.
    private void ReplayMetering()
    {
        _meteringActive = false;
        if (_meteringOwners == 0 || _native.EngineHandle == 0 || _native is not IDspMeterNativeInterop meter) return;
        _meteringActive = Safe(() => meter.SetMetering(true) && meter.GetMetering(), false, nameof(ReplayMetering));
    }
    private void ReleaseMetering()
    {
        lock (_gate)
        {
            if (_meteringOwners <= 0 || --_meteringOwners != 0) return;
            if (_native.EngineHandle != 0 && _native is IDspMeterNativeInterop meter)
                Safe(() => meter.SetMetering(false), false, nameof(ReleaseMetering));
            _meteringActive = false;
        }
    }
    public bool TryGetNodeMeter(int nodeId, out DspNodeMeter reading)
    {
        lock (_gate)
        {
            reading = default;
            if (!_meteringActive || _native.EngineHandle == 0 || _native is not IDspMeterNativeInterop meter ||
                !_nodes.Any(n => n.NodeId == nodeId))
            {
                return false;
            }

            try
            {
                if (!meter.TryGetNodeMeter(nodeId, out var result) ||
                    !float.IsFinite(result.InputPeak) || !float.IsFinite(result.InputRms) ||
                    !float.IsFinite(result.OutputPeak) || !float.IsFinite(result.OutputRms))
                {
                    return false;
                }

                reading = result;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"DSP node meter read failed: {ex.Message}");
                return false;
            }
        }
    }
    private sealed class MeterLease(JuceDspChainService owner) : IDisposable
    {
        private JuceDspChainService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseMetering();
    }
}
