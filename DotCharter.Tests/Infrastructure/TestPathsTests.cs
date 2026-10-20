namespace DotCharter.Tests.Infrastructure;

public sealed class TestPathsTests
{
    [Fact]
    public void RepoRoot_HoldsTheSolutionAndTheTestProject()
    {
        var root = TestPaths.RepoRoot();

        Assert.True(File.Exists(Path.Combine(root, "DotCharter.sln")));
        Assert.True(File.Exists(Path.Combine(root, "DotCharter.Tests", "DotCharter.Tests.csproj")));
    }

    /// <summary>
    ///     A CI build (<c>ContinuousIntegrationBuild=true</c>) maps source paths to
    ///     <c>/_/…</c>, so the compile-time path names no real folder; the root must
    ///     still be found from the test binaries' folder (#54).
    /// </summary>
    [Fact]
    public void RepoRoot_IsFoundFromTheBinaries_WhenTheSourcePathIsADeterministicBuildPath()
    {
        var found = TestPaths.FindRepoRoot("/_/DotCharter.Tests/Infrastructure/TestPaths.cs", AppContext.BaseDirectory);

        Assert.Equal(TestPaths.RepoRoot(), found);
    }

    /// <summary>
    ///     Only <see cref="TestPaths" /> may find the repo from <c>[CallerFilePath]</c>:
    ///     every private copy of that walk-up failed on CI (#54).
    /// </summary>
    [Fact]
    public void NoOtherTestFindsTheRepoFromItsOwnSourcePath()
    {
        var testsRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");
        var offenders = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) is not ("TestPaths.cs" or "TestPathsTests.cs"))
            .Where(f => SourceText.StripComments(File.ReadAllText(f)).Contains("CallerFilePath", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(testsRoot, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These tests find files from [CallerFilePath], which is /_/… on CI; use TestPaths.RepoRoot(): " +
            string.Join(", ", offenders));
    }
}
