using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
/// The shipped factory catalogue against the <b>LIVE native module catalogue</b>
/// and through the whole preset pipeline.
///
/// <para><c>DspTonePresetLibraryTests.EveryModule_HasAKnownTypeId_AndInRangeParams</c>
/// validates every preset against the MANAGED mirror, and
/// <c>DcRigTargetCatalogTests</c> validates the mirror against native — but the
/// second of those walks <c>DcRigTargetCatalog.AllModules</c>, so it can only
/// catch a mirror entry that drifted, never a mirror entry that is MISSING.
/// A preset built on a module the engine does not have would pass both and fail
/// at apply time as a node that was never added. This closes that loop: preset →
/// native, directly.</para>
///
/// <para>It also runs every preset through the two gates a shared tone crosses —
/// <see cref="TonePresetJsonFile"/> (export/import) and
/// <see cref="DcRigPresetPreparation"/> (the one preset→rig preparation
/// pipeline) — because a preset the browser can show but nobody can send is half
/// a feature, and the import gate rejects an unknown module id WHOLESALE.</para>
///
/// <para>Skips cleanly (never fails) where the native ABI cannot load, the same
/// way <c>DcRigTargetCatalogTests</c> does.</para>
/// </summary>
public sealed class FactoryPresetsAgainstLiveCatalogueTests
{
    /// <summary>Loads the catalogue over the REAL P/Invoke interop; an absent or
    /// unloadable DLL surfaces as an empty list, never a test crash.</summary>
    private static IReadOnlyList<DspModuleType> TryLoadNativeCatalog()
    {
        try
        {
            var svc = new JuceDspChainService();
            svc.LoadCatalog();
            return svc.Catalog;
        }
        catch
        {
            return [];
        }
    }

