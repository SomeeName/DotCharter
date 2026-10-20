using System.Globalization;
using System.Text.Json;

namespace DotCharter.Tone.Infrastructure.Json;

/// <summary>
///     Reads one field of a JSON object without throwing. Every method answers
///     "absent" (null, empty or false) when the element is not an object, the
///     field is missing, or it holds another JSON kind — a reply from a server
///     or a file is data, and a malformed one must not take the parser down.
///     Only the <c>…OrNumericText</c> methods also accept a number written as a
///     string, parsed in the invariant culture.
/// </summary>
public static class JsonFields
{
    public static string? String(JsonElement element, string name) => Field(element, name, JsonValueKind.String) is { } value ? value.GetString() : null;

    public static string StringOrEmpty(JsonElement element, string name) => String(element, name) ?? string.Empty;

    public static int? Int32(JsonElement element, string name)
    {
        return Field(element, name, JsonValueKind.Number) is { } value && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    public static int? Int32OrNumericText(JsonElement element, string name)
    {
        if (Int32(element, name) is { } number)
        {
            return number;
        }

        return String(element, name) is { } text &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static long? Int64(JsonElement element, string name)
    {
        return Field(element, name, JsonValueKind.Number) is { } value && value.TryGetInt64(out var number)
            ? number
            : null;
    }

    public static long? Int64OrNumericText(JsonElement element, string name)
    {
        if (Int64(element, name) is { } number)
        {
            return number;
        }

        return String(element, name) is { } text &&
               long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static double? Double(JsonElement element, string name)
    {
        return Field(element, name, JsonValueKind.Number) is { } value && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    public static bool IsTrue(JsonElement element, string name) => Field(element, name, JsonValueKind.True) is not null;

    private static JsonElement? Field(JsonElement element, string name, JsonValueKind kind)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == kind
            ? value
            : null;
    }
}
