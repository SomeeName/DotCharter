using System.Globalization;
using System.Text;
using System.Text.Json;
using DotCharter.Localization;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Ai;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
/// What an ask / explain question is shown: the scope's sounds, anchors and chord
///     fingering in the chunk listing the FHP / fingering pass reads (<see cref="AiChartPrompt.Listing" />
///     with <see cref="AiChartJob.Explain" />), the arrangement's sections and phrases, the bars with
///     their start times (so the model can name "bar 34"), the chart quality report's findings in the
///     scope, and the question with the conversation before it.
///     <para>
///         The reply is ONE text field (<see cref="Schema" />). Anything else a reply carries —
///         anchors, chords, changes — is ignored by <see cref="TryReadAnswer" />: this job has no
///         reader, validator or applier, so nothing it says can reach the chart.
///     </para>
/// </summary>
public static class AiExplainPrompt
{
    /// <summary>The schema's name on the wire.</summary>
    public const string SchemaName = "dotcharter_chart_answer";

    /// <summary>The line that opens the marker listing.</summary>
    public const string MarkersHeader = "--- sections and phrases ---";

    /// <summary>The line that opens the bar listing.</summary>
    public const string BarsHeader = "--- bars ---";

    /// <summary>The line that opens the quality findings; the tests look for it.</summary>
    public const string FindingsHeader = "--- chart quality report: findings in the scope ---";

    /// <summary>The line that opens the question; the tests look for it.</summary>
    public const string QuestionHeader = "--- the charter's question ---";

    /// <summary>The most scope sounds listed; a longer scope is cut and says so.</summary>
    public const int MaxSounds = 600;

    /// <summary>The most findings listed.</summary>
    public const int MaxFindings = 60;

    /// <summary>Sounds shown either side of the scope, marked ctx.</summary>
    private const int Context = 4;

    /// <summary>The longest finding message and answer kept.</summary>
    private const int MaxMessageChars = 300;

    private const int MaxAnswerChars = 8000;

