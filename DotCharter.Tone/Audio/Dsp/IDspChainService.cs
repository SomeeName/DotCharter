namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
/// Managed surface over the in-house tone-rig DSP engine: the reorderable chain
/// of effect modules the guitar input is routed through. Backed by the native
/// engine-scoped ABI (<c>dca_engine_dsp_*</c>) plus the static module catalogue
/// (<c>dca_dsp_module_*</c>). The implementation keeps a managed mirror of the
/// chain so the UI can bind an ordered list; every mutation is applied natively
/// first and only mirrored on success.
///
/// All members are safe to call before the native entry points exist — a
/// build without the DSP ABI resolves to the no-op <see cref="NullDspChainService"/>.
/// </summary>
public interface IDspChainService
{
    /// <summary>Points the chain-control ops at a live native engine
    /// (<c>dca_engine*</c>); pass <see cref="nint.Zero"/> to detach. Called by
    /// the audio service on every engine (re)create. Until attached, the static
    /// catalogue still works but engine-scoped ops fail safely.
    /// <para>A handle CHANGE resets the managed mirror (a different handle is a
    /// different, freshly created and therefore EMPTY native chain; a zero handle
    /// is no chain at all) and raises <see cref="NodesChanged"/>. Re-attaching the
    /// SAME handle — what an in-place device reconfigure does — keeps it, because
    /// that engine kept its chain.</para></summary>
    void AttachEngine(nint engineHandle);

    /// <summary>
    ///     The LIVE form of <see cref="AttachEngine(nint)" />: hands the chain
    ///     the wrapper object as well as the handle, which is what lets the real
    ///     interop go through <c>JuceDspChain</c> (pinned) instead of a bare
    ///     <c>dca_engine*</c>. Pass null to detach.
    ///     <para>
    ///         Production always uses this one. The handle-only overload remains
    ///         because the handle is still the identity token the mirror-reset
    ///         rule is expressed in, and because a test can name an engine that
    ///         does not exist. The default implementation here forwards to it, so
    ///         an in-memory fake keeps working unchanged and simply never gets a
    ///         wrapper.
    ///     </para>
    /// </summary>
    void AttachEngine(JuceSharp.JuceAudioEngine? engine)
        => AttachEngine(engine?.NativeHandle ?? nint.Zero);

    /// <summary>
    ///     Which SEAT's native chain this service drives (ABI v28). The
    ///     engine hosts one independent tone rig per player; <c>0</c> is the
    ///     single-player chain and is what every pre-v28 caller gets.
    ///     <para>
    ///         Resolve another seat's chain through
    ///         <see cref="IPlayerDspChains" /> — never by constructing a second
    ///         service against the same seat, which would give you two managed
    ///         mirrors of one native chain.
    ///     </para>
    /// </summary>
    int Player { get; }

    /// <summary>
    ///     Whether a LIVE native engine handle is attached (see
    ///     <see cref="AttachEngine(nint)" />). While this is false EVERY chain-scoped op
    ///     below fails without making a native call at all — the static catalogue
    ///     is the only thing that works.
    ///     <para>
    ///         <b>"The chain has no nodes" is not a substitute for this.</b> An
    ///         empty board and a detached chain look identical through
    ///         <see cref="GetNodes" />, and using the former as a proxy for the
    ///         latter is exactly what made the startup rig restore run against a
    ///         null engine handle: the DSP chains are built at DI time while the
    ///         audio engine is built LAZILY (first audio load / first transport
    ///         call), so on the shipped startup path there really is no engine for
    ///         several seconds. Every module of the player's saved rig was then
    ///         reported as a failed native op that no native code ever saw.
    ///     </para>
    /// </summary>
    bool HasEngine { get; }

    /// <summary>The static module catalogue, loaded once. Empty when the native
    /// DSP ABI is unavailable.</summary>
    IReadOnlyList<DspModuleType> Catalog { get; }

    /// <summary>The current chain nodes, in signal order (a snapshot).</summary>
    IReadOnlyList<DspNode> GetNodes();

