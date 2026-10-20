using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// Tests the managed DSP-chain wrapper's coordination logic against a fake
/// native interop (the P/Invoke seam). Proves catalogue mapping, the managed
/// chain mirror, the engine-detached guard and native-exception safety without
/// linking the real native.
/// </summary>
public sealed class JuceDspChainServiceTests
{
    /// <summary>
    ///     The one cap the barrier test waits on. It is a DEADLOCK DETECTOR: a
    ///     green run crosses each rendezvous in microseconds and never reads this
    ///     value, so it is sized to outlast any amount of CPU starvation rather
    ///     than to bound how long the product may take. Nothing here asserts a
    ///     duration.
    /// </summary>
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(60);

    private static JuceDspChainService NewLoaded(out FakeDspNativeInterop native)
    {
        native = new FakeDspNativeInterop();
        var svc = new JuceDspChainService(native);
        svc.LoadCatalog();
        return svc;
    }

    [Fact]
    public void ControlAssignments_AreCopiedValidatedAndReplayAfterEngineRebuild()
    {
        var svc = NewLoaded(out _);
        var node = svc.AddNode("overdrive")!;
        var input = new[] { new RigControlAssignment("level", 0, 0, 1, 0, 7) };
        var notifications = 0;
        svc.NodesChanged += () => { notifications++; Assert.NotNull(svc.GetNodes()); };
        Assert.True(svc.SetControlAssignments(node.NodeId, input));
        Assert.Equal(1, notifications);
        var old = svc.GetNodes()[0];
        input[0] = input[0] with { MacroIndex = 3 };
        Assert.Equal(0, Assert.Single(old.ControlAssignments).MacroIndex);
        Assert.False(svc.SetControlAssignments(node.NodeId, [input[0] with { Max = float.NaN }]));
        var snapshot = DcRigSnapshot.FromNodes(svc.GetNodes());
        svc.AttachEngine(456);
        Assert.Empty(svc.GetNodes());
        new DcRigApplier(svc).Apply(snapshot);
        Assert.Equal(0, Assert.Single(svc.GetNodes()[0].ControlAssignments).MacroIndex);
        Assert.True(svc.SetControlAssignments(svc.GetNodes()[0].NodeId, []));
        Assert.Empty(svc.GetNodes()[0].ControlAssignments);
        Assert.Single(old.ControlAssignments);
    }

    [Fact]
    public void LoadCatalog_maps_types_and_params_from_native()
    {
        var svc = NewLoaded(out _);

        Assert.Equal(new[] { "overdrive", "delay", "cab", "miku" }, svc.Catalog.Select(m => m.Id).ToArray());
        var od = svc.Catalog.First(m => m.Id == "overdrive");
        Assert.Equal("Overdrive", od.DisplayName);
        Assert.Equal(new[] { "drive", "level" }, od.Params.Select(p => p.Id).ToArray());
        Assert.Equal(0.5f, od.Params.First(p => p.Id == "drive").Default);
        Assert.Empty(od.StringParams);
    }

    [Fact]
    public void LoadCatalog_maps_string_params_with_file_filter()
    {
        var svc = NewLoaded(out _);

        var cab = svc.Catalog.First(m => m.Id == "cab");
        var ir = Assert.Single(cab.StringParams);
        Assert.Equal("irPath", ir.Id);
        Assert.Equal("Impulse Response", ir.DisplayName);
        Assert.Equal("*.wav", ir.FileFilter);

        // free-text string params advertise an empty filter (no picker).
        var miku = svc.Catalog.First(m => m.Id == "miku");
        var phrases = Assert.Single(miku.StringParams);
        Assert.Equal("phrases", phrases.Id);
        Assert.Equal(string.Empty, phrases.FileFilter);
    }

