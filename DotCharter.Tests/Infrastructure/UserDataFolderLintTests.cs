using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     #439: product code reaches <c>%APPDATA%</c> / <c>%LOCALAPPDATA%</c> ONLY through
///     <c>UserDataFolders</c>. <c>Environment.GetFolderPath</c> ignores environment
///     variables, so a direct call is a path that <c>DOTCHARTER_DATA_ROOT</c> cannot
///     redirect — and a test or measurement launch then writes, renames or deletes
///     the user's real data (on 2026-10-08 that cost the owner two stem archives).
///     <para>
///         The scan covers every <c>DotCharter.*</c> project except the tests, with
///         comments stripped: a doc comment that NAMES <c>%LOCALAPPDATA%</c> is
///         fine, a <c>SpecialFolder.LocalApplicationData</c> in code is not, nor is
///         reading the <c>APPDATA</c> / <c>LOCALAPPDATA</c> variables or expanding
///         them. The resolver itself is the one exemption, and the lint fails if it
///         stops matching (a stale exemption).
///     </para>
/// </summary>
public sealed class UserDataFolderLintTests
{
    private const string Resolver = "DotCharter.Tone/Infrastructure/Storage/UserDataFolders.cs";

    private static readonly Regex DirectUserFolder = new(
        @"SpecialFolder\s*\.\s*(?:Local)?ApplicationData\b" +
        @"|GetEnvironmentVariable\s*\(\s*""(?:LOCAL)?APPDATA""" +
        @"|ExpandEnvironmentVariables\s*\([^;]*%(?:LOCAL)?APPDATA%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static bool ReachesAUserFolderDirectly(string source) =>
        DirectUserFolder.IsMatch(SourceText.StripComments(source));

    internal static IEnumerable<string> ProductSources()
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

    internal static string Relative(string file) =>
        Path.GetRelativePath(TestPaths.RepoRoot(), file).Replace('\\', '/');

    [Fact]
    public void ProductCode_ReachesTheUserFolders_OnlyThroughUserDataFolders()
    {
        var scanned = 0;
        var resolverMatched = false;
        var offenders = new List<string>();
        foreach (var file in ProductSources())
        {
            scanned++;
            if (!ReachesAUserFolderDirectly(File.ReadAllText(file))) continue;
            var relative = Relative(file);
            if (relative == Resolver) resolverMatched = true;
            else offenders.Add(relative);
        }

        Assert.True(scanned > 1000, $"only scanned {scanned} product files — the glob is broken.");
        Assert.True(resolverMatched, $"{Resolver} no longer names the user folders: the exemption is stale.");
        Assert.True(offenders.Count == 0,
            "product code reaches %APPDATA% / %LOCALAPPDATA% directly, which DOTCHARTER_DATA_ROOT cannot " +
            "redirect, so a test launch would touch the user's real data (#439). Use " +
            "UserDataFolders.RoamingAppData / LocalAppData (or OtherProgramsLocalAppData to FIND another " +
            "program's install, read-only):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Non-vacuity: the shapes that shipped before #439 are caught, and a
    /// comment or a user-facing message that merely names the folder is not.</summary>
    [Fact]
    public void ThePattern_CatchesDirectCalls_AndIgnoresCommentsAndMessages()
    {
        Assert.True(ReachesAUserFolderDirectly(
            """
            private static string DefaultRoot() => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotCharter", "x");
            """));
        Assert.True(ReachesAUserFolderDirectly(
            "var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);"));
        Assert.True(ReachesAUserFolderDirectly("""var r = GetFolderPath(SpecialFolder . ApplicationData);"""));
        Assert.True(ReachesAUserFolderDirectly("""var l = Environment.GetEnvironmentVariable("LOCALAPPDATA");"""));
        Assert.True(ReachesAUserFolderDirectly(
            """var p = Environment.ExpandEnvironmentVariables(@"%APPDATA%\DotCharter");"""));
        Assert.False(ReachesAUserFolderDirectly(
            """
            /// <summary>Lives in <c>%LOCALAPPDATA%\DotCharter</c> (SpecialFolder.LocalApplicationData).</summary>
            // Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is banned here.
            var root = UserDataFolders.LocalAppData;
            throw new InvalidOperationException("Put the model in %APPDATA%/DotCharter/models/.");
            """));
    }
}
