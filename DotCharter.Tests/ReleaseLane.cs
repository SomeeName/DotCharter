using System.Text.Json;

namespace DotCharter.Tests;

/// <summary>One class of the release lane: its <c>FullName</c> (a nested class joins with <c>+</c>),
/// the measured seconds that balance the lane's shards, and why it is not a per-change check.</summary>
internal sealed record ReleaseLaneEntry(string Class, double Seconds, string Why);

/// <summary>
/// The release lane's declared list, <c>tools/ci/release-lane.json</c>: the heaviest
/// corpus / integration / determinism classes, which the default gate (and so a plain
/// <c>dotnet test</c>) leaves out and which <c>tools/agent/Invoke-Gate.ps1 -ReleaseLane</c>
/// runs, always, in the merge train, the release preflight and CI. The file is the ONE list:
/// <c>tools/ci/GateShards.psm1</c> derives both lanes' filters from it, and
/// <c>Infrastructure/ReleaseLaneLintTests</c> proves every class runs in exactly one lane.
/// </summary>
internal static class ReleaseLane
{
    public static string ListPath() => Path.Combine(TestPaths.RepoRoot(), "tools", "ci", "release-lane.json");

    public static IReadOnlyList<ReleaseLaneEntry> Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return [.. doc.RootElement.GetProperty("classes").EnumerateObject()
            .Select(p => new ReleaseLaneEntry(p.Name, p.Value.GetProperty("seconds").GetDouble(), p.Value.GetProperty("why").GetString() ?? ""))
            .OrderBy(e => e.Class, StringComparer.Ordinal)];
    }

    /// <summary>The list in the repository, <c>tools/ci/release-lane.json</c>.</summary>
    public static IReadOnlyList<ReleaseLaneEntry> Read() => Read(ListPath());

    /// <summary>
    /// One <c>--filter-not-class</c> pair per release class, for a plain <c>dotnet test</c>. A list
    /// that cannot be read leaves NOTHING out: the plain run then does more, never less.
    /// </summary>
    public static string[] ExclusionArgs(string? path = null)
    {
        try
        {
            return [.. Read(path ?? ListPath()).SelectMany(e => new[] { "--filter-not-class", e.Class })];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
