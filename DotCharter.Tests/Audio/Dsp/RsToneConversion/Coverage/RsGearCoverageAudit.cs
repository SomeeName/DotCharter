using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Coverage;

/// <summary>How faithfully one piece of Rocksmith gear reaches the rig.</summary>
public enum CoverageStatus
{
    /// <summary>A model of the same circuit, every knob lands, nothing lossy reported.</summary>
    Modelled,

    /// <summary>Reaches the rig, but through a stand-in, a folded knob or a lossy note.</summary>
    Approximated,

    /// <summary>Dropped: the converter emits nothing for it.</summary>
    Missing
}

/// <summary>One RS knob and the rig params it moves (empty = folded).</summary>
public sealed record KnobCoverage(RsGearKnob Knob, IReadOnlyList<string> Params)
{
    public bool Lands => Params.Count > 0;
}

/// <summary>The audit row for one catalogue gear key.</summary>
public sealed record GearCoverage(
    RsGearData Gear,
    int Uses,
    IReadOnlyList<DcRigModule> Modules,
    IReadOnlyList<KnobCoverage> Knobs,
    IReadOnlyList<ConversionNote> Notes,
    CoverageStatus Status,
    IReadOnlyList<string> Reasons);

/// <summary>
///     The Rocksmith gear coverage audit, computed from code so it cannot drift
///     from what the converter does: every catalogue gear key is put through the
///     REAL <see cref="RsToneConverter" /> (<c>ConvertGear</c>), each knob is
///     swept from its catalogue minimum to its maximum to find the rig params it
///     actually moves, and the converter's own <see cref="ConversionReport" />
///     notes are collected. The result renders as
///     <c>docs/agents/services/rs-tone-coverage.md</c>, which
///     <see cref="RsGearCoverageAuditTests" /> keeps byte-identical to this output.
///     <para>
///         Status is MECHANICAL except for one curated input: whether a stand-in
///         model is the same circuit (<see cref="RsGearCoverageJudgements" />).
///         Nothing here claims parity with Rocksmith's own DSP — there is no
///         Rocksmith render to compare against; see the calibration harness.
///     </para>
/// </summary>
public static class RsGearCoverageAudit
{
    /// <summary>The generated guide's path, relative to the repository root.</summary>
    public const string DocRelativePath = "docs/agents/services/rs-tone-coverage.md";

    private static readonly Regex CabVariant =
        new("^(?<stem>.+?)_(?<mic>57|Condenser|Ribbon|Tube)_(?<pos>Cone|Edge|OffAxis)$", RegexOptions.CultureInvariant);

    /// <summary>Audits every gear key in <paramref name="catalog" />.</summary>
    public static IReadOnlyList<GearCoverage> Run(
        RsGearCatalog catalog, RsGearOverrides overrides, RsGearLibraryUsage usage)
    {
        var rows = new List<GearCoverage>();
        foreach (var gear in catalog.AllGear.OrderBy(g => g.Key, StringComparer.Ordinal))
            rows.Add(AuditOne(gear, catalog, overrides, usage));
        return rows;
    }

    /// <summary>The cab family stem of a cabinet key (the key without its mic and
    /// position suffix), or the key itself for a one-off cabinet.</summary>
    public static string CabStem(string cabKey)
    {
        var m = CabVariant.Match(cabKey);
        return m.Success ? m.Groups["stem"].Value : cabKey;
    }

    /// <summary>The (mic, position) a cabinet key encodes, or null for a one-off.</summary>
    public static (string Mic, string Position)? CabMic(string cabKey)
    {
        var m = CabVariant.Match(cabKey);
        return m.Success ? (m.Groups["mic"].Value, m.Groups["pos"].Value) : null;
    }

