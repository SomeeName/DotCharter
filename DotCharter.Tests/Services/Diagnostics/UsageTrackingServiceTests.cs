using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Diagnostics;

public sealed class UsageTrackingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-usage-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, UsageStatistics.FileName);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void AnUnreadableHistoryIsNotReplacedAfterTheLockIsReleased()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows file sharing is required.");
        Directory.CreateDirectory(_dir);
        var history = new UsageStatistics();
        history.Record(UsageKind.Command, "old", 10);
        File.WriteAllText(StorePath, history.ToJson());
        var before = File.ReadAllBytes(StorePath);

        UsageTrackingService service;
        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
            service = new UsageTrackingService(StorePath, clock: () => 20);

        using (service)
        {
            service.Record(UsageKind.Command, "new");
            service.Flush();
            Assert.Equal(1, service.Snapshot.CountOf(UsageKind.Command, "new"));
        }

        Assert.Equal(before, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public void ACorruptHistoryIsQuarantinedBeforeNewCountersAreWritten()
    {
        Directory.CreateDirectory(_dir);
        const string corrupt = "{ broken history";
        File.WriteAllText(StorePath, corrupt);
        using var service = new UsageTrackingService(StorePath, clock: () => 20);
        service.Record(UsageKind.Command, "new");
        service.Flush();

        Assert.Equal(corrupt, File.ReadAllText(Assert.Single(Directory.GetFiles(_dir, "*.corrupt-*"))));
        Assert.Equal(1, UsageStatistics.FromJson(File.ReadAllText(StorePath)).CountOf(UsageKind.Command, "new"));
    }

    [Fact]
    public void AFailedFlushIsRetriedWithoutAnotherRecord()
    {
        using var service = new UsageTrackingService(StorePath, clock: () => 20);
        service.Record(UsageKind.Command, "new");
        Directory.CreateDirectory(StorePath);
        service.Flush();
        Directory.Delete(StorePath);
        service.Flush();

        Assert.Equal(1, UsageStatistics.FromJson(File.ReadAllText(StorePath)).CountOf(UsageKind.Command, "new"));
    }

    [Fact]
    public void AReadableHistoryKeepsOldCountersWhenNewCountersAreFlushed()
    {
        Directory.CreateDirectory(_dir);
        var history = new UsageStatistics();
        history.Record(UsageKind.Command, "old", 10);
        File.WriteAllText(StorePath, history.ToJson());
        using var service = new UsageTrackingService(StorePath, clock: () => 20);
        service.Record(UsageKind.Command, "new");
        service.Flush();

        var saved = UsageStatistics.FromJson(File.ReadAllText(StorePath));
        Assert.Equal(1, saved.CountOf(UsageKind.Command, "old"));
        Assert.Equal(1, saved.CountOf(UsageKind.Command, "new"));
    }
}