    [Fact]
    public void EveryShippedPreset_UsesOnlyModulesAndParamsTheENGINEActuallyHas()
    {
        var native = TryLoadNativeCatalog();
        Assert.SkipWhen(native.Count == 0,
            "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var byId = native.ToDictionary(m => m.Id, StringComparer.Ordinal);

        // Collect every fault before failing: a preset wave usually breaks in
        // several places at once, and fail-on-first makes the fix a one-param-
        // per-run grind.
        var faults = new List<string>();

        foreach (var preset in DspTonePresetLibrary.All)
        {
            foreach (var module in preset.Rig.Modules)
            {
                if (!byId.TryGetValue(module.ModuleTypeId, out var type))
                {
                    faults.Add($"'{preset.Id}': the engine has no module '{module.ModuleTypeId}'.");
                    continue;
                }

                var paramsById = type.Params.ToDictionary(p => p.Id, StringComparer.Ordinal);
                foreach (var (paramId, value) in module.Params)
                {
                    if (!paramsById.TryGetValue(paramId, out var info))
                    {
                        faults.Add($"'{preset.Id}': '{module.ModuleTypeId}' has no param '{paramId}'.");
                        continue;
                    }

                    if (value < info.Min - 1e-4f || value > info.Max + 1e-4f)
                    {
                        faults.Add(
                        $"'{preset.Id}': '{module.ModuleTypeId}.{paramId}' = {value} is outside " +
                        $"the engine's [{info.Min}, {info.Max}].");
                    }
                }

                foreach (var stringId in module.StringParams?.Keys ?? [])
                {
                    if (type.StringParams.All(s => s.Id != stringId))
                        faults.Add($"'{preset.Id}': '{module.ModuleTypeId}' has no string param '{stringId}'.");
                }

                // ABI v34: the engine REJECTS a detector key on a module type that
                // does not advertise the capability, and a rejected configuration
                // leaves the module BYPASSED. A shipped preset that keyed the wrong
                // module would therefore go out with one pedal silently switched off.
                if (module.Sidechain != JuceSharp.Dsp.JuceDspSidechainSource.None && !type.SupportsSidechain)
                {
                    faults.Add(
                    $"'{preset.Id}': '{module.ModuleTypeId}' is keyed off " +
                    $"{module.Sidechain}, but the engine says this module type takes no sidechain " +
                    "- the key would be rejected and the module would stay bypassed.");
                }
            }
        }

        Assert.True(faults.Count == 0,
            $"{faults.Count} factory preset fault(s) against the LIVE native catalogue:\n  " +
            string.Join("\n  ", faults));
    }

    [Fact]
    public void EveryShippedPreset_SurvivesTheExportImportRoundTrip_WithNoFault()
    {
        // The import gate rejects a tone WHOLESALE on one unknown module id, so
        // a factory preset that cannot come back through it is a tone nobody can
        // share — and that is invisible until somebody tries.
        foreach (var preset in DspTonePresetLibrary.All)
        {
            var imported = TonePresetJsonFile.Parse(TonePresetJsonFile.Serialize(preset));

            Assert.True(imported.Ok,
                $"preset '{preset.Id}' failed to round-trip: {imported.Fault} ({imported.Detail})");
            Assert.Equal(TonePresetImportFault.None, imported.Fault);

            // Same modules in the same order. The import FILLS defaults, so the
            // param COUNT legitimately grows; the identity of the chain does not.
            Assert.Equal(
                preset.Rig.Modules.Select(m => m.ModuleTypeId).ToArray(),
                imported.Preset!.Rig.Modules.Select(m => m.ModuleTypeId).ToArray());

            // Every value the preset authored survives verbatim — an import that
            // silently CLAMPED one would mean the preset shipped out of range.
            for (var i = 0; i < preset.Rig.Modules.Count; i++)
            {
                foreach (var (paramId, value) in preset.Rig.Modules[i].Params)
                    Assert.Equal(value, imported.Preset.Rig.Modules[i].Params[paramId], 0.0001);
            }

            // String params (a namamp capture name, a miku phrase) travel too.
            for (var i = 0; i < preset.Rig.Modules.Count; i++)
            {
                if (preset.Rig.Modules[i].StringParams is not { } strings) continue;
                foreach (var (id, text) in strings)
                    Assert.Equal(text, imported.Preset.Rig.Modules[i].StringParams?[id]);
            }

            // …and so do the two NODE-STATE fields, which no param loop above
            // would notice going missing: the ABI-v23 branch tag and the ABI-v34
            // sidechain key are both re-pushed by the applier only when the
            // preset CARRIES them, so a shape that dropped one flattens a split
            // region / unkeys a ducker with every op still reporting success.
            for (var i = 0; i < preset.Rig.Modules.Count; i++)
            {
                Assert.Equal(preset.Rig.Modules[i].Branch, imported.Preset.Rig.Modules[i].Branch);
                Assert.Equal(preset.Rig.Modules[i].Sidechain, imported.Preset.Rig.Modules[i].Sidechain);
            }
        }
    }

    [Fact]
    public void EveryShippedPreset_PreparesIntoAFullyDefaultedInRangeRig()
    {
        // DcRigPresetPreparation is the ONE preset→rig pipeline (live apply and
        // offline re-amp both go through it). Its FillDefaults step skips an
        // unknown module type SILENTLY, so a preset naming a module the mirror
        // lost would prepare into a rig with a sparse node nobody filled — which
        // then inherits the previous tone's values on every omitted param.
        foreach (var preset in DspTonePresetLibrary.All)
        {
            var prepared = DcRigPresetPreparation.Prepare(preset, namCaptureDirectory: null);

            Assert.Equal(preset.Rig.Modules.Count + 1, prepared.Modules.Count);   // + the trailing master gain
            Assert.Equal(DcRigTargetCatalog.Gain, prepared.Modules[^1].ModuleTypeId);

            foreach (var module in prepared.Modules)
            {
                var specs = DcRigTargetCatalog.AllModules
                    .First(entry => entry.ModuleTypeId == module.ModuleTypeId).Params;

                // FillDefaults means EVERY param is present after preparation.
                Assert.Equal(specs.Count, module.Params.Count);
                foreach (var spec in specs)
                {
                    Assert.True(module.Params.TryGetValue(spec.Id, out var value),
                        $"'{preset.Id}': '{module.ModuleTypeId}.{spec.Id}' was not filled.");
                    Assert.InRange(value, spec.Min, spec.Max);
                }
            }
        }
    }

    [Fact]
    public void TheGearWaveShowcase_ReachesEveryNewDrawer()
    {
        // The showcase exists so the 0.9 wave's modules are reachable without
        // building a board by hand. Naming the ids would only assert that the
        // list I wrote is the list I wrote; naming the DRAWERS asserts the
        // coverage the block is FOR, and fails when a preset is dropped or
        // rewritten around different gear.
        var used = DspTonePresetLibrary.All
            .Where(p => p.Id.StartsWith("gear-", StringComparison.Ordinal))
            .SelectMany(p => p.Rig.Modules)
            .Select(m => m.ModuleTypeId)
            .ToHashSet(StringComparer.Ordinal);

        string[] mustAppear =
        [
            // one representative per new drawer, plus the modules whose absence
            // from a preset would leave them unreachable outside the rig editor
            "rat", "ds1", "hm2", "bassod", "tubepre",              // drive
            "optocomp", "multiband", "limiter", "transient",       // dynamics
            "bassamp", "acpre", "poweramp", "micpos",              // amp / cab
            "svf", "vocoder", "talkbox", "tilt",                   // filter
            "tzflanger", "autopan", "ensemble",                    // modulation
            "twelvestring", "basssynth",                           // pitch
            "multitap",                                            // delay
            "hallreverb", "gatedreverb", "reversereverb",          // reverb
            "volume", "looper", "midside"                          // utility
        ];

        var missing = mustAppear.Where(id => !used.Contains(id)).ToArray();
        Assert.True(missing.Length == 0,
            $"the gear-wave showcase reaches none of: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryFeaturedPresetId_ExistsInTheLibrary()
    {
        // PresetCurationTests already covers this for the pre-0.9 shelf; keeping
        // it here as well is deliberate — the showcase block was appended to
        // Featured in the same change that added the presets, and a typo in one
        // half is exactly what a dangling featured id looks like.
        var ids = DspTonePresetLibrary.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dangling = PresetCuration.FeaturedIds.Where(id => !ids.Contains(id)).ToArray();

        Assert.True(dangling.Length == 0,
            $"featured ids with no preset: {string.Join(", ", dangling)}");
    }
}
