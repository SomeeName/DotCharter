using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio.Dsp;

/// <summary>
///     <b>A half-built rig must never be written over the player's saved one.</b>
///     <para>
///         <c>ToneSessionService.PersistIfChanged</c> took its <c>_restoring</c>
///         check under <c>_gate</c>, released the lock, and then ran BOTH the
///         chain snapshot and the store write unlocked. So the 1 s persist timer
///         could pass the check, a device change / NAM 48 kHz request / watchdog
///         recovery could raise <c>EngineAttached</c>, and the snapshot then
///         landed part-way through the restore's one-node-at-a-time rebuild. Two
///         of a six-pedal board is non-empty, so the <c>Count == 0</c> guard —
///         which exists for exactly this class of loss — let it straight through
///         and <c>tone-rig.json</c> was overwritten with the truncated board.
///     </para>
///     <para>
///         The existing coverage is the DEFERRAL path (a restore arriving while
///         one is running). Nothing covered persist-during-restore, which is the
///         opposite direction and the one that loses data.
///     </para>
///     <para>
///         Driven with no threads: the fake chain runs the restore from inside
///         the persist's own <c>GetNodes()</c> call and hands back the partial
///         board the persist thread would really have got, so the interleaving is
///         exact and the test is deterministic. Modelled on
///         <c>ProfileToneRigTests.SwitchProfileMidRestoreChain</c>.
///     </para>
/// </summary>
public sealed class TonePersistDuringRestoreTests : IDisposable
{
    private const nint EngineA = 0x1000;
    private const nint EngineB = 0x2000;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static UserTonePresetDto Rig(params string[] modules)
        => UserTonePresetDto.FromPreset(new DspTonePreset(
            "active", "Active rig", DspToneCategory.Clean, string.Empty,
            new DcRigPreset([.. modules.Select(m => new DcRigModule(m, new Dictionary<string, float>(), false))]),
            DspToneOrigin.User));

    private static string[] Stored(IProfileToneRigStore store)
        => store.GetRig(0)?.Modules.Select(m => m.ModuleTypeId).ToArray() ?? [];

    /// <summary>THE regression. The persist's snapshot spans a restore, so what it
    /// holds is two thirds of a board — and the saved rig must survive intact.</summary>
    [Fact]
    public void APersistWhoseSnapshotSpansARestore_DoesNotTruncateTheSavedRig()
    {
        var store = new ProfileToneRigStore(_dir);
        store.SetRig(0, Rig("overdrive", "delay", "reverb"));

        var chain = new PersistDuringRestoreChain();
        var config = new FakeConfigurationService();
        using var session = new ToneSessionService(chain, config, new DspTonePresetService(chain, config), store);

        session.Start();
        chain.AttachEngine(EngineA);
        Assert.Equal(["overdrive", "delay", "reverb"], chain.GetNodes().Select(n => n.ModuleTypeId));

        // Arm the exact interleaving: the persist is between its _restoring check
        // and its read of the chain; a device change lands there.
        chain.OnNextRead(truncateTo: 1, then: () => chain.AttachEngine(EngineB));

        session.Flush();

        // The board on disk is still the whole board.
        Assert.Equal(["overdrive", "delay", "reverb"], Stored(store));
        // …and the live chain came back whole too, so the restore itself worked.
        Assert.Equal(["overdrive", "delay", "reverb"], chain.GetNodes().Select(n => n.ModuleTypeId));
    }

    /// <summary>
    /// The other half, or the fix would be indistinguishable from "never
    /// persists": an ordinary edit that does NOT span a restore is still saved.
    /// </summary>
    [Fact]
    public void AnOrdinaryPersistStillWritesTheBoard()
    {
        var store = new ProfileToneRigStore(_dir);
        store.SetRig(0, Rig("overdrive", "delay", "reverb"));

        var chain = new PersistDuringRestoreChain();
        var config = new FakeConfigurationService();
        using var session = new ToneSessionService(chain, config, new DspTonePresetService(chain, config), store);

        session.Start();
        chain.AttachEngine(EngineA);

        chain.AddNode("chorus");
        session.Flush();

        Assert.Equal(["overdrive", "delay", "reverb", "chorus"], Stored(store));
    }

    /// <summary>
    ///     An in-memory chain that can hand a persist the PARTIAL board it would
    ///     really have seen. On the armed read it truncates itself, captures that
    ///     truncated view, runs the supplied action (the restore) and then returns
    ///     the capture — which is exactly what a snapshot taken across a restore
    ///     produces, with no threads and no timing.
    /// </summary>
    private sealed class PersistDuringRestoreChain : IDspChainService
    {
        private sealed class Entry
        {
            public int Id;
            public string ModuleTypeId = string.Empty;
            public bool Bypassed;
            public int Branch;
            public readonly Dictionary<string, float> Values = new(StringComparer.Ordinal);
            public readonly Dictionary<string, string> StringValues = new(StringComparer.Ordinal);
        }