    /// <summary>The reply schema: one text field.</summary>
    public const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["answer"],
          "properties": {
            "answer": { "type": "string", "description": "The answer to the charter's question, as plain text." }
          }
        }
        """;

    /// <summary>The system prompt. Stable, so a provider can cache it.</summary>
    public const string System = """
        You answer a charter's question about a Rocksmith 2014 guitar or bass chart: why a hand position (anchor, FHP) or a chord fingering is what it is, what is wrong in a bar, what a quality finding means. You EXPLAIN. You never change the chart, and nothing you write is applied to it.

        How to read the request:
        - The chart in the scope, in time order (ms). Notes: string (0 = the lowest-pitched string) and fret. Chords: one fret per string, lowest string first, -1 = not played; their fingers (1 index, 2 middle, 3 ring, 4 pinky, 0 thumb, -1 none) and their name. Anchors: fret F, width W - the index finger sits on fret F and the hand covers frets F..F+W-1 until the next anchor; Rocksmith derives a finger as 1 + (fret - F) - (W - 4). Lines between "--- chunk starts" and "--- chunk ends ---" are the scope the question is about; lines marked "ctx" are just before or after it.
        - The sections and phrases, and the bars with their start times, so you can name a bar.
        - The chart quality report's findings in the scope: rule code, severity, time, bar and message. They are the app's own checks; cite them when they answer the question.
        - The question, and the conversation before it.

        How to answer:
        - In the language of the question, as plain text, in at most about 150 words unless the question asks for more.
        - Name places by bar and m:ss time, never by sound id or ms.
        - Base the answer on the listing and the findings. When they do not show what is asked, say so instead of guessing.
        - When the charter wants something changed, say what you would change and that the chat's Task picker runs it as a proposal they can accept.

        Answer with one JSON object: {"answer": "<your answer>"}. Any other field (anchors, chords, changes) is ignored.
        """;

    /// <summary>The prompt for one question.</summary>
    /// <param name="facts">The song.</param>
    /// <param name="level">The level whose sounds are listed.</param>
    /// <param name="beats">The beat map.</param>
    /// <param name="from">First sound position of the scope (inclusive), or null for the start.</param>
    /// <param name="to">Last sound position of the scope (inclusive), or null for the end.</param>
    /// <param name="findings">The scope's quality findings, one line each (<see cref="Findings" />).</param>
    /// <param name="question">The question and the conversation excerpt.</param>
    public static ToneDesignPrompt Build(AiChartSongFacts facts, ChartLevel level, IList<BeatMarker> beats,
        FractionalPosition? from, FractionalPosition? to, IReadOnlyList<string> findings, AiChartInstruction question)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(question);
        var text = Listing(facts, level, beats, from, to) + FindingsSection(findings) + QuestionSection(question);
        return new ToneDesignPrompt(System, [new ToneDesignTurn(ToneDesignRole.User, text)], Schema, SchemaName);
    }

    /// <summary>The chart listing (internal for the tests): the pass's chunk listing over the whole
    /// scope, then the markers and the bars it spans.</summary>
    internal static string Listing(AiChartSongFacts facts, ChartLevel level, IList<BeatMarker> beats,
        FractionalPosition? from, FractionalPosition? to)
    {
        var chunk = ScopeChunk(level, beats, from, to, out var total);
        var sb = new StringBuilder(AiChartPrompt.Listing(AiChartJob.Explain, facts, level, beats, chunk));
        if (total > chunk.Core.Count)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"(the listing stops after {chunk.Core.Count} of the scope's {total} sounds)").Append('\n');
        }

        var grid = AiStructureGrid.Of(beats);
        sb.AppendLine(MarkersHeader);
        var markers = AiStructureContext.Of(facts.Arrangement, beats, from, to).Markers;
        if (markers.Count == 0) sb.AppendLine("(none)");
        foreach (var m in markers)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{(m.Kind == AiStructureKind.Section ? "section" : "phrase")} \"{m.Name}\" at {Where(grid, m.Position)} ({Clock(m.Position.GetPositionMs(beats))})\n");
        }

        // The bars the listing spans: from the first listed sound's bar to the last one's.
        var listed = chunk.Before.Concat(chunk.Core).Concat(chunk.After).ToList();
        var firstBar = listed.Count > 0 && grid.Locate(listed[0].Position) is { } a ? a.Bar : 1;
        var lastBar = listed.Count > 0 && grid.Locate(listed[^1].Position) is { } b ? b.Bar : int.MaxValue;
        sb.AppendLine(BarsHeader);
        foreach (var bar in grid.Bars.Where(x => x.Number >= firstBar && x.Number <= lastBar))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"bar {bar.Number} at {(int)Math.Round(bar.StartMs)}ms ({Clock(bar.StartMs)}), {bar.BeatCount} beats\n");
        }

        sb.AppendLine("--- end of bars ---");
        return sb.ToString();
    }

    /// <summary>
    ///     The quality report's findings for <paramref name="arrangement" /> in the scope, one line
    ///     each: rule code, severity, time and bar, the check's own message. A finding on another
    ///     level, or timed outside the scope, is left out; one with no time (about the whole
    ///     arrangement) is kept. At most <see cref="MaxFindings" />.
    /// </summary>
    public static IReadOnlyList<string> Findings(ChartQualityReport report, Arrangement arrangement, ChartLevel level,
        IList<BeatMarker> beats, FractionalPosition? from, FractionalPosition? to, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localization);
        var fromMs = from is { } f ? f.GetPositionMs(beats) - 1 : double.NegativeInfinity;
        var toMs = to is { } t ? t.GetPositionMs(beats) + 1 : double.PositiveInfinity;
        var grid = AiStructureGrid.Of(beats);
        return report.Findings
            .Where(x => ReferenceEquals(x.Arrangement, arrangement) && (x.Level is null || ReferenceEquals(x.Level, level)))
            .Where(x => x.TimeMs is not { } ms || ms >= fromMs && ms <= toMs)
            .OrderBy(x => x.TimeMs ?? -1)
            .Take(MaxFindings)
            .Select(x => Line(x, grid, localization))
            .ToList();
    }

    /// <summary>The findings as a prompt section; "(none)" when the scope has none.</summary>
    internal static string FindingsSection(IReadOnlyList<string> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FindingsHeader);
        if (findings.Count == 0) sb.AppendLine("(none)");
        foreach (var line in findings) sb.AppendLine(line);
        sb.AppendLine("--- end of findings ---");
        return sb.ToString();
    }

    /// <summary>The question, and the conversation before it, as the prompt's last section.</summary>
    internal static string QuestionSection(AiChartInstruction question)
    {
        var sb = new StringBuilder();
        sb.AppendLine(QuestionHeader);
        sb.AppendLine(question.Text.Trim());
        sb.AppendLine("--- end of question ---");
        if (!string.IsNullOrWhiteSpace(question.Conversation))
        {
            sb.AppendLine(AiChartPrompt.ConversationHeader);
            sb.AppendLine(question.Conversation.Trim());
            sb.AppendLine("--- end of conversation ---");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Reads the reply's answer. <paramref name="proposed" /> is true when the reply carried any
    ///     other field (a proposal the model was told not to make): it is ignored, never read. A reply
    ///     that is not JSON at all is taken as the answer's text.
    /// </summary>
    /// <returns>False when there is no answer text.</returns>
    public static bool TryReadAnswer(string? json, out string answer, out bool proposed)
    {
        answer = "";
        proposed = false;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals("answer") && property.Value.ValueKind == JsonValueKind.String)
                    answer = property.Value.GetString()!.Trim();
                else
                    proposed = true;
            }
        }
        catch (JsonException)
        {
            var text = json.Trim();
            if (text.StartsWith('{') || text.StartsWith('[')) return false;
            answer = text;
        }

        if (answer.Length > MaxAnswerChars) answer = answer[..MaxAnswerChars].TrimEnd() + "…";
        return answer.Length > 0;
    }

    /// <summary>One chunk over the whole scope (capped at <see cref="MaxSounds" />) with a few sounds
    /// of context either side, for <see cref="AiChartPrompt.Listing" />.</summary>
    private static AiChartChunk ScopeChunk(ChartLevel level, IList<BeatMarker> beats, FractionalPosition? from,
        FractionalPosition? to, out int total)
    {
        var sounds = AiChartChunker.Sounds(level, beats);
        var first = -1;
        var last = -1;
        for (var i = 0; i < sounds.Count; i++)
        {
            var p = sounds[i].Position;
            if (from is { } f && p < f) continue;
            if (to is { } t && p > t) continue;
            if (first < 0) first = i;
            last = i;
        }

        total = first < 0 ? 0 : last - first + 1;
        if (first < 0)
        {
            var start = from ?? FractionalPosition.Zero;
            return new AiChartChunk(0, start, to, AiChartChunker.Ms(start, beats), [], [], [], "");
        }

        var cut = Math.Min(last, first + MaxSounds - 1);
        var core = sounds.Skip(first).Take(cut - first + 1).ToList();
        var before = sounds.Skip(Math.Max(0, first - Context)).Take(first - Math.Max(0, first - Context)).ToList();
        var after = cut < last ? [] : sounds.Skip(last + 1).Take(Context).ToList();
        FractionalPosition? end = cut + 1 < sounds.Count ? sounds[cut + 1].Position : null;
        return new AiChartChunk(0, core[0].Position, end, core[0].Ms, core, before, after, "");
    }

    private static string Line(QualityFinding finding, AiStructureGrid grid, ILocalizationService localization)
    {
        var message = string.Join(' ', QualityFindingText.Message(finding, localization)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (message.Length > MaxMessageChars) message = message[..MaxMessageChars] + "…";
        var severity = finding.Severity.ToString().ToLowerInvariant();
        if (finding.TimeMs is not { } ms) return $"{finding.Code} {severity} (whole arrangement): {message}";
        var bar = grid.Bars.LastOrDefault(b => b.StartMs <= ms + 0.5);
        return string.Create(CultureInfo.InvariantCulture,
            $"{finding.Code} {severity} at {Clock(ms)}{(bar is null ? "" : $" bar {bar.Number}")}: {message}");
    }

    private static string Where(AiStructureGrid grid, FractionalPosition position)
    {
        if (grid.Locate(position) is not { } at) return "the song's edge";
        return at.OnBeat
            ? string.Create(CultureInfo.InvariantCulture, $"bar {at.Bar} beat {at.Beat}")
            : string.Create(CultureInfo.InvariantCulture, $"bar {at.Bar} between beats {at.Beat} and {at.Beat + 1}");
    }

    private static string Clock(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}");
    }
}
