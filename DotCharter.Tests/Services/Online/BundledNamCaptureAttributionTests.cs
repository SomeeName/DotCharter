using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
/// Provenance gate for the app's bundled NAM captures: every shipped capture
/// file MUST have a manifest.json entry with a license + attribution URL, and
/// vice-versa, and every file MUST be named in ATTRIBUTION.md. This enforces the
/// attribution policy — no capture can ship without recorded provenance.
/// Resolves the repo's DotCharter.UI/NamCaptures dir from the test base dir;
/// skips (does not fail) when run outside a source tree.
/// </summary>
public sealed class BundledNamCaptureAttributionTests
{
    /// <summary>
    ///     The bundled set stopped being a "starter pack" in 0.37: it is now the
    ///     BACKBONE of the factory tone catalogue — every capture-backed preset
    ///     resolves its <c>modelPath</c> to one of these files, so a missing
    ///     capture is a silent preset. The cap still exists (an unbounded model
    ///     folder would dominate the release zip and the first-run install copy),
    ///     it is just sized for a full amp+pedal roster rather than five examples.
    /// </summary>
    private const long MaxTotalBytes = 32L * 1024 * 1024;
    private static readonly string[] CaptureExtensions = [".nam", ".aidax"];

    private static string? FindBundleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;
        if (dir == null) return null;
        var bundle = UiSourceTree.Resolve(Path.Combine(dir.FullName, "DotCharter.UI", "NamCaptures"));
        return Directory.Exists(bundle) ? bundle : null;
    }

    private static bool IsCapture(string path)
        => CaptureExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EveryBundledCaptureFile_HasAManifestEntry_AndViceVersa()
    {
        var bundleDir = FindBundleDir();
        Assert.SkipWhen(bundleDir is null, "Bundle dir not found (running outside a source tree).");

        var manifestPath = Path.Combine(bundleDir!, BundledNamCaptureInstaller.ManifestFileName);
        Assert.True(File.Exists(manifestPath), "NamCaptures/manifest.json is missing.");

        var entries = BundledNamCaptureInstaller.ParseManifest(UiSourceTree.ReadAllText(manifestPath));
        Assert.NotEmpty(entries);

        var filesOnDisk = UiSourceTree.EnumerateFiles(bundleDir!)
            .Where(IsCapture)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filesInManifest = entries.Select(e => e.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undocumented = filesOnDisk.Except(filesInManifest, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(undocumented.Count == 0,
            $"Bundled capture file(s) with NO manifest entry: {string.Join(", ", undocumented)}");

        var missingFiles = filesInManifest.Except(filesOnDisk, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(missingFiles.Count == 0,
            $"manifest.json lists file(s) not present on disk: {string.Join(", ", missingFiles)}");
    }

    [Fact]
    public void EveryManifestEntry_CarriesCompleteProvenance()
    {
        var bundleDir = FindBundleDir();
        Assert.SkipWhen(bundleDir is null, "Bundle dir not found (running outside a source tree).");

        var entries = BundledNamCaptureInstaller.ParseManifest(
            UiSourceTree.ReadAllText(Path.Combine(bundleDir!, BundledNamCaptureInstaller.ManifestFileName)));

        foreach (var e in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title), $"{e.FileName}: blank title.");
            Assert.False(string.IsNullOrWhiteSpace(e.Author), $"{e.FileName}: blank author.");
            Assert.False(string.IsNullOrWhiteSpace(e.License), $"{e.FileName}: blank license.");
            Assert.False(string.IsNullOrWhiteSpace(e.LicenseUrl), $"{e.FileName}: blank license URL.");
            Assert.False(string.IsNullOrWhiteSpace(e.AttributionUrl), $"{e.FileName}: blank attribution URL.");
            // Only redistribution-permitting licenses may ship. GPL-3.0 is on
            // the list as of 0.37 and the reason is specific, not a relaxation:
            // the shipped binary statically links JUCE 9, Rubber Band 4 and the
            // VST 3 SDK, all GPLv3-or-commercial, so the DISTRIBUTED app is
            // already effectively GPLv3 (THIRD-PARTY-NOTICES.md §2.2). Copyleft model data
            // adds no obligation the release did not already carry. It does
            // constrain the REPO — the source is GPL-3.0-or-later since v0.50.0, and these data files
            // do not — which is why ATTRIBUTION.md states it in full and names
            // the one-step reversal. Anything not on this list (notably
            // "unlicensed" model-zoo files) must still be refused.
            Assert.Contains(e.License, new[] { "MIT", "CC0", "CC-BY", "CC-BY-4.0", "Public Domain", "GPL-3.0" });
        }
    }

    [Fact]
    public void AttributionMarkdown_ExistsAndNamesEveryBundledFile()
    {
        var bundleDir = FindBundleDir();
        Assert.SkipWhen(bundleDir is null, "Bundle dir not found (running outside a source tree).");

        var attributionPath = Path.Combine(bundleDir!, "ATTRIBUTION.md");
        Assert.True(File.Exists(attributionPath), "NamCaptures/ATTRIBUTION.md is missing.");
        var text = UiSourceTree.ReadAllText(attributionPath);

        foreach (var file in UiSourceTree.EnumerateFiles(bundleDir!).Where(IsCapture).Select(Path.GetFileName))
        {
            Assert.True(text.Contains(file!, StringComparison.OrdinalIgnoreCase),
                $"ATTRIBUTION.md does not mention bundled file '{file}'.");
        }
    }

    [Fact]
    public void BundledSet_StaysSmall()
    {
        var bundleDir = FindBundleDir();
        Assert.SkipWhen(bundleDir is null, "Bundle dir not found (running outside a source tree).");

        var total = UiSourceTree.EnumerateFiles(bundleDir!).Where(IsCapture).Sum(f => new FileInfo(f).Length);
        Assert.True(total <= MaxTotalBytes,
            $"Bundled captures total {total:N0} bytes, over the {MaxTotalBytes:N0} cap.");
    }
}
