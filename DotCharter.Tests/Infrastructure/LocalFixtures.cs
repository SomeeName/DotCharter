namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Where the owner-only fixtures are — files that cannot be committed
///     (copyrighted game content, third-party tabs, the local corpus). No test
///     names a machine path: each location comes from an environment variable,
///     the git-ignored <c>TestData/</c> junction or a discoverable install, and
///     is null when absent so the test skips cleanly. The variables are listed
///     in <c>DotCharter.Tests/AGENTS.md</c>; <c>PersonalDataLintTests</c>
///     keeps machine paths out of the tree.
/// </summary>
internal static class LocalFixtures
{
    /// <summary>Env var naming the folder of RS7 (7-string) reference packages.</summary>
    public const string Rs7DirVariable = "DOTCHARTER_RS7_DIR";

    /// <summary>Env var naming a Rocksmith 2014 install root (the folder holding
    /// the base-game package); without it, the Steam install is detected.</summary>
    public const string RocksmithDirVariable = "DOTCHARTER_RS2014_DIR";

    /// <summary>The RS7 reference-package folder, or null when unset.</summary>
    public static string? Rs7Dir() => FromVariable(Rs7DirVariable);

    /// <summary><paramref name="relative" /> under <see cref="Rs7Dir" />; SKIPS the
    /// calling test when the variable is unset or the package is absent.</summary>
    public static string RequireRs7(string relative)
    {
        var root = Rs7Dir();
        Assert.SkipWhen(root == null, $"RS7 reference packages not configured: set {Rs7DirVariable}.");
        var path = Path.Combine(root, relative);
        Assert.SkipUnless(File.Exists(path), $"RS7 package not on this machine: {path}");
        return path;
    }

    /// <summary>The Rocksmith 2014 install root: <see cref="RocksmithDirVariable" />,
    /// else the first install Steam reports; null when neither exists, or when the
    /// real keys are not loaded — every stock package is encrypted (#756,
    /// <see cref="TestRocksmithKeys" />).</summary>
    public static string? RocksmithInstall() =>
        !TestRocksmithKeys.HasRealKeys
            ? null
            : FromVariable(RocksmithDirVariable) ?? TestGameInstall.Directories.FirstOrDefault();

    /// <summary>A path under the git-ignored <c>TestData/</c> corpus, or null when
    /// no <c>TestData/</c> folder sits above the test binaries.</summary>
    public static string? TestData(params string[] parts) =>
        FindTestDataRoot() is { } root ? Path.Combine([root, .. parts]) : null;

    // The nearest TestData/ above the test binaries. Its own copy rather than a test class's,
    // so the plugin's tests can link this file alone.
    private static string? FindTestDataRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? FromVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
