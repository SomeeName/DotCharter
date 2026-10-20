// RigDockUnit: the five SPECIALISED editor families' projections - graphic EQ faders, parametric bands, the treadle, the vocal device and the dynamics rack.
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The projections the five SPECIALISED editor families render — the graphic
///     equaliser's fader bank, the parametric bands, the treadle, the vocal
///     device and the dynamics rack.
///
///     <para>
///         <b>Nothing here is a parameter table.</b> Every list is looked up BY
///         ID against the live <see cref="PedalNodeViewModel" />
///         (<c>KnobParams</c> / <c>ModelSelectors</c> / <c>StringParams</c>), so
///         a layout can only ever ARRANGE controls the module catalogue already
///         declared. A knob a layout does not name lands in
///         <see cref="MoreKnobs" /> and a picker in <see cref="MoreChoices" />,
///         which is what makes "no parameter is dropped" structural rather than
///         a promise — <c>UI/RigFamilyEditorTests</c> asserts the union.
///     </para>
/// </summary>
public sealed partial class RigDockUnit
{
    private bool _familyBuilt;

    private IReadOnlyList<PedalParamViewModel> _graphicEqBands = [];
    private PedalParamViewModel? _graphicEqLevel;
    private IReadOnlyList<RigParamSection> _parametricSections = [];
    private IReadOnlyList<RigParamSection> _dynamicsSections = [];
    private PedalParamViewModel? _expressionPosition;
    private IReadOnlyList<PedalModelSelectorViewModel> _expressionChoices = [];
    private IReadOnlyList<PedalParamViewModel> _expressionKnobs = [];
    private IReadOnlyList<PedalParamViewModel> _vocalMainKnobs = [];
    private IReadOnlyList<PedalParamViewModel> _vocalArticulation = [];
    private PedalModelSelectorViewModel? _vocalPattern;
    private PedalModelSelectorViewModel? _vocalOctave;
    private IReadOnlyList<PedalStringParamViewModel> _lyricParams = [];
    private IReadOnlyList<PedalParamViewModel> _moreKnobs = [];
    private IReadOnlyList<PedalModelSelectorViewModel> _moreChoices = [];

    // ── The graphic equaliser ────────────────────────────────────────────────

    /// <summary>The ten band gains, low to high — one vertical fader each.</summary>
    public IReadOnlyList<PedalParamViewModel> GraphicEqBands { get { EnsureFamily(); return _graphicEqBands; } }

    /// <summary>The master level knob beside the fader bank.</summary>
    public PedalParamViewModel? GraphicEqLevel { get { EnsureFamily(); return _graphicEqLevel; } }

    public bool HasGraphicEqLevel => GraphicEqLevel != null;

    // ── The parametric equaliser and the dynamics rack ───────────────────────

    /// <summary>LOW · MID · HIGH · OUTPUT, each over its own params.</summary>
    public IReadOnlyList<RigParamSection> ParametricSections { get { EnsureFamily(); return _parametricSections; } }

    /// <summary>The rack's sections, which differ per dynamics module.</summary>
    public IReadOnlyList<RigParamSection> DynamicsSections { get { EnsureFamily(); return _dynamicsSections; } }

    // ── The expression treadle ───────────────────────────────────────────────

    /// <summary>The treadle's own param. The rocker and the vertical fader are
    /// two views of THIS one value, never two values.</summary>
    public PedalParamViewModel? ExpressionPosition { get { EnsureFamily(); return _expressionPosition; } }

    public bool HasExpressionPosition => ExpressionPosition != null;

    /// <summary>The labelled params (wah <c>mode</c>, whammy <c>formant</c> /
    /// <c>lowLatency</c>, swell <c>mode</c>) as pickers.</summary>
    public IReadOnlyList<PedalModelSelectorViewModel> ExpressionChoices { get { EnsureFamily(); return _expressionChoices; } }

    public bool HasExpressionChoices => ExpressionChoices.Count > 0;

    /// <summary>Everything else the pedal has, as knobs.</summary>
    public IReadOnlyList<PedalParamViewModel> ExpressionKnobs { get { EnsureFamily(); return _expressionKnobs; } }

    // ── The vocal device ─────────────────────────────────────────────────────

    /// <summary>The two big knobs on the device face: mix and character.</summary>
    public IReadOnlyList<PedalParamViewModel> VocalMainKnobs { get { EnsureFamily(); return _vocalMainKnobs; } }

    /// <summary>The stomp's articulation controls; empty on the plain synth,
    /// which has none of them.</summary>
    public IReadOnlyList<PedalParamViewModel> VocalArticulation { get { EnsureFamily(); return _vocalArticulation; } }

