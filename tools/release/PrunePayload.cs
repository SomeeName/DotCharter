// .NET 10 file-based program. Removes dead weight from a published payload using
// the REAL rules type (ReleasePayloadPrune from DotCharter.Services), so what the
// release ships and what the gate tests pin cannot drift apart.
//
//   prune <publishDir> [--dry-run]
//
// Runs AFTER the publish-layout check and BEFORE hashing/zipping, so the payload
// manifest describes the pruned payload -- the manifest is what the in-app updater
// range-fetches against, and a manifest that lists files the zip does not contain
// would make every update fail to find them.
//
// Exit codes: 0 = ok, 1 = failure, 2 = usage.

#:project ../../DotCharter.Services/DotCharter.Services.csproj

using DotCharter.Services.Update;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: dotnet run PrunePayload.cs <publishDir> [--dry-run]");
    return 2;
}

var publishDir = args[0];
var dryRun = args.Length > 1 && args[1].Equals("--dry-run", StringComparison.OrdinalIgnoreCase);

if (!Directory.Exists(publishDir))
{
    Console.Error.WriteLine($"FAILED: no such directory: {publishDir}");
    return 1;
}

var removedBytes = 0L;
var removedCount = 0;
var byReason = new Dictionary<PruneReason, (int Count, long Bytes)>();

foreach (var path in Directory.EnumerateFiles(publishDir, "*", SearchOption.AllDirectories))
{
    var directory = Path.GetDirectoryName(path)!;
    var reason = ReleasePayloadPrune.Classify(
        Path.GetFileName(path),
        sibling => File.Exists(Path.Combine(directory, sibling)));

    if (reason == PruneReason.Keep) continue;

    var size = new FileInfo(path).Length;
    byReason.TryGetValue(reason, out var tally);
    byReason[reason] = (tally.Count + 1, tally.Bytes + size);
    removedBytes += size;
    removedCount++;

    if (dryRun) continue;

    try
    {
        File.Delete(path);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED: could not delete {path}: {ex.Message}");
        return 1;
    }
}

foreach (var (reason, tally) in byReason.OrderByDescending(e => e.Value.Bytes))
    Console.WriteLine(
        $"    {(dryRun ? "would remove" : "removed")} {tally.Count,3} x {ReleasePayloadPrune.Describe(reason)} " +
        $"({tally.Bytes / 1024.0 / 1024.0:F1} MB on disk)");

Console.WriteLine(
    $"    payload pruned: {removedCount} file(s), {removedBytes / 1024.0 / 1024.0:F1} MB on disk" +
    (dryRun ? " (dry run, nothing deleted)" : ""));

return 0;
