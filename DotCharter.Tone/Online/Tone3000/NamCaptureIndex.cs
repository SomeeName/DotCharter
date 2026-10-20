using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     One locally stored NAM capture's metadata, persisted as a JSON sidecar
///     next to the capture file. License + attribution URL are load-bearing:
///     per the app's attribution policy (docs/PLAN-0.16.md) every download
///     keeps its provenance and the UI displays it.
/// </summary>
public sealed record NamCaptureManifest(
    long ToneId,
    long ModelId,
    string Title,
    string Author,
    string GearType,
    string Format,
    string SizeTier,
    string License,
    string LicenseUrl,
    string AttributionUrl,
    string FileName,
    string Sha256,
    long FileBytes,
    DateTimeOffset DownloadedUtc);

/// <summary>
///     Pure filtering over the local NAM-capture library (the "Library" view's
///     brain) plus the download file-naming rules. No I/O — unit-tested
///     directly (<c>NamCaptureIndexTests</c>); <see cref="NamCaptureLibrary"/>
///     is the impure shell.
/// </summary>
public static class NamCaptureIndex
{
    /// <summary>
    ///     Filters the library: a non-empty <paramref name="text"/> must appear
    ///     (case-insensitively) in the title or author; non-empty
    ///     <paramref name="gearType"/> / <paramref name="format"/> must match
    ///     exactly (case-insensitive). Order is preserved.
    /// </summary>
    public static IReadOnlyList<NamCaptureManifest> Filter(
        IReadOnlyList<NamCaptureManifest> all,
        string? text = null,
        string? gearType = null,
        string? format = null)
    {
        if (all is null || all.Count == 0) return [];

        IEnumerable<NamCaptureManifest> query = all;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var needle = text.Trim();
            query = query.Where(m =>
                (m.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Author?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(gearType))
            query = query.Where(m => string.Equals(m.GearType, gearType.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(format))
            query = query.Where(m => string.Equals(m.Format, format.Trim(), StringComparison.OrdinalIgnoreCase));

        return query.ToList();
    }

    /// <summary>Manifest sidecar suffix (capture file name + this).</summary>
    public const string ManifestSuffix = ".t3k.json";

    /// <summary>
    ///     Builds the local file name for a downloaded model. The remote title
    ///     is UNTRUSTED input: it is
    ///     slugged down to <c>[a-z0-9-]</c> so no path separator, traversal
    ///     segment, drive letter or invalid character can survive, and the
    ///     model id prefix keeps names unique + lookup-able. The EXTENSION is
    ///     load-bearing: NeuralAudio dispatches its loader by the file's
    ///     (lowercase, case-sensitive) extension, so a NAM stored as
    ///     <c>.bin</c>/<c>.NAM</c> fails to load and plays silently — see
    ///     <see cref="ExtensionFor"/>.
    /// </summary>
    public static string BuildFileName(
        long modelId, string? title, string? sizeTier, string? format, string? modelUrl = null)
    {
        var slug = Slug(title, maxLength: 60);
        var size = Slug(sizeTier, maxLength: 12);
        var ext = ExtensionFor(format, modelUrl);

        var sb = new StringBuilder();
        sb.Append(FileNamePrefix(modelId, title, sizeTier, format, modelUrl));
        if (slug.Length > 0) sb.Append('-').Append(slug);
        if (size.Length > 0) sb.Append('-').Append(size);
        sb.Append(ext);
        return sb.ToString();
    }

    /// <summary>
    ///     The unique part of a downloaded capture's file name.
    ///     <para>
    ///         It was <c>Math.Max(0, modelId)</c>, which collapses EVERY absent
    ///         or non-positive id onto the single prefix <c>0</c>: two captures
    ///         with no id and the same title and tier produced the same file
    ///         name, and the download path is <c>File.Exists ⇒ Delete ⇒ Move</c>
    ///         plus a sidecar rewrite — so the second one silently replaced the
    ///         first in the library with nothing said. The truncating 60-char
    ///         slug makes the collision easier still.
    ///     </para>
    ///     <para>
    ///         Without an id the only stable discriminator is the model's own
    ///         description, so an <c>x</c> plus a hash of it is the prefix. Same
    ///         model ⇒ same name (a re-download stays idempotent, which the
    ///         overwrite path depends on); different model ⇒ different name.
    ///     </para>
    /// </summary>
    internal static string FileNamePrefix(
        long modelId, string? title, string? sizeTier, string? format, string? modelUrl)
    {
        if (modelId > 0) return modelId.ToString(CultureInfo.InvariantCulture);

        var identity = string.Join('\u001f',
            (title ?? "").Trim().ToLowerInvariant(),
            (sizeTier ?? "").Trim().ToLowerInvariant(),
            (format ?? "").Trim().ToLowerInvariant(),
            (modelUrl ?? "").Trim().ToLowerInvariant());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "x" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>Canonical model format: <c>nam</c>, <c>aidax</c>, <c>ir</c>, or
    /// empty when unknown. TONE3000 uses several spellings
    /// (<c>aida-x</c>/<c>aida_x</c>/<c>keras</c>, <c>wav</c>/<c>impulse</c>).</summary>
    public static string NormalizeFormat(string? format)
    {
        var f = format?.Trim().ToLowerInvariant() ?? string.Empty;
        if (f.Length == 0) return string.Empty;
        if (f is "nam") return "nam";
        if (f.Contains("aida") || f is "aidax" or "keras" or "h5") return "aidax";
        if (f is "ir" or "wav" or "impulse" or "impulse-response" or "cab") return "ir";
        return string.Empty;
    }

    /// <summary>
    ///     Resolves the on-disk extension for a downloaded model. Order: the
    ///     declared <paramref name="format"/> (normalized) → the
    ///     <paramref name="modelUrl"/>'s own extension when it is a known neural
    ///     type → <c>.nam</c> as the safe default (the dominant format).
    ///     NEVER returns <c>.bin</c>: the neural loader can't read it, so a
    ///     mis-typed capture would be silently dead. Always lowercase.
    /// </summary>
    public static string ExtensionFor(string? format, string? modelUrl = null)
    {
        var norm = NormalizeFormat(format);
        if (norm.Length > 0) return ExtForNormalized(norm);

        // Fall back to the download URL's own extension when it is a neural type
        // (TONE3000 URLs commonly end in the real file name, e.g. .../model.nam).
        var urlExt = UrlExtension(modelUrl);
        if (urlExt is ".nam" or ".aidax" or ".wav") return urlExt;

        return ".nam";
    }

    private static string ExtForNormalized(string norm) => norm switch
    {
        "aidax" => ".aidax",
        "ir" => ".wav",
        _ => ".nam",
    };

    private static string UrlExtension(string? modelUrl)
    {
        if (string.IsNullOrWhiteSpace(modelUrl)) return string.Empty;
        var url = modelUrl.Trim();
        // Strip a query / fragment before reading the extension.
        var cut = url.IndexOfAny(['?', '#']);
        if (cut >= 0) url = url[..cut];
        try { return Path.GetExtension(url).ToLowerInvariant(); }
        catch { return string.Empty; }
    }

    /// <summary>The manifest sidecar name for a capture file name.</summary>
    public static string ManifestFileName(string captureFileName) => captureFileName + ManifestSuffix;

    private static string Slug(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var lastDash = true; // suppress a leading dash
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }

            if (sb.Length >= maxLength) break;
        }

        return sb.ToString().Trim('-');
    }
}
