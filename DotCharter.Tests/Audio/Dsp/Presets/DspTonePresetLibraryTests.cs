using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
/// Guards the built-in factory tone catalogue (count/distribution/id integrity +
/// every module param inside its catalogue range) and the
/// <see cref="DspTonePresetService"/> apply + user-profile CRUD surface. Param
/// ranges are validated against the production <see cref="DcRigTargetCatalog"/>
/// (reachable via InternalsVisibleTo("DotCharter.Tests")).
/// </summary>
public sealed class DspTonePresetLibraryTests
{
    // Every known module type id — the AUTHORITATIVE managed mirror of the native
    // catalogue (100 modules since the 2026-09 gear wave; 39 when this was
    // written, incl. the Wave-2A cores + the ABI-v22 namamp/miku),
    // so a preset may use any real module (the artist/genre presets exploit
    // wah/phaser/bigmuff/springreverb/tapedelay/doubler/shimmer/namamp/…).
    private static readonly string[] KnownModuleIds = DcRigTargetCatalog.ModuleTypeIds.ToArray();

    private static readonly IReadOnlyDictionary<DspToneCategory, int> CategoryMinimums =
        new Dictionary<DspToneCategory, int>
        {
            [DspToneCategory.Clean] = 6,
            [DspToneCategory.Crunch] = 6,
            [DspToneCategory.HighGain] = 8,
            [DspToneCategory.Lead] = 6,
            [DspToneCategory.Acoustic] = 4,
            [DspToneCategory.Bass] = 6,
            [DspToneCategory.Ambient] = 4,
            [DspToneCategory.Experimental] = 4
        };

    // ------------------------------------------------------- count + spread

    [Fact]
    public void Library_HasAtLeast44Presets()
    {
        Assert.True(DspTonePresetLibrary.All.Count >= 44,
            $"Expected >= 44 factory presets, got {DspTonePresetLibrary.All.Count}.");
    }

    [Fact]
    public void EveryCategory_MeetsItsMinimumCount()
    {
        foreach (var (category, min) in CategoryMinimums)
        {
            var count = DspTonePresetLibrary.All.Count(p => p.Category == category);
            Assert.True(count >= min, $"Category {category} has {count} presets, expected >= {min}.");
        }
    }

    [Fact]
    public void EveryPreset_IsBuiltInOrigin() => Assert.All(DspTonePresetLibrary.All, p => Assert.Equal(DspToneOrigin.BuiltIn, p.Origin));

    // ------------------------------------------------------- id / name integrity

