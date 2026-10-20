using System.Globalization;
using System.Text;
using DotCharter.Tone.Ai;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
/// What the structure job is shown: the beat grid bar by bar, the level's sounds summed
///     up per bar (with a repeat letter, so identical bars read as identical), the section and
///     phrase markers as they are, the scope it may change, and the charter's instruction. ONE
///     request over the whole scope — a song's form is only visible all at once.
///     <para>
///         The job never sees a note it could change: a bar is counts, a fret span and a repeat
///         letter. Its schema and <see cref="AiStructureReply" /> are the closed set of section and
///         phrase operations; the FHP / fingering prompts (<see cref="AiChartPrompt" />) are not
///         touched by any of this.
///     </para>
/// </summary>
public static class AiStructurePrompt
{
    /// <summary>The schema's name on the wire.</summary>
    public const string SchemaName = "dotcharter_sections_phrases";

    /// <summary>The line that opens the marker listing; the tests look for it.</summary>
    public const string MarkersHeader = "--- current sections and phrases ---";

    /// <summary>The line that opens the bar listing.</summary>
    public const string BarsHeader = "--- bars ---";

    /// <summary>The reply schema. Every property of a change is required and nullable, so a
    /// provider's strict structured-output mode accepts it.</summary>
    public const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["changes"],
          "properties": {
            "changes": {
              "type": "array",
              "description": "Every section and phrase change, in song order. Empty when nothing should change.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["op", "kind", "id", "bar", "beat", "name"],
                "properties": {
                  "op": { "type": "string", "enum": ["add", "rename", "move", "remove"] },
                  "kind": { "type": ["string", "null"], "enum": ["section", "phrase", null], "description": "Required for add; null otherwise (the id says it)." },
                  "id": { "type": ["string", "null"], "description": "The listed marker (s1, p3, ...) for rename, move, remove; null for add." },
                  "bar": { "type": ["integer", "null"], "description": "Bar number for add and move; null otherwise." },
                  "beat": { "type": ["integer", "null"], "description": "1-based beat in the bar for a phrase add or move; null or 1 for a section (always on the bar line)." },
                  "name": { "type": ["string", "null"], "description": "The name for add and rename; null otherwise." }
                }
              }
            }
          }
        }
        """;

    /// <summary>The system prompt: the job, the vocabulary and the rules. Stable, so a provider can
    /// cache it.</summary>
    public static readonly string System = """
        You mark the STRUCTURE of a Rocksmith guitar or bass chart: its sections and its phrases.

        SECTIONS label the song's form for the player (the Riff Repeater shows them). A section name
        MUST be one of these, spelled exactly so (lower case):
        {SECTIONS}
        A section starts ON A BAR LINE. Repeated parts reuse the name (verse, chorus, verse, chorus).
        Use "noguitar" only over bars where this arrangement plays nothing.

        PHRASES are the units Rocksmith levels difficulty by. A phrase starts on a BEAT. Name a phrase
        with letters A-Z, digits, spaces, _ or # only (for example "verse", "chorus2", "solo_a"). A
        phrase normally covers 2 to 8 bars; a long section is split into several phrases. Each section
        usually starts with a phrase on the same bar line. COUNT (the silent count-in) and END (after
        the last note) are the usual first and last phrases.
        A phrase that holds notes needs a hand position (anchor) of its own: bars list the beats that
        carry an anchor as "A@beat". Where a phrase starts without one, the editor adds the one it
        needs for you (the hand position already in force, restated at the phrase start); you never
        write anchors, and a phrase start should follow the music, not the anchors. A phrase that
        starts inside a held chord shape cannot get one and is refused.

        You may ONLY add, rename, move or remove section and phrase markers. You never change notes,
        chords, frets, timing, techniques, hand positions or fingering; there is no way to express
        that in the reply, and a reply that tries is thrown away whole.

        Rules for the reply:
        - add: kind, bar (and beat for a phrase), name. Never where a marker of that kind already is.
        - rename: id and the new name. move: id and the new bar (and beat). remove: id.
        - Change a marker at most once. Markers marked [fixed] cannot be changed.
        - Stay inside the scope you are given. Propose only what the charter asked for; when the
          structure is already right, answer with no changes.

        Answer with ONLY the JSON object the schema describes.
        """.Replace("{SECTIONS}", string.Join(", ", RsSectionCatalog.Names), StringComparison.Ordinal);

    /// <summary>The prompt for one structure request.</summary>
    /// <param name="facts">The song (its <see cref="AiChartSongFacts.Arrangement" /> is the snapshot).</param>
    /// <param name="level">The level whose sounds are summed up per bar.</param>
    /// <param name="beats">The beat map.</param>
    /// <param name="context">The grid, the markers and the scope.</param>
    /// <param name="instruction">The charter's instruction and the recent conversation.</param>
    public static ToneDesignPrompt Build(
        AiChartSongFacts facts, ChartLevel level, IList<BeatMarker> beats, AiStructureContext context,
        AiChartInstruction? instruction)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(context);
        var text = Listing(facts, level, beats, context) + AiChartPrompt.InstructionSection(instruction);
        return new ToneDesignPrompt(System, [new ToneDesignTurn(ToneDesignRole.User, text)], Schema, SchemaName);
    }

    /// <summary>The words that open a retry's last turn.</summary>
    public const string RetryHeader = "Your reply was REFUSED";

    /// <summary>The ONE retry of a refused reply: the first prompt, the refused reply and the reason.</summary>
    public static ToneDesignPrompt Retry(ToneDesignPrompt first, string rejectedReply, AiChartRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(rejection);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{RetryHeader} ({rejection.Code}): {rejection.Reason}.\n") +
            "Answer again: the whole reply object, with that fixed and every other rule kept. Only section and " +
            "phrase changes, names from the lists, positions on the listed bars and beats. This is the only retry.";
        return first with
        {
            Messages =
            [
                .. first.Messages,
                new ToneDesignTurn(ToneDesignRole.Assistant, rejectedReply ?? ""),
                new ToneDesignTurn(ToneDesignRole.User, text)
            ]
        };
    }

    /// <summary>The listing (internal for the tests).</summary>
    internal static string Listing(AiChartSongFacts facts, ChartLevel level, IList<BeatMarker> beats,
        AiStructureContext context)
    {
        var sb = new StringBuilder(4096);
        var arrangement = facts.Arrangement;
        sb.Append(CultureInfo.InvariantCulture,
            $"Arrangement \"{arrangement.Name}\" ({(facts.Bass ? "bass" : "guitar")}), {context.Grid.Bars.Count} bars");
        if (facts.Key is { } key) sb.Append(CultureInfo.InvariantCulture, $", key {key}");
        sb.Append('\n');
        sb.Append(context.From is null && context.To is null
            ? "Scope: the whole song.\n"
            : string.Create(CultureInfo.InvariantCulture,
                $"Scope: change markers and place new ones only from {Where(context, context.From)} to {Where(context, context.To)} (inclusive); the rest is context.\n"));

        sb.AppendLine(MarkersHeader);
        if (context.Markers.Count == 0) sb.AppendLine("(none)");
        foreach (var m in context.Markers)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{m.Id} {(m.Kind == AiStructureKind.Section ? "section" : "phrase")} \"{m.Name}\" at {Where(context, m.Position)} ({Clock(m.Position.GetPositionMs(beats))})");
            if (!m.Editable) sb.Append(" [fixed]");
            sb.Append('\n');
        }

        sb.AppendLine(BarsHeader);
        sb.AppendLine("bar | start | beats | notes chords | frets | repeat | anchors");
        var summary = BarSummaries(arrangement, level, beats, context.Grid);
        foreach (var bar in context.Grid.Bars)
        {
            var s = summary[bar.Number];
            sb.Append(CultureInfo.InvariantCulture,
                $"{bar.Number} | {Clock(bar.StartMs)} | {bar.BeatCount} | {s.Notes} {s.Chords} | ");
            sb.Append(s.LowFret is { } lo ? string.Create(CultureInfo.InvariantCulture, $"{lo}-{s.HighFret}") : "-");
            sb.Append(" | ").Append(s.Pattern).Append(" | ");
            sb.Append(s.Anchors.Count == 0 ? "-" : string.Join(" ", s.Anchors.Select(b => "A@" + b.ToString(CultureInfo.InvariantCulture))));
            sb.Append('\n');
        }

        sb.AppendLine("--- end of bars ---");
        return sb.ToString();
    }

    private static string Where(AiStructureContext context, FractionalPosition? position)
    {
        if (position is not { } p || context.Grid.Locate(p) is not { } at) return "the song's edge";
        return at.OnBeat
            ? string.Create(CultureInfo.InvariantCulture, $"bar {at.Bar} beat {at.Beat}")
            : string.Create(CultureInfo.InvariantCulture, $"bar {at.Bar} between beats {at.Beat} and {at.Beat + 1}");
    }

    private static string Clock(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}");
    }

    private sealed class BarSummary
    {
        public int Notes;
        public int Chords;
        public int? LowFret;
        public int HighFret;
        public string Pattern = "-";
        public readonly List<int> Anchors = [];
        public readonly List<string> Content = [];
    }

    /// <summary>Per bar: counts, the fretted span, the beats an anchor starts on, and a repeat
    /// letter shared by bars whose sounds are identical (same offsets, strings and frets).</summary>
    private static Dictionary<int, BarSummary> BarSummaries(
        Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats, AiStructureGrid grid)
    {
        var bars = grid.Bars.ToDictionary(b => b.Number, _ => new BarSummary());
        void Add(FractionalPosition position, string content, IEnumerable<int> frets, bool chord)
        {
            if (grid.Locate(position) is not { } at || !bars.TryGetValue(at.Bar, out var s)) return;
            if (chord) s.Chords++;
            else s.Notes++;
            foreach (var fret in frets.Where(f => f > arrangement.Capo))
            {
                s.LowFret = s.LowFret is { } lo ? Math.Min(lo, fret) : fret;
                s.HighFret = Math.Max(s.HighFret, fret);
            }

            s.Content.Add(string.Create(CultureInfo.InvariantCulture,
                $"{at.Beat}+{position.Fraction}:{content}"));
        }

        foreach (var n in level.Notes) Add(n.Position, $"{n.StringIndex}/{n.Fret}", [n.Fret], chord: false);
        foreach (var c in level.Chords)
        {
            var frets = c.ChordNotes.Select(cn => cn.Fret).ToList();
            Add(c.Position, string.Join(",", c.ChordNotes.Select(cn => $"{cn.StringIndex}/{cn.Fret}")) + "#" +
                c.TemplateId.ToString(CultureInfo.InvariantCulture), frets, chord: true);
        }

        foreach (var a in level.Anchors)
        {
            if (grid.Locate(a.Position) is { OnBeat: true } at && bars.TryGetValue(at.Bar, out var s))
                s.Anchors.Add(at.Beat);
        }

        var letters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bar in grid.Bars)
        {
            var s = bars[bar.Number];
            s.Anchors.Sort();
            if (s.Content.Count == 0) continue;
            s.Content.Sort(StringComparer.Ordinal);
            var key = string.Join(";", s.Content);
            if (!letters.TryGetValue(key, out var letter))
            {
                letter = Letter(letters.Count);
                letters[key] = letter;
            }

            s.Pattern = letter;
        }

        return bars;
    }

    /// <summary>A, B, … Z, AA, AB, …</summary>
    private static string Letter(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }

        return sb.ToString();
    }
}
