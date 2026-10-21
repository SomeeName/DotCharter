using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     Reads a model's JSON reply into an <see cref="RsTone" />, and <b>rejects
///     anything the game does not have</b>.
///     <para>
///         <b>Reject, never repair — and that is the difference between this reader
///         and the tone-DESIGN validator beside it.</b> `ToneDraftValidator` drops an
///         unknown module, clamps an out-of-range value and reports both as WARNINGS,
///         which is right for the app's own DSP rig: the rig is ours, a dropped
///         module is a slightly different tone, and the user is looking at the result
///         in an editor. This output goes into a Rocksmith package: a gear key the
///         game does not have is a tone the game will not load, and a knob past its
///         stop means the model misread the unit, not that it wanted the maximum.
///     </para>
///     <para>
///         Every reply goes through <see cref="RsToneCatalogValidation" /> before it
///         is returned, so a caller cannot obtain an unvalidated tone from here.
///     </para>
/// </summary>
public static class RsToneReplyReader
{
    /// <summary>
    ///     Parses and validates a reply.
    /// </summary>
    /// <param name="json">The model's reply. Markdown fences and surrounding prose
    /// are tolerated — <see cref="ToneDraftJson.ExtractJsonObject" /> does that job
    /// for the design lane and it is the same failure mode here.</param>
    /// <param name="key">The tone key the caller assigned. The reply does not get to
    /// choose it: a key has to be unique within the project and the model cannot know
    /// what else is in it.</param>
    /// <param name="fallbackName">Used when the reply carries no name.</param>
    /// <param name="catalog">The gear catalogue to validate against.</param>
    /// <param name="rationale">The model's own one-line reason, or empty.</param>
    /// <exception cref="RsToneCatalogException">The reply is unreadable, or it names
    /// gear or a value the game does not have.</exception>
    public static RsTone Read(
        string? json, string key, string fallbackName, RsGearCatalog catalog, out string rationale)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        rationale = string.Empty;

