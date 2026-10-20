// JuceDspChainService root: the managed chain mirror over the native tone-rig ABI - engine attach, the module catalogue and every node mutation.
namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
/// Real <see cref="IDspChainService"/> over the native tone-rig DSP ABI. Keeps
/// a managed mirror of the chain, authoritative for the UI-facing composition,
/// and applies every mutation natively FIRST, only updating the mirror on
/// success. The engine can report a node's type and bypass (ABI v33), but the
/// mirror never asks: it records the type from the id AddNode passed (it never
/// changes), and it also holds what native does not (control assignments), so
/// <see cref="GetNodes"/> is a snapshot with no P/Invoke per node. Every native call is guarded so a partially-implemented
/// or absent native surface degrades to a no-op instead of crashing the UI.
///
/// Threading: mutations are serialized under <c>_gate</c>; <see cref="GetNodes"/>
/// returns a snapshot.
///
/// INTEGRATION SEAM: engine-scoped ops target <see cref="IDspNativeInterop.EngineHandle"/>,
/// which is <c>nint.Zero</c> until <see cref="AttachEngine(nint)"/> is called with the
/// live <c>dca_engine*</c>. Until then only the static catalogue is functional.
/// Wiring that handle from the audio engine is done at native integration time.
/// </summary>
public sealed partial class JuceDspChainService : IDspChainService
{
    private readonly IDspNativeInterop _native;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();
    private readonly List<DspNode> _nodes = [];

    private IReadOnlyList<DspModuleType> _catalog = [];
    private Dictionary<string, DspModuleType> _catalogById = new(StringComparer.Ordinal);

    /// <param name="logger">Optional app logger.</param>
    /// <param name="player">Which SEAT's native chain this service drives
    /// (ABI v28). 0 — the single-player chain — is the default, and every
    /// pre-v28 caller keeps it.</param>
    public JuceDspChainService(IAppLogger? logger = null, int player = 0)
        : this(new DspNativeInterop(player), logger)
    {
    }

    // Test seam: inject an in-memory interop.
    internal JuceDspChainService(IDspNativeInterop native, IAppLogger? logger = null)
    {
        _native = native;
        _logger = logger;
    }

    public IReadOnlyList<DspModuleType> Catalog => _catalog;

    /// <inheritdoc />
    public bool HasEngine
    {
        get
        {
            // Under the same gate AttachEngine writes the handle beneath, so a
            // caller can never read "attached" from a half-completed detach.
            lock (_gate) return _native.EngineHandle != nint.Zero;
        }
    }

    /// <inheritdoc />
    public int Player => _native.Player;

    /// <inheritdoc />
    public event Action? NodesChanged;

    /// <inheritdoc />
    public event Action? EngineAttached;

    /// <summary>Attaches the live native engine handle so the chain ops become
    /// functional. Called at audio-engine integration time.
    /// <para>A handle CHANGE also empties the managed mirror: a different handle is
    /// a different (freshly created, therefore empty) native chain, and a zero
    /// handle is no chain at all.</para></summary>
    public void AttachEngine(nint engineHandle) => AttachEngineCore(engineHandle, null);

    /// <summary>
    ///     The LIVE form. Publishes the wrapper OBJECT as well as the handle, so
    ///     the real interop drives <c>JuceDspChain</c> — every call of which pins
    ///     the engine handle for its duration — instead of a bare, unpinned
    ///     <c>dca_engine*</c>. Production always calls this one.
    ///     <para>
    ///         The handle keeps its old job unchanged: it is the IDENTITY the
    ///         mirror-reset rule below is expressed in, and it is what the
    ///         metering facade addresses. Both are written together under the
    ///         gate, so no caller can observe one without the other.
    ///     </para>
    /// </summary>
    public void AttachEngine(JuceSharp.JuceAudioEngine? engine)
        => AttachEngineCore(engine?.NativeHandle ?? nint.Zero, engine);

