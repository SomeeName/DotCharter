using System.Globalization;
using System.Text.Json;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
/// What the structure job works on: the beat grid, every section and phrase marker
///     with its id, and the scope (inclusive; null = the song's edge).
/// </summary>
public sealed class AiStructureContext
{
    private AiStructureContext(AiStructureGrid grid, IReadOnlyList<AiStructureMarker> markers,
        FractionalPosition? from, FractionalPosition? to, AiStructureEventScope? events)
    {
        Events = events;
        Grid = grid;
        Markers = markers;
        From = from;
        To = to;
    }

    public AiStructureGrid Grid { get; }

    /// <summary>The arrangements event changes may name, or null where the request allows no event change.</summary>
    public AiStructureEventScope? Events { get; }

    /// <summary>Sections then phrases, each in song order.</summary>
    public IReadOnlyList<AiStructureMarker> Markers { get; }

    public FractionalPosition? From { get; }

    public FractionalPosition? To { get; }

    /// <summary>
    ///     The markers of <paramref name="arrangement" />: every section and phrase on its
    ///     <see cref="Arrangement.EventPoints" /> (editable), plus every phrase only the imported
    ///     list has (<see cref="PhraseMarkers.All" /> reads both stores; those are [fixed]).
    /// </summary>
    public static AiStructureContext Of(Arrangement arrangement, IList<BeatMarker> beats, FractionalPosition? from,
        FractionalPosition? to, AiStructureEventScope? events = null, IReadOnlyList<Arrangement>? project = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(beats);
        var markers = new List<AiStructureMarker>();
        var sections = arrangement.EventPoints.Where(e => !string.IsNullOrWhiteSpace(e.Section))
            .OrderBy(e => e.Position).ToList();
        for (var i = 0; i < sections.Count; i++)
        {
            var notes = AiStructureMarkerNotes.Of(arrangement, sections[i].Position, sections[i], beats, project, ownsPosition: true);
            markers.Add(new AiStructureMarker(string.Create(CultureInfo.InvariantCulture, $"s{i + 1}"),
                AiStructureKind.Section, sections[i].Position, sections[i].Section, Editable: true)
            {
                Notes = notes
            });
        }

        var authored = arrangement.EventPoints.Where(e => !string.IsNullOrWhiteSpace(e.Phrase))
            .Select(e => e.Position).ToHashSet();
        var phrases = PhraseMarkers.All(arrangement);
        for (var i = 0; i < phrases.Count; i++)
        {
            // PhraseMarkers.All trims; the guard compares the live field trimmed too.
            var position = phrases[i].Position;
            var point = FindPoint(arrangement, position, beats);
            var notes = AiStructureMarkerNotes.Of(arrangement, position, point, beats, project,
                ownsPosition: !sections.Any(e => e.Position == position), phrases[i].Name);
            markers.Add(new AiStructureMarker(string.Create(CultureInfo.InvariantCulture, $"p{i + 1}"),
                AiStructureKind.Phrase, position, phrases[i].Name, authored.Contains(position))
            {
                Notes = notes
            });
        }

        return new AiStructureContext(AiStructureGrid.Of(beats), markers, from, to, events);
    }

    private static EventPoint? FindPoint(Arrangement arrangement, FractionalPosition position, IList<BeatMarker> beats)
        => EventMarkerEdit.EventAt(arrangement, position.GetPositionMs(beats), beats);

    /// <summary>True when <paramref name="position" /> lies inside the scope.</summary>
    public bool InScope(FractionalPosition position)
        => (From is not { } from || position >= from) && (To is not { } to || position <= to);
}

/// <summary>
/// Reads a structure reply STRICTLY and checks every change against the grid, the
///     vocabulary, the markers and the scope. Like <see cref="AiChartReplyReader" />, there is no
///     best effort: ONE unknown field, operation or kind — a note, a fret, an anchor — refuses the
///     whole reply (<see cref="AiChartRejectionCodes.NotStructure" />), and so does one bad name or
///     position. The chart checks (<see cref="AiStructureGate" />) then judge each change alone.
/// </summary>
public static class AiStructureReply
{
    /// <summary>The most changes one reply may carry.</summary>
    public const int MaxChanges = 200;