    [Fact]
    public void LoadCatalog_maps_value_labels_for_model_picker_params()
    {
        var svc = NewLoaded(out _);

        // A labelled ("model picker") param carries its full label set (ABI v24).
        var drive = svc.Catalog.First(m => m.Id == "overdrive").Params.First(p => p.Id == "drive");
        Assert.NotNull(drive.ValueLabels);
        Assert.Equal(new[] { "Generic", "Screamer", "Rat", "Metal" }, drive.ValueLabels!);

        // A continuous (numeric) param stays label-free — the UI keeps a knob.
        var level = svc.Catalog.First(m => m.Id == "overdrive").Params.First(p => p.Id == "level");
        Assert.Null(level.ValueLabels);
    }

    [Fact]
    public void SetNodeString_pushes_native_and_GetNodeString_reads_it_back()
    {
        var svc = NewLoaded(out var native);
        var node = svc.AddNode("cab")!;

        Assert.True(svc.SetNodeString(node.NodeId, "irPath", "C:/irs/marshall.wav"));

        Assert.Equal("C:/irs/marshall.wav", native.Strings[(node.NodeId, "irPath")]); // pushed natively
        Assert.Equal("C:/irs/marshall.wav", svc.GetNodeString(node.NodeId, "irPath")); // read back
        Assert.Equal("C:/irs/marshall.wav", svc.GetNodes().Single().StringValues["irPath"]); // mirror updated
    }

    [Fact]
    public void SetNodeString_empty_reverts_and_is_cached()
    {
        var svc = NewLoaded(out _);
        var node = svc.AddNode("cab")!;
        svc.SetNodeString(node.NodeId, "irPath", "C:/irs/marshall.wav");

        Assert.True(svc.SetNodeString(node.NodeId, "irPath", string.Empty));

        Assert.Equal(string.Empty, svc.GetNodeString(node.NodeId, "irPath"));
    }

    [Fact]
    public void GetNodeString_falls_back_to_cache_when_native_read_unavailable()
    {
        // A native that accepts the set but never reads back (returns unknown)
        // still resolves via the managed cache — mirrors GetParam's fallback.
        var native = new WriteOnlyStringInterop();
        var svc = new JuceDspChainService(native);
        svc.LoadCatalog();
        var node = svc.AddNode("miku")!;

        Assert.True(svc.SetNodeString(node.NodeId, "phrases", "mi ku"));
        Assert.Equal("mi ku", svc.GetNodeString(node.NodeId, "phrases")); // from cache
    }

    [Fact]
    public void SetNodeString_failed_native_does_not_advance_the_cache()
    {
        // A rejected set (e.g. failed IR load) returns false and the mirror keeps
        // the previous value — matching the native "keeps previous state" contract.
        var native = new RejectingStringInterop();
        var svc = new JuceDspChainService(native);
        svc.LoadCatalog();
        var node = svc.AddNode("cab")!;

        Assert.False(svc.SetNodeString(node.NodeId, "irPath", "C:/bad.wav"));
        Assert.Null(svc.GetNodeString(node.NodeId, "irPath")); // never cached
    }

    [Fact]
    public void SetNodeBranch_pushes_native_and_mirrors_and_GetNodeBranch_reads_it_back()
    {
        var svc = NewLoaded(out var native);
        var node = svc.AddNode("overdrive")!;

        Assert.Equal(0, svc.GetNodeBranch(node.NodeId)); // default before any set

        Assert.True(svc.SetNodeBranch(node.NodeId, 2));
        Assert.Equal(2, native.Branches[node.NodeId]);          // pushed natively
        Assert.Equal(2, svc.GetNodeBranch(node.NodeId));        // read back (native)
        Assert.Equal(2, svc.GetNodes().Single().Branch);        // mirror hydrated
    }

    [Fact]
    public void SetNodeBranch_out_of_range_is_rejected_and_mirror_unchanged()
    {
        var svc = NewLoaded(out _);
        var node = svc.AddNode("overdrive")!;
        svc.SetNodeBranch(node.NodeId, 1);

        Assert.False(svc.SetNodeBranch(node.NodeId, 4)); // native InvalidArg
        Assert.Equal(1, svc.GetNodes().Single().Branch); // kept previous
    }

