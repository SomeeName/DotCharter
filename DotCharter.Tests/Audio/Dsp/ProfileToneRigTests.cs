using DotCharter.Services.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio.Dsp;

/// <summary>
///     The rig follows the PLAYER PROFILE (0.45). Two people sharing a machine
///     each get their own pedals and their own knob positions, and neither can
///     reach the other's board.
///     <para>
///         <b>Profile and seat are different axes</b> and this file exercises the
///         profile one: a profile owns a rig PER co-op SEAT, so switching profile
///         brings that profile's whole set of seat rigs. The seat axis (a seat
///         with no entry inherits nothing, an empty chain is never persisted) is
///         <see cref="PerPlayerToneSessionTests" /> and is unchanged.
///     </para>
///     <para>
///         Every path here is pointed at a TEMP directory. Nothing in this file
///         may reach <c>%LocalAppData%\DotCharter</c>: the profile service takes
///         its registry path, and every profile data directory is derived from
///         that path's parent, so one override redirects the lot.
///     </para>
/// </summary>
public sealed class ProfileToneRigTests : IDisposable
{
    private const nint EngineA = 0x1000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private PlayerProfileService NewProfiles()
        => new(FakeLocalization.English(), logger: null, filePath: Path.Combine(_dir, "player-profiles.json"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static UserTonePresetDto Rig(string moduleTypeId, float drive = 0.5f)
        => UserTonePresetDto.FromPreset(new DspTonePreset(
            "active", "Active rig", DspToneCategory.Clean, "",
            new DcRigPreset([
                new DcRigModule(moduleTypeId, new Dictionary<string, float> { ["drive"] = drive })
            ]),
            DspToneOrigin.User));

    // ---- the store on its own ------------------------------------------------

    [Fact]
    public void ARigRoundTripsThroughTheProfilesOwnFile()
    {
        var profiles = NewProfiles();
        var store = new ProfileToneRigStore(profiles);

        store.SetRig(0, Rig("overdrive", 0.81f));

        // A SECOND instance, so this proves the file rather than a cache — the
        // whole point is that a rig outlives the process.
        var reread = new ProfileToneRigStore(profiles).GetRig(0);
        Assert.NotNull(reread);
        Assert.Equal("overdrive", reread!.Modules.Single().ModuleTypeId);
        Assert.Equal(0.81f, reread.Modules.Single().Params["drive"]);

        Assert.True(File.Exists(Path.Combine(
            profiles.GetDataDirectory(profiles.ActiveProfile.Id), ProfileToneRigStore.StoreFileName)));
    }

    [Fact]
    public void AProfileCarriesARigPerSeat_AndTheSeatsStayDistinct()
    {
        var profiles = NewProfiles();
        var store = new ProfileToneRigStore(profiles);

        store.SetRig(0, Rig("overdrive"));
        store.SetRig(1, Rig("delay"));

        var reread = new ProfileToneRigStore(profiles);
        Assert.Equal("overdrive", reread.GetRig(0)!.Modules.Single().ModuleTypeId);
        Assert.Equal("delay", reread.GetRig(1)!.Modules.Single().ModuleTypeId);
        Assert.Null(reread.GetRig(2));
    }

    [Fact]
    public void WritingOneProfilesRig_LeavesTheOthersUntouched()
    {
        var profiles = NewProfiles();
        var a = profiles.ActiveProfile;
        var b = profiles.Create("Zwei");

        var store = new ProfileToneRigStore(profiles);
        store.SetRig(0, Rig("overdrive"));

        profiles.SwitchTo(b.Id);
        store.Rescope();
        // A fresh profile starts with NOTHING — not with a copy of A's board.
        Assert.Null(store.GetRig(0));

        store.SetRig(0, Rig("delay"));
        Assert.Equal("delay", store.GetRig(0)!.Modules.Single().ModuleTypeId);

        profiles.SwitchTo(a.Id);
        store.Rescope();
        Assert.Equal("overdrive", store.GetRig(0)!.Modules.Single().ModuleTypeId);
    }

    [Fact]
    public void TheScopeIsPINNED_SoASwitchDoesNotRetargetWritesUntilRescope()
    {
        // The reason the store does not resolve the active profile per call:
        // ActiveProfileChanged is raised AFTER the switch, so the coordinator's
        // flush would otherwise write the OUTGOING player's last edits into the
        // INCOMING player's file. That is the leak this whole feature prevents.
        var profiles = NewProfiles();
        var a = profiles.ActiveProfile;
        var b = profiles.Create("Zwei");

        var store = new ProfileToneRigStore(profiles);
        Assert.Equal(a.Id, store.ScopedProfileId);

        profiles.SwitchTo(b.Id);
        Assert.Equal(a.Id, store.ScopedProfileId);
        store.SetRig(0, Rig("overdrive"));

        Assert.True(store.Rescope());
        Assert.Equal(b.Id, store.ScopedProfileId);
        Assert.Null(store.GetRig(0));

        // ...and the write really did land on A.
        profiles.SwitchTo(a.Id);
        store.Rescope();
        Assert.Equal("overdrive", store.GetRig(0)!.Modules.Single().ModuleTypeId);
    }

    [Fact]
    public void RescopingToTheSameProfile_ReportsNoChange()
    {
        var profiles = NewProfiles();
        var store = new ProfileToneRigStore(profiles);
        _ = store.ScopedProfileId;

        Assert.False(store.Rescope());
    }

    // ---- migration -----------------------------------------------------------

    [Fact]
    public void ThePre045GlobalRig_IsAdoptedIntoTheActiveProfile_AndCleared()
    {
        var profiles = NewProfiles();
        var config = new FakeConfigurationService();
        config.Update(c =>
        {
            c.ActiveToneRig = Rig("overdrive", 0.77f);
            c.ActiveToneRigsByPlayer["1"] = Rig("delay");
        });

        var store = new ProfileToneRigStore(profiles, config);

        Assert.Equal("overdrive", store.GetRig(0)!.Modules.Single().ModuleTypeId);
        Assert.Equal(0.77f, store.GetRig(0)!.Modules.Single().Params["drive"]);
        Assert.Equal("delay", store.GetRig(1)!.Modules.Single().ModuleTypeId);

        // Cleared, which is what makes it one-shot: there is one source of truth
        // for a rig afterwards, and a second profile cannot re-adopt it.
        Assert.Null(config.LoadAppConfig().ActiveToneRig);
        Assert.Empty(config.LoadAppConfig().ActiveToneRigsByPlayer);
    }

    [Fact]
    public void TheAdoptedRigLandsOnTheACTIVEProfile_NotTheDefaultOne()
    {
        // The global rig belonged to whoever was last playing. On a machine whose
        // active profile is not the default, adopting into the default would greet
        // that player with the factory tone and file their board under someone
        // else's name.
        var profiles = NewProfiles();
        var defaultProfile = profiles.ActiveProfile;
        var second = profiles.Create("Zwei");
        profiles.SwitchTo(second.Id);

        var config = new FakeConfigurationService();
        config.Update(c => c.ActiveToneRig = Rig("overdrive"));

        var store = new ProfileToneRigStore(profiles, config);
        Assert.Equal("overdrive", store.GetRig(0)!.Modules.Single().ModuleTypeId);

        Assert.False(File.Exists(Path.Combine(
            profiles.GetDataDirectory(defaultProfile.Id), ProfileToneRigStore.StoreFileName)));
    }

    [Fact]
    public void ASecondProfileDoesNotAdoptTheAlreadyMigratedRig()
    {
        var profiles = NewProfiles();
        var config = new FakeConfigurationService();
        config.Update(c => c.ActiveToneRig = Rig("overdrive"));

        var store = new ProfileToneRigStore(profiles, config);
        Assert.NotNull(store.GetRig(0));

        var second = profiles.Create("Zwei");
        profiles.SwitchTo(second.Id);
        store.Rescope();

        Assert.Null(store.GetRig(0));
    }

    [Fact]
    public void WithNoGlobalRigStored_TheMigrationWritesNothing()
    {
        var profiles = NewProfiles();
        var config = new FakeConfigurationService();

        var store = new ProfileToneRigStore(profiles, config);
        Assert.Null(store.GetRig(0));

        // No file: a player who never had a rig must not gain an empty one, which
        // would then read as "this profile has a stored (empty) board" forever.
        Assert.False(File.Exists(Path.Combine(
            profiles.GetDataDirectory(profiles.ActiveProfile.Id), ProfileToneRigStore.StoreFileName)));
    }

    // ---- the coordinator: flush → rescope → reload ---------------------------

    private sealed class Harness : IDisposable
    {
        public required PlayerProfileService Profiles { get; init; }
        public required FakeConfigurationService Config { get; init; }
        public required PlayerDspChains Chains { get; init; }
        public required ProfileToneRigStore Store { get; init; }
        public required ToneSessionSet Sessions { get; init; }
        public required ProfileToneRigCoordinator Coordinator { get; init; }

        public FakeDspChainService Chain(int seat) => (FakeDspChainService)Chains.For(seat);

        public void Dispose()
        {
            Coordinator.Dispose();
            Sessions.Dispose();
        }
    }

    private Harness NewHarness(int seats = 2, string defaultPresetId = "")
    {
        var profiles = NewProfiles();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = defaultPresetId);
        var chains = new PlayerDspChains(Enumerable.Range(0, seats)
            .Select(p => (IDspChainService)new FakeDspChainService { Player = p })
            .ToList());
        var store = new ProfileToneRigStore(profiles, config);
        var sessions = ToneSessionSet.Create(chains, config,
            new DspTonePresetService(chains.For(0), config, null, null, chains), store);
        return new Harness
        {
            Profiles = profiles,
            Config = config,
            Chains = chains,
            Store = store,
            Sessions = sessions,
            Coordinator = new ProfileToneRigCoordinator(profiles, store, sessions)
        };
    }

    [Fact]
    public void SwitchingProfile_BringsThatProfilesOwnBoardUp()
    {
        using var h = NewHarness();
        var a = h.Profiles.ActiveProfile;
        var b = h.Profiles.Create("Zwei");

        h.Coordinator.Start();
        h.Chains.AttachEngineToAll(EngineA);

        h.Chain(0).AddNode("overdrive");
        h.Profiles.SwitchTo(b.Id);

        // B has nothing stored, so B's board is EMPTY — not a copy of A's, which
        // the next poll would then save as if B had built it.
        Assert.Empty(h.Chain(0).GetNodes());

        h.Chain(0).AddNode("delay");
        h.Profiles.SwitchTo(a.Id);

        Assert.Equal(["overdrive"], h.Chain(0).GetNodes().Select(n => n.ModuleTypeId));

        h.Profiles.SwitchTo(b.Id);
        Assert.Equal(["delay"], h.Chain(0).GetNodes().Select(n => n.ModuleTypeId));
    }

    [Fact]
    public void EditingOneProfilesRig_NeverTouchesTheOthersStoredRig()
    {
        using var h = NewHarness();
        var a = h.Profiles.ActiveProfile;
        var b = h.Profiles.Create("Zwei");

        h.Coordinator.Start();
        h.Chains.AttachEngineToAll(EngineA);
        h.Chain(0).AddNode("overdrive");

        h.Profiles.SwitchTo(b.Id);
        h.Chain(0).AddNode("delay");
        var node = h.Chain(0).GetNodes().Single();
        h.Chain(0).SetParam(node.NodeId, "feedback", 0.9f);
        h.Sessions.FlushAll();

        // Read A's file directly, bypassing the live store's scope.
        var aStore = new ProfileToneRigStore(Path.Combine(
            Path.GetDirectoryName(Path.Combine(_dir, "player-profiles.json"))!,
            "profiles", a.Id.ToString("N")));
        Assert.Equal(["overdrive"], aStore.GetRig(0)!.Modules.Select(m => m.ModuleTypeId));
    }

    [Fact]
    public void TheOutgoingProfileKeepsEditsMadeSinceTheLastPoll()
    {
        // The persist is a 5 s POLL, so at the instant of a switch up to five
        // seconds of the outgoing player's knob-twiddling is still only in the
        // live chain. The coordinator flushes FIRST, while the store is still
        // scoped to them, which is the entire reason for the step order.
        using var h = NewHarness();
        var a = h.Profiles.ActiveProfile;
        var b = h.Profiles.Create("Zwei");

        h.Coordinator.Start();
        h.Chains.AttachEngineToAll(EngineA);

        var node = h.Chain(0).AddNode("overdrive")!;
        h.Chain(0).SetParam(node.NodeId, "drive", 0.93f);

        h.Profiles.SwitchTo(b.Id);   // no explicit flush anywhere in the test
        h.Profiles.SwitchTo(a.Id);

        var restored = h.Chain(0).GetNodes().Single();
        Assert.Equal("overdrive", restored.ModuleTypeId);
        Assert.Equal(0.93f, restored.Values["drive"]);
    }

    [Fact]
    public void EverySeatFollowsTheProfileSwitchTogether()
    {
        // Profile is the OUTER axis: one switch repoints every seat, and seat 2's
        // rig is as much profile-owned as seat 1's.
        using var h = NewHarness();
        var a = h.Profiles.ActiveProfile;
        var b = h.Profiles.Create("Zwei");

        h.Coordinator.Start();
        h.Chains.AttachEngineToAll(EngineA);
        h.Chain(0).AddNode("overdrive");
        h.Chain(1).AddNode("delay");

        h.Profiles.SwitchTo(b.Id);
        Assert.Empty(h.Chain(0).GetNodes());
        Assert.Empty(h.Chain(1).GetNodes());

        h.Chain(1).AddNode("reverb");
        h.Profiles.SwitchTo(a.Id);

        Assert.Equal(["overdrive"], h.Chain(0).GetNodes().Select(n => n.ModuleTypeId));
        Assert.Equal(["delay"], h.Chain(1).GetNodes().Select(n => n.ModuleTypeId));
    }

    [Fact]
    public void WithNoEngineAttached_AProfileSwitchAppliesNothingAndWaits()
    {
        // A restore fired at a null handle turns every module into a failed
        // managed op that no native code ever sees — the v0.44 "the app applies
        // no tone rig at all" shape. The pending EngineAttached restores from the
        // now-correct store instead.
        using var h = NewHarness();
        var a = h.Profiles.ActiveProfile;
        var b = h.Profiles.Create("Zwei");

        h.Coordinator.Start();
        h.Chains.AttachEngineToAll(EngineA);

        // Give BOTH profiles a stored rig, so a missing guard would have real
        // modules to fire at the null handle — a test where the incoming profile
        // stores nothing proves nothing, because "apply nothing" makes no calls
        // either way.
        h.Chain(0).AddNode("overdrive");
        h.Profiles.SwitchTo(b.Id);
        h.Chain(0).AddNode("delay");
        h.Profiles.SwitchTo(a.Id);

        h.Chains.AttachEngineToAll(nint.Zero);
        Assert.False(h.Chain(0).HasEngine);

        var addsBefore = h.Chain(0).Adds.Count;
        h.Profiles.SwitchTo(b.Id);
        Assert.Equal(addsBefore, h.Chain(0).Adds.Count); // nothing fired at a null handle

        // ...and the wait is not a loss: the pending attach restores B's board
        // from the now-correct store.
        h.Chains.AttachEngineToAll(EngineA);
        Assert.Equal(["delay"], h.Chain(0).GetNodes().Select(n => n.ModuleTypeId));
    }

    /// <summary>
    ///     The profile switch and the engine attach share one restore path, and
    ///     that path DEFERS a request that lands while another is running (main's
    ///     v0.44 fix — dropping it left the board empty for a whole session). A
    ///     deferred profile switch therefore has to carry its own intent into the
    ///     re-run: re-running with the ORIGINAL attach's flag leaves the outgoing
    ///     player's pedals up, and five seconds later the poll saves them as the
    ///     incoming player's.
    /// </summary>
    [Fact]
    public void AProfileSwitchLandingMidRestore_IsDeferred_AndStillClears()
    {
        var profiles = NewProfiles();
        var a = profiles.ActiveProfile;
        var b = profiles.Create("Zwei");

        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = string.Empty);
        var store = new ProfileToneRigStore(profiles);
        // A has a board; B has nothing, so B's correct board is EMPTY.
        store.SetRig(0, UserTonePresetDto.FromPreset(new DspTonePreset(
            "active", "Active rig", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("amp", new Dictionary<string, float>())]),
            DspToneOrigin.User)));

        var chain = new SwitchProfileMidRestoreChain();
        var chains = new PlayerDspChains([chain]);
        using var sessions = ToneSessionSet.Create(chains, config,
            new DspTonePresetService(chain, config, null, null, chains), store);
        using var coordinator = new ProfileToneRigCoordinator(profiles, store, sessions);
        coordinator.Start();

        // The switch fires from INSIDE the first AddNode of the attach restore,
        // so it lands while _restoring is set and is deferred rather than run.
        chain.SwitchOnFirstAdd(() => profiles.SwitchTo(b.Id));
        chain.AttachEngine(0x1000);

        Assert.Equal(b.Id, store.ScopedProfileId);
        // B stores nothing, so B's board is empty — not A's amp left standing.
        Assert.Empty(chain.GetNodes());
        // ...and A's own file still has A's board: a switch landing mid-restore
        // must not corrupt the profile it left.
        Assert.Equal(["amp"], Reread(profiles, a.Id));
    }

    /// <summary>One profile's board, read straight off that profile's own file,
    /// bypassing whatever the live store is scoped to.</summary>
    private static string[] Reread(PlayerProfileService profiles, Guid profileId)
        => new ProfileToneRigStore(profiles.GetDataDirectory(profileId))
            .GetRig(0)?.Modules.Select(m => m.ModuleTypeId).ToArray() ?? [];

    /// <summary>
    ///     A minimal in-memory chain that runs one supplied action from inside its
    ///     FIRST <see cref="AddNode" /> — i.e. a profile switch landing while a
    ///     restore is between two of its own ops, without a thread race. Modelled
    ///     on <c>ToneRestoreEngineAttachTests.RebuildMidRestoreChain</c>.
    /// </summary>
    private sealed class SwitchProfileMidRestoreChain : IDspChainService
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
        private Action? _onFirstAdd;

        public void SwitchOnFirstAdd(Action action) => _onFirstAdd = action;

        public nint AttachedEngine { get; private set; }
        public bool HasEngine => AttachedEngine != nint.Zero;
        public int Player => 0;

        public IReadOnlyList<DspModuleType> Catalog { get; } =
        [
            new DspModuleType("amp", "Amp", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f)])
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

            var action = _onFirstAdd;
            _onFirstAdd = null;
            action?.Invoke();

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

    /// <summary>
    ///     v0.44's "the app applies no tone rig at all", pinned at the PRODUCTION
    ///     entry point rather than one seat's service.
    ///     <para>
    ///         <c>App.axaml.cs</c> runs <see cref="ProfileToneRigCoordinator.Start" />
    ///         from a background <c>Task.Run</c> (after the bundled NAM install),
    ///         while the JUCE engine is built LAZILY seconds later — measured 5.2 s
    ///         on the owner's machine. Every seat's stored rig was therefore fired
    ///         at <c>EngineHandle == 0</c>, and
    ///         <c>DspNativeInterop.AddNode</c> returns −1 before making a native
    ///         call, so every module came back as a failed op no native code ever
    ///         saw (the owner's log: 3 failed ops on seat 0, 5 on seat 1).
    ///     </para>
    ///     <para>
    ///         <b>Not a duplicate of the sibling tests.</b>
    ///         <c>ToneRestoreEngineAttachTests</c> drives a single hand-built
    ///         <see cref="ToneSessionService" />, and
    ///         <see cref="WithNoEngineAttached_AProfileSwitchAppliesNothingAndWaits" />
    ///         drives a profile SWITCH. Neither covers
    ///         <see cref="ProfileToneRigCoordinator.Start" /> — the one call the app
    ///         actually makes on the startup path — nor the MULTI-SEAT fan-out that
    ///         produced two applies milliseconds apart in that log.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheStartupRestore_FiresNothingBeforeAnEngineExists_AndEverySeatLandsWhenOneDoes()
    {
        using var h = NewHarness();
        // Both seats have a stored rig, exactly as the owner's config did — a seat
        // that stores nothing proves nothing here, because "apply nothing" makes
        // no calls either way.
        h.Store.SetRig(0, Rig("overdrive"));
        h.Store.SetRig(1, Rig("delay"));

        Assert.False(h.Chain(0).HasEngine);
        Assert.False(h.Chain(1).HasEngine);

        h.Coordinator.Start();

        // Adds records every ATTEMPT, refused or not, which is the whole point:
        // the real chain would have answered null and logged a failed op.
        Assert.Empty(h.Chain(0).Adds);
        Assert.Empty(h.Chain(1).Adds);

        // ...and skipping is not dropping. The attach that eventually arrives is
        // what the restore was waiting for, on EVERY seat.
        h.Chains.AttachEngineToAll(EngineA);
        Assert.Equal(["overdrive"], h.Chain(0).GetNodes().Select(n => n.ModuleTypeId));
        Assert.Equal(["delay"], h.Chain(1).GetNodes().Select(n => n.ModuleTypeId));
    }

    /// <summary>
    ///     The other half of <c>ResolveStartupTone</c>: seat 0 with NOTHING stored
    ///     falls through to <c>AppConfig.DefaultTonePresetId</c>. That branch has
    ///     its own reachers and would happily fire a whole capture-backed preset at
    ///     a null handle — on a FRESH install, i.e. the one launch where the
    ///     player's first impression is formed.
    /// </summary>
    [Fact]
    public void TheFirstRunDefaultPresetAlsoWaitsForAnEngine()
    {
        var profiles = NewProfiles();
        var config = new FakeConfigurationService();
        var chains = new PlayerDspChains([new FakeDspChainService { Player = 0 }]);
        var store = new ProfileToneRigStore(profiles, config);
        var presets = new DspTonePresetService(chains.For(0), config, null, null, chains);

        // A user preset rather than a factory id: it is guaranteed resolvable and
        // guaranteed to be built from modules the fake chain's catalogue has, so a
        // missing guard has real AddNodes to make.
        var saved = presets.SaveUser("First run", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>())]));
        config.Update(c => c.DefaultTonePresetId = saved.Id);

        using var sessions = ToneSessionSet.Create(chains, config, presets, store);
        using var coordinator = new ProfileToneRigCoordinator(profiles, store, sessions);

        var chain = (FakeDspChainService)chains.For(0);
        Assert.Null(store.GetRig(0)); // nothing stored: the default is the tone

        coordinator.Start();
        Assert.Empty(chain.Adds);

        chains.AttachEngineToAll(EngineA);
        Assert.Equal(["overdrive"], chain.GetNodes().Select(n => n.ModuleTypeId));
    }

    [Fact]
    public void TheCoordinatorStartsTheToneSessions_SoItCannotBeADeadRegistration()
    {
        // Nothing else calls StartAll. If this stops being resolved, the rig never
        // restores at all — loud on the first launch, rather than invisible until
        // someone makes a second profile.
        using var h = NewHarness(defaultPresetId: "fac-clean-1");
        var presets = new FakeDspTonePresetService();
        using var sessions = ToneSessionSet.Create(h.Chains, h.Config, presets, h.Store);
        using var coordinator = new ProfileToneRigCoordinator(h.Profiles, h.Store, sessions);

        coordinator.Start();
        h.Chains.AttachEngineToAll(EngineA);

        Assert.NotEmpty(presets.AppliedPerPlayer);
    }
}
