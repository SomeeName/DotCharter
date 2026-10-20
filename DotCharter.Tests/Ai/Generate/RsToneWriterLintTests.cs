using System.Text.RegularExpressions;
using DotCharter.Services.Ai.Generate;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     <b>Nothing this lane writes bypasses the catalogue.</b> Two halves, because
///     neither one alone is enough: a source lint that every file constructing an
///     <see cref="RsTone" /> under <c>Ai/Generate/</c> also reaches
///     <see cref="RsToneCatalogValidation" />, and a behavioural pair proving the
///     validator actually refuses.
///     <para>
///         The source half is the one that catches a NEW writer — a second path added
///         next year that builds a tone and hands it to the project, with its own
///         perfectly passing unit tests and no gate. That is the exact shape this
///         repo's dead-registration bugs took.
///     </para>
/// </summary>
public sealed class RsToneWriterLintTests
{
    private static string LaneRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "DotCharter.Services", "Ai", "Generate");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("DotCharter.Services/Ai/Generate was not found from the test binaries.");
    }

    /// <summary>Comments and string literals masked out — a `see cref` is exactly as
    /// inert as a comment, and this repo has watched a lint count prose before.</summary>
    private static string Strip(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\n]*", " ");
        source = Regex.Replace(source, "\"\"\".*?\"\"\"", "\"\"", RegexOptions.Singleline);
        source = Regex.Replace(source, @"""(\\.|[^""\\])*""", "\"\"");
        return source;
    }

    [Fact]
    public void EveryFileThatBuildsAnRsTone_AlsoReachesTheValidator()
    {
        var root = LaneRoot();
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length >= 5, $"only {files.Length} files under {root} — the lint lost its subject");

        var builders = new List<string>();
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var source = Strip(File.ReadAllText(file));
            if (!source.Contains("new RsTone(", StringComparison.Ordinal)) continue;

            builders.Add(Path.GetFileName(file));
            if (!source.Contains("RsToneCatalogValidation.", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(builders.Count > 0,
            "no file under Ai/Generate constructs an RsTone — the lint is passing vacuously");
        Assert.True(offenders.Count == 0,
            $"these build an RsTone without reaching RsToneCatalogValidation: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheLaneNeverCLAMPSAKnobInsteadOfRejectingIt()
    {
        // RsToneAuthoring.ClampKnobValue is the tone EDITOR's helper and is right
        // there. Reaching for it here would turn "the model misread the unit" into a
        // tone nobody can explain — the fault this lane exists to surface.
        var offenders = Directory
            .GetFiles(LaneRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => Strip(File.ReadAllText(f)).Contains("ClampKnobValue", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"these clamp a generated knob instead of rejecting it: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheValidatorActuallyRefuses_SoTheSourceLintIsNotTheOnlyClaim()
    {
        // A source lint proves a CALL, not an EFFECT. This is the effect.
        var (amp, cab) = GenerateFakes.RealGuitarGear();
        var bogus = new RsTone(
            "gen", "Generated", RsToneAuthoring.DefaultVolumeDb, [RsToneAuthoring.CleanDescriptorUIName],
            new RsAmp("Amp_DoesNotExist", "Amp", new Dictionary<string, float>(StringComparer.Ordinal)),
            new RsCab(cab, RsGearCatalog.Shared.GetCategory(cab), new Dictionary<string, float>(StringComparer.Ordinal)),
            [], [], []);

        Assert.Throws<RsToneCatalogException>(
            () => RsToneCatalogValidation.Require(bogus, RsGearCatalog.Shared, "x"));

        var fine = bogus with
        {
            Amp = new RsAmp(amp, RsGearCatalog.Shared.GetCategory(amp),
                new Dictionary<string, float>(StringComparer.Ordinal))
        };
        Assert.Same(fine, RsToneCatalogValidation.Require(fine, RsGearCatalog.Shared, "x"));
    }
}