    /// <summary>Raised after chain composition or control assignments change, not
    /// for a numeric param/bypass/string tweak. Fires synchronously on
    /// the mutating thread AFTER the mirror is updated and the lock released.
    /// The audio service uses it to apply the 48 kHz device-rate policy when a
    /// <c>namamp</c> (neural-amp) node appears. Handlers must be throw-free and
    /// cheap (or hop off-thread themselves).</summary>
    event Action? NodesChanged;

    /// <summary>
    ///     Raised after a LIVE engine handle was attached — i.e. the chain just
    ///     became usable, and the native chain behind it is EMPTY (a fresh
    ///     engine has no nodes; see <see cref="AttachEngine(nint)" />).
    ///     <para>
    ///         This exists so a listener can tell "the engine was rebuilt under
    ///         me" apart from "the player removed the last pedal". Both end with
    ///         an empty chain and both raise <see cref="NodesChanged" />, so
    ///         anything that repopulates the rig — <c>ToneSessionService</c>
    ///         restoring the persisted tone — would otherwise have to guess, and
    ///         guessing wrong means fighting a player who is deliberately
    ///         clearing their board.
    ///     </para>
    ///     <para>
    ///         Fires only on a handle CHANGE to a non-zero value: re-attaching
    ///         the same handle (what an in-place device reconfigure does) keeps
    ///         its chain and is not a rebuild. Raised on the mutating thread
    ///         after the lock is released, like <see cref="NodesChanged" />;
    ///         handlers must be throw-free and cheap or hop off-thread
    ///         themselves.
    ///     </para>
    /// </summary>
    event Action? EngineAttached;

    /// <summary>Appends a module of the given catalogue type to the end of the
    /// chain. Returns the created node (with default parameter values) or
    /// <c>null</c> on failure / unknown type. Set <paramref name="initiallyBypassed"/>
    /// to configure a regular node at zero wet before fading it in. Splitter and
    /// merger topology remains immediate; this is not an atomic rig replacement.</summary>
    DspNode? AddNode(string moduleTypeId, bool initiallyBypassed = false);

    /// <summary>Removes the node with the given id. Returns false if unknown or
    /// the native call failed.</summary>
    bool RemoveNode(int nodeId);

    /// <summary>Moves the node to a new zero-based index within the chain
    /// (clamped). Returns false on failure.</summary>
    bool MoveNode(int nodeId, int newIndex);

    /// <summary>Bypasses (or un-bypasses) the node. Returns false on failure.</summary>
    bool SetBypass(int nodeId, bool bypassed);

    /// <summary>Sets a parameter value on the node. Returns false on failure.</summary>
    /// <summary>Replaces durable managed-only module mappings; raises NodesChanged after success.
    /// Unsupported implementations return false. Empty clears previous assignments.</summary>
    bool SetControlAssignments(int nodeId, IReadOnlyList<RigControlAssignment> assignments) => false;

    bool SetParam(int nodeId, string paramId, float value);

    /// <summary>Reads a parameter value, preferring the native value and falling
    /// back to the last-written cache. Returns null when unknown.</summary>
    float? GetParam(int nodeId, string paramId);

    /// <summary>Sets a string-valued parameter (ABI v22) on the node — a file path
    /// (namamp <c>modelPath</c>, cab / acousticsim <c>irPath</c>) or free text
    /// (miku <c>phrases</c>). Native semantics: an empty <paramref name="value"/>
    /// reverts to the module's built-in default; a failed synchronous IR load
    /// returns <c>false</c> and KEEPS the previous value; a namamp
    /// <c>modelPath</c> is accepted immediately (returns <c>true</c>) and loads
    /// asynchronously — poll the read-only <c>"loaded"</c> / <c>"rateMismatch"</c>
    /// float params (via <see cref="GetParam"/>) for the outcome. Returns
    /// <c>false</c> on any native rejection / unknown node or param.</summary>
    bool SetNodeString(int nodeId, string paramId, string value);

    /// <summary>Reads a string-valued parameter (ABI v22), preferring the native
    /// value and falling back to the last-written cache. Returns <c>null</c> when
    /// the node / param is unknown; an empty string when the value is the
    /// built-in default.</summary>
    string? GetNodeString(int nodeId, string paramId);

