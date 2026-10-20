using System.Reflection;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every <c>*UiPerfSmokeTests</c> class must sit in the serialized
///     <c>HeadlessUI</c> collection.
///     <para>
///         <c>Avalonia.Headless</c>'s dispatcher is PROCESS-GLOBAL and
///         <c>HeadlessUiCollection</c> is the only thing serializing access to
///         it. A class that forgets <c>[Collection("HeadlessUI")]</c> is its own
///         collection, so xunit runs it IN PARALLEL with that one — and the
///         moment two of them dispatch at once the session is poisoned for the
///         whole run: <c>InvalidProgramException: You've caused dispatcher
///         loop</c> out of <c>Dispatcher.ResetForUnitTests()</c>, then
///         <c>InvalidOperationException: The calling thread cannot access this
///         object</c> for everything after it.
///     </para>
///     <para>
///         Four classes had been written that way by 2026-08-23
///         (<c>EditorPaneConstruction…</c>, <c>InspectorConstruction…</c>,
///         <c>SettingsConstruction…</c>, <c>ChordTemplateEditorConstruction…</c>)
///         and every one of them read as an intermittently flaky suite rather
///         than as a missing attribute. The suite is outside the default gate,
///         so nothing else was ever going to report it — but THIS lint is a
///         source/reflection check with no app session, so it runs in the gate.
///     </para>
///     <para>
///         Reflection, not a text scan, on purpose: a partial type carries the
///         attribute on ONE part, and grepping files would flag the other seven
///         parts of <c>PlayModeScreensUiPerfSmokeTests</c> forever.
///     </para>
/// </summary>
public sealed class HeadlessCollectionLintTests
{
    private const string CollectionName = "HeadlessUI";

    [Fact]
    public void EveryUiPerfSmokeTestClass_IsInTheSerializedHeadlessCollection()
    {
        var offenders = typeof(HeadlessCollectionLintTests).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.Name.EndsWith("UiPerfSmokeTests", StringComparison.Ordinal))
            .Where(t => t.GetCustomAttribute<CollectionAttribute>()?.Name != CollectionName)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"these *UiPerfSmokeTests classes are missing [Collection(\"{CollectionName}\")], so xunit will " +
            "run them in PARALLEL with the serialized headless collection and poison the shared Avalonia " +
            "session for the whole run:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The NAME is a proxy; USING THE SESSION is the actual hazard. This is
    ///     the same rule keyed on the real thing.
    ///     <para>
    ///         A class that boots the headless session and is not called
    ///         <c>*UiPerfSmokeTests</c> is invisible to the fact above, runs in
    ///         the DEFAULT gate, and races the serialized collection on the
    ///         process-global dispatcher. Two classes hit exactly that and were
    ///         fixed by RENAMING (<c>EscapeKeyRoutingUiPerfSmokeTests</c>,
    ///         <c>PlayPauseSpaceKeyRoutingUiPerfSmokeTests</c>, both carrying a
    ///         comment explaining the rename) — which is a fix that depends on
    ///         somebody remembering the convention. This fact does not.
    ///     </para>
    ///     <para>
    ///         <b>Comments are stripped first, and that is the whole point.</b>
    ///         <c>DotCharter.Tests/AGENTS.md</c> asserted for weeks that seven
    ///         named classes "all call <c>HeadlessSessionFixture.Get()</c>"
    ///         without the attribute. They do not: every one of the seven is
    ///         pure or parses XAML as XML, names no Avalonia type at all, and
    ///         mentions the fixture only in a doc comment saying why it AVOIDS
    ///         it. The claim came from a grep that counted prose — the same
    ///         mistake the reachability lints strip comments to avoid — and
    ///         acting on it would have been a REGRESSION: giving those seven the
    ///         attribute puts members of the <c>HeadlessUI</c> collection in the
    ///         default gate, which drags <c>HeadlessSessionLifecycle</c>'s
    ///         teardown in with them, and <c>HeadlessSessionFixture
    ///         .ShutdownAsync</c> ends with an UNCONDITIONAL
    ///         <c>WritablePaths.OverrideRootsForTesting(null, null)</c> that is
    ///         only safe because that collection has no members there.
    ///     </para>
    ///     <para>
    ///         Source scan for the USE, reflection for the ATTRIBUTE: a partial
    ///         type carries the attribute on one part while another part does
    ///         the dispatching (all seven <c>DialogConstructionUiPerfSmokeTests</c>
    ///         topic files use the session; only the root declares the
    ///         attribute), so neither half can do this alone.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryTestClassThatBootsTheHeadlessSession_IsInTheSerializedCollection()
    {
        var root = TestPaths.RepoRoot();
        var testRoot = Path.Combine(root, "DotCharter.Tests");
        var usingClasses = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            // This lint quotes the very symbols it hunts for, and the fixture
            // file DECLARES them rather than consuming them.
            if (name is "HeadlessCollectionLintTests.cs" or "HeadlessAppFixture.cs") continue;

            scanned++;
            var code = SourceText.StripComments(File.ReadAllText(file));
            if (!SessionUse.IsMatch(code)) continue;

            foreach (Match m in ClassDeclaration.Matches(code))
                usingClasses.Add(m.Groups["n"].Value);
        }

