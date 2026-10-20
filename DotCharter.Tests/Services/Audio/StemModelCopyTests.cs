using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     What Settings ▸ Stem models SAYS about the models must match what this build
///     can run, in every language. The descriptions once promised DirectML while the
///     only ONNX Runtime package was the CUDA one; the build now ships the DirectML
///     runtime and NO CUDA, so the same fault would be copy that still promises CUDA
///     — and the copy was once English under German and Japanese headings.
/// </summary>
public sealed class StemModelCopyTests
{
    private static readonly string[] Languages = ["", ".de-DE", ".ja-JP"];

    /// <summary>
    ///     The premise every other case leans on: the ONNX project references the
    ///     DirectML runtime and nothing references the CUDA (Gpu) one. If CUDA ever
    ///     comes back, this fails first: offer it in
    ///     <see cref="StemExecutionProviderOptions" /> and say so in the copy, then
    ///     change this test.
    /// </summary>
    [Fact]
    public void TheBuildShipsTheDirectMlRuntime_AndNoCuda()
    {
        var root = TestPaths.RepoRoot();
        var projects = Directory.GetDirectories(root, "DotCharter*")
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "JuceSharp", "src"), "*.csproj", SearchOption.AllDirectories))
            .ToList();
        var onnx = projects.SingleOrDefault(p => p.EndsWith("JuceSharp.StemSeparation.Onnx.csproj", StringComparison.Ordinal));
        Assert.True(onnx != null, "the ONNX stem-separation project was not found; the scan is blind");
        Assert.Contains("Microsoft.ML.OnnxRuntime.DirectML", PackageReferences(onnx));
        var cuda = projects.SelectMany(p => PackageReferences(p).Select(r => $"{Path.GetFileName(p)}: {r}"))
            .Where(r => r.Contains("OnnxRuntime.Gpu", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(cuda.Count == 0, "the CUDA ONNX Runtime package is referenced: " + string.Join(", ", cuda));
        var options = StemExecutionProviderOptions.For(FakeLocalization.KeysOnly(), directMlBlocker: null);
        Assert.Equal(["DirectML", "CPU"], options.Select(o => o.Label));
        Assert.All(options, o => Assert.True(o.IsAvailable));
    }

    /// <summary>Every built-in model carries the app's own copy as a resx key, defined and non-empty in all three languages.</summary>
    [Fact]
    public void EveryBuiltInModel_HasItsDescriptionInEveryLanguage()
    {
        var keys = StemModelCatalog.DescriptionKeys.ToList();
        // 10 since #254 removed the two MrCitron htdemucs rows and their copy.
        Assert.True(keys.Count >= 10, $"only {keys.Count} description keys; the catalogue lost its copy");
        Assert.All(StemModelCatalog.BuiltIn, d => Assert.False(d.DescriptionKey is null, $"{d.Id} has no resx description"));

        foreach (var language in Languages)
        {
            var values = ResxValues(language);
            foreach (var key in keys)
            {
                Assert.True(values.TryGetValue(key, out var value) && value.Trim().Length > 0,
                    $"Resources{language}.resx has no value for {key}");
            }
        }
    }

    /// <summary>
    ///     No stem-models string promises a provider the build lacks: only the rows
    ///     about the OLD CUDA runtime (the "GpuAccel" ones, whose whole point is that
    ///     it is unused) may name CUDA, and nothing tells a user to download it.
    /// </summary>
    [Fact]
    public void NoStemModelsCopy_PromisesCuda()
    {
        foreach (var language in Languages)
        {
            var offenders = ResxValues(language)
                .Where(kv => kv.Key.StartsWith("Settings_StemModels_", StringComparison.Ordinal)
                             && !kv.Key.StartsWith("Settings_StemModels_GpuAccel", StringComparison.Ordinal)
                             && kv.Key != "Settings_StemModels_RefreshGpu")
                .Where(kv => Regex.IsMatch(kv.Value, "CUDA|cuDNN", RegexOptions.IgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            Assert.True(offenders.Count == 0, $"Resources{language}.resx: {string.Join(", ", offenders)} promise CUDA");
        }
        Assert.All(StemModelCatalog.BuiltIn, d => Assert.DoesNotContain("CUDA", d.Description, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The row reads the key in the UI language rather than the English fallback.</summary>
    [Fact]
    public void AModelRow_ShowsItsDescriptionThroughTheLocalization()
    {
        var descriptor = StemModelCatalog.BuiltIn.First(d => d.DescriptionKey != null);
        var row = new StemModelRowViewModel(
            new StemModelManager(modelsDirectoryOverride: Path.GetTempPath()), descriptor, FakeLocalization.KeysOnly());

        Assert.Equal(descriptor.DescriptionKey, row.Description);
    }

    private static IEnumerable<string> PackageReferences(string csproj) =>
        XDocument.Load(csproj).Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? "");

    private static Dictionary<string, string> ResxValues(string language) =>
        XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{language}.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "", StringComparer.Ordinal);
}
