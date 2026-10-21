using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Keeps the release documentation honest about WHAT A RELEASE SHIPS.
/// </summary>
/// <remarks>
///     <para>
///         Three places name the asset set — <c>tools/release/New-Release.ps1</c>'s
///         header, its actual <c>$assets</c> list, and <c>tools/release/README.md</c>
///         — and all three had drifted apart: the README said 5, the script header
///         said 6, and the script was uploading 7 (v0.60 cut the set to 3 when the
///         self-hosted server stopped shipping). The extra one was
///         <c>SHA256SUMS.txt</c>, which is not a convenience:
///         <c>GitHubUpdateService</c> fetches it to authenticate a downloaded
///         release asset, and an accepted asset is robocopied over the live
///         install.
///         "How many assets are there" is the kind of number the root
///         <c>AGENTS.md</c> says nothing mechanical covers — so this covers it.
///     </para>
///     <para>
///         The expected set is DERIVED from the script's own numbered header list;
///         nothing here hard-codes a count, or the test would just be a fourth
///         place to forget.
///     </para>
/// </remarks>
public sealed class ReleaseAssetLintTests
{
    /// <summary>The numbered "N. name" list in the script's .DESCRIPTION block.</summary>
    private static string[] DeclaredAssets(string script) =>
        Regex.Matches(script, @"^\s{6}\d+\.\s+(?<name>\S+)\s{2,}", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToArray();

    /// <summary>
    ///     The changelog stub lists the commits since the last APP release. Model files ship as their
    ///     own pre-release tags (<c>models-bs-roformer-sw-v1</c>), and an unfiltered describe can start
    ///     the stub at one of those instead.
    /// </summary>
    [Fact]
    public void The_changelog_stub_starts_at_the_last_app_release_tag()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));

        var describes = Regex.Matches(script, @"describe --tags[^\r\n]*").Select(m => m.Value).ToList();

        Assert.NotEmpty(describes);
        Assert.All(describes, d => Assert.Contains("--match 'v[0-9]*'", d));
    }

    [Fact]
    public void The_script_header_the_readme_and_the_upload_list_all_agree_on_the_asset_count()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));
        var readme = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "README.md"));

        var declared = DeclaredAssets(script);
        Assert.True(declared.Length >= 3,
            $"could not parse the asset list out of New-Release.ps1's header (found {declared.Length}).");

        // The prose count in both files must equal the list the script itself prints.
        Assert.Contains($"builds all {declared.Length} GitHub release assets", script);
        Assert.Contains($"A release has exactly {declared.Length} assets", script);
        Assert.Contains($"**{declared.Length} release assets**", readme);

        // …and every one of them must have a row in the README's asset table, or
        // the person cutting the release does not know it exists.
        foreach (var asset in declared)
        {
            Assert.True(readme.Contains(asset, StringComparison.Ordinal),
                $"tools/release/README.md does not mention the release asset '{asset}'.");
        }
    }

    /// <summary>
    ///     The half that actually broke: an asset the script UPLOADS but its own
    ///     header never listed. Every literal filename joined to <c>$OutputDir</c>
    ///     and then appended to <c>$assets</c> must appear in the header list.
    ///     (<c>notes-v&lt;ver&gt;.md</c> is deliberately not one — it goes to
    ///     <c>gh --notes-file</c>, not to the asset list.)
    /// </summary>
    [Fact]
    public void Every_literally_named_asset_the_script_uploads_is_listed_in_its_header()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));
        var declared = DeclaredAssets(script);

        var appended = Regex.Matches(script, @"\$assets\s*(?:\+=|=\s*@\(\$assets\)\s*\+)\s*\$(?<var>\w+)")
            .Select(m => m.Groups["var"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(appended);

        foreach (var name in appended)
        {
            var assignment = Regex.Match(script,
                @"\$" + name + @"\s*=\s*Join-Path\s+\$OutputDir\s+'(?<file>[^']+)'");
            if (!assignment.Success) continue; // built from an interpolated name — not a literal

            var file = assignment.Groups["file"].Value;
            Assert.True(declared.Contains(file),
                $"New-Release.ps1 uploads '{file}' but its own header does not list it — " +
                "the header is what tells the release cutter (and tools/release/README.md) what ships.");
        }
    }

    /// <summary>
    ///     The release SIGNS its <c>SHA256SUMS.txt</c>, uploads the signature,
    ///     and stops before building anything when the key is missing — every
    ///     install from v1.1.0 on refuses an unsigned release, so a script that
    ///     could print a publish command without one would cut a release nobody
    ///     can install.
    /// </summary>
    [Fact]
    public void The_release_signs_its_checksums_uploads_the_signature_and_fails_closed_without_the_key()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));
        var tool = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "SignReleaseManifest.cs"));

        Assert.Contains("SHA256SUMS.txt.sig", DeclaredAssets(script));

        // The signature is produced by the tool, after the checksums, and appended to the upload list.
        var sums = script.IndexOf("$checksumPath = Join-Path $OutputDir 'SHA256SUMS.txt'", StringComparison.Ordinal);
        var sign = script.IndexOf("& dotnet run $SignTool sign $checksumPath $SigningKeyPath $signaturePath", StringComparison.Ordinal);
        Assert.True(sums >= 0 && sign > sums, "New-Release.ps1 does not sign SHA256SUMS.txt after writing it.");
        Assert.Matches(@"\$signaturePath\s*=\s*Join-Path\s+\$OutputDir\s+'SHA256SUMS\.txt\.sig'", script);
        Assert.Matches(@"\$assets\s*=\s*@\(\$assets\)\s*\+\s*\$signaturePath", script);
        Assert.Contains("# release: v$Version", script, StringComparison.Ordinal);

        // Fail closed: a missing key throws in PREFLIGHT, before the publishes.
        var preflight = script.IndexOf("Write-Step 'Preflight checks'", StringComparison.Ordinal);
        var missingKey = script.IndexOf("The release-signing key is missing", StringComparison.Ordinal);
        var firstPublish = script.IndexOf("# --- Asset 1:", StringComparison.Ordinal);
        Assert.True(preflight >= 0 && missingKey > preflight && missingKey < firstPublish,
            "New-Release.ps1 must refuse a missing release-signing key in preflight.");
        Assert.Contains("Signing SHA256SUMS.txt failed", script, StringComparison.Ordinal);

        // The tool verifies with the app's own check and the pinned key, and never overwrites a key.
        Assert.Contains("ReleaseSignature.Check(", tool, StringComparison.Ordinal);
        Assert.Contains("ReleaseSignature.PinnedPublicKey()", tool, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", tool, StringComparison.Ordinal);
    }
}
