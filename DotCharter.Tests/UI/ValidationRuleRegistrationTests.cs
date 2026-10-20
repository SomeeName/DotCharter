namespace DotCharter.Tests.UI;

/// <summary>
///     The validation rule set is registered TWICE and the two must agree.
///     <para>
///         <c>ArrangementValidationService</c> has a logger-only constructor that
///         wires <see cref="ArrangementValidationService.DefaultRules" />, and the
///         app instead registers each rule individually against
///         <c>IArrangementRule</c> in <c>App.axaml.cs</c> so DI injects them as
///         one <c>IEnumerable</c>. Every test in this suite uses the FIRST path;
///         the shipping app uses only the SECOND.
///     </para>
///     <para>
///         So a rule added to <c>DefaultRules()</c> alone passes its own tests,
///         passes the whole gate, and never runs for a user. That is exactly what
///         happened to <c>IndexFingerAnchorRule</c> (E04) and
///         <c>HandShapeContentRule</c> (E05) on 2026-07-27: both verified against
///         the owner's real project by a probe, both invisible in the app, and the
///         owner reported "no issues detected" on a chart carrying seven of them.
///     </para>
///     <para>
///         A source lint, the same shape as
///         <c>KeybindDefaultsTests.EveryKeybindIsRegisteredByTheRegistrar</c> —
///         <c>App.axaml.cs</c> needs the whole app booted to run, and what matters
///         is the SET, not the wiring.
///     </para>
/// </summary>
public sealed class ValidationRuleRegistrationTests
{
    [Fact]
    public void EveryRuleInTheDefaultSetIsAlsoRegisteredForDependencyInjection()
    {
        var app = CompositionRootSource();

        var missing = ArrangementValidationService.DefaultRules()
            .Select(r => r.GetType().Name)
            .Where(name => !app.Contains($"AddSingleton<IArrangementRule, {name}>()", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "These rules run in every TEST (via DefaultRules) but are never registered for DI, so they " +
            "never run in the APP — the user sees 'no issues detected' on a chart that violates them: " +
            string.Join(", ", missing));
    }

    /// <summary>
    ///     …and the other direction: a rule wired into the app but left out of
    ///     <c>DefaultRules()</c> would be exercised by no test at all.
    /// </summary>
    [Fact]
    public void EveryRuleRegisteredForDependencyInjectionIsAlsoInTheDefaultSet()
    {
        var app = CompositionRootSource();
        var registered = System.Text.RegularExpressions.Regex
            .Matches(app, @"AddSingleton<IArrangementRule,\s*(?<name>\w+)>\(\)")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var defaults = ArrangementValidationService.DefaultRules()
            .Select(r => r.GetType().Name)
            .ToHashSet(StringComparer.Ordinal);

        var untested = registered.Where(name => !defaults.Contains(name)).ToList();

        Assert.True(untested.Count == 0,
            "These rules ship in the app but are not in DefaultRules(), so no test in this suite ever " +
            "runs them: " + string.Join(", ", untested));
    }

    /// <summary>
    ///     The set is also complete: an <c>IArrangementRule</c> that exists in the
    ///     assembly and is in neither list is dead code that looks alive.
    /// </summary>
    [Fact]
    public void NoRuleImplementationIsOrphaned()
    {
        var implementations = typeof(ArrangementValidationService).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IArrangementRule).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        var defaults = ArrangementValidationService.DefaultRules()
            .Select(r => r.GetType().Name)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = implementations.Where(name => !defaults.Contains(name)).ToList();

        Assert.True(orphans.Count == 0,
            "These IArrangementRule implementations are in neither the default set nor, therefore, the " +
            "app — they are dead code that reads as a shipped check: " + string.Join(", ", orphans));
    }

    /// <summary>
    ///     The composition root is ONE class spread over several partial files
    ///     (<c>App.axaml.cs</c> plus the topical <c>App.*.cs</c> slices), so a lint
    ///     over the registrations must read all of them — anchored on one file it
    ///     silently stops covering whichever slice a registration moved into.
    /// </summary>
    private static string CompositionRootSource() =>
        string.Concat(UiSourceTree
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));
}