    private static readonly string[] ChangeFields = ["op", "kind", "id", "bar", "beat", "name", "event"];

    /// <summary>Decodes and validates <paramref name="json" />.</summary>
    /// <param name="json">The reply.</param>
    /// <param name="context">The grid, the markers and the scope the prompt listed.</param>
    /// <param name="changes">The changes, in reply order, when the answer is true.</param>
    /// <param name="rejection">Why the reply was refused, when the answer is false.</param>
    public static bool TryRead(string? json, AiStructureContext context, out IReadOnlyList<AiStructureChange> changes,
        out AiChartRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(context);
        changes = [];
        rejection = null;
        if (string.IsNullOrWhiteSpace(json)) return Refuse(AiChartRejectionCodes.Malformed, "the reply was empty", out rejection);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Refuse(AiChartRejectionCodes.Malformed, "the reply is not JSON: " + ex.Message, out rejection);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Refuse(AiChartRejectionCodes.Malformed, "the reply is not an object", out rejection);
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name != "changes")
                    return Refuse(AiChartRejectionCodes.NotStructure, $"\"{p.Name}\" is not a section or phrase change", out rejection);
            }

            if (!root.TryGetProperty("changes", out var list) || list.ValueKind != JsonValueKind.Array)
                return Refuse(AiChartRejectionCodes.Malformed, "the reply has no \"changes\" list", out rejection);
            if (list.GetArrayLength() > MaxChanges)
                return Refuse(AiChartRejectionCodes.Malformed, $"more than {MaxChanges} changes", out rejection);

            var result = new List<AiStructureChange>();
            var touched = new HashSet<string>(StringComparer.Ordinal);
            var added = new HashSet<(AiStructureKind, FractionalPosition)>();
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                index++;
                if (!TryChange(item, index, context, touched, added, out var change, out var why, out var code))
                    return Refuse(code, why, out rejection);
                if (change is not null) result.Add(change);
            }

            if (EventConflict(result, context) is { } clash) return Refuse(AiChartRejectionCodes.Conflict, clash, out rejection);

            // A destination may not collide with a marker of its kind that this reply leaves in place.
            foreach (var c in result.Where(c => c.Op is AiStructureOp.Add or AiStructureOp.Move))
            {
                var occupied = context.Markers.Any(m => m.Kind == c.Kind && m.Position == c.To &&
                    !result.Any(o => o.Op is AiStructureOp.Move or AiStructureOp.Remove && o.Kind == c.Kind && o.From == m.Position));
                if (occupied)
                {
                    return Refuse(AiChartRejectionCodes.Conflict,
                        $"a {Kind(c.Kind)} already starts at {Where(context, c.To!.Value)}", out rejection);
                }
            }

            changes = result;
            return true;
        }
    }

    private static bool TryChange(JsonElement item, int index, AiStructureContext context, HashSet<string> touched,
        HashSet<(AiStructureKind, FractionalPosition)> added, out AiStructureChange? change, out string why,
        out string code)
    {
        change = null;
        code = AiChartRejectionCodes.Malformed;
        why = "";
        var at = string.Create(CultureInfo.InvariantCulture, $"change {index}");
        if (item.ValueKind != JsonValueKind.Object)
        {
            why = at + " is not an object";
            return false;
        }

        foreach (var p in item.EnumerateObject())
        {
            if (Array.IndexOf(ChangeFields, p.Name) >= 0) continue;
            code = AiChartRejectionCodes.NotStructure;
            why = $"{at} has \"{p.Name}\": only sections and phrases may change";
            return false;
        }

        if (TryEventPart(item, at, context, out change, out why, out code) is { } done) return done;

        if (!Text(item, "op", out var op) || !Text(item, "kind", out var kindText) || !Text(item, "id", out var id) ||
            !Text(item, "name", out var name) || !Number(item, "bar", out var bar) || !Number(item, "beat", out var beat))
        {
            why = at + " has a field of the wrong type";
            return false;
        }

        var parsed = ParseOp(op);
        if (parsed is not { } operation)
        {
            code = AiChartRejectionCodes.NotStructure;
            why = $"{at}: \"{op}\" is not add, rename, move or remove";
            return false;
        }

        AiStructureKind? kind = kindText switch
        {
            "section" => AiStructureKind.Section,
            "phrase" => AiStructureKind.Phrase,
            _ => null
        };
        if (kindText is not null && kind is null)
        {
            code = AiChartRejectionCodes.NotStructure;
            why = $"{at}: \"{kindText}\" is not a section or a phrase";
            return false;
        }

        AiStructureMarker? marker = null;
        if (operation != AiStructureOp.Add)
        {
            marker = id is null ? null : context.Markers.FirstOrDefault(m => m.Id == id);
            if (marker is null)
            {
                code = AiChartRejectionCodes.UnknownMarker;
                why = $"{at}: \"{id}\" is not a listed marker";
                return false;
            }

            if (kind is { } k && k != marker.Kind)
            {
                code = AiChartRejectionCodes.Conflict;
                why = $"{at}: {id} is a {Kind(marker.Kind)}, not a {Kind(k)}";
                return false;
            }

            if (!marker.Editable)
            {
                code = AiChartRejectionCodes.ImportedMarker;
                why = $"{at}: {id} is [fixed] (from the imported phrase list)";
                return false;
            }

            if (!touched.Add(marker.Id))
            {
                code = AiChartRejectionCodes.Conflict;
                why = $"{at}: {id} is changed twice";
                return false;
            }

            if (!context.InScope(marker.Position))
            {
                code = AiChartRejectionCodes.OutOfScope;
                why = $"{at}: {id} is outside the scope";
                return false;
            }

            kind = marker.Kind;
        }
        else if (kind is null)
        {
            why = at + ": an add needs a kind";
            return false;
        }

        var kindValue = kind!.Value;
        FractionalPosition? to = null;
        if (operation is AiStructureOp.Add or AiStructureOp.Move)
        {
            if (bar is not { } barNumber)
            {
                why = at + " has no bar";
                return false;
            }

            var beatNumber = beat ?? 1;
            if (kindValue == AiStructureKind.Section && beatNumber != 1)
            {
                code = AiChartRejectionCodes.OffGrid;
                why = $"{at}: a section starts on a bar line, not on beat {beatNumber}";
                return false;
            }

            to = context.Grid.At(barNumber, beatNumber);
            if (to is null)
            {
                code = AiChartRejectionCodes.OffGrid;
                why = string.Create(CultureInfo.InvariantCulture, $"{at}: there is no bar {barNumber} beat {beatNumber}");
                return false;
            }

            if (!context.InScope(to.Value))
            {
                code = AiChartRejectionCodes.OutOfScope;
                why = $"{at}: {Where(context, to.Value)} is outside the scope";
                return false;
            }

            if (!added.Add((kindValue, to.Value)))
            {
                code = AiChartRejectionCodes.Conflict;
                why = $"{at}: two {Kind(kindValue)}s at {Where(context, to.Value)}";
                return false;
            }
        }

        string? newName = null;
        if (operation is AiStructureOp.Add or AiStructureOp.Rename)
        {
            if (!TryName(kindValue, name, out newName))
            {
                code = kindValue == AiStructureKind.Section ? AiChartRejectionCodes.SectionName : AiChartRejectionCodes.PhraseName;
                why = kindValue == AiStructureKind.Section
                    ? $"{at}: \"{name}\" is not a Rocksmith section name"
                    : $"{at}: \"{name}\" is not a phrase name Rocksmith keeps (letters A-Z, digits, spaces, _ and # only)";
                return false;
            }
        }

        // A rename to the same name and a move to the same place change nothing: left out, not refused.
        if (operation == AiStructureOp.Rename && string.Equals(newName, marker!.Name, StringComparison.Ordinal)) return true;
        if (operation == AiStructureOp.Move && to == marker!.Position) return true;

        change = new AiStructureChange(operation, kindValue, marker?.Position, marker?.Name, to, newName);
        return true;
    }

    /// <summary>A section name from <see cref="RsSectionCatalog" /> (stored lower case), or a phrase
    /// name Rocksmith keeps as it is (<see cref="RsPhraseName.IsLegal" />).</summary>
    public static bool TryName(AiStructureKind kind, string? name, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (kind == AiStructureKind.Section)
        {
            normalized = RsSectionCatalog.Normalize(name);
            return normalized is not null;
        }

        var trimmed = name.Trim();
        if (!RsPhraseName.IsLegal(trimmed)) return false;
        normalized = trimmed;
        return true;
    }

    private static AiStructureOp? ParseOp(string? op) => op switch
    {
        "add" => AiStructureOp.Add,
        "rename" => AiStructureOp.Rename,
        "move" => AiStructureOp.Move,
        "remove" => AiStructureOp.Remove,
        _ => null
    };

    /// <summary>Null for an ordinary change; else the result of reading an <c>event</c> op (or of refusing a stray
    /// <c>event</c> object on any other op).</summary>
    private static bool? TryEventPart(JsonElement item, string at, AiStructureContext context, out AiStructureChange? change,
        out string why, out string code)
    {
        change = null;
        why = "";
        code = AiChartRejectionCodes.Malformed;
        var isEvent = item.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String && op.GetString() == "event";
        if (isEvent) return TryEventChange(item, at, context, out change, out why, out code);
        if (!item.TryGetProperty("event", out var stray) || stray.ValueKind == JsonValueKind.Null) return null;
        code = AiChartRejectionCodes.NotStructure;
        why = at + " has an \"event\" without op event";
        return false;
    }

    private static bool TryEventChange(JsonElement item, string at, AiStructureContext context, out AiStructureChange? change,
        out string why, out string code)
    {
        change = null;
        code = AiChartRejectionCodes.Malformed;
        if (Number(item, "bar", out var bar) && Number(item, "beat", out var beat))
            return AiStructureEvents.TryRead(item, at, bar, beat, context, out change, out why, out code);
        why = at + " has a field of the wrong type";
        return false;
    }

    /// <summary>An event change may not share its beat with another event change, or with a marker another change
    /// adds, moves or removes: the dialog's marker at a beat is ONE, and two edits of it would race.</summary>
    private static string? EventConflict(List<AiStructureChange> result, AiStructureContext context)
    {
        foreach (var e in result.Where(c => c.Op == AiStructureOp.Event))
        {
            var clash = result.Any(o => !ReferenceEquals(o, e) &&
                (o.Op == AiStructureOp.Event ? o.To == e.To : o.To == e.To || o.From == e.To));
            if (clash) return $"two changes touch the marker at {Where(context, e.To!.Value)}";
        }

        return null;
    }

    private static string Kind(AiStructureKind kind) => kind == AiStructureKind.Section ? "section" : "phrase";

    private static string Where(AiStructureContext context, FractionalPosition position)
        => context.Grid.Locate(position) is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"bar {at.Bar} beat {at.Beat}")
            : "that position";

    private static bool Refuse(string code, string reason, out AiChartRejection? rejection)
    {
        rejection = new AiChartRejection(0, code, reason);
        return false;
    }

    /// <summary>An optional string: absent and null read as null; anything else but a string is wrong.</summary>
    private static bool Text(JsonElement e, string name, out string? value)
    {
        value = null;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return true;
        if (p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString();
        return true;
    }

    /// <summary>An optional integer: absent and null read as null.</summary>
    private static bool Number(JsonElement e, string name, out int? value)
    {
        value = null;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return true;
        if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var v)) return false;
        value = v;
        return true;
    }
}
