using System.Text.Json;
using DotCharter.Services.Export;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
/// A startup reload and a user open two seconds apart ran two tidies on the same
/// folder at once; both recycled the legacy stamp and the loser logged the shell's
/// FileNotFound as a failed tidy.
/// </summary>
public sealed class ProjectFolderTidyConcurrencyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tidy-race-").FullName;

    [Fact]
    public void Two_opens_of_the_same_project_tidy_the_legacy_stamp_once_without_a_failure()
    {
        File.WriteAllText(Path.Combine(_root, "project.dcproj"), "project");
        var stamp = Path.Combine(_root, CompanionOwnership.LegacyStampFileName);
        File.WriteAllText(stamp, JsonSerializer.Serialize(new { Version = 1, Files = new Dictionary<string, string> { ["project.eof"] = "hash" } }));
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var project = Path.Combine(_root, "project.dcproj");
        var first = new FakeAppLogger();
        var second = new FakeAppLogger();
        var recycled = 0;
        Thread? racer = null;

        // Shell stand-in: an item another caller already took fails the way
        // IFileOperation does (0x80070002 wrapped by RecycleBin.Execute).
        void Shell(string path)
        {
            try { File.Move(path, Path.Combine(bin, Path.GetFileName(path))); }
            catch (FileNotFoundException ex)
            {
                throw new IOException("The Recycle Bin could not accept the item; it was left in place.", ex);
            }
            Interlocked.Increment(ref recycled);
        }

        // The first open is inside its recycle when the second open starts; it goes on
        // only once the second has finished or is SEEN parked waiting for the first.
        void FirstRecycle(string path)
        {
            if (racer == null)
            {
                racer = new Thread(() => ProjectFolderTidy.Run(project, new ChartProject(), second, Shell)) { IsBackground = true };
                racer.Start();
                var deadline = DateTime.UtcNow + BoundedWait.DeadlockCap;
                while (racer.IsAlive && (racer.ThreadState & ThreadState.WaitSleepJoin) == 0)
                {
                    Assert.True(DateTime.UtcNow < deadline, "The second open neither finished nor parked");
                    Thread.Yield();
                }
            }
            Shell(path);
        }

        ProjectFolderTidy.Run(project, new ChartProject(), first, FirstRecycle);
        Assert.True(racer!.Join(BoundedWait.DeadlockCap), "The second open never finished");

        Assert.False(File.Exists(stamp));
        Assert.Equal(1, recycled);
        Assert.DoesNotContain(first.Snapshot().Concat(second.Snapshot()), entry => entry.Level == "WARN");
    }

    public void Dispose() => Directory.Delete(_root, true);
}