    [Fact]
    public void GetNodeBranch_falls_back_to_cache_when_native_read_unavailable()
    {
        var native = new WriteOnlyBranchInterop();
        var svc = new JuceDspChainService(native);
        svc.LoadCatalog();
        var node = svc.AddNode("overdrive")!;

        Assert.True(svc.SetNodeBranch(node.NodeId, 3));
        Assert.Equal(3, svc.GetNodeBranch(node.NodeId)); // from mirror cache
    }

    [Fact]
    public void GetNodeBranch_unknown_node_returns_zero()
    {
        var svc = NewLoaded(out _);
        Assert.Equal(0, svc.GetNodeBranch(999));
    }

    private sealed class WriteOnlyBranchInterop : FakeDspNativeInterop
    {
        // Accepts the write but reports the tag as unknown on read-back.
        public override bool SetNodeBranch(int nodeId, int branch) => branch is >= 0 and <= 3;
    }

    private sealed class WriteOnlyStringInterop : FakeDspNativeInterop
    {
        // Accepts the write but reports the value as unknown on read-back.
        public override bool SetNodeString(int nodeId, string paramId, string value) => true;
    }

    private sealed class RejectingStringInterop : FakeDspNativeInterop
    {
        public override bool SetNodeString(int nodeId, string paramId, string value) => false;
    }

    [Fact]
    public void AddNode_returns_node_with_default_values_and_mirrors_chain()
    {
        var svc = NewLoaded(out _);

        var node = svc.AddNode("overdrive");

        Assert.NotNull(node);
        Assert.Equal("overdrive", node!.ModuleTypeId);
        Assert.Equal(0.5f, node.Values["drive"]);
        Assert.Equal(0.8f, node.Values["level"]);
        Assert.Equal(new[] { node.NodeId }, svc.GetNodes().Select(n => n.NodeId).ToArray());
    }

    [Fact]
    public void AddNode_unknown_type_returns_null()
    {
        var svc = NewLoaded(out _);
        Assert.Null(svc.AddNode("does-not-exist"));
        Assert.Empty(svc.GetNodes());
    }

    [Fact]
    public void MoveNode_reorders_the_mirror()
    {
        var svc = NewLoaded(out _);
        var a = svc.AddNode("overdrive")!;
        var b = svc.AddNode("delay")!;

        Assert.True(svc.MoveNode(a.NodeId, 1));

        Assert.Equal(new[] { b.NodeId, a.NodeId }, svc.GetNodes().Select(n => n.NodeId).ToArray());
    }

    [Fact]
    public void RemoveNode_drops_from_the_mirror()
    {
        var svc = NewLoaded(out _);
        var a = svc.AddNode("overdrive")!;
        var b = svc.AddNode("delay")!;

        Assert.True(svc.RemoveNode(a.NodeId));

        Assert.Equal(new[] { b.NodeId }, svc.GetNodes().Select(n => n.NodeId).ToArray());
    }

    [Fact]
    public void SetParam_caches_and_GetParam_reads_native()
    {
        var svc = NewLoaded(out var native);
        var node = svc.AddNode("overdrive")!;

        Assert.True(svc.SetParam(node.NodeId, "drive", 0.9f));

        Assert.Equal(0.9f, native.Params[(node.NodeId, "drive")]); // pushed natively
        Assert.Equal(0.9f, svc.GetParam(node.NodeId, "drive"));      // read back from native
        Assert.Equal(0.9f, svc.GetNodes().Single().Values["drive"]); // mirror updated
    }

