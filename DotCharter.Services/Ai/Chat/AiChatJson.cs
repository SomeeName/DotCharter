using System.Globalization;
using System.Text.Json;

namespace DotCharter.Services.Ai.Chat;

/// <summary>
///     The conversation file format: one JSON object per conversation, read
///     STRICTLY — a missing field, a wrong type, an unknown role or an id that is
///     not <see cref="ProjectIdentity" />-shaped makes the whole file
///     <see cref="AiChatStatus.Corrupt" />, never a repaired guess.
/// </summary>
internal static class AiChatJson
{
    /// <summary>The format this build writes and reads.</summary>
    internal const int FormatVersion = 1;

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 16 };

    internal static byte[] Serialize(AiChatConversation conversation)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", FormatVersion);
            writer.WriteString("id", conversation.Id);
            writer.WriteString("chartId", conversation.ChartId);
            writer.WriteString("title", conversation.Title);
            writer.WriteString("created", FormatTime(conversation.CreatedUtc));
            writer.WriteString("updated", FormatTime(conversation.UpdatedUtc));
            writer.WriteString("model", conversation.ModelId);
            writer.WriteStartArray("turns");
            foreach (var turn in conversation.Turns) WriteTurn(writer, turn);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     Reads a conversation. <see cref="AiChatStatus.Ok" />,
    ///     <see cref="AiChatStatus.Corrupt" /> or <see cref="AiChatStatus.UnsupportedFormat" />;
    ///     whose chart it is, is the caller's check.
    /// </summary>
    internal static AiChatStatus TryRead(byte[] bytes, out AiChatConversation? conversation, out string? detail)
    {
        conversation = null;
        detail = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Corrupt("not a JSON object", out detail);
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number ||
                !format.TryGetInt32(out var version))
            {
                return Corrupt("no format version", out detail);
            }
            if (version > FormatVersion)
            {
                detail = $"format {version} (this build reads {FormatVersion})";
                return AiChatStatus.UnsupportedFormat;
            }

            if (version < 1) return Corrupt($"format {version}", out detail);

            if (!TryString(root, "id", out var id) || !ProjectIdentity.IsWellFormed(id))
                return Corrupt("no well-formed id", out detail);
            if (!TryString(root, "chartId", out var chartId) || !ProjectIdentity.IsWellFormed(chartId))
                return Corrupt("no well-formed chartId", out detail);
            if (!TryString(root, "title", out var title)) return Corrupt("no title", out detail);
            if (!TryTime(root, "created", out var created)) return Corrupt("no created time", out detail);
            if (!TryTime(root, "updated", out var updated)) return Corrupt("no updated time", out detail);
            if (!TryString(root, "model", out var model)) return Corrupt("no model", out detail);
            if (!root.TryGetProperty("turns", out var turnsElement) || turnsElement.ValueKind != JsonValueKind.Array)
                return Corrupt("no turns array", out detail);

            var turns = new List<AiChatTurn>(turnsElement.GetArrayLength());
            var index = 0;
            foreach (var element in turnsElement.EnumerateArray())
            {
                if (!TryReadTurn(element, out var turn)) return Corrupt($"turn {index} is malformed", out detail);
                turns.Add(turn!);
                index++;
            }

            conversation = new AiChatConversation(id, chartId, AiChatTitle.Normalize(title), created, updated, model,
                turns);
            return AiChatStatus.Ok;
        }
        catch (JsonException ex)
        {
            return Corrupt(ex.Message, out detail);
        }
    }

    private static AiChatStatus Corrupt(string why, out string? detail)
    {
        detail = why;
        return AiChatStatus.Corrupt;
    }

    private static void WriteTurn(Utf8JsonWriter writer, AiChatTurn turn)
    {
        writer.WriteStartObject();
        writer.WriteString("role", turn.Role == AiChatRole.User ? "user" : "assistant");
        writer.WriteString("text", turn.Text);
        writer.WriteString("at", FormatTime(turn.At));
        if (turn.Usage is { IsEmpty: false } usage)
        {
            if (usage.InputTokens is { } input) writer.WriteNumber("inputTokens", input);
            if (usage.OutputTokens is { } output) writer.WriteNumber("outputTokens", output);
            if (usage.CostUsd is { } cost) writer.WriteNumber("costUsd", cost);
        }

        if (!string.IsNullOrEmpty(turn.ReviewId)) writer.WriteString("reviewId", turn.ReviewId);
        writer.WriteEndObject();
    }

    private static bool TryReadTurn(JsonElement element, out AiChatTurn? turn)
    {
        turn = null;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!TryString(element, "role", out var roleText)) return false;
        AiChatRole role;
        switch (roleText)
        {
            case "user":
                role = AiChatRole.User;
                break;
            case "assistant":
                role = AiChatRole.Assistant;
                break;
            default:
                return false;
        }

        if (!TryString(element, "text", out var text) || !TryTime(element, "at", out var at)) return false;
        if (!TryOptionalInt(element, "inputTokens", out var input) ||
            !TryOptionalInt(element, "outputTokens", out var output))
        {
            return false;
        }

        decimal? cost = null;
        if (element.TryGetProperty("costUsd", out var costElement))
        {
            if (costElement.ValueKind != JsonValueKind.Number || !costElement.TryGetDecimal(out var c)) return false;
            cost = c;
        }

        string? reviewId = null;
        if (element.TryGetProperty("reviewId", out var reviewElement))
        {
            if (reviewElement.ValueKind != JsonValueKind.String) return false;
            reviewId = reviewElement.GetString();
        }

        var usage = new AiChatUsage(input, output, cost);
        turn = new AiChatTurn(role, text, at, usage.IsEmpty ? null : usage, reviewId);
        return true;
    }

    private static bool TryString(JsonElement owner, string name, out string value)
    {
        value = "";
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return true;
    }

    private static bool TryOptionalInt(JsonElement owner, string name, out int? value)
    {
        value = null;
        if (!owner.TryGetProperty(name, out var element)) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var parsed) || parsed < 0)
            return false;
        value = parsed;
        return true;
    }

    private static bool TryTime(JsonElement owner, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(owner, name, out var text) &&
               DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static string FormatTime(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