    private void AttachEngineCore(nint engineHandle, JuceSharp.JuceAudioEngine? engine)
    {
        // Under _gate, and the HANDLE WRITE must be inside it. Every other member of
        // this class holds the gate across its P/Invoke, and so must this one: a bare
        // field write would make the documented mitigation — "detach the DSP tone rig
        // BEFORE destroying an engine, AttachEngine(nint.Zero) first, always" — only
        // half a barrier. It would stop FUTURE writes but not wait for one already
        // inside dca_engine_dsp_set_param. So a pedalboard knob drag (UI thread →
        // SetParam → native against engine E) racing a device change returned from
        // AttachEngine(0) immediately, ReplaceEngineGated freed E on the very next
        // line, and the in-flight call dereferenced freed memory. IDspNativeInterop.
        // EngineHandle is a bare, unpinned dca_engine* — there is no NativeHandleOwner
        // drain protecting this path, which is exactly WHY the detach rule exists.
        // Taking the lock makes AttachEngine(0) the real barrier the rule assumes.
        //
        // The mirror is AUTHORITATIVE for the UI-facing composition (nothing reads
        // the composition back from native), so it must die with the chain it mirrors.
        // ReplaceEngineGated has always CLAIMED this — "reattaching resets the
        // managed mirror to match" — but nothing ever did it, and every survivor of
        // a device change, buffer-size change or watchdog recovery became a phantom:
        //   * GetNodes() rendered a pedalboard of pedals that do not exist natively,
        //     producing no sound;
        //   * ChainHasNeuralAmp fired the one-shot 48 kHz request against a ghost
        //     `namamp` on a chain that has none;
        //   * and worst, DcRigApplier diffs a preset against this mirror — a preset
        //     MATCHING the phantoms is reported NoOp with ZERO native calls, so
        //     re-picking the tone could not rebuild it. User-visible as "my tone is
        //     gone and re-picking it does nothing".
        //
        // Handle-EQUAL is deliberately NOT a clear. ApplyEngineConfig re-asserts the
        // attachment defensively after an in-place Reconfigure, which keeps the same
        // engine AND the same native chain; clearing there would manufacture exactly
        // the bug this fixes.
        bool cleared;
        bool attachedLive;
        lock (_gate)
        {
            var previous = _native.EngineHandle;
            _native.EngineHandle = engineHandle;
            // Same lock, same instant: an op that saw the new handle must never
            // find the old wrapper (or none) behind it.
            _native.Engine = engineHandle == nint.Zero ? null : engine;

            cleared = previous != engineHandle && _nodes.Count > 0;
            // A CHANGE to a live handle is a fresh, empty native chain — the one
            // moment a persisted rig may be restored without fighting the player.
            // Note this is deliberately independent of `cleared`: the first attach
            // of a session clears NOTHING (the mirror was already empty) and is
            // precisely the case that must fire.
            attachedLive = previous != engineHandle && engineHandle != nint.Zero;
            if (previous != engineHandle)
            {
                _nodes.Clear();
                // the scenes described THAT chain; the restore brings the stored ones back
                _scenes = null;
            }
            ReplayMetering();
            // A fresh engine starts with spillover OFF (ABI v37).
            ReplaySpillover();
        }

        // One line per (re)attach so a runtime log shows whether the in-house rig
        // is actually pointed at a live engine (engineHandle != 0). A zero handle
        // here means every AddNode/SetParam will no-op and RS in-game tones can't apply.
        _logger?.Info(AppLogCategory.Audio,
            engineHandle == nint.Zero
                ? $"DSP chain (player {Player + 1}) detached (engine handle = 0); chain ops will no-op until re-attached."
                : $"DSP chain (player {Player + 1}) attached to engine handle 0x{engineHandle:X}; {_catalog.Count} module type(s) in catalogue.");

        // Notify AFTER the lock, like every other mutation here, so a handler can
        // call back into GetNodes without deadlock. Only when something actually
        // went — an idempotent re-attach must stay silent.
        if (cleared) RaiseNodesChanged();

        // EngineAttached comes LAST and outside the lock: its handler repopulates
        // the chain (AddNode/SetParam), which takes _gate itself.
        if (attachedLive) RaiseEngineAttached();
    }

