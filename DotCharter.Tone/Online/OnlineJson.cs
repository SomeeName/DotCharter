using System.Globalization;
using System.Text.Json;
using DotCharter.Tone.Infrastructure.Json;

namespace DotCharter.Tone.Online;

/// <summary>
///     <para>
///         The tolerant JSON readers every HTTP client in this folder uses on a
///         response body. Missing, null and wrong-typed properties all fall back
///         instead of throwing, because a lenient client survives minor
///         server-side contract evolution — an older server that stops sending a
///         field must degrade a readout, never fault the call.
///     </para>
///     <para>
///         A private copy of this bank (~60 identical lines) or of
///         <see cref="ExtractErrorMessage" /> drifts.
///         Add a reader here rather than privately: a private copy compiles
///         clean, passes every test, and quietly becomes the one place a bug
///         gets fixed while the others keep it.
///     </para>
/// </summary>
internal static class OnlineJson
{
    /// <summary>
    ///     The error keys a TONE3000-style body uses, in preference order. A
    ///     server with a different set passes its own — which is why the keys are
    ///     a parameter at all.
    /// </summary>
    private static readonly string[] DefaultErrorKeys = ["error", "message", "detail", "title"];

    /// <summary>Non-empty string property, else <paramref name="fallback" />.</summary>
    internal static string GetString(JsonElement element, string name, string fallback = "") => JsonFields.String(element, name) is { Length: > 0 } value ? value : fallback;

    /// <summary>
    ///     Absent or non-boolean reads as false — an older server that does not
    ///     send <c>verified</c> must not have its rows shown as proved.
    /// </summary>
    internal static bool GetBool(JsonElement element, string name) => JsonFields.IsTrue(element, name);

    internal static int GetInt(JsonElement element, string name, int fallback = 0) => JsonFields.Int32(element, name) ?? fallback;

    internal static long GetLong(JsonElement element, string name) => JsonFields.Int64(element, name) ?? 0;

    internal static double GetDouble(JsonElement element, string name) => JsonFields.Double(element, name) ?? 0;

    /// <summary>
    ///     An ISO timestamp normalized to UTC; <see cref="DateTime.MinValue" />
    ///     when absent or unparseable. A naive timestamp is ASSUMED UTC — the
    ///     server speaks UTC and the client's local zone must not shift a row.
    /// </summary>
    internal static DateTime GetUtc(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(prop.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return DateTime.MinValue;
    }

    /// <summary>
    ///     The first non-blank string under <paramref name="keys" /> (default:
    ///     <c>error, message, detail, title</c>), or empty. A non-JSON body — a
    ///     proxy's HTML error page — yields empty rather than throwing, so the
    ///     caller falls back to its own status-code wording.
    /// </summary>
    internal static string ExtractErrorMessage(string? body, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var key in keys.Length == 0 ? DefaultErrorKeys : keys)
            {
                if (doc.RootElement.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var value = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON error page (proxy, HTML) — nothing useful to surface.
        }

        return string.Empty;
    }
}
