using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>
///     Writes a finished match's feature vectors and per-block breakdown somewhere a
///     human can read them afterwards.
///     <para>
///         <b>A score nobody can inspect is a score nobody can argue with.</b> The
///         distances a match reports are derived from two vectors and two anchors; if
///         none of the four survives the run, "why did this score 40 %" has no answer
///         except re-running the whole search — which, on real material, is minutes
///         and a different library snapshot.
///     </para>
/// </summary>
public interface IMatchResultStore
{
    /// <summary>
    ///     Persists <paramref name="result" />. Returns the file path, or null with
    ///     <paramref name="reason" /> set — <b>never throws</b>, because a match must
    ///     not be lost to its own bookkeeping.
    /// </summary>
    /// <param name="result">The finished result, carrying the target vector.</param>
    /// <param name="extractorId">Which feature space the vectors are in. Two vectors
    /// from different extractors are not comparable, so the file says which.</param>
    /// <param name="stemHint">The instrument the caller declared, or null.</param>
    /// <param name="reason">Why nothing was written, when the answer is null.</param>
    string? TryWrite(ToneMatchResult result, string extractorId, string? stemHint, out string? reason);
}

/// <summary>One candidate as the artefact stores it.</summary>
public sealed record MatchArtifactCandidate(
    string Origin,
    double Distance,
    double RankingDistance,
    double PlausibilityPenalty,
    int? Percent,
    string? UnratedReason,
    IReadOnlyList<string> Chain,
    IReadOnlyList<MatchScoreBlock> Blocks,
    float[]? Features);

/// <summary>
///     The persisted shape. Plain records with a parameterless-friendly serializer
///     shape, versioned, and carrying the extractor id — a vector without the
///     extractor that made it is a list of numbers.
/// </summary>
public sealed record MatchArtifact(
    int Version,
    string ExtractorId,
    string WrittenUtc,
    string? StemHint,
    double SelfDistance,
    double ReferenceDistance,
    double ElapsedSeconds,
    IReadOnlyList<string> Notes,
    float[]? TargetFeatures,
    float[]? ReferenceFeatures,
    IReadOnlyList<MatchArtifactCandidate> Candidates)
{
    /// <summary>Bumped when the shape changes; a reader refuses another version
    /// rather than mapping a different document onto these fields.</summary>
    public const int CurrentVersion = 1;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>Reads an artefact back, or null when the file is absent, unreadable
    /// or another version. Never throws.</summary>
    /// <param name="path">The artefact file.</param>
    public static MatchArtifact? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var artifact = JsonSerializer.Deserialize<MatchArtifact>(File.ReadAllText(path), Json);
            return artifact is null || artifact.Version != CurrentVersion ? null : artifact;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
///     The shipping store: one JSON file per match under
///     <c>%LOCALAPPDATA%\DotCharter\ai-match\results\</c>, beside the retrieval
///     library and the estimator model — the folder this lane already owns.
///     <para>
///         <b>Bounded, because it writes on every match.</b> A vector is ~360 floats
///         and a run keeps up to six of them, so a file is a few tens of KB; the store
///         keeps the newest <see cref="KeepFiles" /> and deletes the rest on each
///         write, oldest first. An unbounded diagnostic folder is a disk leak with a
///         good excuse.
///     </para>
///     <para>
///         The name is sortable and collision-free: <c>match-yyyyMMdd-HHmmssfff-N.json</c>,
///         where N is a counter within the millisecond.
///     </para>
/// </summary>
public sealed class MatchResultStore : IMatchResultStore
{
    /// <summary>How many result files to keep. Enough to compare a few attempts on
    /// one song against each other, which is the question this folder answers.</summary>
    public const int KeepFiles = 40;

    private readonly Func<DateTimeOffset> _now;
    private readonly string _root;
    private int _counter;

    /// <summary>Builds the store.</summary>
    /// <param name="root">Where to write. Null for the shipping folder; a test MUST
    /// pass a temp directory so a run cannot touch the user's own results.</param>
    /// <param name="now">Injected clock, so a test's file names are deterministic.</param>
    public MatchResultStore(string? root = null, Func<DateTimeOffset>? now = null)
    {
        _root = root ?? DefaultRoot();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Where the shipping store writes:
    /// <c>%LOCALAPPDATA%\DotCharter\ai-match\results</c>.</summary>
    public static string DefaultRoot() => Path.Combine(
        UserDataFolders.LocalAppData,
        "DotCharter", "ai-match", "results");

    /// <summary>The folder this instance writes to.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public string? TryWrite(ToneMatchResult result, string extractorId, string? stemHint, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(result);
        reason = null;
        try
        {
            Directory.CreateDirectory(_root);
            var stamp = _now().UtcDateTime;
            var name = string.Create(CultureInfo.InvariantCulture,
                $"match-{stamp:yyyyMMdd-HHmmssfff}-{Interlocked.Increment(ref _counter)}.json");
            var path = Path.Combine(_root, name);

            var artifact = new MatchArtifact(
                MatchArtifact.CurrentVersion,
                extractorId,
                stamp.ToString("O", CultureInfo.InvariantCulture),
                stemHint,
                result.TargetSelfDistanceFloor,
                result.ReferenceDistance,
                result.Elapsed.TotalSeconds,
                result.Notes,
                result.TargetFeatures,
                result.ReferenceFeatures,
                result.Candidates.Select(c => new MatchArtifactCandidate(
                    c.Origin,
                    c.Distance,
                    c.RankingDistance,
                    c.PlausibilityPenalty,
                    c.Score?.Percent,
                    c.Score?.UnratedReason,
                    c.Rig.Modules.Select(m => m.ModuleTypeId).ToList(),
                    c.Score?.Blocks ?? [],
                    c.Features)).ToList());

            // Temp + move: a reader that opens the folder mid-write must never see a
            // half-written document and decide the run was corrupt.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(artifact, MatchArtifact.Json));
            File.Move(tmp, path, overwrite: true);
            Prune();
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or JsonException)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private void Prune()
    {
        try
        {
            var files = Directory.GetFiles(_root, "match-*.json");
            if (files.Length <= KeepFiles) return;
            foreach (var stale in files.OrderBy(f => f, StringComparer.Ordinal).Take(files.Length - KeepFiles))
                File.Delete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be pruned is still a folder that was written to.
        }
    }
}
