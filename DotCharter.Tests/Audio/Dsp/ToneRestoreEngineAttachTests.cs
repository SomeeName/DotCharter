using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio.Dsp;

/// <summary>
///     v0.44 shipped with the tone rig applied at a chain that had no engine, and
///     the owner reported "no tone rig at all". These pin the two halves of that.
///     <para>
///         <b>The bug.</b> <see cref="ToneSessionService.Start" /> restored the
///         player's saved rig whenever the chain reported zero nodes, using that
///         as a proxy for "an engine is attached and its <c>EngineAttached</c> has
///         already fired". A detached chain reports zero nodes too, and on the
///         shipped startup path that is the normal state — the DSP chains are
///         built during DI while the JUCE engine is built lazily on the first
///         audio load. From the owner's own log: catalogue at 12:40:42 (57 module
///         types at the time — 100 since the 2026-09 gear wave — so nothing was wrong with it), the whole restore failing at
///         12:40:48 (<c>Added = 0 … FailedOps = 3</c> for seat 0 and 5 for seat
///         1), the engine finally attaching at 12:40:53.619, and the IDENTICAL
///         rigs applying ten milliseconds later with <c>FailedOps = 0</c>.
///     </para>
///     <para>
///         <b>Why it read as a TOTAL failure rather than noise.</b> The restore
///         is re-entrancy-guarded, and the guard DISCARDED a concurrent attach.
///         The doomed restore runs on the <c>StartAll</c> background task while
///         the real attach runs on whichever thread creates the engine, so the
///         moment the two overlap the genuine <c>EngineAttached</c> is swallowed —
///         and it does not fire again until the next engine rebuild, so the board
///         stays empty for the whole session.
///     </para>
/// </summary>
public sealed class ToneRestoreEngineAttachTests : IDisposable
{
    private const nint EngineA = 0x1000;
    private const nint EngineB = 0x2000;

    /// <summary>Temp directory for the profile-scoped rig store — never the real
    /// config root, which is live owner data.</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static FakeConfigurationService RestoreEnabledConfig()
    {
        var config = new FakeConfigurationService();
        config.Update(c =>
        {
            c.RestoreToneOnStartup = true;
            c.DefaultTonePresetId = string.Empty; // isolate the STORED-rig path
        });
        return config;
    }

    /// <summary>
    ///     The player HAS a saved rig — the precondition every test here needs,
    ///     unchanged in meaning. It moved out of <c>AppConfig.ActiveToneRig</c>
    ///     when the rig became per-PROFILE (0.45): the storage medium changed, the
    ///     fact being asserted did not. Seeding the legacy field instead would now
    ///     exercise the one-shot MIGRATION rather than "a rig is stored", and
    ///     these tests are about neither the config nor the profile — they are
    ///     about not firing a stored rig at a null engine handle.
    /// </summary>
    private ProfileToneRigStore StoreWithSavedRig()
    {
        var store = new ProfileToneRigStore(_dir);
        store.SetRig(0, UserTonePresetDto.FromPreset(new DspTonePreset(
            "active", "Active rig", DspToneCategory.Clean, string.Empty,
            new DcRigPreset([
                new DcRigModule("amp", new Dictionary<string, float>()),
                new DcRigModule("cab", new Dictionary<string, float>())
            ]),
            DspToneOrigin.User)));
        return store;
    }

    private static ToneSessionService Build(
        IDspChainService chain, FakeConfigurationService config, IProfileToneRigStore store)
        => new(chain, config, new DspTonePresetService(chain, config), store);

    [Fact]
    public void StartingBeforeTheEngineExists_DoesNotFireTheRigAtANullEngineHandle()
    {
        // The owner's log, reduced: StartAll ran 5.2 s before the engine was
        // created, so the saved rig was applied to a chain with EngineHandle == 0
        // and every module came back as a failed op.
        var chain = new FakeDspChainService();
        var config = RestoreEnabledConfig();
        var store = StoreWithSavedRig();
        Assert.False(chain.HasEngine);

        using var session = Build(chain, config, store);
        session.Start();

        Assert.Empty(chain.Adds);
        Assert.Empty(chain.GetNodes());
    }

