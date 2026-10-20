using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The product knows plugins only as a neutral extension point (plugins/&lt;name&gt;/, the
///     <c>PluginAssembly</c> declaration, <c>-WithPlugins</c>): no tracked file names a particular
///     plugin, the folder the plugin checkout used before, or the tooling switches that existed
///     for one plugin. A plugin's name, its build and release tooling and its decisions live in
///     the plugin's own repository. The patterns are assembled from pieces, so this file does
///     not match itself.
/// </summary>
public sealed class PluginNeutralityLintTests
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        ("plugin-name", new Regex(Regex.Escape("DotCharter" + ".Rock" + "smith"), Options)),
        ("old-plugin-folder", new Regex("(?<![A-Za-z0-9_.-])" + "add" + "ons[\\\\/]", Options)),
        ("old-build-switch", new Regex("DotCharter" + "Add" + "Ons", Options)),
        ("old-release-switch", new Regex("-With" + "Add" + "On\\b", Options)),
        ("old-gate-switch", new Regex("(?<![A-Za-z])-" + "Add" + "On\\b", RegexOptions.CultureInvariant)),
        ("old-payload-module", new Regex("Add" + "On" + "PayloadCheck", Options)),
    ];

    /// <summary>The rules that hit <paramref name="text" />, as <c>rule: line</c>.</summary>
    internal static IEnumerable<string> Scan(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (rule, pattern) in Rules)
                if (pattern.IsMatch(lines[i])) yield return $"{rule}: line {i + 1}";
        }
    }

    [Fact]
    public void Every_rule_hits_what_it_is_for_and_misses_the_neutral_words()
    {
        string[] bad =
        [
            "the " + "DotCharter.rock" + "smith.dll plugin",
            "<ProjectReference Include=\"..\\" + "add" + "ons\\X\\X.csproj\" />",
            "-p:DotCharter" + "AddOns=false",
            "New-Release.ps1 -With" + "AddOn",
            "Invoke-Gate.ps1 -" + "AddOn",
            "Import-Module Add" + "OnPayloadCheck.psm1",
        ];
        string[] neutral =
        [
            "plugins/Example/Example.csproj", "-p:DotCharterPlugins=false", "New-Release.ps1 -WithPlugins",
            "AddOnlineServices(services);", "Rocksmith 2014", "an optional plugin (add-on)",
        ];

        Assert.All(bad, line => Assert.NotEmpty(Scan(line)));
        Assert.Equal(Rules.Select(r => r.Rule), bad.Select(line => Scan(line).First().Split(':')[0]));
        Assert.All(neutral, line => Assert.Empty(Scan(line)));
    }

    [Fact]
    public void No_tracked_file_names_a_plugin_or_its_old_tooling()
    {
        var root = TestPaths.RepoRoot();
        Assert.SkipUnless(Path.Exists(Path.Combine(root, ".git")),
            "Not a git checkout (no .git) — the tracked-file list is unknowable here.");

        var tracked = PersonalDataLintTests.TrackedFiles(root);
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var relative in tracked)
        {
            foreach (var (rule, pattern) in Rules)
                if (pattern.IsMatch(relative)) offenders.Add($"{relative} ({rule}: the path itself)");

            var full = Path.Combine(root, relative);
            if (!File.Exists(full)) continue; // deleted in the work tree
            var bytes = File.ReadAllBytes(full);
            if (PersonalDataLintTests.IsBinary(bytes)) continue;
            scanned++;
            offenders.AddRange(Scan(Encoding.UTF8.GetString(bytes)).Select(hit => $"{relative} ({hit})"));
        }

        // A scan over nothing would pass.
        Assert.Contains("DotCharter.Services/Plugins/PluginLoader.cs", tracked);
        Assert.True(scanned > 1000, $"Only {scanned} tracked text files scanned.");
        Assert.True(offenders.Count == 0, LintReport.Of(
            "A tracked product file names a plugin, its old folder or its old tooling switches",
            offenders,
            "say \"a plugin\" / \"plugins/<name>/\" / -WithPlugins / DotCharterPlugins; a plugin's name, scripts and " +
            "decisions belong in the plugin's own repository",
            "docs/agents/services/contested-features.md"));
    }
}
