using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// The pedalboard's live telemetry: every unit's output level, read
///     from the engine's per-node meters (<see cref="IDspMeterSource" />) onto its
///     card, and the whole chain's latency from the per-node figures the cards
///     already hold. Polled on the rig's own meter clock (<c>AmpFxRigView</c>, the
///     coalesced UI path the module meters use) and leased only while a rig host
///     is visible — there is no timer and no audio pump here.
/// </summary>
public sealed class RigBoardMetersViewModel(
    Func<IDspMeterSource?> source,
    Func<IReadOnlyList<PedalNodeViewModel>> nodes,
    Func<int> sampleRate,
    ILocalizationService localization) : ObservableObject, IDisposable
{
    private readonly HashSet<object> _surfaces = new(ReferenceEqualityComparer.Instance);
    private IDspMeterSource? _source;
    private IDisposable? _lease;
    private bool _disposed;
    private int? _chainLatencySamples;

    /// <summary>The chain's latency in samples, or null when any unit is unmeasured.</summary>
    public int? ChainLatencySamples
    {
        get => _chainLatencySamples;
        private set
        {
            if (!SetProperty(ref _chainLatencySamples, value)) return;
            OnPropertyChanged(nameof(HasChainLatency));
            OnPropertyChanged(nameof(ChainLatencyText));
        }
    }

    /// <summary>Shown only when measured: a "0 smp" chip on every clean board is noise.</summary>
    public bool HasChainLatency => _chainLatencySamples is > 0;

    /// <summary>"Chain: 2048 smp / 42.7 ms". The ms figure needs the device rate
    /// and reads 0.0 without one rather than inventing a rate.</summary>
    public string ChainLatencyText
    {
        get
        {
            if (_chainLatencySamples is not { } samples || samples <= 0) return string.Empty;
            var rate = sampleRate();
            var ms = rate > 0 ? samples * 1000.0 / rate : 0.0;
            return localization.Format("Rig_ChainLatencyLabel", samples, ms.ToString("0.0", CultureInfo.CurrentCulture));
        }
    }

    public void SetSurfaceLive(object owner, bool live)
    {
        if (_disposed) return;
        if (live) _surfaces.Add(owner);
        else _surfaces.Remove(owner);
        Poll();
    }

    /// <summary>One tick: every card's output level, then the chain latency.</summary>
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

        var cards = nodes();
        var live = _source is { IsMeteringActive: true };
        foreach (var card in cards)
        {
            if (live && _source!.TryGetNodeMeter(card.NodeId, out var meter) && float.IsFinite(meter.OutputPeak))
                card.SetBoardMeter(RigMeterMath.ToDb(meter.OutputPeak));
            else
                card.SetBoardMeter(null);
        }

        ChainLatencySamples = RigChainLatency.TotalSamples(cards.Select(c =>
            new RigLatencyUnit(c.IsSplitter, c.IsMerger, c.Branch, c.LatencySamples)));
    }

    public void Dispose()
    {
        _disposed = true;
        _surfaces.Clear();
        _lease?.Dispose();
        _lease = null;
        _source = null;
        foreach (var card in nodes()) card.SetBoardMeter(null);
    }
}

/// <summary>Linear amplitude to dBFS, shared by the rig's meters.</summary>
public static class RigMeterMath
{
    public static double ToDb(float linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;
}