    [Fact]
    public void SetBypass_updates_the_mirror()
    {
        var svc = NewLoaded(out _);
        var node = svc.AddNode("overdrive")!;

        Assert.True(svc.SetBypass(node.NodeId, true));

        Assert.True(svc.GetNodes().Single().Bypassed);
    }

    [Fact]
    public void Engine_detached_guards_chain_ops_but_catalogue_still_loads()
    {
        var native = new FakeDspNativeInterop { EngineHandle = nint.Zero };
        var svc = new JuceDspChainService(native);
        svc.LoadCatalog();

        // Static catalogue works without an engine…
        Assert.NotEmpty(svc.Catalog);
        // …but engine-scoped ops fail safely instead of crashing.
        Assert.Null(svc.AddNode("overdrive"));
        Assert.Empty(svc.GetNodes());
    }

    [Fact]
    public void Native_exception_is_swallowed_and_reported_as_failure()
    {
        var native = new ThrowingInterop();
        var svc = new JuceDspChainService(native);
        svc.LoadCatalog();

        // AddNode's native call throws; the wrapper returns null, no exception.
        Assert.Null(svc.AddNode("overdrive"));
    }

    // ---- Engine replace resets the mirror -----------------------------------
    // A device change / buffer-size change / watchdog recovery destroys the engine
    // and builds a fresh one, whose native chain is EMPTY. The managed mirror is
    // authoritative for the UI-facing composition, so anything surviving that swap
    // is a phantom pedal — and the worst consumer is DcRigApplier, which diffs the
    // user's preset against the mirror and reports NoOp with zero native calls when
    // it matches. That is "my tone is gone and re-picking it does nothing".

    [Fact]
    public void AttachEngine_ToADifferentEngine_EmptiesTheManagedMirror()
    {
        var svc = NewLoaded(out var native);
        svc.AddNode("overdrive");
        svc.AddNode("cab");
        Assert.Equal(2, svc.GetNodes().Count);

        var raised = 0;
        svc.NodesChanged += () => raised++;

        // ReplaceEngineGated: detach from the dying engine, then attach the new one.
        svc.AttachEngine(nint.Zero);
        svc.AttachEngine(0x2000);

        Assert.Empty(svc.GetNodes());
        Assert.Equal(0x2000, native.EngineHandle);
        // Exactly one notification: the detach dropped the pedals, the attach found
        // nothing left to drop.
        Assert.Equal(1, raised);
    }

