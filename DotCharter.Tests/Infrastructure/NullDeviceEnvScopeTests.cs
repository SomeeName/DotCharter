using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <see cref="NullDeviceEnvScope" /> restores what it found, and it is the only
///     writer of the two variables in the test tree.
/// </summary>
[Collection("NativeEngine")]
public sealed partial class NullDeviceEnvScopeTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("1", null)]
    [InlineData("outer", "outer-manual")]
    public void TheScope_SetsBothSwitches_AndRestoresWhatWasThereBefore(string? outerEnable, string? outerManual)
    {
        var realEnable = Environment.GetEnvironmentVariable(NullDeviceEnvScope.EnableVariable);
        var realManual = Environment.GetEnvironmentVariable(NullDeviceEnvScope.ManualVariable);
        try
        {
            Environment.SetEnvironmentVariable(NullDeviceEnvScope.EnableVariable, outerEnable);
            Environment.SetEnvironmentVariable(NullDeviceEnvScope.ManualVariable, outerManual);

            using (NullDeviceEnvScope.Enable())
            {
                Assert.Equal("1", Environment.GetEnvironmentVariable(NullDeviceEnvScope.EnableVariable));
                // Not this scope's variable: left exactly as it was.
                Assert.Equal(outerManual, Environment.GetEnvironmentVariable(NullDeviceEnvScope.ManualVariable));
            }

            Assert.Equal(outerEnable, Environment.GetEnvironmentVariable(NullDeviceEnvScope.EnableVariable));
            Assert.Equal(outerManual, Environment.GetEnvironmentVariable(NullDeviceEnvScope.ManualVariable));

            var manual = NullDeviceEnvScope.EnableManual();
            Assert.Equal("1", Environment.GetEnvironmentVariable(NullDeviceEnvScope.EnableVariable));
            Assert.Equal("1", Environment.GetEnvironmentVariable(NullDeviceEnvScope.ManualVariable));
            manual.Dispose();
            // A second Dispose (a using over a scope something already ended) writes nothing.
            Environment.SetEnvironmentVariable(NullDeviceEnvScope.ManualVariable, "set-after-the-scope");
            manual.Dispose();

            Assert.Equal(outerEnable, Environment.GetEnvironmentVariable(NullDeviceEnvScope.EnableVariable));
            Assert.Equal("set-after-the-scope", Environment.GetEnvironmentVariable(NullDeviceEnvScope.ManualVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(NullDeviceEnvScope.EnableVariable, realEnable);
            Environment.SetEnvironmentVariable(NullDeviceEnvScope.ManualVariable, realManual);
        }
    }

    /// <summary>
    ///     A fixture that names either variable is on its way to writing it, and
    ///     five of the ones that did lost the value the run started with. The
    ///     files allowed to spell a name are this type, the isolation test that
    ///     proves a per-engine flag leaves both variables alone, and the two lints
    ///     that hunt for the names.
    /// </summary>
    [Fact]
    public void NoOtherTestFile_NamesTheNullDeviceVariables()
    {
        var testRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");
        string[] allowed =
        [
            "NullDeviceEnvScope.cs", "NullDeviceRigHostIsolationTests.cs",
            "NativeEngineCollectionLintTests.cs", "ProductNullDeviceEnvLintTests.cs",
        ];
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(testRoot, file).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)) continue;
            scanned++;
            if (allowed.Contains(Path.GetFileName(file))) continue;
            var code = SourceText.StripCommentsKeepingLines(File.ReadAllText(file));
            foreach (Match match in VariableName().Matches(code))
                offenders.Add($"DotCharter.Tests/{relative}:{SourceText.LineAt(code, match.Index)}: {match.Value}");
        }

        Assert.True(scanned > 500, $"only {scanned} test files scanned: the sweep is looking in the wrong place.");
        Assert.True(offenders.Count == 0, LintReport.Of(
            "a test names a null-device variable itself; a write that does not restore the previous value changes every later class",
            offenders,
            "open a NullDeviceEnvScope (Enable / EnableManual) in a using or in the class's constructor, and dispose it",
            "DotCharter.Tests/AGENTS.md"));
    }

    [GeneratedRegex("DCA_" + "(?:ENABLE_NULL_DEVICE|NULL_DEVICE_MANUAL)")]
    private static partial Regex VariableName();
}
