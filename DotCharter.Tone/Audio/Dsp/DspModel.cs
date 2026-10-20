namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
/// One tunable parameter of a DSP module, as advertised by the native module
/// catalogue (<c>dca_dsp_module_param_info</c>). Immutable metadata: the UI
/// builds a generic slider from <see cref="Min"/>..<see cref="Max"/> with
/// <see cref="Default"/> as the reset value, so a module needs no bespoke UI.
/// </summary>
public sealed record DspParamInfo(string Id, string DisplayName, float Min, float Max, float Default)
{
    /// <summary>Whether a param id names a frequency: it ENDS in "Hz"/"kHz"
    /// (case-insensitive on the prefix letter only — the native ids are
    /// camelCase, so <c>cutoffHz</c>, <c>freqHz</c>, <c>notchHz</c>,
    /// <c>toneHz</c>). A bare "hz" inside a word does not count, which is why
    /// this is a suffix test and not a <c>Contains</c>. The knob taper and the
    /// tone matcher's search space both read it, so they agree on which knobs
    /// sweep logarithmically.</summary>
    public static bool IsFrequencyId(string? paramId)
    {
        return paramId is { Length: >= 2 } id &&
               (id.EndsWith("Hz", StringComparison.Ordinal) || id.EndsWith("hz", StringComparison.Ordinal));
    }

    /// <summary>Named labels for a discrete-value param (ABI v24): a model
    /// (amp <c>voicing</c>, cab/overdrive/distortion <c>model</c>), a named
    /// choice (tremolo <c>shape</c>, wah <c>mode</c>) or a two-state switch
    /// (delay <c>pingPong</c>, freeze <c>capture</c>). Index <c>i</c> names the
    /// value <c>Min + i</c>. Null for a continuous (numeric) param, so the UI
    /// keeps a knob; non-null ⇒ render a labelled selector instead. Loaded once
    /// with the catalogue; the label STRINGS come from the native ABI and are
    /// never duplicated into resx.</summary>
    public IReadOnlyList<string>? ValueLabels { get; init; }

    /// <summary>Quantization step for an ORDINAL discrete param (ABI v27) — a
    /// count the native core lrounds into an int: chorus <c>voices</c>, phaser
    /// <c>stages</c> (step <b>2</b>: the core forces the allpass count even),
    /// bitcrusher <c>bits</c>/<c>downsample</c>, … 0 = continuous. The knob
    /// snaps to <c>Min + k*Step</c> and shows the integer, so the readout can
    /// never claim "6.5 stages" while the DSP runs 6. Named discrete params
    /// carry <see cref="ValueLabels"/> (a picker) instead of a step.</summary>
    public float Step { get; init; }

    /// <summary>True when the value is a picker (named discrete choice).</summary>
    public bool HasValueLabels => ValueLabels is { Count: > 0 };

    /// <summary>True when the value is an ordinal count (a snapping knob).</summary>
    public bool IsStepped => Step > 0f;
}

/// <summary>
/// One string-valued parameter of a DSP module (ABI v22), as advertised by
/// <c>dca_dsp_module_string_param_info</c>. Unlike <see cref="DspParamInfo"/>
/// there is no numeric range — the value is a UTF-8 string (a file path or free
/// text). <see cref="FileFilter"/> is a ';'-separated wildcard list for a file
/// picker (e.g. <c>"*.nam;*.aidax"</c>) and is empty when the param is free text
/// (e.g. the Miku <c>phrases</c> table), signalling "no picker".
/// </summary>
public sealed record DspStringParamInfo(string Id, string DisplayName, string FileFilter);

/// <summary>
/// A module type in the static catalogue (<c>dca_dsp_module_*</c>): a stable
/// string <see cref="Id"/> (e.g. <c>"overdrive"</c>), a human display name and
/// the ordered list of its parameters. Loaded once at startup.
/// <see cref="StringParams"/> carries the ABI-v22 string-valued params (empty
/// for the vast majority of modules; e.g. <c>cab</c> exposes <c>irPath</c>,
/// <c>namamp</c> exposes <c>modelPath</c>, <c>miku</c> exposes <c>phrases</c>).
/// </summary>
public sealed record DspModuleType(string Id, string DisplayName, IReadOnlyList<DspParamInfo> Params)
{
    /// <summary>The module's string-valued params (ABI v22). Defaults to empty so
    /// pre-v22 catalogue construction stays source-compatible.</summary>
    public IReadOnlyList<DspStringParamInfo> StringParams { get; init; } = [];

