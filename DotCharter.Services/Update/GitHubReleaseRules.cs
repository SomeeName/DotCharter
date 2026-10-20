using System.Text.Json;
using DotCharter.Tone.Infrastructure.Json;

namespace DotCharter.Services.Update;

internal sealed record GitHubReleaseAsset(string Name, string ApiUrl, string BrowserUrl, long Size);

internal sealed record GitHubRelease(
    string TagName, string Body, string HtmlUrl, bool Prerelease, IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>
///     The pure rules for reading a GitHub release: version tags, the release
///     document, which asset is the app's payload or manifest, and
///     which hosts the updater may talk to. No I/O and no state, so every rule
///     is unit-tested on its own; <see cref="GitHubUpdateService" /> does the
///     network and disk work around them.
/// </summary>
internal static class GitHubReleaseRules
{
    /// <summary>Validates release tags and retains prerelease identifiers for comparison.</summary>
    internal static string? TryParseVersionFromTag(string? tag) => SemanticVersionRules.ParseTag(tag);

    internal static int CompareVersions(string a, string b) => SemanticVersionRules.Compare(a, b);

    /// <summary>Compares treating missing build/revision components as 0,
    /// so tag 0.13.0 == assembly version 0.13.0.0.</summary>
    internal static int CompareVersions(Version a, Version b)
    {
        return Normalize(a).CompareTo(Normalize(b));

        static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    internal static GitHubRelease? ParseLatestRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var assets = new List<GitHubReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetsElement) &&
                assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var name = JsonFields.StringOrEmpty(asset, "name");
                    if (string.IsNullOrEmpty(name)) continue;
                    assets.Add(new GitHubReleaseAsset(
                        name,
                        JsonFields.StringOrEmpty(asset, "url"),
                        JsonFields.StringOrEmpty(asset, "browser_download_url"),
                        asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                            ? size.GetInt64()
                            : 0));
                }
            }

            return new GitHubRelease(
                tagElement.GetString() ?? string.Empty,
                JsonFields.StringOrEmpty(root, "body"),
                JsonFields.StringOrEmpty(root, "html_url"),
                root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Three-part version text, the spelling release tags and asset names use.</summary>
    internal static string FormatVersion(Version version)
        => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>
    ///     File name for a locally ASSEMBLED payload. It is never published — it is
    ///     built on this machine out of range-fetched bytes — but it carries the
    ///     marker below so it can never be mistaken for a release asset.
    /// </summary>
    internal static string PartialAssetName(Version version)
        => $"DotCharter-v{FormatVersion(version)}{PartialNameMarker}win-x64.zip";

    /// <summary>
    ///     Marks an asset name that is NOT the full payload — the old published
    ///     delta patches (<c>-from-v…</c>, retired) and the locally
    ///     assembled partial payload (<c>-partial-</c>).
    ///     <para>
    ///         <b>The <c>-from-v</c> half still matters even though nothing builds
    ///         those any more.</b> Releases up to v0.42.4 published patch assets and
    ///         those release pages are permanent; a patch is named
    ///         <c>…-win-x64.zip</c> too, so without this exclusion
    ///         <see cref="SelectWinX64Asset" /> could hand a PARTIAL payload to an
    ///         install it was never diffed against — the broken-install case.
    ///     </para>
    /// </summary>
    private const string PatchNameMarker = "-from-v";

    private const string PartialNameMarker = "-partial-";

    /// <summary>
    ///     The standalone rig's zip (<c>DotCharter-RigLab-v….zip</c>) ships on the
    ///     SAME release page. It is a different app: installed over
    ///     DotCharter it would replace the charting app with the rig. It is named
    ///     without "win-x64" / "x64" so installs from before this exclusion never
    ///     pick it either; this is the half that makes it an explicit rule.
    /// </summary>
    private const string RigLabNameMarker = "RigLab";

    /// <summary>
    ///     The release's <c>payload-manifest.json</c> asset, or null when it
    ///     publishes none (older releases). Its presence is the entire
    ///     condition for a partial download.
    /// </summary>
    internal static GitHubReleaseAsset? SelectManifestAsset(IReadOnlyList<GitHubReleaseAsset> assets)
        => assets.FirstOrDefault(a =>
            string.Equals(a.Name, PayloadManifest.FileName, StringComparison.OrdinalIgnoreCase));

    internal static GitHubReleaseAsset? SelectWinX64Asset(IReadOnlyList<GitHubReleaseAsset> assets)
    {
        var zips = assets
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Name.Contains(PatchNameMarker, StringComparison.OrdinalIgnoreCase) &&
                        !a.Name.Contains(PartialNameMarker, StringComparison.OrdinalIgnoreCase) &&
                        !a.Name.Contains(RigLabNameMarker, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return zips.FirstOrDefault(a => a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
               ?? zips.FirstOrDefault(a =>
                   a.Name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
               ?? (zips.Count == 1 ? zips[0] : null);
    }

    /// <summary>
    ///     Hosts the updater will talk to, and the only ones the user's GitHub
    ///     token is ever attached to.
    ///     <para>
    ///         Every download url in here comes out of a JSON document (the release
    ///         asset's <c>url</c> / <c>browser_download_url</c>), so it is data, not
    ///         a constant — and <c>GitHubUpdateService.CreateRequest</c> put a bearer on
    ///         whatever it was handed. `SocketsHttpHandler` does strip
    ///         <c>Authorization</c> across a cross-host REDIRECT, but that is the
    ///         wrong half of the problem: a url that simply IS somewhere else was
    ///         never redirected, so nothing stripped anything.
    ///     </para>
    /// </summary>
    internal static readonly string[] AllowedHosts =
        ["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>
    ///     Whether the updater will talk to a url at all: https, and one of
    ///     <see cref="AllowedHosts" /> (or a subdomain of githubusercontent.com,
    ///     which GitHub shards asset delivery across).
    ///     <para>
    ///         <b>It gates the REQUEST, not only the header.</b> For
    ///         three releases this decided only whether the token was attached, so
    ///         a release document naming any host at all was still downloaded from
    ///         — and the one backstop was a digest check the same document could
    ///         defeat. <c>GitHubUpdateService.CreateRequest</c> refuses outright now.
    ///     </para>
    /// </summary>
    internal static bool IsAllowedReleaseHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = uri.Host;
        foreach (var allowed in AllowedHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }
}
