using System.Globalization;
using System.Text;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     The catalogue-grounded prompt that turns measured features + context into
///     Rocksmith GEAR.
///     <para>
///         <b>It grounds the model in the game's own closed catalogue</b>, because a
///         model asked for "a Marshall-ish amp" will name one Rocksmith does not
///         have. Every amp, pedal and rack is listed with its exact key, its
///         category, and every knob with its unit and range; cabinets are listed as
///         MODEL stems plus the nine mic/position suffixes, because 450 full keys is
///         twelve kilobytes of prompt describing one choice made twice.
///     </para>
///     <para>
///         <b>The catalogue is GENERATED from <see cref="RsGearCatalog" />, never
///         hand-written</b> — the same rule the tone-design prompt follows. A
///         hand-written list is a second copy of the game's data that rots silently
///         and teaches the model gear the validator will then reject.
///     </para>
///     <para>
///         <b>The guidance is deliberately thin.</b> Rocksmith's rules that this
///         prompt states are the ones the FORMAT enforces (four slots a rail, a bass
///         arrangement takes bass-prefixed amps and cabs, an amp always has a
///         cabinet); it does not tell the model which amp is "right" for a tilt of
///         −7 dB/octave, because that is the judgement it is being asked for and
///         baking a table of it in would make the model a lookup nobody measured.
///     </para>
/// </summary>
public static class RsToneGeneratePrompt
{
    /// <summary>The reply schema's name, for a provider that takes one.</summary>
    public const string SchemaName = "rs_tone";

    /// <summary>The bass gear key prefix. Bass amps and cabs carry it; guitar gear
    /// does not, and the two pools do not mix in the game.</summary>
    public const string BassPrefix = "Bass_";

    private static readonly Lazy<string> GuitarCatalogue =
        new(() => BuildCatalogue(RsGearCatalog.Shared, bass: false));

    private static readonly Lazy<string> BassCatalogue =
        new(() => BuildCatalogue(RsGearCatalog.Shared, bass: true));

    /// <summary>The catalogue block for an instrument, cached — it is a stable
    /// prompt PREFIX, so a provider's prompt cache can hit it.</summary>
    /// <param name="bass">True for a bass arrangement.</param>
    public static string CatalogueBlock(bool bass) => bass ? BassCatalogue.Value : GuitarCatalogue.Value;

