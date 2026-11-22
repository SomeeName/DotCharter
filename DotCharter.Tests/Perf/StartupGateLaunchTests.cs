using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The isolation of the startup gate's child, checked WITHOUT launching the app:
///     the gate itself is opt-in (<c>DOTCHARTER_PERF_GATE=1</c>), so these are what
///     the default gate holds it to.
/// </summary>
public sealed class StartupGateLaunchTests
{
    [Fact]
    public void TheChild_IsStartedWithADataRootOfItsOwn_NeverTheUsersFolders()
    {
        using var first = new StartupGateLaunch();
        using var second = new StartupGateLaunch();

        var info = first.StartInfo(Path.Combine(Path.GetTempPath(), "bin", "DotCharter.exe"));

        Assert.Equal(first.DataRoot, info.Environment[UserDataFolders.DataRootVariable]);
        Assert.False(info.UseShellExecute, "a shell launch would not carry the environment to the child.");
        Assert.NotEqual(first.DataRoot, second.DataRoot);
        Assert.True(Directory.Exists(first.DataRoot));
        AssertUnder(Path.GetTempPath(), first.DataRoot);
        foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var real = Path.Combine(Environment.GetFolderPath(folder), "DotCharter");
            Assert.False(IsUnder(real, first.DataRoot), $"the child's data root '{first.DataRoot}' is inside the user's '{real}'.");
            Assert.False(IsUnder(real, first.LogsDirectory), $"the gate reads logs from the user's '{real}'.");
        }
    }

    [Fact]
    public void TheLogFolder_IsTheOneTheProductResolvesUnderThatDataRoot()
    {
        using var launch = new StartupGateLaunch();

        AssertUnder(launch.DataRoot, launch.LogsDirectory);
        using (UserDataFolders.UseDataRootForTesting(launch.DataRoot))
            Assert.Equal(WritablePaths.RedirectedLogRoot(), launch.LogsDirectory);
    }

    [Fact]
    public void OnlyThisChildsLog_IsRead_AnotherInstancesStartupLineIsNot()
    {
        using var mine = new StartupGateLaunch();
        using var other = new StartupGateLaunch();
        Directory.CreateDirectory(other.LogsDirectory);
        File.WriteAllText(Path.Combine(other.LogsDirectory, "app.log"),
            "2026-01-01 00:00:00.000 [INF] StartupPhases: interactive=1\n");

        // Nothing of mine yet, whatever another instance wrote a moment ago.
        Assert.Null(mine.FindStartupPhasesLine());

        Directory.CreateDirectory(mine.LogsDirectory);
        File.WriteAllText(Path.Combine(mine.LogsDirectory, "app.log"),
            "2026-01-01 00:00:00.000 [INF] booting\n2026-01-01 00:00:01.000 [INF] StartupPhases: host=12 interactive=345\n");

        Assert.Equal("StartupPhases: host=12 interactive=345", mine.FindStartupPhasesLine());
    }

    [Fact]
    public void ALogTheAppStillHasOpen_IsRead()
    {
        using var launch = new StartupGateLaunch();
        Directory.CreateDirectory(launch.LogsDirectory);
        using var held = new FileStream(Path.Combine(launch.LogsDirectory, "app.log"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(held);
        writer.Write("x [INF] StartupPhases: interactive=7\n");
        writer.Flush();

        Assert.Equal("StartupPhases: interactive=7", launch.FindStartupPhasesLine());
    }

    [Fact]
    public void Dispose_RemovesTheChildsDataRoot()
    {
        string root;
        using (var launch = new StartupGateLaunch())
        {
            root = launch.DataRoot;
            Directory.CreateDirectory(launch.LogsDirectory);
            File.WriteAllText(Path.Combine(launch.LogsDirectory, "app.log"), "x");
        }

        Assert.False(Directory.Exists(root));
    }

    /// <summary>The gate's own source starts the app through the launch type and
    /// names no user folder itself.</summary>
    [Fact]
    public void TheGate_ResolvesNoUserFolderItself()
    {
        var source = SourceText.StripComments(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests", "Perf", "StartupPerfGateTests.cs")));

        Assert.DoesNotContain("SpecialFolder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.Contains("new StartupGateLaunch()", source, StringComparison.Ordinal);
    }

    private static void AssertUnder(string parent, string path) =>
        Assert.True(IsUnder(parent, path), $"'{path}' is not under '{parent}'.");

    private static bool IsUnder(string parent, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(path));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
