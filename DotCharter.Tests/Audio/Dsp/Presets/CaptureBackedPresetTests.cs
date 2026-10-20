using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Audio.Dsp.Presets;

/// <summary>
///     The seam between the factory catalogue and the bundled capture files.
///     <para>
///         A <c>namamp</c> preset names its capture as a BARE file name that
///         <c>DspTonePresetService</c> resolves against the NAM library
///         directory at apply time. Nothing about that lookup can fail loudly:
///         a name with a typo, a file dropped from the bundle, or an extension
///         changed to <c>.NAM</c> all produce the same result — a
///         <c>namamp</c> node with no model, which is SILENCE, reported nowhere.
///         Since 0.37 the whole capture-backed half of the catalogue depends on
///         it, so the mapping is pinned here rather than discovered by ear.
///     </para>
/// </summary>
public sealed class CaptureBackedPresetTests
{
    private const string NamAmpType = "namamp";
    private const string ModelPath = "modelPath";

    private static string? FindBundleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;
        if (dir == null) return null;
        var bundle = UiSourceTree.Resolve(Path.Combine(dir.FullName, "DotCharter.UI", "NamCaptures"));
        return Directory.Exists(bundle) ? bundle : null;
    }

    /// <summary>Every (preset id, capture file) pair the catalogue references.</summary>
    private static IEnumerable<(string PresetId, string File)> CaptureReferences()
        => from preset in DspTonePresetLibrary.All
           from module in preset.Rig.Modules
           where module.ModuleTypeId == NamAmpType
           let file = module.StringParams?.GetValueOrDefault(ModelPath)
           where !string.IsNullOrWhiteSpace(file)
           select (preset.Id, file!);

    [Fact]
    public void EveryCaptureBackedPreset_NamesABundledCaptureFile()
    {
        var bundleDir = FindBundleDir();
        Assert.SkipWhen(bundleDir is null, "Bundle dir not found (running outside a source tree).");

        var bundled = UiSourceTree.EnumerateFiles(bundleDir!)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal); // ORDINAL: the extension is case-sensitive to NeuralAudio.

        var references = CaptureReferences().ToList();
        Assert.NotEmpty(references);

        var broken = references.Where(r => !bundled.Contains(r.File)).ToList();
        Assert.True(broken.Count == 0,
            "Preset(s) naming a capture that is not bundled — each of these applies as a MODELLESS " +
            "namamp, i.e. silence:\n" +
            string.Join("\n", broken.Select(b => $"  {b.PresetId} -> '{b.File}'")));
    }

    /// <summary>
    ///     A preset may only name a capture the manifest also lists. The manifest
    ///     is what <see cref="BundledNamCaptureInstaller" /> installs from, so a
    ///     file present in the repo but absent from the manifest is present at
    ///     BUILD time and missing at RUN time — the worst version of this bug,
    ///     because every local check passes.
    /// </summary>
    [Fact]
    public void EveryReferencedCapture_IsAlsoInTheInstallManifest()
    {
        var bundleDir = FindBundleDir();
        Assert.SkipWhen(bundleDir is null, "Bundle dir not found (running outside a source tree).");

        var manifest = BundledNamCaptureInstaller
            .ParseManifest(UiSourceTree.ReadAllText(
                Path.Combine(bundleDir!, BundledNamCaptureInstaller.ManifestFileName)))
            .Select(e => e.FileName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = CaptureReferences()
            .Where(r => !manifest.Contains(r.File))
            .Select(r => $"  {r.PresetId} -> '{r.File}'")
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0,
            "Preset(s) naming a capture absent from manifest.json (it would never be installed):\n" +
            string.Join("\n", missing));
    }

    /// <summary>
    ///     Every algorithmic preset that was ported has exactly one capture-backed
    ///     twin, and the pair is named the way the browser presents it: the twin
    ///     carries the plain name, the original is suffixed "(Legacy)". Pinned
    ///     because the two halves live in different files and a hand-added preset
    ///     on either side silently breaks the pairing the UI relies on.
    /// </summary>
    [Fact]
    public void EveryCaptureTwin_PairsWithALegacyPresetOfTheSameName()
    {
        var byId = DspTonePresetLibrary.All.ToDictionary(p => p.Id, StringComparer.Ordinal);

        var twins = DspTonePresetLibrary.All.Where(p => p.Id.StartsWith("nam-", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(twins);

        var problems = new List<string>();
        foreach (var twin in twins)
        {
            var legacyId = twin.Id["nam-".Length..];
            if (!byId.TryGetValue(legacyId, out var legacy))
                continue; // the four hand-authored nam-* showcases have no algorithmic original

            if (legacy.Name != twin.Name + " (Legacy)")
            {
                problems.Add($"  {twin.Id} is '{twin.Name}' but {legacy.Id} is '{legacy.Name}' " +
                             $"(expected '{twin.Name} (Legacy)')");
            }

            if (legacy.Category != twin.Category)
                problems.Add($"  {twin.Id} is {twin.Category} but {legacy.Id} is {legacy.Category}");
            if (twin.Rig.Modules.All(m => m.ModuleTypeId != NamAmpType))
                problems.Add($"  {twin.Id} has no namamp module — it is not capture-backed at all");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    ///     No preset may carry a <c>namamp</c> with no <c>modelPath</c> at all.
    ///     That is the failure the string-param plumbing was built to prevent
    ///     (a dropped string param persists a modelless, silent amp) and it is
    ///     cheap to assert at the catalogue level too.
    /// </summary>
    [Fact]
    public void NoPresetCarriesAModellessNeuralAmp()
    {
        var modelless = DspTonePresetLibrary.All
            .SelectMany(p => p.Rig.Modules.Select(m => (Preset: p, Module: m)))
            .Where(x => x.Module.ModuleTypeId == NamAmpType)
            .Where(x => string.IsNullOrWhiteSpace(x.Module.StringParams?.GetValueOrDefault(ModelPath)))
            .Select(x => x.Preset.Id)
            .ToList();

        Assert.True(modelless.Count == 0,
            "Preset(s) with a namamp carrying no modelPath (applies as silence): " +
            string.Join(", ", modelless));
    }

    /// <summary>
    ///     Every capture-backed preset's <c>cab</c> module sets <c>model</c>
    ///     EXPLICITLY. The bundled amp captures are all amp-only (no <c>-Cab-</c>
    ///     variants were chosen), so the cab module is the whole speaker — and a
    ///     cab entry with no <c>model</c> silently rides the native default
    ///     (0, "1x12 Combo"), which is an authoring accident indistinguishable
    ///     from a decision. An omitted KNOB falls back to a neutral default by
    ///     design; the cab MODEL is a voicing choice, and for a preset whose
    ///     whole point is mirroring a specific rig it must be written down.
    /// </summary>
    [Fact]
    public void EveryCaptureBackedPreset_ChoosesItsCabModelExplicitly()
    {
        var captureBacked = DspTonePresetLibrary.All
            .Where(p => p.Rig.Modules.Any(m => m.ModuleTypeId == NamAmpType))
            .ToList();
        Assert.NotEmpty(captureBacked);

        var silent = new List<string>();
        foreach (var preset in captureBacked)
        {
            foreach (var cab in preset.Rig.Modules.Where(m => m.ModuleTypeId == "cab"))
            {
                if (!cab.Params.ContainsKey("model"))
                    silent.Add(preset.Id);
            }
        }

        Assert.True(silent.Count == 0,
            "Capture-backed preset(s) whose cab never sets 'model' — each rides the native " +
            "default 1x12 silently instead of choosing its speaker:\n  " +
            string.Join("\n  ", silent.Distinct()));
    }
}
