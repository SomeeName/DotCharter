using DotCharter.Localization;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
/// One proposed tone, as the review card lists it: the tone definition it would replace
///     (or create), the gear chain proposed beside the current one, the song window it was
///     generated from, how it came to exist, and the charter's decision.
/// </summary>
public sealed class AiToneReviewItem
{
    internal AiToneReviewItem(string key, string name, ToneWindow window, RsTone proposed, string? currentJson,
        RsTone? current, ToneGenerationProvenance? provenance)
    {
        Key = key;
        Name = name;
        Window = window;
        Proposed = proposed;
        ProposedJson = RsToneJsonWriter.Write(proposed);
        CurrentJson = currentJson;
        Current = current;
        Provenance = provenance;
    }

    /// <summary>The tone definition's key: what the placed tone changes name.</summary>
    public string Key { get; }

    /// <summary>Its display name (the definition's, else the key).</summary>
    public string Name { get; }

    /// <summary>The song window the tone was generated from (one measure of lead, clamped).</summary>
    public ToneWindow Window { get; }

    /// <summary>The proposed tone, catalogue-valid, keyed <see cref="Key" />.</summary>
    public RsTone Proposed { get; }

    /// <summary>The proposed body, as <see cref="RsToneJsonWriter" /> writes it — what Apply stores and
    /// what an audition converts.</summary>
    public string ProposedJson { get; }

    /// <summary>The definition's body when the proposal was held, or null when the project had no
    /// definition under <see cref="Key" /> (Apply then adds one). The stale guard: a body edited
    /// meanwhile is left alone.</summary>
    public string? CurrentJson { get; }

    /// <summary>The current tone, parsed, or null when there is none or its body does not parse.</summary>
    public RsTone? Current { get; }

    /// <summary>The window, stem, sources, model, rationale and confidence.</summary>
    public ToneGenerationProvenance? Provenance { get; }

    /// <summary>The charter's decision.</summary>
    public AiChartDecision Decision { get; internal set; }
}

/// <summary>
/// The Tones job's write: the ACCEPTED tones' definitions, as ONE
///     <see cref="CompositeAction" /> of <see cref="UpsertToneDefinitionAction" />s — the Tone
///     designer's own action, so undo puts the very definition objects back. Nothing else is
///     reachable from here: no tone change, no arrangement tone slot, no note.
/// </summary>
public static class AiToneApplier
{
    /// <summary>
    ///     The action that writes <paramref name="accepted" />, or null when nothing is left to write.
    ///     A tone whose definition changed since the proposal was held (edited, renamed, deleted, or
    ///     added when there was none) is stale and left out. Every proposed tone is validated against
    ///     the catalogue AGAIN first, and one that fails writes nothing at all.
    /// </summary>
    /// <param name="project">The live project.</param>
    /// <param name="accepted">The accepted tones.</param>
    /// <param name="applied">How many definitions the action writes.</param>
    /// <param name="stale">How many were left out as stale.</param>
    /// <exception cref="RsToneCatalogException">A proposed tone is not something Rocksmith can load.</exception>
    public static IChartAction? Build(ChartProject project, IReadOnlyList<AiToneReviewItem> accepted, out int applied,
        out int stale)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(accepted);
        foreach (var item in accepted)
            RsToneCatalogValidation.Require(item.Proposed, RsGearCatalog.Shared, $"the proposed tone '{item.Key}'");

        var actions = new List<IChartAction>(accepted.Count);
        stale = 0;
        foreach (var item in accepted)
        {
            var live = Find(project, item.Key);
            if (item.CurrentJson is null ? live is not null : live is null || !string.Equals(live.Json, item.CurrentJson, StringComparison.Ordinal))
            {
                stale++;
                continue;
            }

            var definition = new ToneDefinition
            {
                Key = live?.Key ?? item.Key,
                Name = live?.Name is { Length: > 0 } name ? name : item.Name,
                Json = item.ProposedJson
            };
            actions.Add(new UpsertToneDefinitionAction(project, live?.Key, definition));
        }