    /// <summary>Sets the node's split/parallel branch tag (ABI v23): an int
    /// <c>0..3</c> (0 = unassigned → branch 1). The tag is inert outside a split
    /// region, survives move/reorder natively but NOT remove/re-add, and clamps
    /// native-side to the splitter's enabled branch count. Returns <c>false</c>
    /// on a native rejection (out-of-range id / unknown node) or when no engine
    /// is attached. Throw-free.</summary>
    bool SetNodeBranch(int nodeId, int branch);

    /// <summary>Reads the node's branch tag (ABI v23), preferring the native value
    /// and falling back to the last-written cache. Returns <c>0</c> (the default /
    /// branch-1 tag) when the node is unknown or no engine is attached — never
    /// throws.</summary>
    int GetNodeBranch(int nodeId);

    /// <summary>
    ///     Points the node's DETECTOR at an external key (ABI v34). The enum is
    ///     the WRAPPER's — its numeric values are ABI and are persisted, so this
    ///     layer must not re-type that vocabulary.
    ///     <para>
    ///         Only a module type whose <see cref="DspModuleType.SupportsSidechain" />
    ///         is true accepts a source other than
    ///         <see cref="JuceSharp.Dsp.JuceDspSidechainSource.None" />; every
    ///         other one REJECTS it, and this returns <c>false</c>. A UI must
    ///         therefore gate the control on the catalogue flag rather than
    ///         offering it and hoping.
    ///     </para>
    ///     Defaults to <c>false</c> — a chain with no v34 engine behind it
    ///     cannot key anything, and saying so is the honest answer.
    /// </summary>
    bool SetNodeSidechain(int nodeId, JuceSharp.Dsp.JuceDspSidechainSource source) => false;

    /// <summary>Reads the node's detector key (ABI v34), preferring the native
    /// value and falling back to the mirror. <c>None</c> when unknown — never
    /// throws.</summary>
    JuceSharp.Dsp.JuceDspSidechainSource GetNodeSidechain(int nodeId)
        => JuceSharp.Dsp.JuceDspSidechainSource.None;

    /// <summary>
    ///     Latency this node adds, in SAMPLES at the chain's prepared rate, or
    ///     <c>null</c> when the engine cannot answer (detached, unknown node).
    ///     <para>
    ///         <c>null</c> and <c>0</c> are different answers and must stay that
    ///         way: 0 is "this module is in the path and adds nothing" — which is
    ///         also what a BYPASSED node reports, because it is not in the path —
    ///         while null is "nobody asked the engine". A readout that folded them
    ///         would print "0 samples" for a chain it never measured.
    ///     </para>
    /// </summary>
    int? GetNodeLatencySamples(int nodeId) => null;

    /// <summary>
    ///     The live rig's scenes (A–H) and which one is playing — MANAGED state
    ///     beside the chain, never sent to the engine (a scene switch is params and
    ///     bypass on the nodes that exist). Written by <c>DcRigApplier</c> with
    ///     every rig it applies (a rig without scenes clears it, so one tone's
    ///     scenes can never be projected onto another's modules) and by the rig
    ///     surface's scene edits; read by <c>DcRigSnapshot.FromChain</c>, which is
    ///     how scenes reach undo, a saved tone and the persisted live rig.
    ///     <para>
    ///         Cleared with the mirror when a NEW engine attaches: the chain is
    ///         empty then, and the restore that follows puts the stored rig — its
    ///         scenes included — back. The default members keep a chain that has no
    ///         such state (test doubles, the null service) scene-less.
    ///     </para>
    /// </summary>
    Scenes.RigSceneSet? Scenes
    {
        get => null;
        set { }
    }

    /// <summary>
    ///     SPILLOVER (ABI v37): whether a delay/reverb this chain REMOVES rings
    ///     out after the chain instead of being cut. The value is RECORDED and
    ///     replayed onto every engine this chain is attached to (a rebuilt engine
    ///     starts with it off), so it may be set before any engine exists. A
    ///     chain without the capability records nothing and reads false.
    /// </summary>
    bool Spillover { get => false; set { } }

    /// <summary>
    ///     Whether the engine behind this chain can honour <see cref="Spillover" />
    ///     right now — false with no engine attached, or on a chain with no native
    ///     side. A control over it must show itself disabled when this is false.
    /// </summary>
    bool SupportsSpillover => false;
}
