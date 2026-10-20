using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IDspChainService" /> for pedalboard VM tests: a
/// functional chain (so the VM's ordered list can be compared against the
/// chain) plus per-op call recording (so tests can assert the VM drives the
/// service correctly). Each <c>Fail*</c> toggle simulates a native rejection.
/// </summary>
public sealed class FakeDspChainService : IDspChainService, IDspMeterSource
{
    /// <summary>Last engine handle passed to <see cref="AttachEngine"/> (for
    /// assertions); the fake operates purely in memory regardless.</summary>
    public nint AttachedEngine { get; private set; }

    /// <summary>Mirrors the real service: true only while a non-zero handle is
    /// attached. The fake's own chain ops stay in-memory regardless (they model a
    /// CHAIN, not the engine), but this has to be honest — it is what
    /// <c>ToneSessionService</c> asks before restoring a rig, and answering "yes"
    /// unconditionally is how the premature-restore bug stayed invisible to every
    /// test that used this fake.</summary>
    public bool HasEngine => AttachedEngine != nint.Zero;

    /// <summary>The co-op seat this fake stands in for (ABI v28). 0 — the
    /// single-player chain — unless a test is building a per-player set.</summary>
    public int Player { get; init; }

    /// <summary>Spillover as last written (ABI v37); starts at the app default, on.</summary>
    public bool Spillover { get; set; } = true;

    /// <summary>What <see cref="IDspChainService.SupportsSpillover" /> answers; a
    /// test flips it to model an engine that can honour the switch.</summary>
    public bool SupportsSpillover { get; set; }

    /// <summary>Mirrors the real service: a CHANGE to a live handle empties the
    /// chain and raises <see cref="EngineAttached"/>; re-attaching the same
    /// handle is a no-op. Tests of restore-on-attach depend on both halves.</summary>
    public void AttachEngine(nint engineHandle)
    {
        var previous = AttachedEngine;
        AttachedEngine = engineHandle;
        if (previous == engineHandle) return;

        _entries.Clear();
        Scenes = null; // like the real service: the scenes described the chain that went
        if (engineHandle != nint.Zero) EngineAttached?.Invoke();
    }

    /// <summary>The live rig's scenes, held like the real service holds them.</summary>
    public DotCharter.Tone.Audio.Dsp.Scenes.RigSceneSet? Scenes { get; set; }

    private sealed class Entry
    {
        public int Id;
        public string ModuleTypeId = string.Empty;
        public bool Bypassed;
        public int Branch;
        public IReadOnlyList<RigControlAssignment> ControlAssignments = Array.Empty<RigControlAssignment>();
        public Dictionary<string, float> Values = new(StringComparer.Ordinal);
        public Dictionary<string, string> StringValues = new(StringComparer.Ordinal);
    }

    private readonly List<Entry> _entries = [];
    private int _nextId = 1;

    public FakeDspChainService(IReadOnlyList<DspModuleType>? catalog = null) => Catalog = catalog ?? DefaultCatalog();

    public IReadOnlyList<DspModuleType> Catalog { get; }

    public event Action? NodesChanged;
    public event Action? EngineAttached;
    public int NodesChangedCount { get; private set; }

    // --- Recording ---------------------------------------------------------
    public List<string> Adds { get; } = [];
    public List<int> Removes { get; } = [];
    public List<(int NodeId, int NewIndex)> Moves { get; } = [];
    public List<(int NodeId, bool Bypassed)> BypassSets { get; } = [];
    public List<(int NodeId, string ParamId, float Value)> ParamSets { get; } = [];
    public List<(int NodeId, string ParamId, string Value)> StringSets { get; } = [];
    public List<(int NodeId, int Branch)> BranchSets { get; } = [];

    // --- Native-failure simulation ----------------------------------------
    public bool FailAdd { get; set; }
    public bool FailRemove { get; set; }
    public bool FailMove { get; set; }
    public bool FailBypass { get; set; }
    public bool FailParam { get; set; }
    public bool FailSetString { get; set; }
    public bool FailSetBranch { get; set; }

