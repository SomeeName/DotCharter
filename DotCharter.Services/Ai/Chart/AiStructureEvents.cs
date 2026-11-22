using System.Globalization;
using System.Text.Json;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.State;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
///     The fields of the Edit event / phrase dialog an <see cref="AiStructureOp.Event" /> change sets on
///     the marker at one beat. A null field is LEFT as the marker has it (the dialog's untouched
///     control); <see cref="Section" />, <see cref="Phrase" /> and <see cref="Tone" /> set to "" clear.
///     <see cref="Arrangements" /> is the dialog's "Apply to arrangements": always filled by the reader
///     (the model's names, else the run's set), so ONE change writes ONE marker edit on all of them.
/// </summary>
public sealed record AiEventEdit(
    string? Section,
    SectionStatus? Status,
    string? Phrase,
    bool? Solo,
    string? Tone,
    IReadOnlyList<string>? Events,
    IReadOnlyList<string> Arrangements);

/// <summary>The arrangements an event change may name and the ones it applies to when it names none.</summary>
public sealed record AiStructureEventScope(IReadOnlyList<string> Defaults, IReadOnlyList<string> All);

/// <summary>The live project an event change resolves its arrangement names and beat grid against.</summary>
public sealed record AiStructureWorld(IReadOnlyList<Arrangement> Arrangements, IList<BeatMarker> Beats);

/// <summary>
///     Event changes as the structure job writes them: <see cref="Append" /> reuses the dialog's own edit
///     path (<see cref="EventMarkerEdit.AppendActions" />) once per target arrangement, so the AI writes
///     exactly what the dialog would, into ONE composite; <see cref="TryRead" /> is the strict reader.
/// </summary>
public static class AiStructureEvents
{
    private static readonly string[] EventFields = ["section", "status", "phrase", "solo", "tone", "events", "arrangements"];

    /// <summary>Appends what the change writes on each of its arrangements; false when it writes nothing.</summary>
    public static bool Append(AiStructureChange change, AiStructureWorld? world, List<IChartAction> actions)
    {
        if (change.Event is not { } edit || world is null || change.To is not { } position) return false;
        var timeMs = position.GetPositionMs(world.Beats);
        var before = actions.Count;
        foreach (var name in edit.Arrangements)
        {
            var arrangement = world.Arrangements.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
            if (arrangement is null) continue;
            EventMarkerEdit.AppendActions(arrangement, position, timeMs, world.Beats, FieldsFor(arrangement, edit, timeMs, world.Beats),
                actions);
        }

        return actions.Count > before && new CompositeAction([.. actions.Skip(before)]).HasEffect;
    }

    /// <summary>The marker the dialog would save: the arrangement's current fields under the edit's set ones.</summary>
    private static EventMarkerFields FieldsFor(Arrangement arrangement, AiEventEdit edit, double timeMs, IList<BeatMarker> beats)
    {
        var existing = EventMarkerEdit.EventAt(arrangement, timeMs, beats);
        var tone = EventMarkerEdit.NearestTone(arrangement.ToneChanges, timeMs, beats);
        var section = edit.Section ?? existing?.Section ?? "";
        var phrase = edit.Phrase ?? existing?.Phrase ?? "";
        var solo = edit.Solo ?? (phrase.Length > 0 && arrangement.PhraseInfos.TryGetValue(phrase, out var info) && info.Solo);
        // Codes outside the catalogue belong to whoever authored the chart: kept, like the dialog keeps them.
        var events = edit.Events is null
            ? existing?.Events ?? ""
            : RsEventCatalog.Join([.. edit.Events, .. RsEventCatalog.Parse(existing?.Events).Where(c => !RsEventCatalog.IsKnown(c))]);
        return new EventMarkerFields(section, phrase, edit.Status ?? existing?.Status ?? SectionStatus.None, events, solo,
            edit.Tone ?? tone?.ToneName ?? "");
    }

    /// <summary>The marker a review shows for an event change: only the section and phrase it names.</summary>
    public static EventPoint? Shown(AiStructureChange change) => change is { Event: { } e, To: { } at } &&
        (!string.IsNullOrEmpty(e.Section) || !string.IsNullOrEmpty(e.Phrase))
        ? new EventPoint { Position = at, Section = e.Section ?? "", Phrase = e.Phrase ?? "" }
        : null;