    [Fact]
    public void AttachEngine_ToTheSameEngine_KeepsTheMirror()
    {
        var svc = NewLoaded(out var native);
        svc.AddNode("overdrive");

        var raised = 0;
        svc.NodesChanged += () => raised++;

        // ApplyEngineConfig re-asserts the attachment defensively after an IN-PLACE
        // Reconfigure (a sample-rate or buffer change on the same device). That
        // engine kept its native chain, so dropping the mirror here would
        // manufacture the very bug the change above fixes.
        svc.AttachEngine(native.EngineHandle);

        Assert.Equal("overdrive", svc.GetNodes().Single().ModuleTypeId);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void AfterAnEngineReplace_ReapplyingTheSameTone_RebuildsTheChain_InsteadOfNoOpping()
    {
        var svc = NewLoaded(out _);
        var rig = new DcRigPreset([
            new DcRigModule("overdrive", new Dictionary<string, float> { ["drive"] = 0.5f, ["level"] = 0.8f }, false),
            new DcRigModule("cab", new Dictionary<string, float> { ["level"] = 0.8f }, false)
        ]);

        // The user's tone is live on the current engine.
        Assert.Equal(2, new DcRigApplier(svc).Apply(rig).Added);

        // A device change: the engine is destroyed and rebuilt, so the native chain
        // is empty again.
        svc.AttachEngine(nint.Zero);
        svc.AttachEngine(0x2000);

        // Re-picking the tone must actually REBUILD it. With a stale mirror the
        // applier saw a chain that already matched and returned NoOp having issued
        // zero native calls — the rig stayed silent until the user hand-edited it.
        var reapply = new DcRigApplier(svc).Apply(rig);
        Assert.False(reapply.NoOp);
        Assert.Equal(2, reapply.Added);
        Assert.Equal(["overdrive", "cab"], svc.GetNodes().Select(n => n.ModuleTypeId));
    }

    /// <summary>
    ///     The documented mitigation is "detach the DSP tone rig BEFORE destroying
    ///     an engine — <c>AttachEngine(nint.Zero)</c> first, always". That only
    ///     holds if the detach WAITS: <c>ReplaceEngineGated</c> frees the
    ///     <c>dca_engine*</c> on the very next line, and
    ///     <c>IDspNativeInterop.EngineHandle</c> is a bare unpinned pointer with no
    ///     <c>NativeHandleOwner</c> drain behind it. A knob drag already inside
    ///     <c>dca_engine_dsp_set_param</c> when the detach returns writes into freed
    ///     memory.
    ///     <para>
    ///         <b>Asserted as an ORDERING, not a duration.</b> This case used to
    ///         say <c>Assert.NotSame(detach, await Task.WhenAny(detach,
    ///         Task.Delay(250, ct)))</c> — "the detach has not finished within
    ///         250 ms" — which is a wall-clock assertion in the DEFAULT gate, the
    ///         one thing the root <c>AGENTS.md</c> bans outright. It failed twice
    ///         under a loaded box and passed in isolation, i.e. it flaked exactly
    ///         the way that rule predicts, and a 250 ms budget is also not what the
    ///         invariant says: the claim is that the handle write happens AFTER the
    ///         in-flight native call leaves, however long either takes.
    ///     </para>
    ///     <para>
    ///         So the fake records the two events in the order they really happen —
    ///         the knob leaving native, and the detach writing the handle — and
    ///         the sequence itself is the assertion. No delay, no stopwatch, and it
    ///         cannot pass by being slow.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task AttachEngine_WaitsForAnInFlightNativeWrite_SoDetachIsARealBarrier()
    {
        var ct = TestContext.Current.CancellationToken;
        var native = new BlockingSetParamInterop();
        var svc = new JuceDspChainService(native);
        svc.LoadCatalog();
        var node = svc.AddNode("overdrive")!;
        var live = native.EngineHandle;

        // A pedalboard knob turn, now parked INSIDE the native call.
        //
        // Every cap below is a DEADLOCK DETECTOR, never a budget: a green run
        // never reaches one. They were 5 s, which is a budget — on a box running
        // eight agents the thread pool does not schedule `Task.Run` inside it, and
        // the class failed with "the barrier never engaged" messages about a
        // barrier that was working perfectly.
        var knob = Task.Run(() => svc.SetParam(node.NodeId, "drive", 0.9f), ct);
        Assert.True(native.Entered.Wait(Rendezvous, ct), "the knob write never entered native");

        // A device change detaching the rig. The RENDEZVOUS is what replaces the
        // old sleep, and it has to be a real one: an earlier draft only waited for
        // the detaching thread to be STARTED, and the test thread then went
        // straight on to release the knob — so even with the barrier deliberately
        // removed the detaching thread had not reached its write yet, and the
        // ordering assertion never saw the violation it exists for.
        //
        // So we hand the detaching thread the floor until it is genuinely parked
        // on the gate (or has finished, which is the unguarded case: its write has
        // then already happened, which is exactly what we want to catch). Both
        // arms terminate on a STATE, not a duration — nothing here asserts or
        // depends on how long anything takes.
        Thread? detachThread = null;
        using var detachReached = new ManualResetEventSlim(false);
        var detach = Task.Run(() =>
        {
            detachThread = Thread.CurrentThread;
            detachReached.Set();
            svc.AttachEngine(nint.Zero);
        }, ct);
        Assert.True(detachReached.Wait(Rendezvous, ct), "the detaching thread never started");

        // Hand the detaching thread the floor until it is genuinely parked on
        // AttachEngine's `lock (_gate)` — a Monitor wait, hence WaitSleepJoin — or
        // has finished, which is the unguarded case. This used to be a fixed
        // 10,000 SpinOnce budget: on a saturated box that expires before the
        // thread has even reached the lock, and the test then releases the knob
        // first and can no longer observe the violation it exists for. A STATE
        // with a deadlock-detector cap cannot go vacuous that way.
        // (fully qualified: `using System.Diagnostics` would make the
        // System.Threading.ThreadState below ambiguous with the Diagnostics one.)
        var handover = System.Diagnostics.Stopwatch.StartNew();
        while (handover.Elapsed < Rendezvous)
        {
            if (detach.IsCompleted) break;
            if (detachThread!.ThreadState.HasFlag(ThreadState.WaitSleepJoin)) break;
            // Yield on the fast path (that is the handover), then back off — a
            // detector cap must never license a 60-second hot spin on a box that
            // is already starved.
            if (handover.ElapsedMilliseconds < 50) Thread.Yield();
            else Thread.Sleep(1);
        }

        native.Release.Set();
        await detach.WaitAsync(Rendezvous, ct);
        Assert.True(await knob);

        // THE assertion: the knob write left native BEFORE the detach wrote the
        // handle. Reversed, the real engine would have been freed under a live
        // dca_engine_dsp_set_param.
        Assert.Equal(["knob-left-native", "engine-detached"], native.Order);
        Assert.True(native.KnobHadLeftNativeWhenTheHandleWasWritten,
            "AttachEngine(0) wrote the engine handle while a SetParam was still inside native — the " +
            "detach is not a barrier, and ReplaceEngineGated frees that pointer on the next line.");

        // ...and the write it was waiting on saw the LIVE engine the whole time,
        // never the detached one.
        Assert.Equal(live, native.HandleSeenLeavingNative);
        Assert.Equal(nint.Zero, native.EngineHandle);
    }

    /// <summary>Parks inside <c>SetParam</c> until released, standing in for a slow
    /// <c>dca_engine_dsp_set_param</c>, records the engine handle it saw on the
    /// way out — the pointer the real native call would have dereferenced — and
    /// logs the ORDER of the two events the barrier is about.</summary>
    private sealed class BlockingSetParamInterop : FakeDspNativeInterop
    {
        private readonly List<string> _order = [];
        private readonly Lock _orderGate = new();

        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public nint HandleSeenLeavingNative { get; private set; } = -1;

        /// <summary>The two events, in the order they really happened.</summary>
        public IReadOnlyList<string> Order
        {
            get { lock (_orderGate) return _order.ToArray(); }
        }

        /// <summary>Captured AT the handle write, so it reports what was true then
        /// rather than what is true once everything has settled.</summary>
        public bool KnobHadLeftNativeWhenTheHandleWasWritten { get; private set; }

        /// <summary>The detach's handle write — the moment
        /// <c>ReplaceEngineGated</c> is then free to destroy the engine.</summary>
        public override nint EngineHandle
        {
            get => base.EngineHandle;
            set
            {
                if (value == nint.Zero)
                {
                    lock (_orderGate)
                    {
                        KnobHadLeftNativeWhenTheHandleWasWritten = _order.Contains("knob-left-native");
                        _order.Add("engine-detached");
                    }
                }

                base.EngineHandle = value;
            }
        }

        public override bool SetParam(int nodeId, string paramId, float value)
        {
            var ok = base.SetParam(nodeId, paramId, value);
            Entered.Set();
            Release.Wait(Rendezvous);
            HandleSeenLeavingNative = EngineHandle;
            lock (_orderGate) _order.Add("knob-left-native");
            return ok;
        }
    }

    private sealed class ThrowingInterop : FakeDspNativeInterop
    {
        public override int AddNode(string moduleTypeId, bool initiallyBypassed = false) => throw new InvalidOperationException("boom");
    }
}

/// <summary>
/// In-memory <see cref="IDspNativeInterop" /> standing in for the native ABI.
/// Reports "attached" by default (<see cref="EngineHandle" /> non-zero) and
/// tracks pushed parameter values so the wrapper's native-read path is covered.
/// </summary>
internal class FakeDspNativeInterop : IDspNativeInterop
{
    private static readonly (string Id, string Name, DspParamInfo[] Params, DspStringParamInfo[] StringParams)[] Cat =
    [
        ("overdrive", "Overdrive", new[]
        {
            new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f),
            new DspParamInfo("level", "Level", 0f, 1f, 0.8f),
        }, Array.Empty<DspStringParamInfo>()),
        ("delay", "Delay", new[]
        {
            new DspParamInfo("time", "Time", 0f, 2000f, 350f),
        }, Array.Empty<DspStringParamInfo>()),
        // cab carries a file-path string param (ABI v22); miku a free-text one.
        ("cab", "Cabinet", new[]
        {
            new DspParamInfo("level", "Level", 0f, 1f, 0.8f),
        }, new[]
        {
            new DspStringParamInfo("irPath", "Impulse Response", "*.wav"),
        }),
        ("miku", "Miku", Array.Empty<DspParamInfo>(), new[]
        {
            new DspStringParamInfo("phrases", "Phrases", string.Empty),
        }),
    ];

