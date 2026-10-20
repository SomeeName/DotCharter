namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The repository is public (#829): no tracked file may be game data, a
///     copyrighted tab or copyrighted audio. Game archives and audio (<c>*.psarc</c>,
///     <c>*.sng</c>, <c>*.bnk</c>, <c>*.wem</c>, <c>*.mp3</c>, <c>*.flac</c>) are
///     allowed only when their path is listed in
///     <c>DotCharter.Tests/Fixtures/PROVENANCE.md</c>; tab files (<c>*.gp</c>,
///     <c>*.gp3</c>, <c>*.gp4</c>, <c>*.gp5</c>, <c>*.gpx</c>, <c>*.eof</c>) are never
///     allowed. "Tracked" means <c>git ls-files</c>, as in
///     <see cref="PersonalDataLintTests" />.
/// </summary>
public sealed class ForbiddenContentLintTests
{
    private const string ProvenanceFile = "DotCharter.Tests/Fixtures/PROVENANCE.md";

    private static readonly string[] GameAndAudioExtensions = [".psarc", ".sng", ".bnk", ".wem", ".mp3", ".flac"];
    private static readonly string[] TabExtensions = [".gp", ".gp3", ".gp4", ".gp5", ".gpx", ".eof"];

    [Fact]
    public void No_tracked_file_is_game_data_audio_or_a_tab()
    {
        var root = TestPaths.RepoRoot();
        Assert.SkipUnless(Path.Exists(Path.Combine(root, ".git")),
            "Not a git checkout (no .git) — the tracked-file list is unknowable here.");

        var tracked = PersonalDataLintTests.TrackedFiles(root);
        // The sentinel the scan must reach: a lint that lists no files passes vacuously.
        Assert.Contains("DotCharter.Tests/Infrastructure/PersonalDataLintTests.cs", tracked);
        Assert.True(tracked.Count > 1000, $"Only {tracked.Count} tracked files listed.");

        var violations = Violations(tracked, AllowList(Path.Combine(root, ProvenanceFile)));
        Assert.True(violations.Count == 0,
            "Game data, copyrighted audio or a tab file in tracked files — remove it, or, for " +
            "game or audio, list it in DotCharter.Tests/Fixtures/PROVENANCE.md:\n" +
            string.Join('\n', violations));
    }

    /// <summary>The probe twin: a synthetic path list must yield exactly the expected
    /// violations, and the allow-list exempts a listed game or audio file but never a tab.</summary>
    [Fact]
    public void Forbidden_extension_check_catches_synthetic_paths()
    {
        string[] synthetic =
        [
            "Fixtures/song.psarc", "Fixtures/Chart.SNG", "Audio/bank.bnk", "Audio/voice.wem",
            "Audio/track.mp3", "Audio/track.FLAC", "Tabs/riff.gp", "Tabs/riff.gp3", "Tabs/riff.gp4",
            "Tabs/riff.gp5", "Tabs/riff.gpx", "Tabs/riff.eof",
            "Assets/preview.png", "Docs/gp-notes.md", "Src/Psarc.cs", "Src/eof.txt", "Audio/track.mp3.txt"
        ];
        string[] expected =
        [
            "Fixtures/song.psarc", "Fixtures/Chart.SNG", "Audio/bank.bnk", "Audio/voice.wem",
            "Audio/track.mp3", "Audio/track.FLAC", "Tabs/riff.gp", "Tabs/riff.gp3", "Tabs/riff.gp4",
            "Tabs/riff.gp5", "Tabs/riff.gpx", "Tabs/riff.eof"
        ];
        Assert.Equal(expected, Violations(synthetic, new HashSet<string>(StringComparer.Ordinal)));

        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Fixtures/song.psarc", "Tabs/riff.gp5" };
        Assert.Equal(expected.Where(p => p != "Fixtures/song.psarc").ToArray(), Violations(synthetic, allowed));

        // A folder row covers every game or audio file under it, never a tab.
        var folder = new HashSet<string>(StringComparer.Ordinal) { "Audio/", "Tabs/" };
        Assert.Equal(expected.Where(p => !p.StartsWith("Audio/", StringComparison.Ordinal)).ToArray(),
            Violations(synthetic, folder));
    }

    /// <summary>PROVENANCE.md is a table: the allow-list must read its rows, or every
    /// listed fixture would still be reported.</summary>
    [Fact]
    public void The_allow_list_reads_table_rows_and_bullets()
    {
        var file = Path.Combine(Path.GetTempPath(), $"provenance-{Guid.NewGuid():N}.md");
        try
        {
            File.WriteAllLines(file,
            [
                "| Path | Origin | Licence |",
                "|---|---|---|",
                "| `Fixtures/a.psarc` | built by a test | GPL |",
                "- `Audio/b.wem`",
                "Plain prose without a path."
            ]);
            Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "Fixtures/a.psarc", "Audio/b.wem" },
                AllowList(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>The paths <see cref="ProvenanceFile"/> lists: the first backticked path on
    /// each line, so both a table row (<c>| `path` | origin | licence |</c>) and a bullet
    /// (<c>- `path`</c>) count. No provenance file means an empty allow-list.</summary>
    internal static HashSet<string> AllowList(string provenancePath)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(provenancePath)) return allowed;

        foreach (var line in File.ReadAllLines(provenancePath))
        {
            if (FirstCodeSpan(line) is { Length: > 0 } path) allowed.Add(path);
        }
        return allowed;
    }

    /// <summary>Listed by its own row, or by a folder row (a trailing <c>/</c> covers
    /// everything under it, as in PROVENANCE.md).</summary>
    private static bool IsListed(string path, IReadOnlySet<string> allowed) =>
        allowed.Contains(path) ||
        allowed.Any(entry => entry.EndsWith('/') && path.StartsWith(entry, StringComparison.Ordinal));

    private static string? FirstCodeSpan(string line)
    {
        var start = line.IndexOf('`');
        if (start < 0) return null;
        var end = line.IndexOf('`', start + 1);
        return end > start ? line[(start + 1)..end] : null;
    }

    /// <summary>The tracked paths that are forbidden: every tab file, and every game or
    /// audio file that is not in <paramref name="allowed" />.</summary>
    internal static List<string> Violations(IEnumerable<string> trackedPaths, IReadOnlySet<string> allowed)
    {
        var hits = new List<string>();
        foreach (var path in trackedPaths)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var forbidden = TabExtensions.Contains(extension) ||
                (GameAndAudioExtensions.Contains(extension) && !IsListed(path, allowed));
            if (forbidden) hits.Add(path);
        }
        return hits;
    }
}
