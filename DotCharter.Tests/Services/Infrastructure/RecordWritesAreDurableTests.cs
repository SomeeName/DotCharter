using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     A small record was written with the exact pattern <c>AtomicFile</c>'s
///     own doc says every hand-rolled copy got wrong: a bare truncating
///     <c>File.WriteAllText</c> of the resumable-update record right before the
///     updater is launched (<c>UpdateInstaller</c>, formerly inside
///     <c>GitHubUpdateService</c>), which leaves a half-written record that
///     makes the staged tree unresumable. (The LAN
///     scoreboard store was the second such record until it was removed on
///     2026-09-19.)
///     <para>
///         Pinned by SOURCE, on purpose: no observer can see a missing fsync
///         until the power goes.
///     </para>
/// </summary>
public sealed class RecordWritesAreDurableTests
{
    [Fact]
    public void TheUpdater_WritesThePendingUpdateRecord_ThroughAtomicFile()
    {
        // The apply half left GitHubUpdateService for UpdateInstaller (#652); read the
        // whole set so a write that moves back cannot slip past.
        var update = Path.Combine(SourceRoot(), "DotCharter.Services", "Update");
        var source = string.Concat(
            File.ReadAllText(Path.Combine(update, "GitHubUpdateService.cs")),
            File.ReadAllText(Path.Combine(update, "UpdateInstaller.cs")));
        var writes = Regex.Matches(source, @"(\w+)\.WriteAllText\(\s*UpdaterScriptBuilder\.PendingStatePathFor");
        Assert.NotEmpty(writes);
        Assert.All(writes, m => Assert.Equal("AtomicFile", m.Groups[1].Value));
    }

    private static string SourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("DotCharter.sln not found above " + AppContext.BaseDirectory);
    }
}
