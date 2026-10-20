using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Tone.Ai;

/// <summary>
///     The wire shape of a <see cref="ToneDraft" />: the schema the model is given,
///     the serializer both halves use, and the defensive extraction that turns
///     "here is your tone!\n```json\n{…}\n```" into parseable JSON.
///
///     <para>
///         <b>The schema is hand-written and lives next to the record on purpose.</b>
///         <c>ToneDesignPromptTests</c> compares it against
///         <see cref="ToneDraft" />'s and <see cref="ToneDraftModule" />'s reflected
///         properties, so a property added to either record and not to the schema
///         fails the gate. A generated schema would track the record automatically
///         but could not express the <c>description</c> strings that are half the
///         grounding.
///     </para>
///     <para>
///         <b>The schema stays inside the subset BOTH constrained decoders accept,
///         and that is what decides the shape of <c>params</c>.</b> The obvious
///         encoding of a param id → value map is
///         <c>"additionalProperties": { "type": "number" }</c>, and it is rejected
///         by both: Anthropic's structured outputs list
///         "<c>additionalProperties</c> set to anything other than <c>false</c>"
///         among the UNSUPPORTED features, and OpenAI's strict mode requires
///         <c>additionalProperties: false</c> on every object. So <c>params</c> is
///         a LIST of <c>{ id, value }</c> objects on the wire, converted back to
///         the dictionary <see cref="ToneDraftModule.Params" /> declares by
///         <c>ToneParamsConverter</c> in <see cref="Options" />. The RECORD is
///         unchanged — the few-shots, the validator and the eval all still see a
///         dictionary — and the converter still READS the old open-map form, which
///         is what a local model that ignores the schema keeps emitting.
///         <list type="bullet">
///             <item>Anthropic: https://platform.claude.com/docs/en/build-with-claude/structured-outputs</item>
///             <item>OpenAI: https://developers.openai.com/api/docs/guides/structured-outputs</item>
///         </list>
///     </para>
///     <para>
///         For the same reason the schema carries NO <c>minimum</c>/<c>maximum</c>
///         on <c>confidence</c> and no <c>minItems</c>/<c>maxItems</c> on
///         <c>modules</c>: both are outside the common subset (Anthropic supports
///         <c>minItems</c> for the values 0 and 1 only; OpenAI supports neither,
///         and lists <c>minimum</c>/<c>maximum</c> as unsupported type-specific
///         keywords). Nothing is lost — <see cref="ToneDraftValidator" /> clamps
///         every range and truncates the chain app-side anyway, and the bounds are
///         stated in the <c>description</c> strings, which the model reads.
///     </para>
/// </summary>
public static class ToneDraftJson
{
    /// <summary>The schema's name, passed to the provider's structured-output
    /// config (Claude's <c>OutputConfig.Format</c>, an OpenAI-compatible server's
    /// <c>response_format.json_schema.name</c>).</summary>
    public const string SchemaName = "tone_draft";

