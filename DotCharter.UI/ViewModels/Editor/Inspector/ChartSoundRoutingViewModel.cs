// ChartSoundRoutingViewModel: the Mixer's chart-sound routing (design D) - the four-way Sound source with its two toggles, and Route instrument (guitar / bass seat), each with its availability and reason; owned by TransportViewModel.
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Mixer's "Instrument routing" card, bound as
///     <c>Transport.ChartSoundRouting</c>.
///     <para>
///         Sound source is NOT new engine state: it is a view over three existing
///         switches (<see cref="ChartSoundRouting" /> has the table) — the built-in
///         voice's tone (<c>ChartSynthPlaysOwnRig</c>), whether the loaded VST
///         instrument plays (<c>AppConfig.ChartSoundUsesInstrument</c>) and whether it
///         goes into the rig (<c>RouteInstrumentToRig</c>). Route instrument IS engine
///         state (ABI v51 seat masks): <see cref="IChartSynthEngine.ChartSeatRoute" />,
///         saved in <c>AppConfig.ChartSoundSeatRoute</c> and replayed by the service
///         onto a rebuilt engine.
///     </para>
/// </summary>
public sealed class ChartSoundRoutingViewModel : ViewModelBase
{
    private readonly TransportViewModel _transport;
    private readonly IConfigurationService _config;
    private readonly DotCharter.Localization.ILocalizationService _localization;

    internal ChartSoundRoutingViewModel(TransportViewModel transport, IConfigurationService config,
        DotCharter.Localization.ILocalizationService localization)
    {
        _transport = transport;
        _config = config;
        _localization = localization;
    }

    private IChartSynthEngine? Synth => _transport.ChartSynth;

    /// <summary>
    ///     The four-way Sound source (Song tones / Own rig / My instrument /
    ///     Instrument through rig). Reads what PLAYS: with no VST instrument loaded
    ///     it is a built-in value whatever was saved. A value that needs the
    ///     instrument is refused while none is loaded (<see cref="CanUseMyInstrument" />).
    /// </summary>
    public ChartSoundSource Source
    {
        get
        {
            var instrument = _transport.PlaysInstrumentVst;
            var rig = instrument ? _transport.RouteInstrumentToRig && _transport.CanRouteInstrumentToRig : _transport.ChartSynthPlaysOwnRig;
            return ChartSoundRouting.Compose(instrument, rig);
        }
        set
        {
            if (!Enum.IsDefined(value) || value == Source) return;
            var useInstrument = ChartSoundRouting.UsesInstrument(value);
            if (useInstrument && !CanUseMyInstrument) return;
            var rig = ChartSoundRouting.ThroughRig(value);
            if (useInstrument && rig && !_transport.CanRouteInstrumentToRig) return;
            var cfg = _config.LoadAppConfig();
            if (cfg.ChartSoundUsesInstrument != useInstrument)
            {
                cfg.ChartSoundUsesInstrument = useInstrument;
                _config.SaveAppConfig(cfg);
            }
            if (useInstrument) _transport.RouteInstrumentToRig = rig;
            else _transport.ChartSynthPlaysOwnRig = rig;
            _transport.ApplyMidiOutRouting();
            Raise();
        }
    }

    /// <summary>"Use my instrument": the half of <see cref="Source" /> that picks the
    /// loaded VST instrument over the built-in voice.</summary>
    public bool UsesInstrument
    {
        get => ChartSoundRouting.UsesInstrument(Source);
        set => Source = ChartSoundRouting.Compose(value, ThroughRig);
    }

    /// <summary>"Through rig": the other half — the player's own rig for the
    /// built-in voice, the rig input for the instrument.</summary>
    public bool ThroughRig
    {
        get => ChartSoundRouting.ThroughRig(Source);
        set => Source = ChartSoundRouting.Compose(UsesInstrument, value);
    }

    // The card's segments: one bool per value, so a radio group binds each one; a
    // segment's "unchecked" write is the group letting go and changes nothing.

    /// <summary>The Sound source segment "Song tones".</summary>
    public bool IsSongTones
    {
        get => Source == ChartSoundSource.SongTones;
        set => PickSource(value, ChartSoundSource.SongTones);
    }

