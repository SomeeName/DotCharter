using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     #85: product code never names the native null-device environment variables
///     <c>DCA_ENABLE_NULL_DEVICE</c> / <c>DCA_NULL_DEVICE_MANUAL</c>. They are
///     PROCESS-wide and every engine reads them at start-up, so writing one around an
///     offline engine's construction (as <c>NullDeviceRigHost</c> did) also registers the
///     "Null" device type in a LIVE engine another thread rebuilds in that window. The
///     per-engine switch is <c>JuceEngineConfig.EnableNullDevice</c> /
///     <c>NullDeviceManualPump</c> (ABI v41, <c>dca_engine_create2</c>).
///     <para>
///         Naming the variable at all is the rule, not just <c>SetEnvironmentVariable</c>:
///         a const declared in one file and written in another passes a call-site scan.
///         Comments are stripped, so a doc comment that explains the history still
///         counts as code-free. Tests and tools keep the env path (it is what the native
///         RT gate and the env-path tests exercise); they are outside this scan.
///     </para>
/// </summary>
public sealed class ProductNullDeviceEnvLintTests
{
    private static readonly Regex EnvName = new(
        @"DCA_ENABLE_NULL_DEVICE|DCA_NULL_DEVICE_MANUAL", RegexOptions.Compiled);

    private static IEnumerable<string> ProductSources()
    {
        var root = TestPaths.RepoRoot();
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            if (Path.GetFileName(project) is "DotCharter.Tests") continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    internal static bool NamesTheEnvVar(string source) => EnvName.IsMatch(SourceText.StripComments(source));

    [Fact]
    public void ProductCode_NeverNamesTheNullDeviceEnvVars()
    {
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var file in ProductSources())
        {
            scanned++;
            if (NamesTheEnvVar(File.ReadAllText(file)))
                offenders.Add(Path.GetRelativePath(TestPaths.RepoRoot(), file));
        }

        Assert.True(scanned > 1000, $"only scanned {scanned} product files — the glob is broken.");
        Assert.True(offenders.Count == 0,
            "product code names the process-wide null-device env vars. Every engine in the process reads " +
            "them, so a write leaks the \"Null\" device type into the live engine (#85). Use " +
            "JuceEngineConfig.EnableNullDevice / NullDeviceManualPump instead:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>Non-vacuity: the shape #85 shipped with is caught, and a comment that
    /// merely mentions the variable is not.</summary>
    [Fact]
    public void ThePattern_CatchesTheShippedShape_AndIgnoresComments()
    {
        Assert.True(NamesTheEnvVar(
            """
            private const string NullDeviceVariable = "DCA_ENABLE_NULL_DEVICE";
            Environment.SetEnvironmentVariable(NullDeviceVariable, "1");
            """));
        Assert.True(NamesTheEnvVar("""Environment.SetEnvironmentVariable("DCA_NULL_DEVICE_MANUAL", "1");"""));
        Assert.False(NamesTheEnvVar(
            """
            /// <summary>Never the process-wide <c>DCA_ENABLE_NULL_DEVICE</c> (#85).</summary>
            // DCA_NULL_DEVICE_MANUAL is a test-only switch.
            var config = new JuceEngineConfig { EnableNullDevice = true };
            """));
    }
}
