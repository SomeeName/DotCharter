using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DotCharter.Tone.Infrastructure.Json;

/// <summary>
///     Per-property tolerant deserialization for user-owned settings files. One
///     value this build cannot read (an enum name from a newer build, a wrong JSON
///     type) leaves THAT property at its default instead of failing the whole
///     document, so only content that is not JSON at all reaches the quarantine.
///     Each skipped property is recorded; the caller drains and logs them with
///     <see cref="TakeWarnings" />.
/// </summary>
public static class TolerantJson
{
    [ThreadStatic] private static List<string>? _warnings;
    [ThreadStatic] private static bool _skipNextSet;

    /// <summary>Makes <paramref name="options" /> tolerant per property.</summary>
    public static JsonSerializerOptions Apply(JsonSerializerOptions options)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(Tolerate);
        options.TypeInfoResolver = resolver;
        return options;
    }

    /// <summary>Returns and clears the warnings of the last deserialize on this thread.</summary>
    public static IReadOnlyList<string> TakeWarnings()
    {
        var taken = _warnings ?? [];
        _warnings = null;
        _skipNextSet = false;
        return taken;
    }

    private static void Tolerate(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;

        foreach (var prop in info.Properties)
        {
            if (prop.Set == null || prop.CustomConverter != null || prop.IsExtensionData) continue;

            var converter = (JsonConverter)Activator.CreateInstance(
                typeof(TolerantConverter<>).MakeGenericType(prop.PropertyType), prop.Name)!;
            prop.CustomConverter = converter;

            var original = prop.Set;
            prop.Set = (owner, value) =>
            {
                if (_skipNextSet)
                {
                    _skipNextSet = false;
                    return;
                }

                original(owner, value);
            };
        }
    }

    private sealed class TolerantConverter<T>(string name) : JsonConverter<T>
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var inner = (JsonConverter<T>)options.GetConverter(typeof(T));
            var probe = reader;
            try
            {
                var value = inner.Read(ref probe, typeToConvert, options);
                reader = probe;
                return value;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                var text = reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray
                    ? reader.TokenType.ToString()
                    : Describe(reader);
                reader.Skip();
                (_warnings ??= []).Add($"'{name}' has a value this build cannot read ({text}); using its default.");
                _skipNextSet = true;
                return default;
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => ((JsonConverter<T>)options.GetConverter(typeof(T))).Write(writer, value, options);

        private static string Describe(Utf8JsonReader reader)
        {
            try
            {
                return reader.TokenType == JsonTokenType.String ? $"\"{reader.GetString()}\"" : reader.TokenType.ToString();
            }
            catch (InvalidOperationException)
            {
                return reader.TokenType.ToString();
            }
        }
    }
}
