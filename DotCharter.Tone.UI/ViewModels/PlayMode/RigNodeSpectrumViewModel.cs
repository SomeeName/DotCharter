using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Spectrum;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>What the node spectrum is showing.</summary>
public enum RigSpectrumState
{
    /// <summary>Nothing measured for the selected unit yet.</summary>
    Idle,

    /// <summary>The host has no offline renderer (the standalone rig).</summary>
    Unavailable,

    /// <summary>The selected unit sits inside a split and cannot be cut at.</summary>
    Unsupported,

    Measuring,
    Ready,

    /// <summary>The offline render failed.</summary>
    Failed
}

/// <summary>
/// The selected unit's pre/post spectrum. The engine reports no
///     spectrum per node, and the owner's rule is no new native DSP, so the unit
///     is heard OFFLINE: the live chain is snapshotted, cut short before and after
///     the unit (<see cref="NodeSpectrumChain" />), and the calibration phrase
///     (<see cref="CalibrationPhrase" />) is rendered through each cut on an engine
///     of its own (<see cref="IOfflineToneRenderer" />). Both taps are measured on
///     one press, so flipping PRE / POST is instant: <see cref="Tap" /> only picks
///     which curve is bright.
///     <para>
///         It is the unit's effect on a REFERENCE phrase, not on the live input:
///         a spectrum of the playing signal needs a native per-node tap, which is
///         the native change this slice was not allowed to make.
///     </para>
/// </summary>
public sealed partial class RigNodeSpectrumViewModel(
    Func<IOfflineToneRenderer?> renderer,
    Func<IDspChainService> chain,
    Func<int?> selectedNode) : ObservableObject
{
    /// <summary>Bands across 20 Hz … 20 kHz: a third of an octave, roughly.</summary>
    public const int BandCount = 30;

    private double[]? _pre;
    private double[]? _post;
    private int? _measuredNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPre), nameof(IsPost), nameof(Bands), nameof(ReferenceBands))]
    private NodeSpectrumTap _tap = NodeSpectrumTap.Post;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeasuring), nameof(IsReady), nameof(IsUnsupported), nameof(IsFailed),
        nameof(Bands), nameof(ReferenceBands))]
    [NotifyCanExecuteChangedFor(nameof(MeasureCommand))]
    private RigSpectrumState _state = RigSpectrumState.Idle;

    public bool IsPre => Tap == NodeSpectrumTap.Pre;
    public bool IsPost => Tap == NodeSpectrumTap.Post;
    public bool IsAvailable => renderer() is not null;
    public bool IsUnavailable => !IsAvailable;
    public bool IsMeasuring => State == RigSpectrumState.Measuring;
    public bool IsReady => State == RigSpectrumState.Ready;
    public bool IsUnsupported => State == RigSpectrumState.Unsupported;
    public bool IsFailed => State == RigSpectrumState.Failed;

    /// <summary>The picked tap's curve (bright), or null before a measurement.</summary>
    public double[]? Bands => IsReady ? (IsPre ? _pre : _post) : null;

    /// <summary>The other tap's curve (faint).</summary>
    public double[]? ReferenceBands => IsReady ? (IsPre ? _post : _pre) : null;

    [RelayCommand]
    private void SelectTap(string? tap)
    {
        if (Enum.TryParse<NodeSpectrumTap>(tap, ignoreCase: true, out var wanted)) Tap = wanted;
    }

    /// <summary>Drops a measurement that belongs to another unit than the one
    /// selected now. Called on the rig's meter clock, so a re-selection clears the
    /// plot within a tick instead of showing the previous unit's curve.</summary>
    public void SyncNode()
    {
        if (State is RigSpectrumState.Idle or RigSpectrumState.Measuring || selectedNode() == _measuredNode) return;
        _pre = _post = null;
        _measuredNode = null;
        State = RigSpectrumState.Idle;
    }

    private bool CanMeasure() => State != RigSpectrumState.Measuring && IsAvailable;

    /// <summary>Renders the phrase through both cuts and folds each into bands.</summary>
    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private async Task MeasureAsync()
    {
        var render = renderer();
        if (render is null || selectedNode() is not { } nodeId)
        {
            State = render is null ? RigSpectrumState.Unavailable : RigSpectrumState.Idle;
            return;
        }

        var dsp = chain();
        var nodes = dsp.GetNodes();
        var modules = DcRigSnapshot.Modules(nodes);
        var index = -1;
        for (var i = 0; i < nodes.Count; i++)
            if (nodes[i]?.NodeId == nodeId) index = i;
        var pre = NodeSpectrumChain.Select(modules, index, NodeSpectrumTap.Pre);
        var post = NodeSpectrumChain.Select(modules, index, NodeSpectrumTap.Post);
        _measuredNode = nodeId;
        if (pre is null || post is null)
        {
            _pre = _post = null;
            State = RigSpectrumState.Unsupported;
            return;
        }

        State = RigSpectrumState.Measuring;
        var result = await Task.Run(() => (Pre: Render(render, pre), Post: Render(render, post)));
        if (_measuredNode != nodeId) return;
        _pre = result.Pre;
        _post = result.Post;
        State = _pre is null || _post is null ? RigSpectrumState.Failed : RigSpectrumState.Ready;
    }

    /// <summary>One cut's spectrum. An empty cut is the dry phrase itself (the first
    /// unit's PRE) and needs no render. The cut carries NO scenes: the snapshot's
    /// module values ARE the live ones, and a scene re-applied onto a shortened
    /// chain would match the wrong units.</summary>
    internal static double[]? Render(IOfflineToneRenderer renderer, IReadOnlyList<DcRigModule> cut)
    {
        var left = CalibrationPhrase.Samples.ToArray();
        var right = CalibrationPhrase.Samples.ToArray();
        if (cut.Count > 0 && !renderer.TryRender(new DcRigPreset(cut), left, right, CalibrationPhrase.SampleRate))
            return null;
        return MagnitudeSpectrum.Compute(left, CalibrationPhrase.SampleRate, BandCount);
    }
}
