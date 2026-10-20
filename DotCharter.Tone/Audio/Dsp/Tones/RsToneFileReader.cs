using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>One tone read from a tone file: the project's key and name, and the Rocksmith tone JSON body.</summary>
public sealed record RsToneFileTone(string Key, string Name, string Json);

/// <summary>
///     Reads the tone files DLC Builder (Rocksmith2014.NET) writes and the Toolkit
///     shares: a Rocksmith tone JSON body (<c>*.tone2014.json</c>), the <c>Tone2014</c>
///     DataContract XML (<c>*.tone2014.xml</c>, either namespace) and a DLC Builder
///     project (<c>*.rs2dlc</c>, whose <c>Tones</c> array holds the same JSON bodies).
///     The format is told from the CONTENT, not the extension. A JSON body that already
///     has a valid key is kept verbatim (skins and all); XML and keyless bodies are
///     rebuilt through <see cref="RsToneJsonWriter" />. Pure and I/O-free; a file that
///     is not a tone file throws <see cref="FormatException" /> with a short reason.
/// </summary>
public static class RsToneFileReader
{
    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true };

    /// <param name="text">The file's text.</param>
    /// <param name="fallbackKey">The key for a tone that carries none (the file name without extensions).</param>
    public static IReadOnlyList<RsToneFileTone> Read(string text, string fallbackKey = "tone")
    {
        var trimmed = (text ?? "").TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.Length == 0) throw new FormatException("The file is empty.");
        var tones = trimmed[0] switch
        {
            '<' => ReadXmlFile(trimmed, fallbackKey),
            '{' or '[' => ReadJson(trimmed, fallbackKey),
            _ => throw new FormatException("The file is neither JSON nor XML.")
        };
        if (tones.Count == 0) throw new FormatException("The file holds no tones.");
        return tones;
    }

    /// <summary>The file name without its directory and tone/project extensions: the key a keyless tone falls back to.</summary>
    public static string FallbackKeyFor(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var suffix in new[] { ".tone2014.json", ".tone2014.xml", ".rs2dlc", ".json", ".xml" })
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            name = name[..^suffix.Length];
            break;
        }

        return RsToneAuthoring.SanitizeKey(name);
    }

    private static List<RsToneFileTone> ReadJson(string text, string fallbackKey)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The file is not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var result = new List<RsToneFileTone>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray()) AddBody(result, item, fallbackKey);
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("GearList", out _))
            {
                AddBody(result, root, fallbackKey);
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Tones", out var tones) &&
                     tones.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tones.EnumerateArray()) AddBody(result, item, fallbackKey);
            }
            else
            {
                throw new FormatException("The file has no tone (no GearList and no Tones list).");
            }

            return result;
        }
    }

    private static void AddBody(List<RsToneFileTone> into, JsonElement body, string fallbackKey)
    {
        if (body.ValueKind != JsonValueKind.Object) return;
        var raw = body.GetRawText();
        if (!RsToneJsonParser.TryParse(raw, out var tone) || tone is null) return;
        if (RsToneAuthoring.IsValidKey(tone.Key))
        {
            into.Add(new RsToneFileTone(tone.Key, tone.Name, raw));
            return;
        }

        // A keyless or odd key cannot be a slot name: rebuild the body under a valid one.
        var key = RsToneAuthoring.SanitizeKey(tone.Key.Length > 0 ? tone.Key : fallbackKey);
        var name = tone.Name.Length > 0 && tone.Name != tone.Key ? tone.Name : key;
        into.Add(new RsToneFileTone(key, name, RsToneJsonWriter.Write(tone with { Key = key, Name = name })));
    }

    private static List<RsToneFileTone> ReadXmlFile(string text, string fallbackKey) => [ReadXml(text, fallbackKey)];

    private static RsToneFileTone ReadXml(string text, string fallbackKey)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new FormatException("The file is not valid XML: " + ex.Message, ex);
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Tone2014") throw new FormatException("Not a Tone2014 XML file.");
        var gearList = Child(root, "GearList") ?? throw new FormatException("The tone has no GearList.");
        var amp = Pedal(gearList, "Amp") ?? throw new FormatException("The tone is missing an amp.");
        var cab = Pedal(gearList, "Cabinet") ?? throw new FormatException("The tone is missing a cabinet.");

        var key = Text(root, "Key");
        if (!RsToneAuthoring.IsValidKey(key)) key = RsToneAuthoring.SanitizeKey(key.Length > 0 ? key : fallbackKey);
        var name = Text(root, "Name");
        if (name.Length == 0) name = key;
        var descriptors = Child(root, "ToneDescriptors")?.Elements().Select(e => e.Value).ToList() ?? [];

        var tone = new RsTone(key, name, RsToneJsonParser.ParseVolume(Text(root, "Volume")), descriptors,
            new RsAmp(amp.Key, amp.Category, amp.Knobs), new RsCab(cab.Key, cab.Category, cab.Knobs),
            Slots(gearList, "PrePedal", RsGearRole.PrePedal), Slots(gearList, "Rack", RsGearRole.Rack),
            Slots(gearList, "PostPedal", RsGearRole.PostPedal))
        {
            SortOrder = float.TryParse(Text(root, "SortOrder"), NumberStyles.Float, CultureInfo.InvariantCulture, out var order)
                ? order
                : null
        };
        return new RsToneFileTone(key, name, RsToneJsonWriter.Write(tone));
    }

    private sealed record XmlPedal(RsGearType Type, string Key, string? Category, Dictionary<string, float> Knobs);

    private static List<RsPedal> Slots(XElement gearList, string prefix, RsGearRole role)
    {
        var result = new List<RsPedal>();
        for (var i = 1; i <= 4; i++)
        {
            if (Pedal(gearList, prefix + i.ToString(CultureInfo.InvariantCulture)) is not { } p) continue;
            result.Add(new RsPedal(role, i, p.Key, p.Type, p.Category, p.Knobs));
        }

        return result;
    }

    /// <summary>A pedal element, or null when it is absent or nil (<c>i:nil="true"</c>).</summary>
    private static XmlPedal? Pedal(XElement gearList, string slot)
    {
        var element = Child(gearList, slot);
        if (element is null || element.Attributes().Any(a => a.Name.LocalName == "nil" && a.Value == "true")) return null;
        var key = Text(element, "PedalKey");
        if (key.Length == 0) return null;
        var type = Text(element, "Type") switch
        {
            "Amps" => RsGearType.Amp,
            "Cabinets" => RsGearType.Cabinet,
            "Pedals" => RsGearType.Pedal,
            "Racks" => RsGearType.Rack,
            _ => RsGearType.Unknown
        };
        var category = Text(element, "Category");
        var knobs = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var entry in Child(element, "KnobValues")?.Elements() ?? [])
        {
            var knobKey = Text(entry, "Key");
            if (knobKey.Length > 0 && float.TryParse(Text(entry, "Value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                knobs[knobKey] = value;
        }

        return new XmlPedal(type, key, category.Length > 0 ? category : null, knobs);
    }

    private static XElement? Child(XElement parent, string localName) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string Text(XElement parent, string localName)
    {
        var child = Child(parent, localName);
        return child is null || child.Attributes().Any(a => a.Name.LocalName == "nil" && a.Value == "true") ? "" : child.Value.Trim();
    }
}
