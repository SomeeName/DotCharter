using System.Globalization;
using System.Text;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Ai;

/// <summary>
///     One row of the house idiom: the catalogue module DotCharter's own
///     Rocksmith converter produces, and the real gear that lands on it.
/// </summary>
/// <param name="ModuleTypeId">The catalogue module the converter emits.</param>
/// <param name="RsCategory">The Rocksmith routing category the gear sits in
///     (<c>Distortion</c>, <c>Delay</c>, <c>Amp</c>, …) — the converter's own
///     dispatch key, not a tonal family.</param>
/// <param name="Slot">Where the real gear lives: <c>pedal</c>, <c>rack</c>,
///     <c>bass pedal</c>, <c>amp</c>, <c>bass amp</c>, <c>DI preamp</c>. Read
///     off the gear KEY and TYPE, and load-bearing twice over — it is the
///     difference between the <c>Compression</c> stompbox (<c>optocomp</c>) and
///     the <c>Studio Compressor</c> rack (<c>compressor</c>), and it is where in
///     the chain the module goes, because <c>RsTone.InSignalOrder</c> wires
///     pedals in FRONT of the amp and racks AFTER the cab.</param>
/// <param name="Examples">Real gear display names that convert to this module,
///     ordinal-sorted and capped at
///     <see cref="RsConverterIdiom.MaxExamplesPerModule" />.</param>
/// <param name="TotalGear">How many catalogue entries land on this module in
///     total — <see cref="Examples" /> is a sample of it.</param>
public sealed record RsIdiomRow(
    string ModuleTypeId,
    string RsCategory,
    string Slot,
    IReadOnlyList<string> Examples,
    int TotalGear);

/// <summary>
///     <b>The house idiom, DERIVED by running the shipping converter.</b>
///
///     <para>
///         The scorer's ground truth — and the chain the app really plays when a
///         player loads a Rocksmith song — is <c>RsToneConverter</c>'s output. That
///         converter maps a large, very specific real-world gear catalogue onto a
///         small set of this build's modules, and the mapping lives in three
///         places at once: <c>rs_gear_overrides.json</c>'s <c>targetModule</c>
///         table, the substring routing inside <c>RsToneConverter</c>, and the RS
///         gear catalogue's own category. A model that does not know it answers
///         <c>compressor</c> where this app means <c>optocomp</c> and
///         <c>reverb</c> where it means <c>springreverb</c> — the same sound,
///         a different module.
///     </para>
///     <para>
///         <b>So this is not a table.</b> Every row is produced by handing the
///         real <see cref="RsToneConverter" /> a synthetic one-pedal tone built
///         from a real catalogue entry and reading back which module came out.
///         A converter change moves this block on the next build, which a
///         hand-written mapping could never do — and the two could not drift
///         apart even in principle, because there is only one of them.
///     </para>
///     <para>
///         Cabinets are the one collapsed case: all 450 of them convert to
///         <c>cab</c> with nothing but the model integer differing, so they are
///         summarised rather than sampled. Everything else is measured.
///     </para>
/// </summary>
public static class RsConverterIdiom
{
    /// <summary>How many real gear names a row prints. Four is enough to show a
    /// model what KIND of pedal lands there; the full list is 613 entries and
    /// the prompt is charged by the token.</summary>
    public const int MaxExamplesPerModule = 4;

    private static readonly Lazy<IReadOnlyList<RsIdiomRow>> RowsCache = new(Derive, isThreadSafe: true);
    private static readonly Lazy<string> BlockCache = new(Render, isThreadSafe: true);

    /// <summary>Every (module, RS category) pair the converter can produce, in
    /// the order the prompt prints them: by RS category, then by module id.
    /// Deterministic — it is part of a cached prompt prefix.</summary>
    public static IReadOnlyList<RsIdiomRow> Rows => RowsCache.Value;

    /// <summary>The rendered prompt section.</summary>
    public static string Block => BlockCache.Value;