    private static GearCoverage AuditOne(
        RsGearData gear, RsGearCatalog catalog, RsGearOverrides overrides, RsGearLibraryUsage usage)
    {
        var knobs = gear.Knobs ?? [];
        var defaults = knobs.ToDictionary(k => k.Key, k => k.DefaultValue, StringComparer.Ordinal);

        var notes = new List<ConversionNote>();
        var atDefaults = Convert(gear, defaults, catalog, overrides, notes);

        var knobRows = new List<KnobCoverage>(knobs.Count);
        foreach (var knob in knobs)
        {
            var lo = new Dictionary<string, float>(defaults, StringComparer.Ordinal) { [knob.Key] = knob.MinValue };
            var hi = new Dictionary<string, float>(defaults, StringComparer.Ordinal) { [knob.Key] = knob.MaxValue };
            var a = Flatten(Convert(gear, lo, catalog, overrides, notes));
            var b = Flatten(Convert(gear, hi, catalog, overrides, notes));
            var moved = a.Keys.Union(b.Keys)
                .Where(k => !a.TryGetValue(k, out var va) || !b.TryGetValue(k, out var vb) || MathF.Abs(va - vb) > 1e-6f)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            knobRows.Add(new KnobCoverage(knob, moved));
        }

        var distinctNotes = notes
            .GroupBy(n => (n.Kind, n.Message))
            .Select(g => g.First())
            .OrderBy(n => n.Kind)
            .ThenBy(n => n.Message, StringComparer.Ordinal)
            .ToList();

        var (status, reasons) = Classify(gear, atDefaults, knobRows, distinctNotes, overrides);
        return new GearCoverage(gear, usage.CountOf(gear.Key), atDefaults, knobRows, distinctNotes, status, reasons);
    }

    private static (CoverageStatus, IReadOnlyList<string>) Classify(
        RsGearData gear,
        IReadOnlyList<DcRigModule> modules,
        IReadOnlyList<KnobCoverage> knobs,
        IReadOnlyList<ConversionNote> notes,
        RsGearOverrides overrides)
    {
        if (modules.Count == 0)
        {
            var why = notes.FirstOrDefault(n => n.Kind == ConversionNoteKind.Skipped)?.Message
                      ?? "the converter emits no module";
            return (CoverageStatus.Missing, [why]);
        }

        var reasons = new List<string>();
        foreach (var k in knobs.Where(k => !k.Lands))
            reasons.Add($"knob '{k.Knob.Name}' folds (no param moves)");
        foreach (var n in notes.Where(n => n.Kind is ConversionNoteKind.Approximated or ConversionNoteKind.Fallback))
            reasons.Add(n.Message);

        switch (gear.Type)
        {
            case "Amps":
                // #88: an amp the game ships runs ITS OWN soundbank chain (`rsamp`);
                // what the clone cannot do exactly is a per-knob note, collected above.
                if (modules[0].ModuleTypeId != DcRigTargetCatalog.RsAmpChain)
                    reasons.Add("not the game's own chain: the guitar amp's substring guess");
                break;
            case "Cabinets":
                if (!overrides.TryGetCabModel(gear.Key, out _))
                {
                    reasons.Add("no explicit override: routed by the substring guess");
                }
                else if (!(RsGearCoverageJudgements.SameSpeakerCabStems.TryGetValue(CabStem(gear.Key), out var cab) &&
                           ModelOf(modules[0], "model") == cab.Model))
                {
                    reasons.Add("nearest roster IR, not the same speaker layout: " + ModuleLabel(modules[0]));
                }

                if (CabMic(gear.Key) is { } mic && modules.All(m => m.ModuleTypeId != DcRigTargetCatalog.MicPos))
                    reasons.Add($"mic {mic.Mic} / {mic.Position} discarded (no mic layer)");
                break;
        }

        return (reasons.Count == 0 ? CoverageStatus.Modelled : CoverageStatus.Approximated, reasons);
    }

    private static int ModelOf(DcRigModule module, string param)
        => (int)MathF.Round(module.Params.GetValueOrDefault(param, -1f));

