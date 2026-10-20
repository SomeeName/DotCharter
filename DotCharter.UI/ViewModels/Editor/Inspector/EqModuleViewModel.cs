using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Equalizer;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     One band, as the chip row and the type picker read it. A thin projection
///     of the section's own <see cref="EqBand" /> — it holds no band state, so
///     it cannot disagree with the engine.
/// </summary>
public sealed partial class EqBandChipViewModel : ViewModelBase
{
    private readonly EqModuleViewModel _owner;

    internal EqBandChipViewModel(EqModuleViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
    }

    /// <summary>0-based. The chip PRINTS <c>Index + 1</c>.</summary>
    public int Index { get; }

    public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The token KEY of this band's identity colour, resolved to a brush
    /// by <c>FireTokenBrushConverter</c>. A key rather than a brush, so no view
    /// model in this app stores a colour.</summary>
    public string ColorToken => $"FireEqBand{Index + 1}";

    private EqBand Band => _owner.Section.Bands[Index];

    /// <summary>The short type label on the chip — HP / LS / BELL / HS / LP /
    /// NTCH. Localized, because German abbreviates two of them differently.</summary>
    public string TypeLabel => _owner.Text($"Inspector_EqTypeShort_{Band.Type}");

    public bool IsEnabled => Band.Enabled;

    public bool IsSelected => _owner.SelectedIndex == Index;

    /// <summary>Selecting a chip is the "one band at a time" gesture the whole
    /// module is built around.</summary>
    [RelayCommand]
    private void Select() => _owner.SelectedIndex = Index;

    /// <summary>The chip's DOT toggles the band. Separate from selecting it, and
    /// deliberately so: the mockup's "click a band card to select, click its dot
    /// to bypass".</summary>
    [RelayCommand]
    private void ToggleEnabled() => _owner.SetBandEnabled(Index, !IsEnabled);

    /// <summary>Called by the owner after anything changed the section.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>
///     One option in the selected band's type picker.
/// </summary>
public sealed partial class EqTypeOptionViewModel : ViewModelBase
{
    private readonly EqModuleViewModel _owner;

    internal EqTypeOptionViewModel(EqModuleViewModel owner, EqBandType type)
    {
        _owner = owner;
        Type = type;
    }

    public EqBandType Type { get; }

    public string Label => _owner.Text($"Inspector_EqTypeShort_{Type}");

    public bool IsSelected => _owner.SelectedBandType == Type;

    [RelayCommand]
    private void Pick() => _owner.SelectedBandType = Type;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>
///     The inspector's EQ module: six typed bands over one
///     <see cref="EqSection" />, pushed to the engine on every change.
///     <para>
///         <b>Everything on screen is derived from that one section.</b> The
///         "n/6" badge, the chip row, the plot's curve, the plot's handles and
///         the band editor all read it; nothing keeps a second copy. And the
///         badge's NUMBER comes from the ENGINE
///         (<see cref="IAudioPlaybackService.EngineEqActiveCount" />) whenever
///         there is one to ask, so it is a statement about the DSP rather than
///         about this class.
///     </para>
///     <para>
///         <b>When the engine has no parametric EQ the whole module ships
///         disabled with its reason printed</b> (<see cref="IsLive" /> /
///         <see cref="UnavailableReason" />). That is the
///         root guide's rule: a control the engine cannot honour must
///         never look live.
///     </para>
/// </summary>
public sealed partial class EqModuleViewModel : ViewModelBase
{
    /// <summary>How many bins the analyser asks the engine for. Enough to make a
    /// hum spike visible without the plot drawing more detail than a 300 px-wide
    /// well can show.</summary>
    private const int SpectrumBins = 128;

    private readonly IAudioPlaybackService _audio;

    private readonly ILocalizationService _localization;

