using System.Reflection;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every test class that opens a REAL native JUCE engine, or that writes the
///     process-global null-device guards, must sit in the serialized
///     <c>NativeEngine</c> collection.
///     <para>
///         The direct analogue of <see cref="HeadlessCollectionLintTests" />,
///         which does this for Avalonia's process-global dispatcher. The state
///         here is <c>DCA_ENABLE_NULL_DEVICE</c> / <c>DCA_NULL_DEVICE_MANUAL</c>:
///         the native side reads them at engine start (<c>GetEnvironmentVariable</c>,
///         not the CRT's cached <c>getenv</c>), so they have to be live before
///         construction — and an environment variable belongs to the PROCESS.
///         <c>NamAmpFaceNativeTests</c> flipped both with no collection at all
///         while eight classes built real engines in parallel with it, so a
///         sibling could silently be handed the null device, or lose it
///         mid-open. That failure is non-deterministic and looks exactly like
///         the "some JuceSharp tests are environment-flaky" note every guide
///         here already carries.
///     </para>
///     <para>
///         Source scan for the USE (comments stripped, so a <c>see cref</c> or a
///         war story in a doc comment cannot count), reflection for the
///         ATTRIBUTE — a partial type carries the attribute on one part while
///         another part does the engine construction, so neither half can do
///         this alone. Same split, and for the same reason, as the headless
///         lint.
///     </para>
/// </summary>
public sealed class NativeEngineCollectionLintTests
{
    private const string CollectionName = "NativeEngine";

    /// <summary>
    ///     Opening a real engine, or arming the guards the engine reads when it
    ///     opens. <c>NullDeviceRigHost</c> (product code) builds an engine (on the
    ///     null device, through the per-engine <c>EnableNullDevice</c> flag since
    ///     #85), so a test that constructs one never names <c>JuceAudioEngine</c>. <c>JuceDspChainService</c> is deliberately NOT here: it wraps
    ///     the interop and reads the module catalogue out of the DLL, but it
    ///     builds no engine and touches no device.
    /// </summary>
    private static readonly Regex NativeEngineUse = new(
        @"new\s+JuceAudioEngine\s*\(|new\s+NullDeviceRigHost\s*\(|DCA_ENABLE_NULL_DEVICE|DCA_NULL_DEVICE_MANUAL",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+(?<n>[A-Za-z_]\w*)", RegexOptions.Compiled);

    [Fact]
    public void EveryTestClassThatOpensANativeEngine_IsInTheSerializedCollection()
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

            var name = Path.GetFileName(file);

            // This lint, the collection's own declaration and the product-side
            // env-var lint QUOTE the symbols they hunt for rather than using them.
            if (name is "NativeEngineCollectionLintTests.cs" or "NativeEngineCollection.cs"
                or "ProductNullDeviceEnvLintTests.cs")
            {
                continue;
            }

            // `LocalOnly_*` is git-ignored (`.gitignore`), so these files exist
            // only in the owner's checkout and in no worktree, no CI and no
            // agent's tree. Two of them DO set the null-device guards
            // (`LocalOnly_NamCpuProbe`, `LocalOnly_TwinDiversityProbe`) and so
            // do race this collection on the owner's box — but a lint that
            // fails on a file nobody but the owner can even see is a red gate
            // with no fix in the tree. The hazard is recorded in
            // DotCharter.Tests/AGENTS.md instead, where the owner reads it.
            if (name.StartsWith("LocalOnly_", StringComparison.Ordinal)) continue;

            scanned++;
            var code = SourceText.StripComments(File.ReadAllText(file));
            if (!NativeEngineUse.IsMatch(code)) continue;

            foreach (Match m in ClassDeclaration.Matches(code))
                usingClasses.Add(m.Groups["n"].Value);
        }

        Assert.True(scanned > 500, $"only scanned {scanned} test files — the glob is broken.");

        var matched = typeof(NativeEngineCollectionLintTests).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => usingClasses.Contains(t.Name))
            .Where(HasTestCases)
            .ToList();

        // Non-vacuity floor. Eight classes construct a real engine today
        // (DspBranchNativeRoundTrip, DspOfflineProcessing, DspStringParamNativeRoundTrip,
        // DspValueLabelNativeRoundTrip, NamAmpFaceNative, NamCaptureNativeLoad,
        // PerPlayerDspChainNative, ToneLowEndResponse); if this ever reads
        // fewer than six the pattern stopped matching and the lint is passing
        // over nothing.
        Assert.True(matched.Count >= 6,
            $"only {matched.Count} test classes matched as native-engine users — the pattern stopped " +
            "matching, so this lint is passing vacuously. Re-check NativeEngineUse against " +
            "Services/Audio/Dsp/*NativeTests.cs.");

        var offenders = matched
            .Where(t => t.GetCustomAttribute<CollectionAttribute>()?.Name != CollectionName)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"these test classes open a real native engine (or arm the null-device guards it reads at " +
            $"start-up) but are missing [Collection(\"{CollectionName}\")], so xunit runs them in PARALLEL " +
            "with its members. DCA_ENABLE_NULL_DEVICE / DCA_NULL_DEVICE_MANUAL are " +
            "process-wide: a sibling opening an engine mid-scope silently gets the null device, or loses " +
            "it. Add the attribute:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     …and it is ONE collection: one definition, so every member shares one
    ///     lane and no two of them ever overlap. That lane, not
    ///     <c>DisableParallelization</c>, is what serializes the env-var writers
    ///     (see <c>NativeEngineCollection</c> for why the lane no longer waits
    ///     for the parallel phase to finish).
    /// </summary>
    [Fact]
    public void TheNativeEngineCollection_IsDefinedExactlyOnce()
    {
        var definitions = typeof(NativeEngineCollectionLintTests).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttributes<CollectionDefinitionAttribute>().Any(a => a.Name == CollectionName))
            .Select(t => t.Name)
            .ToList();

        Assert.True(definitions.Count == 1,
            $"[CollectionDefinition(\"{CollectionName}\")] should be declared once, found: " +
            string.Join(", ", definitions));
    }

    private static bool HasTestCases(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttribute<FactAttribute>() != null ||
                      m.GetCustomAttribute<TheoryAttribute>() != null);
}
