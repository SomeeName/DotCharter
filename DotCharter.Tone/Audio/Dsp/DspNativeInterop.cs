using System.Collections.Concurrent;
using JuceSharp;
using JuceSharp.Dsp;

namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
/// Real <see cref="IDspNativeInterop"/>, over JuceSharp's managed tone-rig
/// wrapper (<see cref="JuceDspChain"/> / <see cref="JuceDspNode"/> /
/// <see cref="JuceDspModuleCatalog"/>). Marshalling + throw→bool only, no
/// policy.
/// <para>
/// <b>This file is not a second copy of the C ABI.</b> The wrapper binds the
/// ~30 <c>dca_engine_dsp_*</c> exports, so the app does not re-declare them
/// against <c>JuceAudioEngine.NativeHandle</c> — a bare, UNPINNED
/// <c>dca_engine*</c>. That bare handle is exactly why
/// <see cref="JuceDspChainService.AttachEngine(nint)"/> has to hold its gate
/// across the detach: nothing else stood between a pedalboard knob drag and a
/// device change freeing the engine underneath it. Every call below pins
/// the engine handle for its duration, so the same race surfaces as a caught
/// <see cref="ObjectDisposedException"/> rather than a read through freed
/// native memory. The gate stays regardless — it is also what keeps the
/// managed mirror coherent across a re-attach.
/// </para>
/// <para>
/// Engine-scoped ops report failure until <see cref="Engine"/> is attached;
/// the static catalogue needs no engine at all and works from process start.
/// </para>
/// </summary>
internal sealed class DspNativeInterop : IDspNativeInterop, IDspMeterNativeInterop, IDspSpilloverNativeInterop
{
    private JuceAudioEngine? _engine;

    /// <summary>
    /// Node handles this interop has seen, by engine-global id.
    /// <para><b>Why a cache.</b> Every node-scoped op here is addressed by ID —
    /// the app's managed mirror is keyed by id and outlives any node object.
    /// When this was written <see cref="JuceDspChain"/> had no
    /// <c>TryGetNode(int)</c>, so the only route from an id to a
    /// <see cref="JuceDspNode"/> was <see cref="JuceDspChain.Nodes"/>, which
    /// costs a P/Invoke and an array per call; a knob drag calls SetParam once
    /// per UI tick. Adds land here directly; a miss falls back to the
    /// enumeration once and sticks. <c>JuceDspChain.TryGetNode</c> now keeps
    /// an id → node map of its own, so the two overlap.</para>
    /// </summary>
    private readonly ConcurrentDictionary<int, JuceDspNode> _nodes = new();

    // Node TYPE and BYPASS: this interop reads neither back. JuceDspNode.TypeId
    // and .Bypass ask the ENGINE (dca_engine_dsp_get_node_type /
    // dca_engine_dsp_get_bypass, ABI v33), but the app has no use for them:
    // JuceDspChainService's mirror records each node's type from the very id it
    // passed to AddNode (a node's type never changes) and its bypass from the
    // SetBypass that succeeded. The mirror is cleared whenever the engine handle
    // changes, so it never outlives the chain it describes.

    /// <param name="player">The seat whose chain this interop addresses
    /// (ABI v28). 0 — the single-player chain — is the default.</param>
    internal DspNativeInterop(int player = 0) => Player = Math.Clamp(player, 0, DspPlayers.MaxPlayers - 1);

    /// <inheritdoc />
    public nint EngineHandle { get; set; }

    /// <inheritdoc />
    public JuceAudioEngine? Engine
    {
        get => _engine;
        set
        {
            // A different engine is a different, freshly created (therefore
            // empty) native chain, so every cached node handle names something
            // that does not exist any more.
            if (!ReferenceEquals(_engine, value)) _nodes.Clear();
            _engine = value;
        }
    }

    /// <inheritdoc />
    public int Player { get; }

    /// <summary>This seat's rig, or null while no engine is attached. Both
    /// halves must agree: a handle with no wrapper behind it (a test harness,
    /// or a caller that only ever had a <c>dca_engine*</c>) must not reach
    /// native, and a wrapper whose handle has been detached must not either —
    /// the detach is the barrier the whole gate exists for.</summary>
    private JuceDspChain? Chain
    {
        get
        {
            var engine = _engine;
            if (engine == null || EngineHandle == nint.Zero) return null;
            try { return engine.GetDspChain(Player); }
            catch (ObjectDisposedException) { return null; }
        }
    }

