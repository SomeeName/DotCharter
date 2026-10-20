using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.ChartSynth;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The built-in chart synth's INSTRUMENT pickers: guitar / bass model,
///     pickup selector, coil split, the recorded-or-simulated line and the song-tone
/// override. <c>Views/Editor/Inspector/ChartSynthInstrumentView</c> binds
///     it in both of its hosts — the editor's Mixer ▸ MIDI synth card and Play Mode's
///     Amp/FX panel — through <see cref="TransportViewModel.ChartSynthInstrument" />,
///     so the two can never offer different choices.
///     <para>
///         WHICH instrument plays — the player profile's, or a playing song tone's
///         override — is <see cref="ChartSynthInstrumentCoordinator" />'s; the pickers
///         show and edit its effective choice. With no coordinator (a test transport)
///         the pickers write the service directly and nothing is saved. With no synth
///         (the null transport) every list is empty and the view shows disabled.
///     </para>
/// </summary>
public sealed partial class ChartSynthInstrumentViewModel : ViewModelBase
{
    /// <summary>The order a player looks for them: the owner's HSS first.</summary>
    private static readonly ChartSynthModel[] ModelOrder =
    [
        ChartSynthModel.StratHss, ChartSynthModel.StratSss, ChartSynthModel.Telecaster, ChartSynthModel.LesPaul,
        ChartSynthModel.SemiHollow, ChartSynthModel.JazzBass, ChartSynthModel.PrecisionBass, ChartSynthModel.PjBass,
        ChartSynthModel.HollowBass,
    ];

    private readonly IChartSynthEngine? _synth;
    private readonly ChartSynthInstrumentCoordinator? _instruments;
    private readonly DotCharter.Localization.ILocalizationService _localization;
    private readonly IUiDispatcher _ui;

    // What each model offers comes from the native table and checks the pack files
    // on disk, so it is read ONCE, off the UI thread, the first time a picker asks.
    private IReadOnlyDictionary<ChartSynthModel, ChartSynthModelInfo>? _info;
    private int _infoRequested;
    private IReadOnlyList<ChartSynthChoice<ChartSynthModel>>? _guitarModelChoices, _bassModelChoices;

    // The pickup choices per model, built ONCE: the combo's ItemsSource must stay the
    // same instance across a refresh. A new array made the ComboBox re-select its
    // OLD pick in the new list and write it back through the two-way binding,
    // undoing the user's pick (bridge -> neck snapped back to bridge).
    private readonly Dictionary<ChartSynthModel, IReadOnlyList<ChartSynthChoice<ChartSynthPickup>>> _pickupChoices = [];

    // True while Refresh runs: a picker write that arrives during that refresh is
    // the control echoing the old selection, never a pick.
    private bool _refreshing;

    /// <summary>Builds the pickers over <paramref name="synth" /> (null: no chart synth).</summary>
    public ChartSynthInstrumentViewModel(IChartSynthEngine? synth, ChartSynthInstrumentCoordinator? instruments,
        DotCharter.Localization.ILocalizationService localization, IUiDispatcher ui)
    {
        _synth = synth;
        _instruments = instruments;
        _localization = localization;
        _ui = ui;

        // The chart synth's instrument follows the profile and the playing song
        // tone: the coordinator says when, from whichever thread moved it.
        _instruments?.Changed += () => _ui.Post(Refresh);
    }

    /// <summary>Raised after every refresh of the pickers — the model table landed or
    /// the instrument moved — so the transport re-reads whether the stand-in plays.</summary>
    internal event Action? Refreshed;

    /// <summary>The audio service has a chart synth (false on the null transport).</summary>
    public bool IsAvailable => _synth != null;

    /// <summary>The model table has loaded and no voice pack is installed, so the
    /// built-in voice plays its stand-in. Asking starts the load.</summary>
    internal bool NoVoicePackInstalled => Info is { Count: > 0 } map && map.Values.All(i => !i.IsInstalled);