    [Fact]
    public void AllIds_AreUnique_NonEmpty_AndKebabCase()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in DspTonePresetLibrary.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id), "A preset has a blank id.");
            Assert.True(seen.Add(p.Id), $"Duplicate preset id '{p.Id}'.");
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", p.Id);
        }
    }

    [Fact]
    public void AllNames_AreUnique_AndNonEmpty()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in DspTonePresetLibrary.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name), $"Preset '{p.Id}' has a blank name.");
            Assert.True(seen.Add(p.Name), $"Duplicate preset name '{p.Name}'.");
            Assert.False(string.IsNullOrWhiteSpace(p.Description), $"Preset '{p.Id}' has a blank description.");
        }
    }

    // ------------------------------------------------- module + param validity

    [Fact]
    public void EveryModule_HasAKnownTypeId_AndInRangeParams()
    {
        var known = new HashSet<string>(KnownModuleIds, StringComparer.Ordinal);

        foreach (var preset in DspTonePresetLibrary.All)
        {
            Assert.NotEmpty(preset.Rig.Modules); // a factory tone is never empty
            foreach (var module in preset.Rig.Modules)
            {
                Assert.True(known.Contains(module.ModuleTypeId),
                    $"Preset '{preset.Id}' uses unknown module type '{module.ModuleTypeId}'.");

                foreach (var (paramId, value) in module.Params)
                {
                    Assert.True(
                        DcRigTargetCatalog.TryGetParam(module.ModuleTypeId, paramId, out var spec),
                        $"Preset '{preset.Id}' module '{module.ModuleTypeId}' sets unknown param '{paramId}'.");
                    Assert.InRange(value, spec.Min, spec.Max);
                }
            }
        }
    }

    [Fact]
    public void Cab_AlwaysComesAfterAmp_WhenBothPresent()
    {
        foreach (var preset in DspTonePresetLibrary.All)
        {
            var mods = preset.Rig.Modules;
            var ampIndex = IndexOf(mods, DcRigTargetCatalog.Amp);
            var cabIndex = IndexOf(mods, DcRigTargetCatalog.Cab);
            if (ampIndex >= 0 && cabIndex >= 0)
            {
                Assert.True(cabIndex > ampIndex,
                    $"Preset '{preset.Id}': cab (idx {cabIndex}) must come after amp (idx {ampIndex}).");
            }
        }
    }

    private static int IndexOf(IReadOnlyList<DcRigModule> mods, string typeId)
    {
        for (var i = 0; i < mods.Count; i++)
            if (mods[i].ModuleTypeId == typeId) return i;
        return -1;
    }

    // ------------------------------------------------------- loudness band

    /// <summary>The catalogue loudness gate: every factory preset's ESTIMATED
    /// static output (DcRigLoudnessNormalizer's documented walker, reference
    /// −18 dBFS input) must land within ±<see cref="LoudnessBandDb"/> of the
    /// −12 dBFS target, so the apply-time normalization only ever applies a
    /// small correction and no preset ships wildly hot or timid. On failure the
    /// message carries the full per-preset table.</summary>
    private const float LoudnessBandDb = 6f;

    [Fact]
    public void EveryFactoryPreset_EstimatedOutput_InsideTheLoudnessBand()
    {
        var target = DcRigLoudnessNormalizer.TargetOutputPeakDb;
        var table = new System.Text.StringBuilder();
        var outliers = new List<string>();

        foreach (var preset in DspTonePresetLibrary.All)
        {
            var est = DcRigLoudnessNormalizer.EstimateChainOutputDb(preset.Rig.Modules);
            var delta = est - target;
            table.AppendLine(FormattableString.Invariant(
                $"  {preset.Id,-28} est {est,7:0.00} dBFS  (target {delta,6:+0.00;-0.00})"));
            if (MathF.Abs(delta) > LoudnessBandDb)
                outliers.Add(preset.Id);
        }

        Assert.True(outliers.Count == 0,
            $"{outliers.Count} factory preset(s) fall outside the ±{LoudnessBandDb} dB loudness band " +
            $"around the {target} dBFS target: {string.Join(", ", outliers)}\n{table}");
    }

    // ------------------------------------------------------- service: apply

    /// <summary>A fake chain catalogue covering all 13 module types so any preset
    /// can be fully materialized.</summary>
    private static FakeDspChainService FullChain() =>
        new(KnownModuleIds.Select(id => new DspModuleType(id, id, new List<DspParamInfo>())).ToList());

    private static DspTonePresetService NewService(
        IDspChainService chain, FakeConfigurationService? config = null) =>
        new(chain, config ?? new FakeConfigurationService());

    [Fact]
    public void Service_ExposesFactoryAndUserSets()
    {
        var service = NewService(FullChain());
        Assert.Equal(DspTonePresetLibrary.All.Count, service.Presets.Count);
        Assert.Empty(service.UserPresets);
        Assert.Equal(service.Presets.Count, service.AllPresets.Count);
        Assert.NotNull(service.FindById(DspTonePresetLibrary.All[0].Id));
        Assert.Null(service.FindById("no-such-preset"));
    }

    [Fact]
    public void Apply_OverFullChain_AddsAllModules_WithoutThrowing()
    {
        var chain = FullChain();
        var service = NewService(chain);
        var preset = DspTonePresetLibrary.All.First(p => p.Category == DspToneCategory.HighGain);

        var ex = Record.Exception(() => service.Apply(preset));

        Assert.Null(ex);
        // Factory apply = the preset's modules + the loudness-normalizing
        // trailing master gain appended at the apply seam.
        Assert.Equal(preset.Rig.Modules.Count + 1, chain.GetNodes().Count);
        Assert.Equal(DcRigTargetCatalog.Gain, chain.GetNodes()[^1].ModuleTypeId);
    }

    [Fact]
    public void Apply_FactoryPreset_NormalizesTrailingGainToTheLoudnessTarget()
    {
        var chain = FullChain();
        var service = NewService(chain);
        var preset = DspTonePresetLibrary.All.First(p => p.Category == DspToneCategory.HighGain);

        service.Apply(preset);

        var gainNode = chain.GetNodes()[^1];
        Assert.Equal(DcRigTargetCatalog.Gain, gainNode.ModuleTypeId);
        var expected = DcRigLoudnessNormalizer.ComputeTrailingGainDb(
            DcRigTargetCatalog.FillDefaults(preset.Rig).Modules);
        Assert.Equal(expected, gainNode.Values["gainDb"], 0.001);
        // A hot high-gain rig gets pulled DOWN toward the target.
        Assert.True(expected < 0f, $"expected a cut for a high-gain rig, got {expected} dB");
    }

    [Fact]
    public void Apply_UserPreset_IsNotLoudnessNormalized()
    {
        var chain = FullChain();
        var service = NewService(chain);
        var user = new DspTonePreset("test-user", "User", DspToneCategory.HighGain, "d",
            new DcRigPreset(new List<DcRigModule>
            {
                new("distortion", new Dictionary<string, float> { ["gain"] = 0.9f }, false)
            }), DspToneOrigin.User);

        service.Apply(user);

        // Verbatim apply: no trailing gain module is appended for user presets.
        Assert.DoesNotContain(chain.GetNodes(), n => n.ModuleTypeId == DcRigTargetCatalog.Gain);
    }

    [Fact]
    public void ApplyEveryPreset_OverFullChain_NeverThrows()
    {
        foreach (var preset in DspTonePresetLibrary.All)
        {
            var service = NewService(FullChain());
            var ex = Record.Exception(() => service.Apply(preset));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Apply_OverNullChain_IsThrowFreeNoOp()
    {
        var service = NewService(new NullDspChainService());
        var preset = DspTonePresetLibrary.All[0];

        var result = service.Apply(preset);

        Assert.True(result.NoOp); // null chain rejects every op — nothing changed
    }

    [Fact]
    public void Apply_SparsePresetAfterFullPreset_ResetsUnspecifiedParamsToCatalogueDefaults()
    {
        var chain = FullChain();
        var service = NewService(chain);

        // Preset A pins EVERY amp param away from its catalogue default.
        var full = new DspTonePreset("test-full", "Full", DspToneCategory.HighGain, "d",
            new DcRigPreset(new List<DcRigModule>
            {
                new("amp", new Dictionary<string, float>
                {
                    ["gain"] = 0.95f, ["bass"] = 0.1f, ["mid"] = 0.15f, ["treble"] = 0.9f,
                    ["presence"] = 0.85f, ["master"] = 0.2f, ["voicing"] = 2f
                }, false)
            }), DspToneOrigin.User);

        // Preset B is SPARSE: only gain. The reused amp node must come out at the
        // catalogue defaults for everything B left out — never at A's values.
        var sparse = new DspTonePreset("test-sparse", "Sparse", DspToneCategory.Clean, "d",
            new DcRigPreset(new List<DcRigModule>
            {
                new("amp", new Dictionary<string, float> { ["gain"] = 0.3f }, false)
            }), DspToneOrigin.User);

        service.Apply(full);
        service.Apply(sparse);

        var amp = chain.GetNodes().Single(n => n.ModuleTypeId == "amp");
        Assert.Equal(0.3f, amp.Values["gain"]);
        foreach (var paramId in new[] { "bass", "mid", "treble", "presence", "master", "voicing" })
        {
            Assert.True(DcRigTargetCatalog.TryGetParam("amp", paramId, out var spec));
            Assert.True(amp.Values.TryGetValue(paramId, out var value),
                $"amp.{paramId} was never pushed to the chain.");
            Assert.Equal(spec.Default, value);
        }
    }

    // ------------------------------------------- capture-backed (namamp) presets

    [Fact]
    public void Apply_CaptureBackedPreset_ResolvesBundledFileNameUnderNamDir()
    {
        var chain = FullChain();
        var service = new DspTonePresetService(
            chain, new FakeConfigurationService(), null, namCaptureDirectory: () => "X:/nam");

        service.Apply(DspTonePresetLibrary.All.First(p => p.Id == "nam-deluxe-clean"));

        var nam = chain.GetNodes().Single(n => n.ModuleTypeId == "namamp");
        var expected = System.IO.Path.Combine("X:/nam", "fender-deluxe-reverb-clean.nam");
        Assert.Equal(expected, chain.GetNodeString(nam.NodeId, "modelPath"));
    }

    [Fact]
    public void Apply_CaptureBackedPreset_NoNamDir_PushesTheBareFileNameVerbatim()
    {
        var chain = FullChain();
        var service = NewService(chain); // no capture-dir provider

        service.Apply(DspTonePresetLibrary.All.First(p => p.Id == "nam-deluxe-clean"));

        var nam = chain.GetNodes().Single(n => n.ModuleTypeId == "namamp");
        Assert.Equal("fender-deluxe-reverb-clean.nam", chain.GetNodeString(nam.NodeId, "modelPath"));
    }

    [Fact]
    public void EveryCaptureBackedPreset_ReferencesABundledCaptureFileName()
    {
        // The namamp presets must name a real bundled capture (a bare .nam file
        // name, resolved at apply time) — never an absolute/dev path.
        foreach (var preset in DspTonePresetLibrary.All)
        {
            foreach (var mod in preset.Rig.Modules.Where(m => m.ModuleTypeId == "namamp"))
            {
                Assert.NotNull(mod.StringParams);
                Assert.True(mod.StringParams!.TryGetValue("modelPath", out var path));
                Assert.EndsWith(".nam", path, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain('/', path);
                Assert.DoesNotContain('\\', path);
            }
        }
    }

    [Fact]
    public void ApplyById_ReturnsNull_ForUnknownId()
    {
        var service = NewService(FullChain());
        Assert.Null(service.ApplyById("no-such-preset"));
        Assert.NotNull(service.ApplyById(DspTonePresetLibrary.All[0].Id));
    }

    // ------------------------------------------------ service: user profiles

    [Fact]
    public void SaveUser_ThenReload_RoundTripsRig_Persistently()
    {
        var config = new FakeConfigurationService();
        var rig = new DcRigPreset(new List<DcRigModule>
        {
            new("amp", new Dictionary<string, float> { ["gain"] = 0.7f, ["voicing"] = 2f }, false),
            new("cab", new Dictionary<string, float> { ["model"] = 3f }, false),
            new("reverb", new Dictionary<string, float> { ["mix"] = 0.25f }, true)
        });

        var saved = NewService(FullChain(), config)
            .SaveUser("My Lead Tone", DspToneCategory.Lead, "custom", rig);

        // A brand-new service instance reads the SAME persisted config.
        var reloaded = NewService(FullChain(), config);
        var user = Assert.Single(reloaded.UserPresets);

        Assert.Equal(saved.Id, user.Id);
        Assert.Equal("My Lead Tone", user.Name);
        Assert.Equal(DspToneCategory.Lead, user.Category);
        Assert.Equal(DspToneOrigin.User, user.Origin);
        Assert.True(user.IsUser);
        AssertRigEqual(rig, user.Rig);
        Assert.Equal(saved.Id, reloaded.FindById(saved.Id)!.Id);
    }

    [Fact]
    public void SaveUser_Overwrite_ReplacesInPlace_SameId()
    {
        var config = new FakeConfigurationService();
        var service = NewService(FullChain(), config);
        var first = service.SaveUser("Tone", DspToneCategory.Clean, "v1",
            new DcRigPreset(new List<DcRigModule> { new("amp", new Dictionary<string, float> { ["gain"] = 0.2f }, false) }));

        var second = service.SaveUser("Tone", DspToneCategory.Clean, "v2",
            new DcRigPreset(new List<DcRigModule> { new("amp", new Dictionary<string, float> { ["gain"] = 0.9f }, false) }),
            overwriteId: first.Id);

        Assert.Equal(first.Id, second.Id);
        var user = Assert.Single(service.UserPresets); // replaced, not appended
        Assert.Equal(0.9f, user.Rig.Modules[0].Params["gain"]);
        Assert.Equal("v2", user.Description);
    }

    [Fact]
    public void SaveUser_TwoDistinctSaves_GetUniqueIds()
    {
        var service = NewService(FullChain());
        var a = service.SaveUser("Same Name", DspToneCategory.Clean, "", new DcRigPreset(new List<DcRigModule>()));
        var b = service.SaveUser("Same Name", DspToneCategory.Clean, "", new DcRigPreset(new List<DcRigModule>()));
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, service.UserPresets.Count);
    }

    [Fact]
    public void DeleteUser_RemovesUserPreset()
    {
        var config = new FakeConfigurationService();
        var service = NewService(FullChain(), config);
        var saved = service.SaveUser("Temp", DspToneCategory.Bass, "", new DcRigPreset(new List<DcRigModule>()));

        Assert.True(service.DeleteUser(saved.Id));
        Assert.Empty(service.UserPresets);
        Assert.False(service.DeleteUser(saved.Id)); // already gone
        Assert.False(service.DeleteUser("no-such-id"));
    }

    [Fact]
    public void DeleteUser_OnBuiltInId_IsRefused()
    {
        var service = NewService(FullChain());
        var builtInId = DspTonePresetLibrary.All[0].Id;

        Assert.False(service.DeleteUser(builtInId));
        Assert.NotNull(service.FindById(builtInId)); // still present
    }

    [Fact]
    public void SaveUser_OverwriteBuiltIn_IsRefused_NoUserPresetCreated()
    {
        var service = NewService(FullChain());
        var builtIn = DspTonePresetLibrary.All[0];

        var result = service.SaveUser("Hijack", DspToneCategory.Clean, "",
            new DcRigPreset(new List<DcRigModule> { new("amp", new Dictionary<string, float> { ["gain"] = 0.99f }, false) }),
            overwriteId: builtIn.Id);

        Assert.Equal(builtIn.Id, result.Id);
        Assert.Equal(DspToneOrigin.BuiltIn, result.Origin); // returned unchanged
        Assert.Empty(service.UserPresets); // nothing persisted
        // The factory preset is untouched.
        Assert.Equal(builtIn.Rig.Modules.Count, service.FindById(builtIn.Id)!.Rig.Modules.Count);
    }

    /// <summary>
    ///     The save-as-a-tone round trip, exercised through the LIVE path:
    ///     snapshot the chain, then <c>SaveUser</c> the rig. That is what
    ///     <c>PedalboardViewModel.BuildCurrentRig</c> hands over, and since 0.60
    ///     it is the only snapshot implementation — the service's own
    ///     <c>CaptureCurrent</c> was a duplicate nothing called and is gone.
    ///     The snapshot's durable extras (ABI-v22 StringParams, ABI-v23 Branch)
    ///     are pinned where the real builder lives:
    ///     <c>PedalboardViewModelTests.BuildCurrentRig_carries_manual_string_params_for_durability</c>
    ///     and <c>PedalboardSplitViewModelTests.BuildCurrentRig_captures_the_branch_tag</c>.
    /// </summary>
    [Fact]
    public void SaveUserThenReapply_RoundTripsThroughTheChain()
    {
        var config = new FakeConfigurationService();

        // Apply a factory preset, then snapshot the resulting chain the way the
        // pedalboard does and persist it as a user tone.
        var buildChain = FullChain();
        var buildService = NewService(buildChain, config);
        var factory = buildService.Presets.First(p => p.Category == DspToneCategory.Clean);
        buildService.Apply(factory);
        var snapshot = new DcRigPreset(buildChain.GetNodes()
            .Select(n => new DcRigModule(
                n.ModuleTypeId,
                new Dictionary<string, float>(n.Values, StringComparer.Ordinal),
                n.Bypassed))
            .ToList());
        var captured = buildService.SaveUser("Snapshot", DspToneCategory.Clean, "", snapshot);

        // Re-apply the saved user preset onto a fresh chain — same module order.
        var replayChain = FullChain();
        var replayService = NewService(replayChain, config);
        replayService.Apply(replayService.FindById(captured.Id)!);

        // The applied factory chain carries the loudness-normalizing trailing
        // gain; the saved user preset replays it verbatim.
        Assert.Equal(
            factory.Rig.Modules.Select(m => m.ModuleTypeId)
                .Append(DcRigTargetCatalog.Gain).ToArray(),
            replayChain.GetNodes().Select(n => n.ModuleTypeId).ToArray());
    }

    /// <summary>
    ///     Replaces <c>ByCategory_IncludesFactoryThenUser</c>, deleted in 0.60
    ///     with the member it named. The service never had a category surface
    ///     anyone used: the browser's rail goes through
    ///     <c>PresetBrowserFilter.Filter</c> (pinned by
    ///     <c>PresetBrowserFilterTests</c>), which does STRICTLY MORE than a
    ///     category match — it also hides a tone superseded by a capture-backed
    ///     <c>nam-</c> twin, so a plain category filter would have listed both
    ///     halves of 70 pairs.
    ///     <para>
    ///         What DID need keeping is the other half that test asserted, and
    ///         which nothing else pinned: <c>AllPresets</c> puts every factory
    ///         preset before every user preset. <c>PresetBrowserFilter</c>'s
    ///         summary states it as fact and its <c>Filter</c> "never reorders"
    ///         on the strength of it.
    ///     </para>
    /// </summary>
    [Fact]
    public void AllPresets_PutsEveryFactoryPresetBeforeEveryUserPreset()
    {
        var service = NewService(FullChain());
        service.SaveUser("My Clean", DspToneCategory.Clean, "", new DcRigPreset(new List<DcRigModule>()));
        service.SaveUser("My Lead", DspToneCategory.Lead, "", new DcRigPreset(new List<DcRigModule>()));

        var all = service.AllPresets;

        Assert.Equal(DspTonePresetLibrary.All.Count + 2, all.Count);
        var firstUser = all.Select((p, i) => (p, i)).First(t => t.p.IsUser).i;
        Assert.All(all.Take(firstUser), p => Assert.False(p.IsUser));
        Assert.All(all.Skip(firstUser), p => Assert.True(p.IsUser));
        Assert.Contains(all, p => p is { IsUser: true, Name: "My Clean" });
    }

    private static void AssertRigEqual(DcRigPreset expected, DcRigPreset actual)
    {
        Assert.Equal(expected.Modules.Count, actual.Modules.Count);
        for (var i = 0; i < expected.Modules.Count; i++)
        {
            var e = expected.Modules[i];
            var a = actual.Modules[i];
            Assert.Equal(e.ModuleTypeId, a.ModuleTypeId);
            Assert.Equal(e.Bypassed, a.Bypassed);
            Assert.Equal(e.Params.Count, a.Params.Count);
            foreach (var (k, v) in e.Params)
            {
                Assert.True(a.Params.TryGetValue(k, out var av), $"Missing param '{k}'.");
                Assert.Equal(v, av);
            }
        }
    }
}
