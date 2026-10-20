using System.Reflection;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every test class that moves the AMBIENT CULTURE joins one serialized
///     collection, because the culture is process-wide and xunit runs the rest of
///     the assembly beside it.
///     <para>
///         <c>LocalizationService.SetConfiguredCulture</c> writes
///         <c>CultureInfo.DefaultThreadCurrentCulture</c> and
///         <c>DefaultThreadCurrentUICulture</c> — process-wide defaults, not
///         thread state — and then the current thread's culture on top. A test
///         that flips to <c>fr-FR</c> is changing what every OTHER test in the
///         run sees, and a test that restores on a pool thread cannot restore
///         what a continuation carried somewhere else.
///     </para>
///     <para>
///         The symptom is a gate that fails ~17 localization cases perhaps one
///         run in three and passes the same cases in isolation, which reads as
///         "flaky localization tests" and gets re-run rather than fixed.
///         <c>CommunityLanguageTests</c> and <c>RtlSupportTests</c> already
///         carried the collection; the classes mutating culture AROUND them did
///         not, so the serialization only ever covered half the participants.
///     </para>
///     <para>
///         This is a SOURCE lint because the fault lives in method bodies, which
///         reflection cannot see. It globs the test tree rather than naming
///         files, so moving a test does not silently retire its case — the trap
///         <c>AGENTS.md</c> records for path-based lints.
///     </para>
/// </summary>
public sealed class CultureCollectionLintTests
{
    private const string CollectionName = "LocalizationSingleton";
    private const string HeadlessCollectionName = "HeadlessUI";

    /// <summary>
    ///     Writes to ambient culture. Deliberately NOT matching reads
    ///     (<c>CultureInfo.InvariantCulture</c>, <c>StringComparison</c>) — a
    ///     reader is a victim of this race, not a cause, and serializing every
    ///     reader would serialize most of the gate.
    /// </summary>
    /// <remarks>
    ///     The last three alternatives are TRANSITIVE writers — types whose
    ///     product code calls <c>SetConfiguredCulture</c> for you, so the test
    ///     source contains none of the direct tokens above and the lint reported
    ///     zero offenders while four real writers ran unserialized.
    ///     <c>OnboardingWizardRig</c> hands the wizard
    ///     <c>LocalizationService.Instance</c>, and every <c>Vm.Next()</c> from
    ///     the Welcome page commits a culture through it.
    ///     <c>ToolTipHoverPolicy.Apply</c> is the other side of the same
    ///     collision — it installs a process-global
    ///     <c>ToolTip.TipProperty.Changed</c> handler that a culture write
    ///     re-enters from a worker thread; see
    ///     <see cref="DotCharter.Tests.UI.ToolTipHoverPolicyTests" /> for the
    ///     measured failure. <b>Add to this list whenever a product type that
    ///     writes the culture becomes reachable from a test.</b>
    /// </remarks>
    private static readonly Regex CultureWrite = new(
        @"DefaultThreadCurrentCulture\s*=|DefaultThreadCurrentUICulture\s*=|CurrentCulture\s*=|CurrentUICulture\s*=|" +
        @"\.SetConfiguredCulture\(|\.LoadCommunityLanguages\(|" +
        @"new\s+OnboardingWizardRig\(|new\s+OnboardingWizardViewModel\(|new\s+GeneralSettingsViewModel\(|" +
        @"new\s+BackupMigrateViewModel\(|" +
        @"ToolTipHoverPolicy\.Apply\(",
        RegexOptions.Compiled);

    private static readonly Regex Collection = new(
        @"\[Collection\(""(?<n>[^""]+)""\)\]", RegexOptions.Compiled);

    /// <summary>
    ///     Files that move the ambient culture and belong to NO test class, with
    ///     the reason each is safe. A shared fixture cannot carry a
    ///     <c>[Collection]</c> attribute — it is not a test class — so the
    ///     exemption has to be named. The test asserts the file still exists, so
    ///     a rename breaks the entry instead of silently widening it.
    /// </summary>
    private static readonly (string File, string Reason)[] ExemptFiles =
    [
        (Path.Combine("DotCharter.Tests", "Perf", "HeadlessAppFixture.cs"),
            "The headless session fixture. It pins the FORMATTING culture to DOTCHARTER_HEADLESS_CULTURE so " +
            "layout findings do not depend on the developer's Windows region — necessary since " +
            "LocalizationService stopped overwriting CurrentCulture (2026-08-29). It is reachable only from " +
            "the HeadlessUI collection, which runs as its own process (root AGENTS.md § Validation), and " +
            "HeadlessCollectionLintTests is what keeps that true."),
        (Path.Combine("DotCharter.Tests", "Infrastructure", "OnboardingWizardRig.cs"),
            "The wizard rig itself — a shared fixture, not a test class, so it cannot carry the attribute. It " +
            "constructs OnboardingWizardViewModel over LocalizationService.Instance; the CLASSES that drive it " +
            "are what must join the collection, and this lint is what makes them.")
    ];