    private int _nextId = 100;

    public virtual nint EngineHandle { get; set; } = 1;

    /// <summary>Always null: a fake cannot produce a real
    /// <see cref="JuceSharp.JuceAudioEngine" /> — constructing one opens a
    /// native engine — which is exactly why this seam still carries the HANDLE
    /// as its identity token. Every test in this file therefore drives the
    /// handle-only <c>AttachEngine(nint)</c> overload, and the wrapper path is
    /// covered by the real-native round-trip suites instead.</summary>
    public JuceSharp.JuceAudioEngine? Engine { get; set; }

    /// <summary>The co-op seat these ops address (ABI v28). 0 unless a test is
    /// standing in for one of players 2..N.</summary>
    public int Player { get; init; }

    /// <summary>Offline processing is native-only — the in-memory fake has no
    /// DSP to run, so it reports failure (callers must treat that as such).</summary>
    public bool ProcessBuffer(float[] left, float[] right, int numFrames, int sampleRate) => false;

    /// <summary>Empties this seat's chain (ABI v28). The fake keeps no node list
    /// of its own (the SERVICE owns the mirror), so this only has to report that
    /// the native call succeeded.</summary>
    public bool Clear()
    {
        Params.Clear();
        Strings.Clear();
        Branches.Clear();
        return true;
    }

