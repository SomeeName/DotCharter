using System.Globalization;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Diagnostic output that belongs to ONE test-host process. The gate runs several shards at
///     once and the merge train runs gates side by side, so a fixed file name is overwritten,
///     interleaved by appends, or left stale from an earlier run. Every invocation writes under
///     its own folder, named by its start time and its process id (the shard), created before
///     anything is written.
/// </summary>
internal static class TestArtifactRun
{
    private static readonly string Started = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>This process's folder name: start time, then process id.</summary>
    public static string Name { get; } = For(Started, Environment.ProcessId);

    /// <summary>The pure half of <see cref="Name" />.</summary>
    public static string For(string started, int processId) =>
        string.Create(CultureInfo.InvariantCulture, $"run-{started}-pid{processId}");

    /// <summary><paramref name="root" />/<see cref="Name" />, created (with its parents) when absent.</summary>
    public static string Directory(string root)
    {
        var dir = Path.Combine(root, Name);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }
}
