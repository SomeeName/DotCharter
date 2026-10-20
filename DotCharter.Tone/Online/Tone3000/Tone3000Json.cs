using System.Text.Json;
using DotCharter.Tone.Infrastructure.Json;

namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     Pure, schema-tolerant JSON mapping for the TONE3000 API responses. A
///     lenient client survives minor server-side contract evolution: every
///     getter tolerates missing/null/renamed properties (several plausible
///     property spellings are probed), a malformed array element is skipped
///     rather than failing the page, and nothing here throws except on
///     top-level non-JSON input (the client maps that to a typed error).
///     Public + pure so the parsers unit-test directly on JSON fixtures.
/// </summary>
public static class Tone3000Json
{
    /// <summary>Parses a search response. Accepts a root array or an object
    /// wrapping the list under <c>tones</c>/<c>data</c>/<c>results</c>/<c>items</c>.</summary>
    public static Tone3000SearchPage ParseSearchPage(string json, int requestedPage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var list = FindArray(root, "tones", "data", "results", "items");
        var tones = new List<Tone3000Tone>();
        if (list.HasValue)
        {
            foreach (var el in list.Value.EnumerateArray())
            {
                if (TryParseTone(el, out var tone))
                    tones.Add(tone);
            }
        }

        var page = GetInt(root, requestedPage, "page");
        var total = GetInt(root, 0, "total", "total_count", "count");
        return new Tone3000SearchPage(tones, page, total);
    }

    /// <summary>Parses a single-tone response body (either the tone object
    /// itself or wrapped under <c>tone</c>/<c>data</c>). Null when unrecognizable.</summary>
    public static Tone3000Tone? ParseToneBody(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var el = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("tone", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                el = wrapped;
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                el = data;
        }

        return TryParseTone(el, out var tone) ? tone : null;
    }

    /// <summary>Parses a model-list response (root array or wrapped under
    /// <c>models</c>/<c>data</c>/<c>results</c>/<c>items</c>).</summary>
    public static IReadOnlyList<Tone3000Model> ParseModels(string json, long toneId)
    {
        using var doc = JsonDocument.Parse(json);
        var list = FindArray(doc.RootElement, "models", "data", "results", "items");
        var models = new List<Tone3000Model>();
        if (!list.HasValue) return models;

        foreach (var el in list.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var url = GetString(el, "", "model_url", "url", "download_url", "file_url");
            if (url.Length == 0) continue; // no download link — useless entry

            models.Add(new Tone3000Model(
                GetLong(el, 0, "id", "model_id"),
                GetLong(el, toneId, "tone_id"),
                GetString(el, "", "name", "title", "model_name"),
                // The current API carries NO format on a model — it is a
                // property of the parent tone. Probed anyway (tolerance), but
                // an empty result means "unknown", not "not a NAM".
                GetString(el, "", "format", "file_format").ToLowerInvariant(),
                GetString(el, "", "size", "size_tier", "tier").ToLowerInvariant(),
                // `architecture_version` is the documented spelling ("1"/"2"/
                // "custom"); the bare `architecture` probes are tolerance only.
                GetString(el, "", "architecture_version", "architectureVersion", "architecture", "arch")
                    .ToLowerInvariant(),
                url,
                GetLong(el, 0, "file_size", "size_bytes", "bytes")));
        }

        return models;
    }

    /// <summary>Maps one tone object. False when the element carries no usable
    /// identity (id + title both absent).</summary>
    public static bool TryParseTone(JsonElement el, out Tone3000Tone tone)
    {
        tone = null!;
        if (el.ValueKind != JsonValueKind.Object) return false;

        var id = GetLong(el, 0, "id", "tone_id");
        var title = GetString(el, "", "title", "name");
        if (id <= 0 && title.Length == 0) return false;

        // Author may be a flat string or a nested user/author object.
        var author = GetString(el, "", "author", "username", "user_name");
        if (author.Length == 0)
        {
            foreach (var key in new[] { "user", "author", "owner" })
            {
                if (el.TryGetProperty(key, out var user) && user.ValueKind == JsonValueKind.Object)
                {
                    author = GetString(user, "", "username", "name", "display_name");
                    if (author.Length > 0) break;
                }
            }
        }

        // License may be a flat string or an object with name/url. The server has
        // used several spellings over time — probe the common ones (an empty
        // license reads as '' on the chip/manifest, which the owner hit).
        var license = GetString(el, "",
            "license", "licence", "license_name", "license_type", "licenseType",
            "license_id", "licenseId", "spdx_id", "spdx");
        var licenseUrl = GetString(el, "", "license_url", "licence_url", "licenseUrl");
        foreach (var licKey in new[] { "license", "licence", "licenseInfo", "license_info" })
        {
            if (el.TryGetProperty(licKey, out var lic) && lic.ValueKind == JsonValueKind.Object)
            {
                license = GetString(lic, license, "name", "title", "id", "code", "spdx_id", "spdx", "type");
                licenseUrl = GetString(lic, licenseUrl, "url", "link", "href");
                break;
            }
        }

        tone = new Tone3000Tone(
            id,
            title,
            author,
            // `gear` is the documented spelling; the rest are tolerance probes
            // (looking ONLY at those would leave every result's gear
            // chip blank).
            GetString(el, "", "gear", "gear_type", "type", "category").ToLowerInvariant(),
            license,
            licenseUrl,
            GetString(el, "", "url", "link", "page_url", "permalink"),
            GetString(el, "", "description", "summary"),
            GetStringList(el, "formats", "format"),
            GetStringList(el, "sizes", "size"),
            GetLong(el, 0, "downloads_count", "downloads", "download_count"),
            // Per-architecture model counts — the only place a TONE carries
            // architecture (a MODEL carries `architecture_version`). Absent on
            // older/partial payloads ⇒ 0, which reads as "unknown", never as a
            // reason to hide the row.
            GetInt(el, 0, "a1_models_count", "a1ModelsCount"),
            GetInt(el, 0, "a2_models_count", "a2ModelsCount"),
            GetInt(el, 0, "custom_models_count", "customModelsCount"));
        return true;
    }

    // ── tolerant getters ─────────────────────────────────────────────────────

    private static JsonElement? FindArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
                return prop;
        }

        return null;
    }

    private static string GetString(JsonElement el, string fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (JsonFields.String(el, name) is { Length: > 0 } value)
                return value;
        }

        return fallback;
    }

    private static long GetLong(JsonElement el, long fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (JsonFields.Int64OrNumericText(el, name) is { } value)
                return value;
        }

        return fallback;
    }

    private static int GetInt(JsonElement el, int fallback, params string[] names)
    {
        var value = GetLong(el, fallback, names);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : fallback;
    }

    /// <summary>Reads a string list from an array property (or folds a single
    /// string property into a one-element list). Never null.</summary>
    private static IReadOnlyList<string> GetStringList(JsonElement el, string arrayName, string scalarName)
    {
        if (el.ValueKind != JsonValueKind.Object) return [];
        if (el.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    list.Add(s.ToLowerInvariant());
            }

            return list;
        }

        var scalar = GetString(el, "", scalarName);
        return scalar.Length > 0 ? [scalar.ToLowerInvariant()] : [];
    }
}