    private static IReadOnlyList<DcRigModule> Convert(
        RsGearData gear, IReadOnlyDictionary<string, float> knobValues,
        RsGearCatalog catalog, RsGearOverrides overrides, List<ConversionNote> notes)
    {
        RsGear rsGear = gear.Type switch
        {
            "Amps" => new RsAmp(gear.Key, gear.Category, knobValues),
            "Cabinets" => new RsCab(gear.Key, gear.Category, knobValues),
            "Racks" => new RsPedal(RsGearRole.Rack, 1, gear.Key, RsGearType.Rack, gear.Category, knobValues),
            _ => new RsPedal(RsGearRole.PrePedal, 1, gear.Key, RsGearType.Pedal, gear.Category, knobValues)
        };

        var report = new ConversionReport();
        var modules = RsToneConverter.ConvertGear(rsGear, catalog, overrides, report);
        notes.AddRange(report.Notes);
        return modules;
    }

    private static Dictionary<string, float> Flatten(IReadOnlyList<DcRigModule> modules)
    {
        // A gear may emit the same module twice (Rack_StudioEQ's second bell), so
        // the second occurrence is "eq#2" — keyed by type alone, the second node
        // would overwrite the first and every knob of the first would "fold".
        var flat = new Dictionary<string, float>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in modules)
        {
            var n = seen[m.ModuleTypeId] = seen.GetValueOrDefault(m.ModuleTypeId) + 1;
            var prefix = n == 1 ? m.ModuleTypeId : m.ModuleTypeId + "#" + n.ToString(CultureInfo.InvariantCulture);
            foreach (var (id, v) in m.Params)
                flat[prefix + "." + id] = v;
        }