    private IReadOnlyDictionary<ChartSynthModel, ChartSynthModelInfo>? Info
    {
        get
        {
            if (_info == null) RequestInfo();
            return _info;
        }
    }

    private void RequestInfo()
    {
        var synth = _synth;
        if (synth == null || Interlocked.Exchange(ref _infoRequested, 1) == 1) return;
        _ = Task.Run(() => ReadInfo(synth)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            _ui.Post(() =>
            {
                _info = t.Result;
                Refresh();
            });
        }, TaskScheduler.Default);
    }

    private static IReadOnlyDictionary<ChartSynthModel, ChartSynthModelInfo> ReadInfo(IChartSynthEngine synth)
    {
        var map = new Dictionary<ChartSynthModel, ChartSynthModelInfo>();
        foreach (var m in ModelOrder)
            if (synth.GetChartSynthModelInfo(m) is { } info) map[m] = info;
        return map;
    }

    /// <summary>Test seam: what the pickers read, loaded synchronously.</summary>
    internal void LoadInfoForTest()
    {
        if (_synth == null) return;
        _info = ReadInfo(_synth);
        _infoRequested = 1;
        _guitarModelChoices = _bassModelChoices = null;
        _pickupChoices.Clear();
        Refresh();
    }

    /// <summary>The guitar models the built-in voice offers (empty until loaded, or with no synth).</summary>
    public IReadOnlyList<ChartSynthChoice<ChartSynthModel>> GuitarModels => ModelChoices(bass: false);

    /// <summary>The bass models the built-in voice offers.</summary>
    public IReadOnlyList<ChartSynthChoice<ChartSynthModel>> BassModels => ModelChoices(bass: true);

    private IReadOnlyList<ChartSynthChoice<ChartSynthModel>> ModelChoices(bool bass)
    {
        var info = Info;
        if (info == null) return [];
        ref var cache = ref bass ? ref _bassModelChoices : ref _guitarModelChoices;
        return cache ??= ModelOrder.Where(m => info.TryGetValue(m, out var i) && i.IsBass == bass)
            .Select(m => new ChartSynthChoice<ChartSynthModel>(m, Loc("ChartSynth_Model_" + m)))
            .ToArray();
    }

    /// <summary>The guitar model. Changing it keeps the pickup when the new
    /// model has it, else takes the model's default; the choice is saved.</summary>
    public ChartSynthChoice<ChartSynthModel>? SelectedGuitarModel
    {
        get => ModelChoice(bass: false);
        set => SelectModel(value, bass: false);
    }

    /// <summary>The bass model.</summary>
    public ChartSynthChoice<ChartSynthModel>? SelectedBassModel
    {
        get => ModelChoice(bass: true);
        set => SelectModel(value, bass: true);
    }

    /// <summary>The guitar model's pickup selector, neck first.</summary>
    public IReadOnlyList<ChartSynthChoice<ChartSynthPickup>> GuitarPickups => PickupChoices(bass: false);

    /// <summary>The bass model's pickup selector, neck first.</summary>
    public IReadOnlyList<ChartSynthChoice<ChartSynthPickup>> BassPickups => PickupChoices(bass: true);

    /// <summary>The guitar's selected pickup position.</summary>
    public ChartSynthChoice<ChartSynthPickup>? SelectedGuitarPickup
    {
        get => PickupChoice(bass: false);
        set => SelectPickup(value, bass: false);
    }

    /// <summary>The bass's selected pickup position.</summary>
    public ChartSynthChoice<ChartSynthPickup>? SelectedBassPickup
    {
        get => PickupChoice(bass: true);
        set => SelectPickup(value, bass: true);
    }

    /// <summary>The guitar model has a humbucker the coil split changes.</summary>
    public bool GuitarHasCoilSplit => InfoFor(false) is { HasCoilSplit: true };

    /// <summary>The guitar's humbuckers as one coil (thinner, brighter).</summary>
    public bool GuitarCoilSplit
    {
        get => Instrument(false) is { CoilSplit: true };
        set
        {
            if (Instrument(false) is not { } current || current.CoilSplit == value) return;
            ApplyInstrument(current with { CoilSplit = value });
        }
    }

    /// <summary>Whether the guitar's selection is real recordings or simulated.</summary>
    public string GuitarSound => SoundOf(bass: false);

    /// <summary>Whether the bass's selection is real recordings or simulated.</summary>
    public string BassSound => SoundOf(bass: true);

    private ChartSynthInstrument? Instrument(bool bass)
    {
        var synth = _synth;
        if (synth == null) return null;
        if (_instruments != null) return _instruments.Effective(bass);
        return bass ? synth.BassInstrument : synth.GuitarInstrument;
    }

    // ---- Song-tone override ------------------------------------------------

    /// <summary>A song tone is playing and has no instrument of its own: offer to give it one.</summary>
    public bool CanOverrideTone => _instruments is { CanOverrideTone: true, HasToneOverride: false } && _synth != null;

    /// <summary>The playing song tone sets the instrument: the pickers edit that tone's choice.</summary>
    public bool HasToneOverride => _instruments is { HasToneOverride: true } && _synth != null;

    /// <summary>Says which tone sets the instrument (empty when none does).</summary>
    public string ToneOverrideText
    {
        get
        {
            if (!HasToneOverride) return string.Empty;
            var tone = _instruments!.ActiveToneName ?? string.Empty;
            return _localization.Format("ChartSynth_Override_Active", tone);
        }
    }

    /// <summary>"Only for this tone": the playing song tone gets its own instrument.</summary>
    [RelayCommand]
    private void OverrideTone()
    {
        if (_instruments?.OverrideTone() == true) Refresh();
    }

    /// <summary>"Use my instrument": drops the playing tone's override; the profile's plays again.</summary>
    [RelayCommand]
    private void UseMyInstrument()
    {
        if (_instruments?.UseProfileInstrument() == true) Refresh();
    }

    private ChartSynthModelInfo? InfoFor(bool bass)
        => Instrument(bass) is { } i && Info is { } map && map.TryGetValue(i.Model, out var info) ? info : null;

    private ChartSynthChoice<ChartSynthModel>? ModelChoice(bool bass)
        => Instrument(bass) is { } i ? ModelChoices(bass).FirstOrDefault(c => c.Value == i.Model) : null;

    private IReadOnlyList<ChartSynthChoice<ChartSynthPickup>> PickupChoices(bool bass)
    {
        if (InfoFor(bass) is not { } info) return [];
        if (!_pickupChoices.TryGetValue(info.Model, out var choices))
        {
            choices = info.Pickups.Select(p => new ChartSynthChoice<ChartSynthPickup>(p, PickupLabel(p, info.Model))).ToArray();
            _pickupChoices[info.Model] = choices;
        }

        return choices;
    }

    private ChartSynthChoice<ChartSynthPickup>? PickupChoice(bool bass)
        => Instrument(bass) is { } i ? PickupChoices(bass).FirstOrDefault(c => c.Value == i.Pickup) : null;

    private void SelectModel(ChartSynthChoice<ChartSynthModel>? choice, bool bass)
    {
        if (_refreshing || choice == null || Instrument(bass) is not { } current || current.Model == choice.Value) return;
        if (Info is not { } map || !map.TryGetValue(choice.Value, out var info)) return;
        var pickup = info.Pickups.Contains(current.Pickup) ? current.Pickup : info.DefaultPickup;
        ApplyInstrument(new ChartSynthInstrument(choice.Value, pickup, current.CoilSplit && info.HasCoilSplit));
    }

    private void SelectPickup(ChartSynthChoice<ChartSynthPickup>? choice, bool bass)
    {
        if (_refreshing || choice == null || Instrument(bass) is not { } current || current.Pickup == choice.Value) return;
        ApplyInstrument(current with { Pickup = choice.Value });
    }

    /// <summary>Hands the pick to the coordinator, which saves it with the player
    /// profile (or the playing tone's override) and sets it on the service (which
    /// loads it off the UI thread); the file write is off the UI thread too.</summary>
    private void ApplyInstrument(ChartSynthInstrument instrument)
    {
        var synth = _synth;
        if (synth == null) return;
        var applied = _instruments?.SetInstrument(instrument) ?? synth.TrySetChartSynthInstrument(instrument);
        if (applied) Refresh();
    }

    /// <summary>Re-reads every picker: the model table landed, the instrument moved,
    /// or the synth's availability changed.</summary>
    internal void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            RaiseProperties();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RaiseProperties()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(GuitarModels));
        OnPropertyChanged(nameof(BassModels));
        OnPropertyChanged(nameof(SelectedGuitarModel));
        OnPropertyChanged(nameof(SelectedBassModel));
        OnPropertyChanged(nameof(GuitarPickups));
        OnPropertyChanged(nameof(BassPickups));
        OnPropertyChanged(nameof(SelectedGuitarPickup));
        OnPropertyChanged(nameof(SelectedBassPickup));
        OnPropertyChanged(nameof(GuitarHasCoilSplit));
        OnPropertyChanged(nameof(GuitarCoilSplit));
        OnPropertyChanged(nameof(GuitarSound));
        OnPropertyChanged(nameof(BassSound));
        OnPropertyChanged(nameof(CanOverrideTone));
        OnPropertyChanged(nameof(HasToneOverride));
        OnPropertyChanged(nameof(ToneOverrideText));
        // The model table landed: whether the voice packs are installed is known.
        Refreshed?.Invoke();
    }

    private string SoundOf(bool bass)
    {
        if (Instrument(bass) is not { } i || InfoFor(bass) is not { } info) return string.Empty;
        var key = !info.IsInstalled ? "ChartSynth_Sound_NotInstalled"
            : i.CoilSplit && info.HasCoilSplit ? "ChartSynth_Sound_Simulated"
            : info.RecordedPickups.Contains(i.Pickup) ? "ChartSynth_Sound_Recorded"
            : info.PartlyRecordedPickups.Contains(i.Pickup) ? "ChartSynth_Sound_PartlyRecorded"
            : "ChartSynth_Sound_Simulated";
        var family = Loc(bass ? "Mixer_ChartSynth_Bass" : "Mixer_ChartSynth_Guitar");
        return _localization.Format("ChartSynth_SoundLine", family, Loc(key));
    }

    /// <summary>A P bass's single pickup reads as "P", a PJ's middle and bridge as
    /// "P" / "J"; everything else by its position.</summary>
    private string PickupLabel(ChartSynthPickup pickup, ChartSynthModel model) => (model, pickup) switch
    {
        (ChartSynthModel.PrecisionBass, ChartSynthPickup.Middle) or (ChartSynthModel.PjBass, ChartSynthPickup.Middle)
            => Loc("ChartSynth_Pickup_P"),
        (ChartSynthModel.PjBass, ChartSynthPickup.MiddleBridge) => Loc("ChartSynth_Pickup_PJ"),
        (ChartSynthModel.PjBass, ChartSynthPickup.Bridge) => Loc("ChartSynth_Pickup_J"),
        _ => Loc("ChartSynth_Pickup_" + pickup),
    };

    private string Loc(string key) => _localization[key];
}

/// <summary>One entry of a chart-synth picker: the value and what the user reads.
/// Its <see cref="ToString" /> is the label, which is what names a list item for
/// a screen reader.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Label">The localized label.</param>
public sealed record ChartSynthChoice<T>(T Value, string Label) where T : struct
{
    /// <inheritdoc />
    public override string ToString() => Label;
}