    public bool HasVocalArticulation => VocalArticulation.Count > 0;

    public PedalModelSelectorViewModel? VocalPattern { get { EnsureFamily(); return _vocalPattern; } }
    public PedalModelSelectorViewModel? VocalOctave { get { EnsureFamily(); return _vocalOctave; } }
    public bool HasVocalPattern => VocalPattern != null;
    public bool HasVocalOctave => VocalOctave != null;

    /// <summary>
    ///     EVERY free-text string the module carries, each opened on its stored
    ///     value. <see cref="LyricsParam" /> is the first of them — the generic
    ///     editor renders one field, and the stomp has THREE phrase slots, so a
    ///     single-field editor silently loses two thirds of its lyrics.
    /// </summary>
    public IReadOnlyList<PedalStringParamViewModel> LyricParams { get { EnsureFamily(); return _lyricParams; } }

    public bool HasLyricParams => LyricParams.Count > 0;

    // ── The leftovers, so nothing is unreachable ─────────────────────────────

    /// <summary>Knobs this unit has that its family layout does not name. The
    /// view draws them in a trailing grid: a module gaining a param in the
    /// native catalogue tomorrow stays fully editable with nothing here to
    /// update.</summary>
    public IReadOnlyList<PedalParamViewModel> MoreKnobs { get { EnsureFamily(); return _moreKnobs; } }

    public bool HasMoreKnobs => MoreKnobs.Count > 0;

    /// <summary>The same, for labelled params.</summary>
    public IReadOnlyList<PedalModelSelectorViewModel> MoreChoices { get { EnsureFamily(); return _moreChoices; } }

    public bool HasMoreChoices => MoreChoices.Count > 0;

    // ── The words each family prints ─────────────────────────────────────────

    /// <summary>A section / eyebrow caption from its resource key. One place, so
    /// a family view never carries display text of its own.</summary>
    public string SectionLabel(string key) => _loc[key];

    /// <summary>SWEEP &amp; ENVELOPE · HEEL / TOE &amp; VOICING · ENVELOPE.</summary>
    public string ExpressionEyebrow => SectionLabel(Node.ModuleTypeId switch
    {
        "wah" => "Rig_Editor_Expr_Eyebrow_Wah",
        "whammy" => "Rig_Editor_Expr_Eyebrow_Whammy",
        _ => "Rig_Editor_Sec_Envelope"
    });

    /// <summary>The word screen-printed on the treadle's rubber plate.</summary>
    public string ExpressionPlate => SectionLabel(Node.ModuleTypeId switch
    {
        "wah" => "Rig_Editor_Expr_Plate_Wah",
        "whammy" => "Rig_Editor_Expr_Plate_Whammy",
        _ => "Rig_Editor_Expr_Plate_Swell"
    });

    /// <summary>The treadle's accent TOKEN KEY — markup resolves it through
    /// <c>FireTokenBrushConverter</c>, so the hue never travels as a brush on a
    /// view model.</summary>
    public string ExpressionAccentKey =>
        Node.ModuleTypeId == "whammy" ? "FireRigAccentWhammy" : "FireRigAccentWah";

    /// <summary>The vocal device's silkscreen — a proper noun, deliberately not
    /// localized, the same call <c>BrandWordmark</c> makes.</summary>
    public string VocalWordmark => "MIKU";

    /// <summary>VOCAL SYNTH or STOMP, under the wordmark.</summary>
    public string VocalVariant => SectionLabel(
        Node.ModuleTypeId == "mikustomp" ? "Rig_Editor_Vocal_Stomp" : "Rig_Editor_Vocal_Synth");

    /// <summary>COMPRESSOR · NOISE GATE · NOISE REDUCTION, on the rack's foot.</summary>
    public string DynamicsLabel => SectionLabel(Node.ModuleTypeId switch
    {
        "compressor" => "Rig_Editor_Dyn_Compressor",
        "gate" => "Rig_Editor_Dyn_Gate",
        _ => "Rig_Editor_Dyn_NoiseReduction"
    });

    // ── The fold ─────────────────────────────────────────────────────────────