        applied = actions.Count;
        return actions.Count == 0 ? null : new CompositeAction(actions);
    }

    /// <summary>The project's definition under <paramref name="key" />: trim + case-insensitive, the
    /// tone-key rule everywhere else.</summary>
    public static ToneDefinition? Find(ChartProject project, string key) =>
        project.ToneDefinitions.FirstOrDefault(d =>
            string.Equals(d.Key.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A tone proposal in words, shared by the review card and the chat reply.</summary>
public static class AiToneText
{
    /// <summary>The gear chain in signal order with every knob, by the game's own display names where the
    /// catalogue has them: "Chorus (Rate 4, Depth 60) → TW22 (Gain 35, Bass 50) → 1x12 Combo". Knob values print as
    /// the model gave them (two decimals at most), never clamped into a different value.</summary>
    /// <param name="tone">The tone.</param>
    public static string Chain(RsTone tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        return string.Join(" → ", tone.InSignalOrder().Select(Gear));
    }

    /// <summary>The chain by gear name only, no knobs — the dense review row's detail.</summary>
    public static string Summary(RsTone tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        return string.Join(" → ", tone.InSignalOrder().Select(Name));
    }

    private static string Name(RsGear gear)
    {
        var name = RsGearCatalog.Shared.TryGetGear(gear.GearKey, out var data) && data is { Name.Length: > 0 }
            ? data.Name
            : gear.GearKey;
        return gear.Type == RsGearType.Cabinet && MicOf(gear.GearKey) is { } mic ? name + " · " + mic : name;
    }

    /// <summary>One gear item: its catalogue name (its key when the catalogue has none) and its knobs, each by
    /// the catalogue's knob name (its pot when there is none).</summary>
    public static string Gear(RsGear gear)
    {
        ArgumentNullException.ThrowIfNull(gear);
        RsGearCatalog.Shared.TryGetGear(gear.GearKey, out var data);
        var knobs = gear.KnobValues
            .Select(kv => KnobName(data, kv.Key) + " " +
                          kv.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        var name = Name(gear);
        return knobs.Count == 0 ? name : $"{name} ({string.Join(", ", knobs)})";
    }

    /// <summary>A cabinet's mic rig — the last two tokens of <c>Cab_&lt;model&gt;_&lt;mic&gt;_&lt;position&gt;</c>
    /// ("57 Cone") — which its display name, shared by every mic of one model, does not say.</summary>
    private static string? MicOf(string key)
    {
        var split = key.LastIndexOf('_');
        if (split <= 0) return null;
        var second = key.LastIndexOf('_', split - 1);
        return second <= 0 || key.IndexOf('_', StringComparison.Ordinal) == second
            ? null
            : key[(second + 1)..].Replace('_', ' ');
    }

    private static string KnobName(RsGearData? gear, string knobKey)
    {
        if (gear?.Knobs?.FirstOrDefault(k => k.Key == knobKey) is { Name.Length: > 0 } knob) return knob.Name;
        var cut = knobKey.LastIndexOf('_');
        return cut >= 0 && cut < knobKey.Length - 1 ? knobKey[(cut + 1)..] : knobKey;
    }

    /// <summary>Where the proposal's evidence came from, for the row: the stem it measured with the
    /// confidence, or that it was mapped from the charter's words alone (no cached stems).</summary>
    public static string Evidence(AiToneReviewItem item, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(loc);
        var provenance = item.Provenance;
        if (provenance is null || provenance.Stem == RsToneGenerator.NoStem) return loc["AiTones_FromWordsOnly"];
        // The stem's NAME, never its id: "guitar" is a file name, not German.
        var stem = ToneStemNames.Display(provenance.Stem, loc);
        return provenance.ConfidencePercent is { } percent
            ? loc.Format("AiTones_FromStemRated", stem, percent)
            : loc.Format("AiTones_FromStem", stem);
    }
}