    public EqModuleViewModel(IAudioPlaybackService audio, ILocalizationService localization)
    {
        _audio = audio;
        _localization = localization;

        for (var i = 0; i < Section.Bands.Count; i++) Bands.Add(new EqBandChipViewModel(this, i));
        foreach (var type in Enum.GetValues<EqBandType>()) Types.Add(new EqTypeOptionViewModel(this, type));
        foreach (var key in EqPresets.Keys) Presets.Add(key);

        // The engine boots with every band off and this section starts the same
        // way, so nothing is pushed until the user touches something — but the
        // ROUTE and the TRIM are ours from the start, and an engine that never
        // heard them would answer a later band edit with a stale route.
        //
        // On a cold start there is usually no engine here at all (it is built on
        // the first audio load), which is precisely why RefreshLiveState exists
        // and why the view polls it.
        RefreshLiveState();
    }

    /// <summary>The one section. Public because the plot control binds it
    /// directly — it needs the whole band list to hit-test handles, and handing
    /// it a projection would put a second copy of the bands on screen.</summary>
    public EqSection Section { get; } = new();

    public ObservableCollection<EqBandChipViewModel> Bands { get; } = [];

    public ObservableCollection<EqTypeOptionViewModel> Types { get; } = [];

    /// <summary>Preset KEYS. The picker shows them through
    /// <see cref="PresetName" />; nothing here is a user-visible string.</summary>
    public ObservableCollection<string> Presets { get; } = [];

    /// <summary>
    ///     True when the engine underneath really has the six-band EQ.
    ///     Everything interactive in the module is gated on it.
    ///     <para>
    ///         <b>It CHANGES, so it notifies.</b> The audio
    ///         engine is built lazily — the first audio load or the first
    ///         transport call — while this view model is constructed with the
    ///         transport, before the window exists. So on a cold start it is false
    ///         at construction and true a second later, and with no notification
    ///         every <c>IsEnabled="{Binding IsLive}"</c> in the module kept
    ///         whatever it read first. <see cref="RefreshLiveState" /> is the one
    ///         place that flip is noticed, and it also PUSHES: an engine that
    ///         appears after the ctor has never been handed the section, and a
    ///         badge reading the engine's honest zero over lit band chips is
    ///         exactly what that looks like.
    ///     </para>
    /// </summary>
    public bool IsLive => _isLive;

    private bool _isLive;

    /// <summary>Printed, in full, wherever the module is disabled. An unexplained
    /// dead control is worse than an absent one.</summary>
    public string UnavailableReason => Text("Inspector_EqUnavailable");

    /// <summary>The analyser's magnitudes, or null when the engine cannot answer
    /// — in which case the plot draws no analyser at all rather than a shape it
    /// made up.</summary>
    [ObservableProperty] private float[]? _spectrum;

    // ── selection ────────────────────────────────────────────────────────

    private int _selectedIndex;

    /// <summary>Which band the editor below the plot is editing. The module's
    /// whole premise ("one band at a time") is this one integer.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value >= Section.Bands.Count || value == _selectedIndex) return;
            _selectedIndex = value;
            OnPropertyChanged();
            RefreshSelection();
        }
    }

    private EqBand Selected => Section.Bands[_selectedIndex];

    /// <summary>"Band 3 · Bell" — the band editor's header.</summary>
    public string SelectedTitle => TextFormat(
        "Inspector_EqBandTitle", _selectedIndex + 1, SelectedTypeName);

    public string SelectedTypeName =>
        Text($"Inspector_EqTypeName_{Selected.Type}");

    public string SelectedColorToken => $"FireEqBand{_selectedIndex + 1}";

    public EqBandType SelectedBandType
    {
        get => Selected.Type;
        set
        {
            if (value == Selected.Type) return;
            // Changing the shape re-clamps the gain: a bell dragged to +9 dB and
            // switched to a high pass must not keep a gain the filter has no way
            // to apply, or the printed value and the audible curve disagree.
            Section.SetBand(_selectedIndex, Selected with { Type = value });
            Commit();
        }
    }

    /// <summary>The frequency slider's position, 0..1 along the LOG axis. The
    /// slider is normalized rather than in Hz because a linear 20..20000 slider
    /// spends 95% of its travel above 1 kHz.</summary>
    public double SelectedFrequencyNormalized
    {
        get => EqCurve.FrequencyToNormalized(Selected.FrequencyHz);
        set
        {
            var hz = EqCurve.NormalizedToFrequency(value);
            if (Math.Abs(hz - Selected.FrequencyHz) < 0.01) return;
            Section.SetBand(_selectedIndex, Selected with { FrequencyHz = hz });
            Commit();
        }
    }

