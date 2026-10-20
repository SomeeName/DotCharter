using System.Text.Json;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The gear catalogue (<c>tone_gear_data.json</c>) may carry game IDENTIFIERS — keys,
///     short display names, categories, knob ranges — but never game TEXT: descriptions,
///     UI strings or lore (#823). It lists no prose today, so any field outside the known
///     set, or any string that reads as a sentence, is a copy of something the game wrote.
/// </summary>
public sealed class RsGearDataProseLintTests
{
    private const string GearData = "DotCharter.Tone/Audio/Dsp/Tones/tone_gear_data.json";
    private const int MaxWords = 4;
    private const int MaxLength = 40;

    private static readonly string[] GearFields = ["Name", "Type", "Category", "Key", "Knobs"];

    private static readonly string[] KnobFields = ["Name", "Key", "UnitType", "MinValue", "MaxValue", "ValueStep", "DefaultValue", "EnumValues"];

    [Fact]
    public void TheGearCatalogue_HoldsIdentifiersOnly_NoGameText()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), GearData);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        // A lint that reads no entries passes vacuously.
        Assert.True(doc.RootElement.GetArrayLength() > 100,
            $"{GearData} holds only {doc.RootElement.GetArrayLength()} entries — the lint lost its subject; check the path and the file's shape.");

        var hits = Violations(doc.RootElement);

        Assert.True(hits.Count == 0, LintReport.Of(
            $"The gear catalogue ({GearData}) holds text beyond identifiers. Game descriptions and UI strings stay out of the tree (#823):",
            hits,
            $"delete the field, or shorten the value to an identifier (allowed fields: {string.Join(", ", GearFields)}; " +
            $"knob fields: {string.Join(", ", KnobFields)}; at most {MaxLength} characters).",
            "LEGAL.md (Game identifiers)"));
    }

    /// <summary>The probe twin, on INVENTED entries: a new field and a sentence are caught, plain identifiers are not.</summary>
    [Fact]
    public void TheRules_CatchProse_AndPassIdentifiers()
    {
        Assert.Empty(Violations(Parse("""[{"Name":"X-1","Type":"Pedals","Category":"Pedal","Key":"Pedal_X1","Knobs":[{"Name":"Gain","Key":"K","UnitType":"number","MinValue":0,"MaxValue":1,"ValueStep":1,"DefaultValue":0}]}]""")));
        Assert.NotEmpty(Violations(Parse("""[{"Name":"X-1","Type":"Pedals","Category":"Pedal","Key":"Pedal_X1","Description":"x"}]""")));
        Assert.NotEmpty(Violations(Parse("""[{"Name":"A warm and rounded drive that loves single coils","Type":"Pedals","Category":"Pedal","Key":"K"}]""")));
        Assert.NotEmpty(Violations(Parse("""[{"Name":"X","Type":"T","Category":"C","Key":"K","Knobs":[{"Name":"Tip","Tooltip":"y"}]}]""")));
        Assert.Empty(Violations(Parse("""[{"Name":"X","Type":"T","Category":"C","Key":"K","Knobs":[{"Name":"Sw","EnumValues":["Off","On"]}]}]""")));
        Assert.NotEmpty(Violations(Parse("""[{"Name":"X","Type":"T","Category":"C","Key":"K","Knobs":[{"Name":"Sw","EnumValues":["Off","A long line of flavour text about it"]}]}]""")));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<string> Violations(JsonElement entries)
    {
        var hits = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            var key = entry.TryGetProperty("Key", out var k) ? k.ToString() : "?";
            Check(entry, GearFields, key, hits);
            if (!entry.TryGetProperty("Knobs", out var knobs)) continue;
            foreach (var knob in knobs.EnumerateArray()) Check(knob, KnobFields, key, hits);
        }

        return hits;
    }

    private static void Check(JsonElement element, string[] allowed, string key, List<string> hits)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                hits.Add($"{key}: unexpected field '{property.Name}'");
                continue;
            }

            List<JsonElement> strings = property.Value.ValueKind == JsonValueKind.Array
                ? [.. property.Value.EnumerateArray()]
                : [property.Value];
            if (strings.Any(v => v.ValueKind == JsonValueKind.String && IsProse(v.GetString()!)))
                hits.Add($"{key}: '{property.Name}' reads as text");
        }
    }

    private static bool IsProse(string value) => value.Length > MaxLength || value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxWords;
}