        return flat;
    }

    /// <summary>"amp (Plexi 100)" — the module id plus its named model when it has one.</summary>
    public static string ModuleLabel(DcRigModule module)
    {
        var modelParam = RsGearCoverageJudgements.ModelParamOf(module.ModuleTypeId);
        if (modelParam is not null &&
            module.Params.TryGetValue(modelParam, out var v) &&
            RsGearCoverageJudgements.ValueLabel(module.ModuleTypeId, modelParam, v) is { } label)
        {
            return $"{module.ModuleTypeId} ({label})";
        }

        return module.ModuleTypeId;
    }

    // ------------------------------------------------------------------ render

    /// <summary>Renders the audit as the committed guide.</summary>
    public static string Render(IReadOnlyList<GearCoverage> rows, RsGearLibraryUsage usage)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.Append("""
                  # Rocksmith gear coverage — what each RS 2014 gear piece becomes on our rig

                  **GENERATED — do not edit by hand.** `RsGearCoverageAuditTests` renders this
                  file from the REAL converter (`RsToneConverter`) and the embedded gear
                  catalogue (`Tones/tone_gear_data.json`), and fails the gate when it is stale.
                  Regenerate with `DOTCHARTER_REGEN_RS_COVERAGE=1` and
                  `Invoke-Gate.ps1 -FilterClass "*RsGearCoverageAuditTests"`. The library usage
                  column comes from the committed snapshot
                  `DotCharter.Tests/Audio/Dsp/RsToneConversion/Coverage/rs_gear_library_usage.json`
                  (rescan: `DOTCHARTER_RS_LIBRARY_SCAN=1`, read-only).

                  How each row is decided:

                  - every knob is swept from its catalogue minimum to its maximum through
                    `RsToneConverter.ConvertGear`; the params that move are what it reaches, and a
                    knob that moves nothing **folds**;
                  - **✅ modelled** — a module or roster model of the same circuit, every knob
                    lands, and the converter reports nothing lossy;
                  - **🟡 approximated** — it reaches the rig, but a knob folds, the converter
                    reports a lossy decision, or the model is the nearest stand-in rather than
                    the same circuit (that last call is the one curated input:
                    `RsGearCoverageJudgements`);
                  - **❌ missing** — the converter drops it.

                  ✅ never means "sounds identical to Rocksmith": no Rocksmith render exists to
                  compare against. How close the rig is, measured, is the calibration harness's
                  job (`RsToneFidelityHarnessTests`).

                  **This is the conversion WITHOUT a Rocksmith 2014 install** (#755). With one
                  (and the player's key file), every amp the game ships runs its own soundbank
                  chain on `rsamp`, and Rack_StudioEQ, Pedal_Compression, Pedal_LoFiFilter,
                  Pedal_BassEmulator, Pedal_BitCruncher, Bass_Pedal_BassOverdrive,
                  Pedal_AcousticEmulator and six chorus pedals follow the game's own
                  knob curves — read from that install at run time (`RsGameTables`), never
                  stored in the repository. See `docs/agents/services/dsp-tone-rig.md`.


                  """);

        // ----- summary
        sb.Append("## Summary\n\n");
        sb.Append(inv, $"Library snapshot: {usage.Tones} distinct tones in {usage.Packages} packages.\n\n");
        sb.Append("| Type | Keys | ✅ | 🟡 | ❌ | Library uses | ✅ uses | 🟡 uses | ❌ uses |\n");
        sb.Append("|---|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (var type in new[] { "Amps", "Cabinets", "Pedals", "Racks" })
        {
            var t = rows.Where(r => r.Gear.Type == type).ToList();
            AppendSummaryRow(sb, type, t);
        }

        AppendSummaryRow(sb, "**All**", rows);
        sb.Append('\n');

        // ----- ranked gaps
        sb.Append("## Gaps ranked by library use\n\n");
        sb.Append("Every 🟡 and ❌ row that the library uses, most-used first. Cabinets are folded to\n");
        sb.Append("their family (all mic/position variants together).\n\n");
        sb.Append("| Uses | Gear | Status | Becomes | Why |\n|---:|---|---|---|---|\n");
        foreach (var gap in RankedGaps(rows))
        {
            sb.Append(inv, $"| {gap.Uses} | `{gap.Key}` {Escape(gap.Name)} | {Icon(gap.Status)} | {gap.Becomes} | {Escape(gap.Why)} |\n");
        }

        sb.Append('\n');

        // ----- per type
        AppendGearTable(sb, "Amps", rows.Where(r => r.Gear.Type == "Amps"));
        AppendCabTable(sb, rows.Where(r => r.Gear.Type == "Cabinets").ToList());
        AppendGearTable(sb, "Pedals", rows.Where(r => r.Gear.Type == "Pedals"));
        AppendGearTable(sb, "Racks", rows.Where(r => r.Gear.Type == "Racks"));

        // ----- features
        sb.Append("## Feature gaps (not tied to one gear key)\n\n");
        sb.Append("| Feature | State | Detail |\n|---|---|---|\n");
        foreach (var f in RsGearCoverageJudgements.FeatureGaps(rows))
            sb.Append(inv, $"| {f.Feature} | {Icon(f.Status)} | {Escape(f.Detail)} |\n");
        sb.Append('\n');

        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static void AppendSummaryRow(StringBuilder sb, string label, IReadOnlyCollection<GearCoverage> t)
    {
        int Count(CoverageStatus s) => t.Count(r => r.Status == s);
        int Uses(CoverageStatus? s) => t.Where(r => s is null || r.Status == s).Sum(r => r.Uses);
        sb.Append(CultureInfo.InvariantCulture,
            $"| {label} | {t.Count} | {Count(CoverageStatus.Modelled)} | {Count(CoverageStatus.Approximated)} | " +
            $"{Count(CoverageStatus.Missing)} | {Uses(null)} | {Uses(CoverageStatus.Modelled)} | " +
            $"{Uses(CoverageStatus.Approximated)} | {Uses(CoverageStatus.Missing)} |\n");
    }

    /// <summary>A gap line for the ranked table.</summary>
    public sealed record Gap(int Uses, string Key, string Name, CoverageStatus Status, string Becomes, string Why);

    /// <summary>Every used 🟡/❌ gear (cabinets folded to families), most-used first.</summary>
    public static IReadOnlyList<Gap> RankedGaps(IReadOnlyList<GearCoverage> rows)
    {
        var gaps = new List<Gap>();
        foreach (var r in rows.Where(r => r.Gear.Type != "Cabinets" && r.Uses > 0 && r.Status != CoverageStatus.Modelled))
        {
            gaps.Add(new Gap(r.Uses, r.Gear.Key, r.Gear.Name, r.Status, Becomes(r), string.Join("; ", r.Reasons)));
        }

        foreach (var family in rows.Where(r => r.Gear.Type == "Cabinets").GroupBy(r => CabStem(r.Gear.Key)))
        {
            var used = family.Where(r => r.Uses > 0).ToList();
            var gapRows = used.Where(r => r.Status != CoverageStatus.Modelled).ToList();
            if (gapRows.Count == 0) continue;
            var why = string.Join("; ", gapRows.SelectMany(r => r.Reasons.Select(GeneraliseCabReason)).Distinct());
            var worst = gapRows.Max(r => r.Status);
            gaps.Add(new Gap(gapRows.Sum(r => r.Uses), family.Key + "_*", family.First().Gear.Name, worst,
                Becomes(gapRows[0]), why));
        }

        return gaps.OrderByDescending(g => g.Uses).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
    }

    private static string GeneraliseCabReason(string reason)
        => Regex.Replace(reason, "^mic (57|Condenser|Ribbon|Tube) / (Cone|Edge|OffAxis) discarded",
            "mic/position discarded", RegexOptions.CultureInvariant);

    private static string Becomes(GearCoverage r)
        => r.Modules.Count == 0 ? "—" : string.Join(" → ", r.Modules.Select(ModuleLabel));

    private static void AppendGearTable(StringBuilder sb, string type, IEnumerable<GearCoverage> rows)
    {
        sb.Append(CultureInfo.InvariantCulture, $"## {type}\n\n");
        sb.Append("| Gear | Category | Uses | Becomes | Knobs (RS → rig param) | Status | Notes |\n");
        sb.Append("|---|---|---:|---|---|---|---|\n");
        foreach (var r in rows.OrderByDescending(r => r.Uses).ThenBy(r => r.Gear.Key, StringComparer.Ordinal))
        {
            var knobs = r.Knobs.Count == 0
                ? "—"
                : string.Join("<br>", r.Knobs.Select(k => DescribeKnob(k) + KnobVerdict(r, k)));
            sb.Append(CultureInfo.InvariantCulture,
                $"| `{r.Gear.Key}`<br>{Escape(r.Gear.Name)} | {Escape(r.Gear.Category)} | {r.Uses} | {Becomes(r)} | " +
                $"{knobs} | {Icon(r.Status)} | {Escape(string.Join("; ", r.Reasons))} |\n");
        }

        sb.Append('\n');
    }

    private static void AppendCabTable(StringBuilder sb, IReadOnlyList<GearCoverage> cabs)
    {
        sb.Append("## Cabinets\n\n");
        sb.Append("450 keys: 49 families × {57, Condenser, Tube} × {Cone, Edge, OffAxis}, 29 of them also\n");
        sb.Append("with Ribbon, plus 9 one-off cabinets. One row per family; the mic columns show what\n");
        sb.Append("each mic/position variant becomes.\n\n");
        sb.Append("| Family | Name | Variants | Uses | Becomes | ✅ | 🟡 | ❌ | Notes |\n");
        sb.Append("|---|---|---:|---:|---|---:|---:|---:|---|\n");
        foreach (var family in cabs.GroupBy(r => CabStem(r.Gear.Key))
                     .OrderByDescending(g => g.Sum(r => r.Uses)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = family.First();
            var reasons = family.SelectMany(r => r.Reasons.Select(GeneraliseCabReason)).Distinct().ToList();
            sb.Append(CultureInfo.InvariantCulture,
                $"| `{family.Key}` | {Escape(first.Gear.Name)} | {family.Count()} | {family.Sum(r => r.Uses)} | " +
                $"{(first.Modules.Count == 0 ? "—" : ModuleLabel(first.Modules[0]))} | " +
                $"{family.Count(r => r.Status == CoverageStatus.Modelled)} | " +
                $"{family.Count(r => r.Status == CoverageStatus.Approximated)} | " +
                $"{family.Count(r => r.Status == CoverageStatus.Missing)} | {Escape(string.Join("; ", reasons))} |\n");
        }

        sb.Append('\n');
        sb.Append("### Mic and position\n\n");
        sb.Append("What one representative family's nine variants become (the mic layer is the same for every family).\n\n");
        sb.Append("| Variant | Uses (all families) | Mic layer |\n|---|---:|---|\n");
        foreach (var variant in cabs.Where(r => CabMic(r.Gear.Key) is not null)
                     .GroupBy(r => CabMic(r.Gear.Key)!.Value)
                     .OrderBy(g => g.Key.Mic, StringComparer.Ordinal).ThenBy(g => g.Key.Position, StringComparer.Ordinal))
        {
            var sample = variant.First();
            var mic = sample.Modules.FirstOrDefault(m => m.ModuleTypeId == DcRigTargetCatalog.MicPos);
            var layer = mic is null
                ? "— (discarded)"
                : $"{ModuleLabel(mic)} position {mic.Params.GetValueOrDefault("position").ToString("0.##", CultureInfo.InvariantCulture)}"
                  + (mic.Params.GetValueOrDefault("angleDeg") > 0f
                      ? $", angle {mic.Params["angleDeg"].ToString("0.#", CultureInfo.InvariantCulture)}°"
                      : "");
            sb.Append(CultureInfo.InvariantCulture,
                $"| {variant.Key.Mic} / {variant.Key.Position} | {variant.Sum(r => r.Uses)} | {layer} |\n");
        }

        sb.Append('\n');
    }

    /// <summary>
    ///     A knob of a game chain (<c>rsamp</c>, #88) carries its own verdict: ✅ when
    ///     its curves only move what the clone does exactly (Guitar Distortion EQ
    ///     bands, FutzBox levels), 🟡 when the converter names it in a
    ///     <c>knob '…':</c> note (the row's Notes say why). Other gear has no
    ///     per-knob verdict: its knobs land on a model, judged as a whole.
    /// </summary>
    public static string KnobVerdict(GearCoverage row, KnobCoverage k)
    {
        if (row.Modules.Count == 0 || row.Modules[0].ModuleTypeId != DcRigTargetCatalog.RsAmpChain || !k.Lands) return "";
        return KnobIsApproximated(row, k) ? " 🟡" : " ✅";
    }

    /// <summary>Whether the converter reported this knob of a game chain as inexact.</summary>
    public static bool KnobIsApproximated(GearCoverage row, KnobCoverage k)
        => row.Notes.Any(n => n.Message.StartsWith($"knob '{k.Knob.Name}':", StringComparison.Ordinal));

    private static string DescribeKnob(KnobCoverage k)
    {
        var range = string.Create(CultureInfo.InvariantCulture,
            $"{k.Knob.MinValue:0.##}..{k.Knob.MaxValue:0.##}{Unit(k.Knob.UnitType)}");
        return k.Lands
            ? $"{Escape(k.Knob.Name)} [{range}] → {string.Join(", ", k.Params.Select(p => "`" + p + "`"))}"
            : $"{Escape(k.Knob.Name)} [{range}] → **folds**";
    }

    private static string Unit(RsKnobUnit unit) => unit switch
    {
        RsKnobUnit.Percent => " %",
        RsKnobUnit.Db => " dB",
        RsKnobUnit.Hz => " Hz",
        RsKnobUnit.Khz => " kHz",
        RsKnobUnit.Ms => " ms",
        RsKnobUnit.Enum => " (enum)",
        _ => ""
    };

    private static string Icon(CoverageStatus s) => s switch
    {
        CoverageStatus.Modelled => "✅",
        CoverageStatus.Approximated => "🟡",
        _ => "❌"
    };

    private static string Escape(string? text) => (text ?? "").Replace("|", "\\|").Replace("\n", " ").Trim();
}