    public Dictionary<(int NodeId, string ParamId), float> Params { get; } = [];
    public Dictionary<(int NodeId, string ParamId), string> Strings { get; } = [];
    public Dictionary<int, int> Branches { get; } = [];

    public int ModuleTypeCount() => Cat.Length;

    public bool TryModuleTypeInfo(int idx, out string id, out string name)
    {
        if (idx < 0 || idx >= Cat.Length)
        {
            id = string.Empty;
            name = string.Empty;
            return false;
        }

        id = Cat[idx].Id;
        name = Cat[idx].Name;
        return true;
    }

    public int ModuleParamCount(string moduleTypeId)
        => Cat.FirstOrDefault(c => c.Id == moduleTypeId).Params?.Length ?? 0;

    public bool TryModuleParamInfo(string moduleTypeId, int idx, out DspParamInfo info)
    {
        var pars = Cat.FirstOrDefault(c => c.Id == moduleTypeId).Params;
        if (pars == null || idx < 0 || idx >= pars.Length)
        {
            info = new DspParamInfo(string.Empty, string.Empty, 0f, 1f, 0f);
            return false;
        }

        info = pars[idx];
        return true;
    }

    // ABI-v24 value labels: only overdrive "drive" is a labelled model picker
    // here (a stand-in for amp voicing / cab model on the real engine), so the
    // catalogue-load path can be unit-tested without the native DLL.
    private static readonly Dictionary<(string Module, string Param), string[]> Labels = new()
    {
        [("overdrive", "drive")] = ["Generic", "Screamer", "Rat", "Metal"],
    };