    public string SelectedFrequencyText => FormatFrequency(Selected.FrequencyHz);

    public double SelectedGainDb
    {
        get => Selected.GainDb;
        set
        {
            if (Math.Abs(value - Selected.GainDb) < 0.001) return;
            Section.SetBand(_selectedIndex, Selected with { GainDb = value });
            Commit();
        }
    }

    public string SelectedGainText =>
        string.Create(CultureInfo.CurrentCulture, $"{Selected.GainDb:+0.0;-0.0;0.0} dB");

    /// <summary>Whether the selected band's SHAPE has gain at all. False swaps
    /// the gain row for the slope readout — the mockup's "no gain on this
    /// type".</summary>
    public bool SelectedHasGain => Selected.HasGain;

    /// <summary>What the gain row prints instead, when there is no gain: the
    /// filter's real slope, or "full cut" for a notch. Both are FACTS about the
    /// biquad, not labels.</summary>
    public string SelectedSlopeText => Selected.Type == EqBandType.Notch
        ? Text("Inspector_EqFullCut")
        : TextFormat("Inspector_EqSlope", EqLimits.SlopeDbPerOctave);

    public double SelectedQNormalized
    {
        get => EqCurve.QToNormalized(Selected.Q);
        set
        {
            var q = EqCurve.NormalizedToQ(value);
            if (Math.Abs(q - Selected.Q) < 0.0001) return;
            Section.SetBand(_selectedIndex, Selected with { Q = q });
            Commit();
        }
    }

    public string SelectedQText => Selected.Q.ToString("0.00", CultureInfo.CurrentCulture);

    // ── the sliders' double-click defaults ───────────────────────────────
    //
    // Every one of these is the SAME number the RESET button and EqSection's own
    // reset use; none is a literal in the markup. A default that disagreed with
    // the reset button one row above it is the drift this feature already had
    // once, between EqLimits and a hand-typed 0.707.

    /// <summary>Where the frequency slider goes back to — the parked frequency
    /// for the SELECTED band's shape, on the slider's own 0..1 log axis.</summary>
    public double SelectedFrequencyDefaultNormalized =>
        EqCurve.FrequencyToNormalized(EqPresets.DefaultFrequencyFor(Selected.Type));

    /// <summary>Flat. The same 0 dB <c>EqSection.ResetBand</c> writes.</summary>
    public static double GainDefaultDb => 0.0;

    /// <summary>Butterworth, on the slider's own 0..1 log axis.</summary>
    public static double QDefaultNormalized => EqCurve.QToNormalized(EqLimits.DefaultQ);

    /// <summary>Unity. The trim is the one control here that is not per band.</summary>
    public static double TrimDefaultDb => 0.0;

    public bool IsSelectedSoloed => Section.SoloedBand == _selectedIndex;

    // ── section-wide state ───────────────────────────────────────────────

    /// <summary>Master power. The mockup's power button in the header.</summary>
    public bool IsSectionEnabled
    {
        get => Section.IsEnabled;
        set
        {
            if (value == Section.IsEnabled) return;
            Section.IsEnabled = value;
            Commit();
        }
    }

    /// <summary>"4/6" — or "SOLO 3" while a band is soloed, because with a solo
    /// standing the count is always 1 and saying so would be useless.</summary>
    public string ActiveBadge
    {
        get
        {
            if (Section.SoloedBand >= 0)
                return TextFormat("Inspector_EqSoloBadge", Section.SoloedBand + 1);

            // The ENGINE's count when there is an engine; the section's own only
            // when there is nothing to ask. Never both.
            var engineCount = _audio.EngineEqActiveCount;
            var count = engineCount >= 0 ? engineCount : Section.ActiveCount;
            return string.Create(CultureInfo.CurrentCulture, $"{count}/{EqLimits.BandCount}");
        }
    }

    public EqRoute Route
    {
        get => Section.Route;
        set
        {
            if (value == Section.Route) return;
            Section.Route = value;
            Commit();
        }
    }

