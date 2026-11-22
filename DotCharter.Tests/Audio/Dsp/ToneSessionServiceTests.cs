using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio.Dsp;

/// <summary>
///     The pedalboard used to start EMPTY on every launch and to be silently
///     wiped by any engine rebuild (a device change creates a new engine, whose
///     native chain has no nodes). These pin the behaviour that replaced that.
/// </summary>
public sealed class ToneSessionServiceTests : IDisposable
{
    private const nint EngineA = 0x1000;
    private const nint EngineB = 0x2000;

    /// <summary>Temp directory for the profile-scoped rig store — never the real
    /// config root, which is live owner data.</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private ProfileToneRigStore NewStore() => new(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>The shared fake's catalogue plus the neural amp — with its
    /// <c>modelPath</c> STRING param, which is the part that matters: most of the
    /// factory catalogue is capture-backed, and a namamp restored without its
    /// modelPath is a silent amp. Kept local rather than added to
    /// <see cref="FakeDspChainService.DefaultCatalog" /> because sibling tests
    /// deliberately assert what happens on a catalogue that LACKS namamp (an
    /// older native DLL), and that guard has to stay meaningful.</summary>
    private static FakeDspChainService NewChain() => new(
        FakeDspChainService.DefaultCatalog()
            .Append(new DspModuleType("namamp", "Neural Amp (NAM)",
            [
                new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f),
                new DspParamInfo("master", "Master", 0f, 1f, 0.5f),
                new DspParamInfo("outputDb", "Output (dB)", -24f, 24f, 0f)
            ])
            {
                StringParams = [new DspStringParamInfo("modelPath", "NAM Model", "*.nam;*.aidax")]
            })
            .ToList());

    private ToneSessionService Build(
        FakeDspChainService chain, FakeConfigurationService config, out DspTonePresetService presets,
        IProfileToneRigStore? store = null)
    {
        presets = new DspTonePresetService(chain, config);
        return new ToneSessionService(chain, config, presets, store ?? NewStore());
    }

    [Fact]
    public void AFailedWriteIsRetriedForAnUnchangedRig()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);
        chain.AddNode("overdrive");

        var path = Path.Combine(_dir, ProfileToneRigStore.StoreFileName);
        Directory.CreateDirectory(path); // Reads succeeded; publishing the save will fail.
        session.Flush();
        Directory.Delete(path);
        session.Flush();

        Assert.Equal("overdrive", Assert.Single(NewStore().GetRig(0)!.Modules).ModuleTypeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistedAThenFailedBThenLiveA_PublishesAAfterRecovery(bool shutdown)
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);
        var node = chain.AddNode("overdrive")!;
        chain.SetParam(node.NodeId, "drive", 0.2f);
        session.Flush();
        var path = Path.Combine(_dir, ProfileToneRigStore.StoreFileName);
        File.Delete(path);
        Directory.CreateDirectory(path);
        chain.SetParam(node.NodeId, "drive", 0.8f);
        session.Flush();
        chain.SetParam(node.NodeId, "drive", 0.2f);
        Directory.Delete(path);

        if (shutdown) session.Dispose();
        else session.Flush();

        Assert.Equal(0.2f, Assert.Single(NewStore().GetRig(0)!.Modules).Params["drive"]);
        var restartedChain = NewChain();
        using var restarted = Build(restartedChain, config, out _);
        restarted.Start();
        restartedChain.AttachEngine(EngineB);
        Assert.Equal(0.2f, Assert.Single(restartedChain.GetNodes()).Values["drive"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExplicitSupportedReplacementAfterPartialRestore_PersistsAndPreservesTheOriginal(bool identical)
    {
        var saved = UserTonePresetDto.FromPreset(new DspTonePreset("saved", "Saved", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>()),
                new DcRigModule("unavailable-module", new Dictionary<string, float>())]), DspToneOrigin.User));
        Assert.True(NewStore().SetRig(0, saved));
        var chain = NewChain();
        var config = new FakeConfigurationService();
        using var session = Build(chain, config, out var presets);
        session.Start();
        chain.AttachEngine(EngineA);
        var replacement = new DspTonePreset("replacement", "Replacement", DspToneCategory.Clean, "",
            identical ? DcRigSnapshot.FromChain(chain) : new DcRigPreset([
                new DcRigModule("delay", new Dictionary<string, float>())]), DspToneOrigin.User);

        var result = presets.Apply(replacement);
        Assert.Equal(0, result.FailedOps);
        Assert.Equal(identical, result.NoOp);
        session.Flush();

        var expected = identical ? "overdrive" : "delay";
        Assert.Equal(expected, Assert.Single(NewStore().GetRig(0)!.Modules).ModuleTypeId);
        var recovery = NewStore().GetRecoveryRig(0);
        Assert.NotNull(recovery);
        Assert.Contains(recovery.Modules, m => m.ModuleTypeId == "unavailable-module");
        Assert.Equal(2, recovery.Modules.Count);
        chain.AttachEngine(EngineB);
        Assert.Equal(expected, Assert.Single(chain.GetNodes()).ModuleTypeId);
    }

    [Fact]
    public void AnExplicitReplacementAndItsFullRecoveryRig_SurviveFailedPublicationTogether()
    {
        var original = UserTonePresetDto.FromPreset(new DspTonePreset("saved", "Saved", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>()),
                new DcRigModule("unavailable-module", new Dictionary<string, float>())]), DspToneOrigin.User));
        Assert.True(NewStore().SetRig(0, original));
        var chain = NewChain();
        using var session = Build(chain, new FakeConfigurationService(), out var presets);
        session.Start();
        chain.AttachEngine(EngineA);
        var result = presets.Apply(new DspTonePreset("replacement", "Replacement", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("delay", new Dictionary<string, float>())]), DspToneOrigin.User));
        Assert.Equal(0, result.FailedOps);
        var path = Path.Combine(_dir, ProfileToneRigStore.StoreFileName);
        File.Delete(path);
        Directory.CreateDirectory(path);
        session.Flush();
        session.Dispose();

        var recovered = NewStore();
        Assert.Equal("delay", Assert.Single(recovered.GetRig(0)!.Modules).ModuleTypeId);
        Assert.Equal(2, recovered.GetRecoveryRig(0)!.Modules.Count);
        Directory.Delete(path);
        recovered.FlushPending();
        Assert.Equal("delay", Assert.Single(NewStore().GetRig(0)!.Modules).ModuleTypeId);
        Assert.Equal(2, NewStore().GetRecoveryRig(0)!.Modules.Count);
    }

    [Fact]
    public void AFailedReplacementDoesNotAuthorizeTheRemainingPartialChain()
    {
        var original = UserTonePresetDto.FromPreset(new DspTonePreset("saved", "Saved", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>()),
                new DcRigModule("unavailable-module", new Dictionary<string, float>())]), DspToneOrigin.User));
        Assert.True(NewStore().SetRig(0, original));
        var chain = NewChain();
        using var session = Build(chain, new FakeConfigurationService(), out var presets);
        session.Start();
        chain.AttachEngine(EngineA);
        var result = presets.Apply(original.ToPreset());
        Assert.True(result.FailedOps > 0);
        session.Flush();

        Assert.Equal(2, NewStore().GetRig(0)!.Modules.Count);
        Assert.Null(NewStore().GetRecoveryRig(0));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnIdenticalPresetSignalsAuthorityOnlyForAnAttachedCurrentSeat(bool attached, bool stale)
    {
        var chain = NewChain();
        if (attached) chain.AttachEngine(EngineA);
        var presets = new DspTonePresetService(chain, new FakeConfigurationService());
        var preset = new DspTonePreset("replacement", "Replacement", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>())]), DspToneOrigin.User);
        presets.Apply(preset);
        var signals = new List<(int Player, long Generation)>();
        presets.PresetApplied += (player, generation) => signals.Add((player, generation));

        var result = presets.ApplyToPlayerForProfile(0, preset, stale ? 1 : 0);

        if (stale) Assert.Null(result);
        else Assert.True(result!.Value.NoOp);
        Assert.Equal(attached && !stale ? 1 : 0, signals.Count);
        Assert.All(signals, signal => Assert.Equal((0, 0L), signal));
        presets.ApplyToPlayer(1, preset);
        Assert.Equal(attached && !stale ? 1 : 0, signals.Count);
    }

    [Fact]
    public void ADisplacedBoardsSuccessfulPresetDoesNotAuthorizeThePartialSavedRig()
    {
        var original = UserTonePresetDto.FromPreset(new DspTonePreset("saved", "Saved", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>()),
                new DcRigModule("unavailable-module", new Dictionary<string, float>())]), DspToneOrigin.User));
        Assert.True(NewStore().SetRig(0, original));
        var chain = NewChain();
        using var session = Build(chain, new FakeConfigurationService(), out var presets);
        session.Start();
        chain.AttachEngine(EngineA);
        session.BoardDisplaced = () => true;
        presets.Apply(new DspTonePreset("song", "Song", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>())]), DspToneOrigin.User));
        session.BoardDisplaced = () => false;
        session.Flush();

        Assert.Equal(2, NewStore().GetRig(0)!.Modules.Count);
        Assert.Null(NewStore().GetRecoveryRig(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPublicationThenRebuild_RetriesTheRestoredUncommittedRig(bool shutdown)
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);
        chain.AddNode("overdrive");
        var path = Path.Combine(_dir, ProfileToneRigStore.StoreFileName);
        Directory.CreateDirectory(path);
        session.Flush();

        chain.AttachEngine(EngineB);
        Assert.Equal("overdrive", Assert.Single(chain.GetNodes()).ModuleTypeId);
        Directory.Delete(path);
        if (shutdown) session.Dispose();
        else session.Flush();

        Assert.Equal("overdrive", Assert.Single(NewStore().GetRig(0)!.Modules).ModuleTypeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingAnUnreadableSeatsFallback_CannotReplaceItsSavedRig(bool shutdown)
    {
        var saved = UserTonePresetDto.FromPreset(new DspTonePreset("saved", "Saved", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float> { ["drive"] = 0.83f })]), DspToneOrigin.User));
        Assert.True(NewStore().SetRig(0, saved));
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "nam-clean-glass");
        using var session = Build(chain, config, out _);
        var path = Path.Combine(_dir, ProfileToneRigStore.StoreFileName);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
        {
            session.Start();
            chain.AttachEngine(EngineA);
            Assert.Contains(chain.GetNodes(), n => n.ModuleTypeId == "namamp");
        }
        chain.AddNode("delay");
        if (shutdown) session.Dispose();
        else session.Flush();

        var restored = Assert.Single(NewStore().GetRig(0)!.Modules);
        Assert.Equal("overdrive", restored.ModuleTypeId);
        Assert.Equal(0.83f, restored.Params["drive"]);
        if (!shutdown) Assert.Equal("overdrive", Assert.Single(chain.GetNodes()).ModuleTypeId);
    }

    [Fact]
    public void AFailedPublicationAtShutdown_IsRecoveredFromItsJournal()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);
        chain.AddNode("overdrive");
        var path = Path.Combine(_dir, ProfileToneRigStore.StoreFileName);
        Directory.CreateDirectory(path);

        session.Dispose();
        Directory.Delete(path);

        Assert.Equal("overdrive", Assert.Single(NewStore().GetRig(0)!.Modules).ModuleTypeId);
    }

    [Fact]
    public void AnUnavailableJournal_DoesNotPreventSuccessfulPrimaryPublication()
    {
        var store = NewStore();
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = Build(chain, config, out _, store);
        session.Start();
        chain.AttachEngine(EngineA);
        chain.AddNode("overdrive");
        Directory.CreateDirectory(Path.Combine(_dir, ProfileToneRigStore.StoreFileName + ".pending"));

        session.Flush();

        Assert.Equal("overdrive", Assert.Single(NewStore().GetRig(0)!.Modules).ModuleTypeId);
    }

    [Fact]
    public void APartiallyRestoredSavedRig_IsNotReplacedByAnEditedPartialChain()
    {
        var saved = UserTonePresetDto.FromPreset(new DspTonePreset("saved", "Saved", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>()),
                new DcRigModule("unavailable-module", new Dictionary<string, float>())]), DspToneOrigin.User));
        Assert.True(NewStore().SetRig(0, saved));
        var chain = NewChain();
        using var session = Build(chain, new FakeConfigurationService(), out _);
        session.Start();
        chain.AttachEngine(EngineA);
        chain.SetParam(Assert.Single(chain.GetNodes()).NodeId, "drive", 0.2f);

        session.Flush();

        Assert.Equal(2, NewStore().GetRig(0)!.Modules.Count);
    }

    [Fact]
    public void ASidechainOnlyEditIsPersistedAndRestored()
    {
        var chain = new FakeDspChainService([
            new DspModuleType("ducker", "Ducker", []) { SupportsSidechain = true }
        ]);
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);
        var node = chain.AddNode("ducker")!;
        session.Flush();

        Assert.True(chain.SetNodeSidechain(node.NodeId, JuceSharp.Dsp.JuceDspSidechainSource.SongMix));
        session.Flush();
        Assert.Equal((int)JuceSharp.Dsp.JuceDspSidechainSource.SongMix, NewStore().GetRig(0)!.Modules[0].Sidechain);

        chain.AttachEngine(EngineB);
        Assert.Equal(JuceSharp.Dsp.JuceDspSidechainSource.SongMix, Assert.Single(chain.GetNodes()).Sidechain);
    }

    [Fact]
    public void AssignmentOnlyEditsPersistAndRestoreOnEngineRebuild_AndClearingPersists()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);
        var node = chain.AddNode("overdrive")!;
        session.Flush();
        var mapping = new RigControlAssignment("drive", 2, 0, 1, 4, 10);
        Assert.True(chain.SetControlAssignments(node.NodeId, [mapping]));
        session.Flush();
        Assert.Equal(mapping, Assert.Single(NewStore().GetRig(0)!.Modules[0].ControlAssignments!));
        chain.AttachEngine(EngineB);
        Assert.Equal(mapping, Assert.Single(chain.GetNodes()[0].ControlAssignments));
        Assert.True(chain.SetControlAssignments(chain.GetNodes()[0].NodeId, []));
        session.Flush();
        Assert.Null(NewStore().GetRig(0)!.Modules[0].ControlAssignments);
    }

    [Fact]
    public void OnAFreshEngine_WithNothingStored_TheDefaultPresetIsApplied()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "nam-crunch-classic-rock");

        using var session = Build(chain, config, out var presets);
        session.Start();
        chain.AttachEngine(EngineA);

        var expected = presets.FindById("nam-crunch-classic-rock");
        Assert.NotNull(expected);
        var live = chain.GetNodes().Select(n => n.ModuleTypeId).ToList();
        Assert.NotEmpty(live);

        // The fake's catalogue is a subset of the real one, and AddNode refuses a
        // type it does not know — so assert over the intersection rather than
        // demanding the whole rig. The amp is the part that matters here: the
        // whole point of a first-run default is that a fresh install sounds like
        // an amp instead of a raw DI.
        Assert.Contains("namamp", live);
        var known = chain.Catalog.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var module in expected!.Rig.Modules.Where(m => known.Contains(m.ModuleTypeId)))
            Assert.Contains(module.ModuleTypeId, live);
    }

    [Fact]
    public void ADefaultPresetIdThatMatchesNothing_LeavesTheBoardEmptyWithoutThrowing()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "no-such-preset");

        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);

        Assert.Empty(chain.GetNodes());
    }

    [Fact]
    public void AnEmptyDefaultPresetId_KeepsThePre037EmptyBoard()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");

        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);

        Assert.Empty(chain.GetNodes());
    }

    [Fact]
    public void AStoredRigBeatsTheDefaultPreset_AndCarriesItsCaptureAndBranch()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "nam-crunch-classic-rock");
        var store = NewStore();
        store.SetRig(0, UserTonePresetDto.FromPreset(new DspTonePreset(
            "active", "Active rig", DspToneCategory.HighGain, "",
            new DcRigPreset([
                new DcRigModule("namamp", new Dictionary<string, float> { ["gain"] = 0.7f })
                {
                    StringParams = new Dictionary<string, string> { ["modelPath"] = "engl-savage.nam" }
                }
            ]),
            DspToneOrigin.User)));

        using var session = Build(chain, config, out _, store);
        session.Start();
        chain.AttachEngine(EngineA);

        var nam = chain.GetNodes().Single(n => n.ModuleTypeId == "namamp");
        // The capture is the load-bearing part: dropping the string param
        // restores a MODELLESS namamp, which is a silent amp.
        Assert.Equal("engl-savage.nam", nam.StringValues["modelPath"]);
    }

    [Fact]
    public void RestoreOff_StartsFromTheDefaultEvenWhenARigIsStored()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c =>
        {
            c.RestoreToneOnStartup = false;
            c.DefaultTonePresetId = "nam-clean-glass";
        });
        var store = NewStore();
        store.SetRig(0, UserTonePresetDto.FromPreset(new DspTonePreset(
            "active", "Active rig", DspToneCategory.HighGain, "",
            new DcRigPreset([new DcRigModule("fuzz", new Dictionary<string, float>())]),
            DspToneOrigin.User)));

        using var session = Build(chain, config, out _, store);
        session.Start();
        chain.AttachEngine(EngineA);

        Assert.DoesNotContain(chain.GetNodes(), n => n.ModuleTypeId == "fuzz");
        Assert.NotEmpty(chain.GetNodes());
    }

    /// <summary>
    ///     The regression that motivated the whole service: changing audio device
    ///     rebuilds the engine, and the new engine's chain is empty. The board
    ///     used to just vanish.
    /// </summary>
    [Fact]
    public void AnEngineRebuild_RestoresTheBoardInsteadOfLeavingItEmpty()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "nam-metal-5150-rhythm");

        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);
        var afterFirst = chain.GetNodes().Select(n => n.ModuleTypeId).ToList();
        Assert.NotEmpty(afterFirst);

        // A device change: detach, then attach a DIFFERENT engine.
        chain.AttachEngine(nint.Zero);
        Assert.Empty(chain.GetNodes());
        chain.AttachEngine(EngineB);

        Assert.Equal(afterFirst, chain.GetNodes().Select(n => n.ModuleTypeId).ToList());
    }

    [Fact]
    public void ReattachingTheSameEngine_DoesNotRebuildTheBoard()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "nam-clean-glass");

        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);

        // An in-place device reconfigure re-asserts the SAME handle; it keeps its
        // chain, so a restore there would duplicate every pedal.
        var before = chain.GetNodes().Count;
        chain.AttachEngine(EngineA);
        Assert.Equal(before, chain.GetNodes().Count);
    }

    [Fact]
    public void Flush_WritesTheLiveRigIntoTheProfilesRigStore()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");

        using var session = Build(chain, config, out _);
        session.Start();
        chain.AttachEngine(EngineA);

        var node = chain.AddNode("overdrive");
        Assert.NotNull(node);
        chain.SetParam(node!.NodeId, "drive", 0.83f);

        session.Flush();

        // Read through a SECOND store instance so this proves the file, not a
        // cache: the rig has to survive the process, not just the session.
        var stored = NewStore().GetRig(0);
        Assert.NotNull(stored);
        var od = stored!.Modules.Single(m => m.ModuleTypeId == "overdrive");
        Assert.Equal(0.83f, od.Params["drive"]);
    }

    /// <summary>
    ///     An empty chain is indistinguishable from "the engine is down", so it
    ///     must never overwrite a good saved rig — otherwise a device change (or
    ///     a shutdown that tears the engine down first) loses exactly the thing
    ///     this service exists to keep.
    /// </summary>
    [Fact]
    public void AnEmptyChainNeverOverwritesTheStoredRig()
    {
        var chain = NewChain();
        var config = new FakeConfigurationService();
        var original = UserTonePresetDto.FromPreset(new DspTonePreset(
            "active", "Active rig", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("chorus", new Dictionary<string, float>())]),
            DspToneOrigin.User));
        config.Update(c => c.DefaultTonePresetId = "");
        var store = NewStore();
        store.SetRig(0, original);

        using var session = Build(chain, config, out _, store);
        session.Start();
        chain.AttachEngine(nint.Zero);
        session.Flush();

        var stored = NewStore().GetRig(0);
        Assert.NotNull(stored);
        Assert.Equal("chorus", stored!.Modules.Single().ModuleTypeId);
    }

    [Fact]
    public void TheSignature_ChangesForEveryFieldThatRoundTrips_AndForNothingElse()
    {
        static DcRigModule Module(
            float drive = 0.5f, bool bypassed = false, int branch = 0, string? capture = null) =>
            new("overdrive", new Dictionary<string, float> { ["drive"] = drive, ["tone"] = 0.4f }, bypassed)
            {
                StringParams = capture is null
                    ? null
                    : new Dictionary<string, string> { ["modelPath"] = capture },
                Branch = branch
            };

        var baseline = ToneSessionService.Signature([Module()]);

        Assert.Equal(baseline, ToneSessionService.Signature([Module()]));
        Assert.NotEqual(baseline, ToneSessionService.Signature([Module(drive: 0.51f)]));
        Assert.NotEqual(baseline, ToneSessionService.Signature([Module(bypassed: true)]));
        Assert.NotEqual(baseline, ToneSessionService.Signature([Module(branch: 2)]));
        Assert.NotEqual(baseline, ToneSessionService.Signature([Module(capture: "ts9.nam")]));
        // Order is part of a rig's identity — a chain is a signal path.
        Assert.NotEqual(
            ToneSessionService.Signature([Module(), Module(drive: 0.2f)]),
            ToneSessionService.Signature([Module(drive: 0.2f), Module()]));

        // Param enumeration order is not a contract, so the signature must not
        // depend on it: the same params inserted in a different order are the
        // same rig, and a spurious difference means a config write every poll.
        var a = new DcRigModule("eq", new Dictionary<string, float> { ["low"] = 1f, ["high"] = 2f });
        var b = new DcRigModule("eq", new Dictionary<string, float> { ["high"] = 2f, ["low"] = 1f });
        Assert.Equal(ToneSessionService.Signature([a]), ToneSessionService.Signature([b]));
    }
}
