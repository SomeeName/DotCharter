// PedalNodeViewModel: one pedal card - its skin and rig visuals, its param/selector surfaces, bypass, branch tag and neural-amp status badge.

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using JuceSharp.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// One pedal card: a chain node with its bypass toggle and the generic
/// parameter sliders. Structural ops (reorder / remove) live on the parent
/// <see cref="PedalboardViewModel"/>; bypass + params flow straight to the
/// service from here.
/// </summary>
public sealed partial class PedalNodeViewModel : ObservableObject
{
    private readonly IDspChainService _dsp;
    private readonly IAudioPlaybackService? _audio;
    private readonly ILocalizationService _loc;
    private bool _suppress;

    /// <summary>The words this card prints, handed on to the editor built over it.</summary>
    public ILocalizationService Localization => _loc;

    /// <summary>The native neural-amp (NAM/AIDA-X) module id — its poll-only
    /// <c>loaded</c>/<c>rateMismatch</c> status drives the card's badge.</summary>
    private const string NamampModuleTypeId = "namamp";

    public PedalNodeViewModel(IDspChainService dsp, DspNode node, DspModuleType? type,
        ILocalizationService localization,
        IAudioPlaybackService? audio = null,
        DotCharter.Tone.Infrastructure.Storage.IFileStorageService? files = null,
        Func<string, NamCaptureManifest?>? captureLookup = null,
        Func<string, IReadOnlyList<NamCaptureChoice>>? libraryChoices = null)
    {
        _dsp = dsp;
        _audio = audio;
        _loc = localization;
        NodeId = node.NodeId;
        ModuleTypeId = node.ModuleTypeId;
        DisplayName = type?.DisplayName is { Length: > 0 } name ? name : node.ModuleTypeId;
        IsNeuralAmp = string.Equals(ModuleTypeId, NamampModuleTypeId, StringComparison.Ordinal);
        IsSplitter = string.Equals(ModuleTypeId, SplitRegionLayout.SplitterId, StringComparison.Ordinal);
        IsMerger = string.Equals(ModuleTypeId, SplitRegionLayout.MergerId, StringComparison.Ordinal);
        Branch = node.Branch;
        _bypassed = node.Bypassed;

        // Rig visuals derived from the module type (pure look-up). Accent/body
        // use the per-MODULE colour so pedals within a family stay distinct.
        Category = DspModuleVisuals.Categorize(ModuleTypeId);
        KnobStyle = DspModuleVisuals.KnobStyleFor(Category);
        var accent = DspModuleVisuals.AccentColor(ModuleTypeId);
        Accent = accent;
        AccentBrush = new SolidColorBrush(accent);
        BodyColor = DspModuleVisuals.BodyColor(ModuleTypeId);
        BodyBrush = DspModuleVisuals.IsWideChassis(Skin)
            ? BuildTolexBrush(DspModuleVisuals.BodyColor(ModuleTypeId))
            : BuildBodyBrush(DspModuleVisuals.BodyColor(ModuleTypeId));
        EdgeBrush = new SolidColorBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B));

        var pars = type?.Params ?? [];
        Params = new ObservableCollection<PedalParamViewModel>(
            pars.Select(p =>
            {
                var value = node.Values.TryGetValue(p.Id, out var v) ? v : p.Default;
                return new PedalParamViewModel(_dsp, NodeId, p, value);
            }));
        if (ModuleTypeId == "delay" && Params.FirstOrDefault(p => p.Id == "sync") is { } sync &&
            Params.FirstOrDefault(p => p.Id == "timeMs") is { } manualTime)
        {
            void RefreshManualTime()
            {
                manualTime.IsControlActive = sync.Value < .5f;
                manualTime.InactiveHint = manualTime.IsControlActive ? null : _loc["Rig_DelayManualInactive"];
            }
            sync.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PedalParamViewModel.Value)) RefreshManualTime();
            };
            RefreshManualTime();
        }

        // rsamp: ten generic knob POSITIONS whose meaning is the picked
        // Rocksmith amp's. Label them as the game does, and grey the ones this
        // amp does not have; again whenever the model picker moves. Without a
        // Rocksmith install nothing says what they are: all ten go grey
        // and say why.
        if (ModuleTypeId == RsAmpKnobs.ModuleTypeId && Params.FirstOrDefault(p => p.Id == "model") is { } rsModel)
        {
            var needsInstall = _loc["Rig_RsAmpNeedsInstall"];
            rsModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PedalParamViewModel.Value)) RelabelRsAmpKnobs(Params, rsModel.Value, needsInstall);
            };
            RelabelRsAmpKnobs(Params, rsModel.Value, needsInstall);
        }

        // String-valued params (ABI v22): file-picker rows (namamp modelPath,
        // cab/acousticsim irPath) + the miku phrases lyric editor. Seeded from
        // the node's current string values so a reopened board shows them.
        var strPars = type?.StringParams ?? [];
        StringParams = new ObservableCollection<PedalStringParamViewModel>(
            strPars.Select(sp =>
            {
                var value = node.StringValues.TryGetValue(sp.Id, out var s) ? s : string.Empty;
                // An empty irPath does not mean "unconfigured": the cab runs its
                // built-in IR (the `model` param, this node's badge). Hand the
                // plate that name — the closure reads the FIELD lazily because
                // _badgeParam is assigned below, after the string params exist.
                var builtinLabel = sp.Id == "irPath"
                    ? new Func<string?>(() => _badgeParam?.CurrentLabel)
                    : null;
                return new PedalStringParamViewModel(_dsp, NodeId, sp, value, files, _loc, OnStringCommitted,
                    captureLookup, libraryChoices, builtinLabel);
            }));
        HasStringParams = StringParams.Count > 0;

        // Skin selection (metadata-guarded): the specialized faces only apply
        // when the metadata they key off is present, so a changed native module
        // degrades to the generic stompbox instead of an empty face.
        var skin = DspModuleVisuals.SkinFor(ModuleTypeId);
        WahTreadleParam = skin == PedalSkin.Wah
            ? Params.FirstOrDefault(p => p.Id == "position")
            : null;
        if (skin == PedalSkin.Wah && WahTreadleParam == null) skin = PedalSkin.Stompbox;
        // The fader bank renders the CONTINUOUS params — a graphiceq whose bands
        // all vanished (or turned into labelled selectors) would show an empty
        // recess, so degrade to the generic stompbox instead.
        if (skin == PedalSkin.GraphicEq && !Params.Any(p => !p.HasValueLabels))
            skin = PedalSkin.Stompbox;
        // A namamp holding a capture of a PEDAL is a pedal, not a second amp.
        // A boosted preset is a pedal capture in FRONT of an amp
        // capture and both are namamp nodes, so keying the face off the module
        // type alone drew two amp heads side by side — the board read as "there
        // are two amps in here". The capture's gear type is the only thing that
        // separates them, and it lives on the loaded FILE, not on the module.
        // Resolved here rather than reactively because every path that changes a
        // modelPath (preset apply, the card's file picker, "load into rig")
        // rebuilds the board, so the value is already correct at construction.
        if (skin == PedalSkin.NeuralAmp && CaptureIsPedal(StringParams, captureLookup))
            skin = PedalSkin.NeuralPedal;
        Skin = skin;
        WahKnobParams = skin == PedalSkin.Wah
            ? Params.Where(p => !ReferenceEquals(p, WahTreadleParam) && !p.HasValueLabels).ToList()
            : [];

        // Model pickers (ABI v24 value labels): a labelled param renders as a
        // selector, NOT a raw knob. On the amp face the voicing becomes the
        // prominent clickable model plate (grouped popup); cab/od/dist get a
        // compact selector. Everything else stays a knob.
        // Three roles, not one. A labelled param is a PICKER unless the (module,
        // param) table says it is a transport or a momentary trigger - see
        // RigParamRoles, and the comment there about the capability bit it
        // stands in for. Splitting HERE is what keeps every downstream surface
        // honest: ModelSelectors is the only list a picker appears on, so a
        // transport left in it would render as a dropdown on every face AND as
        // buttons in the editor, and the dropdown would lose the race with the
        // engine's own state changes.
        var byRole = pars.Where(p => p.HasValueLabels)
            .ToLookup(p => RigParamRoles.RoleOf(ModuleTypeId, p.Id));
        TransportParams = byRole[RigParamRole.Transport]
            .Select(p => new PedalTransportParamViewModel(_dsp, NodeId, p)).ToList();
        MomentaryParams = byRole[RigParamRole.Momentary]
            .Select(p => new PedalMomentaryParamViewModel(_dsp, NodeId, p)).ToList();
        var choiceIds = byRole[RigParamRole.Choice].Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        var labelled = Params.Where(p => p.HasValueLabels && choiceIds.Contains(p.Id)).ToList();
        KnobParams = Params.Where(p => !p.HasValueLabels).ToList();
        if (skin == PedalSkin.AmpHead && labelled.FirstOrDefault(p => p.Id == "voicing") is { } voicing)
        {
            AmpModelSelector = new PedalModelSelectorViewModel(voicing, PedalModelSelectorKind.AmpVoicing, _loc);
            ModelSelectors = labelled.Where(p => !ReferenceEquals(p, voicing)).Select(MakeSelector).ToList();
        }
        else
        {
            ModelSelectors = labelled.Select(MakeSelector).ToList();
        }

        // The face badge shows the CURRENT model name from the ABI labels (amp
        // voicing / cab model), replacing the old hardcoded 0–6 map. Hidden on
        // the amp face — its model plate already shows the name.
        var badgeParamId = DspModuleVisuals.BadgeParamId(ModuleTypeId);
        _badgeParam = badgeParamId != null ? Params.FirstOrDefault(p => p.Id == badgeParamId) : null;
        _badgeParam?.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PedalParamViewModel.Value)
                    or nameof(PedalParamViewModel.CurrentLabel))
                {
                    OnPropertyChanged(nameof(BadgeText));
                    OnPropertyChanged(nameof(HasBadge));
                    OnPropertyChanged(nameof(BoardSummary));
                    // The cab's file strip prints the built-in IR name while no
                    // user IR is loaded — switching the model re-titles it.
                    foreach (var sp in StringParams)
                        sp.RefreshPlate();
                }
            };

        // A splitter's enabledBranches param changes the board's parallel row
        // count — signal the parent to re-lay the board when it moves.
        if (IsSplitter)
        {
            _enabledBranchesParam = Params.FirstOrDefault(p => p.Id == "enabledBranches");
            _enabledBranchesParam?.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PedalParamViewModel.Value))
                        StructureChanged?.Invoke();
                };
        }

        // ABI v34: only a module the CATALOGUE says is keyable gets the picker.
        // The engine REJECTS a key on any other type, so offering it elsewhere
        // would be a control whose every setting is refused - the "a capability
        // the app cannot honour must never look live" rule.
        SupportsSidechain = type?.SupportsSidechain ?? false;
        _sidechain = SupportsSidechain ? _dsp.GetNodeSidechain(NodeId) : JuceDspSidechainSource.None;
        SidechainOptions = SupportsSidechain
            ? [
                new RigSidechainOption(JuceDspSidechainSource.None, "Rig_Sidechain_None", _loc),
                new RigSidechainOption(JuceDspSidechainSource.SeatInput, "Rig_Sidechain_SeatInput", _loc),
                new RigSidechainOption(JuceDspSidechainSource.SongMix, "Rig_Sidechain_SongMix", _loc)
              ]
            : [];

        RefreshNamStatus();
        RefreshPolledState();

        // Latency moves with a PARAM on several of the 0.9 modules (whammy's
        // lowLatency re-runs its geometry; convreverb's IR sets its block). Only
        // hook it when the engine answered at all - with no engine there is
        // nothing to re-read and nothing to show, and this would otherwise be a
        // subscription per param per card on a board that cannot report.
        if (_latencySamples.HasValue)
        {
            foreach (var param in Params)
            {
                param.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PedalParamViewModel.Value)) RefreshLatency();
                };
            }
        }
    }

    // -- per-node latency (ABI v30) ------------------------------------------

    private int? _latencySamples;

    /// <summary>
    ///     Samples this module adds to the path, or null when the engine cannot
    ///     answer. Null and 0 are deliberately different - 0 means "measured,
    ///     adds nothing" (which is also what a BYPASSED node reports), null
    ///     means "nobody measured".
    /// </summary>
    public int? LatencySamples => _latencySamples;

    /// <summary>The readout is HIDDEN at zero and when unmeasured: most modules
    /// are 0, and a row of "Latency: 0 smp" on every pedal is noise that buries
    /// the seven that are not.</summary>
    public bool HasLatency => _latencySamples is > 0;

    /// <summary>"Latency: 2048 smp / 42.7 ms" - both units, because samples is
    /// what the engine compensates in and milliseconds is what a player feels.
    /// The ms figure needs the DEVICE rate, so it reads 0.0 when no engine is
    /// open rather than inventing one.</summary>
    public string LatencyText
    {
        get
        {
            if (_latencySamples is not { } samples || samples <= 0) return string.Empty;
            var rate = _audio?.ActiveSampleRate ?? 0;
            var ms = rate > 0 ? samples * 1000.0 / rate : 0.0;
            return _loc.Format("Rig_LatencyLabel",
                samples, ms.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));
        }
    }

    /// <summary>Re-reads the node's latency from the chain. No timer: called at
    /// construction, on every board sync and whenever one of this node's params
    /// moves - the same "refresh on the beat something changed" shape the neural
    /// amp's status badge and the node meters already use.</summary>
    public void RefreshLatency()
    {
        var next = _dsp.GetNodeLatencySamples(NodeId);
        if (next == _latencySamples) return;
        _latencySamples = next;
        OnPropertyChanged(nameof(LatencySamples));
        OnPropertyChanged(nameof(HasLatency));
        OnPropertyChanged(nameof(LatencyText));
    }

    // -- the board meter -----------------------------------------------

    private double _boardOutputDb = double.NegativeInfinity;
    private bool _hasBoardMeter;

    /// <summary>The level leaving this unit (output peak, dBFS), pushed by
    /// <see cref="RigBoardMetersViewModel" /> on the rig's meter clock.</summary>
    public double BoardOutputDb => _boardOutputDb;

    /// <summary>False while the engine meters nothing for this unit: the card then
    /// draws no meter at all rather than an empty one that looks live.</summary>
    public bool HasBoardMeter => _hasBoardMeter;

    /// <summary>One reading, or null for "no reading".</summary>
    public void SetBoardMeter(double? outputDb)
    {
        var db = outputDb ?? double.NegativeInfinity;
        if (!db.Equals(_boardOutputDb))
        {
            _boardOutputDb = db;
            OnPropertyChanged(nameof(BoardOutputDb));
        }
        if (_hasBoardMeter == outputDb.HasValue) return;
        _hasBoardMeter = outputDb.HasValue;
        OnPropertyChanged(nameof(HasBoardMeter));
    }

    /// <summary>Everything on this card that is POLLED rather than pushed: the
    /// latency, and any transport whose state the engine moves on its own.</summary>
    public void RefreshPolledState()
    {
        RefreshLatency();
        foreach (var transport in TransportParams) transport.Refresh();
    }

    // -- the sidechain key (ABI v34) -----------------------------------------

    private JuceDspSidechainSource _sidechain;

    /// <summary>True when the catalogue says this module type accepts an
    /// external detector key. The picker is offered ONLY here.</summary>
    public bool SupportsSidechain { get; }

    /// <summary>The three sources as pickable rows; EMPTY on a module that
    /// cannot be keyed, so a face that binds the list unconditionally shows
    /// nothing rather than a dead control.</summary>
    public IReadOnlyList<RigSidechainOption> SidechainOptions { get; } = [];

    /// <summary>
    ///     What this node's detector listens to. The setter pushes natively
    ///     FIRST and only advances on success, so a rejected key leaves the
    ///     picker on the source that is really running rather than on the one
    ///     the user asked for.
    /// </summary>
    public JuceDspSidechainSource SidechainSource
    {
        get => _sidechain;
        set
        {
            if (_sidechain == value) return;
            if (!SupportsSidechain || !_dsp.SetNodeSidechain(NodeId, value))
            {
                // Re-announce so a bound picker follows the engine back.
                OnPropertyChanged(nameof(SidechainSource));
                OnPropertyChanged(nameof(SelectedSidechainOption));
                return;
            }
            _sidechain = value;
            OnPropertyChanged(nameof(SidechainSource));
            OnPropertyChanged(nameof(SelectedSidechainOption));
        }
    }

    /// <summary>The selected row, for a <c>ComboBox</c> that binds items rather
    /// than an enum.</summary>
    public RigSidechainOption? SelectedSidechainOption
    {
        get => SidechainOptions.FirstOrDefault(o => o.Source == _sidechain);
        set { if (value is not null) SidechainSource = value.Source; }
    }

    private readonly PedalParamViewModel? _enabledBranchesParam;

    /// <summary>Raised when a change on THIS node alters the board's split layout
    /// (a splitter/merger bypass toggle, or a splitter's <c>enabledBranches</c>).
    /// The parent <see cref="PedalboardViewModel"/> re-lays the board on it.</summary>
    public event Action? StructureChanged;

    /// <summary>Raised after a string param on THIS node was pushed natively (a
    /// picked capture / IR file, saved lyrics). On a neural amp the parent uses it
    /// to arm the bounded status poll — the native capture load is ASYNCHRONOUS, so
    /// the badge read taken at push time always sees "not loaded".</summary>
    public event Action? StringCommitted;

    private void OnStringCommitted()
    {
        RefreshNamStatus();       // cheap + immediate (a re-pick of the SAME file is instant)
        StringCommitted?.Invoke();
    }

    /// <summary>
    ///     Whether the incremental <c>Sync</c> may keep this card for
    ///     <paramref name="node" /> instead of rebuilding it. Same id is the
    ///     caller's premise; this rejects the two things a MIRROR cannot fix
    ///     because they are resolved at construction: a different module type
    ///     (an engine rebuild can re-number a fresh chain from the same ids)
    ///     and a neural amp whose capture changed — its SKIN (amp head vs pedal
    ///     chassis) and faceplate identity key off the loaded file.
    /// </summary>
    internal bool CanMirror(DspNode node)
    {
        if (!string.Equals(ModuleTypeId, node.ModuleTypeId, StringComparison.Ordinal)) return false;
        if (!IsNeuralAmp) return true;

        var current = StringParams.FirstOrDefault(sp => sp.Id == "modelPath")?.Value ?? string.Empty;
        var incoming = node.StringValues.TryGetValue("modelPath", out var s) ? s : string.Empty;
        return string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Pushes the service's current state for this node onto the card
    ///     WITHOUT re-pushing anything to native — a preset apply writes
    ///     params/bypass/branch through the chain service directly, so a
    ///     surviving card's knobs would otherwise keep showing the old tone.
    ///     The reuse half of the incremental <c>Sync</c>.
    /// </summary>
    internal void MirrorFrom(DspNode node)
    {
        if (Bypassed != node.Bypassed)
        {
            _suppress = true;
            Bypassed = node.Bypassed;
            _suppress = false;
        }

        if (Branch != node.Branch)
        {
            Branch = node.Branch;
            OnPropertyChanged(nameof(Branch));
        }

        foreach (var p in Params)
        {
            if (node.Values.TryGetValue(p.Id, out var v))
                p.MirrorValue(v);
        }

        foreach (var sp in StringParams)
        {
            // Refresh the library list FIRST (a capture may have been
            // downloaded since this card was built), then mirror the value so
            // its row is found in the fresh list.
            sp.RefreshLibraryChoices();
            sp.MirrorValue(node.StringValues.TryGetValue(sp.Id, out var s) ? s : string.Empty);
        }

        if (IsSplitter) OnPropertyChanged(nameof(EnabledBranches));

        // The board's sync IS the poll beat for everything the ENGINE owns: the
        // latency (which moves with the device rate and with bypass) and a
        // transport the engine drove itself.
        RefreshPolledState();
    }

    /// <summary>The node's split/parallel branch tag (ABI v23), 0..3 (0 = branch
    /// 1). Mirrors the native tag; meaningful only inside a split region.</summary>
    public int Branch { get; private set; }

    /// <summary>True for the region-opening <c>splitter</c> routing node.</summary>
    public bool IsSplitter { get; }

    /// <summary>True for the region-closing <c>merger</c> routing node.</summary>
    public bool IsMerger { get; }

    /// <summary>True for either routing node — rendered as a full-height junction
    /// spanning the region's rows.</summary>
    public bool IsJunction => IsSplitter || IsMerger;

    /// <summary>The splitter's enabled branch count (its <c>enabledBranches</c>
    /// param, clamped 2..3); 2 for non-splitters. Feeds the board row count.</summary>
    public int EnabledBranches => IsSplitter && _enabledBranchesParam != null
        ? Math.Clamp((int)Math.Round(_enabledBranchesParam.Value), 2, 3)
        : 2;

    /// <summary>Re-widths the splitter's parallel region (2..3 branches) — the
    /// chain-builder canvas's routing-mode toolbar. Writes the native
    /// <c>enabledBranches</c> param, whose change raises
    /// <see cref="StructureChanged"/> so the board + canvas re-lay. No-op on a
    /// non-splitter.</summary>
    internal bool SetEnabledBranches(int branches)
    {
        if (!IsSplitter || _enabledBranchesParam == null) return false;
        _enabledBranchesParam.Value = Math.Clamp(branches, 2, 3);
        OnPropertyChanged(nameof(EnabledBranches));
        return true;
    }

    /// <summary>Parameter count — the chain-builder canvas's gear thumbnail draws
    /// this many mini knobs.</summary>
    public int ParamCount => Params.Count;

    /// <summary>Applies a branch tag natively and mirrors it locally on success —
    /// used by the parent's drop-to-row machinery. Returns false on native reject.</summary>
    internal bool ApplyBranch(int branch)
    {
        if (!_dsp.SetNodeBranch(NodeId, branch)) return false;
        Branch = branch;
        OnPropertyChanged(nameof(Branch));
        return true;
    }

    private readonly PedalParamViewModel? _badgeParam;

    // ---- neural-amp (namamp) status: model-loaded + 48 kHz rate ------------------

    /// <summary>True for the native neural-amp module — gates the status badge.</summary>
    public bool IsNeuralAmp { get; }

    private float? _namLoaded;
    private float? _namRateMismatch;
    private float? _namResampling;

    /// <summary>Re-reads the poll-only <c>loaded</c>/<c>rateMismatch</c>/
    /// <c>resampling</c> params from the service and refreshes the badge. Called
    /// at Sync (the pedalboard's natural rebuild) — no timer.</summary>
    public void RefreshNamStatus()
    {
        if (!IsNeuralAmp) return;
        _namLoaded = _dsp.GetParam(NodeId, "loaded");
        _namRateMismatch = _dsp.GetParam(NodeId, "rateMismatch");
        _namResampling = _dsp.GetParam(NodeId, "resampling");
        OnPropertyChanged(nameof(NamModelLoaded));
        OnPropertyChanged(nameof(NamModelMissing));
        OnPropertyChanged(nameof(NamRateMismatch));
        OnPropertyChanged(nameof(NamResampling));
        OnPropertyChanged(nameof(NamHasWarning));
        OnPropertyChanged(nameof(NamStatusText));
    }

    /// <summary>A capture is loaded and active on the node.</summary>
    public bool NamModelLoaded => IsNeuralAmp && _namLoaded is 1f;

    /// <summary>The node has no model loaded yet (so it passes the signal dry —
    /// "silence is never mysterious").</summary>
    public bool NamModelMissing => IsNeuralAmp && _namLoaded is not 1f;

    /// <summary>The module cannot run the capture at all. This is NOT
    /// the ordinary "device is at 44.1 kHz" case — the module resamples around the
    /// capture — it only means the converters could not be built (a capture
    /// reporting a nonsense rate) or a device rate change is still in flight.</summary>
    public bool NamRateMismatch => IsNeuralAmp && _namRateMismatch is 1f;

    /// <summary>The rate converters are in the path (device rate ≠ capture rate).
    /// Informational, NOT a warning: the amp is heard, it just costs ~1.4 ms.</summary>
    public bool NamResampling => IsNeuralAmp && _namResampling is 1f;

    /// <summary>Either problem — drives the warning styling on the badge.
    /// <see cref="NamResampling"/> is deliberately NOT one of them.</summary>
    public bool NamHasWarning => NamModelMissing || NamRateMismatch;

    /// <summary>Localized one-line status for the amp face: the rate failure wins,
    /// then the no-model hint, then the informational "resampled" note, else
    /// "loaded".</summary>
    public string NamStatusText
    {
        get
        {
            if (!IsNeuralAmp) return string.Empty;
            if (NamRateMismatch) return _loc.Format("Nam_RateMismatchBadge", EngineKhz());
            if (NamModelMissing) return _loc["Nam_NoModelBadge"];
            if (NamResampling) return _loc.Format("Nam_ResampledBadge", EngineKhz());
            return _loc["Nam_ModelLoadedBadge"];
        }
    }

    private string EngineKhz()
    {
        var hz = _audio?.ActiveSampleRate ?? 0;
        return hz > 0 ? (hz / 1000.0).ToString("0.#") : "?";
    }

    /// <summary>The current model NAME on the amp/cab face ("Plexi 100",
    /// "4x12 Scooped", …), read live from the ABI value labels (replaces the
    /// old hardcoded 0–6 map). Empty for modules without a labelled badge param.</summary>
    public string BadgeText => _badgeParam?.CurrentLabel ?? string.Empty;

    /// <summary>The board card's second line in series mode: "Bypassed", else the
    /// model name, else the first knob and its value ("Gain 6.5").</summary>
    public string BoardSummary =>
        Bypassed ? _loc["Rig_Unit_Bypassed"]
        : BadgeText.Length > 0 ? BadgeText
        : KnobParams.Count > 0 ? $"{KnobParams[0].DisplayName} {KnobParams[0].DisplayText}"
        : string.Empty;

    /// <summary>The badge shows only when there's a model name AND the amp's own
    /// model plate isn't already showing it (the amp plate wins).</summary>
    public bool HasBadge => BadgeText.Length > 0 && AmpModelSelector == null;

    private PedalModelSelectorViewModel MakeSelector(PedalParamViewModel p)
    {
        var kind = ModuleTypeId == "cab" && p.Id == "model"
            ? PedalModelSelectorKind.CabModel
            : PedalModelSelectorKind.Flat;
        return new PedalModelSelectorViewModel(p, kind, _loc);
    }

    /// <summary>Params rendered as knobs (the labelled model-picker params are
    /// split out into <see cref="ModelSelectors"/> / <see cref="AmpModelSelector"/>).</summary>
    public IReadOnlyList<PedalParamViewModel> KnobParams { get; } = [];

    /// <summary>False for a module with only labelled/string params — the
    /// stompbox then skips the (empty) control plate entirely.</summary>
    public bool HasKnobParams => KnobParams.Count > 0;

    /// <summary>Compact selectors for the labelled (named-discrete) params on
    /// non-amp skins: cab/drive <c>model</c>, but also every named choice and
    /// two-state switch — tremolo <c>shape</c>, wah <c>mode</c>, swell <c>mode</c>,
    /// whammy <c>formant</c>, freeze <c>capture</c>, merger <c>mode</c>…
    /// <b>EVERY skin must render this list.</b> It is the only surface a labelled
    /// param appears on (<see cref="KnobParams"/> and <see cref="WahKnobParams"/>
    /// both exclude labelled params), so a skin that omits it makes those params
    /// invisible and unreachable — which is exactly what happened to the swell
    /// pedal's Auto-Swell/Volume switch on the treadle face.</summary>
    public IReadOnlyList<PedalModelSelectorViewModel> ModelSelectors { get; } = [];

    /// <summary>Labelled params the <see cref="RigParamRoles"/> table calls a
    /// TRANSPORT (the looper's Stop/Record/Play/Overdub). Rendered as buttons by
    /// the module editor, never as a picker - see
    /// <see cref="PedalTransportParamViewModel"/>.</summary>
    public IReadOnlyList<PedalTransportParamViewModel> TransportParams { get; } = [];

    public bool HasTransportParams => TransportParams.Count > 0;

    /// <summary>Labelled params the table calls a one-shot TRIGGER (the looper's
    /// undo). Write-only buttons - see
    /// <see cref="PedalMomentaryParamViewModel"/>.</summary>
    public IReadOnlyList<PedalMomentaryParamViewModel> MomentaryParams { get; } = [];

    public bool HasMomentaryParams => MomentaryParams.Count > 0;

    public bool HasModelSelectors => ModelSelectors.Count > 0;

    /// <summary>True when the node has ANY interactive param surface (knobs or
    /// selectors). The junction card's "⋯" params flyout gates on this — gating on
    /// knobs alone hid the merger's Average/Sum picker the moment it was labelled.</summary>
    public bool HasParamControls => HasKnobParams || HasModelSelectors;

    /// <summary>The amp face's prominent voicing model plate (grouped popup over
    /// all 24 named amps); null for every non-amp module.</summary>
    public PedalModelSelectorViewModel? AmpModelSelector { get; }

    /// <summary>True when the amp face should show the model plate (v24 labels
    /// present); a pre-v24 catalogue falls back to the module display name.</summary>
    public bool HasAmpModelSelector => AmpModelSelector != null;

    public int NodeId { get; }
    public string ModuleTypeId { get; }
    public string DisplayName { get; }
    public ObservableCollection<PedalParamViewModel> Params { get; }

    /// <summary>String-valued params (ABI v22): file-picker rows + the miku
    /// phrases lyric editor. Empty for the common module.</summary>
    public ObservableCollection<PedalStringParamViewModel> StringParams { get; }
    public bool HasStringParams { get; }

    /// <summary>Visual family (drives the enclosure colour + amp-vs-stompbox skin).</summary>
    public DspModuleCategory Category { get; }

    /// <summary>
    ///     The same unit on the FIRE layer: one of the eight fixed gear-category
    ///     identities (§6.8). Folded from <see cref="Category" /> by the pure
    ///     <see cref="FireRigCategory" /> so the two skins can never disagree
    ///     about what a unit IS.
    /// </summary>
    public FireUnitCategory FireCategory => FireRigCategory.For(Category);

    /// <summary>The unit's category as the classic rack prints it ("Drive",
    /// "Modulation") — the gear rail's own label table.</summary>
    public string CategoryLabel => GearRailViewModel.CategoryLabel(_loc, Category);

    /// <summary>
    ///     The IDENTITY label the Fire card's kicker prints above the unit name
    ///     ("DRIVE", "MOD"). It is the eight-identity label, not
    ///     <see cref="CategoryLabel" />'s eleven-family one, so the kicker on a
    ///     card and the heading over that unit's gear-list group are the same
    ///     word — and it is mono/uppercase in every locale because the resource
    ///     is authored that way rather than upper-cased at render time (Turkish
    ///     dotless-i is the classic way that goes wrong).
    /// </summary>
    public string FireCategoryLabel =>
        _loc[FireRigCategory.LabelKeyFor(FireCategory)];

    /// <summary>The resource key of that identity's fixed colour — markup binds
    /// it through <c>FireTokenBrushConverter</c>, so a hue never travels as a
    /// brush on a view model.</summary>
    public string FireTokenKey => FireUnitState.TokenKeyFor(FireCategory);

    /// <summary>The knob hardware family this pedal's dials render as
    /// (chicken-head for amps, skirted 0–10 for drives, cream for modulation).</summary>
    public KnobStyle KnobStyle { get; }

    /// <summary>The enclosure skin this node renders with (specialized faces for
    /// amp/cab/graphiceq/wah; everything else the generic stompbox).</summary>
    public PedalSkin Skin { get; }

    public bool IsAmpHead => Skin == PedalSkin.AmpHead;

    /// <summary>The NEURAL amp head (<c>namamp</c>): its own wide chassis whose
    /// faceplate names the loaded CAPTURE rather than a built-in voicing, and
    /// which carries the capture-status pill. Distinct from
    /// <see cref="IsAmpHead"/> because the two faces show different things — the
    /// legacy amp has a voicing plate and no capture, this one is the reverse —
    /// and one face bound to both flags could show neither honestly.</summary>
    public bool IsNeuralAmpHead => Skin == PedalSkin.NeuralAmp;

    /// <summary>A <c>namamp</c> whose loaded capture is of a PEDAL: the portrait
    /// die-cast chassis, so a boost in front of an amp capture reads as a pedal
    /// on the floor instead of a second head. Its face still names the capture —
    /// that is what makes it visibly a NEURAL pedal rather than an algorithmic
    /// one standing next to it.</summary>
    public bool IsNeuralPedal => Skin == PedalSkin.NeuralPedal;

    /// <summary>The name a NEURAL PEDAL prints on its face: the loaded capture's
    /// own model name ("TS9 Screamer"), not the module's catalogue name, which
    /// for every capture-backed node is the same string ("Neural Amp"). Falls
    /// back to the module name when nothing is loaded.</summary>
    public string NeuralFaceName
    {
        get
        {
            var title = StringParams.FirstOrDefault(sp => sp.Id == "modelPath")?.CaptureTitle;
            return string.IsNullOrWhiteSpace(title) ? DisplayName : title;
        }
    }

    /// <summary>
    ///     True when a namamp's loaded capture is a pedal. Reads the local
    ///     library manifest's <c>gearType</c>; anything else — an amp capture, an
    ///     unknown file, no capture at all, an unreadable library — is NOT a
    ///     pedal, so the default stays the amp head it has always been.
    /// </summary>
    private static bool CaptureIsPedal(
        IEnumerable<PedalStringParamViewModel> stringParams,
        Func<string, NamCaptureManifest?>? captureLookup)
    {
        if (captureLookup is null) return false;
        var path = stringParams.FirstOrDefault(sp => sp.Id == "modelPath")?.Value;
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            return string.Equals(captureLookup(path)?.GearType, "pedal", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool IsCabinet => Skin == PedalSkin.Cabinet;
    public bool IsGraphicEq => Skin == PedalSkin.GraphicEq;
    public bool IsWah => Skin == PedalSkin.Wah;

    /// <summary>The routing junction card (splitter/merger): a COMPACT full-height
    /// patchbay card spanning the branch rows — its params live in a small popup, so
    /// it never renders as a portrait stompbox.</summary>
    public bool IsJunctionCard => Skin == PedalSkin.Junction;

    /// <summary>The junction card's header: "Split" / "Merge".</summary>
    public string JunctionTitle => _loc[
        IsSplitter ? "Pedalboard_Junction_Split" : "Pedalboard_Junction_Merge"];

    /// <summary>The generic knob-row fallback that covers any future module —
    /// and the neural PEDAL, which is the same portrait die-cast chassis with a
    /// capture plate on it.</summary>
    public bool IsStompbox => Skin is PedalSkin.Stompbox or PedalSkin.NeuralPedal;

    /// <summary>The wah's rocker param ("position"); null on other skins.</summary>
    public PedalParamViewModel? WahTreadleParam { get; }

    /// <summary>The wah's remaining params, rendered as a compact knob row
    /// under the treadle. Empty on other skins.</summary>
    public IReadOnlyList<PedalParamViewModel> WahKnobParams { get; }

    /// <summary>Neon accent colour for the knob control's arcs + LED.</summary>
    public Color Accent { get; }

    /// <summary>The enclosure's PAINT colour — the seam the custom-drawn
    /// <c>PedalEnclosure</c> derives its whole material stack from.</summary>
    public Color BodyColor { get; }

    public IBrush AccentBrush { get; }
    public IBrush BodyBrush { get; }
    public IBrush EdgeBrush { get; }

    /// <summary>Width of the stompbox's knob block: up to three dials across the
    /// top of the face (a real 1590BB row), wrapping below that. A one-knob pedal
    /// stays narrow and TALL, like the hardware.</summary>
    public double KnobRowWidth => Math.Clamp(KnobParams.Count, 1, 3) * 72.0 + 2;

    /// <summary>Screen-printed house brand mark on the enclosure face — a
    /// stylised logotype (a proper noun, deliberately NOT localized), the model
    /// name below it does the talking.</summary>
    public string BrandWordmark => IsNeuralAmp
        // The neural head is our chassis running somebody else's capture, and it
        // stands next to the legacy amp head on the same board wearing the same
        // tolex — so the silkscreen is what tells them apart at a glance.
        ? "DOTCHARTER NEURAL"
        : Category switch
        {
            DspModuleCategory.Amp => "DOTCHARTER AMPLIFICATION",
            DspModuleCategory.Cab => "DC CABINET CO.",
            _ => "DOTCHARTER"
        };

    /// <summary>Italic on the "script" cleans treatment; upright otherwise.</summary>
    public FontStyle NameFontStyle =>
        DspModuleVisuals.TypefaceFor(Category) == DspModuleVisuals.RigTypeface.Script
            ? FontStyle.Italic
            : FontStyle.Normal;

    /// <summary>Per-family letter spacing for the screen-printed model name
    /// (widest on the block-industrial amp/drive stamp).</summary>
    public double NameLetterSpacing =>
        DspModuleVisuals.LetterSpacingFor(DspModuleVisuals.TypefaceFor(Category));

    private static Color Shade(Color c, double f) => Color.FromRgb(
        (byte)Math.Clamp(c.R * f, 0, 255),
        (byte)Math.Clamp(c.G * f, 0, 255),
        (byte)Math.Clamp(c.B * f, 0, 255));

    private static IBrush BuildBodyBrush(Color body)
    {
        // A vertical brushed-metal gradient: a lift at the top, the body colour
        // through the middle, a shadow at the foot.
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Shade(body, 1.35), 0),
                new GradientStop(body, 0.5),
                new GradientStop(Shade(body, 0.7), 1)
            }
        };
    }

    /// <summary>Amp/cab chassis: a deeper "tolex" wrap — darker overall than
    /// the stompbox metal, a tight top sheen band (light catching the vinyl
    /// grain), a long sink through the body and a heavy foot shadow. All
    /// programmatic gradient stops, no bitmaps (RigBoardTexture rule).</summary>
    private static IBrush BuildTolexBrush(Color body)
    {
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Shade(body, 1.28), 0),
                new GradientStop(Shade(body, 0.96), 0.06), // tight vinyl sheen band
                new GradientStop(Shade(body, 1.06), 0.16),
                new GradientStop(Shade(body, 0.88), 0.55),
                new GradientStop(Shade(body, 0.72), 0.86),
                new GradientStop(Shade(body, 0.52), 1)      // heavy foot shadow
            }
        };
    }

    /// <summary>Board highlight driven by the signal-flow strip (see
    /// <see cref="PedalboardViewModel.SelectNodeAt"/>).</summary>
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoardSummary))]
    private bool _bypassed;

    /// <summary>Flips this module's bypass — the slot strip's power dot (a
    /// two-way binding would need a negating converter for the "lit = ON" dot,
    /// and this keeps the one write path through the generated
    /// <c>OnBypassedChanged</c> partial).</summary>
    [RelayCommand]
    private void ToggleBypass() => Bypassed = !Bypassed;

    partial void OnBypassedChanged(bool value)
    {
        if (_suppress) return;
        if (_dsp.SetBypass(NodeId, value))
        {
            // A bypassed splitter collapses the region; a bypassed merger auto-
            // merges it — either re-lays the board.
            if (IsJunction) StructureChanged?.Invoke();
            return;
        }

        // Service rejected the change (native unavailable) — revert so the
        // toggle never lies about the real chain state.
        _suppress = true;
        Bypassed = !value;
        _suppress = false;
    }

    /// <summary>
    ///     Names <c>rsamp</c>'s <c>k1</c>..<c>k10</c> after the knobs of Rocksmith amp
    ///     <paramref name="model" /> (<see cref="RsAmpKnobs.NamesFor" />); a knob the amp
    ///     does not have keeps its catalogue name ("Knob 8") and goes inactive. Without
    ///     the amp chains (<see cref="RsAmpKnobs.Available" />, no Rocksmith install) every
    ///     knob goes inactive with <paramref name="needsInstallHint" /> as its reason.
    /// </summary>
    internal static void RelabelRsAmpKnobs(IEnumerable<PedalParamViewModel> pars, float model, string needsInstallHint)
    {
        var available = RsAmpKnobs.Available;
        var names = RsAmpKnobs.NamesFor((int)MathF.Round(model));
        var ids = RsAmpKnobs.ParamIds.ToList();
        foreach (var p in pars)
        {
            var i = ids.IndexOf(p.Id);
            if (i < 0) continue;
            var used = i < names.Count;
            p.DisplayName = used ? names[i] : "Knob " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            p.IsControlActive = used;
            p.InactiveHint = available ? null : needsInstallHint;
        }
    }
}

/// <summary>
///     One row of the sidechain picker: a wrapper enum value and the resx key
///     naming it for a human.
///     <para>
///         The VALUE is <see cref="JuceDspSidechainSource" /> itself and not a
///         managed copy of it — its numbers are ABI and are persisted, so a
///         second vocabulary here would be a drift the engine reports as a
///         rejected key. The LABEL is localized, because "Song mix" is a
///         sentence for a player and not an engine proper noun (unlike a value
///         label, which comes off the ABI and is never duplicated into resx).
///     </para>
/// </summary>
public sealed class RigSidechainOption
{
    private readonly ILocalizationService _loc;

    internal RigSidechainOption(JuceDspSidechainSource source, string labelKey, ILocalizationService localization)
    {
        Source = source;
        LabelKey = labelKey;
        _loc = localization;
    }

    /// <summary>The wrapper's enum value, passed straight to the service.</summary>
    public JuceDspSidechainSource Source { get; }

    /// <summary>The resx key for this source's human name.</summary>
    public string LabelKey { get; }

    /// <summary>The localized name ("Own input", "Dry input", "Song mix").</summary>
    public string Label => _loc[LabelKey];

    public override string ToString() => Label;
}
