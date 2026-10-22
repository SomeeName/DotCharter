using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Session;

/// <summary>
/// #318: every auto-backup added one more <c>&lt;name&gt;_&lt;stamp&gt;.rscp.zip</c> and
/// nothing deleted one, 12 an hour per actively edited project, forever. After a
/// successful write the project's own oldest zips beyond the keep count go; nothing
/// else in the folder is ever touched.
/// </summary>
public sealed class AutoBackupPruneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-prune-" + Guid.NewGuid().ToString("N"));

    public AutoBackupPruneTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private string[] Names() => Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void KeepsTheNewestN_AndDeletesTheRestOfThisProjectsBackups()
    {
        for (var minute = 0; minute < 8; minute++) Touch($"song_20261007_10{minute:00}00.rscp.zip");

        var deleted = ProjectAutoBackup.PruneBackups(_dir, "song", keep: 5);

        Assert.Equal(3, deleted);
        Assert.Equal(
            [
                "song_20261007_100300.rscp.zip", "song_20261007_100400.rscp.zip", "song_20261007_100500.rscp.zip",
                "song_20261007_100600.rscp.zip", "song_20261007_100700.rscp.zip"
            ],
            Names());
    }

    [Fact]
    public void BackupsNamedForTheDcprojAndTheOlderRscp_ArePrunedAsOneHistory()
    {
        for (var minute = 0; minute < 3; minute++) Touch($"song_20261007_10{minute:00}00.rscp.zip");
        for (var minute = 3; minute < 6; minute++) Touch($"song_20261007_10{minute:00}00.dcproj.zip");

        var deleted = ProjectAutoBackup.PruneBackups(_dir, "song", keep: 4);

        Assert.Equal(2, deleted);
        Assert.Equal(
            [
                "song_20261007_100200.rscp.zip", "song_20261007_100300.dcproj.zip",
                "song_20261007_100400.dcproj.zip", "song_20261007_100500.dcproj.zip"
            ],
            Names());
    }

    [Fact]
    public void AnotherProjectsBackups_AndForeignFiles_AreNeverTouched()
    {
        for (var minute = 0; minute < 4; minute++) Touch($"song_20261007_10{minute:00}00.rscp.zip");
        Touch("song_live_20261001_090000.rscp.zip");
        Touch("song_20261001_090000.rscp.zip.bak");
        Touch("notes.txt");
        Touch("song_manual-copy.rscp.zip");

        ProjectAutoBackup.PruneBackups(_dir, "song", keep: 1);

        Assert.Contains("song_live_20261001_090000.rscp.zip", Names());
        Assert.Contains("song_20261001_090000.rscp.zip.bak", Names());
        Assert.Contains("notes.txt", Names());
        Assert.Contains("song_manual-copy.rscp.zip", Names());
        Assert.Contains("song_20261007_100300.rscp.zip", Names());
        Assert.Equal(5, Names().Length);
    }

    [Fact]
    public void ZeroKeepsEverything()
    {
        for (var minute = 0; minute < 6; minute++) Touch($"song_20261007_10{minute:00}00.rscp.zip");

        Assert.Equal(0, ProjectAutoBackup.PruneBackups(_dir, "song", keep: 0));
        Assert.Equal(6, Names().Length);
    }

    [Fact]
    public void SameSecondSuffixes_AreOrderedNewestLast()
    {
        Touch("song_20261007_100000.rscp.zip");
        Touch("song_20261007_100000_1.rscp.zip");
        Touch("song_20261007_100000_2.rscp.zip");

        ProjectAutoBackup.PruneBackups(_dir, "song", keep: 1);

        Assert.Equal(["song_20261007_100000_2.rscp.zip"], Names());
    }
}
