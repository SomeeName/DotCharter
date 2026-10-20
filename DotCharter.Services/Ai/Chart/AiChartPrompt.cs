using System.Globalization;
using System.Text;
using DotCharter.Tone.Ai;

namespace DotCharter.Services.Ai.Chart;

/// <summary>What the prompt knows about the song besides the chunk itself.</summary>
/// <param name="Arrangement">Tuning, capo, string count and the chord templates.</param>
/// <param name="Bass">True for a bass part — fewer chords, one finger per fret.</param>
/// <param name="Key">The song's key signature in words, or null when unknown.</param>
public sealed record AiChartSongFacts(Arrangement Arrangement, bool Bass, string? Key)
{
    /// <summary>Strings on the instrument, the way the fingering code counts them.</summary>
    public int StringCount => Arrangement.Tuning.Length > 0 ? Arrangement.Tuning.Length : Arrangement.StringCount;
}

/// <summary>
///     The AI chart pass's prompt: a fixed system prompt per job (cacheable across every
///     chunk and every song), a strict reply schema, and the per-chunk listing.
///     <para>
///         <b>The schema stays inside the subset BOTH constrained decoders accept</b>
///         (<c>docs/agents/services/ai.md</c> § wire schema): every object closed, every
///         property required, no numeric/length/item-count keywords. The bounds the
///         decoder cannot carry are stated in the descriptions and enforced by
///         <see cref="AiChartValidator" />, which rejects instead of clamping.
///         <c>AiProviderCompatTests</c> walks both schemas.
///     </para>
/// </summary>
public static class AiChartPrompt
{
    /// <summary>The <c>at</c> value that places an anchor at the chunk's first moment.</summary>
    public const string StartToken = "start";

