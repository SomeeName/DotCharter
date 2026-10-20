using System.Reflection;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
/// Guards the hand-kept <see cref="DcRigTargetCatalog"/> mirror of the native
/// DSP module catalogue. Internal consistency (unique ids, sane ranges,
/// defaults in range, consts ↔ entries) is asserted unconditionally; when the
/// native DSP ABI is loadable in the test process, every managed entry is
/// additionally checked against the NATIVE catalogue metadata
/// (<see cref="JuceDspChainService.LoadCatalog"/>) so silent drift is caught on
/// a machine with the built DLL. Never depends on native being present — the
/// native comparison skips cleanly when the ABI can't load.
/// </summary>
public sealed class DcRigTargetCatalogTests
{
    [Fact]
    public void ManagedCatalog_IsInternallyConsistent()
    {
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (moduleId, specs) in DcRigTargetCatalog.AllModules)
        {
            Assert.False(string.IsNullOrWhiteSpace(moduleId), "Blank module type id.");
            Assert.True(moduleIds.Add(moduleId), $"Duplicate module type id '{moduleId}'.");
            Assert.True(specs.Count > 0, $"Module '{moduleId}' has no params.");

            var paramIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in specs)
            {
                Assert.False(string.IsNullOrWhiteSpace(spec.Id), $"'{moduleId}': blank param id.");
                Assert.True(paramIds.Add(spec.Id), $"'{moduleId}': duplicate param id '{spec.Id}'.");
                Assert.True(spec.Min < spec.Max,
                    $"'{moduleId}.{spec.Id}': min {spec.Min} is not < max {spec.Max}.");
                Assert.True(spec.Default >= spec.Min && spec.Default <= spec.Max,
                    $"'{moduleId}.{spec.Id}': default {spec.Default} outside [{spec.Min}, {spec.Max}].");
            }
        }

        // Every declared module-type-id const has a catalogue entry and vice
        // versa — adding a module one-sidedly is exactly the drift this catches.
        var declaredIds = typeof(DcRigTargetCatalog)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(declaredIds, moduleIds);
    }

    [Fact]
    public void ManagedCatalog_MatchesNativeCatalogue_WhenTheNativeAbiIsLoadable()
    {
        var native = TryLoadNativeCatalog();
        Assert.SkipWhen(native.Count == 0,
            "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        // Collect EVERY mismatch before failing — a drifted catalogue usually
        // drifts in several places at once, and fail-on-first makes the fix a
        // one-param-per-test-run grind.
        var mismatches = new List<string>();
        var nativeById = native.ToDictionary(m => m.Id, StringComparer.Ordinal);
        // A mirror that STALLS is drift too: every native id must have a managed
        // row, or a new module ships with no RsTone target and no preset
        // validation while this test stays green (2026-09-12: the mirror sat at
        // 57 against a native 100 and nothing here noticed).
        var mirrored = DcRigTargetCatalog.AllModules.Select(t => t.ModuleTypeId).ToHashSet(StringComparer.Ordinal);
        foreach (var m in native)
        {
            if (!mirrored.Contains(m.Id))
                mismatches.Add($"'{m.Id}': native catalogue has it, the managed mirror does not.");
        }

        foreach (var (moduleId, specs) in DcRigTargetCatalog.AllModules)
        {
            if (!nativeById.TryGetValue(moduleId, out var nativeType))
            {
                mismatches.Add($"'{moduleId}' is missing from the native catalogue.");
                continue;
            }

            var nativeParams = nativeType.Params.ToDictionary(p => p.Id, StringComparer.Ordinal);
            if (specs.Count != nativeParams.Count)
                mismatches.Add($"'{moduleId}': managed mirror has {specs.Count} params, native reports {nativeParams.Count}.");

            foreach (var spec in specs)
            {
                if (!nativeParams.TryGetValue(spec.Id, out var np))
                {
                    mismatches.Add($"'{moduleId}.{spec.Id}' is missing from the native catalogue.");
                    continue;
                }
                CollectDrift(mismatches, moduleId, spec.Id, "min", np.Min, spec.Min);
                CollectDrift(mismatches, moduleId, spec.Id, "max", np.Max, spec.Max);
                CollectDrift(mismatches, moduleId, spec.Id, "default", np.Default, spec.Default);
            }
        }

        Assert.True(mismatches.Count == 0,
            $"Managed mirror drifted from the native catalogue ({mismatches.Count}):\n  " +
            string.Join("\n  ", mismatches));
    }

    private static void CollectDrift(List<string> sink, string moduleId, string paramId, string what, float native, float managed)
    {
        if (MathF.Abs(native - managed) > 1e-4f)
            sink.Add($"'{moduleId}.{paramId}' {what}: managed mirror has {managed}, native reports {native}.");
    }

    /// <summary>Loads the catalogue over the REAL P/Invoke interop; an absent /
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
}