    public bool IsRouteSong => Route == EqRoute.Song;
    public bool IsRouteGuitar => Route == EqRoute.GuitarIn;
    public bool IsRouteBoth => Route == EqRoute.Both;

    public double TrimDb
    {
        get => Section.TrimDb;
        set
        {
            if (Math.Abs(value - Section.TrimDb) < 0.001) return;
            Section.TrimDb = value;
            Commit();
        }
    }

    public string TrimText =>
        string.Create(CultureInfo.CurrentCulture, $"{Section.TrimDb:+0.0;-0.0;0.0} dB");

    /// <summary>The preset picker's current value, or the localized "Custom" once
    /// anything has been edited.</summary>
    public string PresetName => Section.PresetKey is { } key
        ? Text(EqPresets.ResourceKeyFor(key))
        : Text("Inspector_EqPresetCustom");

    public bool IsCustom => Section.PresetKey is null;

    /// <summary>The picker's SelectedItem. Null while the section is custom —
    /// which is what makes re-picking the preset you just edited work.</summary>
    public string? SelectedPresetKey
    {
        get => Section.PresetKey;
        set
        {
            if (value is null || value == Section.PresetKey) return;
            Section.LoadPreset(value);
            _selectedIndex = Math.Min(_selectedIndex, Section.Bands.Count - 1);
            Commit();
        }
    }

    /// <summary>The label the preset picker shows for a key. Used by the item
    /// template, so the list holds keys and the user reads sentences.</summary>
    public static string PresetLabel(string key, ILocalizationService localization) =>
        localization[EqPresets.ResourceKeyFor(key)];

    internal string Text(string key) => _localization[key];

    private string TextFormat(string key, params object[] args) => _localization.Format(key, args);

    // ── commands ─────────────────────────────────────────────────────────

    // `ToggleSectionEnabledCommand` is DELETED (0.58): the EQ power button is
    // `ToggleButton Classes="EqPower" IsChecked="{Binding IsSectionEnabled}"`,
    // and that property is a real get/set pair that pushes to the engine. Not to
    // be confused with the per-BAND `ToggleEnabledCommand` below, which IS bound.

    [RelayCommand]
    private void ToggleSolo()
    {
        Section.ToggleSolo(_selectedIndex);
        Commit();
    }

    [RelayCommand]
    private void ResetSelected()
    {
        Section.ResetBand(_selectedIndex);
        Commit();
    }

    [RelayCommand]
    private void SetRouteSong() => Route = EqRoute.Song;

    [RelayCommand]
    private void SetRouteGuitar() => Route = EqRoute.GuitarIn;

    [RelayCommand]
    private void SetRouteBoth() => Route = EqRoute.Both;

    /// <summary>Called by the plot when a drag, a wheel or a double-click moved a
    /// band. Same commit path as every slider — there is exactly one.</summary>
    public void OnBandEditedFromPlot(int index)
    {
        if (index != _selectedIndex && index >= 0 && index < Section.Bands.Count)
        {
            _selectedIndex = index;
            OnPropertyChanged(nameof(SelectedIndex));
        }

        Commit();
    }

    /// <summary>Switch a band on or off without selecting it — the chip's dot.</summary>
    public void SetBandEnabled(int index, bool enabled)
    {
        Section.SetBandEnabled(index, enabled);
        Commit();
    }

    /// <summary>
    ///     Poll the engine for a fresh analyser frame.
    ///     <para>
    ///         Driven by the VIEW's attach/detach timer rather than by a timer
    ///         here, so a module that is not on screen costs nothing — an
    ///         inspector tab the user is not looking at must not poll the audio
    ///         engine at 15 Hz forever.
    ///     </para>
    /// </summary>
    public void PollSpectrum()
    {
        // The engine can arrive (or go) between two ticks, and this is the only
        // beat anything in the module runs on.
        RefreshLiveState();

        if (!IsLive) { Spectrum = null; return; }

        var buffer = new float[SpectrumBins];
        var written = _audio.ReadEqSpectrum(buffer);
        // A short read is NOT a silent one: the plot draws its marked preview for
        // null, and that is the honest answer to "the engine could not fill this".
        Spectrum = written == SpectrumBins ? buffer : null;

        // The engine's active count can move without this class touching
        // anything (a device change replays the section), so the badge is
        // re-read on the same beat.
        OnPropertyChanged(nameof(ActiveBadge));
    }

