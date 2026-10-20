using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The public source tree holds no package reader (#892, epic #882): no tracked text file, comments
///     included, names a reader or decryption type, a game-install or game-package path, or the container
///     magic outside the writer, and no tracked package or game-audio binary exists beyond a short,
///     explained allow-list. A reader belongs in an optional plugin (<c>plugins/</c>, git-ignored and
///     never scanned); <see cref="PublicAssembliesHaveNoPackageReaderTests" /> is the compiled-assembly
///     twin of this source-level guard. The banned names live only in this file, which is excluded by
///     path, and every allow-list entry must still exist and still hit, so the list cannot rot.
/// </summary>
public sealed class PublicTreeHasNoPackageReaderTests
{
    private const string TokenRule = "token";
    private const string MagicRule = "magic";
    private const string BinaryRule = "binary";

    private static readonly string[] ScannedExtensions =
        [".cs", ".axaml", ".ps1", ".psm1", ".md", ".json", ".resx", ".csproj", ".props", ".targets"];

    private static readonly string[] BinaryExtensions = [".psarc", ".sng", ".bnk", ".wem"];

    /// <summary>The lint files that carry the banned names as DATA (their own token tables).</summary>
    private static readonly string[] LintFiles =
    [
        "DotCharter.Tests/Infrastructure/PublicTreeHasNoPackageReaderTests.cs",
        "DotCharter.Tests/Infrastructure/PublicAssembliesHaveNoPackageReaderTests.cs",
        "DotCharter.Tests/Infrastructure/WriterSideReaderReferenceLintTests.cs"
    ];

    /// <summary>Type and member names: matched case-sensitively, anywhere in a line.</summary>
    private static readonly Regex ExactNames = new(
        "PsarcArchive|RocksmithSngReader|SngToDomainMapper|UnpackPackedSng|SngDecryptor|PsarcTocDecryptor|" +
        "DecryptCfb|CreateDecryptor|RocksmithInstallLocator|RsGameTablesBuilder|WwiseBankReader|" +
        "PsarcLibrarySource|OfficialContentRule|PsarcProvenance|OfficialRocksmith|\\bEVAS\\b",
        RegexOptions.CultureInvariant);

    /// <summary>Game file names: any casing; a following word character (<c>songs.psarc_path</c>) is another name.</summary>
    private static readonly Regex GameFileNames = new(
        @"songs\.psarc\b|gears\.psarc\b|rs1compatibility",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The container magic as a literal or as its four bytes in either order.</summary>
    private static readonly Regex Magic = new(
        "\"PSAR\"|0x50534152|0x52415350",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string WriterFolder = "DotCharter.Services/Infrastructure/Psarc/Write/";

    /// <summary>Justified hits. Each entry must exist in the tree and must still hit its rule.</summary>
    private static readonly (string Path, string Rule, string Reason)[] Allowed =
    [
        ("DotCharter.Tests/Services/Export/RocksmithPackageBytePinTests.cs", MagicRule,
            "asserts the WRITER's output starts with the container magic"),
        ("DotCharter.Tests/Services/Export/SngWriterBytePinTests.cs", MagicRule,
            "asserts the WRITER's output starts with the container magic"),
        ("DotCharter.Tests/Services/Parsing/Psarc/PsarcWriterTests.cs", MagicRule,
            "asserts the WRITER's output starts with the container magic"),
        ("DotCharter.Tests/Services/Parsing/Fixtures/tone-stereo48k-inline-mod.wem", BinaryRule,
            "synthetic tone written by the repo's own fixture writer (Fixtures/PROVENANCE.md); the WEM-decode tests need a real stream")
    ];

    [Fact]
    public void The_public_tree_names_no_package_reader_and_holds_no_game_package()
    {
        var root = TestPaths.RepoRoot();
        Assert.SkipUnless(Path.Exists(Path.Combine(root, ".git")),
            "Not a git checkout (no .git) — the tracked-file list is unknowable here.");

        var tracked = PersonalDataLintTests.TrackedFiles(root)
            .Where(p => !p.StartsWith("plugins/", StringComparison.Ordinal))
            .ToList();
        // Sentinels the scan must reach: a lint that lists or reads nothing passes vacuously.
        Assert.Contains(WriterFolder + "PsarcWriter.cs", tracked);
        Assert.Contains("DotCharter.Tests/Services/Parsing/Fixtures/tone-stereo48k-inline-mod.wem", tracked);
        Assert.True(tracked.Count > 1000, $"Only {tracked.Count} tracked files listed.");

        var hits = new List<(string Path, string Rule, string Where)>();
        var scanned = 0;
        foreach (var relative in tracked)
        {
            var extension = Path.GetExtension(relative);
            if (BinaryExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                hits.Add((relative, BinaryRule, relative));
                continue;
            }

            if (!ScannedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) || LintFiles.Contains(relative)) continue;

            var full = Path.Combine(root, relative);
            if (!File.Exists(full)) continue; // a file deleted in the work tree

            scanned++;
            hits.AddRange(Scan(relative, File.ReadAllText(full, Encoding.UTF8)).Select(h => (relative, h.Rule, h.Where)));
        }

        Assert.True(scanned > 1000, $"Only {scanned} tracked text files scanned.");

        var offenders = hits
            .Where(h => !Allowed.Any(a => a.Path == h.Path && a.Rule == h.Rule))
            .Select(h => h.Where)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(offenders.Count == 0, LintReport.Of(
            "The public tree names a package reader, a game-package path or the container magic, or holds a game package (#892)",
            offenders,
            "reword a comment or doc to \"the package reader\" / \"the game package\"; a reader belongs in an optional plugin; " +
            "a fixture that truly needs a binary gets a row in Fixtures/PROVENANCE.md and an Allowed entry here with its reason.",
            "docs/agents/services/parsing-psarc.md"));

        var rotten = Allowed
            .Where(a => !tracked.Contains(a.Path) || !hits.Any(h => h.Path == a.Path && h.Rule == a.Rule))
            .Select(a => $"{a.Path} ({a.Rule}): {a.Reason}")
            .ToList();
        Assert.True(rotten.Count == 0, LintReport.Of(
            "An allow-list entry no longer exists or no longer hits (#892)",
            rotten,
            "delete the entry from Allowed in PublicTreeHasNoPackageReaderTests; the list may only hold live, justified hits."));
    }

    /// <summary>Non-vacuity: every rule fires on its own target, and the neutral wording the tree uses stays legal.</summary>
    [Theory]
    [InlineData("var a = PsarcArchive.Open(path);", TokenRule, "DotCharter.Services/A.cs")]
    [InlineData("// the RocksmithSngReader maps", TokenRule, "docs/x.md")]
    [InlineData("using var aes = Aes.Create(); aes.CreateDecryptor();", TokenRule, "tools/x.ps1")]
    [InlineData("\"path\": \"dlc/Songs.PSARC\"", TokenRule, "x.json")]
    [InlineData("see the GEARS.psarc manifests", TokenRule, "x.md")]
    [InlineData("package rs1compatibilitydlc_p", TokenRule, "x.cs")]
    [InlineData("flag EVAS here", TokenRule, "x.md")]
    [InlineData("var magic = \"PSAR\"u8;", MagicRule, "DotCharter.Services/Export/A.cs")]
    [InlineData("var magic = 0x50534152;", MagicRule, "DotCharter.Services/Export/A.cs")]
    public void Every_rule_fires_on_its_target(string text, string rule, string path) =>
        Assert.Contains(Scan(path, text), h => h.Rule == rule);

    [Theory]
    [InlineData("WHERE r.psarc_path = songs.psarc_path")]
    [InlineData("the package reader is an optional plugin")]
    [InlineData("var writer = new PsarcWriter(stream);")]
    [InlineData("PSARC is the container; PSAR is not spelled out")]
    [InlineData("Evas and evasive are words")]
    public void Neutral_wording_is_not_flagged(string text) =>
        Assert.Empty(Scan("DotCharter.Services/A.cs", text));

    [Fact]
    public void The_magic_is_allowed_in_the_writer_folder()
    {
        Assert.Empty(Scan(WriterFolder + "PsarcWriter.cs", "\"PSAR\"u8.CopyTo(header);"));
        Assert.NotEmpty(Scan("DotCharter.Services/Export/A.cs", "\"PSAR\"u8.CopyTo(header);"));
    }

    private static List<(string Rule, string Where)> Scan(string relativePath, string text)
    {
        var found = new List<(string Rule, string Where)>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var where = $"{relativePath}:{i + 1}";
            var token = ExactNames.Match(line);
            if (!token.Success) token = GameFileNames.Match(line);
            if (token.Success) found.Add((TokenRule, $"{where}: {token.Value}"));
            if (!relativePath.StartsWith(WriterFolder, StringComparison.Ordinal) && Magic.IsMatch(line))
                found.Add((MagicRule, $"{where}: {Magic.Match(line).Value}"));
        }

        return found;
    }
}