    private volatile Scenes.RigSceneSet? _scenes;

    /// <inheritdoc />
    public Scenes.RigSceneSet? Scenes
    {
        get => _scenes;
        set => _scenes = value;
    }

    private void RaiseEngineAttached()
    {
        try
        {
            EngineAttached?.Invoke();
        }
        catch (Exception ex)
        {
            // A restore that throws must never take the audio engine's attach
            // path down with it — this runs inside ReplaceEngineGated.
            _logger?.Warning(AppLogCategory.Audio,
                $"A DSP EngineAttached handler threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Loads the static module catalogue from the native ABI. Throws if
    /// the native entry points are absent — the DI factory wraps this call in a
    /// try/catch and falls back to <see cref="NullDspChainService"/>.</summary>
    public void LoadCatalog()
    {
        var count = _native.ModuleTypeCount();
        var list = new List<DspModuleType>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            if (!_native.TryModuleTypeInfo(i, out var id, out var name)) continue;

            var paramCount = _native.ModuleParamCount(id);
            var pars = new List<DspParamInfo>(Math.Max(0, paramCount));
            for (var p = 0; p < paramCount; p++)
            {
                if (_native.TryModuleParamInfo(id, p, out var info) && info.Id.Length > 0)
                {
                    pars.Add(info with
                    {
                        ValueLabels = LoadValueLabels(id, info.Id),
                        Step = LoadStep(id, info.Id)
                    });
                }
            }

            // String-param catalogue (ABI v22). Read once here so the pedalboard
            // UI stays fully catalogue-driven (file-picker filter included). Older
            // DLLs (< v22) return a negative count / throw EntryPointNotFound; the
            // Safe wrapper degrades that to an empty list rather than aborting the
            // whole catalogue load.
            var stringCount = Safe(() => _native.ModuleStringParamCount(id), 0, nameof(LoadCatalog));
            var strPars = new List<DspStringParamInfo>(Math.Max(0, stringCount));
            for (var p = 0; p < stringCount; p++)
            {
                if (Safe(() => _native.TryModuleStringParamInfo(id, p, out var sinfo) ? sinfo : null,
                        null, nameof(LoadCatalog)) is { } si && si.Id.Length > 0)
                {
                    strPars.Add(si);
                }
            }

            // ABI v34 capability bit. Read HERE, with the rest of the static
            // metadata, because a UI that offers the sidechain control without
            // it offers a control the engine rejects — and a UI that reads it
            // per frame pays a P/Invoke for an answer that cannot change.
            var keyable = Safe(() => _native.ModuleSupportsSidechain(id), false, nameof(LoadCatalog));

            list.Add(new DspModuleType(id, name.Length > 0 ? name : id, pars)
            {
                StringParams = strPars,
                SupportsSidechain = keyable
            });
        }

        _catalog = list;
        _catalogById = list.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _logger?.Info(AppLogCategory.Audio, $"DSP module catalogue loaded: {list.Count} module type(s).");
    }

    /// <summary>
    ///     Adopts another seat's already-loaded catalogue (ABI v28). The module
    ///     catalogue is STATIC native data — <c>dca_dsp_module_*</c> takes no
    ///     engine and no player — so every seat's answer is identical and reading
    ///     it once is the only reason this exists. Sharing the loaded
    ///     <see cref="DspModuleType" /> instances is safe: they are immutable
    ///     records of native metadata, not per-chain state.
    /// </summary>
    internal void ShareCatalogFrom(JuceDspChainService source)
    {
        _catalog = source._catalog;
        _catalogById = source._catalogById;
    }

    /// <summary>Loads the ABI-v24 value labels for a discrete ("model picker")
    /// param — amp <c>voicing</c>, cab/od/dist <c>model</c>. A negative count
    /// means the param is continuous (numeric) ⇒ null (the UI keeps a knob).
    /// Wrapped in the <see cref="Safe{T}"/> degrade so a pre-v24 DLL (no label
    /// entry points) returns null rather than aborting the whole catalogue.</summary>
    private IReadOnlyList<string>? LoadValueLabels(string moduleTypeId, string paramId)
    {
        var count = Safe(() => _native.ModuleParamLabelCount(moduleTypeId, paramId), -1, nameof(LoadValueLabels));
        if (count <= 0) return null; // -1 = continuous, 0 = degenerate — keep the knob
        var labels = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            if (Safe(() => _native.TryModuleParamLabel(moduleTypeId, paramId, i, out var l) ? l : null,
                    null, nameof(LoadValueLabels)) is not { } label)
            {
                return null; // a partial/failed label set is not a usable picker
            }

            labels.Add(label);
        }

        return labels;
    }

    /// <summary>Loads the ABI-v27 quantization step for an ORDINAL discrete param
    /// (chorus <c>voices</c>, phaser <c>stages</c>, bitcrusher <c>bits</c>, …) —
    /// the value the core lrounds, so the knob must snap to it and display an
    /// integer. 0 = continuous (a plain knob). Wrapped in the <see cref="Safe{T}"/>
    /// degrade so a pre-v27 DLL (no step entry point) reports "continuous" rather
    /// than aborting the whole catalogue load.</summary>
    private float LoadStep(string moduleTypeId, string paramId)
    {
        var step = Safe(() => _native.ModuleParamStep(moduleTypeId, paramId), 0f, nameof(LoadStep));
        return step > 0f ? step : 0f; // -1 (unknown) and 0 (continuous) both mean "no step"
    }

    public IReadOnlyList<DspNode> GetNodes()
    {
        lock (_gate)
            return _nodes.Select(n => n.Clone()).ToList();
    }

    public DspNode? AddNode(string moduleTypeId, bool initiallyBypassed = false)
    {
        // SAY WHY. Both refusals below happen entirely managed-side — no native
        // call is made — yet at the call site they are indistinguishable from a
        // native rejection, so `DcRigApplier: AddNode('amp') failed` was the only
        // thing a log ever showed. Diagnosing v0.44's "no tone rig at all" from
        // that took reading the whole managed stack and eliminating four native
        // conditions; one line here answers it. The app logger coalesces repeats,
        // so a whole rig failing prints one line per module type, not per attempt.
        if (!_catalogById.TryGetValue(moduleTypeId, out var type))
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"DSP chain (player {Player + 1}): AddNode('{moduleTypeId}') refused — no such module type in " +
                $"the native catalogue ({_catalog.Count} type(s) loaded).");
            return null;
        }