    public int ModuleParamLabelCount(string moduleTypeId, string paramId)
        => Labels.TryGetValue((moduleTypeId, paramId), out var l) ? l.Length : -1;

    // ABI-v27 quantization step: only delay "time" pretends to be an ordinal
    // count here (a stand-in for chorus voices / phaser stages), so the
    // catalogue-load path is covered without the native DLL. 0 = continuous.
    private static readonly Dictionary<(string Module, string Param), float> Steps = new()
    {
        [("delay", "time")] = 10f,
    };

    public float ModuleParamStep(string moduleTypeId, string paramId)
        => Steps.TryGetValue((moduleTypeId, paramId), out var s) ? s : 0f;

    public bool TryModuleParamLabel(string moduleTypeId, string paramId, int valueIndex, out string label)
    {
        if (Labels.TryGetValue((moduleTypeId, paramId), out var l) && valueIndex >= 0 && valueIndex < l.Length)
        {
            label = l[valueIndex];
            return true;
        }

        label = string.Empty;
        return false;
    }

    public int ModuleStringParamCount(string moduleTypeId)
        => Cat.FirstOrDefault(c => c.Id == moduleTypeId).StringParams?.Length ?? 0;

    public bool TryModuleStringParamInfo(string moduleTypeId, int idx, out DspStringParamInfo info)
    {
        var pars = Cat.FirstOrDefault(c => c.Id == moduleTypeId).StringParams;
        if (pars == null || idx < 0 || idx >= pars.Length)
        {
            info = new DspStringParamInfo(string.Empty, string.Empty, string.Empty);
            return false;
        }

        info = pars[idx];
        return true;
    }

    public virtual int AddNode(string moduleTypeId, bool initiallyBypassed = false)
        => EngineHandle == nint.Zero ? -1 : _nextId++;

    public bool RemoveNode(int nodeId) => EngineHandle != nint.Zero;
    public bool MoveNode(int nodeId, int newIndex) => EngineHandle != nint.Zero;
    public bool SetBypass(int nodeId, bool bypassed) => EngineHandle != nint.Zero;

    public virtual bool SetParam(int nodeId, string paramId, float value)
    {
        if (EngineHandle == nint.Zero) return false;
        Params[(nodeId, paramId)] = value;
        return true;
    }

    public bool TryGetParam(int nodeId, string paramId, out float value)
        => Params.TryGetValue((nodeId, paramId), out value);

    public virtual bool SetNodeString(int nodeId, string paramId, string value)
    {
        if (EngineHandle == nint.Zero) return false;
        Strings[(nodeId, paramId)] = value ?? string.Empty;
        return true;
    }

    public bool TryGetNodeString(int nodeId, string paramId, out string value)
    {
        if (EngineHandle != nint.Zero && Strings.TryGetValue((nodeId, paramId), out var v))
        {
            value = v;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public virtual bool SetNodeBranch(int nodeId, int branch)
    {
        if (EngineHandle == nint.Zero) return false;
        if (branch is < 0 or > 3) return false; // native InvalidArg reject
        Branches[nodeId] = branch;
        return true;
    }

    public bool TryGetNodeBranch(int nodeId, out int branch)
    {
        if (EngineHandle != nint.Zero && Branches.TryGetValue(nodeId, out branch))
            return true;
        branch = 0;
        return false;
    }

    public IReadOnlyList<int> GetNodeIds() => [];
}
