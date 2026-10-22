using System.Reflection;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every test class that moves the process working directory or the
///     <c>WritablePaths</c> roots joins the serialized <c>CwdSerialized</c> collection.
///     <para>
///         Both are single process-wide values: <c>Directory.SetCurrentDirectory</c>
///         has no per-thread form, and <c>WritablePaths.OverrideRootsForTesting</c>
///         writes one static pair that every config, log and backup path in the
///         product resolves through. Two classes that each point them at their own
///         temp folder overwrite one another mid-test, and any other class that
///         resolves a config path meanwhile lands in the wrong folder. The
///         collection runs after the parallel phase, so its members never overlap
///         the rest of the gate.
///     </para>
///     <para>
///         The collection holds ONLY the classes that move that state. A class
///         that builds its own temp folder with a GUID and passes explicit paths
///         (the Rocksmith XML and RSCP round-trip classes) touches neither value and
///         runs in parallel. Source scan for the USE (comments stripped), reflection
///         for the ATTRIBUTE, so a partial type that carries the attribute on one
///         part and moves the state in another is judged once, as the type.
///     </para>
/// </summary>
public sealed class CwdCollectionLintTests
{
    private const string CollectionName = "CwdSerialized";

    /// <summary>
    ///     Moving the working directory, or claiming or overriding the roots
    ///     <c>WritablePaths</c> hands out. The headless fixture does the second in its own
    ///     process and has no test cases, so the <c>HasTestCases</c> filter drops it.
    /// </summary>
    private static readonly Regex MovesProcessPaths = new(
        @"Directory\s*\.\s*SetCurrentDirectory\s*\(|Environment\s*\.\s*CurrentDirectory\s*=[^=]|" +
        @"WritablePaths\s*\.\s*(OverrideRootsForTesting|TryClaimRootsForTesting)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+(?<n>[A-Za-z_]\w*)", RegexOptions.Compiled);

    [Fact]
    public void EveryTestClassThatMovesTheCwdOrTheWritableRoots_IsInTheSerializedCollection()
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

            // This lint quotes the very calls it hunts for.
            if (Path.GetFileName(file) == "CwdCollectionLintTests.cs") continue;

            scanned++;
            var code = SourceText.StripComments(File.ReadAllText(file));
            if (!MovesProcessPaths.IsMatch(code)) continue;

            foreach (Match m in ClassDeclaration.Matches(code))
                usingClasses.Add(m.Groups["n"].Value);
        }

        Assert.True(scanned > 500, $"only scanned {scanned} test files — the glob is broken.");

        var matched = typeof(CwdCollectionLintTests).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => usingClasses.Contains(t.Name))
            .Where(HasTestCases)
            .ToList();

        // Non-vacuity floor: eight classes move these values today (the config
        // round-trip classes, WritablePathsTests, LibrarySongIdentityMigrationTests).
        Assert.True(matched.Count >= 6,
            $"only {matched.Count} test classes matched as CWD or WritablePaths movers — the pattern stopped " +
            "matching, so this lint is passing vacuously. Re-check MovesProcessPaths against " +
            "Services/Configuration/*Tests.cs.");

        var offenders = matched
            .Where(t => t.GetCustomAttribute<CollectionAttribute>()?.Name != CollectionName)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            LintReport.Of(
                "A test class that moves the process working directory or the WritablePaths roots must be in " +
                $"[Collection(\"{CollectionName}\")], or it races every other class that resolves a config path.",
                offenders,
                $"add [Collection(\"{CollectionName}\")] to the class, or pass explicit paths instead of moving the process values."));
    }

    /// <summary>The collection is only worth joining if it actually serializes.</summary>
    [Fact]
    public void TheCollection_DisablesParallelization()
    {
        var definition = typeof(CwdCollectionLintTests).Assembly
            .GetTypes()
            .Single(t => t.GetCustomAttribute<CollectionDefinitionAttribute>()?.Name == CollectionName);

        var attribute = definition.GetCustomAttribute<CollectionDefinitionAttribute>()!;

        Assert.True(attribute.DisableParallelization,
            $"[CollectionDefinition(\"{CollectionName}\")] on {definition.Name} dropped " +
            "DisableParallelization — the process working directory and the WritablePaths roots are unguarded.");
    }

    private static bool HasTestCases(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttribute<FactAttribute>() != null ||
                      m.GetCustomAttribute<TheoryAttribute>() != null);
}
