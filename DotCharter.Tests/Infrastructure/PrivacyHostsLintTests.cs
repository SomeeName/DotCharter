using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every host the product code can reach must be named in <c>PRIVACY.md</c>
///     (#825), so a new integration cannot ship unlisted. Scans the <c>.cs</c> files
///     of the <c>DotCharter.*</c> product projects for <c>http(s)://host</c>
///     literals. Ignored on purpose: localhost, the test project, the submodule,
///     comments (doc-only URLs), and the few non-connection identifiers in
///     <see cref="NotConnections" />.
/// </summary>
public sealed partial class PrivacyHostsLintTests
{
    /// <summary>URL-shaped strings that are never opened as a connection.</summary>
    private static readonly Dictionary<string, string> NotConnections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["docs.avaloniaui.net"] = "documentation link in an attribute, never opened by the app",
        ["emergent.net"] = "XML-style namespace in an exported graph file",
        ["local.imported"] = "placeholder URL for a model imported from disk",
        ["schemas.microsoft.com"] = "XML namespace"
    };

    private static readonly string[] SkippedDirectories =
        ["bin", "obj", ".git", ".vs", ".idea", ".claude", ".codex", "TestData", "node_modules", "artifacts"];

    [GeneratedRegex(@"https?://([A-Za-z0-9][A-Za-z0-9.-]*[A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex UrlHost();

    [Fact]
    public void Every_outbound_host_in_product_code_is_listed_in_PRIVACY_md()
    {
        var root = TestPaths.RepoRoot();
        var privacy = File.ReadAllText(Path.Combine(root, "PRIVACY.md"));

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        foreach (var file in ProductSources(root))
        {
            scanned++;
            foreach (var host in HostsIn(File.ReadAllText(file)))
                found.TryAdd(host, Path.GetRelativePath(root, file));
        }

        // Sentinels: a lint that scans nothing or finds nothing passes vacuously.
        Assert.True(scanned > 500, $"Only {scanned} product sources scanned.");
        Assert.Contains("api.github.com", found.Keys);
        Assert.Contains("musicbrainz.org", found.Keys);

        var unlisted = Unlisted(found.Keys, privacy)
            .Select(h => $"{h} (first seen in {found[h]})")
            .ToList();
        Assert.True(unlisted.Count == 0,
            "Outbound host(s) in product code that PRIVACY.md does not mention — add them to the table " +
            "(when, what is sent, whose key), or, if it is not a connection, to NotConnections:\n" +
            string.Join('\n', unlisted));
    }

    /// <summary>The probe twin: a host missing from the document is reported, and
    /// localhost, comments and the non-connection list are not.</summary>
    [Fact]
    public void The_lint_fails_for_a_missing_host_and_ignores_the_exempt_ones()
    {
        const string source = """
            // see https://docs.example.org/page
            /// <c>https://doc-only.example.org</c>
            var a = "https://api.listed.example/v1";
            var b = "https://missing.example/v1"; // https://comment-tail.example
            var c = "http://localhost:1234";
            var d = "http://127.0.0.1:47821/";
            var e = "http://emergent.net/aweb/1.0/{0}";
            /* https://block.example */
            """;
        var hosts = HostsIn(source).ToList();
        Assert.Equal(["api.listed.example", "missing.example"], hosts);

        Assert.Equal(["missing.example"], Unlisted(hosts, "We call `api.listed.example`."));
        Assert.Empty(Unlisted(hosts, "We call `api.listed.example` and `missing.example`."));
    }

    private static List<string> Unlisted(IEnumerable<string> hosts, string privacy) =>
        hosts.Where(h => !privacy.Contains(h, StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Hosts named by URL literals in code. Whole-line comments, block
    /// comments and trailing <c>// …</c> comments are dropped first.</summary>
    private static IEnumerable<string> HostsIn(string source)
    {
        var inBlock = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (inBlock)
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) continue;
                inBlock = false;
                line = line[(end + 2)..];
            }

            if (line.StartsWith("//", StringComparison.Ordinal)) continue;

            var start = line.IndexOf("/*", StringComparison.Ordinal);
            if (start >= 0)
            {
                var end = line.IndexOf("*/", start + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    inBlock = true;
                    line = line[..start];
                }
                else
                {
                    line = line[..start] + line[(end + 2)..];
                }
            }

            // A trailing comment starts at " //" (a URL's "//" follows ":").
            var tail = line.IndexOf(" //", StringComparison.Ordinal);
            if (tail >= 0) line = line[..tail];

            foreach (Match m in UrlHost().Matches(line))
            {
                var host = m.Groups[1].Value.ToLowerInvariant();
                if (IsLoopback(host) || NotConnections.ContainsKey(host)) continue;
                if (seen.Add(host)) yield return host;
            }
        }
    }

    private static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "0.0.0.0" || host.EndsWith(".localhost", StringComparison.Ordinal);

    private static IEnumerable<string> ProductSources(string root)
    {
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            var name = Path.GetFileName(project);
            if (name is "DotCharter.Tests") continue;
            foreach (var file in Walk(project)) yield return file;
        }
    }

    private static IEnumerable<string> Walk(string dir)
    {
        foreach (var child in Directory.EnumerateDirectories(dir))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var file in Walk(child)) yield return file;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            yield return file;
    }
}
