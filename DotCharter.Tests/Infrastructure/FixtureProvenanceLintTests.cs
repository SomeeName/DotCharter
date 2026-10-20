using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     #827: every tracked binary or data fixture says where it came from. A repository
///     that goes public must not carry a transcription or a recording of a copyrighted
///     song, and "who made this, and under what licence" is the question a reviewer
///     cannot answer from a <c>.ogg</c>. <c>DotCharter.Tests/Fixtures/PROVENANCE.md</c>
///     holds one table row per file (or one per folder, written with a trailing slash,
///     where a whole generated or vendored set shares one origin); the first cell is the
///     path in backticks. A new fixture without a row fails here, and so does a row whose
///     file is gone. "Tracked" means <c>git ls-files</c>: the owner's git-ignored corpus
///     is never read.
/// </summary>
public sealed partial class FixtureProvenanceLintTests
{
    private const string ProvenancePath = "DotCharter.Tests/Fixtures/PROVENANCE.md";

    /// <summary>A JSON file this big is data whatever its folder.</summary>
    private const long LargeDataFileBytes = 64 * 1024;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nam", ".aidax", ".ttf", ".otf", ".woff", ".woff2", ".png", ".webp", ".jpg", ".jpeg", ".gif", ".ico", ".svg",
        ".zip", ".bin", ".flat", ".onnx", ".ogg", ".wem", ".wav", ".mp3", ".flac", ".opus", ".psarc", ".dcvp",
        ".dcproj", ".dcstems", ".gp", ".gp3", ".gp4", ".gp5", ".gpx", ".mid", ".midi", ".eof", ".sng", ".sf2"
    };

    private static string Root => TestPaths.RepoRoot();

    /// <summary>Whether <paramref name="path" /> (repo-relative, forward slashes) is a fixture that needs a provenance row.</summary>
    internal static bool NeedsRecord(string path, Func<string, long> sizeOf)
    {
        if (path.StartsWith("JuceSharp/", StringComparison.Ordinal) || path == ProvenancePath) return false;

        var extension = Path.GetExtension(path);
        if (BinaryExtensions.Contains(extension)) return true;

        var inTests = path.StartsWith("DotCharter.Tests/", StringComparison.Ordinal);
        var inAssets = path.Contains("/Assets/", StringComparison.Ordinal);
        var inFixtures = path.Contains("/Fixtures/", StringComparison.Ordinal);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return inTests || inAssets || sizeOf(path) >= LargeDataFileBytes;
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return inFixtures;
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith("docs/", StringComparison.Ordinal);
        return false;
    }

    /// <summary>The recorded paths of <paramref name="provenance" />: the first backticked cell of each table row.</summary>
    internal static List<string> Records(string provenance) =>
        RowPath().Matches(provenance).Select(m => m.Groups[1].Value).ToList();

    internal static bool Covers(string record, string path) =>
        record.EndsWith('/') ? path.StartsWith(record, StringComparison.Ordinal) : record == path;

    internal static List<string> Uncovered(IEnumerable<string> files, IReadOnlyList<string> records) =>
        files.Where(file => !records.Any(record => Covers(record, file))).Order(StringComparer.Ordinal).ToList();

    private static List<string> FixtureFiles() =>
        PersonalDataLintTests.TrackedFiles(Root)
            .Select(file => file.Replace('\\', '/'))
            .Where(file => File.Exists(Path.Combine(Root, file)))
            .Where(file => NeedsRecord(file, f => new FileInfo(Path.Combine(Root, f)).Length))
            .ToList();

    [Fact]
    public void Every_tracked_fixture_has_a_provenance_row()
    {
        var files = FixtureFiles();
        Assert.True(files.Count >= 300, $"Found only {files.Count} fixtures - the scan (git ls-files) is broken.");

        var records = Records(File.ReadAllText(Path.Combine(Root, ProvenancePath)));
        var missing = Uncovered(files, records);
        Assert.True(missing.Count == 0,
            $"{ProvenancePath} has no row for: {string.Join(", ", missing)}. Add one: path, who made it and how, and " +
            "the licence. A recording or a note-for-note transcription of a real song must not be committed at all.");
    }

    [Fact]
    public void Every_provenance_row_names_a_tracked_file_or_folder()
    {
        var tracked = PersonalDataLintTests.TrackedFiles(Root).Select(file => file.Replace('\\', '/')).ToList();
        var records = Records(File.ReadAllText(Path.Combine(Root, ProvenancePath)));
        Assert.True(records.Count >= 30, $"Read only {records.Count} rows - the table format changed.");

        var stale = records.Where(record => !tracked.Any(file => Covers(record, file))).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, $"{ProvenancePath} names files that are not tracked: {string.Join(", ", stale)}.");
    }

    [Fact]
    public void A_fixture_without_a_row_is_reported_and_a_folder_row_covers_its_children()
    {
        var records = Records("| `a/b.ogg` | made here | CC0 |\n| `a/set/` | generated | MIT |\n| not a row | x | y |");
        Assert.Equal(["a/b.ogg", "a/set/"], records);

        var missing = Uncovered(["a/b.ogg", "a/c.ogg", "a/set/one.png", "a/setx/two.png"], records);
        Assert.Equal(["a/c.ogg", "a/setx/two.png"], missing);
    }

    [Fact]
    public void Fixture_classification_picks_data_files_and_ignores_code()
    {
        long Small(string _) => 10;
        long Big(string _) => LargeDataFileBytes;

        Assert.True(NeedsRecord("x/clip.ogg", Small));
        Assert.True(NeedsRecord("DotCharter.Tests/Foo/data.json", Small));
        Assert.True(NeedsRecord("tools/big.json", Big));
        Assert.True(NeedsRecord("DotCharter.Tests/Core/Fixtures/names.txt", Small));
        Assert.True(NeedsRecord("docs/design/page.html", Small));
        Assert.False(NeedsRecord("tools/small.json", Small));
        Assert.False(NeedsRecord("DotCharter.Core/Foo.cs", Small));
        Assert.False(NeedsRecord("JuceSharp/tests/clip.ogg", Small));
        Assert.False(NeedsRecord("DotCharter.Tests/Fixtures/PROVENANCE.md", Small));
    }

    [GeneratedRegex(@"^\|\s*`([^`]+)`", RegexOptions.Multiline)]
    private static partial Regex RowPath();
}