    /// <summary>Runs at the top of every <see cref="GetNodes" /> — a test's way
    /// to hold a caller (the persist timer) inside its snapshot.</summary>
    public Action? OnGetNodes { get; set; }

    public IReadOnlyList<DspNode> GetNodes()
    {
        OnGetNodes?.Invoke();
        return _entries.Select(e => new DspNode(e.Id, e.ModuleTypeId, e.Bypassed, e.Values, e.StringValues, e.Branch, e.ControlAssignments)).ToList();
    }

    /// <summary>Runs at the top of every <see cref="AddNode" /> /
    /// <see cref="RemoveNode" /> — a test's way to hand control to another
    /// thread from INSIDE a plan, which is the only place a two-applier
    /// interleave is observable.</summary>
    public Action? BeforeAddNode { get; set; }

    /// <inheritdoc cref="BeforeAddNode" />
    public Action? BeforeRemoveNode { get; set; }

    public DspNode? AddNode(string moduleTypeId, bool initiallyBypassed = false)
    {
        BeforeAddNode?.Invoke();
        Adds.Add(moduleTypeId);
        if (FailAdd) return null;
        var type = Catalog.FirstOrDefault(m => m.Id == moduleTypeId);
        if (type == null) return null;

        var entry = new Entry { Id = _nextId++, ModuleTypeId = moduleTypeId, Bypassed = initiallyBypassed };
        foreach (var p in type.Params) entry.Values[p.Id] = p.Default;
        _entries.Add(entry);
        RaiseNodesChanged();
        return new DspNode(entry.Id, entry.ModuleTypeId, entry.Bypassed, entry.Values);
    }

    public bool RemoveNode(int nodeId)
    {
        BeforeRemoveNode?.Invoke();
        Removes.Add(nodeId);
        if (FailRemove) return false;
        var removed = _entries.RemoveAll(e => e.Id == nodeId) > 0;
        if (removed) RaiseNodesChanged();
        return removed;
    }

    private void RaiseNodesChanged()
    {
        NodesChangedCount++;
        NodesChanged?.Invoke();
    }

    public bool MoveNode(int nodeId, int newIndex)
    {
        Moves.Add((nodeId, newIndex));
        if (FailMove) return false;
        var current = _entries.FindIndex(e => e.Id == nodeId);
        if (current < 0) return false;

        var target = Math.Clamp(newIndex, 0, _entries.Count - 1);
        var entry = _entries[current];
        _entries.RemoveAt(current);
        _entries.Insert(target, entry);
        return true;
    }

    public bool SetBypass(int nodeId, bool bypassed)
    {
        BypassSets.Add((nodeId, bypassed));
        if (FailBypass) return false;
        var entry = _entries.FirstOrDefault(e => e.Id == nodeId);
        if (entry == null) return false;
        entry.Bypassed = bypassed;
        return true;
    }

    public bool SetControlAssignments(int nodeId, IReadOnlyList<RigControlAssignment> assignments)
    {
        var node = _entries.FirstOrDefault(e => e.Id == nodeId);
        if (node is null || !RigControlAssignments.TryValidate(assignments,
            Catalog.FirstOrDefault(m => m.Id == node.ModuleTypeId), out var copy))
        {
            return false;
        }

        if (node.ControlAssignments.SequenceEqual(copy)) return true;
        node.ControlAssignments = copy;
        RaiseNodesChanged();
        return true;
    }

    public bool SetParam(int nodeId, string paramId, float value)
    {
        ParamSets.Add((nodeId, paramId, value));
        if (FailParam) return false;
        var entry = _entries.FirstOrDefault(e => e.Id == nodeId);
        if (entry == null) return false;
        entry.Values[paramId] = value;
        return true;
    }