    [Fact]
    public void EveryTestThatMovesTheAmbientCulture_JoinsTheSerializedCollection()
    {
        var root = TestPaths.RepoRoot();
        var testRoot = Path.Combine(root, "DotCharter.Tests");
        var offenders = new List<string>();
        var scanned = 0;
        var mutators = 0;

        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            // This lint quotes the very strings it hunts for.
            if (Path.GetFileName(file) == "CultureCollectionLintTests.cs") continue;

            scanned++;
            var text = File.ReadAllText(file);
            var code = SourceText.StripComments(text);
            if (!CultureWrite.IsMatch(code)) continue;

            mutators++;
            var relative = Path.GetRelativePath(root, file);
            if (ExemptFiles.Any(e => e.File == relative)) continue;

            // Comments STRIPPED, because a doc comment that merely QUOTES
            // `[Collection("HeadlessUI")]` — this repo has one, in
            // `Perf/HeadlessAppFixture.cs` — used to exempt its whole file by
            // accident. That is the same "a see cref is exactly as inert" trap
            // the reachability lints strip for.
            var declared = Collection.Matches(code).Select(m => m.Groups["n"].Value).ToList();
            if (declared.Contains(CollectionName)) continue;

            // The headless suite is EXEMPT, and only because it runs as its own
            // `dotnet test` invocation (AGENTS.md § Validation): a separate
            // process has its own ambient culture, so it cannot reach the gate's
            // localization tests. It also cannot join this collection even in
            // principle — a class gets ONE, and it needs HeadlessUI for the
            // process-global dispatcher. If the two suites are ever merged back
            // into one testhost, this exemption becomes a live race.
            if (declared.Contains(HeadlessCollectionName)) continue;

            // …and so is a PARTIAL of one. A partial type carries the attribute
            // on ONE part, so a topic file of a `*UiPerfSmokeTests` class shows
            // no collection of its own; keying on the filename is safe because
            // `HeadlessCollectionLintTests` is what guarantees every such class
            // is in HeadlessUI. Without this the transitive patterns above
            // reported `DialogConstructionUiPerfSmokeTests.Onboarding.cs`, which
            // is a part of a class that is already serialized.
            if (Path.GetFileName(file).Contains("UiPerfSmokeTests", StringComparison.Ordinal)) continue;

            offenders.Add($"{Path.GetRelativePath(root, file)} writes the ambient culture but is in " +
                          (declared.Count == 0 ? "no collection" : $"[{string.Join(", ", declared)}]"));
        }

        Assert.True(scanned > 500, $"only scanned {scanned} test files — the glob is broken");
        Assert.True(mutators >= 10, $"only found {mutators} culture-mutating files — the pattern stopped matching");

        foreach (var (file, reason) in ExemptFiles)
        {
            Assert.True(File.Exists(Path.Combine(root, file)),
                $"Exempt file '{file}' no longer exists — delete the entry ({reason}).");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"Exempt file '{file}' carries no reason.");
        }
        Assert.True(offenders.Count == 0,
            "A test that moves the process-wide culture must join the \"" + CollectionName +
            "\" collection, or it races every other test in the run:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The collection is only worth joining if it actually serializes.</summary>
    [Fact]
    public void TheCollection_DisablesParallelization()
    {
        var definition = typeof(CultureCollectionLintTests).Assembly
            .GetTypes()
            .Single(t => t.GetCustomAttribute<CollectionDefinitionAttribute>()?.Name == CollectionName);

        var attribute = definition.GetCustomAttribute<CollectionDefinitionAttribute>()!;

        Assert.True(attribute.DisableParallelization,
            $"[CollectionDefinition(\"{CollectionName}\")] on {definition.Name} no longer disables " +
            "parallelization — every culture test races the rest of the gate again.");
    }
}
