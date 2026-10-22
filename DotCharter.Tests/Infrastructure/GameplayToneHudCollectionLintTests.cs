using System.Reflection;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every test class that reads or writes the renderer's process-wide
///     <c>GameplayToneHud</c> channel joins the serialized <c>GameplayToneHud</c> collection.
///     <para>
///         The channel is two statics (the enable toggle and the rig override). A
///         tone coordinator built with the app's HUD adapter
///         (<c>RenderingGameplayToneHud</c>) writes the override on every apply, and the
///         snapshot tests read it back, so two such classes running side by side see
///         each other's tone names. A coordinator built WITHOUT the adapter writes
///         nothing, so the preview, levelling and audition classes that build one
///         that way run in parallel with the rest of the gate.
///     </para>
///     <para>
///         Source scan for the USE (comments stripped), reflection for the ATTRIBUTE.
///         The scan keys on the statics and the adapter by name, so a class that
///         starts passing the adapter or touching the statics is flagged until it
///         joins the collection.
///     </para>
/// </summary>
public sealed class GameplayToneHudCollectionLintTests
{
    private const string CollectionName = "GameplayToneHud";

    /// <summary>
    ///     Naming a static member of the channel, or constructing the adapter that
    ///     forwards to it. A bare type name does not count: the lint's own
    ///     declarations and the record type in the renderer would match it.
    /// </summary>
    private static readonly Regex TouchesTheChannel = new(
        @"\bGameplayToneHud\s*\.\s*(Enabled|Rig|SetRig|ClearRig)\b|\bRenderingGameplayToneHud\b",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+(?<n>[A-Za-z_]\w*)", RegexOptions.Compiled);

    [Fact]
    public void EveryTestClassThatTouchesTheGameplayToneHudChannel_IsInTheSerializedCollection()
    {
        var testRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");
        var usingClasses = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            // This lint quotes the very names it hunts for.
            if (Path.GetFileName(file) == "GameplayToneHudCollectionLintTests.cs") continue;

            scanned++;
            var code = SourceText.StripComments(File.ReadAllText(file));
            if (!TouchesTheChannel.IsMatch(code)) continue;

            foreach (Match m in ClassDeclaration.Matches(code))
                usingClasses.Add(m.Groups["n"].Value);
        }

        Assert.True(scanned > 500, $"only scanned {scanned} test files — the glob is broken.");

        var matched = typeof(GameplayToneHudCollectionLintTests).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => usingClasses.Contains(t.Name))
            .Where(HasTestCases)
            .ToList();

        // Non-vacuity floor: GameplayToneHudTests and RsTonePlaybackCoordinatorTests
        // name the channel today.
        Assert.True(matched.Count >= 2,
            $"only {matched.Count} test classes matched as GameplayToneHud users — the pattern stopped " +
            "matching, so this lint is passing vacuously. Re-check TouchesTheChannel against " +
            "Rendering/GameplayToneHudTests.cs and Services/Audio/Coordination/RsTonePlaybackCoordinatorTests.cs.");

        var offenders = matched
            .Where(t => t.GetCustomAttribute<CollectionAttribute>()?.Name != CollectionName)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            LintReport.Of(
                "A test class that touches the process-wide GameplayToneHud channel must be in " +
                $"[Collection(\"{CollectionName}\")], or it sees the tone names another class applied.",
                offenders,
                $"add [Collection(\"{CollectionName}\")] to the class, or build its coordinator without the HUD adapter."));
    }

    /// <summary>The collection is only worth joining if it actually serializes.</summary>
    [Fact]
    public void TheCollection_DisablesParallelization()
    {
        var definition = typeof(GameplayToneHudCollectionLintTests).Assembly
            .GetTypes()
            .Single(t => t.GetCustomAttribute<CollectionDefinitionAttribute>()?.Name == CollectionName);

        var attribute = definition.GetCustomAttribute<CollectionDefinitionAttribute>()!;

        Assert.True(attribute.DisableParallelization,
            $"[CollectionDefinition(\"{CollectionName}\")] on {definition.Name} dropped " +
            "DisableParallelization — the tone-HUD channel is unguarded.");
    }

    private static bool HasTestCases(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttribute<FactAttribute>() != null ||
                      m.GetCustomAttribute<TheoryAttribute>() != null);
}
