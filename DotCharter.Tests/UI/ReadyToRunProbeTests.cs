using DotCharter.UI.Diagnostics;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pins <see cref="ReadyToRunProbe" />, the thing that labels every startup
///     measurement with whether the build it came from was precompiled.
/// </summary>
/// <remarks>
///     The label matters because the two builds are far apart: published-R2R
///     against plain <c>dotnet build -c Release</c>, same machine and same code,
///     measured window ctor 549 → 222 ms and DataContext bind 774 → 298 ms. An
///     unlabelled phase line from a dev build reads as a product regression, and
///     the "fix" is then to move real work around for no gain.
/// </remarks>
public sealed class ReadyToRunProbeTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(72, true)]
    public void The_native_header_directory_size_is_the_whole_decision(int size, bool expected) =>
        Assert.Equal(expected, ReadyToRunProbe.IsPrecompiledHeader(size));

    /// <summary>
    ///     The positive integration case, and the only one available that is
    ///     stable everywhere: every shipped .NET runtime crossgens its own
    ///     <c>System.Private.CoreLib</c>. If the probe cannot see ReadyToRun in
    ///     THAT, it cannot see it anywhere.
    /// </summary>
    [Fact]
    public void The_shipped_runtime_reads_as_precompiled()
    {
        var coreLib = typeof(object).Assembly.Location;
        Assert.SkipWhen(string.IsNullOrEmpty(coreLib),
            "Single-file/bundled host — System.Private.CoreLib has no file path to read.");

        Assert.True(ReadyToRunProbe.IsPrecompiled(coreLib),
            $"'{coreLib}' should be a ReadyToRun image in every shipped .NET runtime.");
    }

    [Fact]
    public void A_missing_file_is_unknown_rather_than_a_throw()
    {
        Assert.Null(ReadyToRunProbe.IsPrecompiled(
            Path.Combine(Path.GetTempPath(), $"dotcharter-no-such-{Guid.NewGuid():N}.dll")));
        Assert.Null(ReadyToRunProbe.IsPrecompiled(null));
        Assert.Null(ReadyToRunProbe.IsPrecompiled(""));
    }

    [Fact]
    public void A_file_that_is_not_a_managed_assembly_is_unknown_rather_than_a_throw()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-not-a-pe-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "this is not a portable executable");
        try
        {
            Assert.Null(ReadyToRunProbe.IsPrecompiled(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Describe_is_one_of_the_three_documented_values() =>
        Assert.Contains(ReadyToRunProbe.Describe(), (string[])["yes", "no", "?"]);

    /// <summary>
    ///     The label has to reach the machine-readable line, or the whole probe is
    ///     a private curiosity. <c>StartupPerfGateTests</c> parses that line and
    ///     its contract is "extend, don't reformat".
    /// </summary>
    [Fact]
    public void The_startup_phase_line_carries_the_precompiled_flag()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml.cs")));
        var line = src[src.IndexOf("StartupPhases:", StringComparison.Ordinal)..];
        line = line[..line.IndexOf(");", StringComparison.Ordinal)];

        Assert.Contains("r2r=", line, StringComparison.Ordinal);
        Assert.Contains("ReadyToRunProbe.Describe()", line, StringComparison.Ordinal);
    }
}
