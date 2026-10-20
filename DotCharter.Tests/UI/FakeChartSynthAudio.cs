using DotCharter.Tone.Audio.ChartSynth;

namespace DotCharter.Tests.UI;

/// <summary>
///     An audio service WITH a chart synth (<see cref="IChartSynthEngine" />) for
///     view-model and headless view tests: it records every write, and its
///     instrument-model table has the native table's shape (#50) — which models,
///     which pickups, which have a coil split, which positions are recorded.
/// </summary>
internal sealed class FakeChartSynthAudio : FakeAudioPlaybackService, IChartSynthEngine
{
    public readonly List<bool> EnabledWrites = [];
    public readonly List<bool> RouteWrites = [];
    public bool Sandboxed { get; set; }
    private bool _route;

    /// <summary>An engine is up (#574); flip it with <see cref="SetEngineUp" />.</summary>
    public bool IsChartSynthAvailable { get; private set; } = true;

    public event EventHandler? ChartSynthAvailabilityChanged;

    /// <summary>What an engine (re)build does: the availability moves, then the event.</summary>
    public void SetEngineUp(bool up)
    {
        IsChartSynthAvailable = up;
        ChartSynthAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether the voice packs are installed (#574); false reports every model missing.</summary>
    public bool PacksInstalled { get; set; } = true;

    public bool IsChartSynthEnabled { get; private set; }

    public void SetChartSynthEnabled(bool enabled)
    {
        EnabledWrites.Add(enabled);
        IsChartSynthEnabled = enabled;
    }

    public ChartSynthVoice ChartSynthVoice => ChartSynthVoice.Placeholder;
    public bool IsChartSynthVoiceAvailable(ChartSynthVoice voice) => voice == ChartSynthVoice.Placeholder;
    public bool TrySetChartSynthVoice(ChartSynthVoice voice) => voice == ChartSynthVoice.Placeholder;
    public float ChartSynthGain { get; set; } = 1f;
    public bool UploadChartNotes(ChartNoteUpload upload) => true;
    public long? UploadedChartFingerprint => null;
    public bool CanRouteInstrumentToRig => !Sandboxed;

    public bool RouteInstrumentToRig
    {
        get => _route && CanRouteInstrumentToRig;
        set
        {
            RouteWrites.Add(value);
            _route = value;
        }
    }

    public bool TryGetChartSynthStatus(out ChartSynthStatus status)
    {
        status = default;
        return false;
    }

    // The instruments (#50), with the native table's shape.
    public readonly List<ChartSynthInstrument> InstrumentWrites = [];
    public ChartSynthInstrument GuitarInstrument { get; set; } = ChartSynthInstrumentSelection.DefaultGuitar;
    public ChartSynthInstrument BassInstrument { get; set; } = ChartSynthInstrumentSelection.DefaultBass;

    public bool TrySetChartSynthInstrument(ChartSynthInstrument instrument)
    {
        var info = GetChartSynthModelInfo(instrument.Model);
        if (info == null || !info.Pickups.Contains(instrument.Pickup)) return false;
        InstrumentWrites.Add(instrument);
        if (instrument.IsBass) BassInstrument = instrument;
        else GuitarInstrument = instrument;
        return true;
    }

    public ChartSynthModelInfo? GetChartSynthModelInfo(ChartSynthModel model)
        => Infos.GetValueOrDefault(model) is { } info ? info with { IsInstalled = PacksInstalled } : null;

    public static readonly Dictionary<ChartSynthModel, ChartSynthModelInfo> Infos = Build();

    private static Dictionary<ChartSynthModel, ChartSynthModelInfo> Build()
    {
        ChartSynthPickup[] five = [ChartSynthPickup.Neck, ChartSynthPickup.NeckMiddle, ChartSynthPickup.Middle, ChartSynthPickup.MiddleBridge, ChartSynthPickup.Bridge];
        ChartSynthPickup[] three = [ChartSynthPickup.Neck, ChartSynthPickup.NeckBridge, ChartSynthPickup.Bridge];
        ChartSynthPickup[] pj = [ChartSynthPickup.Middle, ChartSynthPickup.MiddleBridge, ChartSynthPickup.Bridge];
        static ChartSynthModelInfo I(ChartSynthModel m, ChartSynthPickup[] p, ChartSynthPickup d, bool split, ChartSynthPickup[] rec, ChartSynthPickup[] partly)
            => new(m, (int)m >= 16, p, d, split, true, rec, partly);
        return new()
        {
            [ChartSynthModel.SemiHollow] = I(ChartSynthModel.SemiHollow, three, ChartSynthPickup.Bridge, false, [ChartSynthPickup.Bridge], [ChartSynthPickup.Bridge]),
            [ChartSynthModel.StratSss] = I(ChartSynthModel.StratSss, five, ChartSynthPickup.Bridge, false, five, five),
            [ChartSynthModel.StratHss] = I(ChartSynthModel.StratHss, five, ChartSynthPickup.Bridge, true, five[..3], five[..3]),
            [ChartSynthModel.Telecaster] = I(ChartSynthModel.Telecaster, three, ChartSynthPickup.Bridge, false, [], []),
            [ChartSynthModel.LesPaul] = I(ChartSynthModel.LesPaul, three, ChartSynthPickup.Bridge, true, [], []),
            [ChartSynthModel.JazzBass] = I(ChartSynthModel.JazzBass, three, ChartSynthPickup.NeckBridge, false, [], [ChartSynthPickup.NeckBridge]),
            [ChartSynthModel.PrecisionBass] = I(ChartSynthModel.PrecisionBass, [ChartSynthPickup.Middle], ChartSynthPickup.Middle, false, [], []),
            [ChartSynthModel.PjBass] = I(ChartSynthModel.PjBass, pj, ChartSynthPickup.MiddleBridge, false, [], []),
            [ChartSynthModel.HollowBass] = I(ChartSynthModel.HollowBass, [ChartSynthPickup.Neck], ChartSynthPickup.Neck, false, [], [ChartSynthPickup.Neck]),
        };
    }
}
