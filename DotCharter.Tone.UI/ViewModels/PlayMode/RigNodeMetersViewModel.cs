using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>Selected-module telemetry, leased only while a rig host is visible.</summary>
public sealed class RigNodeMetersViewModel(Func<IDspMeterSource?> source, Func<int?> selectedNode) : ObservableObject, IDisposable
{
    private readonly HashSet<object> _surfaces = new(ReferenceEqualityComparer.Instance);
    private IDspMeterSource? _source;
    private IDisposable? _lease;
    private bool _disposed;
    private bool _hasReading;
    private double _inputDb = double.NegativeInfinity, _outputDb = double.NegativeInfinity, _outputRmsDb = double.NegativeInfinity;
    public bool HasReading { get => _hasReading; private set => SetProperty(ref _hasReading, value); }
    public double InputDb { get => _inputDb; private set => SetProperty(ref _inputDb, value); }
    public double OutputDb { get => _outputDb; private set => SetProperty(ref _outputDb, value); }

    /// <summary>The output's RMS (dBFS): what the A/B level match listens to.
    /// Not bound — a peak meter is what a player reads.</summary>
    public double OutputRmsDb => _outputRmsDb;

    /// <summary>The selected unit's pre/post spectrum, hosted under these meters in
    /// every editor; null on meters that have none (the rig's OUTPUT bar).</summary>
    public RigNodeSpectrumViewModel? Spectrum { get; init; }

    public void SetSurfaceLive(object owner, bool live)
    {
        if (_disposed) return;
        if (live) _surfaces.Add(owner); else _surfaces.Remove(owner);
        Poll();
    }

    public void Poll()
    {
        if (_disposed) return;
        var next = _surfaces.Count == 0 ? null : source();
        if (!ReferenceEquals(_source, next))
        {
            _lease?.Dispose();
            _source = next;
            _lease = next?.AcquireMetering();
        }
        if (_source is { IsMeteringActive: true } && selectedNode() is { } node &&
            _source.TryGetNodeMeter(node, out var meter) &&
            float.IsFinite(meter.InputPeak) && float.IsFinite(meter.OutputPeak))
        {
            InputDb = RigMeterMath.ToDb(meter.InputPeak);
            OutputDb = RigMeterMath.ToDb(meter.OutputPeak);
            _outputRmsDb = float.IsFinite(meter.OutputRms) ? RigMeterMath.ToDb(meter.OutputRms) : double.NegativeInfinity;
            HasReading = true;
        }
        else
        {
            Clear();
        }
        Spectrum?.SyncNode();
    }

    private void Clear()
    {
        _outputRmsDb = double.NegativeInfinity;
        InputDb = OutputDb = double.NegativeInfinity;
        HasReading = false;
    }
    public void Dispose()
    {
        _disposed = true;
        _surfaces.Clear();
        _lease?.Dispose();
        _lease = null;
        _source = null;
        Clear();
    }
}