        DspNode snapshot;
        lock (_gate)
        {
            if (_native.EngineHandle == nint.Zero)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"DSP chain (player {Player + 1}): AddNode('{moduleTypeId}') refused — no engine is attached, " +
                    "so this chain cannot hold anything yet.");
                return null;
            }

            var id = Safe(() => _native.AddNode(moduleTypeId, initiallyBypassed), -1, nameof(AddNode));
            if (id < 0) return null;

            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var p in type.Params) values[p.Id] = p.Default;

            var node = new DspNode(id, moduleTypeId, initiallyBypassed, values);
            _nodes.Add(node);
            snapshot = node.Clone();
        }
        // Notify AFTER releasing the lock so a handler (e.g. the audio service's
        // 48 kHz-for-namamp policy) can call back into GetNodes without deadlock.
        RaiseNodesChanged();
        return snapshot;
    }

    public bool RemoveNode(int nodeId)
    {
        bool removed;
        lock (_gate)
        {
            if (!Safe(() => _native.RemoveNode(nodeId), false, nameof(RemoveNode))) return false;
            _nodes.RemoveAll(n => n.NodeId == nodeId);
            removed = true;
        }
        RaiseNodesChanged();
        return removed;
    }

    private void RaiseNodesChanged()
    {
        try { NodesChanged?.Invoke(); }
        catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, $"DSP NodesChanged handler threw: {ex.Message}"); }
    }

    public bool MoveNode(int nodeId, int newIndex)
    {
        lock (_gate)
        {
            var current = _nodes.FindIndex(n => n.NodeId == nodeId);
            if (current < 0) return false;

            var target = Math.Clamp(newIndex, 0, _nodes.Count - 1);
            if (target == current) return true;

            if (!Safe(() => _native.MoveNode(nodeId, target), false, nameof(MoveNode))) return false;

            var node = _nodes[current];
            _nodes.RemoveAt(current);
            _nodes.Insert(target, node);
            return true;
        }
    }

    public bool SetBypass(int nodeId, bool bypassed)
    {
        lock (_gate)
        {
            if (!Safe(() => _native.SetBypass(nodeId, bypassed), false, nameof(SetBypass))) return false;
            _nodes.FirstOrDefault(n => n.NodeId == nodeId)?.SetBypassed(bypassed);
            return true;
        }
    }

    public bool SetParam(int nodeId, string paramId, float value)
    {
        lock (_gate)
        {
            if (!Safe(() => _native.SetParam(nodeId, paramId, value), false, nameof(SetParam))) return false;
            _nodes.FirstOrDefault(n => n.NodeId == nodeId)?.SetValue(paramId, value);
            return true;
        }
    }

    public float? GetParam(int nodeId, string paramId)
    {
        lock (_gate)
        {
            var native = Safe(() =>
            {
                return _native.TryGetParam(nodeId, paramId, out var v) ? v : (float?)null;
            }, null, nameof(GetParam));
            if (native.HasValue) return native.Value;

            var node = _nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (node != null && node.Values.TryGetValue(paramId, out var cached)) return cached;
            return null;
        }
    }

    public bool SetNodeString(int nodeId, string paramId, string value)
    {
        var v = value ?? string.Empty;
        lock (_gate)
        {
            // Native FIRST; only mirror the cache on success (a failed IR load
            // returns false and the native side keeps the previous value — so the
            // mirror must not advance either). A namamp modelPath returns true
            // immediately; the async load result is read via GetParam("loaded").
            if (!Safe(() => _native.SetNodeString(nodeId, paramId, v), false, nameof(SetNodeString))) return false;
            _nodes.FirstOrDefault(n => n.NodeId == nodeId)?.SetStringValue(paramId, v);
            return true;
        }
    }

    public string? GetNodeString(int nodeId, string paramId)
    {
        lock (_gate)
        {
            var native = Safe(() =>
            {
                return _native.TryGetNodeString(nodeId, paramId, out var v) ? v : null;
            }, null, nameof(GetNodeString));
            if (native is not null) return native;

            var node = _nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (node != null && node.StringValues.TryGetValue(paramId, out var cached)) return cached;
            return null;
        }
    }

    public bool SetControlAssignments(int nodeId, IReadOnlyList<RigControlAssignment> assignments)
    {
        lock (_gate)
        {
            var node = _nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (node is null || !RigControlAssignments.TryValidate(assignments,
                    _catalog.FirstOrDefault(m => m.Id == node.ModuleTypeId), out var copy))
            {
                return false;
            }

            if (node.ControlAssignments.SequenceEqual(copy)) return true;
            node.SetControlAssignments(copy);
        }
        RaiseNodesChanged();
        return true;
    }

    public bool SetNodeBranch(int nodeId, int branch)
    {
        lock (_gate)
        {
            // Native FIRST; only mirror the tag on success. A branch change is a
            // ROUTING tweak, not a composition change — deliberately does NOT
            // raise NodesChanged (the pedalboard VM refreshes itself after the
            // drop, and the 48 kHz-for-namamp policy has no interest in routing).
            if (!Safe(() => _native.SetNodeBranch(nodeId, branch), false, nameof(SetNodeBranch))) return false;
            _nodes.FirstOrDefault(n => n.NodeId == nodeId)?.SetBranch(branch);
            return true;
        }
    }

    public int GetNodeBranch(int nodeId)
    {
        lock (_gate)
        {
            var native = Safe(() =>
            {
                return _native.TryGetNodeBranch(nodeId, out var v) ? v : (int?)null;
            }, null, nameof(GetNodeBranch));
            if (native.HasValue) return native.Value;

            var node = _nodes.FirstOrDefault(n => n.NodeId == nodeId);
            return node?.Branch ?? 0;
        }
    }

    public bool SetNodeSidechain(int nodeId, JuceSharp.Dsp.JuceDspSidechainSource source)
    {
        lock (_gate)
        {
            // Native FIRST; only mirror on success. A source the engine refuses
            // — the common case, because every module that does not advertise
            // the capability refuses one — must leave the mirror (and therefore
            // the picker that reads it) on the value that is really running.
            if (!Safe(() => _native.SetNodeSidechain(nodeId, source), false, nameof(SetNodeSidechain))) return false;
            _nodes.FirstOrDefault(n => n.NodeId == nodeId)?.SetSidechain(source);
            return true;
        }
    }

    public JuceSharp.Dsp.JuceDspSidechainSource GetNodeSidechain(int nodeId)
    {
        lock (_gate)
        {
            var native = Safe(() =>
            {
                return _native.TryGetNodeSidechain(nodeId, out var v) ? v : (JuceSharp.Dsp.JuceDspSidechainSource?)null;
            }, null, nameof(GetNodeSidechain));
            if (native.HasValue) return native.Value;

            return _nodes.FirstOrDefault(n => n.NodeId == nodeId)?.Sidechain
                   ?? JuceSharp.Dsp.JuceDspSidechainSource.None;
        }
    }

    /// <summary>
    ///     ABI v30/v34 per-node latency. There is NO managed mirror behind this
    ///     one and there must not be: latency is derived native state (it moves
    ///     with the prepared rate, with a param the module re-derives its
    ///     geometry from, and with bypass), so a cached number would be stale
    ///     the moment a device changed. No reading ⇒ <c>null</c>, never 0.
    /// </summary>
    public int? GetNodeLatencySamples(int nodeId)
    {
        lock (_gate)
        {
            return Safe(() =>
            {
                return _native.TryGetNodeLatency(nodeId, out var v) ? v : (int?)null;
            }, null, nameof(GetNodeLatencySamples));
        }
    }

    /// <summary>Offline tooling path (v0.27 take re-amp): runs a STEREO buffer
    /// through this chain via <c>dca_engine_dsp_process_buffer</c> — the same
    /// code the live input callback runs, natively chunked to a prepared-safe
    /// block size (state persists across calls). Serialized against a live
    /// callback by the chain's own native process barrier; false when no
    /// engine is attached or the native call fails. Deliberately NOT on
    /// <see cref="IDspChainService"/> — a concrete tooling surface, not a
    /// chain op (the fakes have no DSP to run).
    /// <para>Under <c>_gate</c> like every other engine-scoped member. This was
    /// expression-bodied straight into <c>Safe(...)</c> with no lock, over a
    /// non-volatile <c>EngineHandle</c> — so an <c>AttachEngine(nint.Zero)</c>
    /// plus <c>dca_engine_destroy</c> could complete inside the interop's own
    /// null-check-then-call window and the render would dereference freed
    /// memory. That was masked only by the single caller owning a private,
    /// device-less engine; the CLASS invariant is what
    /// <c>docs/agents/services/audio-engine.md</c> says the unpinned handle is
    /// safe by. The caller renders in chunks, so the hold is one block, not the
    /// whole file.</para></summary>
    public bool ProcessOfflineBuffer(float[] left, float[] right, int numFrames, int sampleRate)
    {
        lock (_gate)
        {
            return Safe(() => _native.ProcessBuffer(left, right, numFrames, sampleRate), false,
                nameof(ProcessOfflineBuffer));
        }
    }

    private T Safe<T>(Func<T> op, T fallback, string what)
    {
        try
        {
            return op();
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"DSP chain native call '{what}' failed: {ex.Message}");
            return fallback;
        }
    }
}