    /// <summary>The JSON schema the reply must match.</summary>
    public static string Schema { get; } = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "descriptors", "volumeDb", "amp", "cabinet"],
          "properties": {
            "name": { "type": "string" },
            "descriptors": { "type": "array", "items": { "type": "string" }, "maxItems": 4 },
            "volumeDb": { "type": "number" },
            "rationale": { "type": "string" },
            "amp": { "$ref": "#/$defs/gear" },
            "cabinet": { "$ref": "#/$defs/gear" },
            "prePedals": { "type": "array", "maxItems": 4, "items": { "$ref": "#/$defs/gear" } },
            "racks": { "type": "array", "maxItems": 4, "items": { "$ref": "#/$defs/gear" } },
            "postPedals": { "type": "array", "maxItems": 4, "items": { "$ref": "#/$defs/gear" } }
          },
          "$defs": {
            "gear": {
              "type": "object",
              "additionalProperties": false,
              "required": ["key"],
              "properties": {
                "key": { "type": "string" },
                "knobs": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["pot", "value"],
                    "properties": { "pot": { "type": "string" }, "value": { "type": "number" } }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>The system prompt for one instrument.</summary>
    /// <param name="bass">True for a bass arrangement.</param>
    public static string SystemPrompt(bool bass) => Rules(bass) + "\n\n" + CatalogueBlock(bass);

    /// <summary>
    ///     Builds the prompt for one window.
    /// </summary>
    /// <param name="request">What is known about the window.</param>
    public static ToneDesignPrompt Build(RsToneMapRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bass = string.Equals(request.Instrument, ToneStems.Bass, StringComparison.OrdinalIgnoreCase);

        var user = new StringBuilder(2048);
        user.Append("Instrument: ").AppendLine(request.Instrument);
        user.Append("Tone key to use: ").AppendLine(request.ToneKey);
        user.Append("Tone name to use: ").AppendLine(request.ToneName);

        if (!string.IsNullOrWhiteSpace(request.SongTitle) || !string.IsNullOrWhiteSpace(request.Artist))
        {
            user.AppendLine(CultureInfo.InvariantCulture,
                $"Song: {request.SongTitle ?? "(unknown)"} — {request.Artist ?? "(unknown artist)"}");
        }

        if (request.Features is { } features)
        {
            user.AppendLine();
            user.AppendLine("MEASURED from the isolated stem for this exact stretch of the song:");
            user.Append(features.ToPromptLines());
        }
        else
        {
            user.AppendLine();
            user.AppendLine(
                "No measurements are available for this stretch — work from the context below alone, " +
                "and say so in the rationale.");
        }

        // The charter's NAME for the tone, when it says something about the sound ("Clean",
        // "Dist2", "Lead") — a soft hint only; a generic one ("Tone B", "Base", the song's own
        // name) is left out entirely rather than handed over as noise (owner).
        if (RsToneNameHint.Of(request.ToneName, request.SongTitle, request.ArrangementName) is { } named)
        {
            user.AppendLine();
            user.AppendLine(NameHintLine(named));
        }

        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            user.AppendLine();
            user.AppendLine("CONTEXT the charter supplied (band, era, known rig, intent):");
            user.AppendLine(request.Context!.Trim());
        }

        user.Append(PreviousSection(request.PreviousProposal));
        user.Append(InstructionSection(request.Instruction));

        user.AppendLine();
        user.AppendLine("Answer with the JSON object only.");

        return new ToneDesignPrompt(
            SystemPrompt(bass),
            [new ToneDesignTurn(ToneDesignRole.User, user.ToString())],
            Schema,
            SchemaName);
    }

    /// <summary>The line that opens the refined proposal's section; the tests look for it.</summary>
    public const string PreviousHeader = "--- the current proposal, to be refined ---";

    /// <summary>
    /// The proposal a Refine changes, as its manifest JSON, in a section of its own before the
    ///     instruction: keep what the instruction does not ask to change. Empty for none, so a first
    ///     ask's prompt has no such section.
    /// </summary>
    /// <param name="previous">The previous proposal's JSON, or null.</param>
    public static string PreviousSection(string? previous)
    {
        if (string.IsNullOrWhiteSpace(previous)) return "";
        var sb = new StringBuilder(previous.Length + 256);
        sb.AppendLine();
        sb.AppendLine(PreviousHeader);
        sb.AppendLine(previous.Trim());
        sb.AppendLine("--- end of current proposal ---");
        sb.AppendLine("Start from this tone and change only what the instruction below asks for.");
        return sb.ToString();
    }

    /// <summary>The line that opens the AI chat's instruction section; the tests look for it.</summary>
    public const string InstructionHeader = "--- the charter's instruction for these tones ---";

    /// <summary>
    /// The soft name hint: the charter's own name for the tone, said to be a hint the
    ///     measurements and the instruction overrule. Only for a name <see cref="RsToneNameHint" />
    ///     judged descriptive.
    /// </summary>
    /// <param name="name">The descriptive name.</param>
    public static string NameHintLine(string name) =>
        $"The charter named this tone \"{name.Trim()}\". Treat that as a SOFT hint about its character only " +
        "(clean, crunch, lead, acoustic, an effect): the measurements above and the charter's instruction below " +
        "win wherever they disagree with it.";

    /// <summary>
    /// The AI chat's instruction for these tones as a section of its own after the
    ///     context: what the charter typed, collapsed to one line by the chat. Empty for none, so
    ///     the menu verb's prompt has no such section. It steers the CHOICE of gear and cannot
    ///     widen it: the catalogue rules above still decide what a reply may name.
    /// </summary>
    /// <param name="instruction">The instruction, or null.</param>
    public static string InstructionSection(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return "";
        var sb = new StringBuilder(instruction.Length + 256);
        sb.AppendLine();
        sb.AppendLine(InstructionHeader);
        sb.AppendLine(instruction.Trim());
        sb.AppendLine("--- end of instruction ---");
        sb.AppendLine("Follow it for this tone where it applies (it may name one tone of several), within the " +
            "rules above: only catalogue gear, every knob inside its printed range.");
        return sb.ToString();
    }

    private static string Rules(bool bass) =>
        $"""
        You choose Rocksmith 2014 in-game gear for one stretch of a song.

        Answer with ONE JSON object matching the schema. No prose outside it.

        HARD RULES — a reply that breaks any of these is rejected, not repaired:
        - Use ONLY gear keys from the catalogue below, spelled exactly. Do not invent
          a key, and do not translate a real-world pedal name into one.
        - Every knob value must be inside the range printed beside that knob, in the
          unit printed beside it. A knob marked `enum` takes a whole number selecting
          one of its listed settings, counting from 0.
        - Name a knob by its POT (the token after the last underscore of its full
          key): `Gain`, `Bass`, `Mix`.
        - `amp` and `cabinet` are both required. A cabinet key is
          `{(bass ? "Bass_Cab" : "Cab")}_<model>_<mic>_<position>` — compose it from
          the model stems and the mic/position suffixes listed below.
        - At most four entries in each of `prePedals`, `racks` and `postPedals`.
          Leave a rail out entirely rather than sending an empty object.
        - `volumeDb` is between {RsToneAuthoring.MinVolumeDb.ToString("0.#", CultureInfo.InvariantCulture)}
          and {RsToneAuthoring.MaxVolumeDb.ToString("0.#", CultureInfo.InvariantCulture)};
          {RsToneAuthoring.DefaultVolumeDb.ToString("0.#", CultureInfo.InvariantCulture)} is the usual value.
        - `descriptors` are chosen from: {string.Join(", ", RsToneAuthoring.Descriptors.Select(d => d.Name))}.
        - `name` is what a player sees. `rationale` is one sentence saying which
          measurement or which piece of context drove the choice.
        {(bass
            ? "- This is a BASS arrangement: use the bass-prefixed amps and cabinets below."
            : "- This is a GUITAR arrangement: bass-prefixed gear is not offered below and must not be used.")}

        Only a knob you deliberately set needs to appear; everything you omit takes
        the game's own default, which is usually what you want.
        """;

    private static string BuildCatalogue(RsGearCatalog catalog, bool bass)
    {
        var sb = new StringBuilder(32 * 1024);

        sb.AppendLine("# AMPS");
        foreach (var gear in Slice(catalog, RsGearType.Amp, bass))
            AppendGear(sb, gear);

        sb.AppendLine();
        sb.AppendLine("# CABINETS");
        AppendCabinets(sb, catalog, bass);

        sb.AppendLine();
        sb.AppendLine("# PEDALS (prePedals / postPedals)");
        foreach (var group in Slice(catalog, RsGearType.Pedal, bass).GroupBy(g => g.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("## ").AppendLine(group.Key);
            foreach (var gear in group) AppendGear(sb, gear);
        }

        sb.AppendLine();
        sb.AppendLine("# RACKS");
        foreach (var group in Slice(catalog, RsGearType.Rack, bass).GroupBy(g => g.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("## ").AppendLine(group.Key);
            foreach (var gear in group) AppendGear(sb, gear);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     The gear an instrument may use. Amps and cabinets split cleanly on the
    ///     <see cref="BassPrefix" />; PEDALS and RACKS do not — the game offers the
    ///     bass-voiced ones beside the rest on a bass board, and several general
    ///     pedals are used on both — so for bass they are offered whole.
    /// </summary>
    private static IEnumerable<RsGearData> Slice(RsGearCatalog catalog, RsGearType type, bool bass)
    {
        var all = RsToneAuthoring.GearOptions(catalog, type);
        if (type is RsGearType.Pedal or RsGearType.Rack)
            return bass ? all : all.Where(g => !IsBass(g.Key));
        return all.Where(g => IsBass(g.Key) == bass);
    }

    private static bool IsBass(string key) => key.StartsWith(BassPrefix, StringComparison.Ordinal);

    private static void AppendGear(StringBuilder sb, RsGearData gear)
    {
        sb.Append(gear.Key).Append(" — ").Append(gear.Name);
        if (gear.Knobs is { Count: > 0 } knobs)
        {
            sb.Append(" — ");
            for (var i = 0; i < knobs.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                var k = knobs[i];
                sb.Append(k.Pot).Append(' ')
                    .Append(Num(k.MinValue)).Append("..").Append(Num(k.MaxValue));
                var unit = UnitText(k.UnitType);
                if (unit is not null) sb.Append(' ').Append(unit);
                sb.Append(", def ").Append(Num(k.DefaultValue));
                if (k.UnitType == RsKnobUnit.Enum && k.EnumValues is { Count: > 0 } options)
                    sb.Append(" [").Append(string.Join('/', options)).Append(']');
            }
        }

        sb.AppendLine();
    }

    /// <summary>
    ///     Cabinets as MODEL stems + the mic/position suffixes they combine with.
    ///     450 full keys is 12 KB of prompt for what is two choices; the composed key
    ///     is validated against the catalogue afterwards, so an impossible
    ///     combination is caught rather than trusted.
    /// </summary>
    private static void AppendCabinets(StringBuilder sb, RsGearCatalog catalog, bool bass)
    {
        var cabs = Slice(catalog, RsGearType.Cabinet, bass).ToList();
        var prefix = bass ? BassPrefix + "Cab_" : "Cab_";

        var stems = new SortedSet<string>(StringComparer.Ordinal);
        var suffixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var cab in cabs)
        {
            if (!cab.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = cab.Key[prefix.Length..];
            // <model>_<mic>_<position>: the last two tokens are the mic rig.
            var split = rest.LastIndexOf('_');
            if (split <= 0) continue;
            var second = rest.LastIndexOf('_', split - 1);
            if (second <= 0) continue;
            stems.Add(rest[..second]);
            suffixes.Add(rest[(second + 1)..]);
        }

        sb.Append("Compose a key as ").Append(prefix).AppendLine("<model>_<mic>_<position>.");
        sb.Append("models: ").AppendLine(string.Join(", ", stems));
        sb.Append("mic_position: ").AppendLine(string.Join(", ", suffixes));
        sb.AppendLine("Cabinets have no knobs.");
    }

    private static string? UnitText(RsKnobUnit unit) => unit switch
    {
        RsKnobUnit.Percent => "%",
        RsKnobUnit.Db => "dB",
        RsKnobUnit.Hz => "Hz",
        RsKnobUnit.Khz => "kHz",
        RsKnobUnit.Ms => "ms",
        RsKnobUnit.Enum => "(enum)",
        _ => null
    };

    private static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