    /// <summary>Reads one <c>op: "event"</c> change; false with the rejection's code and reason.</summary>
    public static bool TryRead(JsonElement item, string at, int? bar, int? beat, AiStructureContext context,
        out AiStructureChange? change, out string why, out string code)
    {
        change = null;
        why = "";
        code = AiChartRejectionCodes.NotStructure;
        if (context.Events is not { } scope)
        {
            why = at + ": an event marker edit is not allowed in this request";
            return false;
        }

        code = AiChartRejectionCodes.Malformed;
        if (!item.TryGetProperty("event", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            why = at + ": an event change needs an \"event\" object";
            return false;
        }

        foreach (var p in body.EnumerateObject())
        {
            if (Array.IndexOf(EventFields, p.Name) >= 0) continue;
            code = AiChartRejectionCodes.NotStructure;
            why = $"{at} has event field \"{p.Name}\": only the event dialog's fields may be set";
            return false;
        }

        if (bar is not { } barNumber)
        {
            why = at + " has no bar";
            return false;
        }

        var beatNumber = beat ?? 1;
        if (context.Grid.At(barNumber, beatNumber) is not { } position)
        {
            code = AiChartRejectionCodes.OffGrid;
            why = string.Create(CultureInfo.InvariantCulture, $"{at}: there is no bar {barNumber} beat {beatNumber}");
            return false;
        }

        if (!context.InScope(position))
        {
            code = AiChartRejectionCodes.OutOfScope;
            why = $"{at}: that position is outside the scope";
            return false;
        }

        if (!TryFields(body, at, beatNumber, scope, out var edit, out why, out code)) return false;
        change = edit is null ? null : new AiStructureChange(AiStructureOp.Event, AiStructureKind.Phrase, null, null, position, null)
        {
            Event = edit
        };
        return true;
    }

    private static bool TryFields(JsonElement body, string at, int beat, AiStructureEventScope scope, out AiEventEdit? edit,
        out string why, out string code)
    {
        edit = null;
        code = AiChartRejectionCodes.Malformed;
        if (!Str(body, "section", out var section) || !Str(body, "phrase", out var phrase) || !Str(body, "tone", out var tone) ||
            !Str(body, "status", out var statusText))
        {
            why = at + " has an event field of the wrong type";
            return false;
        }

        if (!TrySection(ref section, beat, at, out why, out code) || !TryPhrase(ref phrase, at, out why, out code) ||
            !TryStatus(statusText, at, out var status, out why) || !TrySolo(body, at, out var solo, out why) ||
            !TryTone(ref tone, at, out why) || !Names(body, "events", at, out var events, out why) ||
            !Names(body, "arrangements", at, out var named, out why))
        {
            return false;
        }

        if (events?.FirstOrDefault(e => !RsEventCatalog.IsKnown(e)) is { } unknown)
        {
            why = $"{at}: \"{unknown}\" is not a Rocksmith event code";
            return false;
        }

        if (named?.FirstOrDefault(n => !scope.All.Contains(n, StringComparer.Ordinal)) is { } absent)
        {
            code = AiChartRejectionCodes.UnknownMarker;
            why = $"{at}: \"{absent}\" is not an arrangement of this song";
            return false;
        }

        if (section is null && phrase is null && tone is null && status is null && solo is null && events is null) return true;
        edit = new AiEventEdit(section, status, phrase, solo, tone, events, named is { Count: > 0 } ? named : scope.Defaults);
        return true;
    }

    /// <summary>A section: a Rocksmith name (on a bar line), "" to clear, null to keep.</summary>
    private static bool TrySection(ref string? section, int beat, string at, out string why, out string code)
    {
        why = "";
        code = AiChartRejectionCodes.SectionName;
        if (section is null) return true;
        if (section.Trim().Length == 0)
        {
            section = "";
            return true;
        }

        section = RsSectionCatalog.Normalize(section);
        if (section is null)
        {
            why = $"{at}: the section is not a Rocksmith section name";
            return false;
        }

        if (beat == 1) return true;
        code = AiChartRejectionCodes.OffGrid;
        why = $"{at}: a section starts on a bar line, not on beat {beat}";
        return false;
    }

    private static bool TryPhrase(ref string? phrase, string at, out string why, out string code)
    {
        why = "";
        code = AiChartRejectionCodes.PhraseName;
        phrase = phrase?.Trim();
        if (string.IsNullOrEmpty(phrase) || RsPhraseName.IsLegal(phrase)) return true;
        why = $"{at}: \"{phrase}\" is not a phrase name Rocksmith keeps (letters A-Z, digits, spaces, _ and # only)";
        return false;
    }

    private static bool TryStatus(string? text, string at, out SectionStatus? status, out string why)
    {
        status = null;
        why = "";
        if (text is null) return true;
        if (Enum.TryParse<SectionStatus>(text.Trim(), true, out var parsed) && Enum.IsDefined(parsed))
        {
            status = parsed;
            return true;
        }

        why = $"{at}: \"{text}\" is not a status (none, todo, draft, review, done)";
        return false;
    }

    private static bool TrySolo(JsonElement body, string at, out bool? solo, out string why)
    {
        solo = null;
        why = "";
        if (!body.TryGetProperty("solo", out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            solo = value.GetBoolean();
            return true;
        }

        why = at + ": solo is not true or false";
        return false;
    }

    private static bool TryTone(ref string? tone, string at, out string why)
    {
        why = "";
        tone = tone?.Trim();
        if (tone is null || tone.Length <= MaxToneName && !tone.Any(char.IsControl)) return true;
        why = $"{at}: the tone name is not a tone name";
        return false;
    }

    private const int MaxToneName = 64;

    private static bool Str(JsonElement e, string name, out string? value)
    {
        value = null;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return true;
        if (p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString();
        return true;
    }

    /// <summary>An optional list of distinct non-blank strings; null when absent or null.</summary>
    private static bool Names(JsonElement e, string name, string at, out List<string>? values, out string why)
    {
        values = null;
        why = "";
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return true;
        if (p.ValueKind != JsonValueKind.Array)
        {
            why = $"{at}: {name} is not a list";
            return false;
        }

        values = [];
        foreach (var v in p.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String)
            {
                why = $"{at}: {name} holds something that is not text";
                return false;
            }

            var text = v.GetString()?.Trim() ?? "";
            if (text.Length > 0 && !values.Contains(text, StringComparer.Ordinal)) values.Add(text);
        }

        return true;
    }
}