    /// <summary>The reply schema for <see cref="AiChartJob.FhpAndFingering" />.</summary>
    public const string FingeringSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["anchors", "chords"],
          "properties": {
            "anchors": {
              "type": "array",
              "description": "The COMPLETE anchor list for the chunk, in time order. The first is at \"start\". Anchors not listed are deleted.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["at", "fret", "width"],
                "properties": {
                  "at": { "type": "string", "description": "\"start\" or the id of a CHUNK sound (never a ctx sound)." },
                  "fret": { "type": "integer", "description": "Index-finger fret, capo+1..24." },
                  "width": { "type": "integer", "description": "Frets covered, normally 4." }
                }
              }
            },
            "chords": {
              "type": "array",
              "description": "Every chord marked [fix], exactly once.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "ms", "frets", "fingers"],
                "properties": {
                  "id": { "type": "string", "description": "The chord's id, copied." },
                  "ms": { "type": "integer", "description": "The chord's time, copied unchanged." },
                  "frets": { "type": "array", "items": { "type": "integer" }, "description": "The chord's frets, copied unchanged: one per string, -1 = unused." },
                  "fingers": { "type": "array", "items": { "type": "integer" }, "description": "One per string: 1-4 index..pinky, 0 thumb, -1 none (open and unused strings)." }
                }
              }
            }
          }
        }
        """;

    /// <summary>The reply schema for <see cref="AiChartJob.FhpOnly" />: anchors, and no chord
    /// at all — the model is never asked about a fingering.</summary>
    public const string FhpSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["anchors"],
          "properties": {
            "anchors": {
              "type": "array",
              "description": "The COMPLETE anchor list for the chunk, in time order. The first is at \"start\". Anchors not listed are deleted.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["at", "fret", "width"],
                "properties": {
                  "at": { "type": "string", "description": "\"start\" or the id of a CHUNK sound (never a ctx sound)." },
                  "fret": { "type": "integer", "description": "Index-finger fret, capo+1..24." },
                  "width": { "type": "integer", "description": "Frets covered, normally 4." }
                }
              }
            }
          }
        }
        """;

    /// <summary>The reply schema for <see cref="AiChartJob.FingeringOnly" />: chord fingers,
    /// and no anchor — the anchors are fixed input.</summary>
    public const string FingeringOnlySchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["chords"],
          "properties": {
            "chords": {
              "type": "array",
              "description": "Every chord marked [fix], exactly once.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "ms", "frets", "fingers"],
                "properties": {
                  "id": { "type": "string", "description": "The chord's id, copied." },
                  "ms": { "type": "integer", "description": "The chord's time, copied unchanged." },
                  "frets": { "type": "array", "items": { "type": "integer" }, "description": "The chord's frets, copied unchanged: one per string, -1 = unused." },
                  "fingers": { "type": "array", "items": { "type": "integer" }, "description": "One per string: 1-4 index..pinky, 0 thumb, -1 none (open and unused strings)." }
                }
              }
            }
          }
        }
        """;

    /// <summary>The reply schema for <see cref="AiChartJob.ChordNames" />.</summary>
    public const string NamesSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["chords"],
          "properties": {
            "chords": {
              "type": "array",
              "description": "Every chord marked [fix], exactly once.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "ms", "frets", "name"],
                "properties": {
                  "id": { "type": "string", "description": "The chord's id, copied." },
                  "ms": { "type": "integer", "description": "The chord's time, copied unchanged." },
                  "frets": { "type": "array", "items": { "type": "integer" }, "description": "The chord's frets, copied unchanged: one per string, -1 = unused." },
                  "name": { "type": "string", "description": "The chord name; empty for a sound that should carry none." }
                }
              }
            }
          }
        }
        """;

    private const string FingeringSystem = """
        You correct the fret-hand positions ("anchors", FHP) and the chord fingerings of a Rocksmith 2014 chart.
        You may change ONLY anchors and chord fingers. Every note, string, fret and time stays exactly as given.

        How Rocksmith reads a chart:
        - Strings are numbered from 0 = the lowest-pitched string. A fret list has one entry per string; -1 = the string is not played. Fret 0 is open; with a capo, every fret at or below the capo is open.
        - An anchor is where the index finger sits: fret F, width W (normally 4; less than 4 counts as 4) covers frets F..F+W-1. It holds from its time until the next anchor. Every fretted note should lie inside the anchor in force.
        - The game derives the finger for a fret as 1 + (fret - F) - (W - 4). So when a chord uses the index finger (1), the anchor fret must equal the fret under that index finger.
        - Fingers: 1 index, 2 middle, 3 ring, 4 pinky, 0 thumb, -1 none. Every fretted string of a chord gets a finger; open and unused strings get -1. A barre is one finger on several strings at the same fret (normally the index, on the lowest fret of the chord); the index finger is never on a higher fret than another fretted string's finger. A barre never lies across a string that rings OPEN between the barred strings — finger those strings separately.
        - The lowest legal anchor fret is capo + 1 (1 without a capo); the highest is 24, and F + W - 1 must not pass 24.

        What a finished chart looks like (measured on hand-finished charts):
        - The anchor fret is the LOWEST fretted fret among the notes it governs (true for 95% of bass and 89% of guitar anchors), every note it governs lies within anchor..anchor+3, and the width is 4.
        - So a new anchor starts where a note leaves the current four frets — on the first sound that needs the new position, not earlier — and sits on the lowest fret of the notes that follow until the next move. Do not put an anchor on every note, and do not keep an anchor whose lowest fret the passage no longer uses.
        - Look ahead: when the hand must move, choose the position that also covers the notes that follow.
        - Common fingerings: power chord root+fifth(+octave) e.g. frets [-1,3,5,5,-1,-1] -> fingers [-1,1,3,4,-1,-1], [3,5,-1,-1,-1,-1] -> [1,3,-1,-1,-1,-1]; full barre chords put the index across the barre fret; open chords use the standard open fingerings (C [-1,3,2,0,1,0] -> [-1,3,2,-1,1,-1], D [-1,-1,0,2,3,2] -> [-1,-1,-1,1,3,2], E [0,2,2,1,0,0] -> [-1,2,3,1,-1,-1], A [-1,0,2,2,2,0] -> [-1,-1,1,2,3,-1], G [3,2,0,0,0,3] -> [2,1,-1,-1,-1,3]).
        - On bass, one finger per fret inside the anchor: the index finger takes the anchor fret.

        {RULES}

        The request lists the chunk in time order: sounds (notes and chords, with their current fingering) and the anchors that are there now. Lines marked "ctx" are CONTEXT before and after the chunk so you can see where the hand comes from and goes to; they are never changed.

        Answer with one JSON object:
        - "anchors": the COMPLETE list of anchors for the chunk, in time order. The first MUST be at "start" (the chunk's first moment) — never add a second anchor on the chunk's first sound. Every other anchor names, by id, the chunk sound it starts at. Anchors you leave out are deleted, so restate the ones that are already right.
        - "chords": EVERY chord marked [fix], exactly once, with its id, ms and frets copied unchanged and the fingers you choose. Keep fingerings that are already right.
        Never list a ctx sound. A reply that changes a note, fret, string or time, leaves out a chord, or names a context sound is thrown away whole.
        """;

    private const string FhpOnlySystem = """
        You correct the fret-hand positions ("anchors", FHP) of a Rocksmith 2014 chart.
        You may change ONLY anchors. Every note, chord, string, fret, fingering and time stays exactly as given.

        How Rocksmith reads a chart:
        - Strings are numbered from 0 = the lowest-pitched string. A fret list has one entry per string; -1 = the string is not played. Fret 0 is open; with a capo, every fret at or below the capo is open.
        - An anchor is where the index finger sits: fret F, width W (normally 4; less than 4 counts as 4) covers frets F..F+W-1. It holds from its time until the next anchor. Every fretted note should lie inside the anchor in force.
        - The game derives the finger for a fret as 1 + (fret - F) - (W - 4).
        - The lowest legal anchor fret is capo + 1 (1 without a capo); the highest is 24, and F + W - 1 must not pass 24.

        Chord fingerings are not yours to choose. A chord shown with "fingers [...] fixed" was fingered by the charter: when it uses the index finger (1), the anchor in force at that chord MUST sit on that finger's fret, or the reply is thrown away. Every other chord's fingering is re-derived from your anchors afterwards, so place each anchor where the hand should be.

        What a finished chart looks like (measured on hand-finished charts):
        - The anchor fret is the LOWEST fretted fret among the notes it governs (true for 95% of bass and 89% of guitar anchors), every note it governs lies within anchor..anchor+3, and the width is 4.
        - So a new anchor starts where a note leaves the current four frets — on the first sound that needs the new position, not earlier — and sits on the lowest fret of the notes that follow until the next move. Do not put an anchor on every note, and do not keep an anchor whose lowest fret the passage no longer uses.
        - Look ahead: when the hand must move, choose the position that also covers the notes that follow.
        - On bass, one finger per fret inside the anchor: the index finger takes the anchor fret.

        {RULES}

        The request lists the chunk in time order: sounds (notes and chords) and the anchors that are there now. Lines marked "ctx" are CONTEXT before and after the chunk so you can see where the hand comes from and goes to; they are never changed.

        Answer with one JSON object:
        - "anchors": the COMPLETE list of anchors for the chunk, in time order. The first MUST be at "start" (the chunk's first moment) — never add a second anchor on the chunk's first sound. Every other anchor names, by id, the chunk sound it starts at. Anchors you leave out are deleted, so restate the ones that are already right.
        Never name a ctx sound. A reply that places an anchor on a context sound or on a sound outside the chunk is thrown away whole.
        """;

    private const string FingeringOnlySystem = """
        You correct the chord fingerings of a Rocksmith 2014 chart.
        You may change ONLY chord fingers. Every note, string, fret, time and fret-hand position ("anchor") stays exactly as given.

        How Rocksmith reads a chart:
        - Strings are numbered from 0 = the lowest-pitched string. A fret list has one entry per string; -1 = the string is not played. Fret 0 is open; with a capo, every fret at or below the capo is open.
        - An anchor is where the index finger sits: fret F, width W (normally 4; less than 4 counts as 4) covers frets F..F+W-1. It holds from its time until the next anchor. The anchors in the request are FIXED: you cannot move them.
        - The game derives the finger for a fret as 1 + (fret - F) - (W - 4). So a chord that uses the index finger (1) MUST have it on the fret of the anchor in force at that chord; a fingering that puts the index on any other fret is thrown away. When the chord sits above the anchor fret, leave the index out and use the fingers the anchor implies.
        - Fingers: 1 index, 2 middle, 3 ring, 4 pinky, 0 thumb, -1 none. Every fretted string of a chord gets a finger; open and unused strings get -1. A barre is one finger on several strings at the same fret (normally the index, on the lowest fret of the chord); the index finger is never on a higher fret than another fretted string's finger. A barre never lies across a string that rings OPEN between the barred strings — finger those strings separately.
        - Common fingerings: power chord root+fifth(+octave) e.g. frets [-1,3,5,5,-1,-1] -> fingers [-1,1,3,4,-1,-1], [3,5,-1,-1,-1,-1] -> [1,3,-1,-1,-1,-1]; full barre chords put the index across the barre fret; open chords use the standard open fingerings (C [-1,3,2,0,1,0] -> [-1,3,2,-1,1,-1], D [-1,-1,0,2,3,2] -> [-1,-1,-1,1,3,2], E [0,2,2,1,0,0] -> [-1,2,3,1,-1,-1], A [-1,0,2,2,2,0] -> [-1,-1,1,2,3,-1], G [3,2,0,0,0,3] -> [2,1,-1,-1,-1,3]).
        - On bass, one finger per fret inside the anchor.

        {RULES}

        The request lists the chunk in time order: sounds (notes and chords, with their current fingering) and the anchors in force. Lines marked "ctx" are CONTEXT before and after the chunk; they are never changed.

        Answer with one JSON object:
        - "chords": EVERY chord marked [fix], exactly once, with its id, ms and frets copied unchanged and the fingers you choose. Keep fingerings that are already right.
        Never list a ctx sound. A reply that changes a note, fret, string or time, leaves out a chord, names a context sound or contradicts an anchor is thrown away whole.
        """;

    private const string NamesSystem = """
        You name the chords of a Rocksmith 2014 chart. You may change ONLY chord names. Every note, string, fret and time stays exactly as given.

        Strings are numbered from 0 = the lowest-pitched string. A fret list has one entry per string; -1 = the string is not played. Each chord line also gives the notes it sounds (lowest first) and the names an automatic namer found, best first.

        {STYLE}

        Lines marked "ctx" are context before and after the chunk; never list them.

        Answer with one JSON object:
        - "chords": EVERY chord marked [fix], exactly once, with its id, ms and frets copied unchanged and the name you choose ("" for a sound that should carry no name).
        A reply that changes a fret, string or time, leaves out a chord, or names a context sound is thrown away whole.
        """;

    private const string EofStyle = """
        Naming style: Editor on Fire (EOF). Roots are spelled with SHARPS only (C, C#, D, D#, E, F, F#, G, G#, A, A#, B), even in flat keys. Qualities: major is the bare root ("A"), minor "min" ("A#min"), "Maj7", "min7", "7", "dim", "aug", "sus2", "sus4", "add9", "5" for a power chord (root + fifth, with or without the octave), "3" for root + major third. No slash chords unless the bass note is not part of the chord's own name. Two-string shapes other than power chords and root+third usually carry no name.
        """;

    private const string MusicalStyle = """
        Naming style: musical, key-aware. Spell roots the way the song's key spells them (flats in flat keys: Bb, Eb, Ab; sharps in sharp keys). Qualities: major is the bare root ("Bb"), minor "m" ("Gm"), "maj7", "m7", "7", "dim", "aug", "sus2", "sus4", "add9", "5" for a power chord. Use a slash chord ("Cm7/Bb") when the lowest note is not the root. Two-string shapes other than power chords usually carry no name.
        """;

    /// <summary>
    /// The chart checker's rules the reply can trip, by code, in the words the system prompt
    ///     and the retry share: the model is told them up front, and a rejection names one it has seen.
    ///     The anchor rules are shown to the jobs that move anchors, the finger rules to the jobs that
    ///     set fingers; E04 and I46 (the two agree) to every one of them.
    /// </summary>
    private static readonly (string Code, bool Anchor, bool Finger, string Text)[] RuleList =
    [
        ("L02", true, false, "an anchor fret at or below the capo (the lowest legal fret is capo + 1)."),
        ("L03", true, false, "an anchor whose top fret (F + W - 1) passes 24."),
        ("L04", true, false, "a fretted note with no anchor in force."),
        ("L05", true, false, "a fretted note outside the anchor in force (frets F..F+W-1)."),
        ("E06", true, false, "a phrase that plays notes but has no anchor of its own: every phrase needs an anchor at or after its start and no later than its first sound (an anchor carried over from an earlier phrase does not count). A \"phrase starts\" line marks each phrase start inside the chunk; restate the hand position on that phrase's first sound."),
        ("I20", true, false, "an anchor placed INSIDE a hand shape (after the shape's first chord and before its end). A sound marked \"in-shape\" is inside one: never start an anchor there; move the hand on the shape's first chord or after the shape ends."),
        ("I21", true, false, "the same as I20 on a phrase boundary."),
        ("I47", true, false, "an anchor within 4 ms of the end of an unpitched slide."),
        ("I42", true, false, "a hand position change on a pull-off note."),
        ("I43", true, false, "a slide whose finger changes because an anchor change between its start and its end moves the hand."),
        ("E04", true, true, "a chord that uses the index finger (1) with it on another fret than the anchor in force."),
        ("I46", true, true, "a chord fingering that disagrees with the fingers the anchor in force implies."),
        ("L06", false, true, "a finger on an open or unused string."),
        ("L07", false, true, "a fretted string without a finger."),
        ("L08", false, true, "the index finger on a higher fret than another fretted string."),
        ("I44", false, true, "any finger on a lower fret than the lowest-numbered finger of the chord."),
        ("I45", false, true, "a barre across a string that rings open between the barred strings."),
    ];

    /// <summary>The prompt's line for one rule code ("I20: never …"), or empty for a code the list
    /// does not hold. The rule gate quotes it in a rejection's reason.</summary>
    /// <param name="code">The finding code.</param>
    public static string RuleText(string code)
    {
        foreach (var rule in RuleList)
            if (rule.Code == code) return $"{rule.Code}: {rule.Text}";
        return "";
    }

    /// <summary>The codes a job's system prompt lists.</summary>
    /// <param name="job">The job.</param>
    public static IReadOnlyList<string> RuleCodes(AiChartJob job) =>
        [.. RuleList.Where(r => job.ProposesAnchors() && r.Anchor || job.ProposesFingers() && r.Finger).Select(r => r.Code)];

    private static string WithRules(string system, AiChartJob job)
    {
        var sb = new StringBuilder();
        sb.Append("The chart checker refuses a reply that ADDS any of these findings (code: what it flags); the retry names the code:");
        foreach (var code in RuleCodes(job)) sb.Append('\n').Append("- ").Append(RuleText(code));
        return system.Replace("{RULES}", sb.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The system prompt for a job (identical for every chunk of every song, so a
    /// provider can cache it).</summary>
    /// <param name="job">The job.</param>
    /// <param name="style">The naming style; ignored for the fingering job.</param>
    public static string System(AiChartJob job, ChordNamingStyle style) => job switch
    {
        AiChartJob.FhpAndFingering => WithRules(FingeringSystem, job),
        AiChartJob.FhpOnly => WithRules(FhpOnlySystem, job),
        AiChartJob.FingeringOnly => WithRules(FingeringOnlySystem, job),
        _ => NamesSystem.Replace("{STYLE}", style == ChordNamingStyle.Musical ? MusicalStyle.Trim() : EofStyle.Trim(),
            StringComparison.Ordinal)
    };

    /// <summary>The reply schema for a job.</summary>
    /// <param name="job">The job.</param>
    public static string Schema(AiChartJob job) => job switch
    {
        AiChartJob.FhpAndFingering => FingeringSchema,
        AiChartJob.FhpOnly => FhpSchema,
        AiChartJob.FingeringOnly => FingeringOnlySchema,
        _ => NamesSchema
    };

    /// <summary>The schema's name on the wire.</summary>
    private static string SchemaName(AiChartJob job) => job switch
    {
        AiChartJob.FhpAndFingering => "dotcharter_fhp_fingering",
        AiChartJob.FhpOnly => "dotcharter_fhp",
        AiChartJob.FingeringOnly => "dotcharter_fingering",
        _ => "dotcharter_chord_names"
    };

    /// <summary>The prompt for one chunk.</summary>
    /// <param name="job">The job.</param>
    /// <param name="style">The naming style.</param>
    /// <param name="facts">The song.</param>
    /// <param name="level">The level the chunk was cut from (its anchors are listed).</param>
    /// <param name="beats">The beat map, for anchor times.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="instruction">The AI chat's instruction, appended after the listing
    /// as its own section; null leaves the prompt byte-identical to the menu verbs'.</param>
    public static ToneDesignPrompt Build(
        AiChartJob job, ChordNamingStyle style, AiChartSongFacts facts, ChartLevel level,
        IList<BeatMarker> beats, AiChartChunk chunk, AiChartInstruction? instruction = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(chunk);

        var text = Listing(job, facts, level, beats, chunk) + InstructionSection(instruction);
        return new ToneDesignPrompt(
            System(job, style),
            [new ToneDesignTurn(ToneDesignRole.User, text)],
            Schema(job),
            SchemaName(job));
    }

    /// <summary>The line that opens the instruction section; the tests look for it.</summary>
    public const string InstructionHeader = "--- the charter's instruction for this pass ---";

    /// <summary>The line that opens the conversation excerpt inside the instruction section.</summary>
    public const string ConversationHeader = "--- the conversation before it (oldest first, for context only) ---";

    /// <summary>
    /// The AI chat's instruction as a prompt section of its own, after the listing
    ///     (the system prompt stays the job's, so a provider still caches it). Empty for no
    ///     instruction. The section says the instruction steers the job and cannot widen it:
    ///     what may change is still the job's, and a reply past it is thrown away whole.
    /// </summary>
    /// <param name="instruction">The instruction, or null.</param>
    public static string InstructionSection(AiChartInstruction? instruction)
    {
        if (instruction is null || string.IsNullOrWhiteSpace(instruction.Text)) return "";
        var sb = new StringBuilder(instruction.Text.Length + 512);
        sb.AppendLine();
        sb.AppendLine(InstructionHeader);
        sb.AppendLine(instruction.Text.Trim());
        sb.AppendLine("--- end of instruction ---");
        sb.AppendLine("Follow it in this chunk where it applies, within the rules above: it cannot change what you may " +
            "change. Notes, strings, frets and times stay exactly as given; a reply that changes them is thrown away whole.");
        if (!string.IsNullOrWhiteSpace(instruction.Conversation))
        {
            sb.AppendLine(ConversationHeader);
            sb.AppendLine(instruction.Conversation.Trim());
            sb.AppendLine("--- end of conversation ---");
        }

        return sb.ToString();
    }

    /// <summary>The words that open a retry's last turn; the tests and the eval look for them.</summary>
    public const string RetryHeader = "Your reply for this chunk was REJECTED";

    /// <summary>
    /// The ONE retry of a rejected chunk: the first prompt, the model's own rejected
    ///     reply as its turn, and a last turn naming the rejection. Never built for a retry's
    ///     own rejection: a chunk is asked at most twice.
    /// </summary>
    /// <param name="first">The prompt the rejected reply answered.</param>
    /// <param name="rejectedReply">That reply's JSON.</param>
    /// <param name="rejection">Why it was rejected.</param>
    public static ToneDesignPrompt Retry(ToneDesignPrompt first, string rejectedReply, AiChartRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(rejection);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{RetryHeader} ({rejection.Code}): {rejection.Reason}.\n") +
            "Answer again for the same chunk: the whole reply object, with that fixed and every other rule kept. " +
            "Echo every frets list and ms exactly as listed. This is the only retry.";
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

    /// <summary>The per-chunk listing (internal for the tests).</summary>
    internal static string Listing(
        AiChartJob job, AiChartSongFacts facts, ChartLevel level, IList<BeatMarker> beats, AiChartChunk chunk)
    {
        var arrangement = facts.Arrangement;
        var strings = facts.StringCount;
        var sb = new StringBuilder(4096);
        var inv = CultureInfo.InvariantCulture;

        sb.Append(inv, $"Instrument: {(facts.Bass ? "bass" : "guitar")}, {strings} strings, tuning ");
        sb.Append(string.Join(" ", arrangement.Tuning.Take(strings)));
        sb.Append(inv, $" (lowest first), capo {arrangement.Capo}.");
        if (job == AiChartJob.ChordNames && !string.IsNullOrWhiteSpace(facts.Key)) sb.Append(" Key: ").Append(facts.Key).Append('.');
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(chunk.Phrases)) sb.Append("Phrase: ").AppendLine(chunk.Phrases);

        var templates = new Dictionary<int, ChordTemplate>();
        foreach (var t in arrangement.ChordTemplates) templates.TryAdd(t.Id, t);
        // FHP only: a free fingering is the follower's to re-derive, so only an AUTHORED one
        // (which pins its anchor) is shown.
        var free = job == AiChartJob.FhpOnly ? ChordFingeringFollower.FreeGripResolver(arrangement) : null;

        var from = chunk.Before.Count > 0 ? chunk.Before[0].Position : chunk.Start;
        var to = chunk.After.Count > 0 ? chunk.After[^1].Position : (FractionalPosition?)null;
        var anchors = level.Anchors
            .Where(a => a.Position >= from && (to is not { } t || a.Position <= t))
            .OrderBy(a => a.Position).ToList();
        var inForce = level.Anchors.Where(a => a.Position < from).OrderBy(a => a.Position).LastOrDefault();

        sb.AppendLine();
        if (inForce is not null)
            sb.Append(inv, $"ctx anchor in force before: fret {inForce.Fret} width {inForce.Width}").AppendLine();

        var anchorIndex = 0;
        void AnchorsUpTo(FractionalPosition position, bool inclusive)
        {
            while (anchorIndex < anchors.Count &&
                   (anchors[anchorIndex].Position < position || inclusive && anchors[anchorIndex].Position == position))
            {
                var a = anchors[anchorIndex++];
                var where = chunk.Contains(a.Position) ? "" : "ctx ";
                sb.Append(inv, $"{where}anchor {AiChartChunker.Ms(a.Position, beats)}ms fret {a.Fret} width {a.Width}")
                    .AppendLine();
            }
        }

        foreach (var s in chunk.Before)
        {
            AnchorsUpTo(s.Position, inclusive: true);
            Sound(sb, "ctx ", s, job, facts, templates, free, strings, fix: false);
        }

        AnchorsUpTo(chunk.Start, inclusive: false);
        sb.Append(inv, $"--- chunk starts at {chunk.StartMs}ms (\"start\") ---").AppendLine();
        // a job that moves anchors is shown where E06 and I20 bite — the phrase starts inside
        // the chunk and the sounds an anchor may not start on (inside a hand shape).
        var marks = job.ProposesAnchors() ? AnchorMarks.Of(arrangement, level, chunk) : null;
        foreach (var s in chunk.Core)
        {
            AnchorsUpTo(s.Position, inclusive: true);
            marks?.PhrasesUpTo(sb, s.Position, beats);
            Sound(sb, "", s, job, facts, templates, free, strings, fix: job.ListsChords() && IsFixable(s, templates),
                inShape: marks?.InShape(s.Position) ?? false);
        }

        sb.AppendLine("--- chunk ends ---");
        foreach (var s in chunk.After)
        {
            AnchorsUpTo(s.Position, inclusive: true);
            Sound(sb, "ctx ", s, job, facts, templates, free, strings, fix: false);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     True when the reply must list this sound: a chord bound to a template that has at
    ///     least one fretted string. A note, an untemplated chord or an all-open shape has
    ///     nothing a fingering or a name job could change through a template.
    /// </summary>
    /// <param name="sound">The sound.</param>
    /// <param name="templates">The arrangement's templates by id.</param>
    public static bool IsFixable(AiChartSound sound, IReadOnlyDictionary<int, ChordTemplate> templates)
        => sound.Chord is { TemplateId: >= 0 } chord &&
           templates.TryGetValue(chord.TemplateId, out var t) && t.Frets.Values.Any(f => f > 0);

    /// <summary>The template's frets as one entry per string, −1 for an unused string.</summary>
    /// <param name="template">The template.</param>
    /// <param name="strings">The string count.</param>
    public static int[] FretArray(ChordTemplate template, int strings)
    {
        var frets = new int[strings];
        for (var s = 0; s < strings; s++) frets[s] = template.Frets.TryGetValue(s, out var f) && f >= 0 ? f : -1;
        return frets;
    }

    /// <summary>The template's fingers as one entry per string, −1 for none.</summary>
    /// <param name="template">The template.</param>
    /// <param name="strings">The string count.</param>
    public static int[] FingerArray(ChordTemplate template, int strings)
    {
        var fingers = new int[strings];
        for (var s = 0; s < strings; s++)
            fingers[s] = template.Frets.ContainsKey(s) && template.Fingers.TryGetValue(s, out var f) && f >= 0 ? f : -1;
        return fingers;
    }

    private static void Sound(
        StringBuilder sb, string prefix, AiChartSound s, AiChartJob job, AiChartSongFacts facts,
        Dictionary<int, ChordTemplate> templates, Func<ChordTemplate, ChordTemplate?>? free, int strings, bool fix,
        bool inShape = false)
    {
        var inv = CultureInfo.InvariantCulture;
        sb.Append(prefix).Append(s.Id).Append(' ').Append(s.Ms.ToString(inv)).Append("ms ");
        if (s.Note is { } n)
        {
            sb.Append(inv, $"note string {n.StringIndex} fret {n.Fret}");
            Techniques(sb, n.SlideTo, n.UnpitchedSlide, n.BendValues.Count > 0, n.Hopo, n.Mute, n.Harmonic, n.Sustain);
            if (inShape) sb.Append(" in-shape");
            sb.AppendLine();
            return;
        }

        var chord = s.Chord!;
        if (chord.TemplateId < 0 || !templates.TryGetValue(chord.TemplateId, out var template))
        {
            sb.Append("chord (no template) ");
            sb.Append(string.Join(" ", chord.ChordNotes.Select(cn => $"{cn.StringIndex}:{cn.Fret}")));
            sb.AppendLine();
            return;
        }

        sb.Append(inv, $"chord frets [{string.Join(",", FretArray(template, strings))}]");
        if (job.ProposesFingers() || job == AiChartJob.Explain)
        {
            sb.Append(inv, $" fingers [{string.Join(",", FingerArray(template, strings))}]");
            // Ask / explain is asked about a chord by name too; no other job's line changes.
            if (job == AiChartJob.Explain) sb.Append(" name \"").Append(template.Name).Append('"');
        }
        else if (job == AiChartJob.FhpOnly)
        {
            if (free?.Invoke(template) is null && template.Fingers.Values.Any(f => f >= 0))
                sb.Append(inv, $" fingers [{string.Join(",", FingerArray(template, strings))}] fixed");
        }
        else
        {
            sb.Append(" notes ").Append(string.Join(" ", SoundingNotes(facts.Arrangement, template)));
            var candidates = ChordNameSuggester.SuggestChordNames(facts.Arrangement, template.Frets).Take(3).ToList();
            if (candidates.Count > 0) sb.Append(" namer [").Append(string.Join(", ", candidates)).Append(']');
            sb.Append(" name \"").Append(template.Name).Append('"');
        }

        var first = chord.ChordNotes.FirstOrDefault();
        if (first is not null)
        {
            Techniques(sb, chord.GetSlideTo(), first.UnpitchedSlide, chord.ChordNotes.Any(cn => cn.BendValues.Count > 0),
                first.Hopo, first.Mute, first.Harmonic, chord.GetMaxSustain());
        }

        if (inShape) sb.Append(" in-shape");
        if (fix) sb.Append(" [fix]");
        sb.AppendLine();
    }

    /// <summary>
    /// What a job that moves anchors is shown about E06 and I20: the playable phrase
    ///     markers after the chunk's start and up to its last sound, and the hand shapes a core
    ///     sound can sit strictly inside (where the anchor rule, I20, would flag an anchor).
    /// </summary>
    private sealed class AnchorMarks
    {
        private readonly List<PhraseMarkers.PhraseMarker> _phrases;
        private readonly List<HandShape> _shapes;
        private int _next;

        private AnchorMarks(List<PhraseMarkers.PhraseMarker> phrases, List<HandShape> shapes)
        {
            _phrases = phrases;
            _shapes = shapes;
        }

        public static AnchorMarks Of(Arrangement arrangement, ChartLevel level, AiChartChunk chunk)
        {
            var last = chunk.Core.Count > 0 ? chunk.Core[^1].Position : chunk.Start;
            var phrases = PhraseMarkers.Playable(arrangement)
                .Where(m => m.Position > chunk.Start && m.Position <= last)
                .OrderBy(m => m.Position)
                .ToList();
            var shapes = level.HandShapes.Where(h => h.EndPosition > chunk.Start && h.Position < last).ToList();
            return new AnchorMarks(phrases, shapes);
        }

        public void PhrasesUpTo(StringBuilder sb, FractionalPosition position, IList<BeatMarker> beats)
        {
            while (_next < _phrases.Count && _phrases[_next].Position <= position)
            {
                var m = _phrases[_next++];
                sb.Append(CultureInfo.InvariantCulture,
                        $"--- phrase \"{m.Name}\" starts at {AiChartChunker.Ms(m.Position, beats)}ms: it needs its own anchor (E06) ---")
                    .AppendLine();
            }
        }

        public bool InShape(FractionalPosition position) =>
            _shapes.Any(h => position > h.Position && position < h.EndPosition);
    }

    /// <summary>The sounding pitch of each played string, lowest string first — through the
    /// namer's own tuning arithmetic, one string at a time, so this has no second copy of it.</summary>
    private static IEnumerable<string> SoundingNotes(Arrangement arrangement, ChordTemplate template)
    {
        foreach (var (stringIndex, fret) in template.Frets.Where(kv => kv.Value >= 0).OrderBy(kv => kv.Key))
        {
            var single = ChordNameSuggester.SuggestChordNames(
                arrangement.Tuning, new Dictionary<int, int> { [stringIndex] = fret }, arrangement.Capo);
            if (single.Count > 0) yield return single[0];
        }
    }

    private static void Techniques(
        StringBuilder sb, int? slideTo, bool unpitched, bool bend, HopoType hopo, MuteType mute,
        HarmonicType harmonic, FractionalPosition sustain)
    {
        if (slideTo is { } to) sb.Append(unpitched ? " unpitched-slide->" : " slide->").Append(to.ToString(CultureInfo.InvariantCulture));
        if (bend) sb.Append(" bend");
        if (hopo != HopoType.None) sb.Append(' ').Append(hopo.ToString().ToLowerInvariant());
        if (mute != MuteType.None) sb.Append(' ').Append(mute.ToString().ToLowerInvariant()).Append("-mute");
        if (harmonic != HarmonicType.None) sb.Append(" harmonic");
        if (sustain > FractionalPosition.Zero) sb.Append(" sustain");
    }
}
