namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// One module in a converted rig preset: a DSP catalogue type id
/// (<c>"overdrive"</c>, <c>"amp"</c>, …), the parameters to set (by param id,
/// already clamped to the target range) and whether it starts bypassed. Params
/// left out use the module's catalogue defaults when applied.
///
/// <para><see cref="StringParams"/> carries the ABI-v22 string-valued params
/// (file paths / free text — e.g. a <c>namamp</c> <c>modelPath</c>, a
/// <c>cab</c>/<c>acousticsim</c> <c>irPath</c>, or the <c>miku</c>
/// <c>phrases</c>). It is null / empty for the vast majority of modules;
/// <c>DcRigApplier</c> pushes each entry through
/// <see cref="IDspChainService.SetNodeString"/> after the node is created and
/// its float params are set. On reuse, strings are diffed against the live node;
/// omitted values are reset to the built-in default, so a tone or restored
/// snapshot cannot inherit another tone's custom capture or cabinet IR.</para>
/// </summary>
public sealed record DcRigModule(
    string ModuleTypeId,
    IReadOnlyDictionary<string, float> Params,
    bool Bypassed = false)
{
    /// <summary>String-valued params (ABI v22) to push after node creation, keyed
    /// by string-param id. Null / empty for modules with no string params.</summary>
    public IReadOnlyDictionary<string, string>? StringParams { get; init; }

    /// <summary>The module's split/parallel branch tag (ABI v23): an int
    /// <c>0..3</c> (0 = unassigned → branch 1). Meaningful only for modules that
    /// sit inside a split region (between a <c>splitter</c> and a <c>merger</c>);
    /// inert everywhere else. <c>DcRigApplier</c> re-pushes it via
    /// <see cref="IDspChainService.SetNodeBranch"/> after node creation because a
    /// native tag does NOT survive a remove/re-add. Default <c>0</c> (and omitted
    /// from the persisted shape when 0, for schema tolerance).</summary>
    public int Branch { get; init; }

    /// <summary>
    ///     What this module's DETECTOR keys off (ABI v34) — <c>None</c> (the
    ///     default, and the pre-v34 behaviour) for every module that does not
    ///     use one.
    ///     <para>
    ///         It is part of a module's IDENTITY exactly as the branch tag is: a
    ///         ducker keyed off the song mix and the same ducker keyed off its
    ///         own input are two different pedals, and a native key does NOT
    ///         survive a remove/re-add — so <see cref="DcRigApplier" /> re-pushes
    ///         it through <see cref="IDspChainService.SetNodeSidechain" /> after
    ///         node creation, and a snapshot that dropped it would hand the
    ///         player back a ducker that does not duck.
    ///     </para>
    ///     <para>
    ///         Only a module whose <c>DspModuleType.SupportsSidechain</c> is true
    ///         accepts a non-<c>None</c> source; the engine REJECTS one on any
    ///         other type, and the applier reports that like any other rejected
    ///         op. The WRAPPER's enum is used rather than a managed copy because
    ///         its numbers are ABI and are what the persisted shape stores.
    ///     </para>
    /// </summary>
    public JuceSharp.Dsp.JuceDspSidechainSource Sidechain { get; init; }

    private readonly IReadOnlyList<RigControlAssignment> _controlAssignments = Array.Empty<RigControlAssignment>();
    public IReadOnlyList<RigControlAssignment> ControlAssignments
    {
        get => _controlAssignments;
        init => _controlAssignments = RigControlAssignments.Copy(value);
    }
}

/// <summary>
/// An ordered, engine-independent rig description produced by
/// <see cref="RsToneConverter"/>. Modules are in signal order; Phase 2's
/// <c>DcRigApplier</c> diffs this against the live chain.
/// </summary>
public sealed record DcRigPreset(IReadOnlyList<DcRigModule> Modules)
{
    /// <summary>
    ///     The rig's scenes (A–H) and which one is playing — null for a rig that
    ///     has none, which is every factory tone and every tone saved before scenes
    ///     existed, and which behaves exactly as it always did. Additive: a
    ///     transform that builds a new rig from this one must carry it
    ///     (<c>with { Modules = … }</c>, never <c>new DcRigPreset(…)</c>), and
    ///     <see cref="DcRigApplier" /> puts it on the chain with the modules.
    /// </summary>
    public global::DotCharter.Tone.Audio.Dsp.Scenes.RigSceneSet? Scenes { get; init; }
}
