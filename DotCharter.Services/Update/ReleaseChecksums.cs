using System.Security.Cryptography;

namespace DotCharter.Services.Update;

/// <summary>
///     The published <c>SHA256SUMS.txt</c> for a release: its asset name, the
///     parser for its <c>sha256sum</c> format, and the hash of a local file to
///     compare against it.
///     <para>
///         <b>It is published AND read.</b> The reader is
///         <c>GitHubUpdateService.TryFetchReleaseChecksumsAsync</c>, and the
///         download path is what actually compares — a downloaded zip is
///         extracted over the live install by a detached script, so the digest is
///         the only thing that says the bytes are the build they claim to be. A
///         release that predates published digests (or an offline check) yields a
///         null manifest, which is the degraded path, not a rejection.
///     </para>
///     <para>
///         The emitter is <c>tools/release/New-Release.ps1</c>; keep the two in
///         step. Format is the standard one — "&lt;hex&gt;  &lt;filename&gt;" per
///         line.
///     </para>
/// </summary>
public static class ReleaseChecksums
{
    /// <summary>Asset name the release publishes its digests under.</summary>
    public const string AssetName = "SHA256SUMS.txt";

    /// <summary>SHA-256 of a file, lowercase hex.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);
        var digest = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    ///     Parses a <c>sha256sum</c>-style manifest into filename → digest.
    ///     Tolerant of blank lines, comments, CRLF and the "*" binary marker;
    ///     a malformed line is skipped rather than failing the whole manifest.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var split = line.IndexOf(' ');
            if (split <= 0) continue;

            var digest = line[..split].Trim().ToLowerInvariant();
            var name = line[(split + 1)..].TrimStart(' ', '*').Trim();
            if (digest.Length != 64 || name.Length == 0) continue;
            if (!digest.All(Uri.IsHexDigit)) continue;

            map[name] = digest;
        }

        return map;
    }
}