    /// <summary>The Sound source segment "My rig".</summary>
    public bool IsOwnRig
    {
        get => Source == ChartSoundSource.OwnRig;
        set => PickSource(value, ChartSoundSource.OwnRig);
    }

    /// <summary>The Sound source segment "My instrument" (after the mix).</summary>
    public bool IsMyInstrument
    {
        get => Source == ChartSoundSource.MyInstrument;
        set => PickSource(value, ChartSoundSource.MyInstrument);
    }

    /// <summary>The Sound source segment "Instrument through the rig".</summary>
    public bool IsInstrumentThroughRig
    {
        get => Source == ChartSoundSource.InstrumentThroughRig;
        set => PickSource(value, ChartSoundSource.InstrumentThroughRig);
    }

    // A refused pick re-reads every segment, so the clicked one does not stay lit.
    private void PickSource(bool chosen, ChartSoundSource source)
    {
        if (!chosen) return;
        Source = source;
        if (Source != source) Raise();
    }

    /// <summary>"Instrument through the rig" can be honoured: an instrument is loaded
    /// and plugins do not run in the sandbox.</summary>
    public bool CanUseInstrumentThroughRig => CanUseMyInstrument && _transport.CanRouteInstrumentToRig;

    /// <summary>Why one or both instrument segments are disabled (no instrument loaded,
    /// or the sandbox), or empty while both are live or the synth is missing (the card
    /// then says that instead).</summary>
    public string SourceUnavailableReason =>
        Synth == null || CanUseInstrumentThroughRig ? ""
        : !CanUseMyInstrument ? UseMyInstrumentUnavailableReason
        : _localization["Mixer_InstrumentThroughRig_Sandboxed"];

    /// <summary>A VST instrument is loaded, so "Use my instrument" can be honoured.</summary>
    public bool CanUseMyInstrument => Synth != null && _transport.HasInstrumentVst;

    /// <summary>Why "Use my instrument" is disabled, or empty while it is live.</summary>
    public string UseMyInstrumentUnavailableReason =>
        CanUseMyInstrument ? "" : _localization["Mixer_ChartSound_MyInstrument_Unavailable"];

    /// <summary>"Through rig" can be honoured for the CURRENT source: always for the
    /// built-in voice; for the instrument only outside the VST sandbox.</summary>
    public bool CanGoThroughRig => Synth != null && (!UsesInstrument || _transport.CanRouteInstrumentToRig);

    /// <summary>Why "Through rig" is disabled, or empty while it is live.</summary>
    public string ThroughRigUnavailableReason =>
        CanGoThroughRig ? "" : _localization["Mixer_InstrumentThroughRig_Sandboxed"];

