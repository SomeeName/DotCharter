using System.Reflection;
using DotCharter.RigLab;

namespace DotCharter.Tests.RigLab;

/// <summary>
///     The reason RigLab exists: a player who wants the rig gets the rig, not
///     DotCharter's charting stack. What the compiler emitted is the evidence —
///     the WHOLE transitive closure of first-party and package assemblies RigLab
///     loads, walked from its own metadata, so a reference that arrives through
///     the rig's assemblies counts exactly as much as one RigLab names itself.
/// </summary>
public sealed class RigLabDependencyTests
{
    /// <summary>The first-party assemblies RigLab may reach: the domain model,
    /// the strings, and the rig's two assemblies.</summary>
    private static readonly string[] AllowedFirstParty =
        ["DotCharter.Core", "DotCharter.Localization", "DotCharter.Tone", "DotCharter.Tone.UI"];

    /// <summary>What must never ship with RigLab: the app itself, its service
    /// layer, note detection and chart analysis, the renderer, stem separation
    /// (ONNX Runtime, JuceSharp's separators), sheet music, the psarc index, the
    /// Wwise tools and the dock. The AI tone DESIGNER ships (its Claude provider
    /// brings the Anthropic SDK); match-from-audio does not — it is ONNX.</summary>
    private static readonly string[] Forbidden =
    [
        "DotCharter", "DotCharter.UI", "DotCharter.Services", "DotCharter.Analysis", "DotCharter.Rendering",
        "DotCharter.Presentation", "Microsoft.ML.OnnxRuntime", "JuceSharp.StemSeparation", "AlphaTab",
        "AlphaSkia", "Microsoft.Data.Sqlite", "SQLitePCLRaw", "LWI.WwiseTools", "Ww2Ogg",
        "NVorbis", "Dock.", "Silk.NET"
    ];

    private static IReadOnlyCollection<string> Closure()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([typeof(RigLabHost).Assembly]);
        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                if (name.Name is null || !seen.Add(name.Name)) continue;
                // Framework assemblies are not the question; the walk only
                // follows what this repo and its packages bring.
                if (name.Name.StartsWith("System", StringComparison.Ordinal) ||
                    name.Name.StartsWith("Microsoft.Win32", StringComparison.Ordinal) ||
                    name.Name is "netstandard" or "mscorlib")
                {
                    continue;
                }

                try { queue.Enqueue(Assembly.Load(name)); }
                catch (FileNotFoundException) { /* not deployed with the tests: its name is still recorded */ }
            }
        }
        return seen;
    }

    [Fact]
    public void RigLab_ReachesOnlyTheRigsFirstPartyAssemblies()
    {
        var firstParty = Closure()
            .Where(n => n.StartsWith("DotCharter", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Non-vacuity: the walk really does reach the rig.
        Assert.Contains("DotCharter.Tone.UI", firstParty);
        Assert.Contains("DotCharter.Tone", firstParty);

        var illegal = firstParty.Except(AllowedFirstParty, StringComparer.Ordinal).ToArray();
        Assert.True(illegal.Length == 0,
            $"RigLab reaches {string.Join(", ", illegal)} — allowed: {string.Join(", ", AllowedFirstParty)}. " +
            "The standalone rig would ship the charting app with it.");
    }

    [Fact]
    public void RigLab_ShipsNoneOfTheChartingStack()
    {
        var closure = Closure();
        // Non-vacuity: the walk sees packages, not just first-party names.
        Assert.Contains("Avalonia.Base", closure);
        Assert.Contains("JuceSharp", closure);

        var offending = closure
            .Where(n => Forbidden.Any(f => f.EndsWith('.') ? n.StartsWith(f, StringComparison.Ordinal)
                                                           : n == f || n.StartsWith(f + ".", StringComparison.Ordinal)))
            .Where(n => !AllowedFirstParty.Contains(n))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(offending.Length == 0, $"RigLab would ship {string.Join(", ", offending)}.");
    }

    /// <summary>The csproj half: a ProjectReference that emits nothing still
    /// lets the next line of RigLab code compile against the charting app.</summary>
    [Fact]
    public void RigLab_csproj_ReferencesNeitherTheAppNorItsServices()
    {
        var csproj = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.RigLab", "DotCharter.RigLab.csproj"));
        var references = System.Text.RegularExpressions.Regex
            .Matches(csproj, "<ProjectReference\\s+Include=\"([^\"]+)\"")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value))
            .ToArray();

        Assert.Contains("DotCharter.Tone.UI", references);
        Assert.DoesNotContain("DotCharter.UI", references);
        Assert.DoesNotContain("DotCharter.Services", references);
        Assert.DoesNotContain("DotCharter.Analysis", references);
    }
}
