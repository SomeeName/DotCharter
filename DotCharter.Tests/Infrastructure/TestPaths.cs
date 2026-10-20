using System.Runtime.CompilerServices;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Where the repository is, for tests that read source, docs or tool files.
///     It walks up to the folder holding <c>DotCharter.sln</c>, first from this
///     file's compile-time location, then from the test binaries' folder, so it
///     answers the same in the main checkout, in an agent worktree and on CI.
///     <para>
///         The fallback is not optional: CI builds with
///         <c>ContinuousIntegrationBuild=true</c>, which maps every source path to
///         <c>/_/…</c>, so <c>[CallerFilePath]</c> names no real folder there and
///         every test that walked up from it failed (#54). The binaries always sit
///         under the repo. This is the ONE place a test may use
///         <c>[CallerFilePath]</c> to find the repo (<c>TestPathsTests</c> lints it).
///     </para>
/// </summary>
public static class TestPaths
{
    private static readonly Lazy<string> Root = new(FindRepoRoot);

    public static string RepoRoot() => Root.Value;

    private static string FindRepoRoot() =>
        FindRepoRoot(ThisFile(), AppContext.BaseDirectory)
        ?? throw new InvalidOperationException(
            "Could not locate the repository root (no DotCharter.sln above the test sources or the test binaries).");

    /// <summary>The solution folder above <paramref name="sourceFile" />, else above
    /// <paramref name="baseDirectory" />; null when neither has one.</summary>
    internal static string? FindRepoRoot(string? sourceFile, string baseDirectory) =>
        WalkUp(Path.GetDirectoryName(sourceFile)) ?? WalkUp(baseDirectory);

    private static string? WalkUp(string? dir)
    {
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir;
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
