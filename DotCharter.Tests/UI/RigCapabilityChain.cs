using DotCharter.Tone.Audio.Dsp;
using JuceSharp.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     An in-memory <see cref="IDspChainService" /> that answers the FOUR
///     capabilities the 0.9 gear wave needs: the ABI-v34 sidechain key, the
///     per-node latency, a param a transport polls, and a string param the
///     engine can REFUSE.
///     <para>
///         Deliberately its own fake rather than an extension of
///         <c>FakeDspChainService</c>: that one belongs to the board tests, it
///         is sealed, and none of its many callers should have to grow a
///         sidechain or a latency table to keep compiling. It is a real chain,
///         not a stub — <c>PedalboardViewModel</c> drives it end to end in the
///         headless smoke test.
///     </para>
/// </summary>
internal sealed class RigCapabilityChain : IDspChainService
{
    private sealed class Entry
    {
        public int Id;
        public string ModuleTypeId = string.Empty;
        public bool Bypassed;
        public int Branch;
        public readonly Dictionary<string, float> Values = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> StringValues = new(StringComparer.Ordinal);
        public JuceDspSidechainSource Sidechain;
    }

    private readonly List<Entry> _entries = [];
    private int _nextId = 1;

    public RigCapabilityChain(IReadOnlyList<DspModuleType> catalog) => Catalog = catalog;

    public IReadOnlyList<DspModuleType> Catalog { get; }

    /// <summary>Per-node latency in samples; a node with no row reports 0 —
    /// "measured, adds nothing", which is what most modules really are.</summary>
    public Dictionary<int, int> Latency { get; } = [];

    public List<(int Node, string Param, float Value)> ParamSets { get; } = [];
    public List<(int Node, JuceDspSidechainSource Source)> SidechainSets { get; } = [];

    public bool FailSidechain { get; init; }
    public bool FailSetString { get; set; }

    /// <summary>False stands in for a detached engine: the chain cannot answer
    /// at all, which is NOT the same as answering zero.</summary>
    public bool AnswersLatency { get; init; } = true;

    public void AttachEngine(nint engineHandle) { }
    public int Player => 0;
    public bool HasEngine => true;

    public event Action? NodesChanged;
    public event Action? EngineAttached;

    public IReadOnlyList<DspNode> GetNodes() => _entries
        .Select(e => new DspNode(e.Id, e.ModuleTypeId, e.Bypassed, e.Values, e.StringValues, e.Branch,
            null, e.Sidechain))
        .ToList();

    public DspNode? AddNode(string moduleTypeId, bool initiallyBypassed = false)
    {
        var type = Catalog.FirstOrDefault(m => m.Id == moduleTypeId);
        if (type is null) return null;

        var entry = new Entry { Id = _nextId++, ModuleTypeId = moduleTypeId, Bypassed = initiallyBypassed };
        foreach (var p in type.Params) entry.Values[p.Id] = p.Default;
        _entries.Add(entry);
        NodesChanged?.Invoke();
        return new DspNode(entry.Id, entry.ModuleTypeId, entry.Bypassed, entry.Values);
    }

    public bool RemoveNode(int nodeId)
    {
        if (_entries.RemoveAll(e => e.Id == nodeId) == 0) return false;
        NodesChanged?.Invoke();
        return true;
    }

    public bool MoveNode(int nodeId, int newIndex)
    {
        var current = _entries.FindIndex(e => e.Id == nodeId);
        if (current < 0) return false;
        var entry = _entries[current];
        _entries.RemoveAt(current);
        _entries.Insert(Math.Clamp(newIndex, 0, _entries.Count), entry);
        return true;
    }

    public bool SetBypass(int nodeId, bool bypassed)
    {
        var entry = Find(nodeId);
        if (entry is null) return false;
        entry.Bypassed = bypassed;
        return true;
    }

    public bool SetParam(int nodeId, string paramId, float value)
    {
        ParamSets.Add((nodeId, paramId, value));
        var entry = Find(nodeId);
        if (entry is null) return false;
        entry.Values[paramId] = value;
        return true;
    }

    public float? GetParam(int nodeId, string paramId)
        => Find(nodeId) is { } entry && entry.Values.TryGetValue(paramId, out var v) ? v : null;

    /// <summary>Sets a param the way the ENGINE does — no managed push, so the
    /// only way a control learns about it is by polling.</summary>
    public void SetParamFromEngine(int nodeId, string paramId, float value)
    {
        if (Find(nodeId) is { } entry) entry.Values[paramId] = value;
    }

    public bool SetNodeString(int nodeId, string paramId, string value)
    {
        if (FailSetString) return false;
        var entry = Find(nodeId);
        if (entry is null) return false;
        entry.StringValues[paramId] = value ?? string.Empty;
        return true;
    }

    public string? GetNodeString(int nodeId, string paramId)
        => Find(nodeId) is { } entry && entry.StringValues.TryGetValue(paramId, out var v) ? v : null;

    public bool SetNodeBranch(int nodeId, int branch)
    {
        var entry = Find(nodeId);
        if (entry is null || branch is < 0 or > 3) return false;
        entry.Branch = branch;
        return true;
    }

    public int GetNodeBranch(int nodeId) => Find(nodeId)?.Branch ?? 0;

    public bool SetNodeSidechain(int nodeId, JuceDspSidechainSource source)
    {
        if (FailSidechain) return false;
        var entry = Find(nodeId);
        // The ENGINE's own rule, mirrored: a key on a module type that does not
        // advertise the capability is REJECTED, never quietly stored.
        if (entry is null) return false;
        if (source != JuceDspSidechainSource.None &&
            Catalog.FirstOrDefault(m => m.Id == entry.ModuleTypeId) is not { SupportsSidechain: true })
        {
            return false;
        }

        SidechainSets.Add((nodeId, source));
        entry.Sidechain = source;
        return true;
    }

    public JuceDspSidechainSource GetNodeSidechain(int nodeId)
        => Find(nodeId)?.Sidechain ?? JuceDspSidechainSource.None;

    public int? GetNodeLatencySamples(int nodeId)
    {
        if (!AnswersLatency) return null;
        return Latency.TryGetValue(nodeId, out var samples) ? samples : 0;
    }

    private Entry? Find(int nodeId) => _entries.FirstOrDefault(e => e.Id == nodeId);

    /// <summary>Raises <see cref="EngineAttached" />; kept so the event is not
    /// dead code on a fake nothing rebuilds.</summary>
    public void RaiseEngineAttached() => EngineAttached?.Invoke();
}