    public float? GetParam(int nodeId, string paramId)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == nodeId);
        if (entry != null && entry.Values.TryGetValue(paramId, out var v)) return v;
        return null;
    }

    public bool SetNodeString(int nodeId, string paramId, string value)
    {
        StringSets.Add((nodeId, paramId, value));
        if (FailSetString) return false;
        var entry = _entries.FirstOrDefault(e => e.Id == nodeId);
        if (entry == null) return false;
        // Mirrors the native "" = revert semantics for the fake's own book-keeping.
        entry.StringValues[paramId] = value ?? string.Empty;
        return true;
    }

    public string? GetNodeString(int nodeId, string paramId)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == nodeId);
        if (entry != null && entry.StringValues.TryGetValue(paramId, out var v)) return v;
        return null;
    }

    public bool SetNodeBranch(int nodeId, int branch)
    {
        BranchSets.Add((nodeId, branch));
        if (FailSetBranch) return false;
        if (branch is < 0 or > 3) return false; // mirror the native InvalidArg reject
        var entry = _entries.FirstOrDefault(e => e.Id == nodeId);
        if (entry == null) return false;
        entry.Branch = branch;
        return true;
    }

    public int GetNodeBranch(int nodeId)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == nodeId);
        return entry?.Branch ?? 0;
    }

    public static IReadOnlyList<DspModuleType> DefaultCatalog() =>
    [
        new DspModuleType("overdrive", "Overdrive",
        [
            new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f),
            new DspParamInfo("tone", "Tone", 0f, 1f, 0.7f),
            new DspParamInfo("level", "Level", 0f, 1f, 0.8f),
        ]),
        new DspModuleType("delay", "Delay",
        [
            new DspParamInfo("time", "Time", 0f, 2000f, 350f),
            new DspParamInfo("feedback", "Feedback", 0f, 1f, 0.35f),
            new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f),
        ]),
        new DspModuleType("reverb", "Reverb",
        [
            new DspParamInfo("size", "Size", 0f, 1f, 0.5f),
            new DspParamInfo("mix", "Mix", 0f, 1f, 0.25f),
        ]),
        new DspModuleType("amp", "Amp",
        [
            new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f),
            // v24 model-picker param: named voicings (drives the amp model plate).
            new DspParamInfo("voicing", "Voicing", 0f, 2f, 1f)
            {
                ValueLabels = ["Blackface Twin 85", "Plexi 50", "Recto Dual"]
            },
            new DspParamInfo("master", "Master", 0f, 1f, 0.7f),
        ]),
        new DspModuleType("cab", "Cabinet",
        [
            // v24 model-picker param: named speaker models (drives the cab badge).
            new DspParamInfo("model", "Model", 0f, 3f, 0f)
            {
                ValueLabels = ["1x12 Combo", "2x12 Combo", "4x12 Scooped", "Vintage 4x12"]
            },
            new DspParamInfo("level", "Level", 0f, 1f, 0.8f),
        ]),
        new DspModuleType("octaver", "Octaver",
        [
            new DspParamInfo("sub", "Sub", 0f, 1f, 0.5f),
            new DspParamInfo("mix", "Mix", 0f, 1f, 0.5f),
        ]),
    ];

    // ── metering (IDspMeterSource) ─────────────────────────────────────────
    // A test sets Meters[nodeId] and MeteringActive; the leases are counted so a
    // host that forgets to hand its lease back shows up.

    public Dictionary<int, DspNodeMeter> Meters { get; } = [];
    public bool MeteringActive { get; set; }
    public int MeterLeases { get; private set; }

    public IDisposable AcquireMetering()
    {
        MeterLeases++;
        return new MeterLease(this);
    }

    public bool IsMeteringActive => MeteringActive;

    public bool TryGetNodeMeter(int nodeId, out DspNodeMeter meter) => Meters.TryGetValue(nodeId, out meter);

    /// <summary>Per-node latency in samples; a node with no row answers null
    /// ("not measured"), like an engine that cannot say.</summary>
    public Dictionary<int, int> Latency { get; } = [];

    public int? GetNodeLatencySamples(int nodeId) => Latency.TryGetValue(nodeId, out var samples) ? samples : null;

    private sealed class MeterLease(FakeDspChainService owner) : IDisposable
    {
        private bool _done;
        public void Dispose() { if (!_done) { _done = true; owner.MeterLeases--; } }
    }
}