    /// <summary>
    ///     Which of the player's live instrument seats' rigs the chart sound enters:
    ///     Guitar only (the default and the original path), Bass only, Guitar and
    ///     bass. Saved; with one live input the guitar seat plays it whatever this
    ///     says, and the choice is disabled (<see cref="CanRouteToSeats" />).
    /// </summary>
    public ChartSoundSeatRoute SeatRoute
    {
        get => Synth?.ChartSeatRoute ?? ChartSoundRouting.DefaultSeatRoute;
        set
        {
            var synth = Synth;
            var route = ChartSoundRouting.Normalize(value);
            if (synth == null || synth.ChartSeatRoute == route) return;
            synth.ChartSeatRoute = route;
            var cfg = _config.LoadAppConfig();
            cfg.ChartSoundSeatRoute = route;
            _config.SaveAppConfig(cfg);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGuitarOnly));
            OnPropertyChanged(nameof(IsBassOnly));
            OnPropertyChanged(nameof(IsGuitarAndBass));
        }
    }

    /// <summary>The Route instrument segment "Guitar".</summary>
    public bool IsGuitarOnly
    {
        get => SeatRoute == ChartSoundSeatRoute.GuitarOnly;
        set => PickRoute(value, ChartSoundSeatRoute.GuitarOnly);
    }

    /// <summary>The Route instrument segment "Bass".</summary>
    public bool IsBassOnly
    {
        get => SeatRoute == ChartSoundSeatRoute.BassOnly;
        set => PickRoute(value, ChartSoundSeatRoute.BassOnly);
    }

    /// <summary>The Route instrument segment "Both" (guitar and bass).</summary>
    public bool IsGuitarAndBass
    {
        get => SeatRoute == ChartSoundSeatRoute.GuitarAndBass;
        set => PickRoute(value, ChartSoundSeatRoute.GuitarAndBass);
    }

    private void PickRoute(bool chosen, ChartSoundSeatRoute route)
    {
        if (chosen) SeatRoute = route;
    }

    /// <summary>The engine runs a bass seat (two live inputs), so the choice is live.</summary>
    public bool CanRouteToSeats => Synth?.CanRouteChartToSeats ?? false;

    /// <summary>
    ///     Route instrument changes what plays: a bass seat exists AND the source
    ///     enters a rig the route picks. Under Song tones the chart sound always plays
    ///     into the guitar seat (the song's tone exists only on that rig,
    ///     <see cref="ChartSoundRouting.ChartSourceSeatMask" />); My instrument plays
    ///     after the mix, through no rig. The saved route stays for the next source.
    /// </summary>
    public bool CanChooseSeatRoute => CanRouteToSeats && ChartSoundRouting.ThroughRig(Source);

    /// <summary>Why Route instrument is disabled, or empty while it is live.</summary>
    public string SeatRouteUnavailableReason =>
        !CanRouteToSeats ? _localization["Mixer_ChartSound_SeatRoute_Unavailable"]
        : Source == ChartSoundSource.SongTones ? _localization["Mixer_ChartSound_Route_SongTones"]
        : Source == ChartSoundSource.MyInstrument ? _localization["Mixer_ChartSound_Route_AfterMix"]
        : "";

    /// <summary>Startup: the saved seat route and instrument route go to the audio
    /// service, which keeps them as its request and replays them on every rebuild.</summary>
    internal void Restore()
    {
        var synth = Synth;
        if (synth == null) return;
        var cfg = _config.LoadAppConfig();
        synth.ChartSeatRoute = ChartSoundRouting.Normalize(cfg.ChartSoundSeatRoute);
        synth.ChartSynthPlaysSongTones = !cfg.ChartSynthPlaysOwnRig;
        if (cfg.InstrumentThroughRig) synth.RouteInstrumentToRig = true;
    }

    /// <summary>Saves the instrument's Through rig half, so the next start routes it again.</summary>
    internal void SaveThroughRig(bool value)
    {
        var cfg = _config.LoadAppConfig();
        if (cfg.InstrumentThroughRig != value)
        {
            cfg.InstrumentThroughRig = value;
            _config.SaveAppConfig(cfg);
        }

        Raise();
    }

    /// <summary>Something under the card moved (a plugin, the sandbox, the seats, the
    /// synth's tone). Song tones live on the guitar seat's rig, so the service keeps
    /// the chart source there while they play (<c>ChartSoundRouting.ChartSourceSeatMask</c>).</summary>
    internal void Raise()
    {
        Synth?.ChartSynthPlaysSongTones = !_transport.ChartSynthPlaysOwnRig;
        foreach (var name in (string[])[nameof(Source), nameof(UsesInstrument), nameof(ThroughRig), nameof(CanUseMyInstrument),
                     nameof(UseMyInstrumentUnavailableReason), nameof(CanGoThroughRig), nameof(ThroughRigUnavailableReason),
                     nameof(IsSongTones), nameof(IsOwnRig), nameof(IsMyInstrument), nameof(IsInstrumentThroughRig),
                     nameof(CanUseInstrumentThroughRig), nameof(SourceUnavailableReason),
                     nameof(SeatRoute), nameof(IsGuitarOnly), nameof(IsBassOnly), nameof(IsGuitarAndBass),
                     nameof(CanRouteToSeats), nameof(CanChooseSeatRoute), nameof(SeatRouteUnavailableReason)])
        {
            OnPropertyChanged(name);
        }
    }
}