    private void EnsureFamily()
    {
        if (_familyBuilt) return;
        _familyBuilt = true;

        // Every free-text param opens on its stored value, not just the first.
        _lyricParams = Node.StringParams.Where(p => p.IsLyrics).ToArray();
        foreach (var lyric in _lyricParams) lyric.BeginLyricsEdit();

        var named = new List<PedalParamViewModel>();
        var namedChoices = new List<PedalModelSelectorViewModel>();

        switch (Family)
        {
            case RigEditorFamily.GraphicEq:
                _graphicEqBands = Knobs("band31", "band63", "band125", "band250", "band500",
                    "band1k", "band2k", "band4k", "band8k", "band16k");
                _graphicEqLevel = Knob("level");
                named.AddRange(_graphicEqBands);
                if (_graphicEqLevel != null) named.Add(_graphicEqLevel);
                break;

            case RigEditorFamily.ParametricEq:
                _parametricSections = Sections(
                    ("Rig_Editor_Sec_Low", ["lowFreq", "lowGainDb"]),
                    ("Rig_Editor_Sec_Mid", ["midFreq", "midQ", "midGainDb"]),
                    ("Rig_Editor_Sec_High", ["highFreq", "highGainDb"]),
                    ("Rig_Editor_Sec_Output", ["outputTrimDb"]));
                named.AddRange(_parametricSections.SelectMany(s => s.Knobs));
                break;

            case RigEditorFamily.Expression:
                _expressionPosition = Knob("position");
                _expressionChoices = Node.ModelSelectors.ToArray();
                _expressionKnobs = Node.KnobParams
                    .Where(p => !ReferenceEquals(p, _expressionPosition)).ToArray();
                if (_expressionPosition != null) named.Add(_expressionPosition);
                named.AddRange(_expressionKnobs);
                namedChoices.AddRange(_expressionChoices);
                break;

            case RigEditorFamily.Vocal:
                _vocalMainKnobs = Knobs("mix", "character");
                _vocalArticulation = Knobs("breath", "attackMs", "portamentoMs", "vibrato", "vibratoRateHz");
                _vocalOctave = Choice("octave");
                _vocalPattern = Choice("pattern");
                named.AddRange(_vocalMainKnobs);
                named.AddRange(_vocalArticulation);
                if (_vocalOctave != null) namedChoices.Add(_vocalOctave);
                if (_vocalPattern != null) namedChoices.Add(_vocalPattern);
                break;

            case RigEditorFamily.Dynamics:
                _dynamicsSections = Node.ModuleTypeId switch
                {
                    "compressor" => Sections(
                        ("Rig_Editor_Sec_Detection", ["thresholdDb", "ratio", "detector"]),
                        ("Rig_Editor_Sec_Envelope", ["attackMs", "releaseMs"]),
                        ("Rig_Editor_Sec_Output", ["makeupDb", "kneeDb"])),
                    "gate" => Sections(
                        ("Rig_Editor_Sec_Threshold", ["thresholdDb", "ratio"]),
                        ("Rig_Editor_Sec_Envelope", ["attackMs", "holdMs", "releaseMs"])),
                    _ => Sections(
                        ("Rig_Editor_Sec_Reduction", ["amount", "thresholdDb", "releaseMs"]),
                        ("Rig_Editor_Sec_HumFilter", ["humFreq", "humDepth"]))
                };
                named.AddRange(_dynamicsSections.SelectMany(s => s.Knobs));
                break;
        }

        _moreKnobs = Node.KnobParams.Where(p => !named.Contains(p)).ToArray();
        _moreChoices = Node.ModelSelectors.Where(s => !namedChoices.Contains(s)).ToArray();
    }

    private PedalParamViewModel? Knob(string id) =>
        Node.KnobParams.FirstOrDefault(p => p.Id == id);

    private PedalModelSelectorViewModel? Choice(string id) =>
        Node.ModelSelectors.FirstOrDefault(s => s.ParamId == id);

    private IReadOnlyList<PedalParamViewModel> Knobs(params string[] ids) =>
        ids.Select(Knob).OfType<PedalParamViewModel>().ToArray();

    /// <summary>The named sections, minus any the module has no param for — an
    /// empty heading is a section about nothing.</summary>
    private RigParamSection[] Sections(params (string Key, string[] Ids)[] sections) =>
        sections.Select(s => new RigParamSection(s.Key, Knobs(s.Ids), _loc))
            .Where(s => s.Knobs.Count > 0)
            .ToArray();
}

/// <summary>
///     One headed group of knobs in a rack editor (LOW · ENVELOPE · HUM
///     FILTER…). It owns no parameter: <see cref="Knobs" /> holds the same live
///     <see cref="PedalParamViewModel" /> instances the board card binds.
/// </summary>
public sealed class RigParamSection
{
    private readonly ILocalizationService _loc;

    public RigParamSection(string titleKey, IReadOnlyList<PedalParamViewModel> knobs, ILocalizationService localization)
    {
        TitleKey = titleKey;
        _loc = localization;
        Knobs = knobs;
    }

    public string TitleKey { get; }

    /// <summary>The heading, resolved from the resource key — never a literal
    /// in markup.</summary>
    public string Title => _loc[TitleKey];

    public IReadOnlyList<PedalParamViewModel> Knobs { get; }
}