    [Fact]
    public void TheRigIsRestoredWhenTheEngineFinallyArrives()
    {
        // …and the restore is not merely SKIPPED: it has to land on the engine
        // that turns up afterwards, which is the whole point of deferring it.
        var chain = new FakeDspChainService();
        var config = RestoreEnabledConfig();
        var store = StoreWithSavedRig();

        using var session = Build(chain, config, store);
        session.Start();
        chain.AttachEngine(EngineA);

        Assert.Equal(["amp", "cab"], chain.GetNodes().Select(n => n.ModuleTypeId));
    }

    [Fact]
    public void AnEngineREBUILD_StillRestoresTheRig_EvenThoughStartNoLongerRestoresInline()
    {
        // The bug class this repo keeps shipping is state dropped on an engine
        // REBUILD, so the guard has to cover the rebuild and not just the first
        // attach: detach (the device change tears the old engine down) and attach
        // a DIFFERENT handle, which is a fresh, empty native chain.
        var chain = new FakeDspChainService();
        var config = RestoreEnabledConfig();
        var store = StoreWithSavedRig();

        using var session = Build(chain, config, store);
        session.Start();
        chain.AttachEngine(EngineA);
        var afterFirst = chain.GetNodes().Select(n => n.ModuleTypeId).ToList();
        Assert.Equal(["amp", "cab"], afterFirst);

        chain.AttachEngine(nint.Zero);
        Assert.Empty(chain.GetNodes());
        chain.AttachEngine(EngineB);

        Assert.Equal(afterFirst, chain.GetNodes().Select(n => n.ModuleTypeId).ToList());
    }

    [Fact]
    public void AnEngineRebuildLandingMidRestore_IsDeferred_NotDiscarded()
    {
        // The total-failure mode, made deterministic. A restore already in flight
        // belongs to the engine that was live when it started; a rebuild landing
        // on top of it empties the native chain under it. EngineAttached does not
        // fire again until the NEXT rebuild, so dropping this one leaves the board
        // half-built (here: the amp gone) for the rest of the session.
        var chain = new RebuildMidRestoreChain(EngineB);
        var config = RestoreEnabledConfig();
        var store = StoreWithSavedRig();

        using var session = Build(chain, config, store);
        session.Start();
        chain.AttachEngine(EngineA);

        Assert.Equal(EngineB, chain.AttachedEngine);
        Assert.Equal(["amp", "cab"], chain.GetNodes().Select(n => n.ModuleTypeId));
    }

    /// <summary>
    ///     A minimal in-memory chain that rebuilds its engine ONCE, from inside
    ///     the first <see cref="AddNode" /> of the first restore — i.e. a device
    ///     change / NAM 48 kHz request / watchdog recovery landing while the
    ///     restore is between two of its own ops, without a thread race.
    /// </summary>
    private sealed class RebuildMidRestoreChain : IDspChainService
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
        private readonly nint _rebuiltHandle;
        private int _nextId = 1;
        private bool _armed = true;

        public RebuildMidRestoreChain(nint rebuiltHandle) => _rebuiltHandle = rebuiltHandle;

        public nint AttachedEngine { get; private set; }

        public bool HasEngine => AttachedEngine != nint.Zero;

        public int Player => 0;

        public IReadOnlyList<DspModuleType> Catalog { get; } =
        [
            new DspModuleType("amp", "Amp", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f)]),
            new DspModuleType("cab", "Cabinet", [new DspParamInfo("level", "Level", 0f, 1f, 0.8f)])
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

        public IReadOnlyList<DspNode> GetNodes() => _nodes
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

            if (_armed)
            {
                // The rebuild lands AFTER this node reached the chain, so it takes
                // that node with it — only a deferred second restore can put it
                // back.
                _armed = false;
                AttachEngine(_rebuiltHandle);
            }

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
