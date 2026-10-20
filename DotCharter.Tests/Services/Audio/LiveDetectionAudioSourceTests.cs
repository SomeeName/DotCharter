
namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     <b>Opus stems are playback-only — they may never reach live note
///     detection.</b> The durable <c>.dcstems</c> cache stores lossy Opus, and the
///     detector's whole job is to decide whether a partial is really there: a
///     codec that discards inaudible partials by design discards exactly the
///     evidence it judges on.
///     <para>
///         The rule has held since it was written and nothing checked it. It is
///         also the easiest one in the subsystem to break by accident, because a
///         stem set is right there on <c>IAudioPlaybackService</c> and reads like
///         a cleaner signal than the room mic.
///     </para>
///     <para>
///         Asserted on the SOURCE, over the DERIVED file set rather than a list —
///         a lint that names its files goes vacuous the day one is split. The two
///         excluded subfolders are excluded for a stated reason, and the test
///         proves they are still there so the exclusion cannot silently widen.
///     </para>
/// </summary>
public sealed class LiveDetectionAudioSourceTests
{
    /// <summary>
    ///     Names that mean "an audio file somebody else produced". None of them
    ///     belongs anywhere in the detection chain, whose only input is the live
    ///     capture ring (<c>ReadInputCapture*</c> on the playback service).
    /// </summary>
    private static readonly string[] ForbiddenInDetection =
    [
        "StemTrack", "IStemSeparationService", "StemCacheCoordinator", "StemCacheStore",
        "StemArchive", "StemCacheKey", "LoadedStemNames", "HasStems", ".opus",
        // Guitar roles (#260): Lead/Rhythm derived from a separated guitar stem —
        // a stem twice removed from the player, playback-only like its source.
        "GuitarRole", "CentreSideSplitter",
        // A .dcsong's song and stems (#708): Opus, playback-only, the stem mixer's.
        "DcSong",
    ];

    /// <summary>
    ///     <c>Takes/</c> records the PLAYER'S OWN performance to disk and
    ///     <c>Replays/</c> stores the resulting verdict track. Both are outputs of
    ///     a judged run, never inputs to one — <c>LiveTakeStore</c> legitimately
    ///     writes Opus. Everything else under <c>LivePlay/</c> is the detection
    ///     chain.
    /// </summary>
    private static readonly string[] ExcludedSubfolders = ["Takes", "Replays"];

    [Fact]
    public void NothingInTheLiveDetectionChain_ReferencesAStemOrACompressedAudioFile()
    {
        var files = DetectionChainFiles();

        // Vacuity guard: this lint is worthless if the enumeration finds nothing,
        // and a folder rename would make it find nothing while staying green.
        Assert.True(files.Count >= 10,
            $"Only {files.Count} detection-chain files were found — has Audio/LivePlay/ moved?");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = SourceText.StripCommentLines(File.ReadAllText(file));
            foreach (var forbidden in ForbiddenInDetection)
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} -> {forbidden}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Live note detection may only ever read the live input capture. These files reach for a " +
            "SEPARATED or COMPRESSED audio source instead, which is the Opus-stems-are-playback-only rule: " +
            string.Join(", ", offenders));
    }

    /// <summary>The two exclusions must keep existing, or the test above quietly
    /// becomes a scan of a folder set nobody meant to exclude.</summary>
    [Fact]
    public void TheExcludedSubfolders_StillExist_SoTheExclusionCannotWiden()
    {
        foreach (var folder in ExcludedSubfolders)
        {
            var path = Path.Combine(LivePlayRoot(), folder);
            Assert.True(Directory.Exists(path),
                $"'{folder}' no longer exists under Audio/LivePlay/. Delete the exclusion or repoint it — " +
                "an exclusion for a folder that is gone is an exclusion waiting to cover something else.");
        }
    }

    private static IReadOnlyList<string> DetectionChainFiles()
    {
        var root = LivePlayRoot();
        var excluded = ExcludedSubfolders
            .Select(f => Path.Combine(root, f) + Path.DirectorySeparatorChar)
            .ToArray();

        return [.. Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !excluded.Any(e => p.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal)];
    }

    private static string LivePlayRoot() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Audio", "LivePlay");
}
