using System.Text.Json;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
///     Reads a model reply into an <see cref="AiChartReply" />, STRICTLY: the shape the
///     schema names and nothing else. A missing property, a property of the wrong kind or
///     one the schema never declared is a <see cref="AiChartRejectionCodes.Malformed" />
///     rejection of the whole chunk — there is no "best effort" here, because a reply the
///     decoder did not hold to the schema is a reply nothing else in it can be trusted for.
/// </summary>
public static class AiChartReplyReader
{
    /// <summary>Decodes <paramref name="json" /> for <paramref name="job" />.</summary>
    /// <param name="json">The reply text the provider returned.</param>
    /// <param name="job">The job the request was for.</param>
    /// <param name="reply">The decoded reply, when the answer is true.</param>
    /// <param name="reason">Why it was rejected, when the answer is false.</param>
    public static bool TryRead(string? json, AiChartJob job, out AiChartReply? reply, out string reason)
    {
        reply = null;
        reason = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "the reply was empty";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fingering = job.ProposesFingers();
            string[] top = job switch
            {
                AiChartJob.FhpAndFingering => ["anchors", "chords"],
                AiChartJob.FhpOnly => ["anchors"],
                _ => ["chords"]
            };
            if (!Closed(root, top, "the reply", out reason)) return false;

            List<AiChartReplyAnchor>? anchors = null;
            if (job.ProposesAnchors())
            {
                anchors = [];
                foreach (var a in root.GetProperty("anchors").EnumerateArray())
                {
                    if (!Closed(a, ["at", "fret", "width"], "an anchor", out reason)) return false;
                    if (!Str(a, "at", out var at) || !Int(a, "fret", out var fret) || !Int(a, "width", out var width))
                    {
                        reason = "an anchor has a field of the wrong type";
                        return false;
                    }

                    anchors.Add(new AiChartReplyAnchor(at, fret, width));
                }
            }

            var chords = new List<AiChartReplyChord>();
            if (!job.ListsChords())
            {
                reply = new AiChartReply(anchors, chords);
                return true;
            }

            string[] chordFields = fingering ? ["id", "ms", "frets", "fingers"] : ["id", "ms", "frets", "name"];
            foreach (var c in root.GetProperty("chords").EnumerateArray())
            {
                if (!Closed(c, chordFields, "a chord", out reason)) return false;
                if (!Str(c, "id", out var id) || !Int(c, "ms", out var ms) || !Ints(c, "frets", out var frets))
                {
                    reason = "a chord has a field of the wrong type";
                    return false;
                }

                List<int>? fingers = null;
                string? name = null;
                if (fingering && !Ints(c, "fingers", out fingers))
                {
                    reason = $"chord {id}: fingers is not a list of integers";
                    return false;
                }

                if (!fingering && !Str(c, "name", out name))
                {
                    reason = $"chord {id}: name is not a string";
                    return false;
                }

                chords.Add(new AiChartReplyChord(id, ms, frets, fingers, name));
            }

            reply = new AiChartReply(anchors, chords);
            return true;
        }
        catch (JsonException ex)
        {
            reason = "the reply is not JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>True when <paramref name="e" /> is an object with exactly
    /// <paramref name="fields" />, the arrays among them arrays.</summary>
    private static bool Closed(JsonElement e, string[] fields, string what, out string reason)
    {
        reason = "";
        if (e.ValueKind != JsonValueKind.Object)
        {
            reason = what + " is not an object";
            return false;
        }

        var seen = 0;
        foreach (var p in e.EnumerateObject())
        {
            if (Array.IndexOf(fields, p.Name) < 0)
            {
                reason = $"{what} has the undeclared field \"{p.Name}\"";
                return false;
            }

            seen++;
        }

        if (seen != fields.Length)
        {
            reason = $"{what} is missing a field (it needs {string.Join(", ", fields)})";
            return false;
        }

        foreach (var list in new[] { "anchors", "chords" })
        {
            if (Array.IndexOf(fields, list) >= 0 && e.GetProperty(list).ValueKind != JsonValueKind.Array)
            {
                reason = $"{what}: \"{list}\" is not a list";
                return false;
            }
        }

        return true;
    }

    private static bool Str(JsonElement e, string name, out string value)
    {
        var p = e.GetProperty(name);
        value = p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        return p.ValueKind == JsonValueKind.String;
    }

    private static bool Int(JsonElement e, string name, out int value)
    {
        value = 0;
        var p = e.GetProperty(name);
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value);
    }

    private static bool Ints(JsonElement e, string name, out List<int> values)
    {
        values = [];
        var p = e.GetProperty(name);
        if (p.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var v)) return false;
            values.Add(v);
        }

        return true;
    }
}