        var body = ToneDraftJson.ExtractJsonObject(json);
        if (string.IsNullOrWhiteSpace(body))
            throw Fault("the reply carried no JSON object", key);

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body!);
            root = document.RootElement;
        }
        catch (JsonException ex)
        {
            throw Fault("the reply was not valid JSON: " + ex.Message, key);
        }

        using (document)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw Fault("the reply was not a JSON object", key);

            // A name and a reason are a model's words: one capped line each, because both reach the
            // tone list, the confirmation, the status rail and the log.
            rationale = AiUntrustedText.Clean(Text(root, "rationale"), AiUntrustedText.MaxRationaleLength);
            var name = AiUntrustedText.Clean(Text(root, "name"), AiUntrustedText.MaxNameLength);
            if (name.Length == 0) name = fallbackName;

            var volume = root.TryGetProperty("volumeDb", out var v) && v.ValueKind == JsonValueKind.Number
                ? (float)v.GetDouble()
                : RsToneAuthoring.DefaultVolumeDb;

            var descriptors = Descriptors(root);

            var (ampKey, ampKnobs) = Gear(root, "amp", key)
                ?? throw Fault("the reply named no amp, and every Rocksmith tone has one", key);
            var (cabKey, cabKnobs) = Gear(root, "cabinet", key)
                ?? throw Fault("the reply named no cabinet, and every Rocksmith tone has one", key);

            var tone = new RsTone(
                key,
                name,
                volume,
                descriptors,
                new RsAmp(ampKey, catalog.GetCategory(ampKey), ampKnobs),
                new RsCab(cabKey, CabinetCategory(catalog, cabKey), cabKnobs),
                Rail(root, "prePedals", RsGearRole.PrePedal, RsGearType.Pedal, catalog, key),
                Rail(root, "racks", RsGearRole.Rack, RsGearType.Rack, catalog, key),
                Rail(root, "postPedals", RsGearRole.PostPedal, RsGearType.Pedal, catalog, key));

            // THE gate. Nothing leaves this method unvalidated.
            return RsToneCatalogValidation.Require(tone, catalog, $"the generated tone '{key}'");
        }
    }

    /// <summary>
    ///     A cabinet's routing category in the form the FILE uses, not the form the
    ///     catalogue displays. The catalogue's own value for
    ///     <c>Cab_OrangePPC212OB_Ribbon_Cone</c> is <c>"Ribbon Cone"</c>, but every
    ///     tone body written by Rocksmith and by DLC Builder spells it
    ///     <c>"Ribbon_Cone"</c> — it is the mic/position suffix of the gear key. The
    ///     space is the display separator; underscores are what the tone object
    ///     carries. Nothing is invented: only the separator differs.
    /// </summary>
    private static string? CabinetCategory(RsGearCatalog catalog, string cabinetKey)
        => catalog.GetCategory(cabinetKey)?.Replace(' ', '_');

    private static IReadOnlyList<string> Descriptors(JsonElement root)
    {
        if (!root.TryGetProperty("descriptors", out var array) || array.ValueKind != JsonValueKind.Array)
            return [RsToneAuthoring.CleanDescriptorUIName];

        var names = new List<string>(4);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // The model is asked for the DISPLAY name ("Clean"); the manifest stores
            // the loc-id form ("$[35720]CLEAN"). Accept either, and translate here so
            // the model is never asked to reproduce a loc id it could mistype.
            var option = RsToneAuthoring.Descriptors
                             .FirstOrDefault(d => string.Equals(d.Name, raw, StringComparison.OrdinalIgnoreCase))
                         ?? RsToneAuthoring.FindDescriptor(raw);

            // An unknown descriptor is carried through VERBATIM so the validator is
            // the thing that rejects it — translating silently to Clean would hide
            // exactly the fault this lane refuses to hide.
            names.Add(option?.UIName ?? raw!);
            if (names.Count == 4) break;
        }

        return names.Count > 0 ? names : [RsToneAuthoring.CleanDescriptorUIName];
    }

    private static IReadOnlyList<RsPedal> Rail(
        JsonElement root, string property, RsGearRole role, RsGearType type, RsGearCatalog catalog, string key)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var rail = new List<RsPedal>(4);
        var slot = 1;
        foreach (var item in array.EnumerateArray())
        {
            if (GearFrom(item, key) is not var (gearKey, knobs)) continue;
            rail.Add(new RsPedal(role, slot++, gearKey, type, catalog.GetCategory(gearKey), knobs));
        }

        return rail;
    }

    private static (string Key, IReadOnlyDictionary<string, float> Knobs)? Gear(
        JsonElement root, string property, string toneKey)
        => root.TryGetProperty(property, out var element) ? GearFrom(element, toneKey) : null;

    private static (string Key, IReadOnlyDictionary<string, float> Knobs)? GearFrom(
        JsonElement element, string toneKey)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var key = Text(element, "key");
        if (string.IsNullOrWhiteSpace(key)) return null;

        var knobs = new Dictionary<string, float>(StringComparer.Ordinal);
        if (element.TryGetProperty("knobs", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var knob in array.EnumerateArray())
            {
                if (knob.ValueKind != JsonValueKind.Object) continue;
                var pot = Text(knob, "pot");
                if (string.IsNullOrWhiteSpace(pot)) continue;
                if (!knob.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number)
                    throw Fault($"'{key}_{pot}' has no numeric value", toneKey);

                // The model names the POT; the tone stores the FULL key. Composing it
                // here is what lets the validator's "is this a knob that gear has"
                // check be a plain lookup.
                knobs[key + "_" + pot] = (float)value.GetDouble();
            }
        }

        return (key!, knobs);
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static RsToneCatalogException Fault(string message, string key) =>
        new(new RsToneFault("unreadable-reply", null, null, message), $"the generated tone '{key}'");
}