    private JuceDspNode? Node(int nodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var cached)) return cached;
        var chain = Chain;
        if (chain == null) return null;
        try
        {
            foreach (var node in chain.Nodes)
            {
                _nodes[node.Id] = node;
                if (node.Id == nodeId) cached = node;
            }
        }
        catch (Exception ex) when (IsWrapperFailure(ex)) { return null; }
        return cached;
    }

    // ------------------------------------------------------------- structure

    public int AddNode(string moduleTypeId, bool initiallyBypassed = false)
    {
        var chain = Chain;
        if (chain == null) return -1;
        try
        {
            // ABI v32 parked insertion: created at zero wet, so the user never
            // hears the module's DEFAULTS before the preset's values land.
            var node = initiallyBypassed ? chain.AddNodeBypassed(moduleTypeId) : chain.AddNode(moduleTypeId);
            _nodes[node.Id] = node;
            return node.Id;
        }
        catch (Exception ex) when (IsWrapperFailure(ex)) { return -1; }
    }

    public bool RemoveNode(int nodeId) => Try(chain =>
    {
        chain.RemoveNode(nodeId);
        _nodes.TryRemove(nodeId, out _);
    });

    public bool MoveNode(int nodeId, int newIndex) => Try(chain => chain.MoveNode(nodeId, newIndex));

    public bool Clear() => Try(chain =>
    {
        chain.Clear();
        _nodes.Clear();
    });

    public IReadOnlyList<int> GetNodeIds()
    {
        var chain = Chain;
        if (chain == null) return [];
        try { return chain.NodeIds(); }
        catch (Exception ex) when (IsWrapperFailure(ex)) { return []; }
    }

    // ------------------------------------------------------------ node state

    public bool SetBypass(int nodeId, bool bypassed) => TryNode(nodeId, node => node.Bypass = bypassed);

    // The wrapper refuses a non-finite value with an ArgumentException, and
    // that redundancy is the point: every core's clamp is NaN-transparent, so a
    // NaN that reached the coefficients would silence the module for good.
    // Reported as failure here, exactly as the native reject was.
    public bool SetParam(int nodeId, string paramId, float value)
        => TryNode(nodeId, node => node.SetParam(paramId, value));

    public bool TryGetParam(int nodeId, string paramId, out float value)
    {
        var node = Node(nodeId);
        if (node == null) { value = 0f; return false; }
        try { value = node.GetParam(paramId); return true; }
        catch (Exception ex) when (IsWrapperFailure(ex)) { value = 0f; return false; }
    }

    public bool SetNodeString(int nodeId, string paramId, string value)
        => TryNode(nodeId, node => node.SetString(paramId, value ?? string.Empty));

    public bool TryGetNodeString(int nodeId, string paramId, out string value)
    {
        var node = Node(nodeId);
        if (node == null) { value = string.Empty; return false; }
        // GetString returns empty for a present-but-unset value and throws only
        // for an unknown node/param — the same two outcomes the size-then-copy
        // ABI contract reported as 0 and -1.
        try { value = node.GetString(paramId); return true; }
        catch (Exception ex) when (IsWrapperFailure(ex)) { value = string.Empty; return false; }
    }

    public bool SetNodeBranch(int nodeId, int branch) => TryNode(nodeId, node => node.Branch = branch);

    public bool TryGetNodeBranch(int nodeId, out int branch)
    {
        var node = Node(nodeId);
        if (node == null) { branch = 0; return false; }
        try { branch = node.Branch; return true; }
        catch (Exception ex) when (IsWrapperFailure(ex)) { branch = 0; return false; }
    }

    public bool SetNodeSidechain(int nodeId, JuceDspSidechainSource source)
        => TryNode(nodeId, node => node.Sidechain = source);

    public bool TryGetNodeSidechain(int nodeId, out JuceDspSidechainSource source)
    {
        var node = Node(nodeId);
        if (node == null) { source = JuceDspSidechainSource.None; return false; }
        try { source = node.Sidechain; return true; }
        catch (Exception ex) when (IsWrapperFailure(ex)) { source = JuceDspSidechainSource.None; return false; }
    }

    /// <summary>
    /// <see cref="JuceDspNode.LatencySamples"/> answers −1 rather than throwing
    /// when the engine cannot be acquired, so that is the "no reading" case
    /// here — folded to false so a caller can never print −1 samples as a
    /// latency. A real 0 (most modules, and every bypassed one) is a reading.
    /// </summary>
    public bool TryGetNodeLatency(int nodeId, out int samples)
    {
        samples = 0;
        var node = Node(nodeId);
        if (node == null) return false;
        try
        {
            var value = node.LatencySamples;
            if (value < 0) return false;
            samples = value;
            return true;
        }
        catch (Exception ex) when (IsWrapperFailure(ex)) { return false; }
    }

    // --------------------------------------------------------------- offline

    public bool ProcessBuffer(float[] left, float[] right, int numFrames, int sampleRate)
    {
        var chain = Chain;
        if (chain == null || left == null || right == null) return false;
        if (numFrames <= 0 || numFrames > left.Length || numFrames > right.Length) return false;

        // JuceDspChain.ProcessOffline is INTERLEAVED both ways; this seam is
        // planar and IN-PLACE, which is what the take re-amp and the native
        // round-trip tests are written against. Interleave in, de-interleave
        // out. Offline rendering only, never a hot path — but the wrapper
        // having no planar overload is the whole reason the copy exists.
        var interleaved = new float[numFrames * 2];
        for (var i = 0; i < numFrames; i++)
        {
            interleaved[i * 2] = left[i];
            interleaved[i * 2 + 1] = right[i];
        }

        // JuceAudioStatus.Device is the DELIBERATE refusal while a device is
        // open; the caller already treats false as "could not re-amp".
        try { chain.ProcessOffline(interleaved, channels: 2, sampleRate, interleaved); }
        catch (Exception ex) when (IsWrapperFailure(ex)) { return false; }

        for (var i = 0; i < numFrames; i++)
        {
            left[i] = interleaved[i * 2];
            right[i] = interleaved[i * 2 + 1];
        }
        return true;
    }

    // ------------------------------------------------------------- catalogue
    //
    // No engine required, and read once from native then cached for the process
    // by JuceDspModuleCatalog — the catalogue is a compile-time table on the
    // other side. The index-and-out-parameter shape below is this seam's, kept
    // as it was so JuceDspChainService and its in-memory fake are untouched.

    private static IReadOnlyList<JuceDspModuleInfo> Modules => JuceDspModuleCatalog.Modules;

    public int ModuleTypeCount() => Modules.Count;

    public bool TryModuleTypeInfo(int idx, out string id, out string name)
    {
        var modules = Modules;
        if (idx < 0 || idx >= modules.Count) { id = string.Empty; name = string.Empty; return false; }
        id = modules[idx].Id;
        name = modules[idx].DisplayName.Length > 0 ? modules[idx].DisplayName : id;
        return id.Length > 0;
    }

    public int ModuleParamCount(string moduleTypeId)
        => JuceDspModuleCatalog.Find(moduleTypeId)?.Params.Count ?? 0;

    public bool TryModuleParamInfo(string moduleTypeId, int idx, out DspParamInfo info)
    {
        var ps = JuceDspModuleCatalog.Find(moduleTypeId)?.Params;
        if (ps == null || idx < 0 || idx >= ps.Count)
        {
            info = new DspParamInfo(string.Empty, string.Empty, 0f, 1f, 0f);
            return false;
        }

        var p = ps[idx];
        info = new DspParamInfo(p.Id, p.Name.Length > 0 ? p.Name : p.Id, p.Min, p.Max, p.Default);
        return p.Id.Length > 0;
    }

    /// <summary>ABI v34 capability bit, read off the catalogue the wrapper
    /// already loaded once. Never a managed guess table — this is the ONLY
    /// honest answer to "may this module be keyed", and the engine rejects a
    /// key on anything else.</summary>
    public bool ModuleSupportsSidechain(string moduleTypeId)
        => JuceDspModuleCatalog.Find(moduleTypeId)?.SupportsSidechain ?? false;

    public int ModuleStringParamCount(string moduleTypeId)
        => JuceDspModuleCatalog.Find(moduleTypeId)?.StringParams.Count ?? 0;

    public bool TryModuleStringParamInfo(string moduleTypeId, int idx, out DspStringParamInfo info)
    {
        var ps = JuceDspModuleCatalog.Find(moduleTypeId)?.StringParams;
        if (ps == null || idx < 0 || idx >= ps.Count)
        {
            info = new DspStringParamInfo(string.Empty, string.Empty, string.Empty);
            return false;
        }

        var p = ps[idx];
        info = new DspStringParamInfo(p.Id, p.Name.Length > 0 ? p.Name : p.Id, p.FileFilter);
        return p.Id.Length > 0;
    }

    /// <summary>How many value LABELS a param carries, or −1 when it is a plain
    /// numeric param. −1 and 0 are different answers and folding them together
    /// would claim a labelled param with no labels.</summary>
    public int ModuleParamLabelCount(string moduleTypeId, string paramId)
        => FindParam(moduleTypeId, paramId) is { } p ? p.Labels?.Count ?? -1 : -1;

    public bool TryModuleParamLabel(string moduleTypeId, string paramId, int valueIndex, out string label)
    {
        label = string.Empty;
        var labels = FindParam(moduleTypeId, paramId)?.Labels;
        if (labels == null || valueIndex < 0 || valueIndex >= labels.Count) return false;
        label = labels[valueIndex];
        return true;
    }

    /// <summary>Ordinal quantization step, 0 when continuous, negative for an
    /// unknown module/param — this seam's original three-way contract.</summary>
    public float ModuleParamStep(string moduleTypeId, string paramId)
        => FindParam(moduleTypeId, paramId) is { } p ? p.Step : -1f;

    private static JuceDspParamInfo? FindParam(string moduleTypeId, string paramId)
    {
        var module = JuceDspModuleCatalog.Find(moduleTypeId);
        if (module == null || paramId == null) return null;
        foreach (var p in module.Params)
            if (string.Equals(p.Id, paramId, StringComparison.Ordinal)) return p;
        return null;
    }

    // ---------------------------------------------------------------- meters
    //
    // Still JuceDspMeters, the wrapper's raw-handle facade. JuceDspChain owns
    // the pinned form, but its readers are shaped for a whole UI frame
    // (TryReadMeters, chain-ordered) or for a node OBJECT (JuceDspNode.Meter,
    // which returns `default` and cannot say whether the read failed). This
    // seam is per-node and three-valued, and IDspMeterSource distinguishes "no
    // reading" from "silent" — so the facade is still the closer fit.

    public bool SetMetering(bool enabled) => JuceDspMeters.SetEnabled(EngineHandle, Player, enabled);

    public bool GetMetering() => JuceDspMeters.IsEnabled(EngineHandle, Player);

    public bool TryGetNodeMeter(int nodeId, out DspNodeMeter meter)
    {
        var success = JuceDspMeters.TryRead(EngineHandle, nodeId, out var native);
        meter = new(native.InputPeak, native.InputRms, native.OutputPeak, native.OutputRms);
        return success;
    }

    // ------------------------------------------------------------- spillover

    public bool SetSpillover(bool enabled) => Try(c => c.Spillover = enabled);

    public bool TryGetSpillover(out bool enabled)
    {
        var on = false;
        var ok = Try(c => on = c.Spillover);
        enabled = on;
        return ok;
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// The wrapper reports by THROWING where this seam reports by returning
    /// false. Three shapes are expected and are all this seam's "no": the
    /// engine refused (<see cref="JuceAudioException"/> — unknown
    /// module/node/param, or an offline render attempted against an open
    /// device), the engine was disposed under us
    /// (<see cref="ObjectDisposedException"/>, which is the pinning working),
    /// and an argument the wrapper validates ahead of native (a NaN parameter,
    /// a blank param id). Anything else is a real bug and keeps propagating.
    /// </summary>
    private static bool IsWrapperFailure(Exception ex)
        => ex is JuceAudioException or ObjectDisposedException or ArgumentException;

    private bool Try(Action<JuceDspChain> op)
    {
        var chain = Chain;
        if (chain == null) return false;
        try { op(chain); return true; }
        catch (Exception ex) when (IsWrapperFailure(ex)) { return false; }
    }

    private bool TryNode(int nodeId, Action<JuceDspNode> op)
    {
        var node = Node(nodeId);
        if (node == null) return false;
        try { op(node); return true; }
        catch (Exception ex) when (IsWrapperFailure(ex)) { return false; }
    }
}