    /// <summary>Case-insensitive because an OpenAI-compatible server's JSON is
    /// whatever the local model emitted, and <c>Name</c> vs <c>name</c> must not
    /// be the difference between a tone and an error.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new ToneParamsConverter() }
    };

    private static readonly JsonSerializerOptions PrettyOptions = new(Options) { WriteIndented = true };

    /// <summary>The JSON Schema a <see cref="ToneDraft" /> reply must satisfy.
    /// Every object lists every property in <c>required</c> and carries
    /// <c>additionalProperties: false</c>, which both strict decoders demand; no
    /// keyword outside their common subset appears anywhere in it, which
    /// <c>AiProviderCompatTests</c> walks the whole document to prove.</summary>
    public static string Schema { get; } = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "rationale", "confidence", "modules", "captureSearch"],
          "properties": {
            "name": {
              "type": "string",
              "description": "Short evocative name for the tone, 2-4 words."
            },
            "rationale": {
              "type": "string",
              "description": "One or two sentences on why this chain suits the request."
            },
            "confidence": {
              "type": "number",
              "description": "How well this chain matches the request, from 0 to 1."
            },
            "modules": {
              "type": "array",
              "description": "The signal chain in order, input first. At least 1 module and at most 12.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["type", "params", "bypassed", "capture"],
                "properties": {
                  "type": {
                    "type": "string",
                    "description": "A module type id from the catalogue, exactly as listed."
                  },
                  "params": {
                    "type": "array",
                    "description": "The knobs to set: one entry per knob. Omit a knob to take its default.",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["id", "value"],
                      "properties": {
                        "id": {
                          "type": "string",
                          "description": "A param id listed for this module, exactly as listed."
                        },
                        "value": {
                          "type": "number",
                          "description": "The value, in that param's own printed unit and range."
                        }
                      }
                    }
                  },
                  "bypassed": {
                    "type": "boolean",
                    "description": "True to place the module switched off. Normally false."
                  },
                  "capture": {
                    "type": "string",
                    "description": "For namamp only: a capture id from the capture list, exactly as listed. Empty string for every other module."
                  }
                }
              }
            },
            "captureSearch": {
              "type": "string",
              "description": "Only when the rules allow it: a TONE3000 search for the amp a namamp with an empty capture should play. Empty string otherwise."
            }
          }
        }
        """;

    /// <summary>A draft as the few-shot examples render it: indented camelCase
    /// JSON, the same shape the model is asked to produce.</summary>
    public static string Serialize(ToneDraft draft) => JsonSerializer.Serialize(draft, PrettyOptions);

    /// <summary>
    ///     The JSON object inside <paramref name="reply" />, or null when there is
    ///     none.
    ///     <para>
    ///         Claude with structured output returns bare JSON and this is a no-op.
    ///         A local model behind an OpenAI-compatible endpoint is the reason the
    ///         method exists: Ollama's <c>/v1</c> accepts <c>response_format</c> and
    ///         may ignore it, so the reply arrives as prose around a fenced block,
    ///         or as JSON with a trailing "Hope that helps!".
    ///     </para>
    ///     <para>
    ///         The scan is BRACE-COUNTING and string-aware rather than
    ///         first-<c>{</c>-to-last-<c>}</c>, because a model that writes a
    ///         sentence containing a brace after the object would otherwise produce
    ///         a span that parses as nothing. It falls back to the naive span when
    ///         the counted one does not parse.
    ///     </para>
    /// </summary>
    public static string? ExtractJsonObject(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        var text = StripFences(reply);

        var start = text.IndexOf('{');
        if (start < 0) return null;

        var counted = CountedObject(text, start);
        if (counted is not null && LooksLikeJson(counted)) return counted;

        var end = text.LastIndexOf('}');
        if (end <= start) return null;
        var naive = text[start..(end + 1)];
        return LooksLikeJson(naive) ? naive : counted;
    }

    /// <summary>Removes markdown code fences (<c>```json</c> … <c>```</c>),
    /// keeping the fenced body when there is one.</summary>
    private static string StripFences(string text)
    {
        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence < 0) return text;

        // Skip the opening fence and its optional language tag.
        var bodyStart = text.IndexOf('\n', fence);
        if (bodyStart < 0) return text;
        bodyStart++;

        var close = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
        return close < 0 ? text[bodyStart..] : text[bodyStart..close];
    }

    /// <summary>The balanced object starting at <paramref name="start" />, ignoring
    /// braces inside strings and honouring backslash escapes.</summary>
    private static string? CountedObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    private static bool LooksLikeJson(string candidate)
    {
        try
        {
            using var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     <c>params</c> on the wire ⇄ <see cref="ToneDraftModule.Params" /> in the
    ///     record.
    ///
    ///     <para>
    ///         WRITES the strict-subset form — an array of <c>{ id, value }</c> —
    ///         because neither constrained decoder accepts an open
    ///         <c>additionalProperties</c> map (see the class remarks). READS both
    ///         that and the old object map, and that leniency is not tidiness: a
    ///         local model behind LM Studio or Ollama answers in whatever shape it
    ///         learned, and a tone the app could have used must not be lost to the
    ///         encoding it arrived in.
    ///     </para>
    ///     <para>
    ///         A malformed entry becomes <see cref="float.NaN" /> rather than being
    ///         dropped, so <see cref="ToneDraftValidator" /> reports it ("… was not
    ///         a number; the default N was used") instead of silently using a
    ///         default nobody was told about.
    ///     </para>
    /// </summary>
    private sealed class ToneParamsConverter : JsonConverter<IReadOnlyDictionary<string, float>>
    {
        public override IReadOnlyDictionary<string, float> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var map = new Dictionary<string, float>(StringComparer.Ordinal);

            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    ReadPairList(ref reader, map);
                    break;
                case JsonTokenType.StartObject:
                    ReadObjectMap(ref reader, map);
                    break;
                default:
                    // Null, or something that is not a param collection at all. An
                    // empty set is a module on its defaults, which is usable.
                    reader.Skip();
                    break;
            }

            return map;
        }

        public override void Write(
            Utf8JsonWriter writer, IReadOnlyDictionary<string, float> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var (id, number) in value)
            {
                // A non-finite value cannot be written as JSON at all, and an
                // example carrying one would teach the model a token the schema
                // forbids.
                if (string.IsNullOrWhiteSpace(id) || !float.IsFinite(number)) continue;
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteNumber("value", number);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void ReadPairList(ref Utf8JsonReader reader, Dictionary<string, float> map)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                string? id = null;
                var value = float.NaN;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString();
                    reader.Read();
                    if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
                        id = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    else if (string.Equals(name, "value", StringComparison.OrdinalIgnoreCase))
                        value = ReadNumber(ref reader);
                    else
                        reader.Skip();
                }

                if (!string.IsNullOrWhiteSpace(id)) map[id.Trim()] = value;
            }
        }

        private static void ReadObjectMap(ref Utf8JsonReader reader, Dictionary<string, float> map)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var id = reader.GetString();
                reader.Read();
                var value = ReadNumber(ref reader);
                if (!string.IsNullOrWhiteSpace(id)) map[id.Trim()] = value;
            }
        }

        /// <summary>The number at the reader, honouring the same
        /// number-from-string leniency <see cref="Options" /> asks for. Anything
        /// else is NaN, which the validator turns into a reported default.</summary>
        private static float ReadNumber(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.TryGetSingle(out var number) ? number : float.NaN;
                case JsonTokenType.String:
                    return float.TryParse(
                        reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : float.NaN;
                default:
                    reader.Skip();
                    return float.NaN;
            }
        }
    }

    /// <summary>Compact one-line rendering used when a prompt has to quote JSON
    /// inline (the current tone in a follow-up).</summary>
    public static string Compact(string json)
    {
        var sb = new StringBuilder(json.Length);
        foreach (var line in json.Split('\n'))
            sb.Append(line.Trim());
        return sb.ToString();
    }
}