    /// <summary>The set of module ids the converter can ever emit. Everything
    /// else in the catalogue has no Rocksmith counterpart at all.</summary>
    public static IReadOnlySet<string> ReachableModules { get; } =
        Rows.Select(r => r.ModuleTypeId).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<RsIdiomRow> Derive()
    {
        // Part of a CACHED prompt prefix, so never a function of whether the
        // player's game tables had loaded when it first ran: derived on the
        // generic mapping every install shares.
        using var generic = RsGameTables.Scope(RsGameTables.Empty);
        var catalogue = RsGearCatalog.Shared;
        var converter = new RsToneConverter();

        // (module, RS category, slot) -> the real gear names that land there.
        var byRow = new Dictionary<(string Module, string Category, string Slot), List<string>>();

        void Record(string module, string category, string slot, string name)
        {
            var key = (module, category, slot);
            if (!byRow.TryGetValue(key, out var names)) byRow[key] = names = [];
            names.Add(name);
        }

        foreach (var gear in catalogue.AllGear)
        {
            var type = TypeOf(gear.Type);
            if (type == RsGearType.Cabinet) continue; // collapsed below

            var knobs = DefaultKnobs(gear);
            var category = gear.Category ?? "";
            var slot = SlotOf(gear.Key, type);

            if (type == RsGearType.Amp)
            {
                // Every catalogue amp is one the game ships, and converts to its OWN
                // chain (`rsamp`) wherever the install's tables are loaded — which
                // a design cannot write (AiGearNotes.NotDesignable): the idiom names
                // the designable archetype for its slot instead.
                Record(AmpStandIn(slot), category, slot, gear.Name);
                continue;
            }

            var pedal = new RsPedal(
                RsGearRole.PrePedal, 1, gear.Key, type, category, knobs);
            var withPedal = SyntheticTone(NeutralAmp(), [pedal]);
            foreach (var module in converter.Convert(withPedal, out _).Modules)
            {
                // Everything a NEUTRAL amp + default cab tone contributes on its
                // own: the amp, its cab and the normalizer's trailing level trim.
                if (module.ModuleTypeId is "amp" or "cab" or "gain") continue;
                Record(module.ModuleTypeId, category, slot, gear.Name);
            }
        }

        var rows = byRow
            .Select(kv => new RsIdiomRow(
                kv.Key.Module,
                kv.Key.Category,
                kv.Key.Slot,
                kv.Value.Order(StringComparer.Ordinal).Take(MaxExamplesPerModule).ToList(),
                kv.Value.Count))
            .ToList();

        rows.Add(new RsIdiomRow(
            "cab", "Cabinet", "cabinet", ["every cabinet in the game"],
            catalogue.Count(RsGearType.Cabinet)));

        rows.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.RsCategory, b.RsCategory);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.ModuleTypeId, b.ModuleTypeId);
            return c != 0 ? c : string.CompareOrdinal(a.Slot, b.Slot);
        });

        return rows;
    }

    private static string Render()
    {
        var sb = new StringBuilder(8 * 1024);
        sb.AppendLine("# The house idiom — which module stands for which real pedal");
        sb.AppendLine();
        sb.AppendLine("DotCharter converts real Rocksmith rigs onto these modules, and this is the");
        sb.AppendLine("vocabulary its own tones are written in. When a request implies a real pedal,");
        sb.AppendLine("answer with the SPECIFIC module below rather than the generic one — `optocomp`");
        sb.AppendLine("for a squashy compressor pedal, `springreverb` for a spring tank, `bbddelay`");
        sb.AppendLine("for an analog echo. Listed by the gear's own Rocksmith category.");
        sb.AppendLine();
        sb.AppendLine("The slot in brackets is where the real gear lives, and it decides ORDER as");
        sb.AppendLine("well as identity: a PEDAL is wired in front of the amp, a RACK after the cab.");
        sb.AppendLine("That is why a graphic EQ, a reverb and a studio compressor come after the");
        sb.AppendLine("cabinet in these rigs while a stompbox compressor comes before the amp.");
        sb.AppendLine();

        string? category = null;
        foreach (var row in Rows)
        {
            if (!string.Equals(category, row.RsCategory, StringComparison.Ordinal))
            {
                category = row.RsCategory;
                sb.Append("## ").AppendLine(category.Length == 0 ? "Uncategorised" : category);
            }

            sb.Append(row.ModuleTypeId).Append(" [").Append(row.Slot).Append("] <- ")
                .Append(string.Join(", ", row.Examples));
            if (row.TotalGear > row.Examples.Count)
            {
                sb.Append(" (+").Append(
                        (row.TotalGear - row.Examples.Count).ToString(CultureInfo.InvariantCulture))
                    .Append(" more)");
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("A module that is NOT in this list has no Rocksmith counterpart — reach for one");
        sb.AppendLine("only when the request names that effect itself.");
        sb.AppendLine();
        sb.Append(RenderByFamily());
        return sb.ToString();
    }

    /// <summary>
    ///     The same set again, keyed by TONAL family instead of by the gear's
    ///     Rocksmith category — because that is the question a designer actually
    ///     asks ("what is the reverb here?") and because a family whose entire
    ///     membership is three named rooms is how a model learns there is no
    ///     plain <c>reverb</c> to reach for. Families come from
    ///     <see cref="AiGearNotes" />' own category column, which is the one
    ///     place this assembly classifies a module at all.
    /// </summary>
    private static string RenderByFamily()
    {
        var familyOf = AiGearNotes.All.ToDictionary(n => n.Id, n => n.Category, StringComparer.Ordinal);

        var byFamily = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var bass = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (!familyOf.TryGetValue(row.ModuleTypeId, out var family)) continue;
            if (!byFamily.TryGetValue(family, out var set))
                byFamily[family] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(row.ModuleTypeId);
            if (row.Slot.StartsWith("bass", StringComparison.Ordinal)) bass.Add(row.ModuleTypeId);
        }

        var sb = new StringBuilder(1024);
        sb.AppendLine("## Everything a converted rig can contain, by family");
        sb.AppendLine();
        sb.AppendLine("This is the WHOLE vocabulary — a real rig is built out of these and nothing");
        sb.AppendLine("else. Note what is absent from each line: there is no plain `reverb` in one of");
        sb.AppendLine("these rigs, only a spring, a plate or a hall.");
        foreach (var (family, ids) in byFamily)
            sb.Append(family).Append(": ").AppendLine(string.Join(", ", ids));
        sb.AppendLine();
        sb.Append("Gear Rocksmith ships for BASS only, i.e. what a bass rig is made of: ")
            .AppendLine(string.Join(", ", bass));
        return sb.ToString();
    }

    /// <summary>Where a piece of real gear sits, read off its KEY and TYPE.
    /// Rocksmith prefixes bass-only gear <c>Bass_</c> and its DI preamps
    /// <c>DI_</c>, which is the only signal either fact has — the display name
    /// does not always carry it (<c>MB Comp</c> is a bass pedal and says so
    /// nowhere).</summary>
    private static string SlotOf(string gearKey, RsGearType type)
    {
        var bass = gearKey.StartsWith("Bass_", StringComparison.Ordinal);
        return type switch
        {
            RsGearType.Amp when gearKey.StartsWith("DI_", StringComparison.Ordinal) => "DI preamp",
            RsGearType.Amp => bass ? "bass amp" : "amp",
            RsGearType.Rack => bass ? "bass rack" : "rack",
            _ => bass ? "bass pedal" : "pedal"
        };
    }

    /// <summary>The designable archetype a design writes for an amp in
    /// <paramref name="slot" />: the guitar amp, the bass head or the valve DI.</summary>
    private static string AmpStandIn(string slot) => slot switch
    {
        "bass amp" => "bassamp",
        "DI preamp" => "tubepre",
        _ => "amp"
    };

    private static RsGearType TypeOf(string type) => type switch
    {
        "Amps" or "Amp" => RsGearType.Amp,
        "Cabinets" or "Cabinet" => RsGearType.Cabinet,
        "Pedals" or "Pedal" => RsGearType.Pedal,
        "Racks" or "Rack" => RsGearType.Rack,
        _ => RsGearType.Unknown
    };

    /// <summary>Every knob the catalogue declares, at its own default. The
    /// converter's routing reads the POTS as well as the key
    /// (<c>HasAny(gear, "Pitch")</c> picks the harmonizer over the octaver), so a
    /// gear item with no knobs would route differently from the real one.</summary>
    private static Dictionary<string, float> DefaultKnobs(RsGearData gear)
    {
        var knobs = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var knob in gear.Knobs ?? [])
            knobs[knob.Key] = knob.DefaultValue;
        return knobs;
    }

    private static RsAmp NeutralAmp()
        => new(RsToneJsonParser.SyntheticAmpKey, "Amp",
            new Dictionary<string, float>(StringComparer.Ordinal));

    private static RsTone SyntheticTone(RsAmp amp, IReadOnlyList<RsPedal> prePedals)
        => new(
            "idiom-probe", "idiom-probe", 0f, [],
            amp,
            new RsCab(RsToneJsonParser.DefaultCabinetKey, "Cabinet",
                new Dictionary<string, float>(StringComparer.Ordinal)),
            prePedals, [], []);
}