    /// <summary>
    ///     Re-read whether there is an engine with a parametric EQ, and on the
    ///     transition into one, hand it the section.
    ///     <para>
    ///         The push is the load-bearing half. Without it the first engine of
    ///         the session runs an EQ it was never told about: every band off,
    ///         no route, no trim — so the badge reports the engine's truthful 0/6
    ///         while the chips, the curve and the band editor all show a section
    ///         the DSP has never seen. That divergence is the exact fault the
    ///         one-section design exists to prevent, arriving through a door the
    ///         design did not have.
    ///     </para>
    /// </summary>
    private void RefreshLiveState()
    {
        var live = _audio.SupportsParametricEq;
        if (live == _isLive) return;

        _isLive = live;
        if (live) Push();

        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(ActiveBadge));
    }

    // ── the one commit path ──────────────────────────────────────────────

    /// <summary>Push the section to the engine and tell every bound surface to
    /// re-read it. The ONLY way this class changes anything: a second path is how
    /// a display and a DSP come apart.</summary>
    private void Commit()
    {
        Push();
        RefreshAll();
    }

    /// <summary>Asks the SERVICE, not the cached <see cref="IsLive" />. The cache
    /// is a notification aid and can be one poll tick stale; a push that skipped
    /// on it would drop a real edit on the floor.</summary>
    private void Push()
    {
        if (!_audio.SupportsParametricEq) return;
        _audio.ApplyEq(Section);
    }

    private void RefreshSelection()
    {
        foreach (var chip in Bands) chip.Refresh();
        foreach (var type in Types) type.Refresh();

        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedTypeName));
        OnPropertyChanged(nameof(SelectedColorToken));
        OnPropertyChanged(nameof(SelectedBandType));
        OnPropertyChanged(nameof(SelectedFrequencyNormalized));
        OnPropertyChanged(nameof(SelectedFrequencyText));
        OnPropertyChanged(nameof(SelectedFrequencyDefaultNormalized));
        OnPropertyChanged(nameof(SelectedGainDb));
        OnPropertyChanged(nameof(SelectedGainText));
        OnPropertyChanged(nameof(SelectedHasGain));
        OnPropertyChanged(nameof(SelectedSlopeText));
        OnPropertyChanged(nameof(SelectedQNormalized));
        OnPropertyChanged(nameof(SelectedQText));
        OnPropertyChanged(nameof(IsSelectedSoloed));
    }

    private void RefreshAll()
    {
        RefreshSelection();
        OnPropertyChanged(nameof(IsSectionEnabled));
        OnPropertyChanged(nameof(ActiveBadge));
        OnPropertyChanged(nameof(Route));
        OnPropertyChanged(nameof(IsRouteSong));
        OnPropertyChanged(nameof(IsRouteGuitar));
        OnPropertyChanged(nameof(IsRouteBoth));
        OnPropertyChanged(nameof(TrimDb));
        OnPropertyChanged(nameof(TrimText));
        OnPropertyChanged(nameof(PresetName));
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(SelectedPresetKey));
        OnPropertyChanged(nameof(Section));
    }

    /// <summary>"400 Hz" / "3.00 kHz" / "12.0 kHz" — the mockup's own rule, and
    /// the reason it is here rather than in a StringFormat: the unit CHANGES at
    /// 1 kHz and the precision changes again at 10 kHz.</summary>
    internal static string FormatFrequency(double hz) => hz < 1000
        ? string.Create(CultureInfo.CurrentCulture, $"{Math.Round(hz):0} Hz")
        : hz < 10000
            ? string.Create(CultureInfo.CurrentCulture, $"{hz / 1000.0:0.00} kHz")
            : string.Create(CultureInfo.CurrentCulture, $"{hz / 1000.0:0.0} kHz");
}