    /// <summary>
    /// Whether this module type accepts an external DETECTOR key (ABI v34,
    /// <c>dca_dsp_module_type_flags</c> bit 0 — mirrored straight off
    /// <see cref="JuceSharp.Dsp.JuceDspModuleInfo.SupportsSidechain"/>, never
    /// from a managed guess table). True for <c>compressor</c>, <c>gate</c>,
    /// <c>expander</c>, <c>ducker</c> and <c>vocoder</c> today.
    /// <para>
    /// <b>The UI must offer the sidechain control ONLY where this is true.</b>
    /// The engine REJECTS a key on any other type, so a picker on one would be
    /// a live-looking control whose every setting is refused. False on a native
    /// DLL that predates v34.
    /// </para>
    /// </summary>
    public bool SupportsSidechain { get; init; }
}

/// <summary>
/// A live node in the rig's reorderable chain. <see cref="NodeId"/> is the
/// native-assigned id (stable for the node's lifetime); <see cref="ModuleTypeId"/>
/// links back to the catalogue for parameter metadata. <see cref="Values"/> caches
/// the last value pushed per parameter id. Mutation flows through
/// <see cref="IDspChainService"/> — the managed model mirrors the native chain.
/// </summary>
public sealed class DspNode
{
    private readonly Dictionary<string, float> _values;
    private readonly Dictionary<string, string> _stringValues;

    public DspNode(int nodeId, string moduleTypeId, bool bypassed, IReadOnlyDictionary<string, float> values,
        IReadOnlyDictionary<string, string>? stringValues = null, int branch = 0, IReadOnlyList<RigControlAssignment>? controlAssignments = null,
        JuceSharp.Dsp.JuceDspSidechainSource sidechain = JuceSharp.Dsp.JuceDspSidechainSource.None)
    {
        Sidechain = sidechain;
        NodeId = nodeId;
        ModuleTypeId = moduleTypeId;
        Bypassed = bypassed;
        _values = new Dictionary<string, float>(values, StringComparer.Ordinal);
        _stringValues = stringValues is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(stringValues, StringComparer.Ordinal);
        Branch = branch;
        ControlAssignments = RigControlAssignments.Copy(controlAssignments);
    }

    public int NodeId { get; }
    public string ModuleTypeId { get; }
    public bool Bypassed { get; private set; }
    public IReadOnlyDictionary<string, float> Values => _values;

    /// <summary>The node's split/parallel branch tag (ABI v23): 0 = unassigned
    /// (renders / processes as branch 1), 1..3 = an explicit branch inside a
    /// split region. Inert outside a region; survives move/reorder natively but
    /// NOT remove/re-add (the applier re-pushes it). Mirrors the native tag —
    /// mutated through <see cref="IDspChainService.SetNodeBranch"/>.</summary>
    public int Branch { get; private set; }

    /// <summary>Last string value pushed per string-param id (ABI v22). Mirrors
    /// <see cref="Values"/> for the string surface; empty for the common module
    /// with no string params. The AUTHORITATIVE value lives natively on the
    /// node — this is the managed cache read back when the native read path is
    /// unavailable (engine detached).</summary>
    public IReadOnlyDictionary<string, string> StringValues => _stringValues;

    public IReadOnlyList<RigControlAssignment> ControlAssignments { get; private set; }
    internal void SetControlAssignments(IReadOnlyList<RigControlAssignment> values)
        => ControlAssignments = RigControlAssignments.Copy(values);

    /// <summary>
    ///     What this node's DETECTOR keys off (ABI v34). The WRAPPER's enum is
    ///     used directly and deliberately: the numeric values are part of the
    ///     ABI and are persisted, so a second managed copy of that vocabulary is
    ///     a drift the engine would report as a rejected key.
    ///     <para>
    ///         Only a module whose <see cref="DspModuleType.SupportsSidechain" />
    ///         is true accepts anything but
    ///         <see cref="JuceSharp.Dsp.JuceDspSidechainSource.None" />. Mirrors
    ///         the native value — mutated through
    ///         <see cref="IDspChainService.SetNodeSidechain" />.
    ///     </para>
    /// </summary>
    public JuceSharp.Dsp.JuceDspSidechainSource Sidechain { get; private set; }

    internal void SetSidechain(JuceSharp.Dsp.JuceDspSidechainSource source) => Sidechain = source;

    internal void SetBypassed(bool bypassed) => Bypassed = bypassed;
    internal void SetValue(string paramId, float value) => _values[paramId] = value;
    internal void SetStringValue(string paramId, string value) => _stringValues[paramId] = value ?? string.Empty;
    internal void SetBranch(int branch) => Branch = branch;

    /// <summary>Deep copy used when the service hands a snapshot to the UI so
    /// later native-mirroring mutations don't race the caller's list.</summary>
    internal DspNode Clone() => new(NodeId, ModuleTypeId, Bypassed, _values, _stringValues, Branch, ControlAssignments, Sidechain);
}