        private readonly List<Entry> _nodes = [];
        private int _nextId = 1;
        private int _truncateTo = -1;
        private Action? _onRead;

        public void OnNextRead(int truncateTo, Action then)
        {
            _truncateTo = truncateTo;
            _onRead = then;
        }

        public nint AttachedEngine { get; private set; }
        public bool HasEngine => AttachedEngine != nint.Zero;
        public int Player => 0;

        public IReadOnlyList<DspModuleType> Catalog { get; } =
        [
            new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f)]),
            new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
            new DspModuleType("reverb", "Reverb", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.2f)]),
            new DspModuleType("chorus", "Chorus", [new DspParamInfo("depth", "Depth", 0f, 1f, 0.4f)])
        ];

        public event Action? NodesChanged;
        public event Action? EngineAttached;

        public void AttachEngine(nint engineHandle)
        {
            var previous = AttachedEngine;
            AttachedEngine = engineHandle;
            if (previous == engineHandle) return;

            _nodes.Clear();
            NodesChanged?.Invoke();
            if (engineHandle != nint.Zero) EngineAttached?.Invoke();
        }

        public IReadOnlyList<DspNode> GetNodes()
        {
            var hook = _onRead;
            if (hook is null) return Project();

            _onRead = null;
            // The restore has re-added this many of the saved nodes so far.
            if (_truncateTo >= 0 && _nodes.Count > _truncateTo)
                _nodes.RemoveRange(_truncateTo, _nodes.Count - _truncateTo);
            var partial = Project();
            // …and the rest of it completes while the reader holds that view.
            hook();
            return partial;
        }

        private List<DspNode> Project() => _nodes
            .Select(e => new DspNode(e.Id, e.ModuleTypeId, e.Bypassed, e.Values, e.StringValues, e.Branch))
            .ToList();

        public DspNode? AddNode(string moduleTypeId, bool initiallyBypassed = false)
        {
            if (!HasEngine) return null;
            var type = Catalog.FirstOrDefault(t => t.Id == moduleTypeId);
            if (type is null) return null;

            var entry = new Entry { Id = _nextId++, ModuleTypeId = moduleTypeId, Bypassed = initiallyBypassed };
            foreach (var p in type.Params) entry.Values[p.Id] = p.Default;
            _nodes.Add(entry);

            NodesChanged?.Invoke();
            return new DspNode(entry.Id, entry.ModuleTypeId, entry.Bypassed, entry.Values);
        }

        public bool RemoveNode(int nodeId) => _nodes.RemoveAll(e => e.Id == nodeId) > 0;

        public bool MoveNode(int nodeId, int newIndex)
        {
            var current = _nodes.FindIndex(e => e.Id == nodeId);
            if (current < 0) return false;
            var target = Math.Clamp(newIndex, 0, _nodes.Count - 1);
            var entry = _nodes[current];
            _nodes.RemoveAt(current);
            _nodes.Insert(target, entry);
            return true;
        }

        public bool SetBypass(int nodeId, bool bypassed)
        {
            var entry = _nodes.FirstOrDefault(e => e.Id == nodeId);
            if (entry is null) return false;
            entry.Bypassed = bypassed;
            return true;
        }

        public bool SetParam(int nodeId, string paramId, float value)
        {
            var entry = _nodes.FirstOrDefault(e => e.Id == nodeId);
            if (entry is null) return false;
            entry.Values[paramId] = value;
            return true;
        }

        public float? GetParam(int nodeId, string paramId)
            => _nodes.FirstOrDefault(e => e.Id == nodeId) is { } entry
               && entry.Values.TryGetValue(paramId, out var v) ? v : null;

        public bool SetNodeString(int nodeId, string paramId, string value)
        {
            var entry = _nodes.FirstOrDefault(e => e.Id == nodeId);
            if (entry is null) return false;
            entry.StringValues[paramId] = value ?? string.Empty;
            return true;
        }

        public string? GetNodeString(int nodeId, string paramId)
            => _nodes.FirstOrDefault(e => e.Id == nodeId) is { } entry
               && entry.StringValues.TryGetValue(paramId, out var v) ? v : null;

        public bool SetNodeBranch(int nodeId, int branch)
        {
            var entry = _nodes.FirstOrDefault(e => e.Id == nodeId);
            if (entry is null) return false;
            entry.Branch = branch;
            return true;
        }

        public int GetNodeBranch(int nodeId) => _nodes.FirstOrDefault(e => e.Id == nodeId)?.Branch ?? 0;
    }
}