        var offenders = typeof(HeadlessCollectionLintTests).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => usingClasses.Contains(t.Name))
            // Only classes xunit will actually SCHEDULE — a helper stub declared
            // beside a test cannot race anything.
            .Where(HasTestCases)
            .Where(t => t.GetCustomAttribute<CollectionAttribute>()?.Name != CollectionName)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(scanned > 500, $"only scanned {scanned} test files — the glob is broken");

        var covered = typeof(HeadlessCollectionLintTests).Assembly
            .GetTypes()
            .Count(t => usingClasses.Contains(t.Name) && HasTestCases(t));
        Assert.True(covered >= 30,
            $"only {covered} test classes matched as headless-session users — the pattern stopped matching, " +
            "so this lint is passing vacuously. Re-check SessionUse against Perf/HeadlessAppFixture.cs.");

        Assert.True(offenders.Count == 0,
            $"these test classes boot the headless session but are missing [Collection(\"{CollectionName}\")], " +
            "so xunit runs them in PARALLEL with the serialized collection and poisons the process-global " +
            "Avalonia dispatcher for the whole run. Either add the attribute (and accept that the collection " +
            "then has members in the default gate — see HeadlessSessionFixture.ShutdownAsync) or rename the " +
            "class to *UiPerfSmokeTests so it moves to the separate suite:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     …and the collection it names must actually be the serialized one. If
    ///     <c>DisableParallelization</c> is ever dropped from
    ///     <c>HeadlessUiCollection</c>, the attribute above becomes decoration
    ///     and every class starts racing every other one instead.
    /// </summary>
    [Fact]
    public void TheHeadlessCollection_DisablesParallelization()
    {
        var definition = typeof(HeadlessCollectionLintTests).Assembly
            .GetTypes()
            .Single(t => t.GetCustomAttribute<CollectionDefinitionAttribute>()?.Name == CollectionName);

        var attribute = definition.GetCustomAttribute<CollectionDefinitionAttribute>()!;

        Assert.True(attribute.DisableParallelization,
            $"[CollectionDefinition(\"{CollectionName}\")] on {definition.Name} no longer disables " +
            "parallelization — the headless session's process-global dispatcher is now unguarded.");
    }

    /// <summary>
    ///     Reaching the process-global dispatcher. <c>HeadlessSessionFixture.Get()</c>
    ///     is how every test in this repo does it; the two type names are the
    ///     ways to start a session WITHOUT the fixture, which would be worse.
    /// </summary>
    /// <remarks>
    ///     <c>Dispatch</c> is in here as well as <c>Get</c> because the draining
    ///     wrapper (<c>HeadlessSessionFixture.Dispatch</c>) replaced ~150
    ///     <c>Get().Dispatch(…)</c> call sites — and this lint's own
    ///     <c>covered &gt;= 30</c> floor went red the moment they did, which is
    ///     exactly what that floor is for. Both spellings reach the
    ///     process-global dispatcher.
    /// </remarks>
    private static readonly Regex SessionUse = new(
        @"HeadlessSessionFixture\s*\.\s*(Get|Dispatch)\s*\(|\bHeadlessUnitTestSession\b|\bHeadlessAppHelper\b",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+(?<n>[A-Za-z_]\w*)", RegexOptions.Compiled);

    private static bool HasTestCases(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttribute<FactAttribute>() != null ||
                      m.GetCustomAttribute<TheoryAttribute>() != null);
}
