using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The resx sibling of <c>ServiceRegistrationReachabilityTests</c>: a key
///     nothing reads is dead weight in three tables (invariant, de-DE, ja-JP) and
///     in every translator's queue. <c>LocalizationSweepTests</c> proves every
///     REFERENCED key exists; this proves every DEFINED key is referenced.
///     <para>
///         A key counts as used when its name appears as an identifier or string
///         token in any <c>.cs</c> / <c>.axaml</c> / <c>.json</c> file under a
///         <c>DotCharter.*</c> project (tests included, this file excluded;
///         comments are NOT stripped, so a comment naming the key keeps it). Keys
///         whose name is computed (<c>$"Prefix_{x}"</c>) are spelled out nowhere:
///         those families are listed in <see cref="DynamicPrefixes" /> and
///         <see cref="DynamicSuffixes" />, and each entry must still match a key.
///     </para>
/// </summary>
public sealed class UnusedResxKeyTests
{
    /// <summary>Key families built at run time by prefix. Add a row when you add a computed lookup.</summary>
    private static readonly string[] DynamicPrefixes =
    [
        "ChartSynth_Model_", "ChartSynth_Pickup_", "ChordTemplate_Applied_",
        "DcSong_ReadFailure_", "DcSong_WriteFailure_", "DcSong_WriteStage_", "DcSong_WriteWarning_",
        "Dialog_Export_Stage_", "EofLoss_", "History_",
        "Inspector_EqPreset_", "Inspector_EqTypeName_", "Inspector_EqTypeShort_",
        "Inspector_Queue_Filter_", "Inspector_RepeatReason_",
        // Link identical measures shows why it refused: LinkCreate_<LinkCreateRefusal>.
        "LinkCreate_",
        "RigImport_Loss_", "RigImport_Rail_", "ToneMatch_Block_"
    ];

    /// <summary>Companion suffixes appended to another key at run time (<c>key + "_Short"</c>): used when the base key is.</summary>
    private static readonly string[] DynamicSuffixes = ["_Short", "_Subtitle", "_Desc"];

    private static readonly Regex TokenRegex = new(@"[A-Za-z_][A-Za-z0-9_.]*", RegexOptions.Compiled);

    [Fact]
    public void EveryResxKey_IsReferencedBySomething()
    {
        var root = TestPaths.RepoRoot();
        var keys = ReadKeys(Path.Combine(root, "DotCharter.Localization", "Resources.resx"));
        var tokens = CollectTokens(root, out var files);

        Assert.True(files > 1000, $"only {files} source files scanned — the sweep is not reaching the projects");
        Assert.True(keys.Count > 1000, $"only {keys.Count} resx keys read — the sweep is not reading the table");

        var unused = keys.Where(k => !IsUsed(k, tokens)).ToList();
        Assert.True(unused.Count == 0,
            $"{unused.Count} resx key(s) are referenced by no code, XAML or JSON. Delete each from ALL THREE " +
            "resx files, or — if the name is built at run time — add its family to DynamicPrefixes / " +
            "DynamicSuffixes with the reason:\n  " + string.Join("\n  ", unused));
    }

    [Fact]
    public void EveryDynamicFamily_StillMatchesAKey()
    {
        var keys = ReadKeys(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        var stale = DynamicPrefixes.Where(p => !keys.Any(k => k.StartsWith(p, StringComparison.Ordinal))).ToList();
        Assert.True(stale.Count == 0, "DynamicPrefixes entries that match no key (delete them):\n  " + string.Join("\n  ", stale));
    }

    private static bool IsUsed(string key, HashSet<string> tokens)
    {
        if (tokens.Contains(key)) return true;
        if (DynamicPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))) return true;
        foreach (var suffix in DynamicSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal) && tokens.Contains(key[..^suffix.Length])) return true;
        }

        return false;
    }

    private static HashSet<string> CollectTokens(string root, out int fileCount)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var self = Path.Combine(root, "DotCharter.Tests", "UI", "UnusedResxKeyTests.cs");
        fileCount = 0;
        var sources = Directory.EnumerateDirectories(root, "DotCharter.*")
            .SelectMany(project => new[] { "*.cs", "*.axaml", "*.json" }
                .SelectMany(pattern => Directory.EnumerateFiles(project, pattern, SearchOption.AllDirectories)))
            .Where(file => !IsBuildOutput(file) && !string.Equals(file, self, StringComparison.OrdinalIgnoreCase));
        foreach (var file in sources)
        {
            fileCount++;
            foreach (Match m in TokenRegex.Matches(File.ReadAllText(file)))
            {
                tokens.Add(m.Value);
                if (!m.Value.Contains('.')) continue;
                foreach (var part in m.Value.Split('.')) tokens.Add(part);
            }
        }

        return tokens;
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static List<string> ReadKeys(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
}
