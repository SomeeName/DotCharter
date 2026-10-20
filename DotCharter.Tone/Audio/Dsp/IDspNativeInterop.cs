namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
/// Thin 1:1 seam over JuceSharp's tone-rig wrapper, isolated so
/// <see cref="JuceDspChainService"/> can be unit-tested with an in-memory fake
/// and so no native call ever runs on a headless/test path. The real
/// implementation is <see cref="DspNativeInterop"/>, which drives
/// <c>JuceDspChain</c>/<c>JuceDspNode</c>/<c>JuceDspModuleCatalog</c>.
/// </summary>
internal interface IDspNativeInterop
{
    /// <summary>The native <c>dca_engine*</c> the chain ops target. Zero until
    /// the audio engine is attached at integration time; while zero every
    /// engine-scoped op fails safely (the static catalogue still works).</summary>
    nint EngineHandle { get; set; }

    /// <summary>
    ///     The wrapper object behind <see cref="EngineHandle" />, or null when
    ///     nothing is attached. This is what the real interop actually drives:
    ///     <c>JuceAudioEngine.GetDspChain(Player)</c> PINS the handle for the
    ///     duration of every call, so a chain op racing an engine dispose is a
    ///     caught <see cref="ObjectDisposedException" /> rather than a read
    ///     through freed native memory.
    ///     <para>
    ///         It does not replace <see cref="EngineHandle" />: the handle is
    ///         still the IDENTITY token the chain service compares to decide
    ///         whether its managed mirror survives a re-attach, and it is still
    ///         what the metering facade addresses. Both are written together,
    ///         under the chain service's gate. A handle with no engine behind it
    ///         means "not attached" to every engine-scoped op.
    ///     </para>
    /// </summary>
    JuceSharp.JuceAudioEngine? Engine { get; set; }

    /// <summary>Which SEAT's chain these engine-scoped ops address (ABI
    /// v28). 0 is the single-player chain and is the default everywhere. Only
    /// the CHAIN-scoped calls (<see cref="AddNode" />, <see cref="GetNodeIds" />,
    /// <see cref="Clear" />, <see cref="ProcessBuffer" />) use it — the
    /// node-scoped ones resolve the seat from the node id itself.</summary>
    int Player { get; }

    /// <summary>Offline tooling path (v0.27 take re-amp): runs a STEREO buffer
    /// through the attached engine's DSP chain — the same code the live input
    /// callback runs. False when no engine is attached or the native call
    /// fails.</summary>
    bool ProcessBuffer(float[] left, float[] right, int numFrames, int sampleRate);

    // Static catalogue.
    int ModuleTypeCount();
    bool TryModuleTypeInfo(int idx, out string id, out string name);
    int ModuleParamCount(string moduleTypeId);
    bool TryModuleParamInfo(string moduleTypeId, int idx, out DspParamInfo info);

    // Static string-param catalogue (ABI v22).
    int ModuleStringParamCount(string moduleTypeId);
    bool TryModuleStringParamInfo(string moduleTypeId, int idx, out DspStringParamInfo info);

    // Static value-label catalogue (ABI v24). Labels name a param's discrete
    // values (the model pickers: amp voicing, cab/od/dist model). A negative
    // count means the param is continuous (numeric) — no labels.
    int ModuleParamLabelCount(string moduleTypeId, string paramId);
    bool TryModuleParamLabel(string moduleTypeId, string paramId, int valueIndex, out string label);

    // Discrete quantization step (ABI v27). > 0 = an ordinal COUNT the core
    // rounds to an int (the knob snaps and shows an integer); 0 = continuous;
    // negative = unknown module/param.
    float ModuleParamStep(string moduleTypeId, string paramId);

    // Engine-scoped chain ops.
    int AddNode(string moduleTypeId, bool initiallyBypassed = false);
    bool RemoveNode(int nodeId);
    bool MoveNode(int nodeId, int newIndex);
    bool SetBypass(int nodeId, bool bypassed);
    bool SetParam(int nodeId, string paramId, float value);
    bool TryGetParam(int nodeId, string paramId, out float value);

    // Engine-scoped string-param ops (ABI v22). <paramref name="value"/> is
    // UTF-8; the getter is size-then-copy under the hood.
    bool SetNodeString(int nodeId, string paramId, string value);
    bool TryGetNodeString(int nodeId, string paramId, out string value);

    // Engine-scoped split/parallel branch tag (ABI v23). 0..3 (0 = branch 1);
    // out-of-range / unknown node reports failure (native InvalidArg).
    bool SetNodeBranch(int nodeId, int branch);
    bool TryGetNodeBranch(int nodeId, out int branch);

    // ── ABI v34: the external detector key ───────────────────────────────────
    //
    // The engine REJECTS a source on a module type that does not advertise the
    // capability, so the CATALOGUE half below is not decoration: it is what
    // stops the UI offering a control every setting of which would be refused.
    //
    // These four carry a DEFAULT implementation on purpose. This is an internal
    // seam with a hand-written in-memory fake in the test project, and an
    // interop that predates them answering "no" is exactly what a pre-v34 DLL
    // answers — the degrade is the same either way.

    /// <summary>Whether the module TYPE accepts an external key (ABI v34,
    /// <c>dca_dsp_module_type_flags</c> bit 0).</summary>
    bool ModuleSupportsSidechain(string moduleTypeId) => false;

    /// <summary>Points the node's detector at a source. False on a native
    /// rejection — which includes a non-None source on a module that does not
    /// support one.</summary>
    bool SetNodeSidechain(int nodeId, JuceSharp.Dsp.JuceDspSidechainSource source) => false;

    /// <inheritdoc cref="SetNodeSidechain"/>
    bool TryGetNodeSidechain(int nodeId, out JuceSharp.Dsp.JuceDspSidechainSource source)
    {
        source = JuceSharp.Dsp.JuceDspSidechainSource.None;
        return false;
    }

    /// <summary>Latency this node adds, in samples at the chain's prepared rate.
    /// False when the engine cannot answer (detached, unknown node, or the
    /// wrapper's own −1). A BYPASSED node natively reports 0.</summary>
    bool TryGetNodeLatency(int nodeId, out int samples)
    {
        samples = 0;
        return false;
    }

    IReadOnlyList<int> GetNodeIds();

    /// <summary>Removes every node from THIS seat's chain (ABI v28). False when
    /// no engine is attached or the native call failed.</summary>
    bool Clear();
}
